using System;
using System.Globalization;
using System.Threading;
using System.Threading.RateLimiting;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>
/// Limits sign-in attempts per remote address (<see cref="RoofIdentityOptions.SignInAttemptsPerMinute"/>), on top of
/// the per-name and per-kiosk lockout: it bounds how fast one address can guess across many names, or fill the
/// lockout's memory with made-up ones. A refusal is 429 <c>SignInBusy</c> with <c>Retry-After</c>, sent before any
/// secret is checked. Stop never signs in, so it is never limited.
/// </summary>
public static class RoofSignInRateLimiting
{
    /// <summary>The rate-limiting policy on <c>Auth/Session</c>, <c>Auth/Pin</c> and <c>Auth/Password</c>.</summary>
    public const string PolicyName = "RoofSignIn";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LogEvery = TimeSpan.FromSeconds(30);
    private static long s_lastLoggedTicks;
    private static int s_suppressed;

    /// <summary>Registers the sign-in rate limit.</summary>
    public static IServiceCollection AddRoofSignInRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejectedAsync;
            options.AddPolicy(PolicyName, context =>
            {
                var limit = context.RequestServices.GetRequiredService<IOptionsMonitor<RoofIdentityOptions>>().CurrentValue.SignInAttemptsPerMinute;
                var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                // The limit is part of the partition key, so a changed setting takes effect at once.
                return limit <= 0
                    ? RateLimitPartition.GetNoLimiter($"{address}|off")
                    : RateLimitPartition.GetFixedWindowLimiter(
                        $"{address}|{limit}",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = Window, QueueLimit = 0 });
            });
        });

        return services;
    }

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : Window;
        http.Response.Headers.RetryAfter = Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        LogThrottled(http);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Sign-in busy",
            Type = RoofControllerApiContract.ProblemType(RoofControllerErrorCode.SignInBusy),
            Detail = "Too many sign-in attempts from this address. Retry after the time in Retry-After. Stop still works."
        };
        problem.Extensions[RoofControllerApiContract.ProblemCodeExtension] = nameof(RoofControllerErrorCode.SignInBusy);
        await http.RequestServices.GetRequiredService<IProblemDetailsService>()
            .WriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem })
            .ConfigureAwait(false);
    }

    /// <summary>One SECURITY line per <see cref="LogEvery"/> at most, counting the refusals in between.</summary>
    private static void LogThrottled(HttpContext http)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref s_lastLoggedTicks);
        if (now - last < LogEvery.Ticks || Interlocked.CompareExchange(ref s_lastLoggedTicks, now, last) != last)
        {
            Interlocked.Increment(ref s_suppressed);
            return;
        }

        var suppressed = Interlocked.Exchange(ref s_suppressed, 0);
        http.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RoofSignInRateLimiting).FullName!)
            .LogWarning(
                "SECURITY sign-in refused for {RemoteIp} ({Method} {Path}): too many attempts from this address ({Suppressed} more refusals not logged).",
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                http.Request.Method,
                http.Request.Path.Value,
                suppressed);
    }
}
