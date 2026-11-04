using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Asp.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The controller's Alpaca command surface over HTTP (#220, §4, §5, §6, T4): the External Alpaca account's key is refused
/// everywhere but its own routes and the status reads, and only from a trusted Alpaca server address (the TCP peer, never a
/// proxy header); every other account is refused on its routes; the person-only routes refuse every API key; only the
/// Alpaca server's own account reports events; the history records an Alpaca command and never its key.
/// </summary>
[TestClass]
public sealed class AlpacaCommandsApiTests
{
    public const string CommanderKey = "test-alpaca-external-key-not-a-real-secret";
    public const string ServerKey = "test-alpaca-server-key-not-a-real-secret-01";
    private const string Alpaca = "/api/v4.0/alpaca";

    /// <summary>The External account's own routes and the status reads: the only ones it may use.</summary>
    private static readonly HashSet<string> CommanderRoutes = new(StringComparer.Ordinal)
    {
        "POST /api/v4.0/alpaca/shutter/ticket",
        "POST /api/v4.0/alpaca/shutter/close",
        "POST /api/v4.0/alpaca/shutter/open",
        "GET /api/v4.0/RoofControl/Status",
        "GET /api/v4.0/Safety"
    };

    private static RoofApiTestHost CreateHost(IPAddress? remote = null, Dictionary<string, string?>? extra = null, RecordingLoggerProvider? logs = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:ApiKeys:4:Name"] = "alpaca-external",
            ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofAlpacaContract.CommanderRole,
            ["RoofControllerSecurity:ApiKeys:4:Key"] = CommanderKey,
            ["RoofControllerSecurity:ApiKeys:5:Name"] = "alpaca-server",
            ["RoofControllerSecurity:ApiKeys:5:Role"] = RoofAlpacaContract.ServerRole,
            ["RoofControllerSecurity:ApiKeys:5:Key"] = ServerKey,
            ["RoofController:Alpaca:AllowCommands"] = "OpenAndClose",
        };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return new RoofApiTestHost(settings: settings, remoteIp: remote ?? IPAddress.Loopback, configureServices: services =>
        {
            services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000);
            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
            }
        });
    }

    private static async Task<HttpClient> PersonAsync(RoofApiTestHost host, string name = "ada", string role = RoofControllerApiContract.OperatorRole)
    {
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        (await admin.PostAsJsonAsync("/api/v4.0/Identity/Users", new RoofUserCreateRequest { Name = name, Role = role, Password = TestSecrets.Password }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        using var anonymous = host.CreateApiClient();
        var signIn = await anonymous.PostAsJsonAsync("/api/v4.0/Auth/Session", new RoofSignInRequest { Name = name, Password = TestSecrets.Password });
        signIn.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await ApiJson.ReadAsync<RoofSessionResponse>(signIn);
        var client = host.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, session.Token);
        return client;
    }

    private static async Task<RoofAlpacaTicket> TicketAsync(HttpClient client, RoofAlpacaCommandKind kind = RoofAlpacaCommandKind.Close)
    {
        var response = await client.PostAsync($"{Alpaca}/shutter/ticket?kind={kind}", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ApiJson.ReadAsync<RoofAlpacaTicket>(response);
    }

    [TestMethod]
    public async Task TheExternalAccountsKey_IsRefusedEverywhere_ButItsOwnRoutesAndTheStatus()
    {
        using var host = CreateHost();
        using var external = host.CreateApiClient(CommanderKey);
        var failures = new List<string>();

        foreach (var (method, path) in Probes(host))
        {
            var key = $"{method} {path}";
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "PUT")
            {
                request.Content = JsonContent.Create(new { });
            }

            var response = await external.SendAsync(request);
            var allowed = CommanderRoutes.Contains(key);
            if (!allowed && response.StatusCode != HttpStatusCode.Forbidden)
            {
                failures.Add($"{key} answered {(int)response.StatusCode}, not 403");
            }
            else if (allowed && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                failures.Add($"{key} refused the External account ({(int)response.StatusCode})");
            }
        }

        string.Join(Environment.NewLine, failures).Should().BeEmpty("deny by default: the External account's key reaches only its own routes and the status");
        var probed = Probes(host).Select(probe => $"{probe.Method} {probe.Path}").ToList();
        probed.Should().HaveCountGreaterThan(60, "every route the controller maps is probed");
        probed.Should().Contain(CommanderRoutes);
        probed.Should().Contain(["POST /api/v4.0/RoofControl/Stop", "POST /api/v4.0/RoofControl/Open", "POST /api/v4.0/RoofControl/Close", "POST /api/v4.0/RoofControl/ClearFault",
            "POST /api/v4.0/alpaca/grants", "DELETE /api/v4.0/alpaca/hold", "POST /api/v4.0/RoofControl/Lease", "POST /api/v4.0/System/Restart"]);
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never(), "not even Stop");
        host.RoofService.Verify(service => service.Open(), Times.Never());
    }

    [TestMethod]
    public async Task EveryOtherAccount_IsRefusedOnTheShutterRoutes_AndAnonymousIsChallenged()
    {
        using var host = CreateHost();
        using var person = await PersonAsync(host);
        foreach (var client in new[] { host.CreateApiClient(TestApiKeys.Admin), host.CreateApiClient(TestApiKeys.Operator), host.CreateApiClient(TestApiKeys.Viewer), host.CreateApiClient(ServerKey), person })
        {
            (await client.PostAsync($"{Alpaca}/shutter/ticket?kind=Close", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.PostAsync($"{Alpaca}/shutter/ticket?kind=Open", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.PostAsJsonAsync($"{Alpaca}/shutter/close", new RoofAlpacaCommandRequest("t", "i", 1))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.PostAsJsonAsync($"{Alpaca}/shutter/open", new RoofAlpacaCommandRequest("t", "i", 1))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        host.RoofService.Verify(service => service.StartMotionAsync(It.IsAny<HVO.RoofControllerV4.RPi.Logic.RoofMotionRequest>(), It.IsAny<CancellationToken>()), Times.Never());

        using var anonymous = host.CreateApiClient();
        (await anonymous.PostAsync($"{Alpaca}/shutter/ticket?kind=Close", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task TheExternalKey_FromAnUntrustedAddress_IsRefused_WhateverAProxyHeaderSays()
    {
        using var host = CreateHost(IPAddress.Parse("192.168.1.50"));
        using var external = host.CreateApiClient(CommanderKey);
        external.DefaultRequestHeaders.Add("X-Forwarded-For", "127.0.0.1");

        (await external.PostAsync($"{Alpaca}/shutter/ticket?kind=Close", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await external.PostAsync($"{Alpaca}/shutter/ticket?kind=Open", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await external.PostAsJsonAsync($"{Alpaca}/shutter/open", new RoofAlpacaCommandRequest("t", "i", 1))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await external.GetAsync("/api/v4.0/RoofControl/Status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var trusted = CreateHost(IPAddress.Parse("192.168.1.50"), new() { ["RoofController:Alpaca:ServerAddresses:0"] = "192.168.1.50" });
        await TicketAsync(trusted.CreateApiClient(CommanderKey));
    }

    [TestMethod]
    [DataRow("::1")]
    [DataRow("::ffff:127.0.0.1")]
    [DataRow("127.0.0.1")]
    public async Task LoopbackInEveryForm_IsATrustedAlpacaServerAddress(string address)
    {
        using var host = CreateHost(IPAddress.Parse(address));
        var ticket = await TicketAsync(host.CreateApiClient(CommanderKey));
        ticket.InstanceId.Should().Be("test-instance");
    }

    [TestMethod]
    public async Task AnOpenTicket_IsIssued_AndTheOpenRouteIsRegistered_ForTheExternalAccount()
    {
        using var host = CreateHost();
        using var external = host.CreateApiClient(CommanderKey);

        var ticket = await TicketAsync(external, RoofAlpacaCommandKind.Open);

        ticket.Kind.Should().Be(RoofAlpacaCommandKind.Open);
        Probes(host).Should().Contain(("POST", "/api/v4.0/alpaca/shutter/open"), "phase 4 registers the Open route");
        (await external.PostAsync($"{Alpaca}/shutter/ticket?kind=Sideways", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await external.PostAsync($"{Alpaca}/shutter/ticket?kind=2", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task AnOpenCarriedOnACloseTicket_IsRefused_AndNothingStarts()
    {
        using var host = CreateHost();
        using var person = await PersonAsync(host);
        (await person.DeleteAsync($"{Alpaca}/hold")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var external = host.CreateApiClient(CommanderKey);
        var ticket = await TicketAsync(external, RoofAlpacaCommandKind.Close);

        var open = await external.PostAsJsonAsync($"{Alpaca}/shutter/open", new RoofAlpacaCommandRequest(ticket.TicketId, ticket.InstanceId, ticket.Generation));

        open.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiJson.ReadElementAsync(open)).GetProperty("alpacaRefusal").GetString().Should().Be("TicketMismatch");
        host.RoofService.Verify(service => service.StartMotionAsync(It.IsAny<HVO.RoofControllerV4.RPi.Logic.RoofMotionRequest>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [TestMethod]
    public async Task ThePersonOnlyRoutes_RefuseEveryApiKey_AndAcceptAPersonsSession()
    {
        using var host = CreateHost();
        foreach (var key in new[] { TestApiKeys.Viewer, TestApiKeys.Operator, TestApiKeys.Admin, CommanderKey, ServerKey })
        {
            using var client = host.CreateApiClient(key);
            (await client.PostAsJsonAsync($"{Alpaca}/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-unknown:Main"])))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, key);
            (await client.PostAsJsonAsync($"{Alpaca}/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["weather-unsafe:Sky"])))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, key);
            (await client.DeleteAsync($"{Alpaca}/hold")).StatusCode.Should().Be(HttpStatusCode.Forbidden, key);
            (await client.PostAsync($"{Alpaca}/lease?input=true", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden, key);
            (await client.PostAsync($"{Alpaca}/alerts/1/acknowledge", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden, key);
        }

        using var person = await PersonAsync(host);
        var grant = await person.PostAsJsonAsync($"{Alpaca}/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-unknown:Main"]));
        grant.StatusCode.Should().Be(HttpStatusCode.Created, await grant.Content.ReadAsStringAsync());
        (await ApiJson.ReadAsync<RoofOverrideGrant>(grant)).GrantedBy.Should().Contain("ada");
        var open = await person.PostAsJsonAsync($"{Alpaca}/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["weather-unsafe:Sky"]));
        open.StatusCode.Should().Be(HttpStatusCode.Created, await open.Content.ReadAsStringAsync());
        (await ApiJson.ReadAsync<RoofOverrideGrant>(open)).Kind.Should().Be(RoofAlpacaCommandKind.Open);
        (await person.DeleteAsync($"{Alpaca}/grants/Open")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await person.DeleteAsync($"{Alpaca}/grants/Close")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await person.DeleteAsync($"{Alpaca}/hold")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await person.PostAsync($"{Alpaca}/lease?input=true", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await person.DeleteAsync($"{Alpaca}/lease")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var viewer = await PersonAsync(host, "vera", RoofControllerApiContract.ViewerRole);
        (await viewer.DeleteAsync($"{Alpaca}/hold")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "clearing the hold needs the Operator role");
        (await viewer.PostAsync($"{Alpaca}/lease", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task OnlyTheAlpacaServersOwnAccount_ReportsEvents_RaisingACloseAlert_WithNoCredentialData()
    {
        using var host = CreateHost();
        var report = new RoofAlpacaEvent("Close", "NotAuthorised", "192.168.1.60", 12, 34);

        (await host.CreateApiClient(TestApiKeys.Viewer).PostAsJsonAsync($"{Alpaca}/events", report)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await host.CreateApiClient(CommanderKey).PostAsJsonAsync($"{Alpaca}/events", report)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await host.CreateApiClient(ServerKey).PostAsJsonAsync($"{Alpaca}/events", report with { ClientAddress = "not an address" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await host.CreateApiClient(ServerKey).PostAsJsonAsync($"{Alpaca}/events", report)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var alerts = await ApiJson.ReadAsync<RoofAlpacaAlert[]>(await host.CreateApiClient(TestApiKeys.Viewer).GetAsync($"{Alpaca}/alerts"));
        var alert = alerts.Should().ContainSingle().Subject;
        alert.Kind.Should().Be("NotAuthorised");
        alert.ClientId.Should().Be(12u);
        alert.ClientAddress.Should().Be("192.168.1.60");

        (await host.CreateApiClient(ServerKey).PostAsJsonAsync($"{Alpaca}/events", report with { Kind = "Open" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ApiJson.ReadAsync<RoofAlpacaAlert[]>(await host.CreateApiClient(TestApiKeys.Viewer).GetAsync($"{Alpaca}/alerts"))).Should().ContainSingle("an Open's is log-only");
    }

    [TestMethod]
    public async Task AWrongKeyForACloseTicket_RaisesTheAlert_FromTheReportedClient_ButForAnOpenOnlyLogs()
    {
        using var logs = new RecordingLoggerProvider();
        using var host = CreateHost(logs: logs);
        using var wrong = host.CreateApiClient("test-not-the-alpaca-key-not-a-real-secret");
        wrong.DefaultRequestHeaders.Add(RoofAlpacaContract.ClientIdHeader, "77");
        wrong.DefaultRequestHeaders.Add(RoofAlpacaContract.ClientAddressHeader, "192.168.1.61");

        (await wrong.PostAsync($"{Alpaca}/shutter/ticket?kind=Open", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrong.PostAsJsonAsync($"{Alpaca}/shutter/open", new RoofAlpacaCommandRequest("t", "i", 1))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var commands = host.Services.GetRequiredService<RoofAlpacaCommands>();
        commands.Alerts().Should().BeEmpty("an Open refused as not authorised is log-only");
        logs.Entries.Count(entry => entry.Message.Contains("Alpaca Open was refused as not authorised", StringComparison.Ordinal)).Should().Be(2, "both are logged");

        (await wrong.PostAsync($"{Alpaca}/shutter/ticket?kind=Close", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrong.PostAsJsonAsync($"{Alpaca}/shutter/close", new RoofAlpacaCommandRequest("t", "i", 1))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var alert = commands.Alerts().Should().ContainSingle().Subject;
        alert.Kind.Should().Be("NotAuthorised");
        alert.Count.Should().Be(2, "one de-duplicated alert");
        alert.ClientId.Should().Be(77u);
        alert.ClientAddress.Should().Be("192.168.1.61");
    }

    [TestMethod]
    [DataRow("shutter/close", true)]
    [DataRow("shutter/close/", true)]
    [DataRow("SHUTTER/CLOSE", true)]
    [DataRow("shutter/close?kind=Open", true)]
    [DataRow("shutter/open", false)]
    [DataRow("shutter/open/", false)]
    [DataRow("shutter/open/?kind=Close", false)]
    [DataRow("shutter/ticket?kind=Close", true)]
    [DataRow("shutter/ticket/?kind=Close", true)]
    [DataRow("shutter/ticket?kind=Open", false)]
    public async Task AWrongKey_IsAlertedForACloseAndOnlyLoggedForAnOpen_HoweverThePathIsWritten(string path, bool alerted)
    {
        // PR #232 review S3: the command is the matched endpoint's, never the path's spelling or a stray query; only the
        // ticket route reads its kind.
        using var host = CreateHost();
        using var wrong = host.CreateApiClient("test-not-the-alpaca-key-not-a-real-secret");
        wrong.DefaultRequestHeaders.Add(RoofAlpacaContract.ClientAddressHeader, "192.168.1.61");

        var response = await wrong.PostAsJsonAsync($"{Alpaca}/{path}", new RoofAlpacaCommandRequest("t", "i", 1));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the endpoint matched and refused the key");
        var alerts = host.Services.GetRequiredService<RoofAlpacaCommands>().Alerts();
        if (alerted)
        {
            alerts.Should().ContainSingle(alert => alert.Kind == "NotAuthorised" && alert.Command == RoofAlpacaCommandKind.Close);
        }
        else
        {
            alerts.Should().BeEmpty("an Open refused as not authorised is only logged");
        }
    }

    [TestMethod]
    [DataRow(RoofControllerStatus.Closed)]
    [DataRow(RoofControllerStatus.PartiallyOpen)]
    public async Task ClearingTheStopHold_RecordsTheCurrentRoofState(RoofControllerStatus roofState)
    {
        using var host = CreateHost();
        host.RoofService.Setup(service => service.GetCurrentStatusSnapshot())
            .Returns(RoofServiceMock.Snapshot(roofState));
        using var person = await PersonAsync(host);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        (await person.PostAsync("/api/v4.0/RoofControl/Stop", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await person.DeleteAsync($"{Alpaca}/hold")).StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await ApiJson.ReadAsync<RoofCommandHistoryPage>(await admin.GetAsync("/api/v4.0/RoofControl/History"));
        var entry = history.Entries.Should().ContainSingle(row => row.Command == RoofHistoryCommand.StopHoldClear).Which;
        entry.Accepted.Should().BeTrue();
        entry.User.Should().Be("ada");
        entry.Status.Should().Be(roofState, "clearing the Stop hold records the roof afterwards like other history rows");
    }

    [TestMethod]
    [DataRow(RoofHistoryCommand.OverrideGrant)]
    [DataRow(RoofHistoryCommand.OverrideRevoke)]
    [DataRow(RoofHistoryCommand.AlertAcknowledge)]
    public async Task AlpacaAdministrativeHistory_RecordsTheRoofAfterwards(RoofHistoryCommand command)
    {
        using var host = CreateHost();
        host.RoofService.Setup(service => service.GetCurrentStatusSnapshot())
            .Returns(RoofServiceMock.Snapshot(RoofControllerStatus.Closed));
        using var person = await PersonAsync(host);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        if (command == RoofHistoryCommand.AlertAcknowledge)
        {
            using var server = host.CreateApiClient(ServerKey);
            (await server.PostAsJsonAsync($"{Alpaca}/events", new RoofAlpacaEvent("Close", "NotAuthorised", "127.0.0.1", 12, 34)))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);
            var alerts = await ApiJson.ReadAsync<RoofAlpacaAlert[]>(await person.GetAsync($"{Alpaca}/alerts"));
            (await person.PostAsync($"{Alpaca}/alerts/{alerts.Single().Id}/acknowledge", null))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        else
        {
            (await person.PostAsJsonAsync($"{Alpaca}/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-unknown:Main"])))
                .StatusCode.Should().Be(HttpStatusCode.Created);
            if (command == RoofHistoryCommand.OverrideRevoke)
            {
                (await person.DeleteAsync($"{Alpaca}/grants/Close")).StatusCode.Should().Be(HttpStatusCode.NoContent);
            }
        }

        var history = await ApiJson.ReadAsync<RoofCommandHistoryPage>(await admin.GetAsync("/api/v4.0/RoofControl/History"));
        var entry = history.Entries.Should().ContainSingle(row => row.Command == command).Which;
        entry.Accepted.Should().BeTrue();
        entry.Status.Should().Be(RoofControllerStatus.Closed);
    }

    [TestMethod]
    public async Task AnAlpacaClose_IsRecordedInTheHistory_WithTheReportedClient_AndNeverTheKey()
    {
        using var logs = new RecordingLoggerProvider();
        using var host = CreateHost(logs: logs);
        using var person = await PersonAsync(host);
        (await person.DeleteAsync($"{Alpaca}/hold")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var external = host.CreateApiClient(CommanderKey);
        external.DefaultRequestHeaders.Add(RoofAlpacaContract.ClientIdHeader, "7");
        external.DefaultRequestHeaders.Add(RoofAlpacaContract.ClientTransactionIdHeader, "41");
        external.DefaultRequestHeaders.Add(RoofAlpacaContract.ClientAddressHeader, "192.168.1.50");
        external.DefaultRequestHeaders.Add(RoofAlpacaContract.AuthMethodHeader, "basic");
        var ticket = await TicketAsync(external);

        var close = await external.PostAsJsonAsync($"{Alpaca}/shutter/close", new RoofAlpacaCommandRequest(ticket.TicketId, ticket.InstanceId, ticket.Generation));
        close.StatusCode.Should().Be(HttpStatusCode.OK, await close.Content.ReadAsStringAsync());
        (await ApiJson.ReadAsync<RoofStatusResponse>(close)).Alpaca.Should().NotBeNull();
        var replayed = await external.PostAsJsonAsync($"{Alpaca}/shutter/close", new RoofAlpacaCommandRequest("unknown-ticket", ticket.InstanceId, ticket.Generation));
        replayed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ApiJson.ReadElementAsync(replayed);
        problem.GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.AlpacaCommandRefused));
        problem.GetProperty("alpacaRefusal").GetString().Should().Be("TicketUnknown");

        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var historyResponse = await admin.GetAsync("/api/v4.0/RoofControl/History?take=50");
        var historyText = await historyResponse.Content.ReadAsStringAsync();
        var history = JsonSerializer.Deserialize<RoofCommandHistoryPage>(historyText, ApiJson.Options)!;
        var entry = history.Entries.First(e => e.Command == RoofHistoryCommand.Close && e.Accepted);
        entry.Source.Should().Be("Alpaca");
        entry.AlpacaClientId.Should().Be(7u);
        entry.AlpacaClientTransactionId.Should().Be(41u);
        entry.AlpacaClientAddress.Should().Be("192.168.1.50");
        entry.AuthMethod.Should().Be("basic");
        entry.RemoteAddress.Should().Be("127.0.0.1", "the controller's own peer, the Alpaca server");
        entry.User.Should().Be("alpaca-external");
        entry.PrincipalId.Should().StartWith("key:");
        history.Entries.Should().Contain(e => e.Command == RoofHistoryCommand.Close && !e.Accepted && e.AlpacaRefusal == "TicketUnknown");
        history.Entries.Should().Contain(e => e.Command == RoofHistoryCommand.StopHoldClear && e.User == "ada");

        historyText.Should().NotContain(CommanderKey);
        logs.Entries.Should().NotContain(e => e.Message.Contains(CommanderKey, StringComparison.Ordinal) || (e.Exception != null && e.Exception.ToString().Contains(CommanderKey, StringComparison.Ordinal)));
        (await (await external.GetAsync("/api/v4.0/RoofControl/Status")).Content.ReadAsStringAsync()).Should().NotContain(CommanderKey);
    }

    [TestMethod]
    public async Task APersonsStop_SetsTheHold_TheAlpacaServersOwnStopDoesNot()
    {
        using var host = CreateHost();
        using var person = await PersonAsync(host);
        (await person.DeleteAsync($"{Alpaca}/hold")).StatusCode.Should().Be(HttpStatusCode.OK);
        var commands = host.Services.GetRequiredService<RoofAlpacaCommands>();

        (await host.CreateApiClient(ServerKey).PostAsync("/api/v4.0/RoofControl/Stop", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        commands.GetStatus().Hold.Held.Should().BeFalse("the Alpaca server's own AbortSlew");

        (await host.CreateApiClient(TestApiKeys.Viewer).PostAsync("/api/v4.0/RoofControl/Stop", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        commands.GetStatus().Hold.Held.Should().BeTrue("a Stop with another Viewer key from loopback sets the hold");
        (await person.DeleteAsync($"{Alpaca}/hold")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await person.PostAsync("/api/v4.0/RoofControl/Stop", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await ApiJson.ReadAsync<RoofStatusResponse>(await person.GetAsync("/api/v4.0/RoofControl/Status"));
        status.Alpaca!.Hold.Held.Should().BeTrue();
        status.Alpaca.Hold.SetBy.Should().Contain("ada");
        status.Alpaca.Mode.Should().Be(RoofAlpacaCommandMode.OpenAndClose);
    }

    [TestMethod]
    public async Task AllowCommandsOpenAndClose_IsAcceptedAtStart_AndShownInTheStatus()
    {
        using var host = CreateHost(extra: new() { ["RoofController:Alpaca:AllowCommands"] = "OpenAndClose" });

        var status = await ApiJson.ReadAsync<RoofStatusResponse>(await host.CreateApiClient(TestApiKeys.Viewer).GetAsync("/api/v4.0/RoofControl/Status"));

        status.Alpaca!.Mode.Should().Be(RoofAlpacaCommandMode.OpenAndClose);
        status.Alpaca.AllowOpenWithoutBlockingScopes.Should().BeFalse("off by default");
    }

    [TestMethod]
    public async Task InBlockMode_AnOpenGrantIsRefusedWith403_AndTheStatusSaysWhy()
    {
        using var host = CreateHost(extra: new() { [RoofApiTestHost.SafetyInterlockModeKey] = "Block" });
        using var person = await PersonAsync(host);

        var refused = await person.PostAsJsonAsync($"{Alpaca}/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, ["weather-unsafe:Sky"]));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await ApiJson.ReadElementAsync(refused);
        problem.GetProperty("alpacaRefusal").GetString().Should().Be("BlockMode");
        problem.GetProperty("detail").GetString().Should().Contain("Block");
        var status = await ApiJson.ReadAsync<RoofStatusResponse>(await person.GetAsync("/api/v4.0/RoofControl/Status"));
        status.Alpaca!.OpenGrantUnavailable.Should().Contain("Block");
        (await person.PostAsJsonAsync($"{Alpaca}/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, ["scope-unknown:Main"])))
            .StatusCode.Should().Be(HttpStatusCode.Created, "Block concerns Open only");
    }

    [TestMethod]
    public async Task AnAlpacaOpen_IsRecordedInTheHistory_WithTheReportedClientAndTheGrant_AndNeverTheKey()
    {
        using var logs = new RecordingLoggerProvider();
        using var host = CreateHost(logs: logs);
        using var person = await PersonAsync(host);
        (await person.DeleteAsync($"{Alpaca}/hold")).StatusCode.Should().Be(HttpStatusCode.OK);
        using var external = host.CreateApiClient(CommanderKey);
        external.DefaultRequestHeaders.Add(RoofAlpacaContract.ClientIdHeader, "7");
        external.DefaultRequestHeaders.Add(RoofAlpacaContract.ClientTransactionIdHeader, "42");
        external.DefaultRequestHeaders.Add(RoofAlpacaContract.ClientAddressHeader, "192.168.1.50");
        external.DefaultRequestHeaders.Add(RoofAlpacaContract.AuthMethodHeader, "header");

        // With no telescope and no weather source configured, the Open is refused with both reasons, and alerted.
        var ticket = await TicketAsync(external, RoofAlpacaCommandKind.Open);
        var refused = await external.PostAsJsonAsync($"{Alpaca}/shutter/open", new RoofAlpacaCommandRequest(ticket.TicketId, ticket.InstanceId, ticket.Generation));
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
        var problem = await ApiJson.ReadElementAsync(refused);
        problem.GetProperty("alpacaRefusal").GetString().Should().Be("NotSafe");
        var keys = problem.GetProperty("reasonKeys").EnumerateArray().Select(key => key.GetString()!).ToArray();
        keys.Should().BeEquivalentTo([RoofAlpacaContract.NoBlockingScopesKey, RoofAlpacaContract.NoRequiredWeatherKey]);

        // A person grants one Open for exactly those reasons; it is used by the next Open.
        var granted = await person.PostAsJsonAsync($"{Alpaca}/grants", new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Open, keys));
        granted.StatusCode.Should().Be(HttpStatusCode.Created);
        var grant = await ApiJson.ReadAsync<RoofOverrideGrant>(granted);
        ticket = await TicketAsync(external, RoofAlpacaCommandKind.Open);
        var open = await external.PostAsJsonAsync($"{Alpaca}/shutter/open", new RoofAlpacaCommandRequest(ticket.TicketId, ticket.InstanceId, ticket.Generation));
        open.StatusCode.Should().Be(HttpStatusCode.OK, await open.Content.ReadAsStringAsync());

        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var historyText = await (await admin.GetAsync("/api/v4.0/RoofControl/History?take=50")).Content.ReadAsStringAsync();
        var history = JsonSerializer.Deserialize<RoofCommandHistoryPage>(historyText, ApiJson.Options)!;
        var accepted = history.Entries.First(e => e.Command == RoofHistoryCommand.Open && e.Accepted);
        accepted.Source.Should().Be("Alpaca");
        accepted.AlpacaClientId.Should().Be(7u);
        accepted.AlpacaClientTransactionId.Should().Be(42u);
        accepted.AlpacaClientAddress.Should().Be("192.168.1.50");
        accepted.AuthMethod.Should().Be("header");
        accepted.User.Should().Be("alpaca-external");
        historyText.Should().Contain(grant.Id, "the history links the Open to the grant it used");
        history.Entries.Should().Contain(e => e.Command == RoofHistoryCommand.Open && !e.Accepted && e.AlpacaRefusal == "NotSafe");
        history.Entries.Should().Contain(e => e.Command == RoofHistoryCommand.OverrideGrant && e.User == "ada");

        historyText.Should().NotContain(CommanderKey);
        logs.Entries.Should().NotContain(e => e.Message.Contains(CommanderKey, StringComparison.Ordinal));
        host.Services.GetRequiredService<RoofAlpacaCommands>().Alerts().Should().Contain(alert => alert.Command == RoofAlpacaCommandKind.Open && alert.Kind == "Refused");
    }

    /// <summary>Every mapped endpoint with each of its methods and sample route values.</summary>
    private static List<(string Method, string Path)> Probes(RoofApiTestHost host)
    {
        var probes = new List<(string, string)>();
        foreach (var endpoint in host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var path = "/" + string.Join("/", endpoint.RoutePattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart literal => literal.Content,
                RoutePatternSeparatorPart separator => separator.Content,
                RoutePatternParameterPart parameter => parameter.Name switch
                {
                    "version" => endpoint.Metadata.GetMetadata<ApiVersionMetadata>()?.Map(ApiVersionMapping.Explicit | ApiVersionMapping.Implicit)
                        .DeclaredApiVersions.Single().ToString() ?? "4.0",
                    "cameraId" => "1",
                    "documentName" => "v4",
                    "kind" => "Close",
                    "group" => "roof",
                    _ => "sample"
                },
                _ => "x"
            }))));
            IReadOnlyList<string> methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } declared ? declared : ["GET"];
            probes.AddRange(methods.Select(method => (method, path)));
        }

        return probes;
    }
}

/// <summary>
/// The Alpaca accounts' keys stay valid for the status hub's periodic re-check (#220): a key carries its account role
/// beside the Viewer role it reads with, and the re-check must compare the account role, or the Alpaca server's hub
/// connection is closed as "re-roled" and its status goes stale.
/// </summary>
[TestClass]
public sealed class AlpacaAccountKeyValidityTests
{
    [TestMethod]
    [DataRow(RoofAlpacaContract.ServerRole)]
    [DataRow(RoofAlpacaContract.CommanderRole)]
    [DataRow(RoofControllerApiContract.ViewerRole)]
    public void AnAccountsKey_StaysValid_AndIsInvalid_OnceReRoled(string role)
    {
        var monitor = KeyStoreFactory.Monitor(KeyStoreFactory.Key("account", role, "test-account-key-not-a-real-secret-0001"));
        using var keys = KeyStoreFactory.Create(monitor);
        var validator = HVO.RoofControllerV4.RPi.Security.RoofCredentialValidator.ForKeysOnly(keys);
        keys.TryValidate("test-account-key-not-a-real-secret-0001", out var identity).Should().BeTrue();
        var user = HVO.RoofControllerV4.RPi.Security.RoofPrincipalFactory.Create(identity!, "ApiKey");

        validator.IsStillValid(user).Should().BeTrue();

        monitor.Set(new HVO.RoofControllerV4.RPi.Security.RoofControllerSecurityOptions
        {
            ApiKeys = [KeyStoreFactory.Key("account", role == RoofControllerApiContract.ViewerRole ? RoofAlpacaContract.ServerRole : RoofControllerApiContract.ViewerRole, "test-account-key-not-a-real-secret-0001")]
        });
        validator.IsStillValid(user).Should().BeFalse("a re-roled key closes its hub connections");
    }
}
