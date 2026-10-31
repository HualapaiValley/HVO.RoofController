using Avalonia;
using Avalonia.VisualTree;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.Screens;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.Extensions.DependencyInjection;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The kiosk's safety override (#215) and its safety indicator (#207), against the controller in process with its
/// interlock in Warn mode and no source (Unknown, which counts as unsafe): Open asks first and sends nothing; "Open
/// anyway" sends it with the override; Cancel, Stop and the lock withdraw the question. On the roof page the question takes
/// the motion buttons' place, Stop stays, and the indicator gives the first reason and what Open does.
/// </summary>
[TestClass, DoNotParallelize]
public sealed class KioskSafetyOverrideTests
{
    private static readonly Dictionary<string, string?> Warn = new() { ["RoofSafety:InterlockMode"] = "Warn" };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Open_WhileTheConditionsAreUnknown_AsksFirst_AndOpenAnywaySendsItWithTheOverride()
    {
        await using var harness = await KioskHarness.CreateAsync(settings: Warn);
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);

        await harness.Console.OpenAsync();

        var asked = harness.Console.View.SafetyQuestion;
        asked.Should().NotBeNull();
        asked!.Lines[0].Should().Be("The safety conditions are UNKNOWN: No safety sources configured.");
        harness.Calls("Open").Should().Be(0, "nothing is sent before the person confirms");

        await harness.Console.OpenWithSafetyOverrideAsync();

