using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// Managed API keys in the key store, and whether a principal's credential still stands after the key or session
/// behind it changes.
/// </summary>
[TestClass]
public sealed class RoofCredentialValidatorTests
{
    private const string Operator = RoofControllerApiContract.OperatorRole;
    private const string Viewer = RoofControllerApiContract.ViewerRole;
    private const string ManagedSecret = "test-managed-key-not-a-real-secret-0002";

    [TestMethod]
    public void AManagedKey_Validates_AsManaged_AndRotationRetiresTheOldValue()
    {
        using var rig = new IdentityRig();
        rig.Store.AddManagedKey("kiosk-2", Viewer, kiosk: true, RoofIdentityStore.HashHex(ManagedSecret)).Succeeded.Should().BeTrue();

        rig.Keys.TryValidate(ManagedSecret, out var key).Should().BeTrue();
        key!.Source.Should().Be(RoofApiKeySource.Managed);
        key.Kiosk.Should().BeTrue();
        key.Role.Should().Be(Viewer);
        rig.Keys.KeyCount.Should().Be(3);

        const string rotated = "test-managed-key-not-a-real-secret-0003";
        rig.Store.RotateManagedKey("kiosk-2", RoofIdentityStore.HashHex(rotated)).Succeeded.Should().BeTrue();

        rig.Keys.TryValidate(ManagedSecret, out _).Should().BeFalse();
        rig.Keys.TryValidate(rotated, out var after).Should().BeTrue();
        after!.KeyId.Should().NotBe(key.KeyId);
    }

    [TestMethod]
    public void AManagedKey_WhoseNameIsLaterConfigured_IsIgnored_AndReported()
    {
        using var rig = new IdentityRig();
        rig.Store.AddManagedKey("ci", Operator, kiosk: false, RoofIdentityStore.HashHex(ManagedSecret)).Succeeded.Should().BeTrue();

        rig.Security.Set(new RoofControllerSecurityOptions
        {
            ApiKeys = rig.Security.CurrentValue.ApiKeys
                .Append(KeyStoreFactory.Key("CI", Viewer, "test-configured-ci-key-not-a-real-secret"))
                .ToList()
        });

        rig.Keys.TryValidate(ManagedSecret, out _).Should().BeFalse();
        rig.Keys.TryValidate("test-configured-ci-key-not-a-real-secret", out var configured).Should().BeTrue();
        configured!.Source.Should().Be(RoofApiKeySource.Configuration);
        rig.Keys.ConfigurationProblems.Should().ContainSingle().Which.Should().Contain("'ci' has the same name as a configured key");
    }

    [TestMethod]
    public void AnUnavailableStore_ContributesNoManagedKeys()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        File.WriteAllText(path, "{");
        using var rig = new IdentityRig(path);

        rig.Keys.KeyCount.Should().Be(2);
        rig.Validator.TryAuthenticateSession("hvo_s_anything", touch: true, out _, out var failure).Should().BeFalse();
        failure.Should().Be("the identity store is unavailable");
    }

    [TestMethod]
    public async Task AKeysOnlyValidator_RefusesEverySession()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);
        var issued = rig.OpenSession("alice");
        var keysOnly = RoofCredentialValidator.ForKeysOnly(rig.Keys);

        keysOnly.TryAuthenticateSession(issued.Token, touch: true, out _, out var failure).Should().BeFalse();
        failure.Should().Be("sessions are not enabled");
        keysOnly.IsStillValid(RoofPrincipalFactory.CreateForSession(issued.Session, "RoofSession")).Should().BeFalse();
        keysOnly.IsStillValid(RoofPrincipalFactory.Create(rig.Kiosk, "ApiKey")).Should().BeTrue();
        keysOnly.IsStillValid(null).Should().BeTrue("an anonymous caller has nothing to revoke");
    }

    [TestMethod]
    public async Task ASessionPrincipal_StopsBeingValid_WhenTheSessionEndsOrTheRoleChanges()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);
        await rig.AddUserAsync("bob", Operator);
        var alice = rig.OpenSession("alice");
        var bob = rig.OpenSession("bob");
        var alicePrincipal = RoofPrincipalFactory.CreateForSession(alice.Session, "RoofSession");
        var bobPrincipal = RoofPrincipalFactory.CreateForSession(bob.Session, "RoofSession");

        rig.Validator.IsStillValid(alicePrincipal).Should().BeTrue();
        rig.Validator.TryAuthenticateSession(alice.Token, touch: true, out var session, out _).Should().BeTrue();
        session!.Id.Should().Be(alice.Session.Id);

        rig.Store.EndSession(alice.Session.Id);
        rig.Store.UpdateUser("bob", RoofControllerApiContract.AdminRole, null, null, removePassword: false, removePin: false);

        rig.Validator.IsStillValid(alicePrincipal).Should().BeFalse();
        rig.Validator.IsStillValid(bobPrincipal).Should().BeFalse();
    }

    [TestMethod]
    public async Task APinSession_StopsBeingValid_WhenItsKioskKeyIsNoLongerAKioskKey()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("olive", Operator, pin: TestSecrets.Pin);
        var issued = rig.OpenSession("olive", RoofCredentialKind.Pin, rig.Kiosk);
        var principal = RoofPrincipalFactory.CreateForSession(issued.Session, "RoofSession");
        rig.Validator.IsStillValid(principal).Should().BeTrue();

        rig.Security.Set(KeyStoreFactory.Monitor(
            KeyStoreFactory.Key("cfg-admin", RoofControllerApiContract.AdminRole, TestSecrets.AdminKey),
            KeyStoreFactory.Key("cfg-kiosk", Viewer, TestSecrets.KioskKey)).CurrentValue);

        rig.Validator.IsStillValid(principal).Should().BeFalse();
        rig.Validator.TryAuthenticateSession(issued.Token, touch: true, out _, out var failure).Should().BeFalse();
        failure.Should().Contain("no longer a kiosk key");
    }

    [TestMethod]
    public void AKeyPrincipal_StopsBeingValid_WhenTheKeyIsRemovedOrItsRoleChanges()
    {
        using var rig = new IdentityRig();
        rig.Store.AddManagedKey("ci", Operator, kiosk: false, RoofIdentityStore.HashHex(ManagedSecret)).Succeeded.Should().BeTrue();
        rig.Keys.TryValidate(ManagedSecret, out var key).Should().BeTrue();
        var principal = RoofPrincipalFactory.Create(key!, "ApiKey");
        rig.Validator.IsStillValid(principal).Should().BeTrue();

        rig.Store.UpdateManagedKey("ci", Viewer, kiosk: false).Succeeded.Should().BeTrue();
        rig.Validator.IsStillValid(principal).Should().BeFalse();

        rig.Keys.TryValidate(ManagedSecret, out var viewerKey).Should().BeTrue();
        var viewerPrincipal = RoofPrincipalFactory.Create(viewerKey!, "ApiKey");
        rig.Store.RemoveManagedKey("ci").Succeeded.Should().BeTrue();
        rig.Validator.IsStillValid(viewerPrincipal).Should().BeFalse();
    }
}
