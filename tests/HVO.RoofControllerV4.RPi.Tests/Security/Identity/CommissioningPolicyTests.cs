using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

[TestClass]
public sealed class CommissioningPolicyTests
{
    [TestMethod]
    public async Task AdminPin_RemainsValidForLiveStatus_WithoutAdminRights_AndRevokesNormally()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("admin", RoofControllerApiContract.AdminRole, pin: TestSecrets.Pin);
        var result = await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "admin", TestSecrets.Pin, "test", default);
        result.Succeeded.Should().BeTrue();
        var principal = RoofPrincipalFactory.CreateForSession(result.Issued!.Session, "test");
        principal.IsInRole(RoofControllerApiContract.AdminRole).Should().BeFalse();
        principal.IsInRole(RoofControllerApiContract.OperatorRole).Should().BeTrue();
        rig.Validator.IsStillValid(principal).Should().BeTrue("the status broadcaster must retain a valid admin PIN connection at its effective operator role");
        rig.Store.EndSession(result.Issued.Session.Id).Succeeded.Should().BeTrue();
        rig.Validator.IsStillValid(principal).Should().BeFalse("ending the PIN session must still revoke its live status connection");
    }

    [TestMethod]
    public void NewCredentials_UseSevenCharactersAndFourAsciiDigits_WithoutCompositionRules()
    {
        RoofIdentityContract.IsValidPassword("abcdefg").Should().BeTrue();
        RoofIdentityContract.IsValidPassword("1234567").Should().BeTrue();
        RoofIdentityContract.IsValidPassword("abcdef").Should().BeFalse();
        RoofIdentityContract.IsValidPin(TestSecrets.Pin).Should().BeTrue();
        foreach (var value in new[] { "123", "12345", "246810", "１２３４", "12ab", "0000" })
            RoofIdentityContract.IsValidPin(value).Should().BeFalse();
        RoofIdentityContract.IsAcceptedPin("246810").Should().BeTrue("previous credentials must remain usable until explicitly replaced");
        RoofIdentityContract.IsAcceptedPin("0000").Should().BeTrue("the server checks the explicit commissioning grant");
    }

    [TestMethod]
    public async Task TemporaryPin_RequiresExplicitEmulatorGrant_IsNotPersisted_AndCannotReturnAfterPersonalPin()
    {
        using var folder = new TemporaryDirectory();
        var file = folder.File("identity.json");
        var emulator = new HatEmulatorOptions { Enabled = true };
        using var rig = new IdentityRig(storePath: file, options: new() { AllowTemporaryKioskPin = true }, emulator: emulator);
        var hash = await rig.Hasher.HashAsync(TestSecrets.Password, default);
        rig.Store.AddUser("commissioning", "RoofAdmin", hash, null, temporaryPin: true).Succeeded.Should().BeTrue();
        var unlocked = await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "commissioning", "0000", "test", default);
        unlocked.Succeeded.Should().BeTrue();
        rig.IsLive(unlocked.Issued!).Should().BeTrue();
        var principal = RoofPrincipalFactory.CreateForSession(unlocked.Issued!.Session, "test");
        principal.IsInRole("RoofAdmin").Should().BeFalse();
        principal.IsInRole("RoofOperator").Should().BeTrue();
        File.ReadAllText(file).Should().NotContain(unlocked.Issued.Session.Id, "temporary sessions must not survive restart or rollback");
        emulator.Enabled = false;
        rig.IsLive(unlocked.Issued).Should().BeFalse("temporary access must fail on hardware even if a token already exists");
        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "commissioning", "0000", "test", default)).Succeeded.Should().BeFalse();
        emulator.Enabled = true;
        var pinHash = await rig.Hasher.HashAsync(TestSecrets.Pin, default);
        rig.Store.UpdateUser("commissioning", "RoofAdmin", null, pinHash, false, false).Succeeded.Should().BeTrue();
        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "commissioning", "0000", "test", default)).Succeeded.Should().BeFalse();
        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "commissioning", TestSecrets.Pin, "test", default)).Succeeded.Should().BeTrue();
        rig.Store.UpdateUser("commissioning", "RoofAdmin", null, null, false, true).Succeeded.Should().BeTrue();
        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "commissioning", "0000", "test", default)).Succeeded.Should().BeFalse("removing a personal PIN must never reactivate the temporary code");
    }

    [TestMethod]
    public async Task LegacyLongPin_RemainsValidUntilDeliberatelyReplaced()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("legacy", "RoofOperator", pin: "246810");
        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "legacy", "246810", "test", default)).Succeeded.Should().BeTrue();
        RoofIdentityContract.IsValidPin("246810").Should().BeFalse("new enrollment uses four digits");
    }

    [TestMethod]
    public async Task TemporaryPin_IsNeverAnImplicitFallback_AndPhysicalEnrollmentIsRefused()
    {
        using var physical = new IdentityRig(options: new() { AllowTemporaryKioskPin = true });
        var hash = await physical.Hasher.HashAsync(TestSecrets.Password, default);
        physical.Store.AddUser("commissioning", "RoofOperator", hash, null, temporaryPin: true).Succeeded.Should().BeFalse();
        using var rig = new IdentityRig(options: new() { AllowTemporaryKioskPin = true }, emulator: new() { Enabled = true });
        await rig.AddUserAsync("ordinary", "RoofOperator");
        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "ordinary", "0000", "test", default)).Succeeded.Should().BeFalse();
        rig.Store.AddUser("viewer", "RoofViewer", hash, null, temporaryPin: true).Succeeded.Should().BeFalse();
    }
}
