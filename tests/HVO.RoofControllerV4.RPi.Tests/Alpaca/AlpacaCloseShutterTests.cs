using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The Alpaca server's CloseShutter (#220, phase 3): local refusals only for a client not connected, a missing or
/// malformed credential (header, or HTTP Basic with the key as both parts), or an AbortSlew that overtook it before its
/// ticket; otherwise the client's own key is passed on for the ticket and the Close (never the server's, never kept), the
/// client's metadata forwarded, the controller's answer mapped, nothing retried. Refusals as not authorised are slowed
/// down per address, never a correct key.
/// </summary>
[TestClass]
public sealed class AlpacaCloseShutterTests
{
    private const string ClientKey = "test-alpaca-client-key-not-a-real-secret";

    /// <summary>A controller stub: its answers by route, and every request it got (with the headers that matter).</summary>
    private sealed class Controller
    {
        public List<(string Method, string Path, string? ApiKey, Dictionary<string, string> Forwarded, string Body)> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

        public int Count(string path)
        {
            lock (Requests)
            {
                return Requests.Count(request => request.Path.Contains(path, StringComparison.Ordinal));
            }
        }

        public HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var path = request.RequestUri!.PathAndQuery;
            var forwarded = request.Headers.Where(header => header.Key.StartsWith("X-HVO-Alpaca-", StringComparison.Ordinal))
                .ToDictionary(header => header.Key, header => string.Join(",", header.Value));
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            lock (Requests)
            {
                Requests.Add((request.Method.Method, path, request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null, forwarded, body));
            }

            if (Override?.Invoke(request) is { } overridden)
            {
                return overridden;
            }

