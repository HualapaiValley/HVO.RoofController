using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>Production store/HTTP/signature checks. No test here claims native PAM or sudo coverage.</summary>
[TestClass]
public sealed class RoofProviderIdentityTests
{
    private static RoofIdentityOptions Enabled() => new() { LinuxAuth = new() { Enabled = true } };

    [TestMethod]
    public async Task MigrationIsOptInPreservesExactBackupAndLegacyPin()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        RoofIssuedSession pin;
        using (var original = new IdentityRig(path))
        {
            await original.AddUserAsync("same-name", RoofControllerApiContract.AdminRole, pin: TestSecrets.Pin);
            pin = original.OpenSession("same-name", RoofCredentialKind.Pin, original.Kiosk);
        }
        var baseline = File.ReadAllBytes(path);
        using (var disabled = new IdentityRig(path))
        {
            Assert.IsFalse(disabled.Store.ProviderEnabled);
            CollectionAssert.AreEqual(baseline, File.ReadAllBytes(path));
            Assert.IsFalse(File.Exists(path + ".schema1.bak"));
        }
        using (var enabled = new IdentityRig(path, Enabled()))
        {
            Assert.IsTrue(enabled.Store.IsAvailable, enabled.Store.UnavailableReason);
            Assert.IsTrue(enabled.IsLive(pin));
            Assert.IsNotNull(enabled.Store.Users.Single().PrincipalId);
            CollectionAssert.AreEqual(baseline, File.ReadAllBytes(path + ".schema1.bak"));
        }
        using var rolledBackFeature = new IdentityRig(path);
        Assert.IsTrue(rolledBackFeature.Store.IsAvailable);
        Assert.IsTrue(rolledBackFeature.IsLive(pin));
        Assert.IsFalse(rolledBackFeature.Store.ProviderEnabled);
    }

    [TestMethod]
    public async Task CheckedVirtualEpochIsBoundAtomicallyAndOwnPasswordKeepsOnlyCurrentSession()
    {
        using var directory = new TemporaryDirectory();
        using var rig = new IdentityRig(directory.File("identity.json"), Enabled());
        var user = await rig.AddUserAsync("virtual", RoofControllerApiContract.AdminRole);
        var first = rig.OpenSession(user.Name);
        var second = rig.OpenSession(user.Name);
        Assert.AreEqual(user.Stamp, first.Session.PrincipalStamp);
        var changed = rig.Store.ChangeOwnPassword(user.Name, user.Stamp, user.PasswordHash!, first.Session.Id);
        Assert.IsTrue(changed.Succeeded);
        Assert.IsTrue(rig.IsLive(first));
        Assert.IsFalse(rig.IsLive(second));
        Assert.IsFalse(rig.Store.CreateProviderSession(user.PrincipalId!, user.Stamp).Succeeded);
    }

    [TestMethod]
    public async Task OwnedKeysAreBoundToOwnerEpochScopeExpiryAndRevocation()
    {
        using var directory = new TemporaryDirectory();
        using var rig = new IdentityRig(directory.File("identity.json"), Enabled());
        var user = await rig.AddUserAsync("viewer", RoofControllerApiContract.ViewerRole);
        var secret = RoofIdentityStore.NewApiKey();
        var key = rig.Store.CreateOwnedKey(user.PrincipalId!, user.Stamp,
            new() { Name = "status", Scopes = ["read"], ExpiresUtc = rig.Time.GetUtcNow().AddMinutes(1) }, RoofIdentityStore.HashHex(secret));
        Assert.IsTrue(key.Succeeded);
        Assert.IsTrue(rig.Store.TryOwnedKey(secret, out _, out _, out _));
        Assert.IsFalse(rig.Store.CreateOwnedKey(user.PrincipalId!, user.Stamp,
            new() { Name = "broader", Scopes = ["configure"], ExpiresUtc = rig.Time.GetUtcNow().AddMinutes(1) }, RoofIdentityStore.HashHex(RoofIdentityStore.NewApiKey())).Succeeded);
        Assert.IsFalse(rig.Store.RevokeOwnedKey("another-owner", key.Value!.Id).Succeeded);
        rig.Store.UpdateUser(user.Name, RoofControllerApiContract.OperatorRole, null, null, false, false);
        Assert.IsFalse(rig.Store.TryOwnedKey(secret, out _, out _, out _));
        Assert.IsTrue(rig.Store.RevokeOwnedKey(user.PrincipalId!, key.Value.Id).Succeeded);
    }

    [TestMethod]
    public async Task LocalAssertionsAreConsumedAtomicallyAcrossConcurrencyAndRestart()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        var request = Enrollment();
        string id;
        using (var rig = new IdentityRig(path, Enabled()))
        {
            id = rig.Store.EnrollLinux(request).Value!.Id;
            var principal = rig.Store.FindLinux(request.Subject)!;
            var nonce = new StoredAssertionNonce(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), rig.Time.GetUtcNow().AddSeconds(30));
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => rig.Store.CreateProviderSession(id, principal.Stamp, "read", nonce))));
            Assert.AreEqual(1, outcomes.Count(outcome => outcome.Succeeded));
            using var reopened = new IdentityRig(path, Enabled());
            Assert.IsFalse(reopened.Store.CreateProviderSession(id, principal.Stamp, "read", nonce).Succeeded);
        }
    }

    [TestMethod]
    public async Task LinuxSameNameCannotEvictOrChangeVirtualSessions()
    {
        using var directory = new TemporaryDirectory();
        using var rig = new IdentityRig(directory.File("identity.json"), Enabled());
        var user = await rig.AddUserAsync("same-name", RoofControllerApiContract.AdminRole);
        var virtualSession = rig.OpenSession(user.Name);
        var request = Enrollment() with { Name = user.Name };
        var principal = rig.Store.EnrollLinux(request).Value!;
        var linux = rig.Store.FindLinux(request.Subject)!;
        for (var i = 0; i < 30; i++)
            Assert.IsTrue(rig.Store.CreateProviderSession(principal.Id, linux.Stamp, "read",
                new(Guid.NewGuid().ToString("N"), rig.Time.GetUtcNow().AddSeconds(30))).Succeeded);
        Assert.IsTrue(rig.IsLive(virtualSession));
        rig.Store.UpdateLinux(principal.Id, new() { Role = RoofControllerApiContract.ViewerRole, Enabled = false });
        Assert.IsTrue(rig.IsLive(virtualSession));
        Assert.IsFalse(rig.Store.EnrollLinux(request).Succeeded);
    }

    [TestMethod]
    [DataRow("purpose")]
    [DataRow("provider")]
    [DataRow("audience")]
    [DataRow("operation")]
    [DataRow("overflow")]
    [DataRow("future")]
    [DataRow("expired")]
    [DataRow("long")]
    [DataRow("nonce")]
    [DataRow("tamper")]
    public void AssertionVerifierRejectsInvalidSignedClaimsAndTampering(string variant)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualTimeProvider(IdentityRig.Start);
        var key = RandomNumberGenerator.GetBytes(32);
        var path = directory.File("assertion.key");
        File.WriteAllBytes(path, key);
        var options = new TestOptionsMonitor<RoofIdentityOptions>(new() { LinuxAuth = new() { Enabled = true, AssertionKeyPath = path } });
        using var native = new RoofNativeAuthClient(options, clock);
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var claims = new RoofNativeAssertion(1, "local-admin", "linux", Guid.NewGuid().ToString("N"), "fixture",
            Guid.NewGuid().ToString("N"), "hvo-roof-controller", "read", now, now + 30, Encode(RandomNumberGenerator.GetBytes(32)));
        Assert.IsNotNull(native.Verify(Sign(claims, key), "local-admin"));
        claims = variant switch
        {
            "purpose" => claims with { Purpose = "linux-password" },
            "provider" => claims with { Provider = "virtual" },
            "audience" => claims with { Audience = "another-controller" },
            "operation" => claims with { Operation = "login" },
            "future" => claims with { IssuedAt = now + 1 },
            "overflow" => claims with { IssuedAt = long.MinValue },
            "expired" => claims with { ExpiresAt = now },
            "long" => claims with { ExpiresAt = now + 31 },
            "nonce" => claims with { Nonce = "too-short" },
            _ => claims
        };
        var assertion = Sign(claims, variant == "tamper" ? RandomNumberGenerator.GetBytes(32) : key);
        Assert.IsNull(native.Verify(assertion, "local-admin"));
    }

    [TestMethod]
    [DataRow("GET", "/api/v4.0/RoofControl/Configuration", "read", true)]
    [DataRow("POST", "/api/v4.0/Settings/safety", "operate", false)]
    [DataRow("POST", "/api/v4.0/Settings/safety", "configure", true)]
    [DataRow("POST", "/api/v4.0/Identity/ApiKeys", "identity", false)]
    public async Task ScopeCategoriesCannotBeBypassedByHigherOwnerRole(string method, string path, string scope, bool expected)
    {
        var key = new StoredOwnedKey("key", "name", "owner", "stamp", [scope], new string('a', 64), DateTimeOffset.MaxValue, false);
        var user = RoofPrincipalFactory.CreateForOwnedKey(key, "fixture", RoofControllerApiContract.AdminRole, "test");
        var requirement = new RoofScopeRequirement("operate");
        var http = new DefaultHttpContext(); http.Request.Method = method; http.Request.Path = path;
        var context = new AuthorizationHandlerContext([requirement], user, http);
        await new RoofScopeAuthorizationHandler().HandleAsync(context);
        Assert.AreEqual(expected, context.HasSucceeded);
    }

    [TestMethod]
    public async Task ProductionHttpVirtualKeyReadStopAndOwnerIsolation()
    {
        using var directory = new TemporaryDirectory();
        await using var host = new RoofApiTestHost(settings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"] = directory.File("identity.json"),
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true"
        });
        using var admin = host.CreateApiClient(TestApiKeys.Admin);
        var add = await admin.PostAsJsonAsync("/api/v4.0/Identity/Users",
            new RoofUserCreateRequest { Name = "virtual-admin", Role = RoofControllerApiContract.AdminRole, Password = TestSecrets.Password });
        Assert.AreEqual(HttpStatusCode.Created, add.StatusCode);
        using var anonymous = host.CreateApiClient();
        var login = await anonymous.PostAsJsonAsync("/api/v4.0/Auth/ProviderSession",
            new RoofProviderSignInRequest { Provider = "virtual", Name = "virtual-admin", Password = TestSecrets.Password });
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        var session = await ApiJson.ReadAsync<RoofSessionResponse>(login);
        using var owner = host.CreateApiClient();
        owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session!.Token);
        var created = await owner.PostAsJsonAsync("/api/v4.0/Auth/Keys", new RoofOwnedKeyCreateRequest
        { Name = "reader", Scopes = ["read"], ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var issued = await ApiJson.ReadAsync<RoofOwnedKeySecretResponse>(created);
        using var keyed = host.CreateApiClient(issued!.Secret);
        Assert.AreEqual(HttpStatusCode.OK, (await keyed.GetAsync("/api/v4.0/RoofControl/Status")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await keyed.PostAsync("/api/v4.0/RoofControl/Stop", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await keyed.PostAsJsonAsync("/api/v4.0/Settings/safety", new { })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await keyed.GetAsync("/api/v4.0/Auth/Keys")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await owner.DeleteAsync("/api/v4.0/Auth/Keys/" + issued.Key.Id)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await keyed.GetAsync("/api/v4.0/RoofControl/Status")).StatusCode);
    }

    [TestMethod]
    public async Task PinOnlyChangePreservesPasswordSessionAndKeyWhileEndingPinSession()
    {
        using var directory = new TemporaryDirectory();
        using var rig = new IdentityRig(directory.File("identity.json"), Enabled());
        var user = await rig.AddUserAsync("admin", RoofControllerApiContract.AdminRole, pin: TestSecrets.Pin);
        var password = rig.OpenSession(user.Name);
        var pin = rig.OpenSession(user.Name, RoofCredentialKind.Pin, rig.Kiosk);
        var secret = RoofIdentityStore.NewApiKey();
        Assert.IsTrue(rig.Store.CreateOwnedKey(user.PrincipalId!, user.AuthenticationEpoch!,
            new() { Name = "read", Scopes = ["read"], ExpiresUtc = rig.Time.GetUtcNow().AddHours(1) }, RoofIdentityStore.HashHex(secret)).Succeeded);
        Assert.IsTrue(rig.Store.UpdateUser(user.Name, user.Role, null, "test-replacement-pin-hash", false, false).Succeeded);
        Assert.IsTrue(rig.IsLive(password));
        Assert.IsFalse(rig.IsLive(pin));
        Assert.IsTrue(rig.Store.TryOwnedKey(secret, out _, out _, out _));
        rig.Time.Now += TimeSpan.FromHours(2);
        Assert.IsFalse(rig.Store.TryOwnedKey(secret, out _, out _, out _));
    }

    [TestMethod]
    public async Task DisabledFeatureCanAddLegacyUsersToSchemaTwoWithoutBreakingNextStartup()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        using (var enabled = new IdentityRig(path, Enabled())) await enabled.AddUserAsync("first", RoofControllerApiContract.AdminRole);
        using (var disabled = new IdentityRig(path)) await disabled.AddUserAsync("second", RoofControllerApiContract.ViewerRole);
        using var reopened = new IdentityRig(path);
        Assert.IsTrue(reopened.Store.IsAvailable, reopened.Store.UnavailableReason);
        Assert.AreEqual(2, reopened.Store.Users.Count);
    }

    [TestMethod]
    public async Task StopWithIndependentKeyDoesNotWaitForHungNativeWorker()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Unix socket regression runs on Linux.");
        using var directory = new TemporaryDirectory();
        var socketPath = directory.File("worker.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1); // A worker that would accept a connection but never answer HTTP.
        await using var host = new RoofApiTestHost(settings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"] = directory.File("identity.json"),
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true",
            ["RoofControllerSecurity:Identity:LinuxAuth:SocketPath"] = socketPath
        });
        using var client = host.CreateApiClient(TestApiKeys.Viewer);
        var store = host.Services.GetRequiredService<RoofIdentityStore>();
        var enrollment = Enrollment();
        var principal = store.EnrollLinux(enrollment).Value!;
        var linux = store.FindLinux(enrollment.Subject)!;
        var issued = store.CreateProviderSession(principal.Id, linux.Stamp).Value!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var stopped = await client.PostAsync("/api/v4.0/RoofControl/Stop", null, deadline.Token);
        Assert.AreEqual(HttpStatusCode.OK, stopped.StatusCode);
        Assert.IsFalse(listener.Poll(0, SelectMode.SelectRead), "Stop must not connect to the native worker when its independent key is valid.");
    }

    private static RoofPrincipalEnrollRequest Enrollment() => new() { Provider = "linux", Subject = Guid.NewGuid().ToString("N"),
        Name = "fixture", EnrollmentStamp = Guid.NewGuid().ToString("N"), Role = RoofControllerApiContract.AdminRole };
    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Sign(RoofNativeAssertion claims, byte[] key)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(claims, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Encode(payload) + "." + Encode(HMACSHA256.HashData(key, payload));
    }
}
