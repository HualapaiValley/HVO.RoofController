using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.RoofControllerV4.RPi.HealthChecks;

/// <summary>
/// Reports the certificate the controller serves: Unhealthy when it cannot be read or has expired (clients refuse it),
/// Degraded within 30 days of its expiry, otherwise Healthy (also when no certificate file is configured). Not tagged
/// "hardware", so it never fails the readiness probe or a deploy.
/// </summary>
internal sealed class RoofCertificateHealthCheck : IHealthCheck
{
    /// <summary>A certificate this close to expiring is reported Degraded.</summary>
    internal static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(30);

    private const string Renew = "Renew it on the controller with: sudo hvo-roof-install cert";

    private readonly RoofServerCertificate _certificate;
    private readonly TimeProvider _time;

    public RoofCertificateHealthCheck(RoofServerCertificate certificate, TimeProvider time)
    {
        _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var state = _certificate.Read();
        if (!state.Configured)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "No certificate file is configured.", new Dictionary<string, object> { ["configured"] = false }));
        }

        if (state.Problem is not null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"The HTTPS certificate cannot be checked: {state.Problem}.", data: new Dictionary<string, object> { ["configured"] = true }));
        }

        var notAfter = state.NotAfter!.Value;
        var left = notAfter - _time.GetUtcNow();
        var data = new Dictionary<string, object>
        {
            ["configured"] = true,
            ["subject"] = state.Subject ?? string.Empty,
            ["notAfter"] = notAfter.ToString("O", CultureInfo.InvariantCulture),
            ["daysLeft"] = (int)Math.Floor(left.TotalDays),
            ["sha256"] = state.Fingerprint!
        };
        if (state.AuthorityFingerprint is not null)
        {
            data["issuer"] = state.AuthoritySubject ?? string.Empty;
            data["issuerSha256"] = state.AuthorityFingerprint;
        }

        var date = notAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Task.FromResult(
            left <= TimeSpan.Zero
                ? HealthCheckResult.Unhealthy($"The HTTPS certificate expired on {date}: clients refuse it. {Renew}", data: data)
            : left < ExpiryWarning
                ? HealthCheckResult.Degraded($"The HTTPS certificate expires on {date}, in {(int)Math.Ceiling(left.TotalDays)} days. {Renew}", data: data)
            : HealthCheckResult.Healthy($"The HTTPS certificate is valid until {date}.", data));
    }
}
