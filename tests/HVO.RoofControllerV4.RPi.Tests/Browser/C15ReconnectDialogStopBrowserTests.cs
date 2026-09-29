using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Components.Pages;
using HVO.RoofControllerV4.RPi.Security;
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
    private const string NotReached = "Stop failed: The controller could not be reached. Use the stop control at the roof.";

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
    public async Task WhenTheSessionHasEnded_TheDialogsStop_IsNotSent_AndSaysToReloadOrUseTheRoofStop()
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
        await Expect(browser.DialogStopResult).ToHaveTextAsync(RoofStopText.PageSignedOut);
        rig.Controller.GetCurrentStatusSnapshot().IsMoving.Should().BeTrue("a stop without a session is refused");
        rig.Logs.Entries.Should().NotContain(e => e.Message.StartsWith("Console stop (no circuit) from "));

        await client.AcceptedAsync("Stop");
        (await rig.WaitForRestAsync("the API's Stop")).ShouldBeDeenergized(rig);
    }

    /// <summary>
    /// Not a commissioning step: a dialog whose texts cannot be read still sends Stop. The browser posts the form itself
    /// and shows the controller's answer as the page.
    /// </summary>
    [TestMethod]
    public async Task WithoutItsTexts_TheDialogsStop_IsStillSent()
    {
        var browser = _browser = await ConsoleBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0), ConsoleDevices.Phone);
        var rig = browser.Rig;
        await browser.SignInAsync(TestApiKeys.Operator);
        await browser.Open.ClickAsync();
        await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");

        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        // The answer opens in a new tab: Playwright's WebSocket routes fail when the console page itself navigates away.
        await browser.Page.EvalOnSelectorAsync(
            "form[data-console-stop]",
            "form => { form.removeAttribute('data-console-stop-texts'); form.target = '_blank'; }");
        var answer = await browser.Page.RunAndWaitForPopupAsync(() => browser.DialogStop.ClickAsync());

        var stopped = await rig.WaitForRestAsync("the reconnect dialog's Stop, posted by the browser");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.ShouldBeDeenergized(rig);
        rig.Logs.Entries.Should().Contain(e => e.Message.StartsWith("Console stop (no circuit) from "));
        await Expect(answer.Locator("body")).ToContainTextAsync(RoofStopText.AcknowledgedVerified);
    }

    /// <summary>
    /// Not a commissioning step: an answer that is not the endpoint's own (a proxy's error page, the origin check, a
    /// status with no wording) and an answer that never comes are described as every other client describes them.
    /// </summary>
    [TestMethod]
    public async Task AnAnswerThatIsNotTheControllers_OrNoAnswer_IsDescribedInTheSharedWording()
    {
        var browser = _browser = await ConsoleBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0), ConsoleDevices.Phone);
        await browser.SignInAsync(TestApiKeys.Operator);
        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        var stopPath = "**" + RoofControllerSecurityDefaults.ConsoleStopPath;

        async Task ExpectAnswerAsync(int status, string contentType, string body, string text)
        {
            await browser.Page.UnrouteAsync(stopPath);
            await browser.Page.RouteAsync(stopPath, route => route.FulfillAsync(new() { Status = status, ContentType = contentType, Body = body }));
            await browser.DialogStop.ClickAsync();
            await Expect(browser.DialogStopResult).ToHaveAttributeAsync("data-state", "failed");
            await Expect(browser.DialogStopResult).ToHaveTextAsync(text);
        }

        await ExpectAnswerAsync(502, "text/html", "<html><body>Bad gateway</body></html>",
            RoofStopText.Failed(RoofText.DescribeRefusal(502, null, null)));
        await ExpectAnswerAsync(503, "text/html", "<html><body>Service unavailable</body></html>",
            "Stop failed: The controller is not ready. Try again shortly. Use the stop control at the roof.");
        await ExpectAnswerAsync(403, "application/problem+json", """{ "status": 403, "code": "origin_not_allowed" }""",
            RoofStopText.Failed(RoofText.DescribeRefusal(403, null, "origin_not_allowed")));
        await ExpectAnswerAsync(403, "application/problem+json", """{ "status": 403, "code": "constructor" }""",
            RoofStopText.Failed(RoofText.DescribeRefusal(403, null, null)));
        await ExpectAnswerAsync(418, "text/plain", "teapot",
            "Stop failed: The controller refused the request (HTTP 418). Use the stop control at the roof.");

        // The answer never comes: the page gives up after 5 s.
        await browser.Page.UnrouteAsync(stopPath);
        await browser.Page.RouteAsync(stopPath, _ => Task.CompletedTask);
        await browser.DialogStop.ClickAsync();
        await Expect(browser.DialogStopResult).ToHaveTextAsync(RoofStopText.Sending);
        await Expect(browser.DialogStopResult).ToHaveTextAsync(RoofStopText.Failed(RoofText.TimedOut), new() { Timeout = 10_000 });
        await Expect(browser.DialogStopResult).ToHaveAttributeAsync("data-state", "failed");
        browser.Rig.Controller.GetCurrentStatusSnapshot().LastStopReason.Should().NotBe(
            RoofControllerStopReason.NormalStop, "no stop reached the controller: every answer came from the test's route");
    }
}
