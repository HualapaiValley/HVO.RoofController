using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HVO.RoofControllerV4.Web.Security;

/// <summary>
/// Cross-site request defence. Every form post (sign-in, sign-out, Stop) and the live connection (<c>/_blazor</c>,
/// including its WebSocket upgrade, which SameSite cookies and antiforgery tokens do not cover) must come from the web
/// UI's own pages: the <c>Origin</c> header (or, without one, the <c>Referer</c>) must be the request's own
/// scheme://host[:port] or one of <see cref="RoofWebOptions.AllowedOrigins"/>. Otherwise the request is refused with 403
/// before anything else runs. A request with neither header (not a browser) goes on to the usual checks.
/// </summary>
public sealed class OriginCheck(RequestDelegate next, IOptions<RoofWebOptions> options, ILogger<OriginCheck> logger)
{
    /// <summary>The problem code written when a request is refused, as the controller words it.</summary>
    public const string ProblemCode = "origin_not_allowed";

    private static readonly PathString BlazorPath = new("/_blazor");

    public async Task InvokeAsync(HttpContext context, IProblemDetailsService problemDetails)
    {
        var request = context.Request;
        if (!AppliesTo(request) || !TryGetSource(request, out var source))
        {
            await next(context);
            return;
        }

        if (IsAllowedOrigin(source, request, options.Value.AllowedOrigins))
        {
            await next(context);
            return;
        }

        logger.LogWarning(
            "Refused cross-origin request {Method} {Path} from {RemoteIp} with origin {Origin}",
            request.Method,
            request.Path.Value,
            context.Connection.RemoteIpAddress,
            source ?? "(several, or unreadable)");

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Origin not allowed",
            Type = RoofControllerApiContract.ProblemTypePrefix + ProblemCode,
            Detail = "Requests from other sites are not allowed. Open the roof controller's web UI directly.",
        };
        problem.Extensions[RoofControllerApiContract.ProblemCodeExtension] = ProblemCode;
        await problemDetails.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
    }

    /// <summary>True for the live connection and for every request that can change something (not GET, HEAD or OPTIONS).</summary>
    public static bool AppliesTo(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Path.StartsWithSegments(BlazorPath, StringComparison.OrdinalIgnoreCase)
            || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method));
    }

    /// <summary>
    /// True when <paramref name="origin"/> is the request's own origin or one of <paramref name="allowedOrigins"/>. The
    /// opaque origin <c>null</c> and anything that is not an absolute http(s) origin are refused.
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

        foreach (var allowed in allowedOrigins ?? [])
        {
            if (TryParseOrigin(allowed, out var allowedUri) && SameOrigin(originUri, allowedUri))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="value"/> can be an allowed origin: an absolute http(s) URL with a host and nothing after
    /// it (no path, query, fragment or user information).
    /// </summary>
    public static bool IsValidAllowedOrigin(string? value)
        => TryParseOrigin(value, out var origin)
            && origin.AbsolutePath == "/"
            && origin.Query.Length == 0
            && origin.Fragment.Length == 0
            && origin.UserInfo.Length == 0;

    // The Origin header, or the origin of the Referer when a browser sent no Origin. More than one Origin is refused.
    private static bool TryGetSource(HttpRequest request, out string? source)
    {
        if (request.Headers.TryGetValue(HeaderNames.Origin, out var origins))
        {
            source = origins.Count == 1 ? origins[0] : null;
            return true;
        }

        if (request.Headers.TryGetValue(HeaderNames.Referer, out var referers))
        {
            source = referers.Count == 1 && Uri.TryCreate(referers[0], UriKind.Absolute, out var referer)
                ? referer.GetLeftPart(UriPartial.Authority)
                : null;
            return true;
        }

        source = null;
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
