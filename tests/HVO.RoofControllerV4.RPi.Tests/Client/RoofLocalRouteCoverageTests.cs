using System.Linq.Expressions;
using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using HVO.RoofControllerV4.RPi.Tests.Cli;
using FluentAssertions;
using FluentAssertions.Execution;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>Every route available to the local CLI has an explicit native authorization category.</summary>
[TestClass]
public sealed class RoofLocalRouteCoverageTests
{
    // Keep the route name as well as its value: adding a route constant requires an explicit decision here,
    // even when its path happens to fit an existing prefix mapping. These calls never reach a controller.
    private static readonly (string Route, string Method, string Path, string Scope)[] Calls =
    [
        (nameof(RoofApiRoutes.Me), "GET", RoofApiRoutes.Me, "read"),
        (nameof(RoofApiRoutes.Status), "GET", RoofApiRoutes.Status, "read"),
        (nameof(RoofApiRoutes.Stop), "POST", RoofApiRoutes.Stop, "read"),
        (nameof(RoofApiRoutes.Open), "POST", RoofApiRoutes.Open, "operate"),
        (nameof(RoofApiRoutes.Close), "POST", RoofApiRoutes.Close, "operate"),
        (nameof(RoofApiRoutes.Lease), "POST", RoofApiRoutes.Lease, "operate"),
        (nameof(RoofApiRoutes.ClearFault), "POST", RoofApiRoutes.ClearFault, "operate"),
        (nameof(RoofApiRoutes.Configuration), "GET", RoofApiRoutes.Configuration, "read"),
        (nameof(RoofApiRoutes.Configuration), "POST", RoofApiRoutes.Configuration, "configure"),
        (nameof(RoofApiRoutes.History), "GET", RoofApiRoutes.HistoryPage(new RoofHistoryQuery { Mine = true }), "read"),
        (nameof(RoofApiRoutes.Settings), "GET", RoofApiRoutes.Settings, "read"),
        (nameof(RoofApiRoutes.SettingsCatalogue), "GET", RoofApiRoutes.SettingsCatalogue, "read"),
        (nameof(RoofApiRoutes.Settings), "POST", RoofApiRoutes.SettingsGroup("alpaca"), "configure"),
        (nameof(RoofApiRoutes.SettingsReload), "POST", RoofApiRoutes.SettingsReload, "configure"),
        (nameof(RoofApiRoutes.SettingsDiscard), "POST", RoofApiRoutes.SettingsDiscard, "configure"),
        (nameof(RoofApiRoutes.Restart), "POST", RoofApiRoutes.Restart, "configure"),
        (nameof(RoofApiRoutes.SystemSummary), "GET", RoofApiRoutes.SystemSummary, "read"),
        (nameof(RoofApiRoutes.SystemInformation), "GET", RoofApiRoutes.SystemInformation, "read"),
        (nameof(RoofApiRoutes.SystemMetrics), "GET", RoofApiRoutes.SystemMetrics, "read"),
        (nameof(RoofApiRoutes.Health), "GET", RoofApiRoutes.Health, "read"),
        (nameof(RoofApiRoutes.AlpacaGrants), "GET", RoofApiRoutes.AlpacaGrants, "read"),
        (nameof(RoofApiRoutes.AlpacaGrants), "POST", RoofApiRoutes.AlpacaGrants, "operate"),
        (nameof(RoofApiRoutes.AlpacaGrants), "DELETE", RoofApiRoutes.AlpacaGrant(RoofAlpacaCommandKind.Open), "operate"),
        (nameof(RoofApiRoutes.AlpacaHold), "DELETE", RoofApiRoutes.AlpacaHold, "operate"),
        (nameof(RoofApiRoutes.AlpacaLease), "POST", RoofApiRoutes.AlpacaLease, "operate"),
        (nameof(RoofApiRoutes.AlpacaLease), "POST", RoofApiRoutes.AlpacaLease + "?input=true", "operate"),
        (nameof(RoofApiRoutes.AlpacaLease), "DELETE", RoofApiRoutes.AlpacaLease, "operate"),
        (nameof(RoofApiRoutes.AlpacaAlerts), "GET", RoofApiRoutes.AlpacaAlerts, "read"),
        (nameof(RoofApiRoutes.AlpacaAlerts), "POST", RoofApiRoutes.AcknowledgeAlpacaAlert("fixture-alert"), "operate"),
        (nameof(RoofApiRoutes.Principals), "GET", RoofApiRoutes.Principals, "identity"),
        (nameof(RoofApiRoutes.Principals), "POST", RoofApiRoutes.Principals, "identity"),
        (nameof(RoofApiRoutes.Principals), "PUT", RoofApiRoutes.Principals + "/fixture-principal", "identity"),
        (nameof(RoofApiRoutes.Users), "GET", RoofApiRoutes.Users, "identity"),
        (nameof(RoofApiRoutes.Users), "POST", RoofApiRoutes.Users, "identity"),
        (nameof(RoofApiRoutes.Users), "GET", RoofApiRoutes.User("fixture-user"), "identity"),
        (nameof(RoofApiRoutes.Users), "PUT", RoofApiRoutes.User("fixture-user"), "identity"),
        (nameof(RoofApiRoutes.Users), "DELETE", RoofApiRoutes.User("fixture-user"), "identity"),
        (nameof(RoofApiRoutes.Users), "PUT", RoofApiRoutes.UserPin("fixture-user"), "identity"),
        (nameof(RoofApiRoutes.Users), "DELETE", RoofApiRoutes.UserPin("fixture-user"), "identity"),
        (nameof(RoofApiRoutes.ApiKeys), "GET", RoofApiRoutes.ApiKeys, "identity"),
        (nameof(RoofApiRoutes.ApiKeys), "POST", RoofApiRoutes.ApiKeys, "identity"),
        (nameof(RoofApiRoutes.ApiKeys), "PUT", RoofApiRoutes.ApiKey("fixture-key"), "identity"),
        (nameof(RoofApiRoutes.ApiKeys), "DELETE", RoofApiRoutes.ApiKey("fixture-key"), "identity"),
        (nameof(RoofApiRoutes.ApiKeys), "POST", RoofApiRoutes.RotateApiKey("fixture-key"), "identity"),
        (nameof(RoofApiRoutes.Sessions), "GET", RoofApiRoutes.Sessions, "identity"),
        (nameof(RoofApiRoutes.Sessions), "DELETE", RoofApiRoutes.IdentitySession("fixture-session"), "identity")
    ];

