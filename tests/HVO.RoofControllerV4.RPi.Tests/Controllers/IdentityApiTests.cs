using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Hubs;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using WallClock = HVO.RoofControllerV4.RPi.Tests.Security.ManualTimeProvider;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// People, sessions and managed API keys through the REST API: signing in by password and at a kiosk by PIN, what a
/// session may do, lockout, the admin endpoints, the file across a restart, and an unavailable store.
/// </summary>
[TestClass]
public sealed class IdentityApiTests
{
    private const string Auth = "/api/v4.0/Auth";
    private const string Identity = "/api/v4.0/Identity";
    private const string Roof = "/api/v4.0/RoofControl";
    private const string KioskKey = "test-kiosk-key-not-a-real-secret-05";

    private static Dictionary<string, string?> Settings(string? storePath = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:ApiKeys:4:Name"] = "test-kiosk",
            ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.ViewerRole,
            ["RoofControllerSecurity:ApiKeys:4:Key"] = KioskKey,
            ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "true"
        };
        if (storePath is not null)
        {
            settings["RoofControllerSecurity:Identity:StorePath"] = storePath;
        }

        return settings;
    }

    private static RoofApiTestHost CreateHost(
        string? storePath = null,
        RecordingLoggerProvider? logs = null,
        TimeProvider? time = null,
        string environment = "Development",
        IReadOnlyDictionary<string, string?>? extraSettings = null)
        => new(
            settings: Settings(storePath).Concat(extraSettings ?? new Dictionary<string, string?>()).ToDictionary(),
            environment: environment,
            configureServices: services =>
            {
                // A cheap hash so the tests run quickly; production uses the ASP.NET Core default.
                services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000);
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }

                if (time is not null)
                {
                    services.AddSingleton(time);
                }
            });

    private static HttpClient Bearer(RoofApiTestHost host, string token)
    {
        var client = host.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, token);
        return client;
    }

    private static async Task AddUserAsync(RoofApiTestHost host, string name, string role, string? password = TestSecrets.Password, string? pin = null)
    {
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var response = await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = name, Role = role, Password = password, Pin = pin });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<RoofSessionResponse> SignInAsync(RoofApiTestHost host, string name, string password = TestSecrets.Password)
    {
        using var anonymous = host.CreateApiClient();
        var response = await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = name, Password = password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ApiJson.ReadAsync<RoofSessionResponse>(response);
    }

    private static async Task<(int Status, string? Code, string? Detail)> ProblemAsync(HttpResponseMessage response)
    {
        var body = await ApiJson.ReadElementAsync(response);
        body.GetProperty("type").GetString().Should().StartWith("urn:hvo:roof-controller:");
        return ((int)response.StatusCode, body.GetProperty("code").GetString(), body.GetProperty("detail").GetString());
    }

    // ---- Signing in with a password -------------------------------------------------------------------------------

    [TestMethod]
    public async Task APersonAddedByAnAdmin_SignsIn_AndTheSessionCarriesTheirRole()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var anonymous = host.CreateApiClient();

        var response = await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = "ALICE", Password = TestSecrets.Password });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var session = await ApiJson.ReadAsync<RoofSessionResponse>(response);
        session.Token.Should().StartWith("hvo_s_");
        session.Name.Should().Be("alice");
        session.Role.Should().Be(RoofControllerApiContract.OperatorRole);
        session.Kind.Should().Be(RoofCredentialKind.Session);
        session.IdleTimeoutSeconds.Should().BeNull();
        session.ExpiresUtc.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(12), TimeSpan.FromMinutes(1));

        using var alice = Bearer(host, session.Token);
        var me = await ApiJson.ReadAsync<RoofCallerResponse>(await alice.GetAsync($"{Auth}/Me"));
        me.Should().BeEquivalentTo(new RoofCallerResponse("alice", RoofControllerApiContract.OperatorRole, RoofCredentialKind.Session, null, session.SessionId, session.ExpiresUtc, false));
        (await alice.PostAsync($"{Roof}/Open", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await alice.GetAsync($"{Identity}/Users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task AViewersSession_CanReadAndStop_ButNotOpen()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "victor", RoofControllerApiContract.ViewerRole);
        using var victor = Bearer(host, (await SignInAsync(host, "victor")).Token);

        (await victor.GetAsync($"{Roof}/Status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await victor.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await victor.PostAsync($"{Roof}/Open", content: null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    [DataRow(RoofControllerApiContract.ViewerRole)]
    [DataRow(RoofControllerApiContract.OperatorRole)]
    [DataRow(RoofControllerApiContract.AdminRole)]
    public async Task EachRolesSession_IsAllowedAndRefused_AsTheRoleSays(string role)
    {
        // Production, so the OpenAPI document needs the Admin role as it does on the Pi.
        using var host = CreateHost(environment: "Production");
        await AddUserAsync(host, "pat", role);
        using var pat = Bearer(host, (await SignInAsync(host, "pat")).Token);
        var isOperator = role != RoofControllerApiContract.ViewerRole;
        var isAdmin = role == RoofControllerApiContract.AdminRole;

        var calls = new (string Call, Func<Task<HttpResponseMessage>> Send, bool Allowed)[]
        {
            ("GET Status", () => pat.GetAsync($"{Roof}/Status"), true),
            ("POST Stop", () => pat.PostAsync($"{Roof}/Stop", content: null), true),
            ("GET /health", () => pat.GetAsync("/health"), true),
            ("GET Auth/Me", () => pat.GetAsync($"{Auth}/Me"), true),
            ("POST Open", () => pat.PostAsync($"{Roof}/Open", content: null), isOperator),
            ("POST Close", () => pat.PostAsync($"{Roof}/Close", content: null), isOperator),
            ("POST ClearFault", () => pat.PostAsync($"{Roof}/ClearFault", content: null), isOperator),
            ("GET Configuration", () => pat.GetAsync($"{Roof}/Configuration"), isAdmin),
            ("GET System/info", () => pat.GetAsync("/api/v1.0/System/info"), isAdmin),
            ("GET Identity/Users", () => pat.GetAsync($"{Identity}/Users"), isAdmin),
            ("GET Identity/Sessions", () => pat.GetAsync($"{Identity}/Sessions"), isAdmin),
            ("GET /openapi/v4.json", () => pat.GetAsync("/openapi/v4.json"), isAdmin)
        };

        foreach (var (call, send, allowed) in calls)
        {
            using var response = await send();
            if (allowed)
            {
                response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, $"{role} may {call}");
                response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, $"{role} may {call}");
                ((int)response.StatusCode).Should().BeLessThan(500, $"{role} {call}: {await response.Content.ReadAsStringAsync()}");
            }
            else
            {
                response.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} may not {call}");
            }
        }
    }

    [TestMethod]
    public async Task AnExpiredWebSession_IsRefused_ButTheUisOwnKeyStillStops_NamingThePerson()
    {
        var clock = new WallClock(DateTimeOffset.UtcNow);
        using var logs = new RecordingLoggerProvider();
        using var host = CreateHost(logs: logs, time: clock);
        await AddUserAsync(host, "wendy", RoofControllerApiContract.OperatorRole);
        using var wendy = Bearer(host, (await SignInAsync(host, "wendy")).Token);
        (await wendy.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        clock.Now += TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1);

        var expired = await wendy.PostAsync($"{Roof}/Stop", content: null);
        expired.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        expired.Headers.WwwAuthenticate.ToString().Should().Contain("error=\"invalid_token\"");

        // The web UI holds its own key for Stop, and says whom it acts for.
        using var ui = host.CreateApiClient(TestApiKeys.Viewer);
        ui.DefaultRequestHeaders.Add(RoofIdentityContract.OnBehalfOfHeaderName, "wendy");
        (await ui.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        logs.Entries.Select(entry => entry.Message)
            .Should().Contain(m => m.Contains("Roof command stop requested by test-viewer for wendy", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnIdlePinSession_Ends_AndTheLockedKioskStillStops()
    {
        var clock = new WallClock(DateTimeOffset.UtcNow);
        using var host = CreateHost(time: clock);
        await AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, password: null, pin: TestSecrets.Pin);
        using var kiosk = host.CreateApiClient(KioskKey);
        var session = await ApiJson.ReadAsync<RoofSessionResponse>(
            await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin }));
        using var olive = Bearer(host, session.Token);

        clock.Now += TimeSpan.FromMinutes(9);
        (await olive.PostAsync($"{Roof}/Open", content: null)).StatusCode.Should().Be(HttpStatusCode.OK, "activity keeps a PIN session open");
        clock.Now += TimeSpan.FromMinutes(9);
        (await olive.GetAsync($"{Roof}/Status")).StatusCode.Should().Be(HttpStatusCode.OK);

        clock.Now += TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1);

        (await olive.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await kiosk.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await kiosk.PostAsync($"{Roof}/Open", content: null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task AnUnknownName_AWrongPassword_AndAMalformedOne_GetTheSameAnswer()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var anonymous = host.CreateApiClient();

        var answers = new List<(int, string?, string?)>();
        foreach (var request in new[]
                 {
                     new RoofSignInRequest { Name = "mallory", Password = TestSecrets.Password },
                     new RoofSignInRequest { Name = "alice", Password = TestSecrets.OtherPassword },
                     new RoofSignInRequest { Name = "-not a name-", Password = TestSecrets.Password },
                     new RoofSignInRequest { Name = "alice", Password = new string('x', RoofIdentityContract.MaximumPasswordLength + 1) }
                 })
        {
            var response = await anonymous.PostAsJsonAsync($"{Auth}/Session", request);
            response.Headers.RetryAfter.Should().BeNull();
            answers.Add(await ProblemAsync(response));
        }

        answers.Should().AllBeEquivalentTo((401, "SignInFailed", "The name, password or PIN is not correct."));
    }

    [TestMethod]
    public async Task RepeatedFailures_Give429WithRetryAfter_AndStopStillWorks()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var anonymous = host.CreateApiClient();
        for (var i = 0; i < 4; i++)
        {
            (await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = "alice", Password = TestSecrets.OtherPassword }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var fifth = await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = "alice", Password = TestSecrets.OtherPassword });
        var locked = await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = "alice", Password = TestSecrets.Password });

        (await ProblemAsync(fifth)).Code.Should().Be("SignInLockedOut");
        fifth.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromMinutes(5));
        var lockedProblem = await ProblemAsync(locked);
        lockedProblem.Status.Should().Be(429);
        lockedProblem.Detail.Should().Contain("Stop still works");
        locked.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.FromMinutes(4));
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);
        (await viewer.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        host.RoofService.Verify(s => s.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once);
    }

    [TestMethod]
    public async Task ABadBearerToken_IsRefused_EvenWithAGoodApiKey()
    {
        using var host = CreateHost();
        using var client = host.CreateApiClient(TestApiKeys.Admin);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, "hvo_s_not-a-session");

        var response = await client.GetAsync($"{Auth}/Me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a Bearer header selects session sign-in; the API key is not a fallback");
        response.Headers.WwwAuthenticate.Should().Contain(header => header.Scheme == RoofIdentityContract.BearerScheme);
    }

    [TestMethod]
    public async Task AnExpiredSessionSentWithAGoodApiKey_StillStops_ButNothingElseFallsBackToTheKey()
    {
        var clock = new WallClock(DateTimeOffset.UtcNow);
        using var logs = new RecordingLoggerProvider();
        using var host = CreateHost(logs: logs, time: clock);
        await AddUserAsync(host, "wendy", RoofControllerApiContract.OperatorRole);
        var token = (await SignInAsync(host, "wendy")).Token;
        using var client = host.CreateApiClient(TestApiKeys.Viewer);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, token);
        (await client.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        logs.Entries.Select(entry => entry.Message)
            .Should().Contain(m => m.Contains("Roof command stop requested by wendy", StringComparison.Ordinal), "a live session is used first");

        clock.Now += TimeSpan.FromHours(12) + TimeSpan.FromSeconds(1);

        (await client.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.OK, "Stop takes the key beside a dead session");
        logs.Entries.Select(entry => entry.Message)
            .Should().Contain(m => m.Contains("Roof command stop requested by test-viewer", StringComparison.Ordinal));
        (await client.GetAsync($"{Roof}/Status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "only Stop falls back to the key");

        using var noKey = Bearer(host, token);
        var refused = await noKey.PostAsync($"{Roof}/Stop", content: null);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        refused.Headers.WwwAuthenticate.ToString().Should().Contain("error=\"invalid_token\"");
        using var badKey = host.CreateApiClient("test-wrong-key-not-a-real-secret-0000");
        badKey.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, token);
        (await badKey.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.RoofService.Verify(s => s.Stop(It.IsAny<RoofControllerStopReason>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task TooManySignIns_Give429SignInBusy_PerCaller_BeforeAnySecretIsChecked_AndStopStillWorks()
    {
        using var logs = new RecordingLoggerProvider();
        using var host = CreateHost(logs: logs, extraSettings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:SignInAttemptsPerMinute"] = "3"
        });
        await AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, pin: TestSecrets.Pin);
        using var anonymous = host.CreateApiClient();
        for (var i = 0; i < 3; i++)
        {
            (await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = "guess-" + i, Password = TestSecrets.OtherPassword }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var limited = await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = "olive", Password = TestSecrets.Password });

        var problem = await ProblemAsync(limited);
        problem.Status.Should().Be(429);
        problem.Code.Should().Be("SignInBusy");
        problem.Detail.Should().Contain("Stop still works");
        limited.Headers.RetryAfter!.Delta.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        logs.Entries.Select(entry => entry.Message)
            .Should().NotContain(m => m.Contains("AUDIT sign-in: olive", StringComparison.Ordinal), "no secret was checked");
        for (var i = 0; i < 5; i++)
        {
            (await anonymous.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a request without a valid key is refused before it counts");
        }

        using var kiosk = host.CreateApiClient(KioskKey);
        (await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "a kiosk is counted by its key, not by the address it shares");
        for (var i = 0; i < 2; i++)
        {
            (await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "guess", Pin = TestSecrets.OtherPin }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await ProblemAsync(await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin })))
            .Code.Should().Be("SignInBusy", "the kiosk's own three attempts are used up");
        (await kiosk.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);
        (await viewer.GetAsync($"{Roof}/Status")).StatusCode.Should().Be(HttpStatusCode.OK, "only sign-in is limited");
    }

    [TestMethod]
    public async Task APasswordChange_IsCountedByThePerson_AndAKeyNeverGivesAnAnonymousSignInItsOwnBudget()
    {
        using var host = CreateHost(extraSettings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:SignInAttemptsPerMinute"] = "3"
        });
        await AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole);
        using var anonymous = host.CreateApiClient();
        var signIn = await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = "olive", Password = TestSecrets.Password });
        signIn.StatusCode.Should().Be(HttpStatusCode.OK);
        for (var i = 0; i < 2; i++)
        {
            (await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = "guess-" + i, Password = TestSecrets.OtherPassword }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var withKey = new HttpRequestMessage(HttpMethod.Post, $"{Auth}/Session")
        {
            Content = JsonContent.Create(new RoofSignInRequest { Name = "guess", Password = TestSecrets.OtherPassword })
        };
        withKey.Headers.Add(RoofControllerApiContract.ApiKeyHeaderName, TestApiKeys.Viewer);
        (await ProblemAsync(await anonymous.SendAsync(withKey)))
            .Code.Should().Be("SignInBusy", "an anonymous sign-in is counted by its address, whatever key comes with it");

        using var olive = Bearer(host, (await ApiJson.ReadAsync<RoofSessionResponse>(signIn)).Token);
        var change = new RoofPasswordChangeRequest { CurrentPassword = TestSecrets.OtherPassword, NewPassword = TestSecrets.OtherPassword + "-new" };
        for (var i = 0; i < 3; i++)
        {
            (await olive.PostAsJsonAsync($"{Auth}/Password", change))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a signed-in person is counted by name, not by the address that is used up");
        }

        (await ProblemAsync(await olive.PostAsJsonAsync($"{Auth}/Password", change))).Code.Should().Be("SignInBusy");
    }

    // ---- Signing in at a kiosk with a PIN -------------------------------------------------------------------------

    [TestMethod]
    public async Task APin_OpensAnIdleLimitedSession_OnlyAtAKioskKey()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, password: null, pin: TestSecrets.Pin);
        await AddUserAsync(host, "victor", RoofControllerApiContract.ViewerRole);
        await AddUserAsync(host, "alice", RoofControllerApiContract.AdminRole, pin: TestSecrets.OtherPin);
        var request = new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin };

        using var anonymous = host.CreateApiClient();
        (await anonymous.PostAsJsonAsync($"{Auth}/Pin", request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);
        var notKiosk = await viewer.PostAsJsonAsync($"{Auth}/Pin", request);
        (await ProblemAsync(notKiosk)).Should().Be((403, "KioskKeyRequired", "PIN sign-in needs a kiosk key in X-Api-Key; this key is not one."));
        (await viewer.GetAsync($"{Auth}/Pin/Users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var kiosk = host.CreateApiClient(KioskKey);
        var users = await ApiJson.ReadAsync<RoofPinUserResponse[]>(await kiosk.GetAsync($"{Auth}/Pin/Users"));
        users.Should().Equal(new RoofPinUserResponse("alice", RoofControllerApiContract.AdminRole), new RoofPinUserResponse("olive", RoofControllerApiContract.OperatorRole));

        var response = await kiosk.PostAsJsonAsync($"{Auth}/Pin", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await ApiJson.ReadAsync<RoofSessionResponse>(response);
        session.Kind.Should().Be(RoofCredentialKind.Pin);
        session.IdleTimeoutSeconds.Should().Be(600);
        using var olive = Bearer(host, session.Token);
        var me = await ApiJson.ReadAsync<RoofCallerResponse>(await olive.GetAsync($"{Auth}/Me"));
        me.Kind.Should().Be(RoofCredentialKind.Pin);
        me.Device.Should().Be("test-kiosk");
        (await olive.PostAsync($"{Roof}/Open", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var kioskMe = await ApiJson.ReadAsync<RoofCallerResponse>(await kiosk.GetAsync($"{Auth}/Me"));
        kioskMe.IsKiosk.Should().BeTrue();
    }

    [TestMethod]
    public async Task WrongPins_LockTheKioskOut_ButItsKeyStillStops()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, pin: TestSecrets.Pin);
        using var kiosk = host.CreateApiClient(KioskKey);
        for (var i = 0; i < 5; i++)
        {
            await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = "000000" });
        }

        var locked = await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin });

        (await ProblemAsync(locked)).Code.Should().Be("SignInLockedOut");
        (await kiosk.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SignInAsync(host, "olive")).Should().NotBeNull("the name is not locked out, only PINs at that kiosk");
    }

    // ---- The session's own endpoints ------------------------------------------------------------------------------

    [TestMethod]
    public async Task SigningOut_EndsTheSession_AndAnApiKeyHasNoSessionToEnd()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var alice = Bearer(host, (await SignInAsync(host, "alice")).Token);

        (await alice.DeleteAsync($"{Auth}/Session")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await alice.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var key = host.CreateApiClient(TestApiKeys.Operator);
        (await ProblemAsync(await key.DeleteAsync($"{Auth}/Session"))).Code.Should().Be("InvalidRequest");
    }

    [TestMethod]
    public async Task ChangingOnesPassword_KeepsThisSession_AndEndsTheOthers()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var asking = Bearer(host, (await SignInAsync(host, "alice")).Token);
        using var other = Bearer(host, (await SignInAsync(host, "alice")).Token);
        const string newPassword = "test-password-not-real-03";

        var tooShort = await asking.PostAsJsonAsync($"{Auth}/Password", new RoofPasswordChangeRequest { CurrentPassword = TestSecrets.Password, NewPassword = "short" });
        (await ProblemAsync(tooShort)).Code.Should().Be("InvalidRequest");
        var wrong = await asking.PostAsJsonAsync($"{Auth}/Password", new RoofPasswordChangeRequest { CurrentPassword = TestSecrets.OtherPassword, NewPassword = newPassword });
        (await ProblemAsync(wrong)).Should().Be((401, "SignInFailed", "The current password is not correct."));
        (await other.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK);

        var changed = await asking.PostAsJsonAsync($"{Auth}/Password", new RoofPasswordChangeRequest { CurrentPassword = TestSecrets.Password, NewPassword = newPassword });

        changed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await asking.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await other.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SignInAsync(host, "alice", newPassword)).Name.Should().Be("alice");
        using var key = host.CreateApiClient(TestApiKeys.Operator);
        (await ProblemAsync(await key.PostAsJsonAsync($"{Auth}/Password", new RoofPasswordChangeRequest { CurrentPassword = "x", NewPassword = newPassword })))
            .Code.Should().Be("InvalidRequest");
    }

    // ---- Admin: people --------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AnAdmin_ManagesPeople_AndNoSecretIsEverReturned()
    {
        using var host = CreateHost();
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var created = await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest
        {
            Name = "alice", Role = "roofoperator", Password = TestSecrets.Password, Pin = TestSecrets.Pin
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.Location!.ToString().Should().EndWith("/Identity/Users/alice");
        var text = await created.Content.ReadAsStringAsync();
        text.Should().NotContain(TestSecrets.Password).And.NotContain(TestSecrets.Pin).And.NotContainEquivalentOf("hash");
        var alice = JsonSerializer.Deserialize<RoofUserResponse>(text, ApiJson.Options)!;
        alice.Role.Should().Be(RoofControllerApiContract.OperatorRole);
        alice.HasPassword.Should().BeTrue();
        alice.HasPin.Should().BeTrue();

        (await ProblemAsync(await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = "Alice", Role = "RoofViewer", Password = TestSecrets.Password })))
            .Should().Be((409, "IdentityNameConflict", "A person named 'Alice' already exists."));
        (await ProblemAsync(await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = "bad name", Role = "RoofViewer", Password = TestSecrets.Password })))
            .Code.Should().Be("InvalidRequest");
        (await ProblemAsync(await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = "victor", Role = "RoofViewer", Pin = TestSecrets.Pin })))
            .Code.Should().Be("InvalidRequest", "a viewer cannot have a PIN");
        (await ProblemAsync(await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = "victor", Role = "RoofViewer", Password = "short" })))
            .Code.Should().Be("InvalidRequest");
        (await ProblemAsync(await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = "victor", Role = "Owner", Password = TestSecrets.Password })))
            .Code.Should().Be("InvalidRequest");

        using var session = Bearer(host, (await SignInAsync(host, "alice")).Token);
        var updated = await admin.PutAsJsonAsync($"{Identity}/Users/alice", new RoofUserUpdateRequest { Role = RoofControllerApiContract.AdminRole, RemovePin = true });
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterUpdate = await ApiJson.ReadAsync<RoofUserResponse>(updated);
        afterUpdate.Role.Should().Be(RoofControllerApiContract.AdminRole);
        afterUpdate.HasPin.Should().BeFalse();
        (await session.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a new role ends the person's sessions");

        var list = await ApiJson.ReadAsync<RoofUserResponse[]>(await admin.GetAsync($"{Identity}/Users"));
        list.Should().ContainSingle().Which.Name.Should().Be("alice");
        (await admin.DeleteAsync($"{Identity}/Users/alice")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ProblemAsync(await admin.GetAsync($"{Identity}/Users/alice"))).Should().Be((404, "IdentityNotFound", "No person has that name."));
        (await admin.DeleteAsync($"{Identity}/Users/alice")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task ASignedInAdmin_CanManagePeople()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "root", RoofControllerApiContract.AdminRole);
        using var root = Bearer(host, (await SignInAsync(host, "root")).Token);

        (await root.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = "bob", Role = "RoofViewer", Password = TestSecrets.Password }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [TestMethod]
    public async Task AnAdminsPinSession_CannotManagePeopleKeysOrSessions_ButCanRunTheRoof()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.AdminRole, pin: TestSecrets.Pin);
        using var kiosk = host.CreateApiClient(KioskKey);
        var session = await ApiJson.ReadAsync<RoofSessionResponse>(
            await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "alice", Pin = TestSecrets.Pin }));
        using var alice = Bearer(host, session.Token);

        var responses = new[]
        {
            await alice.GetAsync($"{Identity}/Users"),
            await alice.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = "mallory", Role = "RoofAdmin", Password = TestSecrets.Password }),
            await alice.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = "-not a name-", Role = "nobody" }),
            await alice.PostAsJsonAsync($"{Identity}/ApiKeys", new RoofApiKeyCreateRequest { Name = "backdoor", Role = "RoofAdmin" }),
            await alice.GetAsync($"{Identity}/Sessions")
        };

        foreach (var response in responses)
        {
            (await ProblemAsync(response)).Should().Be((403, "CredentialNotAllowed",
                "A PIN session cannot manage people, API keys or sessions. Use an admin API key, or sign in with a password."));
        }

        (await alice.PostAsync($"{Roof}/Open", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        (await ApiJson.ReadAsync<RoofUserResponse[]>(await admin.GetAsync($"{Identity}/Users"))).Should().ContainSingle("nobody was added");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(TestApiKeys.Viewer)]
    [DataRow(TestApiKeys.Operator)]
    [DataRow(KioskKey)]
    public async Task TheIdentityEndpoints_AreForAdminsOnly(string? apiKey)
    {
        using var host = CreateHost();
        using var client = host.CreateApiClient(apiKey);

        var responses = new[]
        {
            await client.GetAsync($"{Identity}/Users"),
            await client.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = "x", Role = "RoofAdmin", Password = TestSecrets.Password }),
            await client.GetAsync($"{Identity}/ApiKeys"),
            await client.PostAsJsonAsync($"{Identity}/ApiKeys", new RoofApiKeyCreateRequest { Name = "x", Role = "RoofAdmin" }),
            await client.GetAsync($"{Identity}/Sessions")
        };

        responses.Select(r => r.StatusCode).Should().AllBeEquivalentTo(apiKey is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden);
    }

    // ---- Admin: API keys and sessions -----------------------------------------------------------------------------

    [TestMethod]
    public async Task AnAdmin_CreatesRotatesAndRemovesAManagedKey_WhoseValueIsShownOnce()
    {
        using var host = CreateHost();
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var created = await admin.PostAsJsonAsync($"{Identity}/ApiKeys", new RoofApiKeyCreateRequest { Name = "ci", Role = RoofControllerApiContract.OperatorRole });

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.CacheControl!.NoStore.Should().BeTrue();
        var key = await ApiJson.ReadAsync<RoofApiKeySecretResponse>(created);
        key.Secret.Should().StartWith("hvo_k_");
        key.Key.Source.Should().Be(RoofApiKeySource.Managed);
        using (var ci = host.CreateApiClient(key.Secret))
        {
            (await ci.PostAsync($"{Roof}/Open", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var listed = await admin.GetAsync($"{Identity}/ApiKeys");
        var listText = await listed.Content.ReadAsStringAsync();
        listText.Should().NotContain(key.Secret).And.NotContain(TestApiKeys.Admin).And.NotContain(KioskKey);
        var keys = JsonSerializer.Deserialize<RoofApiKeyResponse[]>(listText, ApiJson.Options)!;
        keys.Should().Contain(k => k.Name == "test-kiosk" && k.Kiosk && k.Source == RoofApiKeySource.Configuration);
        keys.Should().Contain(k => k.Name == "ci" && k.Source == RoofApiKeySource.Managed);

        (await ProblemAsync(await admin.PostAsJsonAsync($"{Identity}/ApiKeys", new RoofApiKeyCreateRequest { Name = "test-admin", Role = "RoofViewer" })))
            .Code.Should().Be("IdentityNameConflict");
        (await ProblemAsync(await admin.PutAsJsonAsync($"{Identity}/ApiKeys/test-kiosk", new RoofApiKeyUpdateRequest { Role = "RoofViewer", Kiosk = false })))
            .Should().Match<(int Status, string? Code, string? Detail)>(p => p.Status == 409 && p.Code == "IdentityReadOnly");
        (await ProblemAsync(await admin.PostAsJsonAsync($"{Identity}/ApiKeys", new RoofApiKeyCreateRequest { Name = "kiosk-2", Role = "RoofOperator", Kiosk = true })))
            .Code.Should().Be("InvalidRequest");

        var rotated = await admin.PostAsync($"{Identity}/ApiKeys/ci/Rotate", content: null);
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        rotated.Headers.CacheControl!.NoStore.Should().BeTrue();
        var newKey = await ApiJson.ReadAsync<RoofApiKeySecretResponse>(rotated);
        newKey.Secret.Should().StartWith("hvo_k_").And.NotBe(key.Secret);
        using (var old = host.CreateApiClient(key.Secret))
        {
            (await old.GetAsync($"{Roof}/Status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var updated = await admin.PutAsJsonAsync($"{Identity}/ApiKeys/ci", new RoofApiKeyUpdateRequest { Role = "RoofViewer", Kiosk = false });
        (await ApiJson.ReadAsync<RoofApiKeyResponse>(updated)).Role.Should().Be(RoofControllerApiContract.ViewerRole);
        using (var viewerNow = host.CreateApiClient(newKey.Secret))
        {
            (await viewerNow.PostAsync($"{Roof}/Open", content: null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        (await admin.DeleteAsync($"{Identity}/ApiKeys/ci")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using (var removed = host.CreateApiClient(newKey.Secret))
        {
            (await removed.GetAsync($"{Roof}/Status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await admin.DeleteAsync($"{Identity}/ApiKeys/ci")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task AManagedKioskKey_OpensPinSessions_ThatEndWhenTheKeyIsRemoved()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", RoofControllerApiContract.OperatorRole, password: null, pin: TestSecrets.Pin);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var key = await ApiJson.ReadAsync<RoofApiKeySecretResponse>(
            await admin.PostAsJsonAsync($"{Identity}/ApiKeys", new RoofApiKeyCreateRequest { Name = "kiosk-2", Role = "RoofViewer", Kiosk = true }));
        using var kiosk = host.CreateApiClient(key.Secret);
        var session = await ApiJson.ReadAsync<RoofSessionResponse>(
            await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin }));
        using var olive = Bearer(host, session.Token);
        (await olive.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.DeleteAsync($"{Identity}/ApiKeys/kiosk-2")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await olive.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task AnAdmin_SeesOpenSessionsWithoutTokens_AndEndsOne()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        var signedIn = await SignInAsync(host, "alice");
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var listed = await admin.GetAsync($"{Identity}/Sessions");
        var text = await listed.Content.ReadAsStringAsync();
        text.Should().NotContain(signedIn.Token);
        var sessions = JsonSerializer.Deserialize<RoofSessionInfoResponse[]>(text, ApiJson.Options)!;
        sessions.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = signedIn.SessionId, Name = "alice", Kind = RoofCredentialKind.Session });
        var users = await ApiJson.ReadAsync<RoofUserResponse[]>(await admin.GetAsync($"{Identity}/Users"));
        users.Single().ActiveSessions.Should().Be(1);

        (await admin.DeleteAsync($"{Identity}/Sessions/{signedIn.SessionId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var alice = Bearer(host, signedIn.Token);
        (await alice.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await admin.DeleteAsync($"{Identity}/Sessions/{signedIn.SessionId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task TheLastAdminCredential_CannotBeRemoved()
    {
        using var host = new RoofApiTestHost(
            settings: new Dictionary<string, string?>
            {
                ["RoofControllerSecurity:ApiKeys:0:Name"] = "viewer",
                ["RoofControllerSecurity:ApiKeys:0:Role"] = RoofControllerApiContract.ViewerRole,
                ["RoofControllerSecurity:ApiKeys:0:Key"] = TestApiKeys.Viewer
            },
            includeDefaultKeys: false,
            configureServices: services => services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000));
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);
        var store = host.Services.GetRequiredService<HVO.RoofControllerV4.RPi.Security.Identity.RoofIdentityStore>();
        var hasher = host.Services.GetRequiredService<HVO.RoofControllerV4.RPi.Security.Identity.RoofSecretHasher>();
        store.AddUser("root", RoofControllerApiContract.AdminRole, await hasher.HashAsync(TestSecrets.Password, CancellationToken.None), null)
            .Succeeded.Should().BeTrue();
        using var root = Bearer(host, (await SignInAsync(host, "root")).Token);

        var removed = await root.DeleteAsync($"{Identity}/Users/root");
        var demoted = await root.PutAsJsonAsync($"{Identity}/Users/root", new RoofUserUpdateRequest { Role = "RoofOperator" });

        (await ProblemAsync(removed)).Should().Match<(int Status, string? Code, string? Detail)>(p => p.Status == 409 && p.Code == "LastAdministrator");
        (await ProblemAsync(demoted)).Code.Should().Be("LastAdministrator");
        (await root.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Audit and secrets ----------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheAuditLog_NamesWhoActed_AndNoSecretIsLogged()
    {
        using var logs = new RecordingLoggerProvider();
        using var host = CreateHost(logs: logs);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        admin.DefaultRequestHeaders.Add(RoofIdentityContract.OnBehalfOfHeaderName, "alice");
        await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = "olive", Role = "RoofOperator", Password = TestSecrets.Password, Pin = TestSecrets.Pin });
        var key = await ApiJson.ReadAsync<RoofApiKeySecretResponse>(
            await admin.PostAsJsonAsync($"{Identity}/ApiKeys", new RoofApiKeyCreateRequest { Name = "ci", Role = "RoofOperator" }));
        var session = await SignInAsync(host, "olive");
        using var olive = Bearer(host, session.Token);
        await olive.PostAsync($"{Roof}/Open", content: null);
        using var kiosk = host.CreateApiClient(KioskKey);
        var pin = await ApiJson.ReadAsync<RoofSessionResponse>(
            await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin }));
        using var pinClient = Bearer(host, pin.Token);
        await pinClient.PostAsync($"{Roof}/Stop", content: null);
        await olive.PostAsJsonAsync($"{Auth}/Password", new RoofPasswordChangeRequest { CurrentPassword = TestSecrets.Password, NewPassword = "test-password-not-real-04" });
        using var anonymous = host.CreateApiClient();
        await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = "olive", Password = TestSecrets.OtherPassword });

        var messages = logs.Entries.Select(entry => entry.Message).ToList();
        messages.Should().Contain(m => m.Contains("AUDIT test-admin for alice added olive (RoofOperator; password: True, PIN: True)", StringComparison.Ordinal));
        messages.Should().Contain(m => m.Contains("AUDIT test-admin for alice added the API key ci", StringComparison.Ordinal));
        messages.Should().Contain(m => m.Contains("Roof command open requested by olive (signed in)", StringComparison.Ordinal));
        messages.Should().Contain(m => m.Contains("Roof command stop requested by olive (PIN at test-kiosk)", StringComparison.Ordinal));
        messages.Should().Contain(m => m.Contains("SECURITY sign-in for olive", StringComparison.Ordinal));
        LogAssertions.ContainsNone(
            messages,
            TestSecrets.Password, TestSecrets.Pin, TestSecrets.OtherPassword, "test-password-not-real-04",
            session.Token, pin.Token, key.Secret, TestApiKeys.Admin, KioskKey);
    }

    // ---- The file -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task PeopleSessionsAndKeys_SurviveARestart()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        RoofSessionResponse session;
        RoofApiKeySecretResponse key;
        using (var first = CreateHost(path))
        {
            await AddUserAsync(first, "alice", RoofControllerApiContract.OperatorRole);
            session = await SignInAsync(first, "alice");
            using var admin = first.CreateApiClient(TestApiKeys.Admin);
            key = await ApiJson.ReadAsync<RoofApiKeySecretResponse>(
                await admin.PostAsJsonAsync($"{Identity}/ApiKeys", new RoofApiKeyCreateRequest { Name = "ci", Role = "RoofOperator" }));
        }

        var text = await File.ReadAllTextAsync(path);
        text.Should().NotContain(TestSecrets.Password).And.NotContain(session.Token).And.NotContain(key.Secret);

        using var second = CreateHost(path);
        using var alice = Bearer(second, session.Token);
        (await ApiJson.ReadAsync<RoofCallerResponse>(await alice.GetAsync($"{Auth}/Me"))).Name.Should().Be("alice");
        using var ci = second.CreateApiClient(key.Secret);
        (await ci.GetAsync($"{Roof}/Status")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SignInAsync(second, "alice")).Name.Should().Be("alice");
    }

    [TestMethod]
    public async Task ACorruptFile_Refuses503_ReportsUnhealthy_AndKeysStillWork()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        await File.WriteAllTextAsync(path, "{ not json");
        using var host = CreateHost(path);

        using var anonymous = host.CreateApiClient();
        var signIn = await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = "alice", Password = TestSecrets.Password });
        (await ProblemAsync(signIn)).Code.Should().Be("IdentityStoreUnavailable");
        signIn.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        signIn.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(30));
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        (await admin.GetAsync($"{Identity}/Users")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await admin.PostAsJsonAsync($"{Identity}/ApiKeys", new RoofApiKeyCreateRequest { Name = "ci", Role = "RoofOperator" }))
            .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var kiosk = host.CreateApiClient(KioskKey);
        (await kiosk.GetAsync($"{Auth}/Pin/Users")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await kiosk.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);
        var health = await ApiJson.ReadElementAsync(await viewer.GetAsync("/health"));
        var check = health.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "identity_store");
        check.GetProperty("status").GetString().Should().Be("Unhealthy");
        (await File.ReadAllTextAsync(path)).Should().Be("{ not json", "an unavailable store never overwrites the file");
    }

    [TestMethod]
    public async Task AFileWithASessionTwice_StillStarts_Refuses503_AndStopStillWorks()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        using (var first = CreateHost(path))
        {
            await AddUserAsync(first, "alice", RoofControllerApiContract.OperatorRole);
            await SignInAsync(first, "alice");
        }

        var edited = IdentityFiles.WithAnEntryTwice(await File.ReadAllTextAsync(path), "same-session");
        await File.WriteAllTextAsync(path, edited);
        using var host = CreateHost(path);

        using var anonymous = host.CreateApiClient();
        (await ProblemAsync(await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = "alice", Password = TestSecrets.Password })))
            .Code.Should().Be("IdentityStoreUnavailable");
        using var kiosk = host.CreateApiClient(KioskKey);
        (await kiosk.PostAsync($"{Roof}/Stop", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await File.ReadAllTextAsync(path)).Should().Be(edited, "an unavailable store never overwrites the file");
    }

    [TestMethod]
    public async Task TheHealthReport_ShowsAPersistentStore()
    {
        using var directory = new TemporaryDirectory();
        using var host = CreateHost(directory.File("identity.json"));
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer);

        var health = await ApiJson.ReadElementAsync(await viewer.GetAsync("/health"));

        var check = health.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "identity_store");
        check.GetProperty("status").GetString().Should().Be("Healthy");
        check.GetProperty("data").GetProperty("users").GetInt32().Should().Be(1);
        check.GetProperty("data").GetProperty("persistent").GetBoolean().Should().BeTrue();
    }

    // ---- The status hub -------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task ASession_ConnectsToTheStatusHub_AndIsClosedWhenTheSessionEnds()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        var session = await SignInAsync(host, "alice");
        await using var hub = await StatusHub.ConnectWithSessionAsync(host, session.Token);
        (await hub.NextAsync()).Status.Should().NotBeNull();

        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        (await admin.DeleteAsync($"{Identity}/Sessions/{session.SessionId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await hub.ClosedAsync();
    }
}
