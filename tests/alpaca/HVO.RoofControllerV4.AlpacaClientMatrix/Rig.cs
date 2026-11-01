using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace HVO.RoofControllerV4.AlpacaClientMatrix;

/// <summary>A check of the matrix that failed: the message says what was expected and what happened.</summary>
internal sealed class MatrixFailure(string message) : Exception(message);

/// <summary>
/// The test rig around the client under test: the HAT emulator's control API (the roof, the mount, the weather) and the
/// controller's API with the CI person's session (status, alerts, grants), as a person would use it in a UI. Never the
/// Alpaca server: the client under test is the only one that talks to it.
/// </summary>
internal sealed class Rig(Uri controller, Uri emulator, string token) : IDisposable
{
    public const string Scope = "Main";
    public const string Weather = "Emulated weather";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<JsonNode> StatusAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(controller, "api/v4.0/RoofControl/Status"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new MatrixFailure($"The controller's status answered {(int)response.StatusCode}");
        }

        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    public async Task<JsonNode> PlantAsync() => (await GetAsync(new Uri(emulator, "api/emulator/status")))["plant"]!;

    public async Task<JsonArray> ViolationsAsync() => (await GetAsync(new Uri(emulator, "api/emulator/violations"))).AsArray();

    public async Task<JsonArray> AlertsAsync() => (await GetAsync(new Uri(controller, "api/v4.0/alpaca/alerts"), token)).AsArray();

    /// <summary>A change through the emulator's control API (<c>/api/emulator/...</c>).</summary>
    public async Task EmulatorAsync(string path, object body)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(emulator, "api/emulator/" + path), body);
        if (!response.IsSuccessStatusCode)
        {
            throw new MatrixFailure($"The emulator's {path} answered {(int)response.StatusCode}");
        }
    }

    /// <summary>A person grants one Alpaca command of <paramref name="kind"/>, lifting <paramref name="keys"/> (the session API).</summary>
    public async Task GrantAsync(string kind, IEnumerable<string> keys)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(controller, "api/v4.0/alpaca/grants"))
        {
            Content = JsonContent.Create(new { kind, reasonKeys = keys.ToArray() })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw new MatrixFailure($"The person's {kind} grant answered {(int)response.StatusCode}");
        }
    }

    public async Task<JsonNode> SourceAsync(string name)
        => (await StatusAsync())["safety"]!["sources"]!.AsArray().First(source => (string?)source!["name"] == name)!;

    /// <summary>Waits for a condition, or fails naming what it waited for.</summary>
    public static async Task WaitAsync(Func<Task<bool>> condition, string what, double seconds = 30)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            if (clock.Elapsed.TotalSeconds > seconds)
            {
                throw new MatrixFailure($"Timed out after {seconds:0} s waiting for {what}");
            }

            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Resets the emulated plant with the roof closed (or <paramref name="metres"/> open) and its mount parked and weather
    /// safe, and waits for the controller to see it: at rest, every relay off, the weather from a read made after the
    /// reset, no grant, no hold, no person's lease, Alpaca Open and Close allowed.
    /// </summary>
    public async Task BeginAsync(string what, double? metres = null)
    {
        var reading = (string?)(await SourceAsync(Weather))["lastReadingUtc"];
        await EmulatorAsync("reset", metres is { } open ? new { positionMeters = open } : new { });
        await WaitAsync(async () =>
        {
            var status = await StatusAsync();
            var sources = status["safety"]!["sources"]!.AsArray().ToDictionary(source => (string)source!["name"]!, source => source!);
            var at = metres is null
                ? (string?)status["status"] == "Closed"
                : !(bool)status["isOpenLimitActive"]! && !(bool)status["isClosedLimitActive"]!;
            return at && !(bool)status["isMoving"]! && !(bool)status["isStartPending"]! && !(bool)status["isFaultLatched"]!
                && (string?)status["relayRegisterState"] == "Verified" && (int)status["relayRegisterMask"]! == 0
                && (string?)sources[Scope]["state"] == "Safe" && (string?)sources[Weather]["state"] == "Safe"
                && (string?)sources[Weather]["lastReadingUtc"] != reading;
        }, $"the starting state ({what})");

        var alpaca = (await StatusAsync())["alpaca"]!;
        if ((bool)alpaca["hold"]!["held"]! || (bool)alpaca["operatorLease"]!["held"]! || alpaca["grants"]!.AsArray().Count > 0
            || (string?)alpaca["mode"] != "OpenAndClose")
        {
            throw new MatrixFailure($"The starting state ({what}): the Stop hold, a person's lease or a grant is there, or Alpaca Open is not allowed");
        }
    }

    /// <summary>Fails when the roof moves or a relay is on over <paramref name="seconds"/>.</summary>
    public async Task StillAsync(string why, double seconds = 1.0)
    {
        var before = await PlantAsync();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        var after = await PlantAsync();
        if (Math.Abs((double)after["positionMeters"]! - (double)before["positionMeters"]!) > 0.001 || (int)after["relayRegister"]! != 0)
        {
            throw new MatrixFailure($"The roof moved or a relay is on: {why}");
        }
    }

    /// <summary>The roof at rest with every relay off, and the plant recorded no violation.</summary>
    public async Task SettledAsync(string why)
    {
        await WaitAsync(async () => !(bool)(await StatusAsync())["isMoving"]! && (int)(await PlantAsync())["relayRegister"]! == 0, $"the roof at rest: {why}");
        var violations = await ViolationsAsync();
        if (violations.Count > 0)
        {
            throw new MatrixFailure($"{why}: the emulated plant recorded {violations.Count} violations");
        }
    }

    private async Task<JsonNode> GetAsync(Uri uri, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new MatrixFailure($"GET {uri.AbsolutePath} answered {(int)response.StatusCode}");
        }

        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    public void Dispose() => _http.Dispose();
}
