using System;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// Shared helpers for constructing configured instances of <see cref="RoofControllerServiceV4"/> for tests.
/// Centralises the default option values so individual tests only override the settings they care about.
/// </summary>
internal static class RoofControllerTestFactory
{
    /// <summary>
    /// Gets default options commonly used across service tests. The defaults pass
    /// <see cref="RoofControllerOptionsV4Validator"/>: edge polling is off (tests drive inputs explicitly) and periodic
    /// verification is on. Callers can mutate the returned instance before passing it on.
    /// </summary>
    public static RoofControllerOptionsV4 CreateDefaultOptions(Action<RoofControllerOptionsV4>? configure = null)
    {
        var options = new RoofControllerOptionsV4
        {
            EnableDigitalInputPolling = false,
            DigitalInputPollInterval = TimeSpan.FromMilliseconds(5),
            EnablePeriodicVerificationWhileMoving = true,
            PeriodicVerificationInterval = TimeSpan.FromMilliseconds(100),
            SafetyWatchdogTimeout = TimeSpan.FromSeconds(10),
            UseNormallyClosedLimitSwitches = true,
            OpenRelayId = 1,
            CloseRelayId = 2,
            ClearFaultRelayId = 3,
            StopRelayId = 4
        };

        configure?.Invoke(options);
        return options;
    }

    /// <summary>
    /// Constructs a <see cref="RoofControllerServiceV4"/> with default options and allows selective overrides.
    /// </summary>
    public static RoofControllerServiceV4 CreateService(
        FourRelayFourInputHat hat,
        Action<RoofControllerOptionsV4>? configureOptions = null,
        ILogger<RoofControllerServiceV4>? logger = null,
        TimeProvider? timeProvider = null,
        RoofControllerHostOptionsV4? hostOptions = null)
    {
        var options = CreateDefaultOptions(configureOptions);
        return new RoofControllerServiceV4(
            logger ?? NullLogger<RoofControllerServiceV4>.Instance,
            Options.Create(options),
            hat,
            hostOptions is null ? null : Options.Create(hostOptions),
            timeProvider);
    }

    /// <summary>
    /// Constructs a service on a <see cref="ManualTimeProvider"/> with background supervision disabled, so tests drive
    /// time (<see cref="ManualTimeProvider.Advance"/>) and supervision (<c>RunSupervisionCycle</c>) deterministically.
    /// </summary>
    public static RoofControllerServiceV4 CreateManualService(
        FourRelayFourInputHat hat,
        ManualTimeProvider time,
        Action<RoofControllerOptionsV4>? configureOptions = null,
        ILogger<RoofControllerServiceV4>? logger = null)
    {
        var service = CreateService(hat, configureOptions, logger, time);
        service.EnableBackgroundSupervision = false;
        return service;
    }

    /// <summary>
    /// Convenience helper returning wrapped options for scenarios that need an <see cref="IOptions{TOptions}"/> instance.
    /// </summary>
    public static IOptions<RoofControllerOptionsV4> CreateWrappedOptions(Action<RoofControllerOptionsV4>? configureOptions = null)
        => Options.Create(CreateDefaultOptions(configureOptions));
}
