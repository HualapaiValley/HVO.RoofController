using System.Text.Json;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Alpaca.Roof;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Common.Alpaca;

namespace HVO.RoofControllerV4.Alpaca;

/// <summary>
/// The Alpaca server's routes: the management API, the setup pages and the device API (Dome 0 and SafetyMonitor 0). Any
/// other path under
/// <c>/api</c> or <c>/management</c>, an unknown device or member, or a member with the wrong HTTP method is a malformed
/// request (HTTP 400, plain text); anything else is not found (404).
/// </summary>
internal static class AlpacaEndpoints
{
    /// <summary>The Alpaca API versions this server speaks.</summary>
    public static readonly IReadOnlyList<int> ApiVersions = [1];

    public const string ServerName = AlpacaServerIdentity.ServerName;

    public const string Manufacturer = "Hualapai Valley Observatory";

    /// <summary>The device route's value naming the member.</summary>
    public const string MemberRouteValue = "member";

    /// <summary>The device route's value naming the device type.</summary>
    public const string DeviceTypeRouteValue = "deviceType";

    public static void MapAlpaca(this WebApplication app)
    {
        app.MapGet("/management/apiversions", (HttpContext context, AlpacaTransactions transactions) => ManagementAsync(context, transactions, writer =>
        {
            writer.WriteStartArray();
            foreach (var version in ApiVersions)
            {
                writer.WriteNumberValue(version);
            }

            writer.WriteEndArray();
        }));

        app.MapGet("/management/v1/description", (HttpContext context, AlpacaTransactions transactions, AlpacaOptions options) => ManagementAsync(context, transactions, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("ServerName", ServerName);
            writer.WriteString("Manufacturer", Manufacturer);
            writer.WriteString("ManufacturerVersion", RoofProductVersion.WithoutCommit(RoofProductVersion.Of(typeof(AlpacaEndpoints).Assembly)));
            writer.WriteString("Location", options.Location);
            writer.WriteEndObject();
        }));

        app.MapGet("/management/v1/configureddevices", (HttpContext context, AlpacaTransactions transactions, AlpacaDeviceIds ids) => ManagementAsync(context, transactions, writer =>
        {
            writer.WriteStartArray();
            WriteDevice(writer, AlpacaDome.DeviceName, AlpacaDome.DeviceTypeName, ids.Dome);
            WriteDevice(writer, AlpacaSafetyMonitor.DeviceName, AlpacaSafetyMonitor.DeviceTypeName, ids.SafetyMonitor);
            writer.WriteEndArray();
        }));

        app.MapGet("/setup", AlpacaSetupPage.WriteAsync);
        app.MapGet("/setup/v1/{deviceType}/{deviceNumber}/setup", (HttpContext context, string deviceType, string deviceNumber) =>
            deviceType is AlpacaDome.DeviceType or AlpacaSafetyMonitor.DeviceType && deviceNumber == "0"
                ? AlpacaSetupPage.WriteAsync(context)
                : AlpacaResponse.BadRequestAsync(context, "This server has two devices: Dome 0 (/setup/v1/dome/0/setup) and SafetyMonitor 0 (/setup/v1/safetymonitor/0/setup)."));

        app.MapMethods("/api/v{version}/{deviceType}/{deviceNumber}/{member}", [HttpMethods.Get, HttpMethods.Put], DeviceAsync);

        app.MapFallback(context => context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/management")
            ? AlpacaResponse.BadRequestAsync(context, "This server has no such Alpaca endpoint.")
            : Security.AlpacaAccessFilter.Text(context, StatusCodes.Status404NotFound, "Not found. The Alpaca API is under /api/v1/dome/0, /api/v1/safetymonitor/0 and /management; the setup page is /setup."));
    }

    private static void WriteDevice(Utf8JsonWriter writer, string name, string type, string uniqueId)
    {
        writer.WriteStartObject();
        writer.WriteString("DeviceName", name);
        writer.WriteString("DeviceType", type);
        writer.WriteNumber("DeviceNumber", 0);
        writer.WriteString("UniqueID", uniqueId);
        writer.WriteEndObject();
    }

    private static async Task ManagementAsync(HttpContext context, AlpacaTransactions transactions, AlpacaValueWriter value)
    {
        AlpacaRequest request;
        try
        {
            request = await AlpacaRequest.ReadAsync(context).ConfigureAwait(false);
        }
        catch (AlpacaBadRequestException ex)
        {
            await AlpacaResponse.BadRequestAsync(context, ex.Message).ConfigureAwait(false);
            return;
        }

        await AlpacaResponse.WriteManagementAsync(context, request.ClientTransactionId, transactions.Next(), value).ConfigureAwait(false);
    }

    private static async Task DeviceAsync(
        HttpContext context,
        string version,
        string deviceType,
        string deviceNumber,
        string member,
        AlpacaDome dome,
        AlpacaSafetyMonitor safetyMonitor,
        AlpacaTransactions transactions)
    {
        // The Alpaca API's paths are lower case: a version, device type or member cased otherwise is not one. (Routing
        // matches the "v" whatever its case.)
        if (version != "1" || !context.Request.Path.StartsWithSegments("/api/v1", StringComparison.Ordinal))
        {
            await AlpacaResponse.BadRequestAsync(context, "This server speaks Alpaca API version 1: its paths begin /api/v1/.").ConfigureAwait(false);
            return;
        }

        AlpacaDevice? device = deviceNumber != "0" ? null : deviceType switch
        {
            AlpacaDome.DeviceType => dome,
            AlpacaSafetyMonitor.DeviceType => safetyMonitor,
            _ => null,
        };
        if (device is null)
        {
            await AlpacaResponse.BadRequestAsync(context, $"This server has no device {Shorten(deviceType)}/{Shorten(deviceNumber)}; it has dome/0 and safetymonitor/0.").ConfigureAwait(false);
            return;
        }

        try
        {
            var request = await AlpacaRequest.ReadAsync(context).ConfigureAwait(false);
            var result = await device.InvokeAsync(member, request, context.RequestAborted).ConfigureAwait(false);
            if (result is null)
            {
                await AlpacaResponse.BadRequestAsync(context, $"{device.TypeName} has no member '{Shorten(member)}' for HTTP {context.Request.Method}.").ConfigureAwait(false);
                return;
            }

            await AlpacaResponse.WriteAsync(context, request.ClientTransactionId, transactions.Next(), result.Value).ConfigureAwait(false);
        }
        catch (AlpacaBadRequestException ex)
        {
            await AlpacaResponse.BadRequestAsync(context, ex.Message).ConfigureAwait(false);
        }
    }

    private static string Shorten(string text) => text.Length <= 32 ? text : text[..32] + "…";
}
