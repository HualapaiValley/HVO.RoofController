using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;

namespace HVO.RoofControllerV4.RPi.HealthChecks
{
    /// <summary>
    /// Health check for the roof controller. All status data comes from one coherent snapshot.
    /// </summary>
    /// <remarks>
    /// Unhealthy: disposed, shutting down, not initialized, safety inputs unhealthy (stale or failing reads), relay register
    /// unverified, relay register reads failing or stale, a latched safety fault, or <see cref="RoofControllerStatus.Error"/>.
    /// Degraded: status Unknown, physical limit switches ignored, the HAT emulator in place of the physical HAT, simulation
    /// (no physical hardware), or input polling disabled (edge detection relies on periodic verification only).
    /// </remarks>
    public class RoofControllerHealthCheck : IHealthCheck
    {
        private readonly IRoofControllerServiceV4 _roofController;
        private readonly ILogger<RoofControllerHealthCheck> _logger;
        private readonly RoofControllerOptionsV4 _startupOptions;
        private readonly RoofHatConnection? _hatConnection;

        public RoofControllerHealthCheck(
            IRoofControllerServiceV4 roofController,
            ILogger<RoofControllerHealthCheck> logger,
            IOptions<RoofControllerOptionsV4> options,
            RoofHatConnection? hatConnection = null)
        {
            _roofController = roofController;
            _logger = logger;
            _startupOptions = options.Value;
            _hatConnection = hatConnection;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var disposed = _roofController.IsServiceDisposed;
                var snapshot = _roofController.GetCurrentStatusSnapshot();
                var pollingEnabled = GetLiveConfiguration().EnableDigitalInputPolling;

                var data = new Dictionary<string, object>
                {
                    ["IsInitialized"] = snapshot.IsInitialized,
                    ["IsServiceDisposed"] = disposed,
                    ["IsShuttingDown"] = snapshot.IsShuttingDown,
                    ["Status"] = snapshot.Status.ToString(),
                    ["IsMoving"] = snapshot.IsMoving,
                    ["CommandedMotion"] = snapshot.CommandedMotion.ToString(),
                    ["LastStopReason"] = snapshot.LastStopReason.ToString(),
                    ["LastTransitionUtc"] = snapshot.LastTransitionUtc?.UtcDateTime.ToString("O") ?? string.Empty,
                    ["IsWatchdogActive"] = snapshot.IsWatchdogActive,
                    ["WatchdogSecondsRemaining"] = snapshot.WatchdogSecondsRemaining ?? 0d,
                    ["Ready"] = snapshot.IsInitialized && !disposed && !snapshot.IsShuttingDown,
                    ["CheckTime"] = DateTime.UtcNow,
                    ["IgnorePhysicalLimitSwitches"] = snapshot.IsIgnoringPhysicalLimitSwitches,
                    ["HardwareMode"] = HardwareMode(snapshot),
                    ["HatEmulatorEndpoint"] = _hatConnection?.EmulatorEndpoint ?? string.Empty,
                    ["StatusVersion"] = snapshot.StatusVersion,
                    ["InputsHealthy"] = snapshot.InputsHealthy,
                    ["LastSuccessfulInputReadUtc"] = snapshot.LastSuccessfulInputReadUtc?.UtcDateTime.ToString("O") ?? string.Empty,
                    ["ConsecutiveInputReadFailures"] = snapshot.ConsecutiveInputReadFailures,
                    ["RelayRegisterState"] = snapshot.RelayRegisterState.ToString(),
                    ["RelayRegisterReadsHealthy"] = snapshot.RelayRegisterReadsHealthy,
                    ["LastSuccessfulRelayReadUtc"] = snapshot.LastSuccessfulRelayReadUtc?.UtcDateTime.ToString("O") ?? string.Empty,
                    ["ConsecutiveRelayReadFailures"] = snapshot.ConsecutiveRelayReadFailures,
                    ["IsFaultLatched"] = snapshot.IsFaultLatched,
                    ["LatchedFaultReason"] = snapshot.LatchedFaultReason?.ToString() ?? string.Empty,
                    ["IsClearFaultInProgress"] = snapshot.IsClearFaultInProgress,
                    ["DigitalInputPollingEnabled"] = pollingEnabled,
                    ["LastError"] = snapshot.LastError ?? string.Empty
                };

                if (disposed)
                {
                    return Unhealthy("Roof controller service is disposed", data);
                }

                if (snapshot.IsShuttingDown)
                {
                    return Unhealthy("Roof controller is shutting down", data);
                }

                if (!snapshot.IsInitialized)
                {
                    return Unhealthy("Roof controller is not initialized", data);
                }

                if (snapshot.RelayRegisterState == RoofRelayRegisterState.Unverified)
                {
                    return Unhealthy("Roof controller relay register state is unverified", data);
                }

                if (snapshot.IsFaultLatched)
                {
                    return Unhealthy($"Roof controller safety fault is latched ({snapshot.LatchedFaultReason})", data);
                }

                if (!snapshot.InputsHealthy)
                {
                    return Unhealthy("Roof controller safety inputs are not healthy", data);
                }

                if (!snapshot.RelayRegisterReadsHealthy)
                {
                    return Unhealthy("Roof controller relay register reads are failing or stale", data);
                }

                if (snapshot.Status == RoofControllerStatus.Error)
                {
                    return Unhealthy("Roof controller is in error state", data);
                }

                if (snapshot.Status == RoofControllerStatus.Unknown)
                {
                    return Degraded("Roof controller status is unknown", data);
                }

                if (snapshot.IsIgnoringPhysicalLimitSwitches)
                {
                    return Degraded("Roof controller is ignoring physical limit switches", data);
                }

                if (snapshot.HatMode == RoofHatMode.Emulated)
                {
                    var endpoint = _hatConnection?.EmulatorEndpoint;
                    return Degraded(
                        endpoint is null
                            ? "Roof controller is running against the HAT emulator, not the physical HAT"
                            : $"Roof controller is running against the HAT emulator ({endpoint}), not the physical HAT",
                        data);
                }

                if (!snapshot.IsUsingPhysicalHardware)
                {
                    return Degraded("Roof controller is running in simulation mode", data);
                }

                if (!pollingEnabled)
                {
                    return Degraded("Roof controller digital input polling is disabled", data);
                }

                _logger.LogDebug("Roof controller health check passed");
                return Task.FromResult(HealthCheckResult.Healthy("Roof controller is operational", data));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during roof controller health check");
                return Task.FromResult(HealthCheckResult.Unhealthy("Error checking roof controller health", ex));
            }
        }

        // Physical, Emulated or Simulation. A snapshot without the HAT mode (Unknown) falls back to the hardware flag.
        private static string HardwareMode(RoofStatusResponse snapshot) => snapshot.HatMode switch
        {
            RoofHatMode.Unknown => snapshot.IsUsingPhysicalHardware ? nameof(RoofHatMode.Physical) : nameof(RoofHatMode.Simulation),
            var mode => mode.ToString()
        };

        private RoofControllerOptionsV4 GetLiveConfiguration()
        {
            try
            {
                return _roofController.GetConfigurationSnapshot() ?? _startupOptions;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the live configuration; using startup options");
                return _startupOptions;
            }
        }

        private Task<HealthCheckResult> Unhealthy(string description, IReadOnlyDictionary<string, object> data)
        {
            _logger.LogWarning("Roof controller health: {Description}", description);
            return Task.FromResult(HealthCheckResult.Unhealthy(description, null, data));
        }

        private Task<HealthCheckResult> Degraded(string description, IReadOnlyDictionary<string, object> data)
        {
            _logger.LogDebug("Roof controller health degraded: {Description}", description);
            return Task.FromResult(HealthCheckResult.Degraded(description, null, data));
        }
    }
}
