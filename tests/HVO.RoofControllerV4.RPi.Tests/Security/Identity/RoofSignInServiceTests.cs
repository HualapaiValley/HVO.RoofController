using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>Signing in by password and by PIN, lockout, hash upgrades, and changing one's own password.</summary>
[TestClass]
public sealed class RoofSignInServiceTests
{
    private const string Operator = RoofControllerApiContract.OperatorRole;
    private const string Viewer = RoofControllerApiContract.ViewerRole;
    private const string Remote = "192.0.2.10";

    [TestMethod]
    public async Task TheRightPassword_OpensASession_AndIsAudited()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);

        var result = await rig.SignIn.SignInWithPasswordAsync("ALICE", TestSecrets.Password, Remote, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Issued!.Session.UserName.Should().Be("alice");
        result.Issued.Session.Kind.Should().Be(RoofCredentialKind.Session);
        rig.IsLive(result.Issued).Should().BeTrue();
        rig.SignInLogger.Contains(LogLevel.Information, "AUDIT sign-in: alice (RoofOperator) from 192.0.2.10").Should().BeTrue();
        LogAssertions.ContainsNone(LogAssertions.Messages(rig.SignInLogger), TestSecrets.Password, result.Issued.Token);
    }

    [TestMethod]
    public async Task AnUnknownName_AWrongPassword_AndAPersonWithoutAPassword_GetTheSameAnswer()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);
        await rig.AddUserAsync("olive", Operator, password: null, pin: TestSecrets.Pin);

        var unknown = await rig.SignIn.SignInWithPasswordAsync("mallory", TestSecrets.Password, Remote, CancellationToken.None);
        var wrong = await rig.SignIn.SignInWithPasswordAsync("alice", TestSecrets.OtherPassword, Remote, CancellationToken.None);
        var pinOnly = await rig.SignIn.SignInWithPasswordAsync("olive", TestSecrets.Pin, Remote, CancellationToken.None);

        foreach (var result in new[] { unknown, wrong, pinOnly })
        {
            result.Error.Should().Be(RoofControllerErrorCode.SignInFailed);
            result.Detail.Should().Be(RoofSignInService.WrongCredentials);
            result.RetryAfter.Should().BeNull();
        }

        rig.Store.ActiveSessions.Should().BeEmpty();
        LogAssertions.Count(rig.SignInLogger, LogLevel.Warning, "SECURITY sign-in for").Should().Be(3);
        LogAssertions.ContainsNone(LogAssertions.Messages(rig.SignInLogger), TestSecrets.Password, TestSecrets.OtherPassword, TestSecrets.Pin);
    }

    [TestMethod]
    public async Task RepeatedFailures_LockTheNameOut_EvenForTheRightPassword_UntilTheLockoutEnds()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);
        var threshold = rig.Options.LockoutThreshold;

        RoofSignInResult? last = null;
        for (var i = 0; i < threshold; i++)
        {
            last = await rig.SignIn.SignInWithPasswordAsync("alice", TestSecrets.OtherPassword, Remote, CancellationToken.None);
        }

        last!.Error.Should().Be(RoofControllerErrorCode.SignInLockedOut);
        last.RetryAfter.Should().Be(rig.Options.LockoutDuration);
        var locked = await rig.SignIn.SignInWithPasswordAsync("alice", TestSecrets.Password, Remote, CancellationToken.None);
        locked.Error.Should().Be(RoofControllerErrorCode.SignInLockedOut);
        locked.Detail.Should().Contain("Stop still works");
        rig.SignInLogger.Contains(LogLevel.Warning, "locked out for").Should().BeTrue();

        (await rig.SignIn.SignInWithPasswordAsync("bob", TestSecrets.Password, Remote, CancellationToken.None))
            .Error.Should().Be(RoofControllerErrorCode.SignInFailed, "another name is not locked out");

        rig.Time.Now += rig.Options.LockoutDuration;
        (await rig.SignIn.SignInWithPasswordAsync("alice", TestSecrets.Password, Remote, CancellationToken.None)).Succeeded.Should().BeTrue();
    }

    [TestMethod]
    public async Task ThePin_OpensAPinSessionAtTheKiosk_AndAViewersPinNeverWorks()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("olive", Operator, password: null, pin: TestSecrets.Pin);
        await rig.AddUserAsync("victor", Viewer);

        var olive = await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "olive", TestSecrets.Pin, Remote, CancellationToken.None);

        olive.Succeeded.Should().BeTrue();
        olive.Issued!.Session.Kind.Should().Be(RoofCredentialKind.Pin);
        olive.Issued.Session.Device.Should().Be("cfg-kiosk");
        olive.Issued.Session.DeviceKeyId.Should().Be(rig.Kiosk.KeyId);
        rig.SignInLogger.Contains(LogLevel.Information, "AUDIT PIN sign-in at cfg-kiosk: olive").Should().BeTrue();
        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "victor", TestSecrets.Password, Remote, CancellationToken.None))
            .Error.Should().Be(RoofControllerErrorCode.SignInFailed);
        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "olive", TestSecrets.OtherPin, Remote, CancellationToken.None))
            .Detail.Should().Be(RoofSignInService.WrongCredentials);
    }

    [TestMethod]
    public async Task PinFailures_LockTheKioskOut_ForEveryName_ButNotPasswordSignIn()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("olive", Operator, pin: TestSecrets.Pin);

        for (var i = 0; i < rig.Options.LockoutThreshold; i++)
        {
            await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "guess-" + i, TestSecrets.OtherPin, Remote, CancellationToken.None);
        }

        (await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "olive", TestSecrets.Pin, Remote, CancellationToken.None))
            .Error.Should().Be(RoofControllerErrorCode.SignInLockedOut);
        (await rig.SignIn.SignInWithPasswordAsync("olive", TestSecrets.Password, Remote, CancellationToken.None))
            .Succeeded.Should().BeTrue();
    }

    [TestMethod]
    public async Task AnOldHash_IsUpgradedAtSignIn_WithoutEndingSessions()
    {
        using var rig = new IdentityRig();
        using var weak = IdentityRig.CreateHasher(500);
        var weakHash = await weak.HashAsync(TestSecrets.Password, CancellationToken.None);
        rig.Store.AddUser("alice", Operator, weakHash, null).Succeeded.Should().BeTrue();
        var earlier = rig.OpenSession("alice");

        (await rig.SignIn.SignInWithPasswordAsync("alice", TestSecrets.Password, Remote, CancellationToken.None)).Succeeded.Should().BeTrue();

        rig.Store.TryGetUser("alice", out var user).Should().BeTrue();
        user!.PasswordHash.Should().NotBe(weakHash);
        (await rig.Hasher.VerifyAsync(user.PasswordHash, TestSecrets.Password, CancellationToken.None)).Should().Be(RoofSecretCheck.Succeeded);
        rig.IsLive(earlier).Should().BeTrue();
    }

    [TestMethod]
    public async Task AnUnavailableStore_RefusesSignIn_WithARetry()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        await File.WriteAllTextAsync(path, "not json");
        using var rig = new IdentityRig(path);

        var result = await rig.SignIn.SignInWithPasswordAsync("alice", TestSecrets.Password, Remote, CancellationToken.None);
        var change = await rig.SignIn.ChangePasswordAsync("alice", "s", TestSecrets.Password, TestSecrets.OtherPassword, Remote, CancellationToken.None);

        result.Error.Should().Be(RoofControllerErrorCode.IdentityStoreUnavailable);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
        change.Error.Should().Be(RoofControllerErrorCode.IdentityStoreUnavailable);
    }

    [TestMethod]
    public async Task ABusyHasher_RefusesSignIn_WithAShortRetry()
    {
        var options = new RoofIdentityOptions();
        using var rig = new IdentityRig(options: options);
        await rig.AddUserAsync("alice", Operator);
        using var busy = IdentityRig.CreateHasher(3_000_000, slots: 1, slotWait: TimeSpan.FromMilliseconds(50));
        var service = new RoofSignInService(rig.Store, busy, rig.Lockout, rig.SignInLogger);
        var slow = Task.Run(() => busy.HashAsync(TestSecrets.Password, CancellationToken.None));
        SpinWait.SpinUntil(() => busy.FreeSlots == 0, TimeSpan.FromSeconds(10)).Should().BeTrue();

        var result = await service.SignInWithPasswordAsync("alice", TestSecrets.Password, Remote, CancellationToken.None);

        var slowFinished = slow.IsCompleted;
        await slow;
        if (slowFinished)
        {
            Assert.Inconclusive("The slow hash finished before sign-in was tried; this machine is too fast for the test.");
        }

        result.Error.Should().Be(RoofControllerErrorCode.SignInBusy);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(2));
        rig.Lockout.Count.Should().Be(0, "a busy refusal is not a failed attempt");
    }

    [TestMethod]
    public async Task ChangePassword_NeedsTheCurrentPassword_KeepsTheAskingSession_AndEndsTheOthers()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);
        var asking = rig.OpenSession("alice");
        var other = rig.OpenSession("alice");

        var wrong = await rig.SignIn.ChangePasswordAsync("alice", asking.Session.Id, TestSecrets.OtherPassword, "new-password-not-real", Remote, CancellationToken.None);
        wrong.Error.Should().Be(RoofControllerErrorCode.SignInFailed);
        wrong.Detail.Should().Be("The current password is not correct.");
        rig.IsLive(other).Should().BeTrue();

        var changed = await rig.SignIn.ChangePasswordAsync("alice", asking.Session.Id, TestSecrets.Password, "new-password-not-real", Remote, CancellationToken.None);

        changed.Succeeded.Should().BeTrue();
        rig.IsLive(asking).Should().BeTrue();
        rig.IsLive(other).Should().BeFalse();
        rig.SignInLogger.Contains(LogLevel.Warning, "AUDIT alice changed their own password from 192.0.2.10; 1 other session(s) ended.").Should().BeTrue();
        (await rig.SignIn.SignInWithPasswordAsync("alice", TestSecrets.Password, Remote, CancellationToken.None)).Succeeded.Should().BeFalse();
        (await rig.SignIn.SignInWithPasswordAsync("alice", "new-password-not-real", Remote, CancellationToken.None)).Succeeded.Should().BeTrue();
        LogAssertions.ContainsNone(LogAssertions.Messages(rig.SignInLogger), TestSecrets.Password, TestSecrets.OtherPassword, "new-password-not-real");
    }

    [TestMethod]
    public async Task ChangePasswordFailures_CountTowardsTheNamesLockout()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);
        var asking = rig.OpenSession("alice");

        RoofSignInResult? last = null;
        for (var i = 0; i < rig.Options.LockoutThreshold; i++)
        {
            last = await rig.SignIn.ChangePasswordAsync("alice", asking.Session.Id, TestSecrets.OtherPassword, "x-password-not-real", Remote, CancellationToken.None);
        }

        last!.Error.Should().Be(RoofControllerErrorCode.SignInLockedOut);
        (await rig.SignIn.SignInWithPasswordAsync("alice", TestSecrets.Password, Remote, CancellationToken.None))
            .Error.Should().Be(RoofControllerErrorCode.SignInLockedOut);
    }
}