    private static readonly (string Builder, string Method, string Path, string Scope)[] BuilderCalls =
    [
        (nameof(RoofApiRoutes.AlpacaGrant), "DELETE", RoofApiRoutes.AlpacaGrant(RoofAlpacaCommandKind.Close), "operate"),
        (nameof(RoofApiRoutes.AcknowledgeAlpacaAlert), "POST", RoofApiRoutes.AcknowledgeAlpacaAlert("fixture-alert"), "operate"),
        (nameof(RoofApiRoutes.User), "GET", RoofApiRoutes.User("fixture-user"), "identity"),
        (nameof(RoofApiRoutes.UserPin), "PUT", RoofApiRoutes.UserPin("fixture-user"), "identity"),
        (nameof(RoofApiRoutes.ApiKey), "DELETE", RoofApiRoutes.ApiKey("fixture-key"), "identity"),
        (nameof(RoofApiRoutes.RotateApiKey), "POST", RoofApiRoutes.RotateApiKey("fixture-key"), "identity"),
        (nameof(RoofApiRoutes.IdentitySession), "DELETE", RoofApiRoutes.IdentitySession("fixture-session"), "identity"),
        (nameof(RoofApiRoutes.SettingsGroup), "POST", RoofApiRoutes.SettingsGroup("alpaca"), "configure"),
        (nameof(RoofApiRoutes.Camera), "GET", RoofApiRoutes.Camera(1), "read"),
        (nameof(RoofApiRoutes.HistoryPage), "GET", RoofApiRoutes.HistoryPage(new RoofHistoryQuery()), "read"),

        // The roof commands' query variants (#255), as RoofControlApi builds them: the category without the query.
        (nameof(RoofApiRoutes.OpenCommand), "POST", RoofApiRoutes.OpenCommand(overrideSafety: true), "operate"),
        (nameof(RoofApiRoutes.OpenCommand), "POST", RoofApiRoutes.OpenCommand(overrideSafety: false), "operate"),
        (nameof(RoofApiRoutes.ClearFaultCommand), "POST", RoofApiRoutes.ClearFaultCommand(250), "operate"),
        (nameof(RoofApiRoutes.ClearFaultCommand), "POST", RoofApiRoutes.ClearFaultCommand(RoofControllerLimits.MinClearFaultPulseMilliseconds), "operate"),
        (nameof(RoofApiRoutes.ClearFaultCommand), "POST", RoofApiRoutes.ClearFaultCommand(RoofControllerLimits.MaxClearFaultPulseMilliseconds), "operate"),
        (nameof(RoofApiRoutes.ClearFaultCommand), "POST", RoofApiRoutes.ClearFaultCommand(null), "operate"),

        // The operator lease's heartbeat (Refs #260), as RoofAlpacaApi builds it, and every kind's grant.
        (nameof(RoofApiRoutes.AlpacaLeaseHeartbeat), "POST", RoofApiRoutes.AlpacaLeaseHeartbeat(input: true), "operate"),
        (nameof(RoofApiRoutes.AlpacaLeaseHeartbeat), "POST", RoofApiRoutes.AlpacaLeaseHeartbeat(input: false), "operate"),
        (nameof(RoofApiRoutes.AlpacaGrant), "DELETE", RoofApiRoutes.AlpacaGrant(RoofAlpacaCommandKind.Open), "operate")
    ];

    public static IEnumerable<object[]> LocalBuiltRequests()
        => BuilderCalls.Select(call => new object[] { call.Builder, call.Method, call.Path, call.Scope });

    [TestMethod]
    [DynamicData(nameof(LocalBuiltRequests))]
    public void EveryLocalCliRouteBuilder_HasTheExpectedScope(string builder, string method, string path, string scope)
        => RoofLocalAuthorization.Operation(new HttpMethod(method), path).Should().Be(scope, $"{method} {builder} must request only its declared category");

