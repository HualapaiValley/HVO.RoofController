using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Components.Layout;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Roof;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The roof page and the mode banner, rendered from a live page's roof console: what they show and offer, in the
/// shared words, and that they never show a stale or missing status as the roof's state now.
/// </summary>
[TestClass]
public sealed class DashboardTests
{
    [TestMethod]
    public async Task BeforeTheLiveConnection_ThePageSaysItIsConnecting_AndOffersNothing()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.Console.Dispose(); // never starts: the page as it is prerendered
        await using var context = Context(harness);

        var cut = context.Render<Dashboard>();

        Text(cut.Find("[data-testid=connecting]")).Should().Be("Connecting to the roof's status…");
        Text(cut.Find("[data-testid=feed-state]")).Should().Be("Status: not connected");
        foreach (var button in new[] { "open", "close", "clear-fault", "refresh" })
        {
            cut.Find($"[data-testid={button}]").HasAttribute("disabled").Should().BeTrue(button);
        }

        cut.Find("[data-testid=open]").GetAttribute("title").Should().Be("The page is still connecting");
        Text(cut.Find("[data-testid=blocked]")).Should().BeEmpty();
        Text(cut.Find("[data-testid=no-status]")).Should().Be("No status yet.");
        cut.FindAll("[data-testid=signed-out]").Should().BeEmpty();
    }

    [TestMethod]
    public async Task Live_ShowsTheStatus_AndOffersWhatTheRoofIsNotAlreadyDoing()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        await using var context = Context(harness);

        var cut = context.Render<Dashboard>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=dashboard]").GetAttribute("data-feed").Should().Be("live"));
        var status = harness.Console.View.Status!;
        Text(cut.Find("[data-testid=feed-state]")).Should().Be("Status: live");
        cut.Find("[data-testid=feed-state]").ClassList.Should().Contain("bg-success");
        Text(cut.Find("[data-testid=position]")).Should().Be(RoofText.DescribePosition(RoofControllerStatus.Closed));
        Text(cut.Find("[data-testid=commanded]")).Should().Be("None");
        Text(cut.Find("[data-testid=open-limit]")).Should().Be($"Open limit: {RoofStatusText.DescribeInput(status.IsOpenLimitActive)}");
        Text(cut.Find("[data-testid=closed-limit]")).Should().Be($"Closed limit: {RoofStatusText.DescribeInput(status.IsClosedLimitActive)}");
        cut.FindAll("[data-testid=watchdog]").Should().BeEmpty();
        cut.Find("[data-testid=open]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=open]").GetAttribute("title").Should().Be("Open the roof");
        cut.Find("[data-testid=close]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=close]").GetAttribute("title").Should().Be(RoofCommandRules.Capitalize(RoofCommandRules.AlreadyClosed));
        cut.Find("[data-testid=clear-fault]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=refresh]").HasAttribute("disabled").Should().BeFalse();
        Text(cut.Find("[data-testid=blocked]")).Should().Be($"Close: {RoofCommandRules.AlreadyClosed}.");
        Text(cut.Find("[data-testid=status-time]")).Should().Be($"Status at {RoofStatusText.Time(status.SnapshotUtc)}");
        cut.FindAll("[data-testid=status-rows] .rc2-row").Select(row => $"{Text(row.QuerySelector("dt")!)}: {Text(row.QuerySelector("dd")!)}").ToList()
            .Should().Equal(RoofStatusText.DescribeRows(status).Select(row => $"{row.Label}: {row.Value}"));
        cut.FindAll("[data-testid=connecting], [data-testid=signed-out], [data-testid=feed-banner], [data-testid=fault-banner], [data-testid=relays-banner], [data-testid=inputs-banner], [data-testid=role-hint], [data-testid=lease], [data-testid=notices]")
            .Should().BeEmpty();
    }

    [TestMethod]
    public async Task Open_ShowsTheAnswer_AndTheLeaseThisPageRenews()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.OpensTheRoof();
        await using var context = Context(harness);
        var cut = context.Render<Dashboard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=open]").HasAttribute("disabled").Should().BeFalse());

        // Found and clicked on the renderer's thread, so a status that arrives in between cannot draw the button again.
        await cut.InvokeAsync(() => cut.Find("[data-testid=open]").Click());

        cut.WaitForAssertion(() => Text(cut.Find("[data-testid=lease]")).Should().Be("Renewing lease (6 s left)"));
        var notice = cut.Find("[data-testid=notice]");
        notice.GetAttribute("data-level").Should().Be(nameof(WebRoofNoticeLevel.Info));
        Text(notice).Should().StartWith($"{RoofStatusText.Time(WebRoofHarness.Start)} Open accepted. This page renews the operator lease");
        Text(cut.Find("[data-testid=position]")).Should().Be(RoofText.DescribePosition(RoofControllerStatus.Opening));
        Text(cut.Find("[data-testid=commanded]")).Should().Be("Opening");
        Text(cut.Find("[data-testid=watchdog]")).Should().Be($"Watchdog: {RoofStatusText.Seconds(59.5)} left");
        cut.Find("[data-testid=close]").GetAttribute("title").Should().Be(RoofCommandRules.Capitalize(RoofCommandRules.Moving));
        Text(cut.Find("[data-testid=blocked]")).Should().Be($"Open and Close: {RoofCommandRules.Moving}.");
    }

    [TestMethod]
    public async Task AViewer_IsToldTheRoleIsNeeded_AndThatStopStillWorks()
    {
        using var harness = await WebRoofHarness.CreateAsync(RoofControllerApiContract.ViewerRole);
        await using var context = Context(harness);

        var cut = context.Render<Dashboard>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=dashboard]").GetAttribute("data-feed").Should().Be("live"));
        Text(cut.Find("[data-testid=role-hint]")).Should().Be($"Open, Close and Clear fault need the Operator role. {RoofStopText.AlwaysAvailable}");
        cut.Find("[data-testid=open]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=open]").GetAttribute("title").Should().Be(RoofCommandRules.Capitalize(RoofCommandRules.MotionRoleNeeded));
        Text(cut.Find("[data-testid=blocked]")).Should().Be($"Open and Close: {RoofCommandRules.MotionRoleNeeded}.");
        cut.Find("[data-testid=refresh]").HasAttribute("disabled").Should().BeFalse("a viewer may read the status");
    }

    [TestMethod]
    [DataRow(false, WebStopTexts.MayBeRefused)]
    [DataRow(true, WebStopTexts.StillWorks)]
    public async Task SignedOut_ThePageSaysSo_AndOffersSignInAgain(bool withStopKey, string stop)
    {
        using var harness = await WebRoofHarness.CreateAsync(role: null, stopKey: withStopKey ? WebTestSupport.StopKey() : null);
        await using var context = Context(harness);

        var cut = context.Render<Dashboard>();

        cut.WaitForAssertion(() => Text(cut.Find("[data-testid=signed-out]"))
            .Should().Be($"You are signed out, so this page shows no status. Sign in again. {stop}"));
        cut.Find("[data-testid=sign-in-again]").GetAttribute("href").Should().Be("signin");
        Text(cut.Find("[data-testid=feed-state]")).Should().Be("Status: not connected");
        cut.Find("[data-testid=open]").GetAttribute("title").Should().Be("You are signed out");
        cut.Find("[data-testid=refresh]").HasAttribute("disabled").Should().BeTrue();
        Text(cut.Find("[data-testid=blocked]")).Should().Be($"Open and Close: {WebRoofConsole.NoSession}.");
        cut.FindAll("[data-testid=connecting], [data-testid=feed-banner]").Should().BeEmpty();
    }

    [TestMethod]
    public async Task AStaleStatus_IsShownAsTheLastKnownState_AndOffersNoMotion()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        await using var context = Context(harness);
        var cut = context.Render<Dashboard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=dashboard]").GetAttribute("data-feed").Should().Be("live"));

        harness.Clock.Advance(ClientTestSupport.FastFeed.StaleAfter);

        cut.WaitForAssertion(() => cut.Find("[data-testid=dashboard]").GetAttribute("data-feed").Should().Be("STALE"));
        var since = RoofStatusText.Time(WebRoofHarness.Start + ClientTestSupport.FastFeed.StaleAfter);
        var banner = cut.Find("[data-testid=feed-banner]");
        Text(banner).Should().Be($"STALE: no status since {since}. Showing the last known state; Stop still works.");
        banner.ClassList.Should().Contain("rc2-banner-warn");
        cut.Find("[data-testid=feed-state]").ClassList.Should().Contain("bg-danger");
        cut.Find(".rc2-state").ClassList.Should().Contain("rc2-state--stale");
        cut.FindAll(".rc2-state-label").Select(Text).ToList().Should().Equal("Position (last known)", "Commanded (last known)");
        Text(cut.Find("[data-testid=status-time]")).Should()
            .Be($"Last known state, as of {RoofStatusText.Time(harness.Console.View.Status!.SnapshotUtc)}. It may not be the roof's state now.");
        cut.Find("[data-testid=open]").GetAttribute("title").Should().Be(RoofCommandRules.Capitalize(RoofCommandRules.Stale));
    }

    [TestMethod]
    [DataRow(RoofControllerApiContract.OperatorRole, "")]
    [DataRow(RoofControllerApiContract.ViewerRole, " An operator must clear it.")]
    public async Task AFault_IsShownAboveTheControls_AndClearFaultIsOffered(string role, string whoClears)
    {
        using var harness = await WebRoofHarness.CreateAsync(role);
        harness.Report(RoofServiceMock.Snapshot(faultLatched: true) with { LatchedFaultReason = RoofControllerStopReason.DriveFault });
        await using var context = Context(harness);

        var cut = context.Render<Dashboard>();

        cut.WaitForAssertion(() => Text(cut.Find("[data-testid=fault-banner]")).Should().Be(
            $"Fault: {RoofStatusText.DescribeFault(harness.Current)}. Open and Close are refused until the cause is fixed and the fault is cleared.{whoClears}"));
        cut.Find("[data-testid=clear-fault]").HasAttribute("disabled").Should().Be(role == RoofControllerApiContract.ViewerRole);
    }

    [TestMethod]
    public async Task RelaysAndInputs_ThatCannotBeTrusted_AreShownAboveTheControls()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.Report(RoofServiceMock.Snapshot(relayState: RoofRelayRegisterState.Unverified) with { InputsHealthy = false, ConsecutiveInputReadFailures = 2 });
        await using var context = Context(harness);
        var cut = context.Render<Dashboard>();

        cut.WaitForAssertion(() => Text(cut.Find("[data-testid=relays-banner]")).Should().Be("Relays: UNVERIFIED: confirm at the roof that the motor has stopped."));
        Text(cut.Find("[data-testid=inputs-banner]")).Should().Be("Safety inputs: reads failing (2 in a row). Motion may be refused.");
        var status = harness.Console.View.Status!;
        Text(cut.Find("[data-testid=open-limit]")).Should().Be($"Open limit (last read): {RoofStatusText.DescribeInput(status.IsOpenLimitActive)}");
        Text(cut.Find("[data-testid=closed-limit]")).Should().Be($"Closed limit (last read): {RoofStatusText.DescribeInput(status.IsClosedLimitActive)}");

        harness.Push(RoofServiceMock.Snapshot() with { RelayRegisterReadsHealthy = false });

        cut.WaitForAssertion(() => Text(cut.Find("[data-testid=relays-banner]")).Should().Be("Relays: register read-back matched; register reads failing."));
        cut.FindAll("[data-testid=inputs-banner]").Should().BeEmpty();
        Text(cut.Find("[data-testid=open-limit]")).Should().StartWith("Open limit: ");
    }

    [TestMethod]
    public async Task TheModeBanner_WarnsWhileTheRoofDoesNotMove_OrTheLimitsAreIgnored()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        harness.Report(RoofServiceMock.Snapshot() with { HatMode = RoofHatMode.Simulation, IsIgnoringPhysicalLimitSwitches = true });
        await using var context = Context(harness);

        var cut = context.Render<ModeBanner>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mode-banner] p").Select(Text).ToList()
            .Should().Equal(RoofStatusText.Simulation, RoofStatusText.LimitsIgnored));

        harness.Push(RoofServiceMock.Snapshot() with { HatMode = RoofHatMode.Physical });

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=mode-banner]").Should().BeEmpty());
    }

    [TestMethod]
    public async Task TheRoofCamera_IsShownBesideTheControls()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        await using var context = Context(harness);

        var cut = context.Render<Dashboard>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=dashboard]").GetAttribute("data-feed").Should().Be("live"));
        Text(cut.Find("#rc2-camera-title")).Should().Be("Roof camera");
        cut.FindAll("[data-testid=cameras] [data-testid=camera]").Select(camera => camera.GetAttribute("data-camera")).ToList()
            .Should().Equal("2");
        cut.Find("[data-testid=cameras] .player-frame").GetAttribute("aria-label").Should().Be("Roof camera stream");
        cut.FindAll("[data-testid=camera-failed]").Should().BeEmpty();
    }

    [TestMethod]
    public async Task TheCamerasInTheSettings_AreShownEachUnderItsNumber()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        await using var context = Context(harness, new RoofWebOptions { CameraIds = [3, 5] });

        var cut = context.Render<Dashboard>();

        Text(cut.Find("#rc2-camera-title")).Should().Be("Cameras");
        cut.FindAll("[data-testid=cameras] [data-testid=camera]").Select(camera => camera.GetAttribute("data-camera")).ToList()
            .Should().Equal("3", "5");
        cut.FindAll("[data-testid=cameras] .player-frame").Select(frame => frame.GetAttribute("aria-label")).ToList()
            .Should().Equal("Camera 3 stream", "Camera 5 stream");
    }

    [TestMethod]
    public async Task ACameraViewThatFails_LeavesTheRoofControls_AndCanBeRetried()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        await using var context = Context(harness, js: false);
        var module = new Mock<IJSObjectReference>();
        module.Setup(m => m.InvokeAsync<IJSObjectReference>("createPlayer", It.IsAny<object?[]?>()))
            .ReturnsAsync(new Mock<IJSObjectReference>().Object);
        var js = new Mock<IJSRuntime>();
        js.SetupSequence(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()))
            .Returns(ValueTask.FromException<IJSObjectReference>(new InvalidOperationException("the player broke")))
            .ReturnsAsync(module.Object);
        context.Services.AddSingleton(js.Object);

        var cut = context.Render<Dashboard>();

        cut.WaitForAssertion(() => Text(cut.Find("[data-testid=camera-failed]")).Should().StartWith("The camera view failed. The roof controls are unaffected."));
        cut.FindAll("[data-testid=cameras] [data-testid=camera]").Should().BeEmpty();
        cut.WaitForAssertion(() => cut.Find("[data-testid=dashboard]").GetAttribute("data-feed").Should().Be("live"));
        cut.Find("[data-testid=open]").HasAttribute("disabled").Should().BeFalse("the roof controls are unaffected");

        cut.Find("[data-testid=camera-retry]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=cameras] button[title=Pause]").HasAttribute("disabled").Should().BeFalse());
        cut.FindAll("[data-testid=camera-failed]").Should().BeEmpty();
        js.Verify(j => j.InvokeAsync<IJSObjectReference>("import", It.IsAny<object?[]?>()), Times.Exactly(2));
    }

    // The text as it reads: whitespace from the markup collapsed.
    private static string Text(IElement element) => Regex.Replace(element.TextContent, @"\s+", " ").Trim();

    private static BunitContext Context(WebRoofHarness harness, RoofWebOptions? options = null, bool js = true)
    {
        var context = new BunitContext();
        context.Services.AddSingleton(harness.Console);
        context.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        context.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        context.Services.AddSingleton(Options.Create(options ?? new RoofWebOptions()));
        if (js)
        {
            // The camera's player starts in the page's browser.
            WebCameraStreamTests.SetUpThePlayer(context, out _, out _);
        }

        return context;
    }
}
