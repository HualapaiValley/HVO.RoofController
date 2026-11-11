using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Cli;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// Refs #263: extend the recorded-request guard beyond RoofControlApi to the other typed REST APIs and Stop.
/// RoofLocalRouteCoverageTests retains the RoofControlApi overload/query guard and the constant/builder catalogue.
/// All HTTP goes to a recording handler; no controller, camera or other device is contacted.
/// </summary>
[TestClass]
public sealed class RoofLocalClientCallCoverageTests
{
    private const string Anonymous = "anonymous";
    private const string Unavailable = "unavailable";
    private const string ExplicitKey = "explicit-key";
    private const string SavedKey = "test-unused-saved-key";
    private const string ClientKey = "test-alpaca-client-key";

    private static readonly RoofAlpacaForwarding Forwarding = new(1, 2, "127.0.0.1", "header");

    // Literal expectations are independent of the builders and classifier. Expressions identify overloads, while
    // invocation checks the real call sites, including inline paths and the false/true lease query variants.
    private static readonly (Expression<Func<RoofControllerClient, Task>> Call, string Method, string Path, string Scope)[] Calls =
    [
        (c => c.Auth.SignInAsync("fixture-user", "fixture-password", CancellationToken.None), "POST", "api/v4.0/Auth/Session", Anonymous),
        (c => c.Auth.GetProvidersAsync(CancellationToken.None), "GET", "api/v4.0/Auth/Providers", Anonymous),
        (c => c.Auth.SignInWithProviderAsync("linux", "fixture-user", "fixture-password", CancellationToken.None), "POST", "api/v4.0/Auth/ProviderSession", Anonymous),
        (c => c.Auth.SignInWithProviderAsync("virtual", "fixture-user", "fixture-password", CancellationToken.None), "POST", "api/v4.0/Auth/ProviderSession", Anonymous),
        (c => c.Auth.SignInLocallyAsync("fixture-assertion", CancellationToken.None), "POST", "api/v4.0/Auth/LocalSession", Anonymous),
        (c => c.Auth.SignInOnDeviceAsync("fixture-user", "fixture-password", CancellationToken.None), "POST", "api/v4.0/Auth/Session", Anonymous),
        (c => c.Auth.SignInWithPinAsync("fixture-user", "fixture-pin", CancellationToken.None), "POST", "api/v4.0/Auth/Pin", Unavailable),
        (c => c.Auth.GetPinUsersAsync(CancellationToken.None), "GET", "api/v4.0/Auth/Pin/Users", Unavailable),
        (c => c.Auth.SignInWithPrincipalPinAsync("fixture-id", "fixture-pin", CancellationToken.None), "POST", "api/v4.0/Auth/KioskPin", Unavailable),
        (c => c.Auth.CompleteKioskSetupAsync("fixture-grant", "fixture-pin", "fixture-pin", CancellationToken.None), "POST", "api/v4.0/Auth/KioskPin/Complete", Unavailable),
        (c => c.Auth.GetCallerAsync(CancellationToken.None), "GET", "api/v4.0/Auth/Me", "read"),
        (c => c.Auth.RefreshAsync(CancellationToken.None), "GET", "api/v4.0/Auth/Me", "read"),
        (c => c.Auth.SignOutAsync(CancellationToken.None), "DELETE", "api/v4.0/Auth/Session", Unavailable),
        (c => c.Auth.ChangeNamesAsync(new RoofNamesChangeRequest(), CancellationToken.None), "POST", "api/v4.0/Auth/Names", Unavailable),
        (c => c.Auth.ChangePasswordAsync("fixture-old", "fixture-new", CancellationToken.None), "POST", "api/v4.0/Auth/Password", Unavailable),
        (c => c.Auth.ChangePinAsync("fixture-old", "fixture-new", CancellationToken.None), "POST", "api/v4.0/Auth/Pin/Change", Unavailable),
        (c => c.Auth.GetOwnedKeysAsync(CancellationToken.None), "GET", "api/v4.0/Auth/Keys", Unavailable),
        (c => c.Auth.CreateOwnedKeyAsync(new RoofOwnedKeyCreateRequest(), CancellationToken.None), "POST", "api/v4.0/Auth/Keys", Unavailable),
        (c => c.Auth.RevokeOwnedKeyAsync("fixture/id", CancellationToken.None), "DELETE", "api/v4.0/Auth/Keys/fixture%2Fid", Unavailable),

        (c => c.Identity.GetPrincipalsAsync(CancellationToken.None), "GET", "api/v4.0/Identity/Principals", "identity"),
        (c => c.Identity.EnrollPrincipalAsync(new RoofPrincipalEnrollRequest(), CancellationToken.None), "POST", "api/v4.0/Identity/Principals", "identity"),
        (c => c.Identity.UpdatePrincipalAsync("fixture/id", new RoofPrincipalUpdateRequest(), CancellationToken.None), "PUT", "api/v4.0/Identity/Principals/fixture%2Fid", "identity"),
        (c => c.Identity.GetUsersAsync(CancellationToken.None), "GET", "api/v4.0/Identity/Users", "identity"),
        (c => c.Identity.GetUserAsync("fixture-user", CancellationToken.None), "GET", "api/v4.0/Identity/Users/fixture-user", "identity"),
        (c => c.Identity.AddUserAsync(new RoofUserCreateRequest(), CancellationToken.None), "POST", "api/v4.0/Identity/Users", "identity"),
        (c => c.Identity.UpdateUserAsync("fixture-user", new RoofUserUpdateRequest(), CancellationToken.None), "PUT", "api/v4.0/Identity/Users/fixture-user", "identity"),
        (c => c.Identity.SetPinAsync("fixture-user", "fixture-pin", CancellationToken.None), "PUT", "api/v4.0/Identity/Users/fixture-user/Pin", "identity"),
        (c => c.Identity.RemovePinAsync("fixture-user", CancellationToken.None), "DELETE", "api/v4.0/Identity/Users/fixture-user/Pin", "identity"),
        (c => c.Identity.RemoveUserAsync("fixture-user", CancellationToken.None), "DELETE", "api/v4.0/Identity/Users/fixture-user", "identity"),
        (c => c.Identity.GetApiKeysAsync(CancellationToken.None), "GET", "api/v4.0/Identity/ApiKeys", "identity"),
        (c => c.Identity.AddApiKeyAsync(new RoofApiKeyCreateRequest(), CancellationToken.None), "POST", "api/v4.0/Identity/ApiKeys", "identity"),
        (c => c.Identity.UpdateApiKeyAsync("fixture/key", new RoofApiKeyUpdateRequest(), CancellationToken.None), "PUT", "api/v4.0/Identity/ApiKeys/fixture%2Fkey", "identity"),
        (c => c.Identity.RotateApiKeyAsync("fixture/key", CancellationToken.None), "POST", "api/v4.0/Identity/ApiKeys/fixture%2Fkey/Rotate", "identity"),
        (c => c.Identity.RemoveApiKeyAsync("fixture/key", CancellationToken.None), "DELETE", "api/v4.0/Identity/ApiKeys/fixture%2Fkey", "identity"),
        (c => c.Identity.GetSessionsAsync(CancellationToken.None), "GET", "api/v4.0/Identity/Sessions", "identity"),
        (c => c.Identity.EndSessionAsync("fixture/id", CancellationToken.None), "DELETE", "api/v4.0/Identity/Sessions/fixture%2Fid", "identity"),

        (c => c.Settings.GetCatalogueAsync(CancellationToken.None), "GET", "api/v4.0/Settings/Catalogue", "read"),
        (c => c.Settings.GetAsync(CancellationToken.None), "GET", "api/v4.0/Settings", "read"),
        (c => c.Settings.UpdateAsync("fixture/group", new RoofSettingsUpdateRequest(), CancellationToken.None), "POST", "api/v4.0/Settings/fixture%2Fgroup", "configure"),
        (c => c.Settings.ApplyHandEditAsync(new RoofSettingsHandEditRequest(), CancellationToken.None), "POST", "api/v4.0/Settings/Reload", "configure"),
        (c => c.Settings.DiscardHandEditAsync(new RoofSettingsHandEditRequest(), CancellationToken.None), "POST", "api/v4.0/Settings/Discard", "configure"),

        (c => c.System.RestartAsync(false, CancellationToken.None), "POST", "api/v4.0/System/Restart", "configure"),
        (c => c.System.GetSummaryAsync(CancellationToken.None), "GET", "api/v4.0/System/Summary", "read"),
        (c => c.System.GetInformationAsync(CancellationToken.None), "GET", "api/v1.0/System/info", "read"),
        (c => c.System.GetMetricsAsync(CancellationToken.None), "GET", "api/v1.0/System/metrics", "read"),
        (c => c.Health.GetReportAsync(CancellationToken.None), "GET", "health", "read"),
        (c => c.Health.GetReadinessAsync(CancellationToken.None), "GET", "health/ready", Anonymous),
        (c => c.Health.GetLivenessAsync(CancellationToken.None), "GET", "health/live", Anonymous),
        (c => c.Camera.OpenAsync(1, CancellationToken.None), "GET", "api/v1.0/Camera/1/mjpeg", "read"),
        (c => c.Camera.OpenAsync(99, CancellationToken.None), "GET", "api/v1.0/Camera/99/mjpeg", "read"),

        (c => c.Alpaca.GetGrantsAsync(CancellationToken.None), "GET", "api/v4.0/alpaca/grants", "read"),
        (c => c.Alpaca.GrantAsync(RoofAlpacaCommandKind.Close, new[] { "fixture-reason" }, CancellationToken.None), "POST", "api/v4.0/alpaca/grants", "operate"),
        (c => c.Alpaca.GrantAsync(RoofAlpacaCommandKind.Open, new[] { "fixture-reason" }, CancellationToken.None), "POST", "api/v4.0/alpaca/grants", "operate"),
        (c => c.Alpaca.RevokeAsync(RoofAlpacaCommandKind.Close, CancellationToken.None), "DELETE", "api/v4.0/alpaca/grants/Close", "operate"),
        (c => c.Alpaca.RevokeAsync(RoofAlpacaCommandKind.Open, CancellationToken.None), "DELETE", "api/v4.0/alpaca/grants/Open", "operate"),
        (c => c.Alpaca.ClearHoldAsync(CancellationToken.None), "DELETE", "api/v4.0/alpaca/hold", "operate"),
        (c => c.Alpaca.HoldLeaseAsync(false, CancellationToken.None), "POST", "api/v4.0/alpaca/lease", "operate"),
        (c => c.Alpaca.HoldLeaseAsync(true, CancellationToken.None), "POST", "api/v4.0/alpaca/lease?input=true", "operate"),
        (c => c.Alpaca.ReleaseLeaseAsync(CancellationToken.None), "DELETE", "api/v4.0/alpaca/lease", "operate"),
        (c => c.Alpaca.GetAlertsAsync(CancellationToken.None), "GET", "api/v4.0/alpaca/alerts", "read"),
        (c => c.Alpaca.AcknowledgeAlertAsync("fixture/id", CancellationToken.None), "POST", "api/v4.0/alpaca/alerts/fixture%2Fid/acknowledge", "operate"),
        (c => c.Alpaca.RequestTicketAsync(RoofAlpacaCommandKind.Close, new RoofApiKeyCredential(ClientKey), Forwarding, TimeSpan.FromSeconds(1), CancellationToken.None), "POST", "api/v4.0/alpaca/shutter/ticket?kind=Close", ExplicitKey),
        (c => c.Alpaca.RequestTicketAsync(RoofAlpacaCommandKind.Open, new RoofApiKeyCredential(ClientKey), Forwarding, TimeSpan.FromSeconds(1), CancellationToken.None), "POST", "api/v4.0/alpaca/shutter/ticket?kind=Open", ExplicitKey),
        (c => c.Alpaca.CloseAsync(new RoofAlpacaCommandRequest(null, null, null), new RoofApiKeyCredential(ClientKey), Forwarding, TimeSpan.FromSeconds(1), CancellationToken.None), "POST", "api/v4.0/alpaca/shutter/close", ExplicitKey),
        (c => c.Alpaca.OpenAsync(new RoofAlpacaCommandRequest(null, null, null), new RoofApiKeyCredential(ClientKey), Forwarding, TimeSpan.FromSeconds(1), CancellationToken.None), "POST", "api/v4.0/alpaca/shutter/open", ExplicitKey),
        (c => c.Alpaca.ReportEventAsync(new RoofAlpacaEvent("fixture", "fixture", null, 1, 2), CancellationToken.None), "POST", "api/v4.0/alpaca/events", Unavailable),
        (c => c.StopAsync(CancellationToken.None), "POST", "api/v4.0/RoofControl/Stop", "read")
    ];

