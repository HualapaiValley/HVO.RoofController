using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// Versioned, transactional configuration updates.
/// </summary>
[TestClass]
public class RoofControllerConfigurationTests
{
    private static async Task<(SimulatedRoofControllerService Service, FakeRoofHat Hat, ManualTimeProvider Time)> CreateInitializedAsync(
        bool hardwareBacked = false,
        Action<RoofControllerOptionsV4>? configure = null)
    {
        var hat = new FakeRoofHat(hardwareBacked);
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        var service = SimulatedRoofControllerService.Create(hat, time, configure);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        return (service, hat, time);
    }

    [TestMethod]
    public async Task UpdateConfiguration_ShouldFail_WhenRoofIsMoving()
    {
        var (service, _, _) = await CreateInitializedAsync();
        using var _ = service;

        var originalOptions = service.GetConfigurationSnapshot();
        var originalVersion = service.GetConfigurationState().Version;
        service.Open().IsSuccessful.Should().BeTrue();
        service.IsMoving.Should().BeTrue();

        var attemptedUpdate = originalOptions with { SafetyWatchdogTimeout = originalOptions.SafetyWatchdogTimeout + TimeSpan.FromSeconds(30) };

        var result = service.UpdateConfiguration(attemptedUpdate);

        result.IsSuccessful.Should().BeFalse();
        result.Error.Should().BeOfType<RoofControllerException>();
        result.Error.Should().BeAssignableTo<InvalidOperationException>("existing callers catch InvalidOperationException");
        result.ErrorCode().Should().Be(RoofControllerErrorCode.OperationInProgress);

        service.GetConfigurationSnapshot().SafetyWatchdogTimeout.Should().Be(originalOptions.SafetyWatchdogTimeout);
        service.GetConfigurationState().Version.Should().Be(originalVersion);
        service.Stop().IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task UpdateConfiguration_ShouldFail_WhileClearFaultPulseIsInProgress()
    {
        var (service, _, time) = await CreateInitializedAsync();
        using var _ = service;

        var clear = service.ClearFault(200, CancellationToken.None);
        service.GetCurrentStatusSnapshot().IsClearFaultInProgress.Should().BeTrue();

        var options = service.GetConfigurationSnapshot();
        var result = service.UpdateConfiguration(options with { LimitSwitchDebounce = TimeSpan.FromMilliseconds(40) });
        result.ErrorCode().Should().Be(RoofControllerErrorCode.OperationInProgress);
        service.GetConfigurationSnapshot().LimitSwitchDebounce.Should().Be(options.LimitSwitchDebounce);

        time.Advance(TimeSpan.FromMilliseconds(200));
        (await clear.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccessful.Should().BeTrue();

        service.UpdateConfiguration(options with { LimitSwitchDebounce = TimeSpan.FromMilliseconds(40) }).IsSuccessful.Should().BeTrue();
    }

    [TestMethod]
    public async Task VersionedUpdate_ShouldApply_WhenVersionMatches_AndBumpTheVersion()
    {
        var (service, _, _) = await CreateInitializedAsync();
        using var _ = service;

        var state = service.GetConfigurationState();
        var update = state.Options with { SafetyWatchdogTimeout = TimeSpan.FromSeconds(45) };

        var result = service.UpdateConfiguration(update, state.Version);

        result.IsSuccessful.Should().BeTrue();
        result.Value.SafetyWatchdogTimeout.Should().Be(TimeSpan.FromSeconds(45));
        var after = service.GetConfigurationState();
        after.Version.Should().Be(state.Version + 1);
        after.Options.SafetyWatchdogTimeout.Should().Be(TimeSpan.FromSeconds(45));
    }

    [TestMethod]
    public async Task VersionedUpdate_WithStaleVersion_ShouldBeRejectedWithConflict_AndChangeNothing()
    {
        var (service, _, _) = await CreateInitializedAsync();
        using var _ = service;

        var stale = service.GetConfigurationState();
        service.UpdateConfiguration(stale.Options with { SafetyWatchdogTimeout = TimeSpan.FromSeconds(30) }, stale.Version)
            .IsSuccessful.Should().BeTrue();

        var result = service.UpdateConfiguration(stale.Options with { SafetyWatchdogTimeout = TimeSpan.FromSeconds(60) }, stale.Version);

        result.ErrorCode().Should().Be(RoofControllerErrorCode.ConfigurationVersionConflict);
        var current = service.GetConfigurationState();
        current.Version.Should().Be(stale.Version + 1);
        current.Options.SafetyWatchdogTimeout.Should().Be(TimeSpan.FromSeconds(30), "the second writer must not overwrite the first");
    }

    [TestMethod]
    public async Task SingleArgumentUpdate_ShouldApplyAgainstTheCurrentVersion()
    {
        var (service, _, _) = await CreateInitializedAsync();
        using var _ = service;

        var before = service.GetConfigurationState();
        service.UpdateConfiguration(before.Options with { SafetyWatchdogTimeout = TimeSpan.FromSeconds(30) }).IsSuccessful.Should().BeTrue();
        service.UpdateConfiguration(before.Options with { SafetyWatchdogTimeout = TimeSpan.FromSeconds(40) }).IsSuccessful.Should().BeTrue();

        var after = service.GetConfigurationState();
        after.Version.Should().Be(before.Version + 2);
        after.Options.SafetyWatchdogTimeout.Should().Be(TimeSpan.FromSeconds(40));
    }

    [TestMethod]
    public async Task InvalidUpdate_ShouldBeRejected_AndLeaveConfigurationAndVersionUnchanged()
    {
        var (service, _, _) = await CreateInitializedAsync();
        using var _ = service;

        var before = service.GetConfigurationState();
        var result = service.UpdateConfiguration(before.Options with
        {
            SafetyWatchdogTimeout = TimeSpan.FromSeconds(1), // below the minimum
            OpenRelayId = 2 // duplicate relay id
        });

        result.ErrorCode().Should().Be(RoofControllerErrorCode.InvalidRequest);
        var after = service.GetConfigurationState();
        after.Version.Should().Be(before.Version);
        after.Options.Should().Be(before.Options, "a rejected update changes no field");
    }

    [TestMethod]
    public async Task NullUpdate_ShouldBeRejectedAsInvalidRequest()
    {
        var (service, _, _) = await CreateInitializedAsync();
        using var _ = service;

        service.UpdateConfiguration(null!).ErrorCode().Should().Be(RoofControllerErrorCode.InvalidRequest);
    }

    [TestMethod]
    public async Task IgnoringLimitSwitches_OnPhysicalHardware_ShouldBeRejected_AndConsentCannotBeSetRemotely()
    {
        var (service, _, _) = await CreateInitializedAsync(hardwareBacked: true);
        using var _ = service;
        service.IsUsingPhysicalHardware.Should().BeTrue();

        var before = service.GetConfigurationState();
        var result = service.UpdateConfiguration(before.Options with
        {
            IgnorePhysicalLimitSwitches = true,
            AllowIgnoringLimitSwitchesOnPhysicalHardware = true // local-only; the update cannot grant it
        });

        result.ErrorCode().Should().Be(RoofControllerErrorCode.ConfigurationRejected);
        var after = service.GetConfigurationState();
        after.Version.Should().Be(before.Version);
        after.Options.IgnorePhysicalLimitSwitches.Should().BeFalse();
        after.Options.AllowIgnoringLimitSwitchesOnPhysicalHardware.Should().BeFalse();
    }

    [TestMethod]
    public async Task IgnoringLimitSwitches_OnPhysicalHardware_ShouldBeAllowed_WithLocalConsent()
    {
        var (service, _, _) = await CreateInitializedAsync(hardwareBacked: true, opts => opts.AllowIgnoringLimitSwitchesOnPhysicalHardware = true);
        using var _ = service;

        var before = service.GetConfigurationState();
        var result = service.UpdateConfiguration(before.Options with
        {
            IgnorePhysicalLimitSwitches = true,
            AllowIgnoringLimitSwitchesOnPhysicalHardware = false // cannot be revoked remotely either
        });

        result.IsSuccessful.Should().BeTrue();
        result.Value.AllowIgnoringLimitSwitchesOnPhysicalHardware.Should().BeTrue();
        service.IsIgnoringPhysicalLimitSwitches.Should().BeTrue();
    }

    [TestMethod]
    public void Initialize_ShouldRefuse_IgnoringLimitSwitchesOnPhysicalHardwareWithoutConsent()
    {
        var hat = new FakeRoofHat(hardwareBacked: true);
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), opts => opts.IgnorePhysicalLimitSwitches = true);

        var result = service.Initialize(CancellationToken.None).GetAwaiter().GetResult();

        result.ErrorCode().Should().Be(RoofControllerErrorCode.ConfigurationRejected);
        service.IsInitialized.Should().BeFalse();
        service.Open().ErrorCode().Should().Be(RoofControllerErrorCode.NotInitialized);
        hat.RelayMask.Should().Be(0x00);
    }

