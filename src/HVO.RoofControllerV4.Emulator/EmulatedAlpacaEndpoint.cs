using System.Text.Json;
using HVO.RoofControllerV4.Simulation.Safety;

namespace HVO.RoofControllerV4.Emulator;

/// <summary>Changes an emulated mount. Null leaves a setting as it is; <c>Device</c> defaults to 0.</summary>
public sealed record AlpacaTelescopeRequest(int Device = 0, bool? Parked = null, bool? Connected = null, EmulatedAlpacaBehaviour? Behaviour = null, bool? CanPark = null);

/// <summary>Changes an emulated safety monitor. Null leaves a setting as it is; <c>Device</c> defaults to 0.</summary>
public sealed record AlpacaSafetyMonitorRequest(int Device = 0, bool? Safe = null, bool? Connected = null, EmulatedAlpacaBehaviour? Behaviour = null);

/// <summary>
/// The emulator's ASCOM Alpaca devices on its control port (<see cref="EmulatedAlpacaServer"/>): the Alpaca Device API
/// under <c>/api/v1</c> and the Management API under <c>/management</c>, for the controller's safety sources, and the
/// control API calls that park or unpark a mount, make the weather unsafe, disconnect a device or make it fail. Like the
/// rest of the control port it has no authentication and answers no Alpaca discovery: keep it on loopback or a private
/// test network.
/// </summary>
public static class EmulatedAlpacaEndpoint
{
    /// <summary>The control API's Alpaca calls, under <see cref="EmulatorApi.Prefix"/>.</summary>
    public const string ControlPrefix = EmulatorApi.Prefix + "/alpaca";

    // The Alpaca API's names, as it writes them (PascalCase), whatever the host's JSON settings.
    private static readonly JsonSerializerOptions AlpacaJson = new() { PropertyNamingPolicy = null };

    /// <summary>Maps the Device API, the Management API and the control API's Alpaca calls.</summary>
    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapMethods("/api/v1/{deviceType}/{deviceNumber}/{member}", ["GET", "PUT"], AnswerAsync);
        app.MapGet("/management/{**path}", (string path, HttpContext context, EmulatedAlpacaServer alpaca) =>
        {
            if (!IsLowerCase(context.Request.Path))
            {
                return Results.Text("Alpaca URLs are lower case.", statusCode: StatusCodes.Status400BadRequest);
            }

            return alpaca.AnswerManagement(path, Query(context)) is { } answer
                ? Write(answer)
                : Results.Text($"The management path '{path}' is not served here.", statusCode: StatusCodes.Status400BadRequest);
        });

        var control = app.MapGroup(ControlPrefix);
        control.MapGet(string.Empty, (EmulatedAlpacaServer alpaca) => alpaca.GetStatus());
        control.MapPost("/telescope", (AlpacaTelescopeRequest request, EmulatedAlpacaServer alpaca)
            => Apply(alpaca, () => alpaca.SetTelescope(request.Device, request.Parked, request.Connected, request.Behaviour, request.CanPark)));
        control.MapPost("/safetymonitor", (AlpacaSafetyMonitorRequest request, EmulatedAlpacaServer alpaca)
            => Apply(alpaca, () => alpaca.SetSafetyMonitor(request.Device, request.Safe, request.Connected, request.Behaviour)));
        control.MapPost("/reset", (EmulatedAlpacaServer alpaca) => Apply(alpaca, alpaca.Reset));
    }

    private static async Task<IResult> AnswerAsync(string deviceType, string deviceNumber, string member, HttpContext context, EmulatedAlpacaServer alpaca)
    {
        // Routing matches whatever the case; the Alpaca API's URLs are lower case only.
        if (!IsLowerCase(context.Request.Path))
        {
            return Results.Text("Alpaca URLs are lower case.", statusCode: StatusCodes.Status400BadRequest);
        }

        IEnumerable<KeyValuePair<string, string?>> parameters;
        if (HttpMethods.IsPut(context.Request.Method))
        {
            // A PUT's parameters are form-encoded in its body (application/x-www-form-urlencoded).
            if (!context.Request.HasFormContentType)
            {
                return Results.Text("An Alpaca PUT carries its parameters form-encoded.", statusCode: StatusCodes.Status400BadRequest);
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            parameters = form.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value.FirstOrDefault()));
        }
        else
        {
            parameters = Query(context);
        }

        var answer = alpaca.Answer(context.Request.Method, deviceType, deviceNumber, member, parameters);
        if (answer.Hang)
        {
            // A device that stopped responding: nothing comes back until the client gives up (or the host stops).
            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            context.Abort();
            return Results.Empty;
        }

        return Write(answer);
    }

    private static IResult Write(EmulatedAlpacaAnswer answer) => answer.Json is { } json
        ? Results.Json(json, AlpacaJson, statusCode: answer.StatusCode)
        : Results.Text(answer.Text ?? string.Empty, "text/plain", statusCode: answer.StatusCode);

    private static IResult Apply(EmulatedAlpacaServer alpaca, Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid emulator request");
        }

        return Results.Ok(alpaca.GetStatus());
    }

    private static IEnumerable<KeyValuePair<string, string?>> Query(HttpContext context)
        => context.Request.Query.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value.FirstOrDefault()));

    private static bool IsLowerCase(PathString path) => path.Value is { } value && value == value.ToLowerInvariant();
}