            return path switch
            {
                _ when path.Contains("/alpaca/shutter/ticket", StringComparison.Ordinal) => Json(new RoofAlpacaTicket("ticket-1", RoofAlpacaCommandKind.Close, "instance-1", 5, 1000)),
                _ when path.Contains("/alpaca/shutter/close", StringComparison.Ordinal) => Json(RoofServiceMock.Snapshot(RoofControllerStatus.Closing, RoofMotionDirection.Closing, relayMask: 0x0A)),
                _ when path.Contains("/alpaca/events", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ when path.Contains("/RoofControl/Stop", StringComparison.Ordinal) => Json(RoofServiceMock.Snapshot(RoofControllerStatus.Stopped)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }
    }

    private static HttpResponseMessage Json(object value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, ApiJson.Options), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status, RoofControllerErrorCode code, string detail, string? rule = null)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["status"] = (int)status,
                ["detail"] = detail,
                ["code"] = code.ToString(),
                ["alpacaRefusal"] = rule
            }), Encoding.UTF8, "application/problem+json")
        };

    private static async Task<(AlpacaTestHost Alpaca, Controller Controller)> StartAsync()
    {
        var controller = new Controller();
        var alpaca = await AlpacaTestHost.StartAsync(controller.Answer);
        foreach (var clientId in new uint[] { 7, 8, 9 })
        {
            await alpaca.ConnectAsync(clientId);
        }

        return (alpaca, controller);
    }

    private static Dictionary<string, string> Header(string key = ClientKey) => new() { [RoofAlpacaContract.ClientKeyHeader] = key };

    private static Dictionary<string, string> Basic(string user, string password)
        => new() { ["Authorization"] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")) };

    private static int Error(JsonElement answer) => answer.GetProperty("ErrorNumber").GetInt32();

    [TestMethod]
    public async Task WithTheKeyInTheHeader_TheTicketAndTheCloseCarryTheClientsKey_AndItsMetadata()
    {
        var (alpaca, controller) = await StartAsync();
        await using var _ = alpaca;

        var answer = await alpaca.PutAsync("closeshutter", "ClientID=7&ClientTransactionID=41", Header());

        Error(answer).Should().Be(0, answer.GetProperty("ErrorMessage").GetString());
        var calls = controller.Requests.Where(request => request.Path.Contains("/alpaca/shutter/", StringComparison.Ordinal)).ToList();
        calls.Select(call => call.Path).Should().Equal($"/api/v4.0/alpaca/shutter/ticket?kind=Close", "/api/v4.0/alpaca/shutter/close");
        calls.Should().OnlyContain(call => call.ApiKey == ClientKey, "the client's key, passed on for these calls only");
        calls[0].Forwarded.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [RoofAlpacaContract.ClientIdHeader] = "7",
            [RoofAlpacaContract.ClientTransactionIdHeader] = "41",
            [RoofAlpacaContract.ClientAddressHeader] = AlpacaTestHost.ClientAddress.ToString(),
            [RoofAlpacaContract.AuthMethodHeader] = "header"
        });
        JsonSerializer.Deserialize<RoofAlpacaCommandRequest>(calls[1].Body, ApiJson.Options).Should().Be(new RoofAlpacaCommandRequest("ticket-1", "instance-1", 5));
    }

    [TestMethod]
    public async Task WithHttpBasic_KeyAsBothParts_TheCloseGoesOn_AsBasic()
    {
        var (alpaca, controller) = await StartAsync();
        await using var _ = alpaca;

        Error(await alpaca.PutAsync("closeshutter", "ClientID=7", Basic(ClientKey, ClientKey))).Should().Be(0);

        controller.Requests.Where(request => request.Path.Contains("/alpaca/shutter/", StringComparison.Ordinal))
            .Should().HaveCount(2).And.OnlyContain(call => call.ApiKey == ClientKey && call.Forwarded[RoofAlpacaContract.AuthMethodHeader] == "basic");
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("basic parts differ")]
    [DataRow("header and basic differ")]
    [DataRow("not basic")]
    [DataRow("malformed basic")]
    public async Task AMissingOrMalformedCredential_IsNotAuthorised_NothingReachesTheShutterRoutes_AndTheRefusalIsReported(string kind)
    {
        var (alpaca, controller) = await StartAsync();
        await using var _ = alpaca;
        var headers = kind switch
        {
            "basic parts differ" => Basic(ClientKey, ClientKey + "x"),
            "header and basic differ" => Header().Concat(Basic("other-key-not-a-real-secret-00", "other-key-not-a-real-secret-00")).ToDictionary(),
            "not basic" => new Dictionary<string, string> { ["Authorization"] = "Bearer " + ClientKey },
            "malformed basic" => new Dictionary<string, string> { ["Authorization"] = "Basic ###" },
            _ => []
        };

        var answer = await alpaca.PutAsync("closeshutter", "ClientID=9&ClientTransactionID=3", headers);

        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        answer.GetProperty("ErrorMessage").GetString().Should().Be(AlpacaDome.NotAuthorised);
        controller.Count("/alpaca/shutter/").Should().Be(0);
        await AlpacaCommandRig.WaitUntilAsync(() => controller.Count("/alpaca/events") == 1, "the refusal's report");
        var report = controller.Requests.Single(request => request.Path.Contains("/alpaca/events", StringComparison.Ordinal));
        report.ApiKey.Should().Be(TestApiKeys.Viewer, "the server reports with its own account's key");
        report.Body.Should().NotContain(ClientKey);
        JsonSerializer.Deserialize<RoofAlpacaEvent>(report.Body, ApiJson.Options).Should().Be(new RoofAlpacaEvent("Close", "NotAuthorised", AlpacaTestHost.ClientAddress.ToString(), 9, 3));
    }

    [TestMethod]
    public async Task AKeyTheControllerRefuses_IsNotAuthorised_AndNoCloseIsSent()
    {
        var (alpaca, controller) = await StartAsync();
        await using var _ = alpaca;
        controller.Override = request => request.RequestUri!.AbsolutePath.Contains("/shutter/ticket", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : null;

        var answer = await alpaca.PutAsync("closeshutter", "ClientID=7", Header());

        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        answer.GetProperty("ErrorMessage").GetString().Should().Be(AlpacaDome.NotAuthorised);
        controller.Count("/alpaca/shutter/close").Should().Be(0);
    }

    [TestMethod]
    public async Task TheControllersRefusal_IsInvalidOperation_WithItsWords_AndItsStateCodesKeepTheirNumbers()
    {
        var (alpaca, controller) = await StartAsync();
        await using var _ = alpaca;
        controller.Override = request => request.RequestUri!.AbsolutePath.Contains("/shutter/close", StringComparison.Ordinal)
            ? Problem(HttpStatusCode.Conflict, RoofControllerErrorCode.AlpacaCommandRefused, "Not safe to close: Telescope 'Main' is not parked.", "NotSafe")
            : null;

        var refused = await alpaca.PutAsync("closeshutter", "ClientID=7", Header());

        Error(refused).Should().Be(AlpacaErrors.InvalidOperation);
        refused.GetProperty("ErrorMessage").GetString().Should().Contain("Telescope 'Main' is not parked");

        controller.Override = request => request.RequestUri!.AbsolutePath.Contains("/shutter/close", StringComparison.Ordinal)
            ? Problem(HttpStatusCode.ServiceUnavailable, RoofControllerErrorCode.NotInitialized, "The roof controller is not initialized.")
            : null;
        Error(await alpaca.PutAsync("closeshutter", "ClientID=7", Header())).Should().Be(AlpacaErrors.ControllerNotInitialized);
    }

    [TestMethod]
    public async Task AControllerThatDoesNotAnswerTheClose_IsAnError_AndTheCloseIsNeverRetried()
    {
        var (alpaca, controller) = await StartAsync();
        await using var _ = alpaca;
        controller.Override = request => request.RequestUri!.AbsolutePath.Contains("/shutter/close", StringComparison.Ordinal)
            ? throw new TaskCanceledException("The controller did not answer (test).", new TimeoutException())
            : null;

        var answer = await alpaca.PutAsync("closeshutter", "ClientID=7", Header());

        Error(answer).Should().Be(AlpacaErrors.ControllerUnavailable);
        answer.GetProperty("ErrorMessage").GetString().Should().Contain("Nothing is retried");
        controller.Count("/alpaca/shutter/close").Should().Be(1);
    }

    [TestMethod]
    public async Task AnAbortSlewBetweenTheClosesArrivalAndItsTicket_RefusesIt_AndNoCloseIsSent()
    {
        var (alpaca, controller) = await StartAsync();
        await using var _ = alpaca;
        controller.Override = request =>
        {
            if (!request.RequestUri!.AbsolutePath.Contains("/shutter/ticket", StringComparison.Ordinal))
            {
                return null;
            }

            // An AbortSlew arrives while the ticket is on its way.
            Task.Run(() => alpaca.PutAsync("abortslew", "ClientID=8")).GetAwaiter().GetResult();
            return null;
        };

        var answer = await alpaca.PutAsync("closeshutter", "ClientID=7", Header());

        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        answer.GetProperty("ErrorMessage").GetString().Should().Be(AlpacaDome.Aborted);
        controller.Count("/alpaca/shutter/close").Should().Be(0);
        controller.Count("/RoofControl/Stop").Should().Be(1);
    }

    [TestMethod]
    public async Task ReadsAndAbortSlew_NeverPassAClientsCredentialOn()
    {
        var (alpaca, controller) = await StartAsync();
        await using var _ = alpaca;

        await alpaca.PutAsync("abortslew", "ClientID=7", Header());
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dome/0/shutterstatus?ClientID=7"))
        {
            request.Headers.Add(RoofAlpacaContract.ClientKeyHeader, ClientKey);
            await alpaca.Http.SendAsync(request);
        }

        controller.Requests.Should().NotContain(request => request.ApiKey == ClientKey);
        controller.Requests.Single(request => request.Path.Contains("/RoofControl/Stop", StringComparison.Ordinal)).ApiKey.Should().Be(TestApiKeys.Viewer);
    }

    [TestMethod]
    public async Task AfterFiftyBadAttempts_ACorrectKeyIsNeitherRefusedNorDelayed_AndRefusalsAreSlowedDown()
    {
        var (alpaca, controller) = await StartAsync();
        await using var _ = alpaca;
        var refusals = await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            var clock = Stopwatch.StartNew();
            var answer = await alpaca.PutAsync("closeshutter", "ClientID=7");
            return (Error: Error(answer), Took: clock.Elapsed);
        }));

        refusals.Should().OnlyContain(refusal => refusal.Error == AlpacaErrors.InvalidOperation);
        refusals.Max(refusal => refusal.Took).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1500), "refusals in a row are answered ever more slowly");
        var correct = Stopwatch.StartNew();
        var accepted = await alpaca.PutAsync("closeshutter", "ClientID=7", Header());
        correct.Stop();

        Error(accepted).Should().Be(0);
        correct.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "a correct key is never delayed");
        Error(await alpaca.PutAsync("abortslew", "ClientID=7")).Should().Be(0, "AbortSlew is never limited");
    }

    [TestMethod]
    public async Task OpenShutter_IsRefusedLocally_EvenWithACorrectKey_AndAsksForNoTicket()
    {
        var (alpaca, controller) = await StartAsync();
        await using var _ = alpaca;

        var answer = await alpaca.PutAsync("openshutter", "ClientID=7", Header());

        Error(answer).Should().Be(AlpacaErrors.InvalidOperation);
        answer.GetProperty("ErrorMessage").GetString().Should().Be(AlpacaDome.OpenNotAvailable);
        controller.Requests.Should().NotContain(request => request.Path.Contains("/alpaca/", StringComparison.Ordinal));
    }
}