    private static readonly Dictionary<string, string> OutsideLocalCli = new()
    {
        [nameof(RoofApiRoutes.Mode)] = "anonymous mode discovery; no native credential is requested",
        [nameof(RoofApiRoutes.Safety)] = "the CLI reads safety from roof status snapshots",
        [nameof(RoofApiRoutes.AlpacaTicket)] = "Alpaca server only, using the client's explicit key",
        [nameof(RoofApiRoutes.AlpacaClose)] = "Alpaca server only, using the client's explicit key",
        [nameof(RoofApiRoutes.AlpacaOpen)] = "Alpaca server only, using the client's explicit key",
        [nameof(RoofApiRoutes.AlpacaEvents)] = "Alpaca server only",
        [nameof(RoofApiRoutes.Session)] = "remote sign-in and sign-out only",
        [nameof(RoofApiRoutes.Providers)] = "anonymous provider discovery",
        [nameof(RoofApiRoutes.ProviderSession)] = "anonymous remote sign-in exchange",
        [nameof(RoofApiRoutes.LocalSession)] = "anonymous native assertion exchange before the protected request",
        [nameof(RoofApiRoutes.OwnKeys)] = "personal reusable keys explicitly require remote mode",
        [nameof(RoofApiRoutes.Pin)] = "kiosk PIN sign-in only",
        [nameof(RoofApiRoutes.KioskPin)] = "kiosk PIN sign-in only",
        [nameof(RoofApiRoutes.CompleteKioskPin)] = "kiosk PIN setup only",
        [nameof(RoofApiRoutes.PinUsers)] = "kiosk PIN sign-in only",
        [nameof(RoofApiRoutes.PinChange)] = "own PIN changes explicitly require remote mode",
        [nameof(RoofApiRoutes.Password)] = "password changes explicitly require remote mode",
        [nameof(RoofApiRoutes.Names)] = "own names require a remote password session",
        [nameof(RoofApiRoutes.HealthReady)] = "anonymous readiness probe",
        [nameof(RoofApiRoutes.HealthLive)] = "anonymous liveness probe",
        [nameof(RoofApiRoutes.CaCertificate)] = "anonymous CA fetch with independent trust verification"
    };

    public static IEnumerable<object[]> LocalRequests() => Calls.Select(call => new object[] { call.Route, call.Method, call.Path, call.Scope });

    [TestMethod]
    [DynamicData(nameof(LocalRequests))]
    public void EveryLocalCliRequest_HasTheExpectedScope(string route, string method, string path, string scope)
        => RoofLocalAuthorization.Operation(new HttpMethod(method), path).Should().Be(scope, $"{method} {route} must request only its declared category");

