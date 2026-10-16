using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>Synthetic commissioning state; no hardware, native account changes or real PINs.</summary>
[TestClass]
public sealed class KioskOwnerCommissioningTests
{
    private const string OwnerId = "11111111111111111111111111111111";
    private const string OwnerStamp = "22222222222222222222222222222222";
    private static RoofIdentityOptions Enabled() => new() { LinuxAuth = new() { Enabled = true } };

    private static void Seed(string path, string state = "pending", string? hash = null)
    {
        var now = IdentityRig.Start;
        var document = new RoofIdentityDocument
        {
            SchemaVersion = 3,
            LinuxPrincipals = [new(OwnerId, "33333333-3333-4333-8333-333333333333", "fixture-owner",
                RoofControllerApiContract.AdminRole, "44444444-4444-4444-8444-444444444444", OwnerStamp, true)],
            KioskCredential = new(OwnerId, OwnerStamp, "fixture-credential-stamp", state, hash, now, now.AddHours(24))
        };
        Assert.IsTrue(new RoofIdentityFile(path).Save(document));
    }

    private static RoofKioskPinResponse Setup(IdentityRig rig)
    {
        var attempt = rig.Store.BeginKioskAttempt(OwnerId);
        Assert.IsTrue(attempt.Succeeded, attempt.Detail);
        var issued = rig.Store.FinishKioskAttempt(attempt.Value!, rig.Kiosk.Name, rig.Kiosk.KeyId);
        Assert.IsTrue(issued.Succeeded, issued.Detail);
        Assert.IsNull(issued.Value!.Session);
        Assert.IsNotNull(issued.Value.SetupToken);
        return issued.Value;
    }

    [TestMethod]
    public void RevokedDeviceCannotFinishVerificationOrCommitPreviouslyIssuedSetup()
    {
        using var directory = new TemporaryDirectory(); var path = directory.File("identity.json"); Seed(path);
        using var rig = new IdentityRig(path, Enabled());
        var device = rig.Kiosk; var setup = Setup(rig);
        var reserved = rig.Store.BeginKioskAttempt(OwnerId).Value!;
        rig.Security.Set(new HVO.RoofControllerV4.RPi.Security.RoofControllerSecurityOptions
        {
            ApiKeys = [KeyStoreFactory.Key("cfg-admin", RoofControllerApiContract.AdminRole, TestSecrets.AdminKey)]
        });
        Assert.IsFalse(rig.Store.FinishKioskAttempt(reserved, device.Name, device.KeyId).Succeeded);
        Assert.IsFalse(rig.Store.CompleteKioskSetup(setup.SetupToken!, device.KeyId, "synthetic-unused-hash").Succeeded);
        Assert.AreEqual("pending", rig.Store.GetKioskCredential()!.State);
        Assert.AreEqual(0, rig.Store.ActiveSessions.Count);
    }

