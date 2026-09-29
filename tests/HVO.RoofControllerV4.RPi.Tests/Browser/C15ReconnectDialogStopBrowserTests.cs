using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Web.Roof;
using HVO.RoofControllerV4.Web.Security;
using HVO.RoofControllerV4.Web.Sessions;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// C15, the reconnect dialog's Stop, on a phone: while the page's live connection is down, the dialog covers the page and
/// its Stop posts <c>/stop</c> to the web UI without the live connection. It says when Stop could not be sent, stops the
/// roof once the network is back (after the person's controller session ended too, with the web UI's Stop key), and
/// refuses to send one for a page that has been signed out.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class C15ReconnectDialogStopBrowserTests
{
    private static readonly string StopFrom = $"Web stop from {WebBrowser.Operator} at ";

    private WebBrowser? _browser;

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
    public async Task WhileTheWebPageIsDisconnected_TheDialogsStop_SaysWhenItCannotReachTheWebUI_AndStopsTheRoofWhenItCan()
    {
        var browser = _browser = await StartAsync();
        var rig = browser.Rig;
        await browser.SignInAsync(WebBrowser.Operator);

        await browser.Open.ClickAsync();
        await Expect(browser.Position).ToHaveTextAsync("Opening");
        var moving = await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");
        moving.LeaseSecondsRemaining.Should().BeNull("the documented installation runs without an operator lease, so only Stop ends this move");

        // The phone loses the network: the live connection drops and nothing reaches the web UI.
        await browser.CutConnectionAsync();
        await browser.Context.SetOfflineAsync(true);
        await browser.ExpectReconnectDialogAsync();
        var dialogStop = await browser.PlacementOfAsync(browser.DialogStop);
        dialogStop.Reachable.Should().BeTrue("the dialog's Stop must be usable on the phone: {0}", dialogStop);

        await browser.TapAsync(dialogStop);
        await Expect(browser.DialogStopResult).ToHaveAttributeAsync("data-state", "failed");
        await Expect(browser.DialogStopResult).ToHaveTextAsync(RoofStopText.Failed(WebStopTexts.Unreachable));
        rig.Controller.GetCurrentStatusSnapshot().IsMoving.Should().BeTrue("Stop did not reach the web UI");

        // The network is back, but the live connection is not yet: the dialog's Stop reaches the controller on its own.
        // The answer made the dialog taller, so its Stop has moved.
        await browser.Context.SetOfflineAsync(false);
        await browser.TapAsync(await browser.PlacementOfAsync(browser.DialogStop));
        await Expect(browser.DialogStopResult).ToHaveAttributeAsync("data-state", "ok");
        await Expect(browser.DialogStopResult).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);
        await Expect(browser.StopOutcome).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);

        var stopped = await rig.WaitForRestAsync("the reconnect dialog's Stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.ShouldBeDeenergized(rig);
        rig.Plant.OpenLimitActuated.Should().BeFalse("the dialog's Stop ended the move before the open limit");
        rig.Session.Plant.Violations.Should().BeEmpty();
        browser.WebLogs.Entries.Should().Contain(e => e.Message.StartsWith(StopFrom, StringComparison.Ordinal), "the web UI took Stop without the live connection");
        await Expect(browser.ReconnectDialog).ToHaveClassAsync(new Regex(@"\bcomponents-reconnect-show\b"));

        browser.RestoreConnection();
        await browser.ExpectReconnectedAsync();
        await Expect(browser.Position).ToHaveTextAsync(RoofText.DescribePosition(stopped.Status));
        await Expect(browser.Commanded).ToHaveTextAsync("None");
    }

    /// <summary>
    /// Not a commissioning step: an admin ending the person's controller session does not take Stop away from their
    /// page. The controller refuses the session, and takes Stop on the web UI's Stop key on the person's behalf.
    /// </summary>
    [TestMethod]
    public async Task WhenTheControllerSessionHasEnded_TheDialogsStop_IsStillSent_WithTheWebUIsStopKey()
    {
        var browser = _browser = await StartAsync();
        var rig = browser.Rig;
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await browser.SignInAsync(WebBrowser.Operator);

        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        using (var admin = browser.AdminClient())
        {
            var sessions = await admin.Identity.GetSessionsAsync();
            foreach (var session in sessions.Where(s => s.Name == WebBrowser.Operator).ToList())
            {
                await admin.Identity.EndSessionAsync(session.Id);
            }
        }

        await client.AcceptedAsync("Open");
        await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");

        await browser.TapAsync(await browser.PlacementOfAsync(browser.DialogStop));
        await Expect(browser.DialogStopResult).ToHaveAttributeAsync("data-state", "ok");
        await Expect(browser.DialogStopResult).ToHaveTextAsync(RoofStopText.AcknowledgedVerified);

        var stopped = await rig.WaitForRestAsync("the reconnect dialog's Stop, on the Stop key");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.ShouldBeDeenergized(rig);
        browser.WebLogs.Entries.Should().Contain(e => e.Message == $"Web stop for {WebBrowser.Operator}: {RoofStopOutcome.Acknowledged}");
    }

    /// <summary>
    /// Not a commissioning step: a page whose sign-in was cleared must not be able to send Stop, and the dialog must say
    /// Stop was not sent rather than that the web UI is unreachable.
    /// </summary>
    [TestMethod]
    public async Task WhenThePageHasBeenSignedOut_TheDialogsStop_IsNotSent_AndSaysToReloadOrUseTheRoofStop()
    {
        var browser = _browser = await StartAsync();
        var rig = browser.Rig;
        using var client = rig.CreateApiClient(TestApiKeys.Operator);
        await browser.SignInAsync(WebBrowser.Operator);

        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        await browser.Context.ClearCookiesAsync();
        await client.AcceptedAsync("Open");
        await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");

        await browser.TapAsync(await browser.PlacementOfAsync(browser.DialogStop));
        await Expect(browser.DialogStopResult).ToHaveAttributeAsync("data-state", "failed");
        await Expect(browser.DialogStopResult).ToHaveTextAsync(RoofStopText.PageSignedOut);
        rig.Controller.GetCurrentStatusSnapshot().IsMoving.Should().BeTrue("Stop from a signed-out page is refused");
        await browser.WaitForWebLogAsync("Web stop refused for a signed-out page from 127.0.0.1: missing or stale antiforgery token");
        browser.WebLogs.Entries.Should().NotContain(e => e.Message.StartsWith(StopFrom, StringComparison.Ordinal));

        await client.AcceptedAsync("Stop");
        (await rig.WaitForRestAsync("the API's Stop")).ShouldBeDeenergized(rig);
    }

    /// <summary>
    /// Not a commissioning step: a dialog whose texts cannot be read still sends Stop. The browser posts the form itself
    /// and shows the web UI's answer as the page.
    /// </summary>
    [TestMethod]
    public async Task WithoutItsTexts_TheDialogsStop_IsStillSent()
    {
        var browser = _browser = await StartAsync();
        var rig = browser.Rig;
        await browser.SignInAsync(WebBrowser.Operator);
        await browser.Open.ClickAsync();
        await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");

        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        // The answer opens in a new tab: Playwright's WebSocket routes fail when the routed page itself navigates away.
        await browser.Page.EvalOnSelectorAsync(
            "form.reconnect-stop",
            "form => { form.removeAttribute('data-web-stop-texts'); form.target = '_blank'; }");
        var answer = await browser.Page.RunAndWaitForPopupAsync(() => browser.DialogStop.ClickAsync());

        var stopped = await rig.WaitForRestAsync("the reconnect dialog's Stop, posted by the browser");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
        stopped.ShouldBeDeenergized(rig);
        browser.WebLogs.Entries.Should().Contain(e => e.Message.StartsWith(StopFrom, StringComparison.Ordinal));
        await Expect(answer.Locator("body")).ToContainTextAsync(RoofStopText.AcknowledgedVerified);
    }

    /// <summary>
    /// Not a commissioning step: an answer that is not the Stop endpoint's own (a proxy's error page, the origin check, a
    /// status with no wording) and an answer that never comes are described as every other client describes them.
    /// </summary>
    [TestMethod]
    public async Task AnAnswerThatIsNotTheWebUIs_OrNoAnswer_IsDescribedInTheSharedWording()
    {
        var browser = _browser = await StartAsync();
        await browser.SignInAsync(WebBrowser.Operator);
        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        var stopPath = "**" + WebAuthentication.StopPostPath;

        async Task ExpectAnswerAsync(int status, string contentType, string body, string text)
        {
            await browser.Page.UnrouteAsync(stopPath);
            await browser.Page.RouteAsync(stopPath, route => route.FulfillAsync(new() { Status = status, ContentType = contentType, Body = body }));
            await browser.DialogStop.ClickAsync();
            await Expect(browser.DialogStopResult).ToHaveAttributeAsync("data-state", "failed");
            await Expect(browser.DialogStopResult).ToHaveTextAsync(text);
        }

        await ExpectAnswerAsync(502, "text/html", "<html><body>Bad gateway</body></html>",
            RoofStopText.Failed(RoofText.DescribeRefusal(500, null, null)));
        await ExpectAnswerAsync(503, "text/html", "<html><body>Service unavailable</body></html>",
            RoofStopText.Failed(RoofText.DescribeRefusal(503, null, null)));
        await ExpectAnswerAsync(401, "text/html", "<html><body>Unauthorized</body></html>", RoofStopText.PageSignedOut);
        await ExpectAnswerAsync(403, "application/problem+json", $$"""{ "status": 403, "code": "{{OriginCheck.ProblemCode}}" }""",
            RoofStopText.Failed(WebStopTexts.OriginRefused));
        await ExpectAnswerAsync(403, "application/problem+json", """{ "status": 403, "code": "constructor" }""",
            RoofStopText.Failed(RoofText.DescribeRefusal(403, null, null)));
        await ExpectAnswerAsync(418, "text/plain", "teapot", RoofStopText.Failed(RoofText.DescribeRefusal(418, null, null)));

        // The answer never comes: the page gives up after its timeout, cut here from 25 s to 2 s.
        await browser.Page.EvalOnSelectorAsync(
            "form.reconnect-stop",
            "form => { form.dataset.webStopTexts = JSON.stringify({ ...JSON.parse(form.dataset.webStopTexts), timeoutMilliseconds: 2000 }); }");
        await browser.Page.UnrouteAsync(stopPath);
        await browser.Page.RouteAsync(stopPath, _ => Task.CompletedTask);
        await browser.DialogStop.ClickAsync();
        await Expect(browser.DialogStopResult).ToHaveTextAsync(RoofStopText.Sending);
        await Expect(browser.DialogStopResult).ToHaveTextAsync(RoofStopText.Failed(WebStopTexts.TimedOut));
        await Expect(browser.DialogStopResult).ToHaveAttributeAsync("data-state", "failed");
        browser.Rig.Controller.GetCurrentStatusSnapshot().LastStopReason.Should().NotBe(
            RoofControllerStopReason.NormalStop, "no Stop reached the controller: every answer came from the test's route");
        browser.WebLogs.Entries.Should().NotContain(e => e.Message.StartsWith(StopFrom, StringComparison.Ordinal));
    }

    private Task<WebBrowser> StartAsync()
        => WebBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0), WebDevices.Phone, cuttableConnection: true);
}
