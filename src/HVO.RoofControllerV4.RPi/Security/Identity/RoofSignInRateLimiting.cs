using System;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.RateLimiting;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>
/// Limits sign-in attempts per caller (<see cref="RoofIdentityOptions.SignInAttemptsPerMinute"/>), on top of the
/// per-name and per-kiosk lockout: it bounds how fast one caller can guess across many names. It runs after
/// authentication, so a request without a valid key or session is refused before it counts. A caller is the kiosk key
/// (<c>Auth/Pin</c>), the signed-in person (<c>Auth/Password</c>) or, for an anonymous <c>Auth/Session</c>, the remote
/// address (<see cref="AddressOf"/>). A refusal is 429 <c>SignInBusy</c> with <c>Retry-After</c>, sent before any secret
/// is checked. Stop never signs in, so it is never limited.
/// </summary>
public static class RoofSignInRateLimiting
{
    /// <summary>The rate-limiting policy on <c>Auth/Session</c>, <c>Auth/Pin</c> and <c>Auth/Password</c>.</summary>
    public const string PolicyName = "RoofSignIn";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Registers the sign-in rate limit.</summary>
    public static IServiceCollection AddRoofSignInRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<RoofSignInRefusalLog>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejectedAsync;
            options.AddPolicy(PolicyName, context =>
            {
                var limit = context.RequestServices.GetRequiredService<IOptionsMonitor<RoofIdentityOptions>>().CurrentValue.SignInAttemptsPerMinute;
                var caller = PartitionFor(context);

                // The limit is part of the partition key, so a changed setting takes effect at once.
                return limit <= 0
                    ? RateLimitPartition.GetNoLimiter($"{caller}|off")
                    : RateLimitPartition.GetFixedWindowLimiter(
                        $"{caller}|{limit}",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = Window, QueueLimit = 0 });
            });
        });

        return services;
    }

    /// <summary>
    /// Who a sign-in attempt is counted against: <c>key:&lt;id&gt;</c> for an API key, <c>person:&lt;name&gt;</c> for a
    /// session, otherwise <c>address:&lt;address&gt;</c> (<see cref="AddressOf"/>). An anonymous endpoint
    /// (<c>Auth/Session</c>) is always counted by address: a credential sent with it must not buy its own budget.
    /// </summary>
    internal static string PartitionFor(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var user = context.User;
        var anonymous = context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        if (!anonymous && user.Identity?.IsAuthenticated == true)
        {
            if (user.FindFirst(RoofPrincipalFactory.KeyIdClaimType)?.Value is { Length: > 0 } keyId)
            {
                return "key:" + keyId;
            }

            if (user.FindFirst(RoofPrincipalFactory.SessionIdClaimType) is not null && user.Identity.Name is { Length: > 0 } name)
            {
                return "person:" + name.ToLowerInvariant();
            }
        }

        return "address:" + AddressOf(context.Connection.RemoteIpAddress, context.Connection.LocalIpAddress);
    }

    /// <summary>The address an anonymous caller is counted by (<see cref="RoofCallerAddress.Of"/>).</summary>
    internal static string AddressOf(IPAddress? address, IPAddress? local = null) => RoofCallerAddress.Of(address, local);

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : Window;
        http.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        http.RequestServices.GetRequiredService<RoofSignInRefusalLog>().Refused(http);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Sign-in busy",
            Type = RoofControllerApiContract.ProblemType(RoofControllerErrorCode.SignInBusy),
            Detail = "Too many sign-in attempts from this caller. Retry after the time in Retry-After. Stop still works."
        };
        problem.Extensions[RoofControllerApiContract.ProblemCodeExtension] = nameof(RoofControllerErrorCode.SignInBusy);
        await http.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem })
            .ConfigureAwait(false);
    }
}

/// <summary>Logs refused sign-in attempts as one SECURITY line per <see cref="LogEvery"/> at most, counting the rest.</summary>
internal sealed class RoofSignInRefusalLog
{
    internal static readonly TimeSpan LogEvery = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ILogger<RoofSignInRefusalLog> _logger;
    private DateTimeOffset? _lastLogged;
    private int _suppressed;

    public RoofSignInRefusalLog(TimeProvider time, ILogger<RoofSignInRefusalLog> logger)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Refused(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        int suppressed;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lastLogged is { } last && now - last < LogEvery)
            {
                _suppressed++;
                return;
            }

            _lastLogged = now;
            suppressed = _suppressed;
            _suppressed = 0;
        }

        _logger.LogWarning(
            "SECURITY sign-in refused for {Caller} from {RemoteIp} ({Method} {Path}): too many attempts ({Suppressed} more refusals not logged).",
            RoofSignInRateLimiting.PartitionFor(http),
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            http.Request.Method,
            http.Request.Path.Value,
            suppressed);
    }
}
