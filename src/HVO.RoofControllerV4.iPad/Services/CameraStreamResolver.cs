using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// The URL the camera view should load, or why there is none.
/// </summary>
public sealed record CameraStreamResolution(Uri? StreamUri, string? Error);

/// <summary>
/// Recognises the controller's own camera proxy (<c>/api/v1.0/Camera/{id}/mjpeg</c>), which needs a short-lived
/// ticket because a WebView cannot send the <c>X-Api-Key</c> header. Other camera URLs are loaded as configured.
/// </summary>
public static partial class CameraStreamResolver
{
    [GeneratedRegex(@"^/api/v1\.0/camera/(?<id>[^/]+)/mjpeg/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProxyPathRegex();

    /// <summary>
    /// True when <paramref name="cameraUri"/> is the camera proxy on the same origin as the controller.
    /// </summary>
    public static bool TryGetProxyCameraId(Uri cameraUri, Uri controllerRoot, [NotNullWhen(true)] out string? cameraId)
    {
        ArgumentNullException.ThrowIfNull(cameraUri);
        ArgumentNullException.ThrowIfNull(controllerRoot);
        cameraId = null;

        if (!IsSameOrigin(cameraUri, controllerRoot))
        {
            return false;
        }

        var match = ProxyPathRegex().Match(cameraUri.AbsolutePath);
        if (!match.Success)
        {
            return false;
        }

        cameraId = Uri.UnescapeDataString(match.Groups["id"].Value);
        return cameraId.Length > 0;
    }

    public static Uri GetTicketEndpoint(Uri controllerRoot, string cameraId)
    {
        ArgumentNullException.ThrowIfNull(controllerRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        return new Uri(controllerRoot, $"api/v1.0/Camera/{Uri.EscapeDataString(cameraId)}/ticket");
    }

    /// <summary>
    /// Resolves the ticket's stream URL against the controller root. Rejects a URL that points at another origin,
    /// so a ticket is only ever presented to the controller that issued it.
    /// </summary>
    public static bool TryResolveTicketUrl(Uri controllerRoot, string? ticketUrl, [NotNullWhen(true)] out Uri? streamUri)
    {
        ArgumentNullException.ThrowIfNull(controllerRoot);
        streamUri = null;

        if (string.IsNullOrWhiteSpace(ticketUrl) || !Uri.TryCreate(controllerRoot, ticketUrl.Trim(), out var resolved))
        {
            return false;
        }

        if (!IsSameOrigin(resolved, controllerRoot))
        {
            return false;
        }

        streamUri = resolved;
        return true;
    }

    public static bool IsSameOrigin(Uri left, Uri right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.IsAbsoluteUri
            && right.IsAbsoluteUri
            && string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port;
    }
}