    [TestMethod]
    public void Initialize_ShouldAllow_IgnoringLimitSwitchesInSimulation()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(false, false, false, false); // would be contradictory if the limits were honoured
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), opts => opts.IgnorePhysicalLimitSwitches = true);

        service.Initialize(CancellationToken.None).GetAwaiter().GetResult().IsSuccessful.Should().BeTrue();
        service.IsIgnoringPhysicalLimitSwitches.Should().BeTrue();
        service.GetCurrentStatusSnapshot().IsOpenLimitActive.Should().BeNull("ignored limits are reported as unknown");
        service.Status.Should().Be(RoofControllerStatus.Stopped);
    }

    [TestMethod]
    public void Initialize_ShouldRefuse_InvalidConfiguration()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        using var service = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), opts =>
        {
            opts.EnableDigitalInputPolling = false;
            opts.EnablePeriodicVerificationWhileMoving = false; // no software supervision at all
        });

        service.Initialize(CancellationToken.None).GetAwaiter().GetResult().ErrorCode().Should().Be(RoofControllerErrorCode.ConfigurationRejected);
        service.IsInitialized.Should().BeFalse();
    }

    [TestMethod]
    public async Task Update_ShouldApplyHardwareSettings()
    {
        var (service, hat, _) = await CreateInitializedAsync();
        using var _ = service;

        var before = service.GetConfigurationState();
        service.UpdateConfiguration(before.Options with { DigitalInputPollInterval = TimeSpan.FromMilliseconds(40) }, before.Version)
            .IsSuccessful.Should().BeTrue();

        hat.DigitalInputPollInterval.Should().Be(TimeSpan.FromMilliseconds(40));
    }

    [TestMethod]
    public async Task Update_ShouldReevaluateSafetyRules_WithTheNewPolarity()
    {
        // Raw IN3 LOW: inactive with active-high polarity, active with active-low polarity.
        var (service, _, _) = await CreateInitializedAsync();
        using var _ = service;
        service.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();

        var state = service.GetConfigurationState();
        service.UpdateConfiguration(state.Options with { FaultInputActiveHigh = false }, state.Version).IsSuccessful.Should().BeTrue();

        var snapshot = service.GetCurrentStatusSnapshot();
        snapshot.IsDriveFaultActive.Should().BeTrue();
        snapshot.IsFaultLatched.Should().BeTrue();
        snapshot.LatchedFaultReason.Should().Be(RoofControllerStopReason.DriveFault);
    }

    [TestMethod]
    public async Task ConcurrentUpdatesAndSupervision_ShouldNotDeadlock_AndVersionsStayConsistent()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);
        // Real time and the real background loop: this exercises lock ordering, not timing.
        using var service = SimulatedRoofControllerService.Create(hat, configure: opts =>
        {
            opts.EnableDigitalInputPolling = true;
            opts.DigitalInputPollInterval = TimeSpan.FromMilliseconds(5);
        }, backgroundSupervision: true);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var initialVersion = service.GetConfigurationState().Version;
        var succeeded = 0;
        var conflicts = 0;
        using var stop = new CancellationTokenSource();

        var toggler = Task.Run(async () =>
        {
            var high = true;
            while (!stop.IsCancellationRequested)
            {
                high = !high;
                hat.SetInputs(true, true, false, high); // IN4 edges for the HAT poll loop
                service.RefreshStatus(forceHardwareRead: true);
                await Task.Yield();
            }
        });

        var writers = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < 50; n++)
            {
                var state = service.GetConfigurationState();
                var debounce = TimeSpan.FromMilliseconds(10 + ((i * 50 + n) % 100));
                var result = service.UpdateConfiguration(state.Options with { LimitSwitchDebounce = debounce }, state.Version);
                if (result.IsSuccessful)
                {
                    Interlocked.Increment(ref succeeded);
                }
                else
                {
                    result.ErrorCode().Should().Be(RoofControllerErrorCode.ConfigurationVersionConflict);
                    Interlocked.Increment(ref conflicts);
                }
            }
        })).ToArray();

        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(30));
        stop.Cancel();
        await toggler.WaitAsync(TimeSpan.FromSeconds(10));

        (succeeded + conflicts).Should().Be(200);
        succeeded.Should().BeGreaterThan(0);
        service.GetConfigurationState().Version.Should().Be(initialVersion + succeeded, "each successful update bumps the version exactly once");
        hat.RelayMask.Should().Be(0x00);
    }
}
