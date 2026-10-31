using System.Text;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Alpaca.Roof;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common;
using static System.Net.WebUtility;

namespace HVO.RoofControllerV4.Alpaca;

/// <summary>
/// The setup pages (<c>/setup</c> and <c>/setup/v1/dome/0/setup</c>): read-only, in HVO Dark. They say what the server
/// is and does, the roof's identity and state, and where its settings are changed. They change nothing: there is no
/// form, and the settings are changed where the server is installed.
/// </summary>
internal static class AlpacaSetupPage
{
    public static Task WriteAsync(HttpContext context)
    {
        var services = context.RequestServices;
        var options = services.GetRequiredService<AlpacaOptions>();
        var ids = services.GetRequiredService<AlpacaDeviceIds>();
        var dome = services.GetRequiredService<AlpacaDome>();
        var roof = services.GetRequiredService<AlpacaRoof>();
        var connections = services.GetRequiredService<AlpacaConnections>();

        string roofNow;
        try
        {
            var status = roof.GetCurrent();
            roofNow = AlpacaDome.DescribeStatus(status);
        }
        catch (AlpacaException ex)
        {
            roofNow = $"Unknown ({ex.Message})";
        }

        var rows = new List<(string Name, string Value)>
        {
            ("Server", $"{AlpacaEndpoints.ServerName} {RoofProductVersion.Describe(typeof(AlpacaSetupPage).Assembly)}"),
            ("Device", $"{AlpacaDome.DeviceTypeName} 0: {AlpacaDome.DeviceName}"),
            ("UniqueID", ids.Dome),
            ("Commands", "Disabled. The server reads the roof and stops it (AbortSlew); it never opens or closes it."),
            ("Roof now", roofNow),
            ("Controller status", Describe(roof.FeedState)),
            ("Connected clients", connections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Alpaca port", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("Discovery", options.Discovery.Enabled ? $"answered on UDP {options.Discovery.Port}, with port {options.AdvertisedPort}" : "off"),
            ("Driver version", dome.DriverVersion),
        };

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>").Append(HtmlEncode(AlpacaDome.DeviceName)).Append(": Alpaca setup</title><style>")
            .Append("body{margin:0;padding:1.5rem;font-family:system-ui,sans-serif;background:").Append(RoofUiPalette.Background)
            .Append(";color:").Append(RoofUiPalette.Text).Append(";}")
            .Append("main{max-width:48rem;margin:0 auto;background:").Append(RoofUiPalette.Surface)
            .Append(";border:1px solid ").Append(RoofUiPalette.Border).Append(";border-radius:.5rem;padding:1.25rem;}")
            .Append("h1{font-size:1.4rem;margin:0 0 1rem;}")
            .Append("table{border-collapse:collapse;width:100%;}")
            .Append("th,td{text-align:left;vertical-align:top;padding:.45rem .5rem;border-top:1px solid ").Append(RoofUiPalette.Border).Append(";}")
            .Append("th{color:").Append(RoofUiPalette.Muted).Append(";font-weight:600;width:11rem;}")
            .Append("td{overflow-wrap:anywhere;}")
            .Append("p{color:").Append(RoofUiPalette.Muted).Append(";margin:1rem 0 0;}")
            .Append("</style></head><body><main><h1>").Append(HtmlEncode(AlpacaDome.DeviceName)).Append(": Alpaca server</h1><table>");
        foreach (var (name, value) in rows)
        {
            html.Append("<tr><th scope=\"row\">").Append(HtmlEncode(name)).Append("</th><td>").Append(HtmlEncode(value)).Append("</td></tr>");
        }

        html.Append("</table><p>This page only reads. The server's settings (Alpaca__*) are changed where it is installed: ")
            .Append("see docs/alpaca.md in the HVO.RoofController repository.</p></main></body></html>");

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; form-action 'none'; base-uri 'none'";
        return context.Response.WriteAsync(html.ToString(), context.RequestAborted);
    }

    private static string Describe(RoofStatusFeedState state) => state switch
    {
        RoofStatusFeedState.Connected => "connected to the controller's status hub",
        RoofStatusFeedState.Connecting => "connecting to the controller",
        RoofStatusFeedState.Reconnecting => "not connected: trying again",
        RoofStatusFeedState.Unauthorized => "the controller refused the server's API key",
        _ => "stopped",
    };
}
