using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Simulation.Safety;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Roof;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The web UI's safety override (#215) against the controller in process with its interlock in Warn mode: Open while the
/// conditions are not Safe asks first and sends nothing; "Open anyway" sends Open with the override; Cancel, Stop and
/// Close withdraw the question; a refusal for want of the override asks too. And the roof page's safety panel (#207).
/// </summary>
[TestClass]
public sealed class WebSafetyOverrideTests
{
    private static readonly Dictionary<string, string?> Warn = new() { ["RoofSafety:InterlockMode"] = "Warn" };

    [TestMethod]
    public async Task Open_WhileTheConditionsAreUnknown_AsksFirst_AndSendsNothing()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: Warn);
        await harness.StartLiveAsync();

        await harness.Console.OpenAsync();

        var view = harness.Console.View;
        view.SafetyQuestion.Should().NotBeNull();
        view.SafetyQuestion!.Title.Should().Be("Safety override");
        view.SafetyQuestion.Lines.Should().Equal(
            "The safety conditions are UNKNOWN: No safety sources configured.",
            RoofSafetyText.UnknownCountsAsUnsafe,
            RoofSafetyText.OverrideConsequence);
        view.Busy.Should().BeNull("nothing was sent");
        harness.Calls("Open").Should().Be(0);
    }

    [TestMethod]
    public async Task OpenAnyway_SendsOpenWithTheOverride_AndTheRoofOpens()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: Warn);
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.Console.OpenAsync();

        await harness.Console.OpenWithSafetyOverrideAsync();

        harness.Calls("Open").Should().Be(1, "the controller lets Open through only with the override");
        var view = harness.Console.View;
        view.SafetyQuestion.Should().BeNull();
        view.Notices.First().Text.Should().StartWith("Open accepted.");
        await harness.Console.OpenWithSafetyOverrideAsync();
        harness.Calls("Open").Should().Be(1, "a second press of an answered question sends nothing");
    }

    [TestMethod]
    public async Task Cancel_SendsNothing_AndSaysSo()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: Warn);
        await harness.StartLiveAsync();
        await harness.Console.OpenAsync();

        harness.Console.CancelSafetyOverride();

        var view = harness.Console.View;
        view.SafetyQuestion.Should().BeNull();
        view.Notices.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Text = RoofSafetyText.OverrideCancelled, Level = WebRoofNoticeLevel.Info });
        harness.Calls("Open").Should().Be(0);
    }

    [TestMethod]
    public async Task StopAndClose_WithdrawTheQuestion()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: Warn);
        await harness.StartLiveAsync();
        await harness.Console.OpenAsync();

        await harness.Session!.StopAsync(CancellationToken.None);
        await harness.WaitForAsync(view => view.SafetyQuestion is null, "Stop to withdraw the question");
        await harness.Console.OpenWithSafetyOverrideAsync();
        harness.Calls("Open").Should().Be(0, "Stop wins: the question it withdrew sends nothing");

        // The roof stopped part way: both directions are offered.
        harness.Push(RoofServiceMock.Snapshot(RoofControllerStatus.Stopped));
        await harness.WaitForAsync(view => view.Status?.Status == RoofControllerStatus.Stopped && view.OpenBlock is null && view.CloseBlock is null, "the stopped roof");
        await harness.Console.OpenAsync();
        harness.Console.View.SafetyQuestion.Should().NotBeNull();
        await harness.Console.CloseAsync();
        harness.Console.View.SafetyQuestion.Should().BeNull("Close is never gated, and withdraws a question about Open");
        harness.Calls("Close").Should().Be(1);
    }

    [TestMethod]
    public async Task OpenWhileTheConditionsAreSafe_AsksNothing()
    {
        var mount = new SimulatedTelescopeParkSource("Main");
        using var harness = await WebRoofHarness.CreateAsync(settings: Warn, configureServices: services => services.AddSingleton<ITelescopeParkSource>(mount).AddSingleton<IWeatherSafetySource>(new SimulatedWeatherSafetySource("Cloud sensor")));
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.Status?.Safety?.State == RoofSafetyState.Safe, "the parked mount");

        await harness.Console.OpenAsync();

        harness.Console.View.SafetyQuestion.Should().BeNull();
        harness.Calls("Open").Should().Be(1);
    }

    [TestMethod]
    public async Task ARefusalForWantOfTheOverride_AsksThen_WithTheConditionsTheControllerSent()
    {
        var mount = new SimulatedTelescopeParkSource("Main");
        using var harness = await WebRoofHarness.CreateAsync(settings: Warn, configureServices: services => services.AddSingleton<ITelescopeParkSource>(mount).AddSingleton<IWeatherSafetySource>(new SimulatedWeatherSafetySource("Cloud sensor")));
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.Status?.Safety?.State == RoofSafetyState.Safe, "the parked mount");

        await UnparkUnseenAsync(harness, mount);

        await harness.Console.OpenAsync();

        var view = harness.Console.View;
        harness.Calls("Open").Should().Be(0);
        view.SafetyQuestion.Should().NotBeNull();
        view.SafetyQuestion!.Lines[0].Should().Be("The safety conditions are UNSAFE: Telescope 'Main' is not parked.");
        view.Notices.First().Level.Should().Be(WebRoofNoticeLevel.Warning);
        view.Notices.First().Text.Should().StartWith("The conditions are not confirmed safe. Open needs a safety override.");

        await harness.Console.OpenWithSafetyOverrideAsync();
        harness.Calls("Open").Should().Be(1);
    }

    [TestMethod]
    public async Task OverridesNarrowedToAdmins_AnOperatorIsToldWhy_AndIsNotAsked()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: new Dictionary<string, string?>(Warn) { ["RoofSafety:OverrideRole"] = "Admin" });
        await harness.StartLiveAsync();

        await harness.Console.OpenAsync();

        harness.Console.View.OpenBlock.Should().Be(RoofCommandRules.SafetyOverrideNeedsAdmin);
        harness.Console.View.SafetyQuestion.Should().BeNull();
        harness.Console.View.Notices.First().Text.Should().Be($"Not sent: {RoofCommandRules.SafetyOverrideNeedsAdmin}.");
    }

    [TestMethod]
    public async Task AConfirmationThatCrossesAStop_SendsNothing_EvenBeforeThePageHearsOfTheStop()
    {
        // Review F2: the question, and the command it reserves, belong to the Stops counted when it was asked.
        using var harness = await WebRoofHarness.CreateAsync(settings: Warn);
        harness.OpensTheRoof();
        Task? confirmed = null;
        // Subscribed before the page: it runs on each Stop before the page withdraws the question.
        harness.Session!.StopSent += (_, _) => confirmed ??= harness.Console.OpenWithSafetyOverrideAsync();
        await harness.StartLiveAsync();
        await harness.Console.OpenAsync();
        harness.Console.View.SafetyQuestion.Should().NotBeNull();

        await harness.Session.StopAsync(CancellationToken.None);
        await confirmed!;

        harness.Calls("Open").Should().Be(0, "Stop wins over a confirmation it crossed");
        harness.Console.View.SafetyQuestion.Should().BeNull();
    }

    [TestMethod]
    public async Task ARefusalThatArrivesAfterAStop_AsksNothing()
    {
        // Review F3: the refusal belongs to an Open that Stop withdrew.
        var mount = new SimulatedTelescopeParkSource("Main");
        using var harness = await WebRoofHarness.CreateAsync(settings: Warn, configureServices: services => services.AddSingleton<ITelescopeParkSource>(mount).AddSingleton<IWeatherSafetySource>(new SimulatedWeatherSafetySource("Cloud sensor")));
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.Status?.Safety?.State == RoofSafetyState.Safe, "the parked mount");
        await UnparkUnseenAsync(harness, mount);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.HoldAnswer = async request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/Open", StringComparison.Ordinal))
            {
                held.TrySetResult();
                await release.Task;
            }
        };

        var open = harness.Console.OpenAsync();
        await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Session!.StopAsync(CancellationToken.None);
        release.SetResult();
        await open;

        harness.Console.View.SafetyQuestion.Should().BeNull("Stop withdrew the Open this refusal answers");
        harness.Console.View.Notices.First().Level.Should().Be(WebRoofNoticeLevel.Danger);
        harness.Calls("Open").Should().Be(0);
    }

    [TestMethod]
    public async Task ARefusalThatReportsAFault_SaysSo_AndAsksNothing()
    {
        // Review F4: the refusal's status says Open is blocked for another reason.
        var mount = new SimulatedTelescopeParkSource("Main");
        using var harness = await WebRoofHarness.CreateAsync(settings: Warn, configureServices: services => services.AddSingleton<ITelescopeParkSource>(mount).AddSingleton<IWeatherSafetySource>(new SimulatedWeatherSafetySource("Cloud sensor")));
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.Status?.Safety?.State == RoofSafetyState.Safe, "the parked mount");
        await UnparkUnseenAsync(harness, mount);
        harness.Report(RoofServiceMock.Snapshot(faultLatched: true));

        await harness.Console.OpenAsync();

        var view = harness.Console.View;
        view.SafetyQuestion.Should().BeNull("Open is blocked by the fault, whatever the conditions");
        view.Status!.IsFaultLatched.Should().BeTrue();
        view.OpenBlock.Should().Be(RoofCommandRules.FaultLatched);
        view.Notices.First().Level.Should().Be(WebRoofNoticeLevel.Danger);
        harness.Calls("Open").Should().Be(0);
    }

    // ---- The roof page ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheRoofPage_AsksTheQuestion_AndOpenAnywayOpensTheRoof_WhileStopStaysOnThePage()
    {
        using var harness = await WebRoofHarness.CreateAsync(settings: Warn);
        harness.OpensTheRoof();
        await using var context = await RenderContextAsync(harness);
        var cut = context.Render<Dashboard>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=open]").HasAttribute("disabled").Should().BeFalse());

        await cut.InvokeAsync(() => cut.Find("[data-testid=open]").Click());

        await cut.WaitForAssertionAsync(() =>
        {
            var question = cut.Find("[data-testid=safety-override]");
            question.GetAttribute("role").Should().Be("alertdialog");
            Text(question).Should().Contain("Safety override").And.Contain("The safety conditions are UNKNOWN: No safety sources configured.")
                .And.Contain(RoofSafetyText.OverrideConsequence);
            Text(cut.Find("[data-testid=safety-override-confirm]")).Should().Be("Open anyway");
            Text(cut.Find("[data-testid=safety-override-cancel]")).Should().Be("Cancel");
        });
        harness.Calls("Open").Should().Be(0);

        await cut.InvokeAsync(() => cut.Find("[data-testid=safety-override-confirm]").Click());

        await cut.WaitForAssertionAsync(() =>
        {
            cut.FindAll("[data-testid=safety-override]").Should().BeEmpty();
            harness.Calls("Open").Should().Be(1);
        });
    }

    [TestMethod]
    public async Task TheRoofPage_ShowsTheConditions_TheirReasons_EachSource_AndWhatOpenDoes()
    {
        var mount = new SimulatedTelescopeParkSource("Main", isParked: false);
        var weather = new SimulatedWeatherSafetySource("Cloud sensor") { Detail = "clear" };
        using var harness = await WebRoofHarness.CreateAsync(settings: Warn, configureServices: services =>
        {
            services.AddSingleton<ITelescopeParkSource>(mount);
            services.AddSingleton<IWeatherSafetySource>(weather);
        });
        await using var context = await RenderContextAsync(harness);

        var cut = context.Render<Dashboard>();

        await cut.WaitForAssertionAsync(() =>
        {
            var panel = cut.Find("[data-testid=safety-panel]");
            panel.GetAttribute("data-state").Should().Be("Unsafe");
            panel.ClassList.Should().Contain("rc2-safety--unsafe");
            Text(cut.Find("[data-testid=safety-state]")).Should().Be("Safety: Unsafe");
            Text(cut.Find("[data-testid=safety-open]")).Should().Be("Open asks for a safety override (logged)");
            cut.FindAll("[data-testid=safety-reasons] li").Select(Text).Should().Equal("Telescope 'Main' is not parked");
            cut.FindAll("[data-testid=safety-sources] li").Select(Text).Should().Equal(
                "Unsafe: Telescope 'Main' is not parked",
                "Safe: Weather monitor 'Cloud sensor' reports safe weather (clear)");
            cut.FindAll("[data-testid=status-rows] .rc2-row").Select(row => Text(row.QuerySelector("dt")!)).Should().Contain(RoofStatusText.SafetySourceLabel);
        });
    }

    // The mount is unparked after the page's last status: the controller reads it, and the page does not know. The read is
    // made directly, not by moving the controller's clock, which would send the hub's heartbeat with the new conditions.
    private static async Task UnparkUnseenAsync(WebRoofHarness harness, SimulatedTelescopeParkSource mount)
    {
        mount.IsParked = false;
        await harness.Host.Services.GetRequiredService<RoofSafetyService>().PollAsync(CancellationToken.None);
        using var viewer = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Viewer));
        (await viewer.Roof.GetSafetyAsync()).State.Should().Be(RoofSafetyState.Unsafe);
        harness.Console.View.Status!.Safety!.State.Should().Be(RoofSafetyState.Safe, "the page has not heard of it");
    }

    private static async Task<BunitContext> RenderContextAsync(WebRoofHarness harness)
    {
        var context = new BunitContext();
        context.Services.AddSingleton(harness.Console);
        context.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        context.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        context.Services.AddSingleton(Options.Create(new RoofWebOptions()));
        WebCameraStreamTests.SetUpThePlayer(context, out _, out _);
        await harness.StartLiveAsync();
        return context;
    }

    private static string Text(IElement element) => Regex.Replace(element.TextContent, @"\s+", " ").Trim();
}
