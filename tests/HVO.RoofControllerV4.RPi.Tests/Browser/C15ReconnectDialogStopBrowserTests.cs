using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Components.Pages;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// C15, the reconnect dialog's Stop, on a phone: while the console's connection is down, the dialog covers the console
/// and its Stop posts to the controller without the circuit. It says when the stop could not be sent, stops the roof
/// once the network is back, and refuses to send one for a session that has ended.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class C15ReconnectDialogStopBrowserTests
{
    private const string NotReached = "Stop could not reach the controller. Use the stop control at the roof.";

    private ConsoleBrowser? _browser;

    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public async Task CleanupAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }
    }

    [TestMethod]
    [CommissioningCheck("C15", "1")]
    [CommissioningCheck("C15", "2")]
    [CommissioningCheck("C15", "3")]
    public async Task WhileTheConsoleIsDisconnected_TheDialogsStop_SaysWhenItCannotReachTheController_AndStopsTheRoofWhenItCan()
    {
        var browser = _browser = await ConsoleBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0), ConsoleDevices.Phone);
        var rig = browser.Rig;
        await browser.SignInAsync(TestApiKeys.Operator);

        await browser.Open.ClickAsync();
        await Expect(browser.Position).ToHaveTextAsync("Opening");
        var moving = await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");
        moving.LeaseSecondsRemaining.Should().BeNull("the documented installation runs without an operator lease, so only Stop ends this move");

        // The phone loses the network: the circuit drops and nothing reaches the controller.
        await browser.CutConnectionAsync();
        await browser.Context.SetOfflineAsync(true);
        await browser.ExpectReconnectDialogAsync();
        var dialogStop = await browser.PlacementOfAsync(browser.DialogStop);
        dialogStop.Reachable.Should().BeTrue("the dialog's Stop must be usable on the phone: {0}", dialogStop);

        await browser.DialogStop.ClickAsync();
        await Expect(browser.DialogStopResult).ToHaveAttributeAsync("data-state", "failed", new() { Timeout = 6_000 });
        await Expect(browser.DialogStopResult).ToHaveTextAsync(NotReached);
        rig.Controller.GetCurrentStatusSnapshot().IsMoving.Should().BeTrue("the stop did not reach the controller");

        // The network is back, but the circuit is not yet: the dialog's Stop reaches the controller on its own.
        await browser.Context.SetOfflineAsync(false);
        await browser.DialogStop.ClickAsync();
        await Expect(browser.DialogStopResult).ToHaveAttributeAsync("data-state", "ok");
        await Expect(browser.DialogStopResult).ToHaveTextAsync("Stop acknowledged. Relay register verified de-energized.");

        var stopped = await rig.WaitForRestAsync("the reconnect dialog's Stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.ShouldBeDeenergized(rig);
        rig.Plant.OpenLimitActuated.Should().BeFalse("the dialog's Stop ended the move before the open limit");
        rig.Session.Plant.Violations.Should().BeEmpty();
        rig.Logs.Entries.Should().Contain(e => e.Message.StartsWith("Console stop (no circuit) from "), "the controller took the stop without the circuit");
        await Expect(browser.ReconnectDialog).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(@"\bcomponents-reconnect-show\b"));

        browser.RestoreConnection();
        await browser.ExpectReconnectedAsync();
        await Expect(browser.Position).ToHaveTextAsync(RoofConsoleRules.DescribePosition(stopped.Status));
        await Expect(browser.Commanded).ToHaveTextAsync("None");
    }

    /// <summary>
    /// Not a commissioning step: an expired or cleared session must not be able to send a stop, and the dialog must say
    /// the stop was not sent rather than that the controller is unreachable.
    /// </summary>
    [TestMethod]
    public async Task WhenTheSessionHasEnded_TheDialogsStop_IsNotSent_AndSaysToSignInOrUseTheRoofStop()
    {
        var browser = _browser = await ConsoleBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0), ConsoleDevices.Phone);
        var rig = browser.Rig;
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await browser.SignInAsync(TestApiKeys.Operator);

        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        await browser.Context.ClearCookiesAsync();
        await client.AcceptedAsync("Open");
        await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");

        await browser.DialogStop.ClickAsync();
        await Expect(browser.DialogStopResult).ToHaveAttributeAsync("data-state", "failed");
        await Expect(browser.DialogStopResult).ToHaveTextAsync(
            "Stop was not sent because the session has ended. Reload and sign in, or use the stop control at the roof.");
        rig.Controller.GetCurrentStatusSnapshot().IsMoving.Should().BeTrue("a stop without a session is refused");
        rig.Logs.Entries.Should().NotContain(e => e.Message.StartsWith("Console stop (no circuit) from "));

        await client.AcceptedAsync("Stop");
        (await rig.WaitForRestAsync("the API's Stop")).ShouldBeDeenergized(rig);
    }
}
