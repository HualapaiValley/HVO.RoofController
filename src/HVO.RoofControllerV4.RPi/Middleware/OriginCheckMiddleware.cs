using System;
using System.Collections.Generic;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HVO.RoofControllerV4.RPi.Middleware;

/// <summary>
/// Cross-site request defence for the browser console. For <c>/_blazor*</c> (including the WebSocket upgrade, which
/// SameSite cookies and antiforgery tokens do not cover) and for <c>POST /account/*</c> and <c>POST /console/*</c>: when
/// an <c>Origin</c> header is present it must equal the request's own scheme://host[:port] or one of
/// <see cref="RoofControllerSecurityOptions.AllowedOrigins"/>; otherwise the request is refused with 403.
/// Requests without an <c>Origin</c> header (non-browser clients) pass through to normal authentication.
/// </summary>
public sealed class OriginCheckMiddleware
{
    /// <summary>Problem code / type suffix written when a request is refused.</summary>
    public const string ProblemCode = "origin_not_allowed";

    private static readonly PathString BlazorPath = new("/_blazor");
    private static readonly PathString AccountPath = new("/account");
    private static readonly PathString ConsolePath = new("/console");

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<RoofControllerSecurityOptions> _options;
    private readonly ILogger<OriginCheckMiddleware> _logger;

    public OriginCheckMiddleware(
        RequestDelegate next,
        IOptionsMonitor<RoofControllerSecurityOptions> options,
        ILogger<OriginCheckMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetailsService)
    {
        var request = context.Request;
        if (!AppliesTo(request) || !request.Headers.TryGetValue(HeaderNames.Origin, out var origins))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (origins.Count == 1 && IsAllowedOrigin(origins[0], request, _options.CurrentValue.AllowedOrigins))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning(
            "Refused cross-origin request {Method} {Path} from {RemoteIp} with Origin {Origin}",
            request.Method,
            request.Path.Value,
            context.Connection.RemoteIpAddress,
            origins.ToString());

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Origin not allowed",
            Type = RoofControllerApiContract.ProblemTypePrefix + ProblemCode,
            Detail = "Cross-origin requests to the roof console are not allowed."
        };
        problem.Extensions[RoofControllerApiContract.ProblemCodeExtension] = ProblemCode;

        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem
        }).ConfigureAwait(false);
    }

    /// <summary>True for the Blazor hub and for POSTs to the account and console endpoints.</summary>
    public static bool AppliesTo(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Path.StartsWithSegments(BlazorPath, StringComparison.OrdinalIgnoreCase)
            || (HttpMethods.IsPost(request.Method)
                && (request.Path.StartsWithSegments(AccountPath, StringComparison.OrdinalIgnoreCase)
                    || request.Path.StartsWithSegments(ConsolePath, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// True when <paramref name="origin"/> is the request's own origin or one of <paramref name="allowedOrigins"/>.
    /// The opaque origin <c>null</c> and anything that is not an absolute http(s) origin are rejected.
    /// </summary>
    public static bool IsAllowedOrigin(string? origin, HttpRequest request, IEnumerable<string>? allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryParseOrigin(origin, out var originUri))
        {
            return false;
        }

        if (request.Host.HasValue
            && TryParseOrigin($"{request.Scheme}://{request.Host.Value}", out var self)
            && SameOrigin(originUri, self))
        {
            return true;
        }

        if (allowedOrigins is null)
        {
            return false;
        }

        foreach (var allowed in allowedOrigins)
        {
            if (TryParseOrigin(allowed, out var allowedUri) && SameOrigin(originUri, allowedUri))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseOrigin(string? value, out Uri origin)
    {
        origin = null!;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host))
        {
            return false;
        }

        origin = parsed;
        return true;
    }

    private static bool SameOrigin(Uri a, Uri b)
        => string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port;
}
