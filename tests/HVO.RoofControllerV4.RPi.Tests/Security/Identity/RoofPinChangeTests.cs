using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// A person changing their own PIN in the sign-in service and the store (#205): the current PIN is checked as a PIN
/// sign-in checks it, with the same lockouts; the change keeps the caller's session; the emulator's temporary PIN gives
/// way to a personal one; and a change that raced another is refused.
/// </summary>
[TestClass]
public sealed class RoofPinChangeTests
{
    private const string NewPin = "8642";

    [TestMethod]
    public async Task AChangeFromAKiosk_KeepsThatSession_EndsTheOtherPinSessions_AndLeavesPasswordSessions()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("olive", RoofControllerApiContract.OperatorRole, pin: TestSecrets.Pin);
        var here = rig.OpenSession("olive", RoofCredentialKind.Pin, rig.Kiosk);
        var elsewhere = rig.OpenSession("olive", RoofCredentialKind.Pin, rig.Kiosk);
        var password = rig.OpenSession("olive");
        var stamp = Stamp(rig, "olive");

        var result = await ChangeAsync(rig, "olive", here, TestSecrets.Pin, NewPin);

        result.Succeeded.Should().BeTrue(result.Detail);
        rig.IsLive(here).Should().BeTrue();
        rig.IsLive(elsewhere).Should().BeFalse();
        rig.IsLive(password).Should().BeTrue();
        Stamp(rig, "olive").Should().NotBe(stamp, "a sign-in checked against the old PIN cannot open a session");
        rig.Store.TryGetUser("olive", out var olive).Should().BeTrue();
        (await rig.Hasher.VerifyAsync(olive!.PinHash, NewPin, default)).Should().Be(RoofSecretCheck.Succeeded);
        LogAssertions.Count(rig.SignInLogger, LogLevel.Warning, "AUDIT olive changed their own PIN (PIN session at cfg-kiosk)").Should().Be(1);
        LogAssertions.ContainsNone(LogAssertions.Messages(rig.SignInLogger), TestSecrets.Pin, NewPin);
    }

    [TestMethod]
    public async Task WrongPinsThroughAKiosksSession_CountForTheKiosk_ButAPasswordSessionsDoNot()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("olive", RoofControllerApiContract.OperatorRole, pin: TestSecrets.Pin);
        await rig.AddUserAsync("paula", RoofControllerApiContract.OperatorRole, pin: TestSecrets.OtherPin);
        await rig.AddUserAsync("quinn", RoofControllerApiContract.OperatorRole, pin: TestSecrets.Pin);
        var oliveAtKiosk = rig.OpenSession("olive", RoofCredentialKind.Pin, rig.Kiosk);
        var paulaWithPassword = rig.OpenSession("paula");

        for (var i = 0; i < 4; i++)
        {
            (await ChangeAsync(rig, "paula", paulaWithPassword, "9999", NewPin)).Error.Should().Be(RoofControllerErrorCode.SignInFailed);
        }

        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "quinn", TestSecrets.Pin, "test", default)).Succeeded
            .Should().BeTrue("guesses through a password session are not the kiosk's");
        for (var i = 0; i < 4; i++)
        {
            (await ChangeAsync(rig, "olive", oliveAtKiosk, "9999", NewPin)).Error.Should().Be(RoofControllerErrorCode.SignInFailed);
        }

        var locked = await ChangeAsync(rig, "olive", oliveAtKiosk, "9999", NewPin);

        locked.Error.Should().Be(RoofControllerErrorCode.SignInLockedOut);
        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "quinn", TestSecrets.Pin, "test", default)).Error
            .Should().Be(RoofControllerErrorCode.SignInLockedOut, "five wrong PINs at a kiosk lock that kiosk, whatever they were sent through");
        rig.IsLive(oliveAtKiosk).Should().BeTrue("a wrong current PIN ends no session");
    }

    [TestMethod]
    public async Task TheTemporaryPin_GivesWayToAPersonalOne_AndTheSessionStays()
    {
        var emulator = new HatEmulatorOptions { Enabled = true };
        using var rig = new IdentityRig(options: new() { AllowTemporaryKioskPin = true }, emulator: emulator);
        var hash = await rig.Hasher.HashAsync(TestSecrets.Password, default);
        rig.Store.AddUser("commissioning", RoofControllerApiContract.AdminRole, hash, null, temporaryPin: true).Succeeded.Should().BeTrue();
        var signedIn = await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "commissioning", "0000", "test", default);
        signedIn.Succeeded.Should().BeTrue(signedIn.Detail);
        var session = signedIn.Issued!;

        var result = await ChangeAsync(rig, "commissioning", session, "0000", NewPin);

        result.Succeeded.Should().BeTrue(result.Detail);
        rig.Store.TryGetUser("commissioning", out var user).Should().BeTrue();
        user!.TemporaryPin.Should().BeFalse("a personal PIN ends the temporary commissioning grant");
        rig.Store.CanUseTemporaryPin(user).Should().BeFalse();
        rig.IsLive(session).Should().BeTrue();
        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "commissioning", "0000", "test", default)).Succeeded.Should().BeFalse();
        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "commissioning", NewPin, "test", default)).Succeeded.Should().BeTrue();
    }

    [TestMethod]
    public async Task AChangeRacingAnotherChange_IsRefused_AndTheOtherStands()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("olive", RoofControllerApiContract.OperatorRole, pin: TestSecrets.Pin);
        var session = rig.OpenSession("olive");
        var stale = Stamp(rig, "olive");
        var adminHash = (await rig.Hasher.HashAsync(TestSecrets.OtherPin, default))!;
        rig.Store.SetPin("olive", adminHash).Succeeded.Should().BeTrue();

        var refused = rig.Store.ChangeOwnPin("olive", stale, (await rig.Hasher.HashAsync(NewPin, default))!, session.Session.Id);

        refused.Error.Should().Be(RoofControllerErrorCode.SignInFailed);
        rig.Store.TryGetUser("olive", out var olive).Should().BeTrue();
        olive!.PinHash.Should().Be(adminHash);
    }

    [TestMethod]
    public async Task AnAdminsPin_SetAndRemove_KeepTheRoleAndPassword_AndEndOnlyPinSessions()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("olive", RoofControllerApiContract.OperatorRole, pin: TestSecrets.Pin);
        var pin = rig.OpenSession("olive", RoofCredentialKind.Pin, rig.Kiosk);
        var password = rig.OpenSession("olive");
        rig.Store.TryGetUser("olive", out var before).Should().BeTrue();

        var set = rig.Store.SetPin("olive", (await rig.Hasher.HashAsync(NewPin, default))!);
        var removed = rig.Store.SetPin("olive", null);
        var again = rig.Store.SetPin("olive", null);

        set.Value!.SessionsEnded.Should().Be(1);
        rig.IsLive(pin).Should().BeFalse();
        rig.IsLive(password).Should().BeTrue();
        removed.Value!.User.PinHash.Should().BeNull();
        removed.Value.User.Role.Should().Be(before!.Role);
        removed.Value.User.PasswordHash.Should().Be(before.PasswordHash);
        removed.Value.User.AuthenticationEpoch.Should().Be(before.AuthenticationEpoch, "a PIN is not the password: password sessions and keys stay");
        again.Value!.User.Should().Be(removed.Value.User, "removing a PIN that is gone changes nothing");
        rig.Store.SetPin("nobody", null).Error.Should().Be(RoofControllerErrorCode.IdentityNotFound);
    }

    private static string Stamp(IdentityRig rig, string name) => rig.Store.TryGetUser(name, out var user) ? user.Stamp : throw new AssertFailedException(name);

    private static Task<RoofSignInResult> ChangeAsync(IdentityRig rig, string name, RoofIssuedSession session, string current, string replacement)
    {
        var atKiosk = session.Session.Kind == RoofCredentialKind.Pin;
        return rig.SignIn.ChangePinAsync(
            name,
            session.Session.Id,
            atKiosk ? session.Session.DeviceKeyId : null,
            atKiosk ? session.Session.Device : null,
            current,
            replacement,
            "test",
            default);
    }
}
