using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Settings;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Components;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Supervision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The System page: the controller's information and readiness, a restart through the controller (asked first, and
/// confirmed when it would load a safety-critical hand edit), and a forced restart through the container's supervisor
/// for a controller that does not answer (typed to confirm, rechecked against the session, logged).
/// </summary>
[TestClass]
public sealed class SystemPageTests
{
    [TestMethod]
    public async Task TheControllersInformation_IsShown()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);

        var cut = Loaded(context);

        cut.FindAll("[data-testid=system-fact]").Select(fact => fact.GetAttribute("data-label")).ToList()
            .Should().Equal(SystemPage.WebUiLabel, "Application", "Host", "System", "Runtime", "Started", "Memory", "CPU");
        Fact(cut, SystemPage.WebUiLabel).Should().Be(RoofProductVersion.Describe(typeof(RoofWebOptions).Assembly));
        Fact(cut, "Application").Should().StartWith($"HVO.RoofControllerV4.RPi {RoofProductVersion.Describe(typeof(Program).Assembly)}")
            .And.EndWith("(Development)");
        Fact(cut, "Memory").Should().MatchRegex(@"^\d+\.\d (KiB|MiB) working set, \d+\.\d (KiB|MiB) managed$");
        cut.Find("[data-testid=system-information]").TextContent.Should().Contain("Read at 2026-09-29 12:00:00Z.");
        cut.Find("[data-testid=forced-restart-last]").TextContent.Should().Be("The web UI is not running under the container's supervisor.");
    }

    [TestMethod]
    public async Task Readiness_IsCheckedOnRequest()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        using var admin = harness.Admin();
        var readiness = await admin.Health.GetReadinessAsync();

        cut.Click("[data-testid=system-readiness]");

        cut.WaitForAssertion(() => Message(cut).Should().Be($"{(readiness.IsHealthy ? "Ready" : "Not ready")}: {readiness.Status}."));
    }

    [TestMethod]
    public async Task ARestart_IsAskedFirst_ThenSent()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        var signal = harness.Host.Services.GetRequiredService<RoofRestartSignal>();

        cut.Click("[data-testid=restart-start]");
        cut.WaitForAssertion(() => cut.Find("[data-testid=restart-confirm]").TextContent.Should().Contain(RoofSystemText.RestartQuestion));
        cut.FindAll("[data-testid=restart-hand-edit]").Should().BeEmpty();
        cut.Click("[data-testid=panel-cancel]");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=restart-confirm]").Should().BeEmpty());
        signal.Requested.Should().BeFalse("a cancelled restart sends nothing");

        cut.Click("[data-testid=restart-start]");
        cut.WaitForAssertion(() => cut.Find("[data-testid=restart-send]").TextContent.Trim().Should().Be("Restart"));
        cut.Click("[data-testid=restart-send]");

        cut.WaitForAssertion(() => cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Info)));
        Lines(cut).Last().Should().Be(RoofSystemText.RestartComingBack);
        cut.FindAll("[data-testid=system-fact]").Should().BeEmpty("the information is out of date once the controller restarts");
        signal.Requested.Should().BeTrue();
        harness.Roof.Mock.Verify(service => service.Stop(RoofControllerStopReason.HostShutdown), Times.Once());
    }

    [TestMethod]
    public async Task ARestart_ThatLoadsASafetyCriticalHandEdit_IsConfirmed()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        harness.EditSettingsFile("{ \"RoofControllerOptionsV4\": { \"OpenRelayId\": 2, \"CloseRelayId\": 1 } }");

        // Taken before the restart: the restart stops the host, which may be disposed by the time the test looks.
        var signal = harness.Host.Services.GetRequiredService<RoofRestartSignal>();
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        cut.Click("[data-testid=restart-start]");

        cut.WaitForAssertion(() => cut.Find("[data-testid=restart-hand-edit]").TextContent.Should().Be(RoofSystemText.RestartLoadsHandEdit));
        var changes = cut.FindAll("[data-testid=restart-confirm] li").Select(change => change.TextContent).ToList();
        changes.Should().Contain(change => change.StartsWith("RoofControllerOptionsV4:OpenRelayId: ", StringComparison.Ordinal)
            && change.EndsWith("  [SAFETY-CRITICAL]", StringComparison.Ordinal));
        cut.Find("[data-testid=restart-send]").TextContent.Trim().Should().Be("Confirm and restart");
        cut.Click("[data-testid=restart-send]");

        cut.WaitForAssertion(() => cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Info)));
        signal.Requested.Should().BeTrue("the confirmation was sent with the restart");
    }

    [TestMethod]
    public async Task AForcedRestart_IsTypedToConfirm_WrittenForTheSupervisor_AndLogged()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        File.WriteAllText(harness.SupervisorStatePath, WebTestSupport.SupervisorState("running"));
        await using var context = harness.Context(session, supervised: true);
        var cut = Loaded(context);
        var request = Path.Combine(harness.ControlPath, ControllerForcedRestart.RequestFileName);
        cut.Find("[data-testid=forced-restart-last]").TextContent.Should().Be(
            "The last forced restart, at 2026-09-29 05:45:58Z, was ignored: the controller had started less than 10 s before.");

        cut.Click("[data-testid=forced-restart-start]");
        cut.WaitForElement("[data-testid=forced-restart-warning]").TextContent.Should().Be(SystemPage.KillWarning);
        cut.Find("[data-testid=forced-restart-send]").HasAttribute("disabled").Should().BeTrue("the word is not typed yet");
        cut.Input("[data-testid=forced-restart-word]", "restar");
        cut.WaitForAssertion(() => cut.Find("[data-testid=forced-restart-send]").HasAttribute("disabled").Should().BeTrue());
        cut.Input("[data-testid=forced-restart-word]", " Restart ");
        cut.WaitForAssertion(() => cut.Find("[data-testid=forced-restart-send]").HasAttribute("disabled").Should().BeFalse());
        File.Exists(request).Should().BeFalse();

        cut.Click("[data-testid=forced-restart-send]");

        var result = cut.WaitForElement("[data-testid=forced-restart-result]");
        File.Exists(request).Should().BeTrue();
        result.GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Warning));
        result.TextContent.Should().Contain("The supervisor was asked to kill and restart the controller.");
        cut.FindAll("[data-testid=forced-restart-confirm]").Should().BeEmpty();
        harness.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning
            && entry.Message == "Forced restart of the controller asked for by ada: Requested");

        File.WriteAllText(harness.SupervisorStatePath, WebTestSupport.SupervisorState("running")
            .Replace("\"outcome\":\"ignored\"", "\"outcome\":\"restarted\"", StringComparison.Ordinal)
            .Replace("\"at\":\"2026-09-29T05:45:58Z\"", "\"at\":\"2026-09-29T12:00:01Z\"", StringComparison.Ordinal));
        harness.Clock.Advance(TimeSpan.FromSeconds(2));

        cut.WaitForAssertion(() => cut.Find("[data-testid=forced-restart-last]").TextContent.Should().Be(
            "The last forced restart: restarted, at 2026-09-29 12:00:01Z."));
    }

    [TestMethod]
    public async Task AForcedRestart_WithoutTheSupervisor_IsSaidToBeImpossible()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        cut.Click("[data-testid=forced-restart-start]");
        cut.WaitForElement("[data-testid=forced-restart-word]");
        cut.Input("[data-testid=forced-restart-word]", SystemPage.ConfirmWord);
        cut.Click("[data-testid=forced-restart-send]");

        var result = cut.WaitForElement("[data-testid=forced-restart-result]");
        result.GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Danger));
        result.TextContent.Should().Be("The web UI is not running under the container's supervisor, so it cannot restart the controller.");
    }

    [TestMethod]
    public async Task AForcedRestart_FromAnEndedSession_IsNotAskedFor()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session, supervised: true);
        var cut = Loaded(context);
        session.End();

        cut.Click("[data-testid=forced-restart-start]");
        cut.WaitForElement("[data-testid=forced-restart-word]");
        cut.Input("[data-testid=forced-restart-word]", SystemPage.ConfirmWord);
        cut.Click("[data-testid=forced-restart-send]");

        cut.WaitForAssertion(() => cut.Find("[data-testid=forced-restart-result]").TextContent.Should().Be(WebClientPage.SignedOut));
        File.Exists(Path.Combine(harness.ControlPath, ControllerForcedRestart.RequestFileName)).Should().BeFalse();
    }

    [TestMethod]
    public async Task SignedOut_ThePageSaysSo()
    {
        using var harness = new WebAdminHarness();
        await using var context = harness.Context(session: null);

        var cut = context.Render<SystemPage>();

        cut.WaitForAssertion(() => Message(cut).Should().Be(WebClientPage.SignedOut));
        cut.Find("[data-testid=system-information] p[role=status]").TextContent.Should().Be("The controller's information is not read.");
    }

    private static IRenderedComponent<SystemPage> Loaded(BunitContext context)
    {
        var cut = context.Render<SystemPage>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=system-fact]").Should().NotBeEmpty(
            cut.FindAll("[data-testid=page-message]").FirstOrDefault()?.TextContent ?? "the information to be read"));
        return cut;
    }

    private static string Fact(IRenderedComponent<SystemPage> cut, string label)
        => cut.Find($"[data-testid=system-fact][data-label='{label}']").TextContent;

    private static List<string> Lines(IRenderedComponent<SystemPage> cut)
        => [.. cut.Find("[data-testid=page-message]").QuerySelectorAll("p").Select(line => line.TextContent)];

    private static string Message(IRenderedComponent<SystemPage> cut) => string.Join(' ', Lines(cut));
}
