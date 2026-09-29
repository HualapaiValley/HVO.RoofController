using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web.Roof;
using static Microsoft.Playwright.Assertions;

namespace HVO.RoofControllerV4.RPi.Tests.Browser;

/// <summary>
/// C9 step 4, client loss, from the web UI on a phone: the roof page renews the operator lease only while its live
/// connection is up, so a page that is closed, or loses its connection, during a move lets the lease run out and the
/// roof stops with <see cref="RoofControllerStopReason.OperatorLeaseExpired"/>. The documented 2 m roof (about 21 s from
/// limit to limit) is still moving when the lease runs out.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.BrowserCategory)]
public sealed class C9WebLeaseBrowserTests
{
    private const string LeaseLogMessage =
        "Web page connection for " + WebBrowser.Operator + " lost while it held the operator lease; renewal stopped so the lease can expire";

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
    [CommissioningCheck("C9", "4")]
    public async Task TheWebPageRenewsTheLeaseDuringAMove_AndClosingIt_LetsTheLeaseRunOut_AndStopsTheRoof()
    {
        const double lease = 5;
        var browser = _browser = await WebBrowser.StartAsync(TestContext, Scenario.Production(travelMeters: 2.0), WebDevices.Phone);
        var rig = browser.Rig;
        await rig.ConfigureAsync(r => r with { OperatorLeaseTimeoutSeconds = lease });
        await browser.SignInAsync(WebBrowser.Operator);

        await browser.Open.ClickAsync();
        await Expect(browser.Position).ToHaveTextAsync("Opening");
        var moving = await rig.WaitForControllerAsync(s => s.IsMoving, "the roof to open");
        moving.LeaseSecondsRemaining.Should().BeInRange(lease - 1, lease);
        await Expect(browser.Lease).ToHaveTextAsync(new Regex(@"^Renewing lease \(\d+ s left\)$"));

        // Longer than the lease: only the page's renewals keep the roof moving.
        await Task.Delay(TimeSpan.FromSeconds(lease + 2));
        var renewed = rig.Controller.GetCurrentStatusSnapshot();
        renewed.IsMoving.Should().BeTrue("the page renews the lease while it is connected");
        renewed.LeaseSecondsRemaining.Should().BeGreaterThan(lease / 2, "the page renews a third of the way into the lease");

        var clock = Stopwatch.StartNew();
        await browser.Page.CloseAsync();

        await rig.WaitForControllerAsync(s => !s.IsMoving, "the lease to run out");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(lease + 1), "the roof stops within the lease of the page closing");
        var stopped = await rig.WaitForRestAsync("the lease to run out");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
        stopped.ShouldBeDeenergized(rig);
        rig.Plant.OpenLimitActuated.Should().BeFalse("the lease, not the open limit, ended the move");
        rig.Session.Plant.Violations.Should().BeEmpty();
        browser.WebLogs.Entries.Should().Contain(entry => entry.Message.StartsWith("Web page", StringComparison.Ordinal)
            && entry.Message.EndsWith("while it held the operator lease; renewal stopped so the lease can expire", StringComparison.Ordinal));
    }

    [TestMethod]
    [CommissioningCheck("C9", "4")]
    public async Task WhenTheWebPageLosesItsConnection_TheLeaseRunsOut_AndAfterAReconnect_RenewalDoesNotResume_AndThePageWarns()
    {
        const double lease = 15;
        var browser = _browser = await WebBrowser.StartAsync(
            TestContext, Scenario.Production(travelMeters: 2.0), WebDevices.Phone, cuttableConnection: true);
        var rig = browser.Rig;
        await rig.ConfigureAsync(r => r with { OperatorLeaseTimeoutSeconds = lease });
        await browser.SignInAsync(WebBrowser.Operator);

        await browser.Open.ClickAsync();
        await Expect(browser.Position).ToHaveTextAsync("Opening");
        await rig.WaitForControllerAsync(s => s.IsMoving && s.LeaseSecondsRemaining is not null, "the roof to open under the lease");
        await Expect(browser.Lease).ToBeVisibleAsync();

        await browser.CutConnectionAsync();
        await browser.ExpectReconnectDialogAsync();
        await browser.WaitForWebLogAsync(LeaseLogMessage);

        // The page has let go of the lease, so nothing renews it from here: it runs out from what is left now.
        var clock = Stopwatch.StartNew();
        var leaseAtLoss = rig.Controller.GetCurrentStatusSnapshot().LeaseSecondsRemaining;
        leaseAtLoss.Should().NotBeNull().And.BeGreaterThan(6, "enough of the lease is left to reconnect inside it");
        browser.Log($"lease at the connection loss: {leaseAtLoss:0.00} s");

        browser.RestoreConnection();
        await browser.ExpectReconnectedAsync();
        rig.Controller.GetCurrentStatusSnapshot().IsMoving.Should().BeTrue("the page reconnected inside the lease");
        await Expect(browser.Notices.First).ToContainTextAsync(WebRoofConsole.LeaseDroppedOnDisconnect);
        await Expect(browser.Notices.First).ToHaveAttributeAsync("data-level", nameof(WebRoofNoticeLevel.Warning));
        await Expect(browser.Lease).ToBeHiddenAsync();

        var leases = new List<double>();
        while (rig.Controller.GetCurrentStatusSnapshot() is { IsMoving: true } status && clock.Elapsed < TimeSpan.FromSeconds(lease + 5))
        {
            if (status.LeaseSecondsRemaining is { } remaining)
            {
                leases.Add(remaining);
            }

            await Task.Delay(100);
        }

        var elapsed = clock.Elapsed.TotalSeconds;
        var stopped = await rig.WaitForRestAsync("the lease to run out");
        browser.Log($"stopped {elapsed:0.00} s after the connection loss; lease samples: {string.Join(", ", leases.Select(l => l.ToString("0.0")))}");
        leases.Should().NotBeEmpty().And.BeInDescendingOrder("renewal does not resume after the reconnect");
        leases.Should().AllSatisfy(l => l.Should().BeLessThanOrEqualTo(leaseAtLoss!.Value));
        elapsed.Should().BeInRange(leaseAtLoss!.Value - 0.5, leaseAtLoss.Value + 1, "the roof stops when the lease left at the connection loss runs out");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.OperatorLeaseExpired);
        stopped.ShouldBeDeenergized(rig);
        rig.Plant.OpenLimitActuated.Should().BeFalse("the lease, not the open limit, ended the move");
        rig.Session.Plant.Violations.Should().BeEmpty();
        await Expect(browser.Position).ToHaveTextAsync(RoofText.DescribePosition(stopped.Status));
    }
}
