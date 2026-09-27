using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using static HVO.RoofControllerV4.RPi.Tests.Components.RoofConsoleHarness;

namespace HVO.RoofControllerV4.RPi.Tests.Components;

[TestClass]
public class RoofControlV2Tests
{
    private const string StopButton = "[data-testid=stop]";
    private const string OpenButton = "[data-testid=open]";
    private const string CloseButton = "[data-testid=close]";
    private const string ClearFaultButton = "[data-testid=clear-fault]";
    private const string StopOutcome = "[data-testid=stop-outcome]";
    private const string Position = "[data-testid=position]";

    [TestMethod]
    public async Task Stop_IsEnabled_ForViewer_WhileRoofIsMoving()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Opening, 1)).SignInAsViewer();

        var cut = harness.Render();

        cut.Find(StopButton).HasAttribute("disabled").Should().BeFalse();
        cut.Find(Position).TextContent.Should().Be("Opening");
        cut.Find("[data-testid=commanded]").TextContent.Should().Contain("Opening");
    }

    [TestMethod]
    public async Task OpenAndClose_AreDisabled_ForViewer_WithExplanation()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.PartiallyOpen, 1)).SignInAsViewer();

        var cut = harness.Render();

        cut.Find(OpenButton).HasAttribute("disabled").Should().BeTrue();
        cut.Find(CloseButton).HasAttribute("disabled").Should().BeTrue();
        cut.Find(OpenButton).GetAttribute("title").Should().Contain("Operator role");
        cut.Find("[data-testid=role-hint]").TextContent.Should().Contain("Viewer").And.Contain("Stop is always available");
    }

    [TestMethod]
    public async Task OpenAndClose_AreEnabled_ForOperator_WhenIdle()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.PartiallyOpen, 1)).SignInAsOperator();

        var cut = harness.Render();

        cut.Find(OpenButton).HasAttribute("disabled").Should().BeFalse();
        cut.Find(CloseButton).HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("[data-testid=role-hint]").Should().BeEmpty();
    }

    [TestMethod]
    public async Task Open_RechecksRole_WhenClicked()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.PartiallyOpen, 1)).SignInAsOperator();
        var cut = harness.Render();

        harness.Authorization.SetPolicies(RoofControllerSecurityDefaults.ViewerPolicy, RoofControllerSecurityDefaults.StopPolicy);
        cut.Find(OpenButton).HasAttribute("disabled").Should().BeFalse("the page has not re-rendered since the role was revoked");
        await cut.Find(OpenButton).ClickAsync(new MouseEventArgs());

        harness.Roof.Verify(r => r.Open(), Times.Never);
        harness.Footer.Snapshot.LeftNotifications.Should().Contain(n => n.Title == "Open not sent");
    }

    [TestMethod]
    public async Task Stop_SendsStop_ForViewer_AndReportsVerifiedAcknowledgement()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Opening, 1)).SignInAsViewer();
        harness.Roof.Setup(r => r.Stop(RoofControllerStopReason.NormalStop))
            .Returns(() =>
            {
                harness.Current = Status(RoofControllerStatus.PartiallyOpen, 2);
                return Result<RoofControllerStatus>.Success(RoofControllerStatus.PartiallyOpen);
            });
        var cut = harness.Render();

        await cut.Find(StopButton).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            var outcome = cut.Find(StopOutcome);
            outcome.ClassList.Should().Contain("rc2-stop-outcome--ok");
            outcome.TextContent.Should().Contain("Relay register verified de-energized");
        });
        harness.Roof.Verify(r => r.Stop(RoofControllerStopReason.NormalStop), Times.Once);
        cut.Find(Position).TextContent.Should().Be("Partially open");
    }

    [TestMethod]
    public async Task Stop_ReportsRelayUnverified_WhenRegisterCannotBeReadBack()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Closing, 1)).SignInAsOperator();
        harness.Roof.Setup(r => r.Stop(RoofControllerStopReason.NormalStop))
            .Returns(Result<RoofControllerStatus>.Failure(new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "read-back failed")));
        var cut = harness.Render();

        await cut.Find(StopButton).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            var outcome = cut.Find(StopOutcome);
            outcome.ClassList.Should().Contain("rc2-stop-outcome--warn");
            outcome.TextContent.Should().Contain("could not be verified").And.Contain("RelayStateUnverified");
        });
    }

    [TestMethod]
    public async Task Stop_ReportsFailureWithCode_WhenControllerRefuses()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Opening, 1)).SignInAsViewer();
        harness.Roof.Setup(r => r.Stop(RoofControllerStopReason.NormalStop))
            .Returns(Result<RoofControllerStatus>.Failure(new RoofControllerException(RoofControllerErrorCode.HardwareUnavailable, "hat offline")));
        var cut = harness.Render();

        await cut.Find(StopButton).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            var outcome = cut.Find(StopOutcome);
            outcome.ClassList.Should().Contain("rc2-stop-outcome--failed");
            outcome.TextContent.Should().Contain("Stop failed").And.Contain("HardwareUnavailable");
        });
    }

    [TestMethod]
    public async Task Stop_IsNotQueuedBehind_ASlowOpenCommand()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.PartiallyOpen, 1)).SignInAsOperator();
        using var openGate = new ManualResetEventSlim(false);
        harness.Roof.Setup(r => r.Open()).Returns(() =>
        {
            openGate.Wait(TimeSpan.FromSeconds(10));
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        harness.Roof.Setup(r => r.Stop(RoofControllerStopReason.NormalStop))
            .Returns(Result<RoofControllerStatus>.Success(RoofControllerStatus.PartiallyOpen));
        var cut = harness.Render();

        var openTask = cut.Find(OpenButton).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find(OpenButton).GetAttribute("title").Should().Be("A command is in progress"));
        cut.Find(StopButton).HasAttribute("disabled").Should().BeFalse();

        await cut.Find(StopButton).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => harness.Roof.Verify(r => r.Stop(RoofControllerStopReason.NormalStop), Times.Once));
        openTask.IsCompleted.Should().BeFalse("Open is still blocked in the controller");

        openGate.Set();
        await openTask;
    }

    [TestMethod]
    public async Task Stop_IsNotSent_WhenSignedOut()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Opening, 1));
        harness.Authorization.SetNotAuthorized();
        var cut = harness.Render();

        cut.Find(StopButton).HasAttribute("disabled").Should().BeFalse();
        await cut.Find(StopButton).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(StopOutcome).TextContent.Should().Contain("signed out"));
        harness.Roof.Verify(r => r.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never);
    }

    [TestMethod]
    public async Task FaultBanner_UsesDangerAlertClasses_AndBlocksMotion()
    {
        var faulted = Status(RoofControllerStatus.Stopped, 1) with
        {
            LastStopReason = RoofControllerStopReason.DriveFault,
            IsFaultLatched = true,
            LatchedFaultReason = RoofControllerStopReason.DriveFault
        };
        await using var harness = new RoofConsoleHarness(faulted).SignInAsOperator();

        var cut = harness.Render();

        var banner = cut.Find(".rc2-alert-danger");
        banner.ClassList.Should().Contain(new[] { "alert", "alert-danger" });
        banner.GetAttribute("role").Should().Be("alert");
        banner.TextContent.Should().Contain("Fault latched:").And.Contain("Drive fault input active");
        cut.Find(OpenButton).GetAttribute("title").Should().StartWith("A fault is active");
        cut.Find(ClearFaultButton).HasAttribute("disabled").Should().BeFalse();
        cut.Find(StopButton).HasAttribute("disabled").Should().BeFalse();
    }

    [TestMethod]
    public async Task ClearFault_GuardsInFlightPulse_AndDoesNotRaiseSafetyAlertWhenFaultClears()
    {
        var faulted = Status(RoofControllerStatus.Stopped, 1) with
        {
            LastStopReason = RoofControllerStopReason.DriveFault,
            IsFaultLatched = true,
            LatchedFaultReason = RoofControllerStopReason.DriveFault
        };
        await using var harness = new RoofConsoleHarness(faulted).SignInAsOperator();
        var pulse = new TaskCompletionSource<Result<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Roof.Setup(r => r.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(pulse.Task);
        var cut = harness.Render();

        var clickTask = cut.Find(ClearFaultButton).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            cut.Find(ClearFaultButton).HasAttribute("disabled").Should().BeTrue();
            cut.Find(ClearFaultButton).GetAttribute("title").Should().Be("A clear-fault pulse is in progress");
            cut.Find(OpenButton).HasAttribute("disabled").Should().BeTrue();
        });
        harness.Roof.Verify(r => r.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);

        harness.Current = faulted with { StatusVersion = 2, IsFaultLatched = false, LatchedFaultReason = null, Status = RoofControllerStatus.PartiallyOpen };
        pulse.SetResult(Result<bool>.Success(true));
        await clickTask;

        cut.WaitForAssertion(() => cut.FindAll(".rc2-alert-danger").Should().BeEmpty());
        var notifications = harness.Footer.Snapshot.LeftNotifications;
        notifications.Should().Contain(n => n.Title == "Clear fault" && n.Message == "Clear-fault pulse sent");
        notifications.Should().NotContain(n => n.Title == "Fault latched" || n.Title == "Safety stop");
    }

    [TestMethod]
    public async Task SafetyAlert_IsRaisedOnce_WhenFaultLatches()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Opening, 1)).SignInAsViewer();
        var cut = harness.Render();
        var latched = Status(RoofControllerStatus.Stopped, 2) with
        {
            LastStopReason = RoofControllerStopReason.SafetyWatchdogTimeout,
            IsFaultLatched = true,
            LatchedFaultReason = RoofControllerStopReason.SafetyWatchdogTimeout
        };

        await cut.InvokeAsync(() => harness.RaiseStatus(latched));
        await cut.InvokeAsync(() => harness.RaiseStatus(latched with { StatusVersion = 3 }));
        await cut.InvokeAsync(() => { });

        cut.WaitForAssertion(() => cut.Find(".rc2-alert-danger").TextContent.Should().Contain("Safety watchdog timed out"));
        harness.Footer.Snapshot.LeftNotifications.Count(n => n.Title == "Fault latched").Should().Be(1);
    }

    [TestMethod]
    public async Task StaleStatusVersion_IsIgnored()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Open, 5)).SignInAsViewer();
        var cut = harness.Render();
        cut.Find(Position).TextContent.Should().Be("Open");

        harness.RaiseStatus(Status(RoofControllerStatus.Closed, 4), updateCurrent: false);
        await cut.InvokeAsync(() => { });

        cut.Find(Position).TextContent.Should().Be("Open");

        harness.RaiseStatus(Status(RoofControllerStatus.Closing, 6));

        cut.WaitForAssertion(() => cut.Find(Position).TextContent.Should().Be("Closing"));
    }

    [TestMethod]
    public async Task StatusChanged_FromBackgroundThread_IsMarshalledToRenderer()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Closed, 1)).SignInAsViewer();
        var cut = harness.Render();

        await Task.Run(() => harness.RaiseStatus(Status(RoofControllerStatus.Opening, 2)));

        cut.WaitForAssertion(() =>
        {
            cut.Find(Position).TextContent.Should().Be("Opening");
            cut.Find("[data-testid=commanded]").TextContent.Should().Contain("Opening");
        });
    }

    [TestMethod]
    public async Task Dispose_UnsubscribesFromStatusChanges()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Closed, 1)).SignInAsViewer();
        harness.Render();

        await harness.Context.DisposeComponentsAsync();

        harness.Roof.VerifyAdd(r => r.StatusChanged += It.IsAny<EventHandler<RoofStatusChangedEventArgs>>(), Times.Once);
        harness.Roof.VerifyRemove(r => r.StatusChanged -= It.IsAny<EventHandler<RoofStatusChangedEventArgs>>(), Times.Once);
    }

    [TestMethod]
    public async Task HealthDialog_ShowsUnhealthyDetails_WithoutExceptionTextForNonAdmins()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Closed, 1)).SignInAsViewer();
        SetupHealth(harness, UnhealthyReport());
        var cut = harness.Render();

        await cut.Find("button.rc2-badge-button").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=health-check]").Should().HaveCount(2));
        var first = cut.FindAll("[data-testid=health-check]")[0];
        first.TextContent.Should().Contain("Unhealthy").And.Contain("roof-controller").And.Contain("Relay register unverified");
        first.TextContent.Should().Contain("consecutiveFailures");
        first.TextContent.Should().NotContain("secret detail").And.Contain("Administrators can view the details");
        cut.Find(".rc2-modal-title").TextContent.Should().Contain("Unhealthy");
    }

    [TestMethod]
    public async Task HealthDialog_ShowsExceptionText_ForAdmins()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Closed, 1)).SignInAsAdmin();
        SetupHealth(harness, UnhealthyReport());
        var cut = harness.Render();

        await cut.Find("button.rc2-badge-button").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=health-check]")[0].TextContent.Should().Contain("secret detail"));
    }

    [TestMethod]
    public async Task HealthDialog_ClearsPreviousReport_WhenRefreshFails()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.Closed, 1)).SignInAsViewer();
        harness.Health
            .SetupSequence(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UnhealthyReport())
            .ThrowsAsync(new InvalidOperationException("checks crashed"));
        var cut = harness.Render();

        await cut.Find("button.rc2-badge-button").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=health-check]").Should().HaveCount(2));

        await cut.Find(".rc2-modal-footer .btn-primary").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid=health-check]").Should().BeEmpty();
            cut.Find(".rc2-modal-body .alert-warning").TextContent.Should().Contain("Unable to run the health checks");
        });
        cut.Markup.Should().NotContain("checks crashed");
    }

    [TestMethod]
    public async Task ConsoleOutput_IsOnlyOffered_ToAdmins()
    {
        await using var viewer = new RoofConsoleHarness(Status(RoofControllerStatus.Closed, 1)).SignInAsOperator();
        var viewerCut = viewer.Render();
        viewerCut.FindAll(".rc2-console-toggle").Should().BeEmpty();
        viewerCut.Markup.Should().Contain("Console output is available to administrators.");

        await using var admin = new RoofConsoleHarness(Status(RoofControllerStatus.Closed, 1)).SignInAsAdmin();
        var adminCut = admin.Render();
        adminCut.WaitForAssertion(() => adminCut.FindAll(".rc2-console-toggle").Should().ContainSingle());
    }

    [TestMethod]
    public async Task Lease_IsRenewed_WhileTheConnectionIsUp()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.PartiallyOpen, 1)).SignInAsOperator();
        var renewals = SetupLeasedOpen(harness);
        var cut = harness.Render();

        await cut.Find(OpenButton).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Volatile.Read(ref renewals.Count).Should().BeGreaterThan(1), TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Lease_StopsRenewing_WhenTheConnectionDrops_AndDoesNotResumeOnReconnect()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.PartiallyOpen, 1)).SignInAsOperator();
        var renewals = SetupLeasedOpen(harness);
        var cut = harness.Render();
        await cut.Find(OpenButton).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => Volatile.Read(ref renewals.Count).Should().BeGreaterThan(0), TimeSpan.FromSeconds(5));

        harness.Circuit.SetConnected(false);
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        var afterDrop = Volatile.Read(ref renewals.Count);
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        Volatile.Read(ref renewals.Count).Should().Be(afterDrop, "renewal stops when the connection drops");

        harness.Circuit.SetConnected(true);
        cut.WaitForAssertion(() => harness.Footer.Snapshot.LeftNotifications.Should()
            .Contain(n => n.Title == "Lease" && n.Message.Contains("connection dropped")));
        await Task.Delay(TimeSpan.FromMilliseconds(1500));
        Volatile.Read(ref renewals.Count).Should().Be(afterDrop, "renewal does not resume on reconnect");
    }

    [TestMethod]
    public async Task Lease_IsNotTaken_ByACommandThatCompletesAfterTheConnectionDropped()
    {
        await using var harness = new RoofConsoleHarness(Status(RoofControllerStatus.PartiallyOpen, 1)).SignInAsOperator();
        var renewals = SetupLeasedOpen(harness);
        using var openGate = new ManualResetEventSlim(false);
        harness.Roof.Setup(r => r.Open()).Returns(() =>
        {
            openGate.Wait(TimeSpan.FromSeconds(10));
            harness.Current = LeasedOpening();
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        var cut = harness.Render();

        var openTask = cut.Find(OpenButton).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find(OpenButton).GetAttribute("title").Should().Be("A command is in progress"));
        harness.Circuit.SetConnected(false);
        openGate.Set();
        await openTask;
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        Volatile.Read(ref renewals.Count).Should().Be(0);
        harness.Circuit.SetConnected(true);
        cut.WaitForAssertion(() => harness.Footer.Snapshot.LeftNotifications.Should()
            .Contain(n => n.Title == "Lease" && n.Message.Contains("connection dropped")));
    }

    /// <summary>Open succeeds and leaves the roof opening with a 1.5 s lease (renewed every 500 ms); counts renewals.</summary>
    private static RenewalCounter SetupLeasedOpen(RoofConsoleHarness harness)
    {
        var renewals = new RenewalCounter();
        harness.Roof.Setup(r => r.Open()).Returns(() =>
        {
            harness.Current = LeasedOpening();
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        harness.Roof.Setup(r => r.RenewLease()).Returns(() =>
        {
            Interlocked.Increment(ref renewals.Count);
            return Result<RoofStatusResponse>.Success(harness.Current);
        });
        return renewals;
    }

    private static RoofStatusResponse LeasedOpening() => Status(RoofControllerStatus.Opening, 2) with { LeaseSecondsRemaining = 1.5 };

    private sealed class RenewalCounter
    {
        public int Count;
    }

    private static void SetupHealth(RoofConsoleHarness harness, HealthReport report)
        => harness.Health
            .Setup(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

    private static HealthReport UnhealthyReport() => new(
        new Dictionary<string, HealthReportEntry>
        {
            ["camera"] = new(HealthStatus.Healthy, "Camera reachable", TimeSpan.FromMilliseconds(2), null, null),
            ["roof-controller"] = new(
                HealthStatus.Unhealthy,
                "Relay register unverified",
                TimeSpan.FromMilliseconds(3),
                new InvalidOperationException("secret detail"),
                new Dictionary<string, object> { ["consecutiveFailures"] = 3 },
                new[] { "hardware" })
        },
        TimeSpan.FromMilliseconds(5));
}
