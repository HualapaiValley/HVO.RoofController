using System.Globalization;
using System.Text.Json;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Web.Security;

namespace HVO.RoofControllerV4.Web.Roof;

/// <summary>
/// The texts <c>wwwroot/js/stop.js</c> shows, rendered into each Stop form (<c>App.razor</c>) so the script has no wording
/// of its own: every message is <see cref="RoofStopText"/>'s, with <see cref="RoofText"/>'s reason for a refusal that is
/// not the Stop endpoint's own answer (a proxy's error page, or the web UI's origin check), and the web UI's own reason
/// when the page could not reach it.
/// </summary>
public static class WebStopTexts
{
    /// <summary>Where <see cref="Json"/>'s <c>other</c> text takes the HTTP status.</summary>
    public const string StatusPlaceholder = "{status}";

    /// <summary>The page could not reach the web UI (a network error), so it cannot tell whether Stop was sent.</summary>
    public const string Unreachable = "The web UI could not be reached.";

    /// <summary>The web UI did not answer within <see cref="PageTimeout"/>.</summary>
    public const string TimedOut = "The web UI did not answer in time.";

    /// <summary>
    /// How long the page waits for the web UI's answer: longer than the web UI waits for the controller (the client's
    /// Stop timeout, 10 s, twice when a closed session's client is replaced), so the page shows the controller's answer
    /// rather than a timeout of its own.
    /// </summary>
    public static TimeSpan PageTimeout { get; } = TimeSpan.FromSeconds(25);

    /// <summary>The web UI's own refusal of a request from another site (<see cref="OriginCheck"/>).</summary>
    public const string OriginRefused = "The web UI refused a request from this page's origin. [" + OriginCheck.ProblemCode + "]";

    /// <summary>
    /// The statuses below 500 that have their own wording in <see cref="RoofText.DescribeRefusal"/> (401 is
    /// <c>signedOut</c>), and 503; every other status from 500 up is <c>serverError</c>.
    /// </summary>
    public static readonly IReadOnlyList<int> Statuses = [400, 403, 404, 408, 429, 503];

    /// <summary>
    /// The texts as JSON: <c>timeoutMilliseconds</c> (<see cref="PageTimeout"/>), <c>sending</c>, <c>signedOut</c>,
    /// <c>timedOut</c>, <c>unreachable</c>, <c>codes</c> (by problem code), <c>statuses</c> (by HTTP status),
    /// <c>serverError</c> (any other status from 500 up) and <c>other</c> (any other status, named at
    /// <see cref="StatusPlaceholder"/>).
    /// </summary>
    public static string Json { get; } = Build();

    private static string Build()
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var code in Enum.GetValues<RoofControllerErrorCode>())
        {
            codes[code.ToString()] = RoofStopText.Failed(RoofText.DescribeRefusal(0, code, code.ToString()));
        }

        codes[OriginCheck.ProblemCode] = RoofStopText.Failed(OriginRefused);

        // DescribeRefusal names a status it has no wording for; 0 marks where the script puts the real one.
        var other = RoofStopText.Failed(RoofText.DescribeRefusal(0, null, null)).Replace("(HTTP 0)", $"(HTTP {StatusPlaceholder})", StringComparison.Ordinal);
        return JsonSerializer.Serialize(new
        {
            timeoutMilliseconds = (int)PageTimeout.TotalMilliseconds,
            sending = RoofStopText.Sending,
            signedOut = RoofStopText.PageSignedOut,
            timedOut = RoofStopText.Failed(TimedOut),
            unreachable = RoofStopText.Failed(Unreachable),
            codes,
            statuses = Statuses.ToDictionary(
                status => status.ToString(CultureInfo.InvariantCulture),
                status => RoofStopText.Failed(RoofText.DescribeRefusal(status, null, null))),
            serverError = RoofStopText.Failed(RoofText.DescribeRefusal(500, null, null)),
            other
        });
    }
}
