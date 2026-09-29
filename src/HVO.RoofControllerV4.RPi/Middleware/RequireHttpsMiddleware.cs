using System;
using System.Net;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Middleware;

/// <summary>
/// Refuses plain-HTTP requests with 403 ProblemDetails (<c>https_required</c>) when
/// <see cref="RoofControllerSecurityOptions.RequireHttps"/> is in effect (default: true outside Development). This is
/// deliberately not a redirect: a redirect would already have sent the API key or session token in clear text.
/// Exempt: loopback connections (the Docker healthcheck and the deploy script's in-container Stop) and the anonymous
/// <c>/health/live</c> and <c>/health/ready</c> probes.
/// </summary>
public sealed class RequireHttpsMiddleware
{
    /// <summary>Problem code / type suffix written when a request is refused.</summary>
    public const string ProblemCode = "https_required";

    private static readonly PathString LivePath = new("/health/live");
    private static readonly PathString ReadyPath = new("/health/ready");

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<RoofControllerSecurityOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<RequireHttpsMiddleware> _logger;

    public RequireHttpsMiddleware(
        RequestDelegate next,
        IOptionsMonitor<RoofControllerSecurityOptions> options,
        IHostEnvironment environment,
        ILogger<RequireHttpsMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetailsService)
    {
        if (context.Request.IsHttps
            || !IsHttpsRequired(_options.CurrentValue, _environment)
            || IsExemptPath(context.Request.Path)
            || IsLoopback(context.Connection.RemoteIpAddress))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _logger.LogDebug(
            "Refused plain-HTTP request {Method} {Path} from {RemoteIp}: HTTPS is required",
            context.Request.Method,
            context.Request.Path.Value,
            context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "HTTPS required",
            Type = RoofControllerApiContract.ProblemTypePrefix + ProblemCode,
            Detail = "This roof controller only accepts HTTPS from remote clients. Use the https:// address."
        };
        problem.Extensions[RoofControllerApiContract.ProblemCodeExtension] = ProblemCode;

        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem
        }).ConfigureAwait(false);
    }

    /// <summary>Effective RequireHttps value: the configured value, or true outside Development when unset.</summary>
    public static bool IsHttpsRequired(RoofControllerSecurityOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        return options.RequireHttps ?? !environment.IsDevelopment();
    }

    /// <summary>
    /// True for loopback peers. A null remote address (in-process test server, Unix socket) is treated as local, since
    /// Kestrel always reports the peer address for TCP connections.
    /// </summary>
    public static bool IsLoopback(IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return true;
        }

        if (remoteAddress.IsIPv4MappedToIPv6)
        {
            remoteAddress = remoteAddress.MapToIPv4();
        }

        return IPAddress.IsLoopback(remoteAddress);
    }

    private static bool IsExemptPath(PathString path)
        => path.Equals(LivePath, StringComparison.OrdinalIgnoreCase) || path.Equals(ReadyPath, StringComparison.OrdinalIgnoreCase);
}