        harness.Calls("Open").Should().Be(1, "the controller lets Open through only with the override");
        harness.Console.View.SafetyQuestion.Should().BeNull();
        await harness.WaitForAsync(view => view.HoldsLease, "the kiosk to hold the motion's lease");
    }

    [TestMethod]
    public async Task Cancel_TheLock_AndStop_EachWithdrawTheQuestion_AndSendNothing()
    {
        await using var harness = await KioskHarness.CreateAsync(settings: Warn);
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);

        await harness.Console.OpenAsync();
        harness.Console.CancelSafetyOverride();
        harness.Console.View.SafetyQuestion.Should().BeNull();
        harness.Console.View.Notices.First().Text.Should().Be(RoofSafetyText.OverrideCancelled);

        await harness.Console.OpenAsync();
        await harness.Console.StopAsync();
        harness.Console.View.SafetyQuestion.Should().BeNull("Stop wins");
        await harness.Console.OpenWithSafetyOverrideAsync();

        await harness.WaitForAsync(view => view.OpenBlock is null, "Open to be offered again");
        await harness.Console.OpenAsync();
        await harness.Console.LockAsync();
        harness.Console.View.SafetyQuestion.Should().BeNull("a question asked of one person is never answered by the next");

        harness.Calls("Open").Should().Be(0);
    }

    [TestMethod]
    public async Task ARefusalThatArrivesAfterAStop_AsksNothing()
    {
        // Review F3: the refusal belongs to an Open that Stop withdrew.
        var mount = new SimulatedTelescopeParkSource("Main");
        await using var harness = await KioskHarness.CreateAsync(settings: Warn, services: services => services.AddSingleton<ITelescopeParkSource>(mount).AddSingleton<IWeatherSafetySource>(new SimulatedWeatherSafetySource("Cloud sensor")));
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await harness.WaitForAsync(view => view.Status?.Safety?.State == RoofSafetyState.Safe, "the parked mount");
        await UnparkUnseenAsync(harness, mount);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.DelayAnswer = async (request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/Open", StringComparison.Ordinal))
            {
                held.TrySetResult();
                await release.Task;
            }
        };

        var open = harness.Console.OpenAsync();
        await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Console.StopAsync();
        release.SetResult();
        await open;

        harness.Console.View.SafetyQuestion.Should().BeNull("Stop withdrew the Open this refusal answers");
        harness.Console.View.Notices.First().Level.Should().Be(KioskNoticeLevel.Danger);
        harness.Calls("Open").Should().Be(0);
    }

    [TestMethod]
    public async Task ARefusalThatReportsAFault_SaysSo_AndAsksNothing()
    {
        // Review F4: the refusal's status says Open is blocked for another reason.
        var mount = new SimulatedTelescopeParkSource("Main");
        await using var harness = await KioskHarness.CreateAsync(settings: Warn, services: services => services.AddSingleton<ITelescopeParkSource>(mount).AddSingleton<IWeatherSafetySource>(new SimulatedWeatherSafetySource("Cloud sensor")));
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await harness.WaitForAsync(view => view.Status?.Safety?.State == RoofSafetyState.Safe, "the parked mount");
        await UnparkUnseenAsync(harness, mount);
        harness.Report(KioskHarness.Snapshot(faultLatched: true));

        await harness.Console.OpenAsync();

        var view = harness.Console.View;
        view.SafetyQuestion.Should().BeNull("Open is blocked by the fault, whatever the conditions");
        view.OpenBlock.Should().Be(RoofCommandRules.FaultLatched);
        view.Notices.First().Level.Should().Be(KioskNoticeLevel.Danger);
        harness.Calls("Open").Should().Be(0);
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task TheRoofPage_AsksInPlaceOfTheMotionButtons_WithStopStillThere_AndOpenAnywayOpens(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync(settings: Warn);
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Roof));

        await OnUiAsync(() =>
        {
            screen.Visible("safety-reasons").Should().BeTrue();
            screen.Text("safety-reasons").Should().Be("No safety sources configured");
            screen.Text("safety-open").Should().Be("Open asks for a safety override (logged)");
        });
        await screen.TouchAsync(screen.Shell.RoofPage.Open);
        await UntilAsync(() => harness.Console.View.SafetyQuestion is not null, "the question");
        await OnUiAsync(() => screen.Shell.Update(harness.Console.View));

        await screen.RenderAsync("safety-override", TestContext);

        await OnUiAsync(() =>
        {
            var roof = screen.Shell.RoofPage;
            screen.Visible("safety-override").Should().BeTrue();
            screen.Text("safety-override-text").Should().StartWith("The safety conditions are UNKNOWN: No safety sources configured.");
            roof.ConfirmOverride.IsEffectivelyVisible.Should().BeTrue();
            roof.ConfirmOverride.IsEffectivelyEnabled.Should().BeTrue();
            roof.CancelOverride.IsEffectivelyVisible.Should().BeTrue();
            roof.Open.IsEffectivelyVisible.Should().BeFalse("the question takes the motion buttons' place");
            screen.Visible("stop").Should().BeTrue("Stop is on every page, whatever is asked");
        });
        harness.Calls("Open").Should().Be(0);

        await screen.TouchAsync(screen.Shell.RoofPage.ConfirmOverride);

        await UntilAsync(() => harness.Calls("Open") == 1, "Open with the override");
        await harness.WaitForAsync(view => view.SafetyQuestion is null, "the question to close");
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task ManyReasons_Scroll_AndTheAnswersStayOnTheCard(int width, int height, double pixelsPerMillimetre)
    {
        await using var harness = await KioskHarness.CreateAsync(settings: Warn);
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, pixelsPerMillimetre);
        var reasons = Enumerable.Range(1, 8).Select(i => $"Telescope 'Mount {i}' cannot be read (no answer within 5 s)").ToArray();

        await OnUiAsync(() =>
        {
            screen.Shell.ShowPage(KioskPage.Roof);
            screen.Shell.Update(harness.Console.View with
            {
                SafetyQuestion = new RoofSafetyOverrideQuestion(new RoofSafetyStatus { State = RoofSafetyState.Unknown, Reasons = reasons, InterlockMode = RoofSafetyInterlockMode.Warn })
            });
        });
        await screen.CheckAsync("safety-override-many");

        await OnUiAsync(() =>
        {
            var card = screen.Shell.RoofPage.ConfirmOverride.GetVisualAncestors().OfType<Avalonia.Controls.Border>().First(border => border.Name == "safety-override");
            foreach (var answer in new Avalonia.Controls.Control[] { screen.Shell.RoofPage.ConfirmOverride, screen.Shell.RoofPage.CancelOverride })
            {
                var bottom = answer.TranslatePoint(new Avalonia.Point(0, answer.Bounds.Height), screen.Shell)!.Value.Y;
                var pageBottom = screen.Shell.RoofPage.TranslatePoint(new Avalonia.Point(0, screen.Shell.RoofPage.Bounds.Height), screen.Shell)!.Value.Y;
                bottom.Should().BeLessThanOrEqualTo(pageBottom + 0.5, $"{answer.Name} is on the roof page");
            }

            card.IsEffectivelyVisible.Should().BeTrue();
        });
    }

    [TestMethod]
    public async Task Escape_OnTheRoofPage_AnswersTheQuestionWithCancel()
    {
        await using var harness = await KioskHarness.CreateAsync(settings: Warn, options: new KioskConsoleOptions { Wording = KioskWording.Desktop, Blanking = false });
        await harness.StartLiveAsync();
        await harness.PasswordAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, 1280, 800, KioskMetrics.DeskPixelsPerMillimetre);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Roof));
        await harness.Console.OpenAsync();
        await UntilAsync(() => harness.Console.View.SafetyQuestion is not null, "the question");
        // The keyboard on "Open anyway" itself: Esc still sends nothing.
        await OnUiAsync(() =>
        {
            screen.Shell.Update(harness.Console.View);
            screen.Shell.RoofPage.ConfirmOverride.Focus().Should().BeTrue();
        });

        await screen.PressAsync(Avalonia.Input.PhysicalKey.Escape);

        await UntilAsync(() => harness.Console.View.SafetyQuestion is null, "Esc to cancel the question");
        harness.Console.View.Notices.First().Text.Should().Be(RoofSafetyText.OverrideCancelled);
        harness.Calls("Open").Should().Be(0);
    }

    [TestMethod]
    public async Task TheIndicator_SaysNothingMore_WhileTheConditionsAreSafe()
    {
        await using var harness = await KioskHarness.CreateAsync(populateUsers: false);
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, 1280, 720, 8.2);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Roof));

        await OnUiAsync(() =>
        {
            screen.Shell.Update(harness.Console.View with
            {
                Status = harness.Console.View.Status! with
                {
                    Safety = new RoofSafetyStatus { State = RoofSafetyState.Unsafe, Reasons = ["Telescope 'Main' is not parked", "Weather monitor 'Cloud' reports unsafe weather"], InterlockMode = RoofSafetyInterlockMode.Block }
                }
            });
            screen.Text("safety-reasons").Should().Be("Telescope 'Main' is not parked (+1 more)");
            screen.Text("safety-open").Should().Be("Open is blocked by the safety interlock");

            screen.Shell.Update(harness.Console.View with { Status = harness.Console.View.Status! with { Safety = new RoofSafetyStatus { State = RoofSafetyState.Safe, InterlockMode = RoofSafetyInterlockMode.Warn } } });
            screen.Text("safety").Should().Be("Safety: Safe");
            screen.Visible("safety-reasons").Should().BeFalse();
            screen.Visible("safety-open").Should().BeFalse();
        });
    }

    // The mount is unparked after the kiosk's last status: the controller reads it, and the kiosk does not know. The read is
    // made directly, not by moving the controller's clock, which would send the hub's heartbeat with the new conditions.
    private static async Task UnparkUnseenAsync(KioskHarness harness, SimulatedTelescopeParkSource mount)
    {
        mount.IsParked = false;
        await harness.Host.Services.GetRequiredService<HVO.RoofControllerV4.RPi.Safety.RoofSafetyService>().PollAsync(CancellationToken.None);
        using var viewer = HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(HVO.RoofControllerV4.RPi.Tests.Controllers.TestApiKeys.Viewer));
        (await viewer.Roof.GetSafetyAsync()).State.Should().Be(RoofSafetyState.Unsafe);
        harness.Console.View.Status!.Safety!.State.Should().Be(RoofSafetyState.Safe, "the kiosk has not heard of it");
    }
}
