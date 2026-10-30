using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// A person's display name, short name, login type and last sign-in through the REST API (#209): set by an admin,
/// shown in the people list, the kiosk's PIN picker and "who am I", and recorded at every successful sign-in.
/// </summary>
[TestClass]
public sealed class IdentityProfileApiTests
{
    private const string Auth = "/api/v4.0/Auth";
    private const string Identity = "/api/v4.0/Identity";
    private const string KioskKey = "test-kiosk-key-not-a-real-secret-05";

    [TestMethod]
    public async Task AnAdmin_GivesAPersonNames_AndTheListShowsThem_WithTheLoginTypeAndLastSignIn()
    {
        using var host = CreateHost();
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var created = await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest
        {
            Name = "alice",
            Role = RoofControllerApiContract.OperatorRole,
            Password = TestSecrets.Password,
            DisplayName = "  Alice Example  ",
            ShortName = "Ali"
        });

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var person = await ApiJson.ReadAsync<RoofUserResponse>(created);
        person.DisplayName.Should().Be("Alice Example", "the names are trimmed");
        person.ShortName.Should().Be("Ali");
        person.LoginType.Should().Be(RoofLoginTypes.Local);
        person.LastLoginUtc.Should().BeNull("alice has not signed in yet");

        var before = DateTimeOffset.UtcNow;
        await SignInAsync(host, "alice");