    public static IEnumerable<object[]> RecordedCalls()
        => Calls.Select((call, index) => new object[] { call.Call.Body.ToString(), index });

    public static IEnumerable<object[]> NativeCalls()
        => RecordedCalls().Where(row => Calls[(int)row[1]].Scope is not (Anonymous or Unavailable or ExplicitKey));

    [TestMethod]
    public void EveryTypedRestApi_AndEveryOverload_HasARecordedRequestDecision()
    {
        var methods = Calls.Select(call => ((MethodCallExpression)call.Call.Body).Method).ToArray();
        var apis = typeof(RoofControllerClient).GetProperties()
            .Select(property => property.PropertyType).Where(type => type.Name.EndsWith("Api", StringComparison.Ordinal));
        methods.Select(method => method.DeclaringType).Where(type => type != typeof(RoofControllerClient)).Distinct()
            .Append(typeof(RoofControlApi)).Should().BeEquivalentTo(apis,
                "each new API property needs a recorded-request guard; RoofControlApi has its existing guard");
        var expected = apis.Where(type => type != typeof(RoofControlApi))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName)
            .Concat(typeof(RoofControllerClient).GetMethods().Where(method => method.Name == nameof(RoofControllerClient.StopAsync)));
        methods.Distinct().Should().BeEquivalentTo(expected, "every public overload needs an explicit local-mode decision");
    }

    [TestMethod]
    [DynamicData(nameof(RecordedCalls))]
    public async Task ActualCall_InLocalMode_UsesItsDeclaredScopeRequestAndCredential(string name, int index)
    {
        var (call, method, path, scope) = Calls[index];
        using var rig = new RecordingRig();
        var error = await InvokeAsync(call.Compile(), rig.Client);

        if (scope == Unavailable)
        {
            error.Should().BeOfType<RoofLocalRouteUnavailableException>(name);
            rig.Operations.Should().BeEmpty(name);
            rig.Sent.Should().BeEmpty(name);
            return;
        }

        error.Should().BeOfType<RoofApiException>("{0} must reach the refusing handler", name);
        rig.Operations.Should().Equal(scope is Anonymous or ExplicitKey ? [] : new[] { scope }, name);
        var credential = scope == Anonymous ? "none" : scope == ExplicitKey ? "explicit-key" : "session-1";
        rig.Sent.Should().Equal(new[] { $"{method} /{path} {credential}" }, "{0}", name);
    }

    [TestMethod]
    [DynamicData(nameof(NativeCalls))]
    public async Task NativeDenial_SendsNothing_ThenRecoveryObtainsFreshSessions(string name, int index)
    {
        var (expression, _, _, scope) = Calls[index];
        var call = expression.Compile();
        using var rig = new RecordingRig { Deny = true };

        (await InvokeAsync(call, rig.Client)).Should().BeOfType<RoofLocalAuthorizationException>(name);
        rig.Operations.Should().Equal(new[] { scope }, "{0}", name);
        rig.Sent.Should().BeEmpty("a failed native gate never falls back to the saved credential: {0}", name);

        rig.Deny = false;
        (await InvokeAsync(call, rig.Client)).Should().BeOfType<RoofApiException>(name);
        (await InvokeAsync(call, rig.Client)).Should().BeOfType<RoofApiException>(name);
        rig.Operations.Should().Equal(scope, scope, scope);
        rig.Sent.Should().HaveCount(2).And.EndWith(rig.Sent[0].Replace("session-2", "session-3"));
        rig.Sent[0].Should().EndWith(" session-2");
    }

    private static async Task<Exception?> InvokeAsync(Func<RoofControllerClient, Task> call, RoofControllerClient client)
    {
        try
        {
            var task = call(client);
            await task;
            // Stop reports refusals in its result instead of throwing.
            return task is Task<RoofStopResult> stop ? stop.Result.Error : null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private sealed class RecordingRig : IDisposable
    {
        public List<string> Operations { get; } = [];
        public List<string> Sent { get; } = [];
        public bool Deny { get; set; }
        public RoofControllerClient Client { get; }

        public RecordingRig()
        {
            Client = new RoofControllerClient(new RoofConnectionOptions
            {
                BaseAddress = new Uri("http://127.0.0.1/"),
                // A kiosk credential also admits the device-auth methods to their real HTTP/classification path.
                Credential = new RoofKioskCredential(SavedKey),
                LocalAuthorization = (operation, _) =>
                {
                    Operations.Add(operation);
                    if (Deny) throw new InvalidOperationException("fixture native denial");
                    return Task.FromResult<RoofCredential>(new RoofSessionCredential($"test-session-{Operations.Count}"));
                },
                CreateHandler = () => new RoofCliProviderTests.Handler((request, _) =>
                {
                    var key = request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null;
                    var authorization = request.Headers.Authorization;
                    var credential = key is null && authorization is null ? "none"
                        : key == ClientKey && authorization is null ? "explicit-key"
                        : key is null && authorization is { Scheme: "Bearer" }
                            && authorization.Parameter == $"test-session-{Operations.Count}" ? $"session-{Operations.Count}"
                        : "unexpected-credential";
                    Sent.Add($"{request.Method} {request.RequestUri!.PathAndQuery} {credential}");
                    // 502 avoids the health API's deliberately accepted 503. No response body or stream is needed.
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
                })
            });
        }

        public void Dispose() => Client.Dispose();
    }
}
