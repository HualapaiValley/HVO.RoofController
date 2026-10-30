using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Controllers;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// PIN changes through the REST API (#205): a person changes their own PIN with a PIN session at a kiosk or a password
/// session, always confirming the current one; an admin sets or removes anyone's. The caller's session never ends; the
/// person's other PIN sessions do. No PIN is ever logged.
/// </summary>
[TestClass]
public sealed class PinChangeApiTests
{
    private const string Auth = "/api/v4.0/Auth";
    private const string Identity = "/api/v4.0/Identity";
    private const string KioskKey = "test-kiosk-key-not-a-real-secret-05";
    private const string NewPin = "8642";

    [TestMethod]
    public async Task APerson_ChangesTheirOwnPin_AtTheKiosk_KeepsThisSession_AndEndsTheirOtherPinSessions()
    {
        using var logs = new RecordingLoggerProvider();
        using var host = CreateHost(logs);
        await AddUserAsync(host, "olive", pin: TestSecrets.Pin);
        using var here = Bearer(host, (await PinSignInAsync(host, "olive", TestSecrets.Pin)).Token);
        using var elsewhere = Bearer(host, (await PinSignInAsync(host, "olive", TestSecrets.Pin)).Token);
        using var password = Bearer(host, (await SignInAsync(host, "olive")).Token);

        var changed = await here.PostAsJsonAsync($"{Auth}/Pin/Change", new RoofPinChangeRequest { CurrentPin = TestSecrets.Pin, NewPin = NewPin });

        changed.StatusCode.Should().Be(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync());
        (await here.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK, "changing your PIN never ends the session you changed it with");
        (await elsewhere.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the person's other PIN sessions end");
        (await password.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK, "a PIN change leaves password sessions open");
        using var kiosk = host.CreateApiClient(KioskKey);
        (await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the old PIN no longer unlocks a kiosk");
        (await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = NewPin }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var audits = PinLines(logs);
        audits.Should().Contain(line => line.Contains("AUDIT olive changed their own PIN (PIN session at test-kiosk)", StringComparison.Ordinal)
            && line.Contains("1 other PIN session(s) ended", StringComparison.Ordinal));
        LogAssertions.ContainsNone(audits, TestSecrets.Pin, NewPin);
    }

    [TestMethod]
    public async Task APerson_ChangesTheirOwnPin_WithTheirPasswordSession_AndTheirPinSessionsEnd()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", pin: TestSecrets.Pin);
        using var atTheKiosk = Bearer(host, (await PinSignInAsync(host, "olive", TestSecrets.Pin)).Token);
        using var password = Bearer(host, (await SignInAsync(host, "olive")).Token);

        var changed = await password.PostAsJsonAsync($"{Auth}/Pin/Change", new RoofPinChangeRequest { CurrentPin = TestSecrets.Pin, NewPin = NewPin });

        changed.StatusCode.Should().Be(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync());
        (await password.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await atTheKiosk.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var olive = await ApiJson.ReadAsync<RoofUserResponse>(await admin.GetAsync($"{Identity}/Users/olive"));
        olive.HasPin.Should().BeTrue();
        olive.ActiveSessions.Should().Be(1);
        (await PinSignInAsync(host, "olive", NewPin)).Name.Should().Be("olive");
    }

    [TestMethod]
    public async Task AWrongCurrentPin_IsRefused_KeepsTheSession_AndCountsTowardsTheSamePinLockout()
    {
        using var logs = new RecordingLoggerProvider();
        using var host = CreateHost(logs);
        await AddUserAsync(host, "olive", pin: TestSecrets.Pin);
        using var password = Bearer(host, (await SignInAsync(host, "olive")).Token);
        var wrong = new RoofPinChangeRequest { CurrentPin = TestSecrets.OtherPin, NewPin = NewPin };

        var first = await password.PostAsJsonAsync($"{Auth}/Pin/Change", wrong);
        for (var i = 0; i < 3; i++)
        {
            (await password.PostAsJsonAsync($"{Auth}/Pin/Change", wrong)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var fifth = await password.PostAsJsonAsync($"{Auth}/Pin/Change", wrong);
        var right = await password.PostAsJsonAsync($"{Auth}/Pin/Change", new RoofPinChangeRequest { CurrentPin = TestSecrets.Pin, NewPin = NewPin });

        first.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiJson.ReadElementAsync(first)).GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.SignInFailed));
        (await ApiJson.ReadElementAsync(first)).GetProperty("detail").GetString().Should().Be("The current PIN is not correct.");
        fifth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the fifth wrong PIN locks the name's PIN out");
        (await ApiJson.ReadElementAsync(fifth)).GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.SignInLockedOut));
        fifth.Headers.RetryAfter.Should().NotBeNull();
        right.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the right PIN is not checked while locked out");
        (await password.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK, "a wrong current PIN is not a refused session");
        using var kiosk = host.CreateApiClient(KioskKey);
        (await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin }))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "guesses through a PIN change count against PIN sign-in too");
        LogAssertions.ContainsNone(PinLines(logs), TestSecrets.Pin, TestSecrets.OtherPin, NewPin);
    }

    [TestMethod]
    [DataRow("123", "exactly 4 digits")]
    [DataRow("12345", "exactly 4 digits")]
    [DataRow("12a4", "exactly 4 digits")]
    [DataRow("0000", "temporary")]
    [DataRow(TestSecrets.Pin, "different from your current one")]
    public async Task ABadNewPin_IsRefused_AndTheOldOneStillWorks(string newPin, string because)
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", pin: TestSecrets.Pin);
        using var password = Bearer(host, (await SignInAsync(host, "olive")).Token);

        var refused = await password.PostAsJsonAsync($"{Auth}/Pin/Change", new RoofPinChangeRequest { CurrentPin = TestSecrets.Pin, NewPin = newPin });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await ApiJson.ReadElementAsync(refused);
        problem.GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.InvalidRequest));
        problem.GetProperty("detail").GetString().Should().Contain(because);
        (await PinSignInAsync(host, "olive", TestSecrets.Pin)).Name.Should().Be("olive");
    }

    [TestMethod]
    public async Task OnlyAPersonWithAPin_ChangesIt_AndWhoAmISaysWhetherTheyHaveOne()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", pin: TestSecrets.Pin);
        await AddUserAsync(host, "paula");
        using var olive = Bearer(host, (await SignInAsync(host, "olive")).Token);
        using var paula = Bearer(host, (await SignInAsync(host, "paula")).Token);
        using var key = host.CreateApiClient(TestApiKeys.Operator);
        var request = new RoofPinChangeRequest { CurrentPin = TestSecrets.Pin, NewPin = NewPin };

        var noPin = await paula.PostAsJsonAsync($"{Auth}/Pin/Change", request);
        var apiKey = await key.PostAsJsonAsync($"{Auth}/Pin/Change", request);
        var anonymous = await host.CreateApiClient().PostAsJsonAsync($"{Auth}/Pin/Change", request);

        noPin.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ApiJson.ReadElementAsync(noPin)).GetProperty("detail").GetString().Should().Be(AuthController.NoPinToChange);
        apiKey.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an API key is not a person");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ApiJson.ReadAsync<RoofCallerResponse>(await olive.GetAsync($"{Auth}/Me"))).HasPin.Should().BeTrue();
        (await ApiJson.ReadAsync<RoofCallerResponse>(await paula.GetAsync($"{Auth}/Me"))).HasPin.Should().BeFalse();
        (await ApiJson.ReadAsync<RoofCallerResponse>(await key.GetAsync($"{Auth}/Me"))).HasPin.Should().BeNull("an API key is not a person");
    }

    [TestMethod]
    public async Task AnAdmin_SetsAndRemovesAPersonsPin_KeepingTheirRole_AndTheirPinSessionsEnd()
    {
        using var logs = new RecordingLoggerProvider();
        using var host = CreateHost(logs);
        await AddUserAsync(host, "olive", pin: TestSecrets.Pin);
        using var atTheKiosk = Bearer(host, (await PinSignInAsync(host, "olive", TestSecrets.Pin)).Token);
        using var password = Bearer(host, (await SignInAsync(host, "olive")).Token);
        await AddUserAsync(host, "ada", role: RoofControllerApiContract.AdminRole);
        using var ada = Bearer(host, (await SignInAsync(host, "ada")).Token);

        var set = await ada.PutAsJsonAsync($"{Identity}/Users/olive/Pin", new RoofPinSetRequest { Pin = NewPin });

        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        var olive = await ApiJson.ReadAsync<RoofUserResponse>(set);
        olive.HasPin.Should().BeTrue();
        olive.Role.Should().Be(RoofControllerApiContract.OperatorRole, "setting a PIN never sends the role back");
        (await atTheKiosk.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a new PIN ends the person's PIN sessions");
        (await password.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK, "and leaves their password sessions");
        (await ada.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK, "the admin's own session stays open");
        using var newPin = Bearer(host, (await PinSignInAsync(host, "olive", NewPin)).Token);

        var removed = await ada.DeleteAsync($"{Identity}/Users/olive/Pin");

        removed.StatusCode.Should().Be(HttpStatusCode.OK, await removed.Content.ReadAsStringAsync());
        (await ApiJson.ReadAsync<RoofUserResponse>(removed)).HasPin.Should().BeFalse();
        (await newPin.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var kiosk = host.CreateApiClient(KioskKey);
        (await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = NewPin }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ada.DeleteAsync($"{Identity}/Users/olive/Pin")).StatusCode.Should().Be(HttpStatusCode.OK, "removing a PIN that is gone changes nothing");
        var audits = PinLines(logs);
        audits.Should().Contain(line => line.Contains("AUDIT ada (signed in) set olive's PIN", StringComparison.Ordinal)
            && line.Contains("1 PIN session(s) ended", StringComparison.Ordinal));
        audits.Should().Contain(line => line.Contains("AUDIT ada (signed in) removed olive's PIN", StringComparison.Ordinal));
        LogAssertions.ContainsNone(audits, TestSecrets.Pin, NewPin);
    }

    [TestMethod]
    public async Task TheAdminPinRoutes_AreForAdmins_NotAtAKiosk_AndRefuseWhatAPersonCannotHave()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", pin: TestSecrets.Pin);
        await AddUserAsync(host, "vera", role: RoofControllerApiContract.ViewerRole);
        await AddUserAsync(host, "pinonly", password: null, pin: TestSecrets.Pin);
        await AddUserAsync(host, "ada", role: RoofControllerApiContract.AdminRole, pin: TestSecrets.OtherPin);
        using var operatorSession = Bearer(host, (await SignInAsync(host, "olive")).Token);
        using var adminAtTheKiosk = Bearer(host, (await PinSignInAsync(host, "ada", TestSecrets.OtherPin)).Token);
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var request = new RoofPinSetRequest { Pin = NewPin };

        (await operatorSession.PutAsJsonAsync($"{Identity}/Users/olive/Pin", request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await operatorSession.DeleteAsync($"{Identity}/Users/olive/Pin")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await adminAtTheKiosk.PutAsJsonAsync($"{Identity}/Users/olive/Pin", request)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "a PIN session is never an admin's");
        var viewer = await admin.PutAsJsonAsync($"{Identity}/Users/vera/Pin", request);
        var badPin = await admin.PutAsJsonAsync($"{Identity}/Users/olive/Pin", new RoofPinSetRequest { Pin = "0000" });
        var nobody = await admin.PutAsJsonAsync($"{Identity}/Users/nobody/Pin", request);
        var lastSecret = await admin.DeleteAsync($"{Identity}/Users/pinonly/Pin");

        viewer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ApiJson.ReadElementAsync(viewer)).GetProperty("detail").GetString().Should().Contain("Only operators and admins");
        badPin.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        nobody.StatusCode.Should().Be(HttpStatusCode.NotFound);
        lastSecret.StatusCode.Should().Be(HttpStatusCode.BadRequest, "without a PIN, a person with no password could not sign in");
        (await PinSignInAsync(host, "olive", TestSecrets.Pin)).Name.Should().Be("olive", "nothing changed");
    }

    [TestMethod]
    public async Task TheOwnerOfTheCommissionedKioskPin_ChangesItAtTheKiosk_AndKeepsTheSession()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        await SeedCommissionedOwnerAsync(path);
        using var host = CreateHost(extra: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"] = path,
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true"
        });
        using var kiosk = host.CreateApiClient(KioskKey);
        var first = await KioskPinAsync(kiosk, TestSecrets.Pin);
        var second = await KioskPinAsync(kiosk, TestSecrets.Pin);
        using var here = Bearer(host, first.Session!.Token);
        using var elsewhere = Bearer(host, second.Session!.Token);
        (await ApiJson.ReadAsync<RoofCallerResponse>(await here.GetAsync($"{Auth}/Me"))).HasPin.Should().BeTrue();

        var wrong = await here.PostAsJsonAsync($"{Auth}/Pin/Change", new RoofPinChangeRequest { CurrentPin = TestSecrets.OtherPin, NewPin = NewPin });
        var changed = await here.PostAsJsonAsync($"{Auth}/Pin/Change", new RoofPinChangeRequest { CurrentPin = TestSecrets.Pin, NewPin = NewPin });

        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        changed.StatusCode.Should().Be(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync());
        (await here.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await elsewhere.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await kiosk.PostAsJsonAsync($"{Auth}/KioskPin", new RoofKioskPinRequest(OwnerId, TestSecrets.Pin)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the old commissioned PIN no longer unlocks");
        (await KioskPinAsync(kiosk, NewPin)).Session.Should().NotBeNull();
        var store = host.Services.GetRequiredService<RoofIdentityStore>().GetKioskCredential()!;
        store.Failures.Should().Be(0, "a successful change forgets the wrong guess before it");
    }

    private const string OwnerId = "11111111111111111111111111111111";

    /// <summary>A schema 3 store whose kiosk credential, complete with <see cref="TestSecrets.Pin"/>, is a local person's.</summary>
    private static async Task SeedCommissionedOwnerAsync(string path)
    {
        const string ownerStamp = "22222222222222222222222222222222";
        using var hasher = IdentityRig.CreateHasher(1_000);
        var now = DateTimeOffset.UtcNow;
        var document = new RoofIdentityDocument
        {
            SchemaVersion = 3,
            Users =
            [
                new StoredUser("owner", RoofControllerApiContract.AdminRole, await hasher.HashAsync(TestSecrets.Password, default), null, ownerStamp, now, now)
                {
                    PrincipalId = OwnerId,
                    AuthenticationEpoch = ownerStamp
                }
            ],
            KioskCredential = new StoredKioskCredential(OwnerId, ownerStamp, "fixture-credential-stamp", "complete",
                await hasher.HashAsync(TestSecrets.Pin, default), now, now.AddHours(24))
        };
        new RoofIdentityFile(path).Save(document).Should().BeTrue();
    }

    private static async Task<RoofKioskPinResponse> KioskPinAsync(HttpClient kiosk, string pin)
    {
        var response = await kiosk.PostAsJsonAsync($"{Auth}/KioskPin", new RoofKioskPinRequest(OwnerId, pin));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ApiJson.ReadAsync<RoofKioskPinResponse>(response);
    }

    /// <summary>
    /// The log lines about PIN changes: their audits and refusals. (Sign-in lines name a random session identifier, which
    /// could hold four digits that look like a PIN.)
    /// </summary>
    private static string[] PinLines(RecordingLoggerProvider logs)
        => logs.Entries.Select(entry => entry.Message)
            .Where(message => message.Contains("PIN change", StringComparison.Ordinal) || message.Contains("their own PIN", StringComparison.Ordinal)
                || message.Contains("'s PIN", StringComparison.Ordinal))
            .ToArray();

    private static RoofApiTestHost CreateHost(RecordingLoggerProvider? logs = null, IDictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:ApiKeys:4:Name"] = "test-kiosk",
            ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.ViewerRole,
            ["RoofControllerSecurity:ApiKeys:4:Key"] = KioskKey,
            ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "true"
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        return new(
            settings: settings,
            configureServices: services =>
            {
                services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000);
                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
            });
    }

    private static async Task AddUserAsync(
        RoofApiTestHost host,
        string name,
        string role = RoofControllerApiContract.OperatorRole,
        string? password = TestSecrets.Password,
        string? pin = null)
    {
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var response = await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest { Name = name, Role = role, Password = password, Pin = pin });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<RoofSessionResponse> SignInAsync(RoofApiTestHost host, string name)
    {
        using var anonymous = host.CreateApiClient();
        var response = await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = name, Password = TestSecrets.Password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ApiJson.ReadAsync<RoofSessionResponse>(response);
    }

    private static async Task<RoofSessionResponse> PinSignInAsync(RoofApiTestHost host, string name, string pin)
    {
        using var kiosk = host.CreateApiClient(KioskKey);
        var response = await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = name, Pin = pin });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ApiJson.ReadAsync<RoofSessionResponse>(response);
    }

    private static HttpClient Bearer(RoofApiTestHost host, string token)
    {
        var client = host.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, token);
        return client;
    }
}