    [TestMethod]
    public async Task CompletionIsAtomicRevokesEveryGrantAndRequiresFreshPersonalPin()
    {
        using var directory = new TemporaryDirectory(); var path = directory.File("identity.json"); Seed(path);
        using var rig = new IdentityRig(path, Enabled());
        var first = Setup(rig); var second = Setup(rig);
        Assert.IsFalse(rig.Store.TryValidateToken(first.SetupToken, true, out _, out _));
        var hash = (await rig.Hasher.HashAsync(TestSecrets.Pin, default))!;
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            rig.Store.CompleteKioskSetup(first.SetupToken!, rig.Kiosk.KeyId, hash))));
        Assert.AreEqual(1, results.Count(result => result.Succeeded));
        Assert.IsFalse(rig.Store.CompleteKioskSetup(second.SetupToken!, rig.Kiosk.KeyId, hash).Succeeded);
        Assert.AreEqual(0, rig.Store.ActiveSessions.Count);
        using var restarted = new IdentityRig(path, Enabled());
        Assert.AreEqual("complete", restarted.Store.GetKioskCredential()!.State);
        Assert.AreEqual(OwnerId, restarted.Store.GetKioskCredential()!.PrincipalId);
        Assert.AreEqual(RoofSecretCheck.Failed, await restarted.Hasher.VerifyAsync(hash, "0000", default));
        Assert.AreEqual(RoofSecretCheck.Succeeded, await restarted.Hasher.VerifyAsync(hash, TestSecrets.Pin, default));
        var reserved = restarted.Store.BeginKioskAttempt(OwnerId).Value!;
        var session = restarted.Store.FinishKioskAttempt(reserved, restarted.Kiosk.Name, restarted.Kiosk.KeyId).Value!.Session!;
        Assert.AreEqual(RoofControllerApiContract.OperatorRole, session.Role);
        Assert.IsTrue(restarted.Store.TryValidateToken(session.Token, false, out var stored, out _));
        Assert.AreEqual(OwnerId, stored.PrincipalId);
        Assert.AreEqual("linux", stored.Provider);
    }

    [TestMethod]
    public async Task SameNameVirtualHumanRetiresPendingAndCannotInheritOwnerCredential()
    {
        using var directory = new TemporaryDirectory(); var path = directory.File("identity.json"); Seed(path);
        using var rig = new IdentityRig(path, Enabled()); var setup = Setup(rig);
        var other = await rig.AddUserAsync("fixture-owner", RoofControllerApiContract.AdminRole);
        Assert.AreNotEqual(OwnerId, other.PrincipalId);
        Assert.IsNull(rig.Store.GetKioskCredential());
        rig.Store.RemoveUser(other.Name);
        Assert.IsNull(rig.Store.GetKioskCredential());
        Assert.IsFalse(rig.Store.CompleteKioskSetup(setup.SetupToken!, rig.Kiosk.KeyId, "synthetic-unused-hash").Succeeded);
        using var restarted = new IdentityRig(path, Enabled());
        Assert.IsNull(restarted.Store.GetKioskCredential());
    }

    [TestMethod]
    public async Task AddingHumanAfterCompletionKeepsPersonalCredentialButDisablementRetiresIt()
    {
        using var directory = new TemporaryDirectory(); var path = directory.File("identity.json"); Seed(path);
        using var rig = new IdentityRig(path, Enabled()); var setup = Setup(rig);
        var hash = (await rig.Hasher.HashAsync(TestSecrets.Pin, default))!;
        Assert.IsTrue(rig.Store.CompleteKioskSetup(setup.SetupToken!, rig.Kiosk.KeyId, hash).Succeeded);
        await rig.AddUserAsync("fixture-owner", RoofControllerApiContract.AdminRole);
        Assert.AreEqual("complete", rig.Store.GetKioskCredential()!.State);
        rig.Store.UpdateLinux(OwnerId, new() { Role = RoofControllerApiContract.AdminRole, Enabled = false });
        Assert.IsNull(rig.Store.GetKioskCredential());
        rig.Store.UpdateLinux(OwnerId, new() { Role = RoofControllerApiContract.AdminRole, Enabled = true });
        Assert.IsNull(rig.Store.GetKioskCredential());
    }

    [TestMethod]
    public void OwnerDisablementRoleChangeAndDeletionNeverRegrant()
    {
        foreach (var action in new[] { "disable", "role", "missing" })
        {
            using var directory = new TemporaryDirectory(); var path = directory.File("identity.json"); Seed(path);
            using (var rig = new IdentityRig(path, Enabled()))
            {
                if (action != "missing")
                    rig.Store.UpdateLinux(OwnerId, new() { Role = action == "role" ? RoofControllerApiContract.ViewerRole : RoofControllerApiContract.AdminRole, Enabled = action != "disable" });
            }
            if (action == "missing")
            {
                var document = new RoofIdentityFile(path).Load();
                document.LinuxPrincipals.Clear();
                new RoofIdentityFile(path).Save(document);
            }
            using var restarted = new IdentityRig(path, Enabled());
            Assert.IsNull(restarted.Store.GetKioskCredential(), action);
        }
    }

    [TestMethod]
    public void LockoutAndPendingExpirySurviveRestart()
    {
        using var directory = new TemporaryDirectory(); var path = directory.File("identity.json"); Seed(path);
        var time = new ManualTimeProvider(IdentityRig.Start);
        using (var rig = new IdentityRig(path, Enabled(), time: time))
        {
            for (var i = 0; i < 5; i++) Assert.IsTrue(rig.Store.BeginKioskAttempt(OwnerId).Succeeded);
            Assert.AreEqual(RoofControllerErrorCode.SignInLockedOut, rig.Store.BeginKioskAttempt(OwnerId).Error);
        }
        using (var restarted = new IdentityRig(path, Enabled(), time: time))
        {
            Assert.AreEqual(RoofControllerErrorCode.SignInLockedOut, restarted.Store.BeginKioskAttempt(OwnerId).Error);
            time.Now += TimeSpan.FromMinutes(5);
            Assert.IsTrue(restarted.Store.BeginKioskAttempt(OwnerId).Succeeded);
            time.Now += TimeSpan.FromHours(24);
            Assert.IsNull(restarted.Store.GetKioskCredential());
        }
        using var expired = new IdentityRig(path, Enabled(), time: time);
        Assert.IsNull(expired.Store.GetKioskCredential());
    }

    [TestMethod]
    public void SetupGrantIsDeviceBoundShortLivedAndNotPersisted()
    {
        using var directory = new TemporaryDirectory(); var path = directory.File("identity.json"); Seed(path);
        using var rig = new IdentityRig(path, Enabled()); var setup = Setup(rig);
        Assert.IsFalse(rig.Store.CompleteKioskSetup(setup.SetupToken!, "different-device", "synthetic-unused-hash").Succeeded);
        Assert.IsFalse(File.ReadAllText(path).Contains(setup.SetupToken!, StringComparison.Ordinal));
        using (var restarted = new IdentityRig(path, Enabled()))
            Assert.IsFalse(restarted.Store.CompleteKioskSetup(setup.SetupToken!, rig.Kiosk.KeyId, "synthetic-unused-hash").Succeeded);
        rig.Time.Now += TimeSpan.FromMinutes(5);
        Assert.IsFalse(rig.Store.CompleteKioskSetup(setup.SetupToken!, rig.Kiosk.KeyId, "synthetic-unused-hash").Succeeded);
    }

    [TestMethod]
    public async Task InterruptedCompletionAndOldPendingCopyCannotReactivateDefault()
    {
        using var directory = new TemporaryDirectory(); var path = directory.File("identity.json"); Seed(path);
        var pending = File.ReadAllBytes(path);
        using (var rig = new IdentityRig(path, Enabled()))
        {
            var setup = Setup(rig); var hash = (await rig.Hasher.HashAsync(TestSecrets.Pin, default))!;
            Assert.IsTrue(rig.Store.CompleteKioskSetup(setup.SetupToken!, rig.Kiosk.KeyId, hash).Succeeded);
        }
        File.WriteAllBytes(path, pending); // Simulate a crash/old identity copy with durable tombstone retained.
        using var restarted = new IdentityRig(path, Enabled());
        Assert.IsNull(restarted.Store.GetKioskCredential());
        Assert.AreEqual("retired", new RoofIdentityFile(path).Load().KioskCredential!.State);
    }

    [TestMethod]
    public void FailedRetirementWriteDoesNotCompleteOrIssueOperationalSession()
    {
        using var directory = new TemporaryDirectory(); var path = directory.File("identity.json"); Seed(path);
        using var rig = new IdentityRig(path, Enabled()); var setup = Setup(rig);
        Directory.CreateDirectory(path + ".kiosk-retired"); // deterministic I/O fault even under a root test runner
        Assert.ThrowsExactly<RoofIdentityStoreException>(() => rig.Store.CompleteKioskSetup(setup.SetupToken!, rig.Kiosk.KeyId, "synthetic-unused-hash"));
        Assert.AreEqual("pending", new RoofIdentityFile(path).Load().KioskCredential!.State);
        Assert.AreEqual(0, rig.Store.ActiveSessions.Count);
    }

    [TestMethod]
    public void ExistingSchemaTwoAndMissingIdentityNeverProvisionCommissioning()
    {
        using var directory = new TemporaryDirectory(); var path = directory.File("identity.json");
        using (var rig = new IdentityRig(path, Enabled())) Assert.IsNull(rig.Store.GetKioskCredential());
        File.Delete(path);
        using var missing = new IdentityRig(path, Enabled());
        Assert.IsNull(missing.Store.GetKioskCredential());
    }

    [TestMethod]
    public async Task SetupTokenAloneIsRejectedByEveryOrdinaryProtectedSurface()
    {
        using var directory = new TemporaryDirectory(); var path = directory.File("identity.json"); Seed(path);
        // Use a future pending deadline with the host's real clock.
        var document = new RoofIdentityFile(path).Load();
        document.KioskCredential = document.KioskCredential! with { CreatedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) };
        new RoofIdentityFile(path).Save(document);
        await using var host = new RoofApiTestHost(settings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"] = path,
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true",
            ["RoofControllerSecurity:ApiKeys:4:Name"] = "fixture-kiosk",
            ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.ViewerRole,
            ["RoofControllerSecurity:ApiKeys:4:Key"] = TestSecrets.KioskKey,
            ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "true"
        });
        var store = host.Services.GetRequiredService<RoofIdentityStore>();
        var reserved = store.BeginKioskAttempt(OwnerId).Value!;
        var keys = host.Services.GetRequiredService<HVO.RoofControllerV4.RPi.Security.RoofApiKeyStore>();
        Assert.IsTrue(keys.TryValidate(TestSecrets.KioskKey, out var kiosk));
        var setup = store.FinishKioskAttempt(reserved, kiosk.Name, kiosk.KeyId).Value!;
        using var client = host.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.SetupToken);
        foreach (var pathName in new[] { "Auth/Me", "Identity/Users", "Identity/Principals", "RoofControl/Status", "RoofControl/Configuration" })
        {
            var response = await client.GetAsync("/api/v4.0/" + pathName);
            Assert.IsTrue(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, pathName);
        }
        foreach (var pathName in new[] { "RoofControl/Open", "RoofControl/Close", "RoofControl/Stop", "RoofControl/ClearFault", "Auth/Keys", "Auth/KioskPin/Complete" })
        {
            var response = await client.PostAsJsonAsync("/api/v4.0/" + pathName, new { });
            Assert.IsTrue(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, pathName);
        }
    }
}
