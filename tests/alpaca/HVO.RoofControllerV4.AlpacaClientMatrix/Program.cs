using System.Net;
using System.Text.Json;
using HVO.RoofControllerV4.AlpacaClientMatrix;

// The Alpaca client matrix (#220, phase 4): the roof's Alpaca server, driven by ASCOM's own Alpaca client library
// (ASCOM.Alpaca.Components, whose AlpacaDome the ASCOM Alpaca Dynamic Client and NINA's native Alpaca devices are built
// on), in two profiles: the Dynamic Client with the External key as its user name and password (how NINA reaches the
// roof, the owner's decision), and NINA's native client, which sends no credential. Each goes through a capturing proxy on
// loopback, which shows whether the client sent HTTP Basic on every request, the first included (pre-emptively), and that
// the server never challenged it. It runs only against this run's loopback processes on the HAT emulator, after
// tests/alpaca/conformu.sh's chain guard: never against the roof. Keys come from files and are never printed or kept.
//
// Usage: HVO.RoofControllerV4.AlpacaClientMatrix --alpaca URL --controller URL --emulator URL --token FILE
//        --external-key FILE --proxy-port PORT --proxy-port PORT --results FILE

try
{
    var options = ParseArguments(args);
    var alpaca = Loopback(options, "alpaca");
    var controller = Loopback(options, "controller");
    var emulator = Loopback(options, "emulator");
    var token = File.ReadAllText(Single(options, "token")).Trim();
    var key = File.ReadAllText(Single(options, "external-key")).Trim();
    var ports = options.GetValueOrDefault("proxy-port", []).Select(int.Parse).ToArray();
    if (ports.Length != 2)
    {
        throw new MatrixFailure("Give two --proxy-port values, one for each client profile");
    }

    using var rig = new Rig(controller, emulator, token);
    var status = await rig.StatusAsync();
    if ((string?)status["hatMode"] != "Emulated")
    {
        throw new MatrixFailure("The controller does not drive the HAT emulator; nothing is commanded");
    }

    var instance = (string)status["controllerInstanceId"]!;
    var deadline = Profiles.AnswerDeadline(status);
    var timings = new List<CommandTiming>();
    var results = new Dictionary<string, object>();

    foreach (var (profile, port) in new[] { (Profiles.DynamicClient, ports[0]), (Profiles.NinaNative, ports[1]) })
    {
        Console.WriteLine($"Client profile: {profile}");
        await using var proxy = await CapturingProxy.StartAsync(port, alpaca, key);
        var withKey = profile == Profiles.DynamicClient;
        using (var dome = Profiles.Dome(port, withKey ? 31u : 32u, withKey ? key : string.Empty, withKey ? key : string.Empty, profile))
        {
            var session = new ClientSession(profile, dome, rig, timings);
            if (withKey)
            {
                await Profiles.DynamicClientAsync(session, rig, instance);
            }
            else
            {
                await Profiles.NinaNativeAsync(session, rig);
            }
        }

        var requests = proxy.Requests;
        var challenged = requests.Count(request => request.Challenged);
        if (requests.Count == 0 || challenged > 0)
        {
            throw new MatrixFailure($"{profile}: the proxy saw {requests.Count} requests, {challenged} of them challenged by the server");
        }

        if (withKey && requests.Any(request => !request.BasicWithKey))
        {
            var first = requests.Select((request, index) => (request, index)).First(pair => !pair.request.BasicWithKey);
            throw new MatrixFailure($"{profile}: request {first.index + 1} ({first.request.Method} {first.request.Path}) did not carry HTTP Basic with the External key: it is not sent pre-emptively");
        }

        if (!withKey && requests.Any(request => request.Authorization))
        {
            throw new MatrixFailure($"{profile}: a request carried an Authorization header");
        }

        // What the library sent for each member, so a member the session called is seen on the wire.
        var members = requests.GroupBy(request => $"{request.Method} {request.Path}").OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count());
        var sent = withKey
            ? new[] { "connected", "connect", "disconnect", "openshutter", "closeshutter", "abortslew", "shutterstatus" }
            : new[] { "connected", "openshutter", "closeshutter", "abortslew", "shutterstatus" };
        foreach (var member in sent)
        {
            if (!members.Keys.Any(key => key.EndsWith("/api/v1/dome/0/" + member, StringComparison.Ordinal)))
            {
                throw new MatrixFailure($"{profile}: the library sent no {member} request");
            }
        }

        Console.WriteLine(withKey
            ? $"  {profile}: all {requests.Count} requests carried HTTP Basic with the External key, the first ({requests[0].Method} {requests[0].Path}) included; none was challenged"
            : $"  {profile}: none of its {requests.Count} requests carried a credential; none was challenged");
        results[profile] = new
        {
            Requests = requests.Count,
            WithBasicKey = requests.Count(request => request.BasicWithKey),
            WithAuthorization = requests.Count(request => request.Authorization),
            FirstRequest = $"{requests[0].Method} {requests[0].Path}",
            Challenged = challenged,
            Members = members
        };
    }

    // Each command's answer against the controller's deadline (the re-read budget, no direction-change delay left in this
    // run, and 400 ms), the Alpaca server's own wait (that plus 1 s) and the library's standard response timeout.
    var serverWait = deadline + 1000;
    var timeout = LibraryDefaults.StandardResponseTimeout * 1000.0;
    var commands = timings.Where(timing => timing.Member is "OpenShutter" or "CloseShutter" or "AbortSlew").ToList();
    foreach (var timing in commands)
    {
        if (timing.Milliseconds >= timeout)
        {
            throw new MatrixFailure($"{timing.Profile}, {timing.Step}: {timing.Member} took {timing.Milliseconds:0} ms, past the library's {timeout:0} ms timeout (it would retry)");
        }
    }

    var slowest = commands.MaxBy(timing => timing.Milliseconds)!;
    Console.WriteLine($"Answer times: the slowest command, {slowest.Member} ({slowest.Profile}, {slowest.Step}), took {slowest.Milliseconds:0} ms; "
        + $"the controller's deadline is {deadline:0} ms, the Alpaca server waits {serverWait:0} ms, the library's standard timeout is {timeout:0} ms");

    var output = new
    {
        Library = "ASCOM.Alpaca.Components (AlpacaDome)",
        LibraryTimeoutsSeconds = new
        {
            LibraryDefaults.EstablishConnectionTimeout,
            LibraryDefaults.StandardResponseTimeout,
            LibraryDefaults.LongResponseTimeout
        },
        LibraryDefaults.NumberOfRetries,
        ControllerDeadlineMilliseconds = deadline,
        AlpacaServerWaitMilliseconds = serverWait,
        Profiles = results,
        Commands = timings
    };
    await File.WriteAllTextAsync(Single(options, "results"), JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("The client matrix passed");
    return 0;
}
catch (MatrixFailure failure)
{
    Console.Error.WriteLine($"::error::Client matrix: {failure.Message}");
    return 1;
}

static Dictionary<string, List<string>> ParseArguments(string[] args)
{
    var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
        {
            throw new MatrixFailure($"Unexpected argument {args[i]}; see the usage at the top of Program.cs");
        }

        options.TryAdd(args[i][2..], []);
        options[args[i][2..]].Add(args[i + 1]);
    }

    return options;
}

static string Single(Dictionary<string, List<string>> options, string name)
    => options.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : throw new MatrixFailure($"Give --{name} once");

// A URL whose host is a loopback address, written as an address (a name, even localhost, could resolve elsewhere).
static Uri Loopback(Dictionary<string, List<string>> options, string name)
{
    var uri = new Uri(Single(options, name));
    if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address))
    {
        throw new MatrixFailure($"The {name} URL is not a loopback address; nothing is commanded");
    }

    return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
}