        var listed = (await ApiJson.ReadAsync<RoofUserResponse[]>(await admin.GetAsync($"{Identity}/Users"))).Single();
        listed.DisplayName.Should().Be("Alice Example");
        listed.ShortName.Should().Be("Ali");
        listed.LoginType.Should().Be(RoofLoginTypes.Local);
        listed.LastLoginUtc.Should().NotBeNull().And.BeOnOrAfter(before.AddSeconds(-1));
        listed.UpdatedUtc.Should().Be(person.UpdatedUtc, "a sign-in is not a change to the person");
    }

    [TestMethod]
    public async Task AnAdmin_ChangesAndRemovesNames_WithoutEndingTheSessions()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", displayName: "Alice Example", shortName: "Ali");
        var session = await SignInAsync(host, "alice");
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var renamed = await ApiJson.ReadAsync<RoofUserResponse>(await admin.PutAsJsonAsync($"{Identity}/Users/alice",
            new RoofUserUpdateRequest { Role = RoofControllerApiContract.OperatorRole, DisplayName = "Alice B. Example" }));
        var removed = await ApiJson.ReadAsync<RoofUserResponse>(await admin.PutAsJsonAsync($"{Identity}/Users/alice",
            new RoofUserUpdateRequest { Role = RoofControllerApiContract.OperatorRole, ShortName = "   " }));

        renamed.DisplayName.Should().Be("Alice B. Example");
        renamed.ShortName.Should().Be("Ali", "a name not sent is left as it is");
        removed.DisplayName.Should().Be("Alice B. Example");
        removed.ShortName.Should().BeNull("an empty name removes it");
        removed.ActiveSessions.Should().Be(1);
        using var alice = Bearer(host, session.Token);
        var me = await ApiJson.ReadAsync<RoofCallerResponse>(await alice.GetAsync($"{Auth}/Me"));
        me.DisplayName.Should().Be("Alice B. Example", "the session still works and says whom it is for");
        me.ShortName.Should().BeNull();
    }

    [TestMethod]
    [DataRow("Alice\u202Eelpmaxe", null, "display name")]
    [DataRow("Alice\nExample", null, "display name")]
    [DataRow(null, "A short name too long", "short name")]
    [DataRow(null, "Ali\u0007", "short name")]
    public async Task AnInvalidName_IsRefused_BeforeAnythingChanges(string? displayName, string? shortName, string rule)
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice");
        using var admin = host.CreateApiClient(TestApiKeys.Admin);

        var added = await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest
        {
            Name = "bob",
            Role = RoofControllerApiContract.OperatorRole,
            Password = TestSecrets.Password,
            DisplayName = displayName,
            ShortName = shortName
        });
        var changed = await admin.PutAsJsonAsync($"{Identity}/Users/alice",
            new RoofUserUpdateRequest { Role = RoofControllerApiContract.OperatorRole, DisplayName = displayName, ShortName = shortName });

        foreach (var response in new[] { added, changed })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var problem = await ApiJson.ReadElementAsync(response);
            problem.GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.InvalidRequest));
            problem.GetProperty("detail").GetString().Should().Contain(rule);
        }

        var people = await ApiJson.ReadAsync<RoofUserResponse[]>(await admin.GetAsync($"{Identity}/Users"));
        people.Should().ContainSingle().Which.DisplayName.Should().BeNull();
    }

    [TestMethod]
    public async Task TheKiosksPicker_ShowsTheNamesLoginTypeAndLastSignIn_AndAPinSignInRecordsIt()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", password: null, pin: TestSecrets.Pin, displayName: "Olive Example", shortName: "Liv");
        using var kiosk = host.CreateApiClient(KioskKey);

        var before = (await ApiJson.ReadAsync<RoofPinUserResponse[]>(await kiosk.GetAsync($"{Auth}/Pin/Users"))).Single();
        (await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await ApiJson.ReadElementAsync(await kiosk.GetAsync($"{Auth}/Pin/Users"));

        before.DisplayName.Should().Be("Olive Example");
        before.ShortName.Should().Be("Liv");
        before.LoginType.Should().Be(RoofLoginTypes.Local);
        before.LastLoginUtc.Should().BeNull();
        var olive = after[0];
        olive.GetProperty("name").GetString().Should().Be("olive");
        olive.GetProperty("displayName").GetString().Should().Be("Olive Example");
        olive.GetProperty("shortName").GetString().Should().Be("Liv");
        olive.GetProperty("loginType").GetString().Should().Be("Local", "the login type is on the wire, for clients that do not derive it");
        olive.GetProperty("lastLoginUtc").GetDateTimeOffset().Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        olive.TryGetProperty("pin", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task WhoAmI_ShowsNoPersonsNames_ForAnApiKey()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "test-viewer", displayName: "Not The Key");
        using var key = host.CreateApiClient(TestApiKeys.Viewer);

        var me = await ApiJson.ReadAsync<RoofCallerResponse>(await key.GetAsync($"{Auth}/Me"));

        me.Name.Should().Be("test-viewer");
        me.Kind.Should().Be(RoofCredentialKind.ApiKey);
        me.DisplayName.Should().BeNull("an API key is not the person who shares its name");
    }

    [TestMethod]
    public async Task APerson_ChangesTheirOwnNames_WithTheirPasswordSession_AndNothingEnds()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "alice", displayName: "Alice Example", shortName: "Ali");
        var session = await SignInAsync(host, "alice");
        using var alice = Bearer(host, session.Token);

        var renamed = await alice.PostAsJsonAsync($"{Auth}/Names", new RoofNamesChangeRequest { DisplayName = "  Alice B. Example ", ShortName = "" });

        renamed.StatusCode.Should().Be(HttpStatusCode.OK, await renamed.Content.ReadAsStringAsync());
        var me = await ApiJson.ReadAsync<RoofCallerResponse>(renamed);
        me.Name.Should().Be("alice");
        me.DisplayName.Should().Be("Alice B. Example");
        me.ShortName.Should().BeNull("an empty name removes it");
        me.LoginType.Should().Be(RoofLoginTypes.Local);
        (await alice.GetAsync($"{Auth}/Me")).StatusCode.Should().Be(HttpStatusCode.OK, "changing your names ends no session");
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var listed = await ApiJson.ReadAsync<RoofUserResponse>(await admin.GetAsync($"{Identity}/Users/alice"));
        listed.DisplayName.Should().Be("Alice B. Example");
        listed.ShortName.Should().BeNull();
    }

    [TestMethod]
    public async Task OwnNames_AreNotChanged_AtAKiosk_WithAnApiKey_OrInvalid()
    {
        using var host = CreateHost();
        await AddUserAsync(host, "olive", pin: TestSecrets.Pin, displayName: "Olive Example");
        using var kiosk = host.CreateApiClient(KioskKey);
        var pinSession = await ApiJson.ReadAsync<RoofSessionResponse>(
            await kiosk.PostAsJsonAsync($"{Auth}/Pin", new RoofPinSignInRequest { Name = "olive", Pin = TestSecrets.Pin }));
        using var atTheKiosk = Bearer(host, pinSession.Token);
        using var olive = Bearer(host, (await SignInAsync(host, "olive")).Token);
        var request = new RoofNamesChangeRequest { DisplayName = "Changed" };

        var pin = await atTheKiosk.PostAsJsonAsync($"{Auth}/Names", request);
        var key = await kiosk.PostAsJsonAsync($"{Auth}/Names", request);
        var invalid = await olive.PostAsJsonAsync($"{Auth}/Names", new RoofNamesChangeRequest { ShortName = "A short name too long" });

        pin.StatusCode.Should().Be(HttpStatusCode.Forbidden, "names are changed where there is a keyboard, not at the kiosk");
        (await ApiJson.ReadElementAsync(pin)).GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.CredentialNotAllowed));
        key.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an API key is not a person");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ApiJson.ReadElementAsync(invalid)).GetProperty("detail").GetString().Should().Contain("short name");
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        (await ApiJson.ReadAsync<RoofUserResponse>(await admin.GetAsync($"{Identity}/Users/olive"))).DisplayName.Should().Be("Olive Example");
    }

    private static RoofApiTestHost CreateHost()
        => new(
            settings: new Dictionary<string, string?>
            {
                ["RoofControllerSecurity:ApiKeys:4:Name"] = "test-kiosk",
                ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.ViewerRole,
                ["RoofControllerSecurity:ApiKeys:4:Key"] = KioskKey,
                ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "true"
            },
            configureServices: services => services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000));

    private static async Task AddUserAsync(
        RoofApiTestHost host,
        string name,
        string? password = TestSecrets.Password,
        string? pin = null,
        string? displayName = null,
        string? shortName = null)
    {
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var response = await admin.PostAsJsonAsync($"{Identity}/Users", new RoofUserCreateRequest
        {
            Name = name,
            Role = RoofControllerApiContract.OperatorRole,
            Password = password,
            Pin = pin,
            DisplayName = displayName,
            ShortName = shortName
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<RoofSessionResponse> SignInAsync(RoofApiTestHost host, string name)
    {
        using var anonymous = host.CreateApiClient();
        var response = await anonymous.PostAsJsonAsync($"{Auth}/Session", new RoofSignInRequest { Name = name, Password = TestSecrets.Password });
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
