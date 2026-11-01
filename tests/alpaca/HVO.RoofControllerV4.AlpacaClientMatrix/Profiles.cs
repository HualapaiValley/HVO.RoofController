using System.Diagnostics;
using System.Text.Json.Nodes;
using ASCOM.Alpaca.Clients;
using ASCOM.Common.Alpaca;
using ASCOM.Common.DeviceInterfaces;

namespace HVO.RoofControllerV4.AlpacaClientMatrix;

/// <summary>One command's answer time, as the client saw it.</summary>
/// <param name="Profile">The client profile.</param>
/// <param name="Step">The step of the session it belongs to.</param>
/// <param name="Member">The Alpaca member.</param>
/// <param name="Milliseconds">From the call to its return (or its exception), through the library and the proxy.</param>
/// <param name="Outcome">"accepted", or the Alpaca error number and the start of its message.</param>
internal sealed record CommandTiming(string Profile, string Step, string Member, double Milliseconds, string Outcome);

/// <summary>
/// The library's response timeouts, as <c>AlpacaConfiguration</c>'s defaults set them (seconds), and its retries. The
/// matrix runs with these, since its question is what a client built on the library does by default.
/// </summary>
internal static class LibraryDefaults
{
    public const int EstablishConnectionTimeout = 5;
    public const int StandardResponseTimeout = 10;
    public const int LongResponseTimeout = 100;
    public const int NumberOfRetries = 1;
}

/// <summary>A client session of one profile: the library's <see cref="AlpacaDome"/>, timed and checked.</summary>
internal sealed class ClientSession(string profile, AlpacaDome dome, Rig rig, List<CommandTiming> timings)
{
    public AlpacaDome Dome => dome;

    /// <summary>Calls a command, records its answer time, and fails when it throws.</summary>
    public void Command(string step, string member, Action call)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            call();
        }
        catch (ASCOM.DriverException error)
        {
            Record(step, member, clock, $"{Alpaca(error.Number)}: {Start(error.Message)}");
            throw new MatrixFailure($"{profile}, {step}: {member} failed with {Alpaca(error.Number)}: {error.Message}");
        }

        Record(step, member, clock, "accepted");
    }

    /// <summary>Calls a command that must be refused with InvalidOperation (0x40B) and <paramref name="contains"/> in its words.</summary>
    public string Refused(string step, string member, Action call, string contains)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            call();
        }
        catch (ASCOM.InvalidOperationException error) when (Alpaca(error.Number) == "0x40B" && error.Message.Contains(contains, StringComparison.Ordinal))
        {
            Record(step, member, clock, $"{Alpaca(error.Number)}: {Start(error.Message)}");
            return error.Message;
        }
        catch (ASCOM.DriverException error)
        {
            Record(step, member, clock, $"{Alpaca(error.Number)}: {Start(error.Message)}");
            throw new MatrixFailure($"{profile}, {step}: {member} answered {Alpaca(error.Number)} ({error.GetType().Name}): {error.Message}; expected 0x40B with \"{contains}\"");
        }

        Record(step, member, clock, "accepted");
        throw new MatrixFailure($"{profile}, {step}: {member} was accepted; expected 0x40B with \"{contains}\"");
    }

    public void Expect(ShutterState expected, string why)
    {
        var shutter = dome.ShutterStatus;
        if (shutter != expected)
        {
            throw new MatrixFailure($"{profile}, {why}: ShutterStatus reads {shutter}, expected {expected}");
        }
    }

    /// <summary>Polls ShutterStatus, as NINA does, until it reads <paramref name="expected"/> with Slewing false.</summary>
    public Task PollAsync(ShutterState expected, string why, double seconds = 60)
        => Rig.WaitAsync(() => Task.FromResult(dome.ShutterStatus == expected && !dome.Slewing), $"{profile}, {why}: ShutterStatus {expected}", seconds);

    public async Task StillAsync(string why) => await rig.StillAsync($"{profile}, {why}");

    private void Record(string step, string member, Stopwatch clock, string outcome)
    {
        var timing = new CommandTiming(profile, step, member, Math.Round(clock.Elapsed.TotalMilliseconds, 1), outcome);
        timings.Add(timing);
        Console.WriteLine($"  {profile}: {step}: {member} answered in {timing.Milliseconds:0} ms, {outcome}");
    }

    /// <summary>
    /// The Alpaca error number of the library's exception, which carries it as the COM error (0x80040400 plus the Alpaca
    /// number's low bits: InvalidOperation 0x40B is 0x8004040B).
    /// </summary>
    private static string Alpaca(int number) => $"0x{number & 0xFFF:X}";

    private static string Start(string message) => message.Length <= 120 ? message : message[..120] + "...";
}

