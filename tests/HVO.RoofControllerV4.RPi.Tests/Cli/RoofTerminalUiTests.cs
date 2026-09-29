using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Cli.Ui;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Settings;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using TestSecrets = HVO.RoofControllerV4.RPi.Tests.Security.TestSecrets;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.Core.Results;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// <c>hvo-roof ui</c> (#45) on a virtual terminal against the real API with a mocked roof: Stop (F9 and the button) on
/// every page and over a prompt, Esc never closing the interface, the stale banner, the operator lease while the roof
/// moves, and the Setup, People, Settings and System pages. The tests drive the interface's own loop on the test
/// thread, so they never await.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoofTerminalUiTests
{
    private const string WrongKey = "test-wrong-key-not-a-real-secret-99";

    // ---- Stop --------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void F9_SendsStop_FromEveryPage()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);

        foreach (var (key, page) in new[] { (Key.F1, "Roof"), (Key.F2, "Settings"), (Key.F3, "People"), (Key.F4, "System"), (Key.F5, "Setup") })
        {
            tui.Press(key);
            tui.WaitIdle($"the {page} page");
            tui.Ui.CurrentPage.Title.Should().Be(page);
            tui.Screen.Should().Contain($"{RoofStopText.ButtonLabel} (F9)", "Stop is on every page");

            tui.Press(Key.F9);
            tui.WaitIdle($"Stop from the {page} page", () => tui.Ui.StopResult != RoofStopText.Sending);
            tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        }

        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Exactly(5));
    }

    [TestMethod]
    public void TheStopButton_SendsStop_AndSaysSoAtOnce()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);
        using var tui = Started(rig);

        tui.Ui.StopButton.InvokeCommand(Command.Accept);

        tui.Ui.StopResult.Should().Be(RoofStopText.Sending, "the interface says Stop is on its way before the answer");
        tui.WaitIdle("the Stop answer", () => tui.Ui.StopResult != RoofStopText.Sending);
        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified, "a viewer may stop the roof");
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public void F9_SendsStop_WhileAPromptIsOpen_AndLeavesThePromptOpen()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);
        tui.Press(Key.F5);
        Click(tui, "Connection");
        tui.Ui.Panel.Should().NotBeNull();

        tui.Ui.StopButton.Enabled.Should().BeTrue("a prompt never disables Stop");
        tui.Press(Key.F9);
        tui.WaitIdle("Stop over the prompt", () => tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        tui.Ui.Panel.Should().NotBeNull("Stop does not close what the person was typing");
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public void Stop_WithNoController_SaysItFailed_AndStaysEnabled()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);
        using var tui = new TuiDriver(rig);

        tui.Ui.CurrentPage.Should().BeOfType<RoofUiSetupPage>("with no controller, the interface opens on Setup");
        tui.Screen.Should().Contain("No controller is configured: use Setup (F5).");
        tui.Press(Key.F9);

        tui.Ui.StopResult.Should().Be(RoofStopText.Failed("No controller address is configured."));
        tui.Ui.StopButton.Enabled.Should().BeTrue();
        ShouldHaveColours(tui.ColoursOf("No controller is configured"), RoofUiPalette.WarningText, RoofUiPalette.WarningBackground);
        ShouldHaveColours(tui.ColoursOf(tui.Ui.StopResult[..30]), RoofUiPalette.DangerText, RoofUiPalette.Background);
    }

    [TestMethod]
    public void Stop_ThatTheControllerRefuses_SaysSo_InTheSharedWording()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(WrongKey);
        using var tui = new TuiDriver(rig);
        tui.WaitIdle("the refusal", () => tui.Screen.Contains("No status: the controller refused the credential. Sign in on Setup (F5)."));

        tui.Press(Key.F9);
        tui.WaitIdle("the Stop answer", () => tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.StopResult.Should().Be(RoofStopText.KeyRefused);
        ShouldHaveColours(tui.ColoursOf(RoofStopText.KeyRefused[..30]), RoofUiPalette.DangerText, RoofUiPalette.Background, "a refused Stop is an error");
        ShouldHaveColours(tui.ColoursOf("No status: the controller refused"), RoofUiPalette.DangerText, RoofUiPalette.DangerBackground);
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    // ---- Colours -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheScreen_IsDrawnInTheWebUisColours()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        ShouldHaveColours(tui.ColoursOf($"{RoofStopText.ButtonLabel} (F9)"), RoofUiPalette.StopButtonText, RoofUiPalette.StopButton, "Stop is the web UI's yellow button");
        tui.ColoursOf($"{RoofStopText.ButtonLabel} (F9)").Style.Should().HaveFlag(TextStyle.Bold);
        ShouldHaveColours(tui.ColoursOf(" Open "), RoofUiPalette.OpenButtonText, RoofUiPalette.OpenButton, "Open is green, as on the web UI");
        page.CloseButton.GetScheme().Should().BeSameAs(RoofUiTheme.HvoDark.Close, "Close is red, as on the web UI (disabled while the roof is closed)");
        ShouldHaveColours(tui.ColoursOf(RoofStopText.AlwaysAvailable), RoofUiPalette.Text, RoofUiPalette.Background);
        ShouldHaveColours(tui.ColoursOf("Status at"), RoofUiPalette.Text, RoofUiPalette.Background, "the page is HVO Dark's");
        ShouldHaveColours(tui.ColoursOf("· status live"), RoofUiPalette.Text, RoofUiPalette.Surface, "the header is the web UI's navigation bar");
        ShouldHaveColours(tui.ColoursOf("F10"), RoofUiPalette.Accent, RoofUiPalette.Badge, "a key is in the accent colour");
        ShouldHaveColours(tui.ColoursOf("Quit"), RoofUiPalette.Muted, RoofUiPalette.Badge);

        // The address is readable: a window's title has its frame's colour.
        tui.Ui.Window.Title.Should().Be(RoofTerminalUi.WindowTitle(tui.Ui.Connection!.Controller));
        ShouldHaveColours(tui.ColoursOf($"HVO roof: {tui.Ui.Connection.Controller} "), RoofUiPalette.Muted, RoofUiPalette.Background);

        tui.Press(Key.F9);
        tui.WaitIdle("the Stop answer", () => tui.Ui.StopResult != RoofStopText.Sending);
        ShouldHaveColours(tui.ColoursOf(RoofStopText.AcknowledgedVerified), RoofUiPalette.SuccessText, RoofUiPalette.Background);
    }

    [TestMethod]
    public void OnAnEightyColumnTerminal_TheStopResultTheBannerAndTheMessage_AreShownInFull()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(WrongKey);
        using var tui = new TuiDriver(rig, width: 80, height: 24);

        // The status hub's refusal is not a request the driver counts: the banner says when it has arrived.
        tui.WaitIdle("the refusal", () => tui.Ui.PendingOperations == 0
            && Unwrapped(tui.Screen).Contains("No status: the controller refused the credential.", StringComparison.Ordinal));

        tui.Press(Key.F9);
        tui.WaitIdle("the Stop answer", () => tui.Ui.StopResult != RoofStopText.Sending);
        tui.Ui.Say("SAFETY: The roof stopped short of the open limit. Check the drive before you open it again.", error: true);

        var screen = Unwrapped(tui.Screen);
        screen.Should().Contain(RoofStopText.KeyRefused, "a Stop result is never cut short");
        screen.Should().Contain("No status: the controller refused the credential. Sign in on Setup (F5).");
        screen.Should().Contain("SAFETY: The roof stopped short of the open limit. Check the drive before you open it again.");
        tui.Screen.TrimEnd('\n').Split('\n')[^2].Should().Contain(RoofTerminalUi.KeyBar, "the key bar is still on the last row");
    }

    [TestMethod]
    public void TheWindowTitle_EndsTheAddressWithASpace()
    {
        // Terminal.Gui links an address up to the next space: the frame line after the title must not be part of it.
        RoofTerminalUi.WindowTitle(new Uri("https://roof.example.org:5001/")).Should().Be(" HVO roof: https://roof.example.org:5001/ ");
        RoofTerminalUi.WindowTitle(null).Should().Be(" HVO roof ");
    }

    [TestMethod]
    [DataRow(RoofStopOutcome.Sent, RoofUiPalette.InfoText)]
    [DataRow(RoofStopOutcome.Acknowledged, RoofUiPalette.SuccessText)]
    [DataRow(RoofStopOutcome.RelayUnverified, RoofUiPalette.WarningText)]
    [DataRow(RoofStopOutcome.Failed, RoofUiPalette.DangerText)]
    public void EachStopResult_HasTheWebUisColourForIt(RoofStopOutcome outcome, string colour)
        => ShouldHaveColours(RoofUiTheme.HvoDark.ForStop(outcome).Normal, colour, RoofUiPalette.Background);

    [TestMethod]
    public void WithNoColorSet_NothingIsDrawnInColour_AndStopAndTheFocusStillShow()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        rig.Environment["NO_COLOR"] = "1";
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        tui.Ui.Theme.Should().BeSameAs(RoofUiTheme.NoColour);
        tui.DrawnAttributes().Should().OnlyContain(drawn => drawn.Foreground == Color.None && drawn.Background == Color.None,
            "NO_COLOR asks for the terminal's own colours: Terminal.Gui alone would draw 16");
        tui.Ui.StopButton.ShadowStyle.Should().Be(ShadowStyles.None, "Terminal.Gui draws a shadow in black");
        page.OpenButton.ShadowStyle.Should().Be(ShadowStyles.None);

        tui.ColoursOf($"{RoofStopText.ButtonLabel} (F9)").Style.Should().HaveFlag(TextStyle.Reverse, "Stop stands out without its yellow");
        tui.ColoursOf(" Open ").Style.Should().HaveFlag(TextStyle.Bold).And.NotHaveFlag(TextStyle.Reverse);
        tui.ColoursOf(" Close ").Style.Should().HaveFlag(TextStyle.Faint, "Close is disabled while the roof is closed");

        var focused = tui.Ui.Window.MostFocused;
        focused.Should().NotBeNull();
        tui.ColoursAt(focused!).Style.Should().HaveFlag(TextStyle.Reverse, "the focus shows without the accent blue");
        tui.Press(Key.Tab);
        tui.Ui.Window.MostFocused.Should().NotBeSameAs(focused);
        tui.ColoursAt(focused!).Style.Should().NotHaveFlag(TextStyle.Reverse);
        tui.ColoursAt(tui.Ui.Window.MostFocused!).Style.Should().HaveFlag(TextStyle.Reverse);
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("1", true)]
    [DataRow("0", true)]
    [DataRow("false", true)]
    public void NoColor_TurnsTheColoursOff_WhenItIsSetAndNotEmpty(string? value, bool noColour)
        => RoofUiTheme.For(name => name == "NO_COLOR" ? value : null).Should().BeSameAs(noColour ? RoofUiTheme.NoColour : RoofUiTheme.HvoDark,
            "no-color.org: set to any value but the empty string");

    [TestMethod]
    public void TheNoColourTheme_HasNoColourInAnyScheme_AndStillMarksStopTheFocusAndTheWarnings()
    {
        var theme = RoofUiTheme.NoColour;
        Scheme[] schemes =
        [
            theme.Base, theme.Frame, theme.WindowFrame, theme.Header, theme.Key, theme.KeyName, theme.Stop, theme.Open, theme.Close,
            theme.Warning, theme.Danger, theme.Panel, theme.PanelFrame, theme.PanelError,
            .. Enum.GetValues<RoofStopOutcome>().Select(theme.ForStop)
        ];
        VisualRole[] roles = [VisualRole.Normal, VisualRole.HotNormal, VisualRole.Focus, VisualRole.HotFocus, VisualRole.Active,
            VisualRole.HotActive, VisualRole.Highlight, VisualRole.Editable, VisualRole.ReadOnly, VisualRole.Disabled];
        foreach (var scheme in schemes)
        {
            foreach (var role in roles)
            {
                var drawn = scheme.GetAttributeForRole(role);
                drawn.Foreground.Should().Be(Color.None, $"{role} has no colour");
                drawn.Background.Should().Be(Color.None, $"{role} has no colour");
            }
        }

        theme.Stop.Normal.Style.Should().HaveFlag(TextStyle.Reverse);
        theme.Stop.Focus.Style.Should().NotBe(theme.Stop.Normal.Style, "the focus shows on Stop too");
        theme.Base.Focus.Style.Should().HaveFlag(TextStyle.Reverse);
        theme.Base.Editable.Style.Should().HaveFlag(TextStyle.Underline, "an input field shows where it is");
        theme.Warning.Normal.Style.Should().HaveFlag(TextStyle.Reverse);
        theme.Danger.Normal.Style.Should().HaveFlag(TextStyle.Reverse);
        theme.ForStop(RoofStopOutcome.Failed).Normal.Style.Should().HaveFlag(TextStyle.Reverse);
    }

    // ---- Keys --------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void Esc_ClosesAPrompt_ButNeverTheInterface()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);

        tui.Press(Key.Esc);
        tui.Ui.QuitRequested.Should().BeFalse("Esc must not close the interface by accident");

        tui.Press(Key.F5);
        Click(tui, "Connection");
        tui.Ui.Panel.Should().NotBeNull();
        tui.Press(Key.Esc);

        tui.Ui.Panel.Should().BeNull();
        tui.Ui.QuitRequested.Should().BeFalse();
        tui.Screen.Should().Contain(RoofTerminalUi.KeyBar);
    }

    [TestMethod]
    public void F10_Quits_WithoutStoppingARoofItDoesNotMove()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);

        tui.Press(Key.F10);

        tui.Ui.QuitRequested.Should().BeTrue();
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    public void F10_WhileAStopIsOnItsWay_WaitsForItsAnswer_ThenCloses()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { WrapHandler = inner => new GatedHandler(inner, "/RoofControl/Stop", answer.Task) };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = new TuiDriver(rig);
        tui.WaitIdle("the caller", () => tui.Ui.Caller is not null);

        tui.Press(Key.F9);
        tui.Press(Key.F10);

        tui.Ui.QuitRequested.Should().BeTrue();
        tui.Ui.Message.Should().Be("Waiting for Stop to be answered before closing.");
        tui.Ui.StopInFlight.Should().BeTrue();
        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeFalse("closing now would cancel the Stop");

        answer.SetResult();
        tui.WaitIdle("the Stop's answer", () => !tui.Ui.StopInFlight);

        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeTrue("the Stop was answered");
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once());
    }

    [TestMethod]
    public void F10_WhileAStopIsOnItsWay_OffersNoOpenOrClose_AndRefusesOne_SoClosingWaitsOnNoNewMotion()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { WrapHandler = inner => new GatedHandler(inner, "/RoofControl/Stop", answer.Task) };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.OpenButton.Enabled.Should().BeTrue("the roof is stopped and the status is fresh");

        tui.Press(Key.F9);
        tui.Press(Key.F10);

        page.OpenButton.Enabled.Should().BeFalse("quitting may already have sent its Stop, and would wait on the Open");
        page.CloseButton.Enabled.Should().BeFalse();
        page.BlockReason(RoofMotionDirection.Opening).Should().Be("the interface is closing");
        tui.Screen.Should().Contain("Open and Close: the interface is closing.");
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.Ui.Run("Sending Open…", (client, cancellationToken) => client.Roof.OpenAsync(cancellationToken), motion: true)
            .Should().BeFalse("a motion is refused once quitting has begun, whoever asks for it");
        tui.Ui.Message.Should().Be(RoofTerminalUi.MotionWhileQuittingText);
        tui.Ui.MotionInFlight.Should().BeFalse();

        answer.SetResult();
        tui.WaitIdle("the Stop's answer", () => !tui.Ui.StopInFlight);

        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeTrue("nothing but the Stop was on its way");
        host.RoofService.Verify(service => service.Open(), Times.Never());
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once());
    }

    [TestMethod]
    public void F10_WhileAStopIsOnItsWay_ThatNothingConfirms_StaysOpenAndSaysSo_UntilF10AgainClosesIt()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host)
        {
            WrapHandler = inner => new GatedHandler(
                new StubAnswerHandler(inner, "/RoofControl/Stop", HttpStatusCode.ServiceUnavailable, "text/html", "<html>503</html>"),
                "/RoofControl/Stop",
                answer.Task)
        };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = new TuiDriver(rig);
        tui.WaitIdle("the caller", () => tui.Ui.Caller is not null);

        tui.Press(Key.F9);
        tui.Press(Key.F10);
        answer.SetResult();
        tui.WaitIdle("the Stop's answer", () => !tui.Ui.StopInFlight);

        tui.Ui.UnconfirmedStop.Should().NotBeNull();
        tui.Ui.UnconfirmedStop!.Outcome.Should().Be(RoofStopOutcome.Failed);
        tui.Ui.StopResult.Should().Be(RoofStopText.Failed("The controller is not ready. Try again shortly."));
        tui.Ui.Message.Should().Be("Nothing confirmed the Stop, so the interface stays open. F10 closes it.");
        tui.Ui.QuitRequested.Should().BeFalse();
        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeFalse("closing would hide a Stop that nothing confirmed");
        ((RoofUiRoofPage)tui.Ui.CurrentPage).BlockReason(RoofMotionDirection.Opening)
            .Should().NotBe("the interface is closing", "the interface stays open, so the roof's own state decides again");

        tui.Press(Key.F10);

        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeTrue("F10 again closes it, with the result known");
    }

    [TestMethod]
    public void Closing_WithAStopOnItsWay_DeliversTheStopFirst()
    {
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { WrapHandler = inner => new GatedHandler(inner, "/RoofControl/Stop", answer.Task) };
        rig.UseApiKey(TestApiKeys.Operator);
        var tui = new TuiDriver(rig);
        try
        {
            tui.WaitIdle("the caller", () => tui.Ui.Caller is not null);
            tui.Press(Key.F9);
            tui.Ui.StopInFlight.Should().BeTrue();

            // The Stop gets through while the interface closes.
            _ = Task.Delay(TimeSpan.FromMilliseconds(200)).ContinueWith(_ => answer.TrySetResult(), TaskScheduler.Default);
        }
        finally
        {
            tui.Dispose();
        }

        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once());
    }

    [TestMethod]
    public void AnOlderStopsLateAcknowledgement_DoesNotHideANewerStopThatFailed_AndF10SendsStopAgain()
    {
        var (roof, moving) = MovingRoof();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost(roof);
        var stops = 0;
        using var rig = new CliRig(host)
        {
            // The first Stop reaches the controller but its answer is held; the second gets a proxy's 503 at once; later
            // ones get through.
            WrapHandler = inner => new HeldAnswerHandler(
                new StubAnswerHandler(inner, "/RoofControl/Stop", HttpStatusCode.ServiceUnavailable, "text/html", "<html>503</html>",
                    () => Volatile.Read(ref stops) == 2),
                "/RoofControl/Stop",
                reached,
                answer.Task,
                holding: () => Interlocked.Increment(ref stops) == 1)
        };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        tui.Press(Key.F9);
        tui.WaitFor("the first Stop to reach the controller", () => reached.Task.IsCompleted);
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitFor("Open", () => tui.Ui.Message.StartsWith("Open accepted", StringComparison.Ordinal));
        tui.Press(Key.F9);
        tui.WaitFor("the second Stop's failure", () => tui.Ui.UnconfirmedStop is not null);

        answer.SetResult();
        tui.WaitIdle("the first Stop's late answer", () => !tui.Ui.StopInFlight);

        moving().Should().BeTrue("the newest Stop never reached the controller");
        tui.Ui.StopResult.Should().Be(RoofStopText.Failed("The controller is not ready. Try again shortly."),
            "an older Stop's acknowledgement is not news about the newest");
        tui.Ui.UnconfirmedStop!.Outcome.Should().Be(RoofStopOutcome.Failed);

        tui.Press(Key.F10);
        tui.WaitIdle("Stop before quitting", () => ((IRunnable)tui.Ui.Window).StopRequested);

        tui.Ui.Message.Should().Be("Stopping the roof, which moves on a command from this interface, before closing.");
        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        moving().Should().BeFalse();
        roof.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Exactly(2), "the first Stop and the one sent on quitting");
    }

    [TestMethod]
    public void AnOlderStopsLateFailure_AfterANewerStopWasAcknowledged_DoesNotKeepTheInterfaceOpen()
    {
        var (roof, _) = MovingRoof();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost(roof);
        var stops = 0;
        using var rig = new CliRig(host)
        {
            // The first Stop is held on its way and then gets a proxy's 503; the second is acknowledged at once.
            WrapHandler = inner => new HeldAnswerHandler(
                inner,
                "/RoofControl/Stop",
                reached,
                answer.Task,
                holding: () => Interlocked.Increment(ref stops) == 1,
                forwardFirst: false,
                heldAnswer: request => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    Content = new StringContent("<html>503</html>", System.Text.Encoding.UTF8, "text/html")
                })
        };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        tui.Press(Key.F9);
        tui.WaitFor("the first Stop to be held", () => reached.Task.IsCompleted);
        tui.Press(Key.F9);
        tui.WaitFor("the second Stop's acknowledgement", () => tui.Ui.StopResult == RoofStopText.AcknowledgedVerified);

        tui.Press(Key.F10);
        tui.Ui.Message.Should().Be("Waiting for Stop to be answered before closing.");
        answer.SetResult();
        tui.WaitIdle("the first Stop's late failure", () => !tui.Ui.StopInFlight);

        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified, "an older Stop's failure is not news about the newest");
        tui.Ui.UnconfirmedStop.Should().BeNull();
        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeTrue("the newest Stop sent from here was acknowledged");
        roof.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once());
    }

    [TestMethod]
    public void Ui_OnATerminationSignal_ClosesTheInterface_AndExitsInterrupted()
    {
        var exits = new ConcurrentQueue<int>();
        using var termination = new RoofCliTermination(exits.Enqueue);
        using var host = RoofClientApiTests.CreateHost();
        var held = false;
        var closed = false;
        using var rig = new CliRig(host)
        {
            Interactive = true,
            Termination = termination,
            CreateApplication = () =>
            {
                var app = Application.Create(new VirtualTimeProvider());
                app.Init(DriverRegistry.Names.ANSI);
                app.Driver!.SetScreenSize(120, 36);
                return app;
            },
            RunApplication = (app, window) =>
            {
                var session = app.Begin(window);

                // Closing the terminal: two SIGHUPs well under a millisecond apart. The interface holds the process
                // from the start, so the second does not end it before the interface closes.
                held = termination.IsHeld;
                termination.OnSignal(new PosixSignalContext(PosixSignal.SIGHUP));
                termination.OnSignal(new PosixSignalContext(PosixSignal.SIGHUP));
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (!window.StopRequested && DateTime.UtcNow < deadline)
                {
                    app.TimedEvents!.RunTimers();
                    Thread.Sleep(10);
                }

                closed = window.StopRequested;
                app.End(session!);
            }
        };
        rig.UseApiKey(TestApiKeys.Operator);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = RoofCli.RunAsync(["ui", "--credentials-file", rig.CredentialsPath], rig.CreateHost(output, error), termination.Token)
            .GetAwaiter().GetResult();

        code.Should().Be((int)RoofExitCode.Interrupted, error.ToString());
        closed.Should().BeTrue("a termination signal closes the interface as F10 does");
        held.Should().BeTrue("the interface holds the process while it runs");
        exits.Should().BeEmpty("the second SIGHUP must not end the process while the interface closes");
        termination.IsHeld.Should().BeFalse();
    }

    [TestMethod]
    public void Ui_OnATerminationSignal_WhileItsOpenIsOnItsWay_StopsTheRoofAfterTheAnswerWait_SaysSo_AndExitsInterrupted()
    {
        var (roof, moving) = MovingRoof();
        var exits = new ConcurrentQueue<int>();
        using var termination = new RoofCliTermination(exits.Enqueue);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost(roof);
        var closed = false;
        var clock = new VirtualTimeProvider();
        using var rig = new CliRig(host)
        {
            Interactive = true,
            Termination = termination,
            WrapHandler = inner => new HeldAnswerHandler(inner, "/RoofControl/Open", reached, answer.Task),
            CreateApplication = () =>
            {
                var app = Application.Create(clock);
                app.Init(DriverRegistry.Names.ANSI);
                app.Driver!.SetScreenSize(120, 36);
                return app;
            },
            RunApplication = (app, window) =>
            {
                var session = app.Begin(window);
                TuiDriver.PumpUntil(app, "Open to be offered", () => Find((View)window, "Open") is { Enabled: true });
                Find((View)window, "Open")!.InvokeCommand(Command.Accept);
                TuiDriver.PumpUntil(app, "the Open to reach the controller", () => reached.Task.IsCompleted);

                // Closing the terminal while the Open's answer is on its way, and the answer never arrives.
                termination.OnSignal(new PosixSignalContext(PosixSignal.SIGHUP));
                termination.OnSignal(new PosixSignalContext(PosixSignal.SIGHUP));
                TuiDriver.PumpUntil(app, "quitting to wait for the Open's answer", () =>
                {
                    app.LayoutAndDraw(true);
                    return app.Driver!.ToString()!.Contains("Waiting up to 3 s", StringComparison.Ordinal);
                });
                clock.Advance(TimeSpan.FromSeconds(3));
                TuiDriver.PumpUntil(app, "the interface to close", () => window.StopRequested);
                closed = true;
                app.End(session!);
            }
        };
        rig.UseApiKey(TestApiKeys.Operator);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = RoofCli.RunAsync(["ui", "--credentials-file", rig.CredentialsPath], rig.CreateHost(output, error), termination.Token)
            .GetAwaiter().GetResult();

        closed.Should().BeTrue();
        code.Should().Be((int)RoofExitCode.Interrupted, error.ToString());
        exits.Should().BeEmpty("the second SIGHUP must not end the process before the Stop");
        moving().Should().BeFalse("the Open reached the controller, so closing sends Stop");
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
        error.ToString().Should().Be(
            "The Open or Close sent from the interface was not answered, so it may still reach the controller after the Stop. "
            + "Check the roof, and run 'hvo-roof stop' if it moves." + Environment.NewLine);
    }

    [TestMethod]
    public void Ui_ClosedAfterAStopThatNothingConfirmed_SaysSoOnTheTerminal_AndExitsStopNotVerified()
    {
        using var host = RoofClientApiTests.CreateHost();
        var closed = false;
        using var rig = new CliRig(host)
        {
            Interactive = true,
            WrapHandler = inner => new StubAnswerHandler(inner, "/RoofControl/Stop", HttpStatusCode.ServiceUnavailable, "text/html", "<html>503</html>"),
            CreateApplication = () =>
            {
                var app = Application.Create(new VirtualTimeProvider());
                app.Init(DriverRegistry.Names.ANSI);
                app.Driver!.SetScreenSize(120, 36);
                return app;
            },
            RunApplication = (app, window) =>
            {
                var session = app.Begin(window);
                bool Showing(string text)
                {
                    app.LayoutAndDraw(true);
                    return app.Driver!.ToString()!.Contains(text, StringComparison.Ordinal);
                }

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                app.InjectKey(Key.F9);
                while (!Showing("Stop failed") && DateTime.UtcNow < deadline)
                {
                    app.TimedEvents!.RunTimers();
                    Thread.Sleep(10);
                }

                app.InjectKey(Key.F10);
                app.TimedEvents!.RunTimers();
                closed = window.StopRequested;
                app.End(session!);
            }
        };
        rig.UseApiKey(TestApiKeys.Operator);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var code = RoofCli.RunAsync(["ui", "--credentials-file", rig.CredentialsPath], rig.CreateHost(output, error), CancellationToken.None)
            .GetAwaiter().GetResult();

        closed.Should().BeTrue("F10 closes the interface once the Stop is answered, confirmed or not");
        code.Should().Be((int)RoofExitCode.StopNotVerified, error.ToString());
        error.ToString().Should().Be(
            "The last Stop sent from the interface was not confirmed. "
            + RoofStopText.Failed("The controller is not ready. Try again shortly.") + Environment.NewLine);
    }

    // ---- Staleness ---------------------------------------------------------------------------------------------------

    [TestMethod]
    public void AStaleStatus_IsShownAsTheLastKnownState_AndMotionIsNotOffered_ButStopIs()
    {
        // The controller's clock is frozen, so it sends no heartbeat; the client's clock moves only when told.
        var serverClock = new ManualTimeProvider();
        var clientClock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        using var host = RoofClientApiTests.CreateHost(configureServices: services => services.AddSingleton<TimeProvider>(serverClock));
        using var rig = new CliRig(host) { Time = clientClock };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.OpenButton.Enabled.Should().BeTrue("the status is current and the roof is closed");

        clientClock.Advance(ClientTestSupport.FastFeed.StaleAfter);
        tui.WaitIdle("the stale banner", () => tui.Ui.IsStale);

        var screen = tui.Screen;
        screen.Should().Contain("STALE: no status since 2026-03-01");
        ShouldHaveColours(tui.ColoursOf("STALE: no status since"), RoofUiPalette.WarningText, RoofUiPalette.WarningBackground, "a stale status is a warning");
        screen.Should().Contain("Showing the last known state; Stop still works.");
        screen.Should().Contain("status STALE");
        page.Describe().Should().StartWith("LAST KNOWN STATE, as of ");
        page.OpenButton.Enabled.Should().BeFalse();
        page.BlockReason(RoofMotionDirection.Opening).Should().Be("the status is stale, so the roof cannot be watched");
        tui.Ui.StopButton.Enabled.Should().BeTrue();

        tui.Press(Key.F9);
        tui.WaitIdle("Stop while stale", () => tui.Ui.StopResult != RoofStopText.Sending);
        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);

        host.RoofService.Raise(service => service.StatusChanged += null, new RoofStatusChangedEventArgs(RoofServiceMock.Snapshot() with { StatusVersion = 12 }));
        tui.WaitIdle("a current status again", () => !tui.Ui.IsStale && tui.Ui.Status?.StatusVersion == 12);
        tui.Screen.Should().NotContain("STALE");
        page.Describe().Should().StartWith("Status at ");
        page.OpenButton.Enabled.Should().BeTrue();
    }

    [TestMethod]
    public void WithoutLiveStatus_ARefreshedStatus_IsStale_AndMotionIsNotOffered_ButStopIs()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { WrapHandler = inner => new UnreachableHandler(inner, null, RoofStatusHubContract.Path) };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = new TuiDriver(rig);
        tui.WaitIdle("the caller", () => tui.Ui.Caller is not null);
        tui.Ui.Status.Should().BeNull("the status hub cannot be reached");
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        Click(tui, "Refresh");
        tui.WaitIdle("the status read", () => tui.Ui.Status is not null);

        tui.Ui.IsStale.Should().BeTrue("a single read says nothing about the roof since");
        tui.Ui.StaleSince.Should().NotBeNull();
        Unwrapped(tui.Screen).Should().Contain("STALE: status from a single read at ")
            .And.Contain("; live status is not connected. Stop still works.");
        ShouldHaveColours(tui.ColoursOf("STALE: status from a single read"), RoofUiPalette.WarningText, RoofUiPalette.WarningBackground);
        page.Describe().Should().StartWith("LAST KNOWN STATE, as of ");
        page.OpenButton.Enabled.Should().BeFalse();
        page.BlockReason(RoofMotionDirection.Opening).Should().Be("the status is stale, so the roof cannot be watched");
        tui.Ui.StopButton.Enabled.Should().BeTrue();
    }

    [TestMethod]
    public void BeforeTheFirstStatus_NothingIsClaimed()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(WrongKey);
        using var tui = new TuiDriver(rig);
        tui.WaitIdle("the refusal", () => tui.Screen.Contains("No status: the controller refused the credential."));

        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        tui.Ui.Status.Should().BeNull();
        page.Describe().Should().StartWith("No status from the controller yet.");
        page.Describe().Should().NotContain("Closed", "the interface was never told the roof's state");
        page.OpenButton.Enabled.Should().BeFalse();
        tui.Screen.Should().Contain("status refused");
    }

    // ---- The roof page -----------------------------------------------------------------------------------------------

    [TestMethod]
    public void AViewer_IsNotOfferedMotion()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.OpenButton.Enabled.Should().BeFalse();
        page.CloseButton.Enabled.Should().BeFalse();
        page.ClearFaultButton.Enabled.Should().BeFalse();
        page.Describe().Should().EndWith("Open and Close: the Operator role is needed to open or close the roof.");
        tui.Screen.Should().Contain("test-viewer (viewer, API key)");
    }

    [TestMethod]
    public void Open_HoldsTheLease_RenewsIt_AndF10StopsTheRoofBeforeClosing()
    {
        var moving = false;
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => Volatile.Read(ref moving)
            ? RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = 30 }
            : RoofServiceMock.Snapshot());
        roof.Setup(service => service.Open()).Returns(() =>
        {
            Volatile.Write(ref moving, true);
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        roof.Setup(service => service.RenewLease()).Returns(() => Result<RoofStatusResponse>.Success(
            RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = 30 }));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("Open", () => tui.Ui.HoldsLease);

        tui.Ui.Message.Should().Be("Open accepted. This interface renews the operator lease while the roof moves; F9 or quitting stops it.");
        page.BlockReason(RoofMotionDirection.Closing).Should().Be("the roof is moving: stop it first");
        roof.Verify(service => service.Open(), Times.Once());

        // A third of the 30 s lease: renewed after 10 s, not before.
        tui.Tick(9);
        tui.WaitIdle("nothing");
        roof.Verify(service => service.RenewLease(), Times.Never());
        tui.Tick();
        tui.WaitIdle("the renewal");
        roof.Verify(service => service.RenewLease(), Times.Once());
        tui.Tick(10);
        tui.WaitIdle("the next renewal");
        roof.Verify(service => service.RenewLease(), Times.Exactly(2));

        tui.Press(Key.F10);
        tui.WaitIdle("Stop before quitting", () => tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.QuitRequested.Should().BeTrue();
        tui.Ui.Message.Should().Be("Stopping the roof, which moves on a command from this interface, before closing.");
        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
        tui.Ui.HoldsLease.Should().BeFalse();
    }

    [TestMethod]
    public void F9_EndsTheLease_SoItIsNotRenewedAfterStop()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() =>
            RoofServiceMock.Snapshot(RoofControllerStatus.Closing, RoofMotionDirection.Closing) with { LeaseSecondsRemaining = 6 });
        roof.SetupGet(service => service.Status).Returns(RoofControllerStatus.Open);
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.BlockReason(RoofMotionDirection.Closing).Should().Be("the roof is moving: stop it first", "someone else's motion is not held here");
        tui.Ui.HoldsLease.Should().BeFalse();

        tui.Ui.HoldLease(tui.Ui.Status!);
        tui.Ui.HoldsLease.Should().BeTrue();
        tui.Press(Key.F9);
        tui.WaitIdle("Stop", () => tui.Ui.StopResult != RoofStopText.Sending);
        tui.Tick(5);
        tui.WaitIdle("no renewal");

        tui.Ui.HoldsLease.Should().BeFalse();
        roof.Verify(service => service.RenewLease(), Times.Never());
    }

    [TestMethod]
    public void Open_WithNoLease_SaysF9OrQuittingStopsIt_AndF10StopsTheRoofBeforeClosing()
    {
        var moving = false;
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => Volatile.Read(ref moving)
            ? RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = null }
            : RoofServiceMock.Snapshot());
        roof.Setup(service => service.Open()).Returns(() =>
        {
            Volatile.Write(ref moving, true);
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("Open", () => tui.Ui.Message.StartsWith("Open accepted", StringComparison.Ordinal));

        tui.Ui.Message.Should().Be("Open accepted. F9 or quitting stops it.");
        tui.Ui.HoldsLease.Should().BeFalse("the controller holds this motion on no lease");
        tui.Ui.FollowsMotion.Should().BeTrue();

        tui.Press(Key.F10);
        tui.WaitIdle("Stop before quitting", () => !tui.Ui.StopInFlight && tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.Message.Should().Be("Stopping the roof, which moves on a command from this interface, before closing.");
        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeTrue("the interface closes once the Stop is answered");
    }

    [TestMethod]
    public void Open_WithNoLease_F10_WhenTheStopIsRefusedAsUnverified_StaysOpenAndSaysSo_UntilF10AgainClosesIt()
    {
        var moving = false;
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => Volatile.Read(ref moving)
            ? RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening, relayState: RoofRelayRegisterState.Unverified)
                with { LeaseSecondsRemaining = null }
            : RoofServiceMock.Snapshot());
        roof.Setup(service => service.Open()).Returns(() =>
        {
            Volatile.Write(ref moving, true);
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "The relays could not be read back (test).")));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("Open", () => tui.Ui.Message.StartsWith("Open accepted", StringComparison.Ordinal));

        tui.Press(Key.F10);
        tui.WaitIdle("Stop before quitting", () => !tui.Ui.StopInFlight && tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.StopResult.Should().Be(RoofStopText.SentUnverified);
        tui.Ui.UnconfirmedStop!.Outcome.Should().Be(RoofStopOutcome.RelayUnverified);
        tui.Ui.Message.Should().Be("Nothing confirmed the Stop, so the interface stays open. F10 closes it.");
        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeFalse("the roof may still be moving, and the operator must see that");
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());

        tui.Press(Key.F10);

        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeTrue("F10 again closes it, as the message says");
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once(), "the second F10 closes without another Stop");
    }

    [TestMethod]
    public void Open_WithNoLease_AfterF10StayedOpenOnAnUnconfirmedStop_ATerminationSignal_SendsStopAgain_ThenCloses()
    {
        var moving = false;
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => Volatile.Read(ref moving)
            ? RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening, relayState: RoofRelayRegisterState.Unverified)
                with { LeaseSecondsRemaining = null }
            : RoofServiceMock.Snapshot());
        roof.Setup(service => service.Open()).Returns(() =>
        {
            Volatile.Write(ref moving, true);
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "The relays could not be read back (test).")));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("Open", () => tui.Ui.Message.StartsWith("Open accepted", StringComparison.Ordinal));
        tui.Press(Key.F10);
        tui.WaitIdle("Stop before quitting", () => !tui.Ui.StopInFlight && tui.Ui.StopResult != RoofStopText.Sending);
        tui.Ui.Message.Should().Be("Nothing confirmed the Stop, so the interface stays open. F10 closes it.");

        // The terminal closes while the roof may still be moving: the interface closes anyway, and sends Stop first.
        tui.Ui.Quit(interrupted: true);
        tui.WaitIdle("Stop before closing", () => ((IRunnable)tui.Ui.Window).StopRequested);

        tui.Ui.Status!.IsMoving.Should().BeTrue();
        tui.Ui.UnconfirmedStop!.Outcome.Should().Be(RoofStopOutcome.RelayUnverified);
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Exactly(2), "a signal after the interface stayed open still stops a roof it moved");
    }

    [TestMethod]
    public void F10_WhileItsOpenIsOnItsWay_WaitsForItsAnswer_ThenStopsTheRoof_AndCloses()
    {
        var (roof, moving) = MovingRoof();
        var onItsWay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host) { WrapHandler = inner => new InFlightRequestHandler(inner, "/RoofControl/Open", onItsWay, gate.Task) };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        // The Open is on its way, and has not reached the controller yet.
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitFor("the Open to be on its way", () => onItsWay.Task.IsCompleted);

        tui.Press(Key.F10);
        tui.Tick(2);
        Settle(tui);

        tui.Ui.Message.Should().Be("Waiting up to 3 s for the answer to the Open or Close on its way, then stopping the roof before closing.");
        tui.Ui.MotionInFlight.Should().BeTrue("quitting waits for the Open's answer");
        roof.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never(), "a Stop sent now could reach the controller ahead of the Open");

        // The Open reaches the controller within the wait, and is answered.
        gate.SetResult();
        tui.WaitIdle("Stop before quitting", () => ((IRunnable)tui.Ui.Window).StopRequested);

        moving().Should().BeFalse("the Stop was sent after the Open was answered, so it stops the motion the Open started");
        tui.Ui.MotionAnswerLost.Should().BeFalse();
        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        roof.Verify(service => service.Open(), Times.Once());
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public void F10_WhileItsOpenIsOnItsWay_WithNoAnswerInTime_StopsTheRoof_AndStaysOpen_ThenF10StopsTheRoofAgainIfTheOpenArrivedLate()
    {
        var (roof, moving) = MovingRoof();
        var onItsWay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host) { WrapHandler = inner => new InFlightRequestHandler(inner, "/RoofControl/Open", onItsWay, gate.Task) };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitFor("the Open to be on its way", () => onItsWay.Task.IsCompleted);

        tui.Press(Key.F10);
        tui.Tick(2);
        Settle(tui);
        roof.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
        tui.Tick();
        tui.WaitIdle("the Stop after the wait", () => !tui.Ui.MotionInFlight && !tui.Ui.StopInFlight && tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        tui.Ui.MotionAnswerLost.Should().BeTrue();
        tui.Ui.Message.Should().Be(
            "The Open or Close sent from here was not answered, so it may still reach the controller after the Stop. "
            + "The interface stays open to show the roof: F9 stops it, F10 closes the interface.");
        tui.Ui.QuitRequested.Should().BeFalse();
        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeFalse("the Open may still set the roof moving after the Stop");
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());

        // The Open reaches the controller after the Stop, and the status shows the roof moving.
        gate.SetResult();
        tui.WaitFor("the late Open", moving);
        roof.Raise(service => service.StatusChanged += null, new RoofStatusChangedEventArgs(roof.Object.GetCurrentStatusSnapshot()));
        tui.WaitIdle("the moving roof", () => tui.Ui.Status!.IsMoving);
        tui.Ui.FollowsMotion.Should().BeTrue("the late Open set the roof moving");

        tui.Press(Key.F10);
        tui.WaitIdle("Stop before quitting", () => ((IRunnable)tui.Ui.Window).StopRequested);

        tui.Ui.Message.Should().Be("Stopping the roof, which moves on a command from this interface, before closing.");
        moving().Should().BeFalse();
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Exactly(2));
    }

    [TestMethod]
    public void F10_WhileItsOpenIsOnItsWay_WithNoAnswerInTime_AndTheStopUnconfirmed_SaysBoth_ThenF10StopsTheRoofAgainIfTheOpenArrivedLate()
    {
        var (roof, moving) = MovingRoof();
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "The relays could not be read back (test).")));
        var onItsWay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host) { WrapHandler = inner => new InFlightRequestHandler(inner, "/RoofControl/Open", onItsWay, gate.Task) };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitFor("the Open to be on its way", () => onItsWay.Task.IsCompleted);

        // No answer within the wait, and nothing confirms the Stop sent after it.
        tui.Press(Key.F10);
        tui.Tick(3);
        tui.WaitIdle("the Stop after the wait", () => !tui.Ui.MotionInFlight && !tui.Ui.StopInFlight && tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.StopResult.Should().Be(RoofStopText.SentUnverified);
        tui.Ui.MotionAnswerLost.Should().BeTrue();
        tui.Ui.Message.Should().Be(
            "Nothing confirmed the Stop, and the Open or Close sent from here was not answered, so it may still reach the "
            + "controller after the Stop. The interface stays open to show the roof: F9 stops it, F10 closes the interface, "
            + "sending Stop again if the roof moves.");
        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeFalse();
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());

        // The Open reaches the controller after the Stop, and the status shows the roof moving.
        gate.SetResult();
        tui.WaitFor("the late Open", moving);
        roof.Raise(service => service.StatusChanged += null, new RoofStatusChangedEventArgs(roof.Object.GetCurrentStatusSnapshot()));
        tui.WaitIdle("the moving roof", () => tui.Ui.Status!.IsMoving);
        tui.Ui.FollowsMotion.Should().BeTrue("the late Open set the roof moving");

        tui.Press(Key.F10);

        tui.Ui.Message.Should().Be("Stopping the roof, which moves on a command from this interface, before closing.");
        tui.WaitIdle("Stop before quitting", () => ((IRunnable)tui.Ui.Window).StopRequested);
        roof.Verify(
            service => service.Stop(RoofControllerStopReason.NormalStop),
            Times.Exactly(2),
            "the only Stop went out before the Open arrived; F10 sends another before it closes, and has said both results");
    }

    [TestMethod]
    public void Closing_WaitsNoLongerThanFeedCloseWait_ForALiveStatusThatDoesNotClose()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        var tui = Started(rig);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tui.Ui.CloseFeed = async feed =>
        {
            await release.Task;
            await feed.DisposeAsync();
            closed.SetResult();
        };

        // Released later as well, so a wait that lost its bound fails the assertion below instead of hanging the run.
        using var releaseLater = new System.Threading.Timer(_ => release.TrySetResult(), null, TimeSpan.FromSeconds(5), System.Threading.Timeout.InfiniteTimeSpan);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        tui.Dispose();
        watch.Stop();
        release.TrySetResult();
        closed.Task.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the held live status closes once released");

        // The signal budget counts on this: a live status that does not close is dropped with the process.
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(RoofTerminalUi.FeedCloseWait - TimeSpan.FromMilliseconds(50))
            .And.BeLessThan(RoofTerminalUi.FeedCloseWait + TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public void ClosingOnASignal_FitsInTheStopGrace_WithASecondToRestoreTheTerminal()
    {
        // After a signal: the wait for an Open or Close's answer, the Stop, then the live status closing. The terminal is
        // restored, and the Stop's result said on it, in what is left.
        var longest = RoofCli.CommandBuilder.MotionAnswerWait + new RoofConnectionOptions { BaseAddress = ClientTestSupport.BaseAddress }.StopTimeout + RoofTerminalUi.FeedCloseWait;

        (RoofCliTermination.StopGrace - longest).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public void F10_WhileItsOpenIsOnItsWay_WhoseAnswerIsLost_StopsTheRoofAfterTheWait_ThenF10ClosesTheStoppedRoof()
    {
        var (roof, moving) = MovingRoof();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host) { WrapHandler = inner => new HeldAnswerHandler(inner, "/RoofControl/Open", reached, answer.Task) };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        // The controller has the Open, and its answer never arrives.
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitFor("the Open to reach the controller", () => reached.Task.IsCompleted);
        moving().Should().BeTrue();

        tui.Press(Key.F10);
        tui.Tick(3);
        tui.WaitIdle("the Stop after the wait", () => !tui.Ui.MotionInFlight && !tui.Ui.StopInFlight && tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        moving().Should().BeFalse("quitting stops a motion a command from here may have started");
        tui.Ui.Message.Should().StartWith("The Open or Close sent from here was not answered,");
        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeFalse();

        tui.Press(Key.F10);

        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeTrue("the roof is not moving, so F10 closes it");
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public void F10_WhileItsOpenIsOnItsWay_WhoseConnectionEndsDuringTheWait_StopsTheRoof_AndStaysOpen_ThenF10Closes()
    {
        var (roof, moving) = MovingRoof();
        var onItsWay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host) { WrapHandler = inner => new InFlightRequestHandler(inner, "/RoofControl/Open", onItsWay, gate.Task) };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitFor("the Open to be on its way", () => onItsWay.Task.IsCompleted);

        tui.Press(Key.F10);
        tui.Tick();

        // The connection ends within the wait: the Open may have been delivered, or may yet be.
        gate.SetException(new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely (test)."));
        tui.WaitIdle("the Stop after the Open ended", () => !tui.Ui.MotionInFlight && !tui.Ui.StopInFlight && tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        tui.Ui.MotionAnswerLost.Should().BeTrue("the Open may still reach the controller after the Stop");
        tui.Ui.Message.Should().StartWith("The Open or Close sent from here was not answered,");
        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeFalse("the Open may still set the roof moving after the Stop");
        moving().Should().BeFalse();

        tui.Press(Key.F10);

        ((IRunnable)tui.Ui.Window).StopRequested.Should().BeTrue("the roof is not moving, so F10 closes it");
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public void AnOpenAcceptedAfterAStopSentFromHere_IsStoppedAgain()
    {
        var (roof, moving) = MovingRoof();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RoofClientApiTests.CreateHost(roof);

        // The Open is slow to reach the controller, and the Stop sent after it gets there first.
        using var rig = new CliRig(host)
        {
            WrapHandler = inner => new HeldAnswerHandler(inner, "/RoofControl/Open", held, release.Task, forwardFirst: false)
        };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitFor("the Open to be on its way", () => held.Task.IsCompleted);
        tui.Press(Key.F9);
        tui.WaitFor("the Stop's answer", () => !tui.Ui.StopInFlight);
        moving().Should().BeFalse();

        release.SetResult();
        tui.WaitIdle("the Open's answer", () => !tui.Ui.MotionInFlight && !tui.Ui.StopInFlight);

        tui.Ui.Message.Should().Be("Open was accepted after Stop was sent from here, so Stop is sent again.");
        tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
        moving().Should().BeFalse("Stop wins over a command sent before it");
        roof.Verify(service => service.Open(), Times.Once());
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Exactly(2));
    }

    [TestMethod]
    public void AnOpenWhoseAnswerIsLost_SaysTheRoofMayBeMoving_AndF10StopsIt()
    {
        var (roof, moving) = MovingRoof();
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host)
        {
            WrapHandler = inner => new DroppedAnswerHandler(
                inner, "/RoofControl/Open", new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely (test)."))
        };
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the Open", () => !tui.Ui.MotionInFlight);

        tui.Ui.Message.Should().Be(
            "The connection to the controller ended before its answer arrived. The Open may have reached the controller, and the roof may be moving. To stop it, press F9.");
        moving().Should().BeTrue();

        // The status feed shows the roof moving.
        roof.Raise(service => service.StatusChanged += null, new RoofStatusChangedEventArgs(roof.Object.GetCurrentStatusSnapshot()));
        tui.WaitIdle("the moving roof", () => tui.Ui.Status!.IsMoving);
        tui.Ui.FollowsMotion.Should().BeTrue("the Open may have set it moving");

        tui.Press(Key.F10);
        tui.WaitIdle("Stop before quitting", () => ((IRunnable)tui.Ui.Window).StopRequested);

        tui.Ui.Message.Should().Be("Stopping the roof, which moves on a command from this interface, before closing.");
        moving().Should().BeFalse();
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public void ARefusedOpen_SaysWhy_AndOffersTheButtonAgain()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.Open()).Returns(Result<RoofControllerStatus>.Failure(new InvalidOperationException("The roof is not ready.")));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the refusal", () => !tui.Ui.Message.StartsWith("Sending", StringComparison.Ordinal));

        tui.Ui.Message.Should().NotBeNullOrWhiteSpace();
        tui.Ui.Message.Should().NotStartWith("Open accepted");
        tui.Ui.HoldsLease.Should().BeFalse();
        page.OpenButton.Enabled.Should().BeTrue("the command is no longer on its way");
    }

    // ---- Setup -------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void Setup_FromNothing_SavesTheConnection_AddsTheFirstAdmin_SignsInAndOut()
    {
        using var host = RoofClientApiTests.CreateHost();
        _ = host.Server;
        using var rig = new CliRig(host);
        using var tui = new TuiDriver(rig);
        var setup = (RoofUiSetupPage)tui.Ui.CurrentPage;
        setup.Describe().Should().Contain("Connection saves the controller's address");

        Click(tui, "Connection");
        Fill(tui, "http://localhost/", string.Empty, TestApiKeys.Admin);
        tui.Ui.Panel!.Fields[2].Secret.Should().BeTrue("the API key is not shown as it is typed");
        tui.Ui.Panel.Press("Save and check");
        tui.WaitIdle("the check", () => setup.LastCheck is not null);

        tui.Ui.Panel.Should().BeNull();
        rig.Stored!.Controller.Should().Be(ClientTestSupport.BaseAddress);
        rig.Stored.ApiKey.Should().Be(TestApiKeys.Admin);
        setup.LastCheck!.NeedsFirstAdmin.Should().BeTrue();
        tui.Ui.Message.Should().StartWith("Saved in ").And.EndWith("Connected with an admin API key, and nobody can sign in yet: add the first admin person.");
        setup.Describe().Should().Contain("Credential:  API key (from ");

        Click(tui, "First admin");
        Fill(tui, "ada", TestSecrets.Password, TestSecrets.OtherPassword);
        tui.Ui.Panel!.Press("Add");
        tui.Ui.Panel!.Error.Should().Be("The two passwords do not match.");
        Fill(tui, "ada", TestSecrets.Password, TestSecrets.Password);
        tui.Ui.Panel!.Press("Add");
        tui.WaitIdle("the first admin", () => tui.Ui.Message.StartsWith("Added ada", StringComparison.Ordinal) && !tui.Ui.Message.Contains("Checking", StringComparison.Ordinal));
        tui.Ui.Message.Should().Be("Added ada (admin). Sign in as ada here, with Sign in. The connection works.");

        Click(tui, "Sign in");
        Fill(tui, "ada", TestSecrets.Password);
        tui.Ui.Panel!.Fields[1].Secret.Should().BeTrue();
        tui.Ui.Panel.Press("Sign in");
        tui.WaitIdle("the sign-in", () => tui.Ui.Caller?.Kind == RoofCredentialKind.Session);

        tui.Ui.Message.Should().StartWith("Signed in as ada (admin) until ");
        rig.Stored!.Session!.Name.Should().Be("ada");
        rig.Stored.ApiKey.Should().Be(TestApiKeys.Admin, "signing in keeps the saved key");
        tui.Screen.Should().Contain("ada (admin) · status");

        Click(tui, "Sign out");
        tui.WaitIdle("the sign-out", () => tui.Ui.Caller?.Kind == RoofCredentialKind.ApiKey);

        tui.Ui.Message.Should().Be($"Signed out. The session was removed from {rig.CredentialsPath}.");
        rig.Stored!.Session.Should().BeNull();
    }

    [TestMethod]
    public void Setup_Connection_RefusesABadAddress_InThePrompt()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);
        using var tui = new TuiDriver(rig);

        Click(tui, "Connection");
        Fill(tui, "roof.local", string.Empty, string.Empty);
        tui.Ui.Panel!.Press("Save and check");

        tui.Ui.Panel.Should().NotBeNull("the prompt stays open to fix the address");
        tui.Ui.Panel!.Error.Should().Contain("'roof.local' is not a controller address.");
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    public void Setup_FirstAdmin_WithAnOperatorKey_IsRefusedWithoutAPrompt()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        tui.Press(Key.F5);

        Click(tui, "First admin");

        tui.Ui.Panel.Should().BeNull();
        tui.Ui.Message.Should().Be("Adding a person needs the Admin role; test-operator is operator.");
    }

    // ---- People ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void People_AddsAPerson_AndShowsANewKeysSecretOnce()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);
        tui.Press(Key.F3);
        tui.WaitIdle("the lists");
        var people = (RoofUiPeoplePage)tui.Ui.CurrentPage;
        people.Describe().Should().Contain("No people yet");
        tui.Ui.Message.Should().MatchRegex(@"^0 people, \d+ API keys, 0 sessions\.$");

        Click(tui, "Add");
        Fill(tui, "grace", "operator", TestSecrets.Password, TestSecrets.Password, string.Empty, string.Empty);
        tui.Ui.Panel!.Press("Add");
        tui.WaitIdle("the new person", () => people.Describe().Contains("grace", StringComparison.Ordinal));

        tui.Ui.Message.Should().Be("Added grace (operator).", "the result is not replaced by the reload");
        people.Describe().Should().MatchRegex("grace +operator");

        Click(tui, "API keys");
        people.Showing.Should().Be(RoofUiPeoplePage.Mode.Keys);
        people.Describe().Should().Contain("test-admin");
        Click(tui, "Add");
        Fill(tui, "dome-kiosk", "viewer", "yes");
        tui.Ui.Panel!.Press("Add");
        tui.WaitIdle("the new key", () => tui.Ui.Panel?.Prompt.Title == "New API key");

        var secret = tui.Ui.Panel!.Fields[0];
        secret.ReadOnly.Should().BeTrue();
        secret.Text.Should().NotBeNullOrWhiteSpace();
        tui.Ui.Panel.Prompt.Message.Should().Be("The secret of dome-kiosk. Copy it now: it is not shown again.");
        tui.Ui.Message.Should().Be("The key dome-kiosk is ready (viewer, kiosk).");
        people.Describe().Should().NotContain(secret.Text, "the list never shows a secret");
        tui.Ui.Panel.Press("Done");
        tui.Ui.Panel.Should().BeNull();

        people.Select("test-admin");
        Click(tui, "Rotate");
        tui.Ui.Message.Should().Be("The key test-admin comes from the controller's configuration and is read-only here.");
        tui.Ui.Panel.Should().BeNull();
    }

    [TestMethod]
    public void People_SettingAPassword_KeepsARoleChangedElsewhere()
    {
        using var host = RoofClientApiTests.CreateHost();
        RoofClientApiTests.AddUserAsync(host, "grace", RoofControllerApiContract.OperatorRole).GetAwaiter().GetResult();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);
        tui.Press(Key.F3);
        tui.WaitIdle("the lists");
        var people = (RoofUiPeoplePage)tui.Ui.CurrentPage;
        people.Describe().Should().MatchRegex("grace +operator");

        // Another admin makes grace an admin after the list was read.
        OffTheLoop(async () =>
        {
            using var other = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
            await other.Identity.UpdateUserAsync("grace", new RoofUserUpdateRequest { Role = RoofControllerApiContract.AdminRole });
        });

        people.Select("grace");
        Click(tui, "Password");
        Fill(tui, TestSecrets.OtherPassword, TestSecrets.OtherPassword);
        tui.Ui.Panel!.Press("Set");
        tui.WaitIdle("the change", () => tui.Ui.Message == "Set the password of grace.");

        UserRole(host, "grace").Should().Be(RoofControllerApiContract.AdminRole, "the list's out-of-date role is not sent back");
        people.Describe().Should().MatchRegex("grace +admin");
    }

    [TestMethod]
    public void People_Role_ChangesTheRole()
    {
        using var host = RoofClientApiTests.CreateHost();
        RoofClientApiTests.AddUserAsync(host, "grace", RoofControllerApiContract.OperatorRole).GetAwaiter().GetResult();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);
        tui.Press(Key.F3);
        tui.WaitIdle("the lists");
        var people = (RoofUiPeoplePage)tui.Ui.CurrentPage;

        people.Select("grace");
        Click(tui, "Role");
        Fill(tui, "viewer");
        tui.Ui.Panel!.Press("Change");
        tui.WaitIdle("the change", () => tui.Ui.Message == "grace is now viewer.");

        UserRole(host, "grace").Should().Be(RoofControllerApiContract.ViewerRole);
        people.Describe().Should().MatchRegex("grace +viewer");
    }

    [TestMethod]
    public void People_ForAnOperator_SaysTheAdminRoleIsNeeded()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);

        tui.Press(Key.F3);
        tui.WaitIdle("the page");

        tui.Ui.CurrentPage.Describe().Should().StartWith("People, API keys and sessions need the Admin role; test-operator is operator.");
    }

    // ---- Settings ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public void Settings_AnOperatorChangesTheUiGroup()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        tui.Press(Key.F2);
        tui.WaitIdle("the settings", () => ((RoofUiSettingsPage)tui.Ui.CurrentPage).Form is not null);
        var settings = (RoofUiSettingsPage)tui.Ui.CurrentPage;
        settings.Form!.Groups.Select(group => group.Name).Should().Equal(RoofSettingsContract.UiGroup);

        settings.Select("RoofControllerUi:DefaultCamera");
        Click(tui, "Change");
        tui.Ui.Panel!.Prompt.Title.Should().Be("Change Default camera");
        Fill(tui, "Pier");
        tui.Ui.Panel!.Press("Save");
        tui.WaitIdle("the save", () => tui.Ui.Message.StartsWith("Saved", StringComparison.Ordinal));

        tui.Ui.Message.Should().StartWith($"Saved (settings version {settings.Form!.Version}).");
        settings.Form.FindField("RoofControllerUi:DefaultCamera")!.DisplayValue.Should().Contain("Pier");
    }

    [TestMethod]
    public void Settings_ASafetyCriticalChange_IsShown_AndSentOnlyWhenConfirmed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-roof-tui-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var roof = new SettingsApiTests.RoofDouble();
            using var host = new RoofApiTestHost(
                roof.Mock,
                configureServices: services => services.Configure<Microsoft.AspNetCore.Identity.PasswordHasherOptions>(options => options.IterationCount = 1_000),
                settingsFilePath: Path.Combine(directory, "config", "appsettings.Local.json"),
                secretsFilePath: Path.Combine(directory, "secrets", "managed-secrets.json"));
            roof.StartFrom(host);
            using var rig = new CliRig(host);
            rig.UseApiKey(TestApiKeys.Admin);
            using var tui = Started(rig);
            tui.Press(Key.F2);
            tui.WaitIdle("the settings", () => ((RoofUiSettingsPage)tui.Ui.CurrentPage).Form is not null);
            var settings = (RoofUiSettingsPage)tui.Ui.CurrentPage;
            var before = settings.Form!.Version;

            settings.Select("RoofControllerOptionsV4:AtSpeedConfirmationTimeout");
            Click(tui, "Change");
            Fill(tui, string.Empty);
            tui.Ui.Panel!.Press("Save");

            tui.Ui.Panel!.Prompt.Title.Should().Be("Safety-critical change");
            tui.Ui.Panel.Prompt.Message.Should().Contain("SAFETY-CRITICAL").And.Contain("Send it only if the roof is safe with it.");
            tui.WaitIdle("nothing sent");
            settings.Form!.Version.Should().Be(before, "nothing is sent before it is confirmed");

            tui.Ui.Panel.Press("Confirm and send");
            tui.WaitIdle("the save", () => tui.Ui.Message.StartsWith("Saved", StringComparison.Ordinal));

            settings.Form!.Version.Should().Be(before + 1);
            settings.Form.FindField("RoofControllerOptionsV4:AtSpeedConfirmationTimeout")!.DisplayValue.Should().Be(RoofSettingValues.None);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// With the camera proxy on, the controller takes the Blue Iris user and password only together: the terminal UI
    /// asks for both, each typed twice, and clears them together.
    /// </summary>
    [TestMethod]
    public void Settings_TheCamerasUserAndPassword_AreSetAndClearedTogether()
    {
        const string user = "test-camera-user-not-real";
        const string password = "test-camera-password-not-real-16";
        var directory = Path.Combine(Path.GetTempPath(), "hvo-roof-tui-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var roof = new SettingsApiTests.RoofDouble();
            using var host = new RoofApiTestHost(
                roof.Mock,
                settings: new Dictionary<string, string?> { ["BlueIris:BaseUrl"] = "http://192.168.0.4:81" },
                configureServices: services => services.Configure<Microsoft.AspNetCore.Identity.PasswordHasherOptions>(options => options.IterationCount = 1_000),
                settingsFilePath: Path.Combine(directory, "config", "appsettings.Local.json"),
                secretsFilePath: Path.Combine(directory, "secrets", "managed-secrets.json"));
            roof.StartFrom(host);
            using var rig = new CliRig(host);
            rig.UseApiKey(TestApiKeys.Admin);
            using var tui = Started(rig);
            tui.Press(Key.F2);
            tui.WaitIdle("the settings", () => ((RoofUiSettingsPage)tui.Ui.CurrentPage).Form is not null);
            var settings = (RoofUiSettingsPage)tui.Ui.CurrentPage;

            settings.Select("BlueIris:Password");
            Click(tui, "Change");
            tui.Ui.Panel!.Prompt.Title.Should().Be("Change User name and Password");
            tui.Ui.Panel.Prompt.Message.Should().StartWith("User name and Password are set and cleared together.");
            tui.Ui.Panel.Fields.Select(field => field.Secret).Should().AllBeEquivalentTo(true, "no secret is shown as it is typed");
            Fill(tui, user, user, password, password + "!");
            tui.Ui.Panel.Press("Save");
            tui.Ui.Panel!.Error.Should().Be("The two Password values differ.");

            Fill(tui, user, user, password, password);
            tui.Ui.Panel.Press("Save");
            tui.WaitIdle("the save", () => tui.Ui.Message.StartsWith("Saved", StringComparison.Ordinal));
            settings.Form!.FindField("BlueIris:UserName")!.DisplayValue.Should().Be(RoofSettingValues.SecretSet);
            settings.Form.FindField("BlueIris:Password")!.DisplayValue.Should().Be(RoofSettingValues.SecretSet);
            tui.Screen.Should().NotContain(password).And.NotContain(user);

            settings.Select("BlueIris:Password");
            Click(tui, "Clear secret");
            tui.Ui.Panel!.Prompt.Title.Should().Be("Clear User name and Password");
            tui.Ui.Panel.Press("Clear them");
            tui.WaitIdle("the clear", () => settings.Form!.FindField("BlueIris:Password")!.DisplayValue == RoofSettingValues.SecretNotSet);
            settings.Form!.FindField("BlueIris:UserName")!.DisplayValue.Should().Be(RoofSettingValues.SecretNotSet);

            settings.Select("BlueIris:Password");
            Click(tui, "Clear secret");
            tui.Ui.Panel.Should().BeNull();
            tui.Ui.Message.Should().Be("Nothing to clear: Password is not set.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow(true, DisplayName = "A hand edit is pending")]
    [DataRow(false, DisplayName = "None is pending")]
    public void Settings_HandEdit_ForAnOperator_SaysAnAdminIsNeeded_WithoutClaimingNoneIsPending(bool pending)
    {
        var directory = Path.Combine(Path.GetTempPath(), "hvo-roof-tui-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var roof = new SettingsApiTests.RoofDouble();
            var settingsPath = Path.Combine(directory, "config", "appsettings.Local.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            using var host = new RoofApiTestHost(
                roof.Mock,
                configureServices: services => services.Configure<Microsoft.AspNetCore.Identity.PasswordHasherOptions>(options => options.IterationCount = 1_000),
                settingsFilePath: settingsPath,
                secretsFilePath: Path.Combine(directory, "secrets", "managed-secrets.json"));
            roof.StartFrom(host);
            if (pending)
            {
                File.WriteAllText(settingsPath, "{ \"RoofControllerUi\": { \"DefaultCamera\": \"Yard\" } }");
            }

            using var rig = new CliRig(host);
            rig.UseApiKey(TestApiKeys.Operator);
            using var tui = Started(rig);
            tui.Press(Key.F2);
            tui.WaitIdle("the settings", () => ((RoofUiSettingsPage)tui.Ui.CurrentPage).Form is not null);

            Click(tui, "Hand edit");

            // The controller shows a hand edit only to admins; what it does show (its refusals) says one is pending.
            const string NeedsAdmin = "Reviewing, applying or discarding a hand edit of the settings file needs the admin role.";
            tui.Ui.Message.Should().Be(pending ? NeedsAdmin + " A hand edit is pending: ask an admin to review it." : NeedsAdmin);
            tui.Ui.Panel.Should().BeNull();
            var screen = Unwrapped(tui.Screen);
            if (pending)
            {
                screen.Should().Contain("The settings file was edited by hand: changes here are refused until an admin applies or discards it.");
            }
            else
            {
                screen.Should().NotContain("edited by hand");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ---- System ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void System_ShowsHealth_AndRestartsOnlyWhenConfirmed()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);
        tui.Press(Key.F4);
        tui.WaitIdle("the health report", () => tui.Ui.Message.StartsWith("Health:", StringComparison.Ordinal));
        var system = (RoofUiSystemPage)tui.Ui.CurrentPage;

        system.Describe().Should().Contain("Health: Degraded").And.Contain("identity_store");
        system.Describe().Should().NotContain("need the Admin role");

        Click(tui, "Restart");
        tui.WaitIdle("the restart prompt", () => tui.Ui.Panel is not null);
        tui.Ui.Panel!.Prompt.Message.Should().StartWith(RoofUiSystemPage.RestartQuestion);
        tui.Press(Key.Esc);
        tui.Ui.Panel.Should().BeNull();
        host.Services.GetRequiredService<RoofRestartSignal>().Requested.Should().BeFalse("the restart was not confirmed");

        Click(tui, "Restart");
        tui.WaitIdle("the restart prompt", () => tui.Ui.Panel is not null);
        tui.Ui.Panel!.Press("Restart");
        tui.WaitIdle("the restart", () => tui.Ui.Message.StartsWith("The roof is stopped.", StringComparison.Ordinal));

        tui.Ui.Message.Should().EndWith("Readiness says when it is back; the status comes back by itself.");
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.HostShutdown), Times.AtLeastOnce());
    }

    [TestMethod]
    public void System_ForAnOperator_ShowsHealth_ButNotRestart()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        tui.Press(Key.F4);
        tui.WaitIdle("the health report", () => tui.Ui.Message.StartsWith("Health:", StringComparison.Ordinal));

        tui.Ui.CurrentPage.Describe().Should().StartWith("The version, host and resource use need the Admin role.");
        Click(tui, "Restart");

        tui.Ui.Panel.Should().BeNull();
        tui.Ui.Message.Should().Be("Restarting the controller needs the Admin role.");
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    // The screen's words in order, without frame lines, button shadows or line ends, so wrapped text reads as one line.
    private static string Unwrapped(string screen)
        => string.Join(' ', screen.Split([' ', '\n', '│', '▖', '▘', '▝', '▀'], StringSplitOptions.RemoveEmptyEntries));

    private static void ShouldHaveColours(TuiAttribute drawn, string foreground, string background, string because = "")
    {
        drawn.Foreground.Should().Be(new Color(foreground), because);
        drawn.Background.Should().Be(new Color(background), because);
    }

    /// <summary>The role the controller has for <paramref name="name"/>.</summary>
    private static string UserRole(RoofApiTestHost host, string name)
    {
        string role = string.Empty;
        OffTheLoop(async () =>
        {
            using var admin = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
            role = (await admin.Identity.GetUserAsync(name)).Role;
        });
        return role;
    }

    /// <summary>
    /// A roof whose Open sets it moving on no lease and whose Stop stops it, each status newer than the last; the second
    /// item says whether it moves.
    /// </summary>
    private static (Mock<IRoofControllerServiceV4> Roof, Func<bool> Moving) MovingRoof()
    {
        var moving = false;
        long version = 100;
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => (Volatile.Read(ref moving)
                ? RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = null }
                : RoofServiceMock.Snapshot())
            with { StatusVersion = Interlocked.Increment(ref version) });
        roof.Setup(service => service.Open()).Returns(() =>
        {
            Volatile.Write(ref moving, true);
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(() =>
        {
            Volatile.Write(ref moving, false);
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Stopped);
        });
        return (roof, () => Volatile.Read(ref moving));
    }

    /// <summary>
    /// Runs <paramref name="call"/> to the end on the thread pool: the test's thread is the interface's loop, whose
    /// synchronization context runs a continuation only when the loop is pumped, so waiting there would never end.
    /// </summary>
    private static void OffTheLoop(Func<Task> call) => Task.Run(call).GetAwaiter().GetResult();

    /// <summary>
    /// The interface with its first status live and the caller known. The hub can deliver the first status just before it
    /// reports itself connected: until then the status is stale, and Open and Close are not offered.
    /// </summary>
    private static TuiDriver Started(CliRig rig)
    {
        var tui = new TuiDriver(rig);
        try
        {
            tui.WaitIdle("the first status", () => tui.Ui.Status is not null && !tui.Ui.IsStale && tui.Ui.Caller is not null);
            return tui;
        }
        catch
        {
            tui.Dispose();
            throw;
        }
    }

    /// <summary>Runs the loop for long enough that a request sent now would have reached the controller.</summary>
    private static void Settle(TuiDriver tui)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
        while (DateTime.UtcNow < until)
        {
            tui.Pump();
            Thread.Sleep(10);
        }
    }

    /// <summary>Presses the visible button labelled <paramref name="label"/> on the page or prompt, as Enter on it would.</summary>
    private static void Click(TuiDriver tui, string label)
    {
        View root = tui.Ui.Panel is { } panel ? panel : tui.Ui.CurrentPage;
        var button = Find(root, label) ?? throw new AssertFailedException($"No visible button '{label}'. Screen:\n{tui.Screen}");
        button.InvokeCommand(Command.Accept);
        tui.Pump();
    }

    private static Button? Find(View view, string label)
    {
        foreach (var child in view.SubViews)
        {
            if (child is Button { Visible: true } button && button.Text == label)
            {
                return button;
            }

            if (child.Visible && Find(child, label) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void Fill(TuiDriver tui, params string[] values)
    {
        var fields = tui.Ui.Panel?.Fields ?? throw new AssertFailedException($"No prompt is open. Screen:\n{tui.Screen}");
        fields.Should().HaveCount(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            fields[i].Text = values[i];
        }
    }
}
