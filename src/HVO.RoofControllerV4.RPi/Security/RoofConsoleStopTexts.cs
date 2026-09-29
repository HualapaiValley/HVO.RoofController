using System.Globalization;
using System.Text.Json;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Middleware;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// The texts <c>wwwroot/js/console-stop.js</c> shows, rendered into the reconnect dialog's form (<c>App.razor</c>) so the
/// script has no wording of its own: every message is <see cref="RoofStopText"/>'s, with <see cref="RoofText"/>'s reason
/// for a refusal that is not the endpoint's own answer (a proxy error page, or the origin or HTTPS check).
/// </summary>
public static class RoofConsoleStopTexts
{
    /// <summary>Where <see cref="Json"/>'s <c>other</c> text takes the HTTP status.</summary>
    public const string StatusPlaceholder = "{status}";

    /// <summary>
    /// The statuses below 500 that have their own wording in <see cref="RoofText.DescribeRefusal"/> (401 is
    /// <c>signedOut</c>), and 503; every other status from 500 up is <c>serverError</c>.
    /// </summary>
    public static readonly IReadOnlyList<int> Statuses = [400, 403, 404, 408, 429, 503];

    /// <summary>The codes a refusal before the API can carry, besides <see cref="RoofControllerErrorCode"/> names.</summary>
    public static readonly IReadOnlyList<string> PipelineCodes = [RequireHttpsMiddleware.ProblemCode, OriginCheckMiddleware.ProblemCode];

    /// <summary>
    /// The texts as JSON: <c>sending</c>, <c>signedOut</c>, <c>timedOut</c>, <c>unreachable</c>, <c>codes</c> (by problem
    /// code), <c>statuses</c> (by HTTP status), <c>serverError</c> (any other status from 500 up) and <c>other</c> (any
    /// other status, named at <see cref="StatusPlaceholder"/>).
    /// </summary>
    public static string Json { get; } = Build();

    private static string Build()
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var code in Enum.GetValues<RoofControllerErrorCode>())
        {
            codes[code.ToString()] = RoofStopText.Failed(RoofText.DescribeRefusal(0, code, code.ToString()));
        }

        foreach (var code in PipelineCodes)
        {
            codes[code] = RoofStopText.Failed(RoofText.DescribeRefusal(0, null, code));
        }

        // DescribeRefusal names a status it has no wording for; 0 marks where the script puts the real one.
        var other = RoofStopText.Failed(RoofText.DescribeRefusal(0, null, null)).Replace("(HTTP 0)", $"(HTTP {StatusPlaceholder})", StringComparison.Ordinal);
        return JsonSerializer.Serialize(new
        {
            sending = RoofStopText.Sending,
            signedOut = RoofStopText.PageSignedOut,
            timedOut = RoofStopText.Failed(RoofText.TimedOut),
            unreachable = RoofStopText.Failed(RoofText.Unreachable),
            codes,
            statuses = Statuses.ToDictionary(
                status => status.ToString(CultureInfo.InvariantCulture),
                status => RoofStopText.Failed(RoofText.DescribeRefusal(status, null, null))),
            serverError = RoofStopText.Failed(RoofText.DescribeRefusal(500, null, null)),
            other
        });
    }
}
