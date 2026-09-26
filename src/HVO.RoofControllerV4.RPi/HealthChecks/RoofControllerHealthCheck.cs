using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.HealthChecks
{
    /// <summary>
    /// Health check for the roof controller system to monitor its operational status.
    /// </summary>
    public class RoofControllerHealthCheck : IHealthCheck
    {
        private readonly IRoofControllerServiceV4 _roofController;
        private readonly ILogger<RoofControllerHealthCheck> _logger;
        private readonly RoofControllerOptionsV4 _options;

        public RoofControllerHealthCheck(IRoofControllerServiceV4 roofController, ILogger<RoofControllerHealthCheck> logger, IOptions<RoofControllerOptionsV4> options)
        {
            _roofController = roofController;
            _logger = logger;
            _options = options.Value;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    ["IsInitialized"] = _roofController.IsInitialized,
                    ["IsServiceDisposed"] = _roofController.IsServiceDisposed,
                    ["Status"] = _roofController.Status.ToString(),
                    ["IsMoving"] = _roofController.IsMoving,
                    ["LastStopReason"] = _roofController.LastStopReason.ToString(),
                    ["LastTransitionUtc"] = _roofController.LastTransitionUtc?.UtcDateTime.ToString("O") ?? string.Empty,
                    ["IsWatchdogActive"] = _roofController.IsWatchdogActive,
                    ["WatchdogSecondsRemaining"] = _roofController.WatchdogSecondsRemaining ?? 0d,
                    ["Ready"] = _roofController.IsInitialized && !_roofController.IsServiceDisposed,
                    ["CheckTime"] = DateTime.UtcNow,
                    ["IgnorePhysicalLimitSwitches"] = _options.IgnorePhysicalLimitSwitches,
                    ["HardwareMode"] = _roofController.IsUsingPhysicalHardware ? "Physical" : "Simulation"
                };

                // Service disposed is a hard failure for readiness
                if (_roofController.IsServiceDisposed)
                {
                    _logger.LogError("Roof controller service is disposed");
                    return Task.FromResult(HealthCheckResult.Unhealthy(
                        "Roof controller service is disposed",
                        null,
                        data));
                }

                // Check if the roof controller is initialized
                if (!_roofController.IsInitialized)
                {
                    _logger.LogWarning("Roof controller is not initialized");
                    return Task.FromResult(HealthCheckResult.Unhealthy(
                        "Roof controller is not initialized", 
                        null, 
                        data));
                }

                // Check if the roof controller is in an error state
                if (_roofController.Status == RoofControllerStatus.Error)
                {
                    _logger.LogError("Roof controller is in error state");
                    return Task.FromResult(HealthCheckResult.Unhealthy(
                        "Roof controller is in error state", 
                        null, 
                        data));
                }

                // Check if the roof controller is in an unknown state
                if (_roofController.Status == RoofControllerStatus.Unknown)
                {
                    _logger.LogWarning("Roof controller status is unknown");
                    return Task.FromResult(HealthCheckResult.Degraded(
                        "Roof controller status is unknown", 
                        null, 
                        data));
                }

                // All checks passed
                _logger.LogDebug("Roof controller health check passed");
                return Task.FromResult(HealthCheckResult.Healthy(
                    "Roof controller is operational", 
                    data));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during roof controller health check");
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    "Error checking roof controller health", 
                    ex));
            }
        }
    }
}