/// <summary>The two client profiles of the matrix.</summary>
internal static class Profiles
{
    public const string DynamicClient = "Dynamic Client (NINA through it)";
    public const string NinaNative = "NINA native";

    /// <summary>
    /// The library's dome as a client builds it: through the capturing proxy on <paramref name="port"/>, with the library's
    /// default timeouts and retries, and a user name and password (HTTP Basic) or none.
    /// </summary>
    public static AlpacaDome Dome(int port, uint clientNumber, string userName, string password, string profile)
        => new(ServiceType.Http, "127.0.0.1", port, 0,
            establishConnectionTimeout: LibraryDefaults.EstablishConnectionTimeout,
            standardDeviceResponseTimeout: LibraryDefaults.StandardResponseTimeout,
            longDeviceResponseTimeout: LibraryDefaults.LongResponseTimeout,
            clientNumber: clientNumber,
            userName: userName,
            password: password,
            strictCasing: true,
            logger: null,
            userAgentProductName: "HVO-AlpacaClientMatrix",
            userAgentProductVersion: profile == DynamicClient ? "dynamic-client" : "nina-native",
            trustUserGeneratedSslCertificates: false,
            request100Continue: false,
            numberOfRetries: LibraryDefaults.NumberOfRetries,
            uniqueId: string.Empty);

    /// <summary>
    /// The ASCOM Alpaca Dynamic Client, as NINA uses it (the owner's decision, #220): the library's dome with the External
    /// key as both the user name and the password. A NINA-like session: connect; Error recovery (close first); open; park
    /// the mount, then close; a refusal while the weather is unsafe; a person's Open grant, after which the client's next
    /// OpenShutter goes; AbortSlew during that motion; disconnect and connect again; and Error recovery again.
    /// </summary>
    public static async Task DynamicClientAsync(ClientSession session, Rig rig, string instance)
    {
        var dome = session.Dome;
        await rig.BeginAsync("part way open, so the shutter reads Error", 0.6);
        session.Command("connect", "Connected", () => dome.Connected = true);
        if (!dome.Connected)
        {
            throw new MatrixFailure($"{DynamicClient}: Connected reads false after connecting");
        }

        // The Alpaca server behind the proxy is this run's, on the HAT emulator.
        var action = dome.Action("status", string.Empty);
        if (!action.Contains($"(instance {instance}, HAT Emulated)", StringComparison.Ordinal))
        {
            throw new MatrixFailure($"{DynamicClient}: the Alpaca server's controller is not this run's emulated one; nothing is commanded");
        }

        // NINA's Error recovery: a shutter in Error is closed before anything else.
        session.Expect(ShutterState.Error, "a roof part way");
        session.Command("Error recovery", "CloseShutter", dome.CloseShutter);
        session.Expect(ShutterState.Closing, "straight after CloseShutter");
        await session.PollAsync(ShutterState.Closed, "Error recovery");
        await rig.SettledAsync("Error recovery");

        session.Command("open", "OpenShutter", dome.OpenShutter);
        session.Expect(ShutterState.Opening, "straight after OpenShutter");
        await session.PollAsync(ShutterState.Open, "open");
        await rig.SettledAsync("open");

        // Park, then close: the mount unparked while observing, as the controller's poll sees; the client parks it (here
        // through the emulator, as NINA parks it through its mount) and closes at once. The Close's own re-read sees it parked.
        await rig.EmulatorAsync("alpaca/telescope", new { device = 0, parked = false });
        await Rig.WaitAsync(async () => (string?)(await rig.SourceAsync(Rig.Scope))["state"] == "Unsafe", "the controller to read the mount unparked");
        await rig.EmulatorAsync("alpaca/telescope", new { device = 0, parked = true });
        session.Command("park, then close", "CloseShutter", dome.CloseShutter);
        session.Expect(ShutterState.Closing, "straight after CloseShutter");
        await session.PollAsync(ShutterState.Closed, "park, then close");
        await rig.SettledAsync("park, then close");

        // Unsafe weather: the client sees InvalidOperation with the controller's reason, and nothing moves.
        await rig.EmulatorAsync("alpaca/safetymonitor", new { device = 0, safe = false });
        await Rig.WaitAsync(async () => (string?)(await rig.SourceAsync(Rig.Weather))["state"] == "Unsafe", "the controller to read the weather unsafe");
        var before = (await rig.AlertsAsync()).ToDictionary(alert => (string)alert!["id"]!, alert => (int)alert!["count"]!);
        session.Refused("unsafe weather", "OpenShutter", dome.OpenShutter, $"Weather monitor '{Rig.Weather}' reports unsafe weather");
        session.Expect(ShutterState.Closed, "after the refused OpenShutter");
        await session.StillAsync("a refused OpenShutter");

        // A person grants the refused Open's reasons through the session API; the client's next OpenShutter goes.
        var alert = (await rig.AlertsAsync()).FirstOrDefault(alert => (string?)alert!["kind"] == "Refused" && (string?)alert!["command"] == "Open"
            && (int)alert!["count"]! > before.GetValueOrDefault((string)alert!["id"]!))
            ?? throw new MatrixFailure($"{DynamicClient}: no alert for the refused OpenShutter");
        var keys = alert["reasonKeys"]!.AsArray().Select(key => (string)key!).ToArray();
        if (keys.SequenceEqual([$"weather-unsafe:{Rig.Weather}"]) is false)
        {
            throw new MatrixFailure($"{DynamicClient}: the refused OpenShutter's alert names {string.Join(", ", keys)}");
        }

        await rig.GrantAsync("Open", keys);
        session.Command("after a person's grant", "OpenShutter", dome.OpenShutter);
        session.Expect(ShutterState.Opening, "straight after the granted OpenShutter");
        if ((await rig.StatusAsync())["alpaca"]!["grants"]!.AsArray().Count > 0)
        {
            throw new MatrixFailure($"{DynamicClient}: the Open grant was not used up");
        }

        // AbortSlew during that motion: the roof stops part way, and the shutter reads Error.
        await Rig.WaitAsync(async () => (double)(await rig.PlantAsync())["positionMeters"]! > 0.3, "the roof to open 0.3 m");
        session.Command("during a motion", "AbortSlew", dome.AbortSlew);
        await Rig.WaitAsync(() => Task.FromResult(!dome.Slewing), $"{DynamicClient}: Slewing false after AbortSlew", 10);
        session.Expect(ShutterState.Error, "after AbortSlew part way");
        await rig.SettledAsync("AbortSlew");
        await rig.EmulatorAsync("alpaca/safetymonitor", new { device = 0, safe = true });

        // Disconnect and connect again: reads need the connection, and nothing moves.
        session.Command("disconnect", "Disconnect", dome.Disconnect);
        try
        {
            _ = dome.ShutterStatus;
            throw new MatrixFailure($"{DynamicClient}: ShutterStatus answered while disconnected");
        }
        catch (ASCOM.NotConnectedException)
        {
        }

        session.Command("reconnect", "Connect", dome.Connect);
        await Rig.WaitAsync(() => Task.FromResult(!dome.Connecting), $"{DynamicClient}: Connecting false", 10);
        if (!dome.Connected)
        {
            throw new MatrixFailure($"{DynamicClient}: Connected reads false after Connect");
        }

        // Error recovery again, after the reconnect: the roof part way is closed.
        session.Expect(ShutterState.Error, "after reconnecting, the roof part way");
        session.Command("Error recovery after reconnecting", "CloseShutter", dome.CloseShutter);
        await session.PollAsync(ShutterState.Closed, "Error recovery after reconnecting");
        await rig.SettledAsync("Error recovery after reconnecting");
        session.Command("end", "Connected", () => dome.Connected = false);
    }

