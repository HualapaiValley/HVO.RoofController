using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using HVO.RoofControllerV4.RPi.Tests.Cli;
using FluentAssertions;
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
        (nameof(RoofApiRoutes.Settings), "GET", RoofApiRoutes.Settings + "?forConfiguration=true", "configure"),
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
    [DataRow("POST", "api/v4.0/System/Summary")]
    [DataRow("DELETE", "api/v4.0/System/Summary")]
    [DataRow("GET", "api/v4.0/Unknown")]
    [DataRow("GET", "api/v4.0/alpaca/unknown")]
    public void UnknownLocalRequests_FailClosed(string method, string path)
    {
        Action authorize = () => RoofLocalAuthorization.Operation(new HttpMethod(method), path);
        authorize.Should().Throw<RoofLocalRouteUnavailableException>();
    }
}
