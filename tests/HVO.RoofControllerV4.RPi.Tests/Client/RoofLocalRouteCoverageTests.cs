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

        // The roof commands' query variants (Refs #255), as RoofControlApi builds them: the category without the query.
        (nameof(RoofApiRoutes.OpenCommand), "POST", RoofApiRoutes.OpenCommand(overrideSafety: true), "operate"),
        (nameof(RoofApiRoutes.OpenCommand), "POST", RoofApiRoutes.OpenCommand(overrideSafety: false), "operate"),
        (nameof(RoofApiRoutes.ClearFaultCommand), "POST", RoofApiRoutes.ClearFaultCommand(250), "operate"),
        (nameof(RoofApiRoutes.ClearFaultCommand), "POST", RoofApiRoutes.ClearFaultCommand(RoofControllerLimits.MinClearFaultPulseMilliseconds), "operate"),
        (nameof(RoofApiRoutes.ClearFaultCommand), "POST", RoofApiRoutes.ClearFaultCommand(RoofControllerLimits.MaxClearFaultPulseMilliseconds), "operate"),
        (nameof(RoofApiRoutes.ClearFaultCommand), "POST", RoofApiRoutes.ClearFaultCommand(null), "operate")
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
    // With a query (Refs #255): a query never makes an unknown route known.
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
    /// Every public <see cref="RoofControlApi"/> call (Refs #255), each overload, with what the real client code sends in
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
    /// The guard over what the client library actually sends (Refs #255): each RoofControlApi call, in local mode, either
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

    private sealed record SentRequest(HttpMethod Method, string Path, string Query, string Credential)
    {
        public string Line => $"{Method} {Path}{Query}";
    }

    /// <summary>
    /// Runs <paramref name="call"/> on a client in local mode with a saved key it must not use. Its handler records
    /// each request and answers 503, so nothing reaches a controller.
    /// </summary>
    private static async Task<(List<string> Operations, List<SentRequest> Sent, Exception? Error)> RunLocallyAsync(Func<RoofControlApi, Task> call)
    {
        var operations = new List<string>();
        var sent = new List<SentRequest>();
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost/"),
            Credential = new RoofApiKeyCredential("test-unused-saved-key"),
            LocalAuthorization = (operation, _) =>
            {
                operations.Add(operation);
                return Task.FromResult<RoofCredential>(new RoofSessionCredential("test-fresh-local-session"));
            },
            CreateHandler = () => new RoofCliProviderTests.Handler((request, _) =>
            {
                var credential = request.Headers.Contains("X-Api-Key") ? "api-key"
                    : request.Headers.Authorization is { Scheme: "Bearer", Parameter: "test-fresh-local-session" } ? "session"
                    : request.Headers.Authorization is not null ? "other" : "none";
                sent.Add(new SentRequest(request.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query, credential));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            })
        });

        try
        {
            await call(client.Roof);
            return (operations, sent, null);
        }
        catch (Exception error)
        {
            return (operations, sent, error);
        }
    }
}