    /// <summary>
    /// NINA's own Alpaca client, which has no place for a user name and password: the library's dome with no credential.
    /// Reads, Connected and AbortSlew work; OpenShutter and CloseShutter are not authorised, and nothing moves.
    /// </summary>
    public static async Task NinaNativeAsync(ClientSession session, Rig rig)
    {
        var dome = session.Dome;
        await rig.BeginAsync("part way open, so both an Open and a Close would move it", 0.3);
        session.Command("connect", "Connected", () => dome.Connected = true);
        if (!dome.Connected || dome.Name != "HVO roll-off roof" || !dome.CanSetShutter || dome.Slewing)
        {
            throw new MatrixFailure($"{NinaNative}: the reads after connecting are not the roof's");
        }

        session.Expect(ShutterState.Error, "a roof part way");
        session.Command("idle", "AbortSlew", dome.AbortSlew);
        session.Refused("without a credential", "OpenShutter", dome.OpenShutter, "Not authorised");
        await session.StillAsync("an OpenShutter not authorised");
        session.Refused("without a credential", "CloseShutter", dome.CloseShutter, "Not authorised");
        await session.StillAsync("a CloseShutter not authorised");
        session.Expect(ShutterState.Error, "after the refused commands");
        session.Command("end", "Connected", () => dome.Connected = false);
        await rig.SettledAsync(NinaNative);
    }

    /// <summary>The controller's answer deadline for an Alpaca command with no direction-change delay left (milliseconds).</summary>
    public static double AnswerDeadline(JsonNode status) => (double)status["alpaca"]!["rereadBudgetMilliseconds"]! + 400;
}
