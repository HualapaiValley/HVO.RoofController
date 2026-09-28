using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.RoofControllerV4.RPi.HealthChecks;

/// <summary>
/// Reports the identity store: Unhealthy when its file could not be read (sign-in and identity management are refused;
/// configured API keys, Stop and the roof keep working), Degraded when it is kept in memory only, otherwise Healthy.
/// Not tagged "hardware", so it never fails the readiness probe or a deploy.
/// </summary>
internal sealed class RoofIdentityStoreHealthCheck : IHealthCheck
{
    private readonly RoofIdentityStore _identity;

    public RoofIdentityStoreHealthCheck(RoofIdentityStore identity)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_identity.IsAvailable)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "The identity store could not be read: sign-in and identity management are refused; configured API keys " +
                "and Stop still work. See the controller log."));
        }

        var data = new Dictionary<string, object>
        {
            ["persistent"] = _identity.IsPersistent,
            ["users"] = _identity.Users.Count,
            ["managedApiKeys"] = _identity.ManagedKeys.Count,
            ["activeSessions"] = _identity.ActiveSessions.Count
        };

        return Task.FromResult(_identity.IsPersistent
            ? HealthCheckResult.Healthy("The identity store is available.", data)
            : HealthCheckResult.Degraded(
                "No identity store file is configured: people, sessions and managed API keys are lost when the controller restarts.",
                data: data));
    }
}
