using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Drive;
using HVO.RoofControllerV4.Simulation.Emulator;
using HVO.RoofControllerV4.Simulation.Hat;
using HVO.RoofControllerV4.Simulation.LimitSwitches;

namespace HVO.RoofControllerV4.Emulator;

public enum LimitSide
{
    Open,
    Closed
}

// The fields below that are nullable without a default are required: a missing one is refused with 400, never read
// as false or the enum's first value.

public sealed record ResetRequest(double? PositionMeters = null, WiringFault? Wiring = null);

public sealed record TimeScaleRequest(double? Scale);

public sealed record DriveTripRequest(SmVectorTrip Trip = SmVectorTrip.External);

public sealed record PowerRequest(bool? Powered);

public sealed record ExternalStopRequest(bool? Open);

public sealed record JamRequest(bool? Jammed);

public sealed record LimitFaultRequest(LimitSide? Limit, LimitSwitchFault? Fault);

public sealed record RelayFaultRequest(int? Relay, RelayContactFault? Fault);

public sealed record WiringRequest(WiringFault? Wiring);

/// <summary>Bus failures of the emulated I2C transactions (each answered with an I/O error; the connection stays up). Null leaves a setting unchanged.</summary>
public sealed record BusFaultRequest(
    bool? FailReads = null,
    bool? FailWrites = null,
    bool? FailInputReads = null,
    int? FailNextReads = null,
    int? FailNextWrites = null,
    int? FailNextInputReads = null);

/// <summary>Faults of the TCP link itself. Null leaves a setting unchanged; <c>Disconnect</c> drops the open connections once.</summary>
public sealed record LinkRequest(bool? Outage = null, double? ResponseDelayMilliseconds = null, bool Disconnect = false);

/// <summary>The register link's state.</summary>
public sealed record LinkStatus(string RegisterEndpoint, bool Outage, double ResponseDelayMilliseconds, int OpenConnections, long AcceptedConnections, long Requests);

public sealed record EmulatorStatusResponse(HatEmulatorStatus Plant, LinkStatus Link);

/// <summary>One plant event: <c>At</c> is plant time since the last reset.</summary>
public sealed record PlantEventResponse(TimeSpan At, PlantEventKind Kind, string Detail);

/// <summary>
/// The control API, under <c>/api/emulator</c>. It changes the emulated plant and link only; nothing here reaches
/// hardware. It has no authentication: keep the control port on loopback (the default) or on a private test network.
/// </summary>
internal static class EmulatorApi
{
    public const string Prefix = "/api/emulator";
    private const int MaxHistory = 5000;

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup(Prefix);

        api.MapGet("/status", Status);
        api.MapGet("/history", (HatEmulatorSession session, int? limit) =>
        {
            var take = Math.Clamp(limit ?? 200, 1, MaxHistory);
            var history = session.Plant.History;
            return history.Skip(Math.Max(0, history.Count - take)).Select(e => new PlantEventResponse(e.At, e.Kind, e.Detail)).ToArray();
        });
        api.MapGet("/violations", (HatEmulatorSession session) => session.Plant.Violations);

        // Both reset fields are optional, and so is the body.
        api.MapPost("/reset", (ResetRequest? request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () => session.Reset(request?.PositionMeters, request?.Wiring)));
        api.MapPost("/time-scale", (TimeScaleRequest request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () => session.Clock.Scale = Required(request.Scale, "scale")));
        api.MapPost("/drive/trip", (DriveTripRequest request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () =>
            {
                if (request.Trip == SmVectorTrip.None)
                {
                    throw new ArgumentException("Give the trip to inject; the drive's own Clear Fault input (RLY3) resets it.");
                }

                session.Plant.TripDrive(request.Trip);
            }));
        api.MapPost("/drive/power", (PowerRequest request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () => session.Plant.SetDrivePower(Required(request.Powered, "powered"))));
        api.MapPost("/hat/power", (PowerRequest request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () => session.Plant.SetHatPower(Required(request.Powered, "powered"))));
        api.MapPost("/external-stop", (ExternalStopRequest request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () => session.Plant.ExternalStopOpen = Required(request.Open, "open")));
        api.MapPost("/jam", (JamRequest request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () => session.Plant.Jammed = Required(request.Jammed, "jammed")));
        api.MapPost("/limit-fault", (LimitFaultRequest request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () => session.Plant.SetLimitFault(IsOpenLimit(Required(request.Limit, "limit")), Required(request.Fault, "fault"))));
        api.MapPost("/relay-fault", (RelayFaultRequest request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () => session.Plant.SetRelayFault(Required(request.Relay, "relay"), Required(request.Fault, "fault"))));
        api.MapPost("/wiring", (WiringRequest request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () => session.Plant.Wiring = Required(request.Wiring, "wiring")));
        api.MapPost("/bus", (BusFaultRequest request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () => ApplyBusFaults(session.Client, request)));
        api.MapPost("/link", (LinkRequest request, HatEmulatorSession session, HatEmulatorServer server)
            => Apply(session, server, () =>
            {
                if (request.ResponseDelayMilliseconds is { } delay)
                {
                    if (double.IsNaN(delay) || delay < 0 || delay > 60_000)
                    {
                        throw new ArgumentOutOfRangeException(nameof(request.ResponseDelayMilliseconds), "The response delay must be between 0 and 60000 ms.");
                    }

                    server.ResponseDelay = TimeSpan.FromMilliseconds(delay);
                }

                if (request.Outage is { } outage)
                {
                    server.Outage = outage;
                }

                if (request.Disconnect)
                {
                    server.DisconnectAll();
                }
            }));
    }

    private static EmulatorStatusResponse Status(HatEmulatorSession session, HatEmulatorServer server)
        => new(session.GetStatus(), new LinkStatus(
            server.LocalEndPoint.ToString(),
            server.Outage,
            server.ResponseDelay.TotalMilliseconds,
            server.OpenConnections,
            server.AcceptedConnections,
            server.Requests));

    /// <summary>A field the request must give.</summary>
    private static T Required<T>(T? value, string name)
        where T : struct
        => value ?? throw new ArgumentException($"The request must give \"{name}\".");

    private static bool IsOpenLimit(LimitSide limit) => limit switch
    {
        LimitSide.Open => true,
        LimitSide.Closed => false,
        _ => throw new ArgumentOutOfRangeException(nameof(limit), "The limit must be Open or Closed.")
    };

    private static IResult Apply(HatEmulatorSession session, HatEmulatorServer server, Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException ex)
        {
            // The reason alone, without the " (Parameter 'name')" the exception appends.
            var detail = ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", string.Empty, StringComparison.Ordinal);
            return Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid emulator request");
        }

        return Results.Ok(Status(session, server));
    }

    private static void ApplyBusFaults(EmulatedHatRegisterClient client, BusFaultRequest request)
    {
        foreach (var count in new[] { request.FailNextReads, request.FailNextWrites, request.FailNextInputReads })
        {
            if (count is < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), "A failure count cannot be negative.");
            }
        }

        if (request.FailReads is { } failReads)
        {
            client.FailReads = failReads;
        }

        if (request.FailWrites is { } failWrites)
        {
            client.FailWrites = failWrites;
        }

        if (request.FailInputReads is { } failInputReads)
        {
            client.FailInputReads = failInputReads;
        }

        if (request.FailNextReads is { } nextReads)
        {
            client.FailNextReads = nextReads;
        }

        if (request.FailNextWrites is { } nextWrites)
        {
            client.FailNextWrites = nextWrites;
        }

        if (request.FailNextInputReads is { } nextInputReads)
        {
            client.FailNextInputReads = nextInputReads;
        }
    }
}