    [TestMethod]
    public async Task AlpacaStatusSystemSummary_AuthorizesRead_AndSendsTheFreshCredential()
    {
        var operations = new List<string>();
        var requests = 0;
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost/"),
            Credential = new RoofApiKeyCredential("test-unused-saved-key"),
            LocalAuthorization = (operation, _) =>
            {
                operations.Add(operation);
                return Task.FromResult<RoofCredential>(new RoofSessionCredential("test-fresh-read-session"));
            },
            CreateHandler = () => new RoofCliProviderTests.Handler((request, _) =>
            {
                requests++;
                request.Method.Should().Be(HttpMethod.Get);
                request.RequestUri!.AbsolutePath.Should().Be("/" + RoofApiRoutes.SystemSummary);
                request.Headers.Contains("X-Api-Key").Should().BeFalse();
                request.Headers.Authorization!.Parameter.Should().Be("test-fresh-read-session");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SystemSummaryResponse("test-version", 1, DateTimeOffset.UnixEpoch))
                });
            })
        });

        (await client.System.GetSummaryAsync()).ApplicationVersion.Should().Be("test-version");
        operations.Should().Equal("read");
        requests.Should().Be(1);
    }

    [TestMethod]
    public void EveryRouteConstant_IsClassifiedForLocalCli()
    {
        var routes = typeof(RoofApiRoutes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string)).Select(field => field.Name);
        var classified = Calls.Select(call => call.Route).Distinct().Concat(OutsideLocalCli.Keys);
        classified.Should().BeEquivalentTo(routes, "new routes must declare their local authorization category or why local CLI never authorizes them");
        OutsideLocalCli.Values.Should().OnlyContain(reason => !string.IsNullOrWhiteSpace(reason));
    }

    [TestMethod]
    public void EveryRouteBuilder_IsClassifiedForLocalCli()
    {
        var builders = typeof(RoofApiRoutes).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(string)).Select(method => method.Name);
        var classified = BuilderCalls.Select(call => call.Builder).Distinct();
        classified.Should().BeEquivalentTo(builders,
            "every route builder, including query variants and cameras, needs a local CLI scope decision");
    }

    [TestMethod]
    [DataRow("POST", "api/v4.0/System/Summary")]
    [DataRow("DELETE", "api/v4.0/System/Summary")]
    [DataRow("GET", "api/v4.0/Unknown")]
    [DataRow("GET", "api/v4.0/alpaca/unknown")]
    // With a query (#255): a query never makes an unknown route known.
    [DataRow("GET", "api/v4.0/Unknown?x=1")]
    [DataRow("POST", "api/v4.0/RoofControl/Unknown?x=1")]
    [DataRow("POST", "api/v4.0/RoofControl/Unknown?overrideSafety=true")]
    [DataRow("POST", "api/v4.0/RoofControl/Unknown?pulseMs=250")]
    // Near misses of the two query variants: the path boundary holds (no bare prefix match) ...
    [DataRow("POST", "api/v4.0/RoofControl/OpenExtra?overrideSafety=true")]
    [DataRow("POST", "api/v4.0/RoofControl/Open/Extra?overrideSafety=true")]
    [DataRow("POST", "api/v4.0/RoofControl/ClearFault/Extra?pulseMs=250")]
    [DataRow("POST", "api/v4.0/RoofControl/ClearFaultExtra?pulseMs=250")]
    [DataRow("POST", "x/api/v4.0/RoofControl/Open?overrideSafety=true")]
    [DataRow("POST", "x/api/v4.0/RoofControl/ClearFault?pulseMs=250")]
    // ... and only the query the client builds is classified, never another one on the same route.
    [DataRow("POST", "api/v4.0/RoofControl/Open?")]
    [DataRow("POST", "api/v4.0/RoofControl/Open?x=1")]
    [DataRow("POST", "api/v4.0/RoofControl/Open?overrideSafety=false")]
    [DataRow("POST", "api/v4.0/RoofControl/Open?overrideSafety=true&x=1")]
    [DataRow("POST", "api/v4.0/RoofControl/Open?x=1&overrideSafety=true")]
    [DataRow("POST", "api/v4.0/RoofControl/Open?overrideSafety=True")]
    [DataRow("POST", "api/v4.0/RoofControl/ClearFault?")]
    [DataRow("POST", "api/v4.0/RoofControl/ClearFault?pulseMs=")]
    [DataRow("POST", "api/v4.0/RoofControl/ClearFault?pulseMs=abc")]
    [DataRow("POST", "api/v4.0/RoofControl/ClearFault?pulseMs=0250")]
    [DataRow("POST", "api/v4.0/RoofControl/ClearFault?pulseMs=+250")]
    [DataRow("POST", "api/v4.0/RoofControl/ClearFault?pulseMs=250&x=1")]
    [DataRow("POST", "api/v4.0/RoofControl/ClearFault?x=1&pulseMs=250")]
    [DataRow("POST", "api/v4.0/RoofControl/ClearFault?pulse=250")]
    [DataRow("POST", "api/v4.0/RoofControl/ClearFault?overrideSafety=true")]
    [DataRow("POST", "api/v4.0/RoofControl/Close?overrideSafety=true")]
    [DataRow("POST", "api/v4.0/RoofControl/Lease?pulseMs=250")]
    public void UnknownLocalRequests_FailClosed(string method, string path)
    {
        Action authorize = () => RoofLocalAuthorization.Operation(new HttpMethod(method), path);
        authorize.Should().Throw<RoofLocalRouteUnavailableException>();
    }

    private const string Anonymous = "(anonymous: no native authorization)";
    private const string Unavailable = "(refused before sending)";

    /// <summary>
    /// Every public <see cref="RoofControlApi"/> call (#255), each overload, with what the real client code sends in
    /// local mode: the request (path and query, as the controller's contract has them) and the category the native
    /// helper is asked for. Each call is an expression, so the guard below can tell the overloads apart.
    /// </summary>
    private static readonly (Expression<Func<RoofControlApi, Task>> Call, string Method, string Path, string Scope)[] RoofControlCalls =
    [
        (api => api.GetStatusAsync(CancellationToken.None), "GET", "api/v4.0/RoofControl/Status", "read"),
        (api => api.GetModeAsync(CancellationToken.None), "GET", "api/v4.0/RoofControl/Mode", Anonymous),
        (api => api.OpenAsync(CancellationToken.None), "POST", "api/v4.0/RoofControl/Open", "operate"),
        (api => api.OpenAsync(false, CancellationToken.None), "POST", "api/v4.0/RoofControl/Open", "operate"),
        (api => api.OpenAsync(true, CancellationToken.None), "POST", "api/v4.0/RoofControl/Open?overrideSafety=true", "operate"),
        (api => api.CloseAsync(CancellationToken.None), "POST", "api/v4.0/RoofControl/Close", "operate"),
        (api => api.RenewLeaseAsync(CancellationToken.None), "POST", "api/v4.0/RoofControl/Lease", "operate"),
        (api => api.ClearFaultAsync(null, CancellationToken.None), "POST", "api/v4.0/RoofControl/ClearFault", "operate"),
        (api => api.ClearFaultAsync(250, CancellationToken.None), "POST", "api/v4.0/RoofControl/ClearFault?pulseMs=250", "operate"),
        (api => api.ClearFaultAsync(1500, CancellationToken.None), "POST", "api/v4.0/RoofControl/ClearFault?pulseMs=1500", "operate"),
        (api => api.GetHistoryAsync(null, CancellationToken.None), "GET", RoofApiRoutes.HistoryPage(new RoofHistoryQuery()), "read"),
        (api => api.GetHistoryAsync(new RoofHistoryQuery { Mine = true, Take = 5 }, CancellationToken.None), "GET",
            RoofApiRoutes.HistoryPage(new RoofHistoryQuery { Mine = true, Take = 5 }), "read"),
        (api => api.GetSafetyAsync(CancellationToken.None), "GET", "api/v4.0/Safety", Unavailable),
        (api => api.GetConfigurationAsync(CancellationToken.None), "GET", "api/v4.0/RoofControl/Configuration", "read"),
        (api => api.UpdateConfigurationAsync(new RoofConfigurationRequest { ExpectedVersion = 1 }, CancellationToken.None), "POST",
            "api/v4.0/RoofControl/Configuration", "configure")
    ];

    [TestMethod]
    public void EveryRoofControlCall_IsListedForLocalMode()
    {
        var calls = RoofControlCalls.Select(call => ((MethodCallExpression)call.Call.Body).Method.ToString()).Distinct();
        var methods = typeof(RoofControlApi).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName).Select(method => method.ToString());
        calls.Should().BeEquivalentTo(methods,
            "every public RoofControlApi call, each overload, needs its local-mode request and category recorded here");
    }

    /// <summary>
    /// The guard over what the client library actually sends (#255): each RoofControlApi call, in local mode, either
    /// asks the native helper for its declared category and sends exactly its declared request with the fresh session
    /// (never the saved key), or is refused before anything is asked or sent. A query variant the classifier does not
    /// know fails here.
    /// </summary>
    [TestMethod]
    public async Task EveryRoofControlCall_InLocalMode_AsksForItsCategory_AndSendsItsRequest()
    {
        using var scope = new AssertionScope();
        foreach (var (call, method, path, category) in RoofControlCalls)
        {
            var (operations, sent, error) = await RunLocallyAsync(call.Compile());
            var what = call.Body.ToString();
            if (category == Unavailable)
            {
                error.Should().BeOfType<RoofLocalRouteUnavailableException>("{0} is outside the local CLI", what);
                operations.Should().BeEmpty("{0} asks nothing of the native helper", what);
                sent.Should().BeEmpty("{0} sends nothing", what);
                continue;
            }

            string[] asked = category == Anonymous ? [] : [category];
            error.Should().BeOfType<RoofApiException>("{0} is sent, and the test's handler refuses it", what);
            sent.Select(request => request.Line).Should().Equal([$"{method} /{path}"], "{0} sends exactly this request", what);
            sent.Select(request => request.Credential).Should().Equal([category == Anonymous ? "none" : "session"],
                "{0} in local mode carries the fresh native session, never the saved key", what);
            operations.Should().Equal(asked, "{0} asks the native helper for its own category only", what);
        }
    }

    [TestMethod]
    public async Task OpenWithTheOverride_InLocalMode_AsksForOperate_AndSendsTheOverrideQuery()
    {
        var (operations, sent, error) = await RunLocallyAsync(api => api.OpenAsync(overrideSafety: true));

        error.Should().BeOfType<RoofApiException>();
        operations.Should().Equal("operate");
        sent.Should().ContainSingle();
        sent[0].Method.Should().Be(HttpMethod.Post);
        sent[0].Path.Should().Be("/api/v4.0/RoofControl/Open");
        sent[0].Query.Should().Be("?overrideSafety=true");
        sent[0].Credential.Should().Be("session");
    }

    [TestMethod]
    [DataRow(250)]
    [DataRow(300)]
    public async Task ClearFaultWithAPulse_InLocalMode_AsksForOperate_AndSendsThePulse(int pulse)
    {
        var (operations, sent, error) = await RunLocallyAsync(api => api.ClearFaultAsync(pulse));

        error.Should().BeOfType<RoofApiException>();
        operations.Should().Equal("operate");
        sent.Should().ContainSingle();
        sent[0].Method.Should().Be(HttpMethod.Post);
        sent[0].Path.Should().Be("/api/v4.0/RoofControl/ClearFault");
        sent[0].Query.Should().Be($"?pulseMs={pulse}");
        sent[0].Credential.Should().Be("session");
    }

    private const string SavedKey = "test-unused-saved-key";
    private const string PassedOnKeyValue = "test-alpaca-client-key";

    private sealed record SentRequest(HttpMethod Method, string Path, string Query, string Credential)
    {
        public string Line => $"{Method} {Path}{Query}";
    }

    /// <summary>
    /// Runs <paramref name="call"/> on a client in local mode with a saved key it must not use. Its handler records
    /// each request and answers 503, so nothing reaches a controller.
    /// </summary>
    private static Task<(List<string> Operations, List<SentRequest> Sent, Exception? Error)> RunLocallyAsync(Func<RoofControlApi, Task> call)
        => RunClientAsync(client => call(client.Roof));

    /// <summary>
    /// As <see cref="RunLocallyAsync"/>, for a call on any part of the client; without <paramref name="local"/>, in
    /// remote mode, where the saved key is the credential and the native helper is never asked.
    /// </summary>
    private static async Task<(List<string> Operations, List<SentRequest> Sent, Exception? Error)> RunClientAsync(
        Func<RoofControllerClient, Task> call, bool local = true)
    {
        var operations = new List<string>();
        var sent = new List<SentRequest>();
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost/"),
            Credential = new RoofApiKeyCredential(SavedKey),
            LocalAuthorization = !local ? null : (operation, _) =>
            {
                operations.Add(operation);
                return Task.FromResult<RoofCredential>(new RoofSessionCredential("test-fresh-local-session"));
            },
            CreateHandler = () => new RoofCliProviderTests.Handler((request, _) =>
            {
                // Which credential, never its value: a key is the saved one, the one passed on for the call, or another.
                var key = request.Headers.TryGetValues("X-Api-Key", out var keys) ? string.Join(',', keys) : null;
                var credential = key == SavedKey ? "api-key" : key == PassedOnKeyValue ? "passed-on-key" : key is not null ? "other-key"
                    : request.Headers.Authorization is { Scheme: "Bearer", Parameter: "test-fresh-local-session" } ? "session"
                    : request.Headers.Authorization is not null ? "other" : "none";
                sent.Add(new SentRequest(request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, credential));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            })
        });

        try
        {
            await call(client);
            return (operations, sent, null);
        }
        catch (Exception error)
        {
            return (operations, sent, error);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Refs #260. The person-only Alpaca routes (grants, the Stop hold, the operator lease, acknowledging an alert) have
    // a category only exactly as the client builds them: no bare prefix, no other query, no other path under them.
    // ---------------------------------------------------------------------------------------------------------------

    private const string AlpacaClientKey = "(the Alpaca client's own key, passed on: no native authorization)";

    private static readonly string[] GrantReasons = ["scope-unknown:Main"];
    private static readonly RoofCredential PassedOnKey = new RoofApiKeyCredential(PassedOnKeyValue);
    private static readonly RoofAlpacaForwarding Forwarded = new(7, 1, "192.0.2.50", "header");
    private static readonly RoofAlpacaCommandRequest Ticketed = new("fixture-ticket", "fixture-instance", 1);
    private static readonly RoofAlpacaEvent Refused = new("Close", "NotAuthorised", "192.0.2.50", 7, 1);
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Every public <see cref="RoofAlpacaApi"/> call, with what the real client code sends in local mode: the request
    /// (path and query, as the controller's contract has them) and the category the native helper is asked for. The
    /// Alpaca server's own calls carry the Alpaca client's key, which is never the native helper's to replace.
    /// </summary>
    private static readonly (Expression<Func<RoofAlpacaApi, Task>> Call, string Method, string Path, string Scope)[] AlpacaCalls =
    [
        (api => api.GetGrantsAsync(CancellationToken.None), "GET", "api/v4.0/alpaca/grants", "read"),
        (api => api.GrantAsync(RoofAlpacaCommandKind.Close, GrantReasons, CancellationToken.None), "POST", "api/v4.0/alpaca/grants", "operate"),
        (api => api.GrantAsync(RoofAlpacaCommandKind.Open, GrantReasons, CancellationToken.None), "POST", "api/v4.0/alpaca/grants", "operate"),
        (api => api.RevokeAsync(RoofAlpacaCommandKind.Close, CancellationToken.None), "DELETE", "api/v4.0/alpaca/grants/Close", "operate"),
        (api => api.RevokeAsync(RoofAlpacaCommandKind.Open, CancellationToken.None), "DELETE", "api/v4.0/alpaca/grants/Open", "operate"),
        (api => api.ClearHoldAsync(CancellationToken.None), "DELETE", "api/v4.0/alpaca/hold", "operate"),
        (api => api.HoldLeaseAsync(false, CancellationToken.None), "POST", "api/v4.0/alpaca/lease", "operate"),
        (api => api.HoldLeaseAsync(true, CancellationToken.None), "POST", "api/v4.0/alpaca/lease?input=true", "operate"),
        (api => api.ReleaseLeaseAsync(CancellationToken.None), "DELETE", "api/v4.0/alpaca/lease", "operate"),
        (api => api.GetAlertsAsync(CancellationToken.None), "GET", "api/v4.0/alpaca/alerts", "read"),
        (api => api.AcknowledgeAlertAsync("1", CancellationToken.None), "POST", "api/v4.0/alpaca/alerts/1/acknowledge", "operate"),
        (api => api.AcknowledgeAlertAsync("a/b c?d", CancellationToken.None), "POST", "api/v4.0/alpaca/alerts/a%2Fb%20c%3Fd/acknowledge", "operate"),
        (api => api.RequestTicketAsync(RoofAlpacaCommandKind.Close, PassedOnKey, Forwarded, AnswerWithin, CancellationToken.None), "POST",
            "api/v4.0/alpaca/shutter/ticket?kind=Close", AlpacaClientKey),
        (api => api.CloseAsync(Ticketed, PassedOnKey, Forwarded, AnswerWithin, CancellationToken.None), "POST", "api/v4.0/alpaca/shutter/close", AlpacaClientKey),
        (api => api.OpenAsync(Ticketed, PassedOnKey, Forwarded, AnswerWithin, CancellationToken.None), "POST", "api/v4.0/alpaca/shutter/open", AlpacaClientKey),
        (api => api.ReportEventAsync(Refused, CancellationToken.None), "POST", "api/v4.0/alpaca/events", Unavailable)
    ];

    [TestMethod]
    public void EveryAlpacaCall_IsListedForLocalMode()
    {
        var calls = AlpacaCalls.Select(call => ((MethodCallExpression)call.Call.Body).Method.ToString()).Distinct();
        var methods = typeof(RoofAlpacaApi).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName).Select(method => method.ToString());
        calls.Should().BeEquivalentTo(methods,
            "every public RoofAlpacaApi call needs its local-mode request and category recorded here");
    }

    /// <summary>
    /// The guard over what the client library actually sends: each RoofAlpacaApi call, in local mode, asks the native
    /// helper for its declared category and sends exactly its declared request with the fresh session (never the saved
    /// key); or carries the Alpaca client's own key and asks nothing; or is refused before anything is asked or sent. A
    /// person-only request the classifier stops knowing fails here.
    /// </summary>
    [TestMethod]
    public async Task EveryAlpacaCall_InLocalMode_AsksForItsCategory_AndSendsItsRequest()
    {
        using var scope = new AssertionScope();
        foreach (var (call, method, path, category) in AlpacaCalls)
        {
            var send = call.Compile();
            var (operations, sent, error) = await RunClientAsync(client => send(client.Alpaca));
            var what = call.Body.ToString();
            if (category == Unavailable)
            {
                error.Should().BeOfType<RoofLocalRouteUnavailableException>("{0} is outside the local CLI", what);
                operations.Should().BeEmpty("{0} asks nothing of the native helper", what);
                sent.Should().BeEmpty("{0} sends nothing", what);
                continue;
            }

            string[] asked = category == AlpacaClientKey ? [] : [category];
            error.Should().BeOfType<RoofApiException>("{0} is sent, and the test's handler refuses it", what);
            sent.Select(request => request.Line).Should().Equal([$"{method} /{path}"], "{0} sends exactly this request", what);
            sent.Select(request => request.Credential).Should().Equal([category == AlpacaClientKey ? "passed-on-key" : "session"],
                "{0} in local mode carries the fresh native session, or the Alpaca client's own key, never the saved key", what);
            operations.Should().Equal(asked, "{0} asks the native helper for its own category only", what);
        }
    }

    [TestMethod]
    public void EveryAlpacaCommandKind_HasItsRevokeCategory()
    {
        foreach (var kind in Enum.GetValues<RoofAlpacaCommandKind>())
        {
            RoofLocalAuthorization.Operation(HttpMethod.Delete, RoofApiRoutes.AlpacaGrant(kind)).Should().Be("operate", $"revoking the {kind} grant is a person's command");
        }
    }

    /// <summary>
    /// An alert's ID is one path segment however it is spelled: the builder escapes it, and the escaped path is the one
    /// that has a category.
    /// </summary>
    [TestMethod]
    [DataRow("1")]
    [DataRow("42")]
    [DataRow("fixture-alert")]
    [DataRow("a/b")]
    [DataRow("a b")]
    [DataRow("a?b=c")]
    [DataRow("a#b")]
    [DataRow("a&b")]
    [DataRow("100%")]
    [DataRow("%41")]
    [DataRow("%2F")]
    [DataRow("é")]
    [DataRow("x..y")]
    [DataRow("...")]
    [DataRow(".acknowledge")]
    [DataRow("~._-")]
    [DataRow("acknowledge")]
    [DataRow("1/acknowledge")]
    public void AcknowledgingAnAlert_HasItsCategory_WhateverItsId(string id)
    {
        var path = RoofApiRoutes.AcknowledgeAlpacaAlert(id);

        RoofLocalAuthorization.Operation(HttpMethod.Post, path).Should().Be("operate", $"POST {path} acknowledges one alert");
        Action read = () => RoofLocalAuthorization.Operation(HttpMethod.Get, path);
        read.Should().Throw<RoofLocalRouteUnavailableException>($"only POST acknowledges: GET {path} is no request of the client's");
    }

    /// <summary>
    /// An ID that is a dot segment is the one ID whose request goes to another path: the request's address resolves the
    /// segment away (the first half, in remote mode, shows where it goes). So local mode asks nothing for it and sends
    /// nothing.
    /// </summary>
    [TestMethod]
    [DataRow(".", "/api/v4.0/alpaca/alerts/acknowledge")]
    [DataRow("..", "/api/v4.0/alpaca/acknowledge")]
    public async Task AcknowledgingADotSegment_GoesToAnotherPath_SoLocalModeRefusesIt(string id, string resolved)
    {
        var remote = await RunClientAsync(client => client.Alpaca.AcknowledgeAlertAsync(id), local: false);
        var local = await RunClientAsync(client => client.Alpaca.AcknowledgeAlertAsync(id));

        using (new AssertionScope())
        {
            remote.Sent.Select(request => request.Line).Should().Equal([$"POST {resolved}"], "the address resolves a dot segment away");
            remote.Operations.Should().BeEmpty("remote mode never asks the native helper");

            local.Error.Should().BeOfType<RoofLocalRouteUnavailableException>();
            local.Operations.Should().BeEmpty("nothing is asked of the native helper for a request to another path");
            local.Sent.Should().BeEmpty("and nothing is sent");
        }
    }

    /// <summary>
    /// Near misses of the person-only Alpaca requests, each of which had a category while the routes were matched by
    /// prefix: another path that only starts as a known one, another query, another kind, another shape under the
    /// alerts, or another method.
    /// </summary>
    [TestMethod]
    // Grants: only the list itself and one defined kind under it, spelled as the client spells it.
    [DataRow("GET", "api/v4.0/alpaca/grantsExtra")]
    [DataRow("POST", "api/v4.0/alpaca/grantsExtra")]
    [DataRow("DELETE", "api/v4.0/alpaca/grantsExtra/Open")]
    [DataRow("GET", "api/v4.0/alpaca/grants/")]
    [DataRow("POST", "api/v4.0/alpaca/grants/")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/")]
    [DataRow("GET", "api/v4.0/alpaca/grants?")]
    [DataRow("GET", "api/v4.0/alpaca/grants?x=1")]
    [DataRow("POST", "api/v4.0/alpaca/grants?x=1")]
    [DataRow("POST", "api/v4.0/alpaca/grants?input=true")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/Extra")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/open")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/CLOSE")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/OpenExtra")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/Open/")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/Open/Extra")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/Close/Open")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/Open?")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/Open?x=1")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/0")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/1")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/2")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/-1")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/Close,Open")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/ Open")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants//Open")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/%4Fpen")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants/../hold")]
    // The operator lease: only the route itself, and it with the one query the client builds.
    [DataRow("POST", "api/v4.0/alpaca/leaseExtra")]
    [DataRow("DELETE", "api/v4.0/alpaca/leaseExtra")]
    [DataRow("GET", "api/v4.0/alpaca/leaseExtra")]
    [DataRow("POST", "api/v4.0/alpaca/lease/")]
    [DataRow("POST", "api/v4.0/alpaca/lease/Extra")]
    [DataRow("DELETE", "api/v4.0/alpaca/lease/Extra")]
    [DataRow("POST", "api/v4.0/alpaca/lease?")]
    [DataRow("POST", "api/v4.0/alpaca/lease?x=1")]
    [DataRow("DELETE", "api/v4.0/alpaca/lease?x=1")]
    [DataRow("POST", "api/v4.0/alpaca/lease?input")]
    [DataRow("POST", "api/v4.0/alpaca/lease?input=")]
    [DataRow("POST", "api/v4.0/alpaca/lease?input=false")]
    [DataRow("POST", "api/v4.0/alpaca/lease?input=True")]
    [DataRow("POST", "api/v4.0/alpaca/lease?input=TRUE")]
    [DataRow("POST", "api/v4.0/alpaca/lease?input=1")]
    [DataRow("POST", "api/v4.0/alpaca/lease?input=true ")]
    [DataRow("POST", "api/v4.0/alpaca/lease?Input=true")]
    [DataRow("POST", "api/v4.0/alpaca/lease?input=true&x=1")]
    [DataRow("POST", "api/v4.0/alpaca/lease?x=1&input=true")]
    [DataRow("POST", "api/v4.0/alpaca/lease?input=true&input=true")]
    [DataRow("POST", "api/v4.0/alpaca/lease?input=true#x")]
    [DataRow("POST", "api/v4.0/alpaca/lease??input=true")]
    [DataRow("POST", "api/v4.0/alpaca/leaseExtra?input=true")]
    [DataRow("POST", "api/v4.0/alpaca/lease/?input=true")]
    [DataRow("POST", "api/v4.0/alpaca/lease/Extra?input=true")]
    [DataRow("POST", "api/v4.0/alpaca/lease/../hold")]
    // Alerts: only POST alerts/{one escaped ID}/acknowledge.
    [DataRow("POST", "api/v4.0/alpaca/alerts/")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1/")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts//acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1/acknowledgeExtra")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1/acknowledge/")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1/acknowledge/Extra")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1/acknowledge?")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1/acknowledge?x=1")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1/acknowledge#x")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1/Acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1/ack")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1/2/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1/acknowledge/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1?x=1/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/1#x/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/a b/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/a%2fb/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/%41/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/%/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/%zz/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/é/acknowledge")]
    // A dot segment is one the request's address resolves away, so the request would go to another path.
    [DataRow("POST", "api/v4.0/alpaca/alerts/./acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/../acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alerts/../hold")]
    // Another method on one of them: each has its category only as the client sends it.
    [DataRow("PUT", "api/v4.0/alpaca/grants")]
    [DataRow("DELETE", "api/v4.0/alpaca/grants")]
    [DataRow("GET", "api/v4.0/alpaca/grants/Open")]
    [DataRow("POST", "api/v4.0/alpaca/grants/Open")]
    [DataRow("PUT", "api/v4.0/alpaca/grants/Close")]
    [DataRow("GET", "api/v4.0/alpaca/hold")]
    [DataRow("POST", "api/v4.0/alpaca/hold")]
    [DataRow("PUT", "api/v4.0/alpaca/hold")]
    [DataRow("GET", "api/v4.0/alpaca/lease")]
    [DataRow("PUT", "api/v4.0/alpaca/lease")]
    [DataRow("GET", "api/v4.0/alpaca/lease?input=true")]
    [DataRow("DELETE", "api/v4.0/alpaca/lease?input=true")]
    [DataRow("HEAD", "api/v4.0/alpaca/grants")]
    [DataRow("PATCH", "api/v4.0/alpaca/lease")]
    public void AlpacaNearMisses_FailClosed(string method, string path)
    {
        Action authorize = () => RoofLocalAuthorization.Operation(new HttpMethod(method), path);
        authorize.Should().Throw<RoofLocalRouteUnavailableException>();
    }

    /// <summary>
    /// The controls beside them: Alpaca requests that had no category before either. The Stop hold and the alerts'
    /// list were matched exactly already, and the Alpaca server's own routes are outside the local CLI.
    /// </summary>
    [TestMethod]
    [DataRow("GET", "api/v4.0/alpaca")]
    [DataRow("GET", "api/v4.0/alpaca/")]
    [DataRow("GET", "api/v4.0/alpaca/grant")]
    [DataRow("DELETE", "api/v4.0/alpaca/Grants/Open")]
    [DataRow("DELETE", "x/api/v4.0/alpaca/grants/Open")]
    [DataRow("DELETE", "/api/v4.0/alpaca/grants/Open")]
    [DataRow("POST", "api/v4.0/alpaca/leas")]
    [DataRow("POST", "api/v4.0/alpaca/Lease?input=true")]
    [DataRow("POST", "x/api/v4.0/alpaca/lease?input=true")]
    [DataRow("POST", "api/v4.0/RoofControl/Lease?input=true")]
    [DataRow("DELETE", "api/v4.0/alpaca/holdExtra")]
    [DataRow("DELETE", "api/v4.0/alpaca/hold/")]
    [DataRow("DELETE", "api/v4.0/alpaca/hold/Extra")]
    [DataRow("DELETE", "api/v4.0/alpaca/hold?")]
    [DataRow("DELETE", "api/v4.0/alpaca/hold?x=1")]
    [DataRow("DELETE", "api/v4.0/alpaca/Hold")]
    [DataRow("POST", "api/v4.0/alpaca/alerts")]
    [DataRow("DELETE", "api/v4.0/alpaca/alerts")]
    [DataRow("GET", "api/v4.0/alpaca/alertsExtra")]
    [DataRow("GET", "api/v4.0/alpaca/alerts/")]
    [DataRow("GET", "api/v4.0/alpaca/alerts?x=1")]
    [DataRow("GET", "api/v4.0/alpaca/alerts/1")]
    [DataRow("GET", "api/v4.0/alpaca/alerts/1/acknowledge")]
    [DataRow("DELETE", "api/v4.0/alpaca/alerts/1/acknowledge")]
    [DataRow("PUT", "api/v4.0/alpaca/alerts/1/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/alertsExtra/1/acknowledge")]
    [DataRow("POST", "x/api/v4.0/alpaca/alerts/1/acknowledge")]
    [DataRow("POST", "api/v4.0/alpaca/shutter/ticket")]
    [DataRow("POST", "api/v4.0/alpaca/shutter/ticket?kind=Close")]
    [DataRow("POST", "api/v4.0/alpaca/shutter/close")]
    [DataRow("POST", "api/v4.0/alpaca/shutter/open")]
    [DataRow("POST", "api/v4.0/alpaca/events")]
    public void OtherUnknownAlpacaRequests_FailClosed(string method, string path)
    {
        Action authorize = () => RoofLocalAuthorization.Operation(new HttpMethod(method), path);
        authorize.Should().Throw<RoofLocalRouteUnavailableException>();
    }
}
