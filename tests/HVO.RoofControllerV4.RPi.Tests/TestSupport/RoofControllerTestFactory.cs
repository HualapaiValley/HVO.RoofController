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
    /// Gets default options commonly used across service tests. Callers can mutate the returned instance
    /// before passing it to <see cref="Options.Create{TOptions}(TOptions)"/> or <see cref="CreateService"/>.
    /// </summary>
    public static RoofControllerOptionsV4 CreateDefaultOptions(Action<RoofControllerOptionsV4>? configure = null)
    {
        var options = new RoofControllerOptionsV4
        {
            EnableDigitalInputPolling = false,
            DigitalInputPollInterval = TimeSpan.FromMilliseconds(5),
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
        ILogger<RoofControllerServiceV4>? logger = null)
    {
        var options = CreateDefaultOptions(configureOptions);
        return new RoofControllerServiceV4(logger ?? NullLogger<RoofControllerServiceV4>.Instance, Options.Create(options), hat);
    }

    /// <summary>
    /// Convenience helper returning wrapped options for scenarios that need an <see cref="IOptions{TOptions}"/> instance.
    /// </summary>
    public static IOptions<RoofControllerOptionsV4> CreateWrappedOptions(Action<RoofControllerOptionsV4>? configureOptions = null)
        => Options.Create(CreateDefaultOptions(configureOptions));
}
