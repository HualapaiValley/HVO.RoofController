using System;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// <see cref="RoofControllerServiceV4"/> exposing the protected input-edge hooks, the watchdog callback and the relay
/// primitive, for deterministic tests. Background supervision is off unless requested; tests call
/// <see cref="RoofControllerServiceV4.RunSupervisionCycle"/> and advance a <see cref="ManualTimeProvider"/>.
/// </summary>
internal sealed class SimulatedRoofControllerService : RoofControllerServiceV4
{
    public SimulatedRoofControllerService(
        RoofControllerOptionsV4 options,
        FourRelayFourInputHat hat,
        TimeProvider? timeProvider = null,
        ILogger<RoofControllerServiceV4>? logger = null,
        bool backgroundSupervision = false,
        RoofHatConnection? hatConnection = null)
        : base(logger ?? NullLogger<RoofControllerServiceV4>.Instance, Options.Create(options), hat, null, timeProvider, hatConnection)
    {
        EnableBackgroundSupervision = backgroundSupervision;
    }

    /// <summary>Creates a service with the shared test defaults plus <paramref name="configure"/>.</summary>
    public static SimulatedRoofControllerService Create(
        FourRelayFourInputHat hat,
        TimeProvider? timeProvider = null,
        Action<RoofControllerOptionsV4>? configure = null,
        ILogger<RoofControllerServiceV4>? logger = null,
        bool backgroundSupervision = false,
        RoofHatConnection? hatConnection = null)
        => new(RoofControllerTestFactory.CreateDefaultOptions(configure), hat, timeProvider, logger, backgroundSupervision, hatConnection);

    public void SimForwardLimitRaw(bool high) => OnForwardLimitSwitchChanged(high);

    public void SimReverseLimitRaw(bool high) => OnReverseLimitSwitchChanged(high);

    public void SimFaultRaw(bool high) => OnFaultNotificationChanged(high);

    public void SimAtSpeedRaw(bool high) => OnAtSpeedChanged(high);

    /// <summary>Invokes the watchdog timer callback as if the timer for <paramref name="generation"/> fired.</summary>
    public void TriggerWatchdog(long generation) => OnSafetyWatchdogElapsed(generation);

    /// <summary>Calls the protected relay primitive directly (guard tests).</summary>
    public bool ForceRelayStates(bool stop, bool open, bool close) => SetRelayStatesAtomically(stop, open, close);
}
