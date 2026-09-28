using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// The identity store: people, sessions and managed keys, how changes end sessions, the rule that an admin credential
/// always remains, and the file (atomic, owner-only, and a corrupt file that makes the store unavailable).
/// </summary>
[TestClass]
public sealed class RoofIdentityStoreTests
{
    private const string Admin = RoofControllerApiContract.AdminRole;
    private const string Operator = RoofControllerApiContract.OperatorRole;
    private const string Viewer = RoofControllerApiContract.ViewerRole;

    // ---- People and sessions --------------------------------------------------------------------------------------

    [TestMethod]
    public void AStoreWithoutAFile_IsAvailableInMemory_AndSaysSo()
    {
        using var rig = new IdentityRig();

        rig.Store.IsAvailable.Should().BeTrue();
        rig.Store.IsPersistent.Should().BeFalse();
        rig.Store.StorePath.Should().BeNull();
        rig.StoreLogger.Contains(LogLevel.Warning, "kept in memory").Should().BeTrue();
    }

    [TestMethod]
    public async Task AddUser_RefusesANameThatExistsInAnyCase()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);

        var again = rig.Store.AddUser("ALICE", Viewer, "hash", null);

        again.Error.Should().Be(RoofControllerErrorCode.IdentityNameConflict);
        rig.Store.Users.Should().ContainSingle();
    }

    [TestMethod]
    public async Task ASession_HasAPrefixedToken_KeepsOnlyItsHash_AndValidates()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);

        var issued = rig.OpenSession("alice");

        issued.Token.Should().StartWith(RoofIdentityStore.TokenPrefix);
        issued.Session.TokenSha256.Should().Be(RoofIdentityStore.HashHex(issued.Token));
        issued.Session.ExpiresUtc.Should().Be(IdentityRig.Start + rig.Options.SessionLifetime);
        rig.Store.TryValidateToken(issued.Token, touch: true, out var session, out _).Should().BeTrue();
        session!.UserName.Should().Be("alice");
        session.Role.Should().Be(Operator);
        rig.Store.TryValidateToken(issued.Token + "x", touch: true, out _, out var failure).Should().BeFalse();
        failure.Should().Be("unknown or ended session");
        rig.Store.TryValidateToken(new string('a', RoofIdentityStore.MaximumTokenLength + 1), touch: true, out _, out failure).Should().BeFalse();
        failure.Should().Be("malformed token");
    }

    [TestMethod]
    public async Task APasswordSession_EndsAtItsLifetime_HoweverMuchItIsUsed()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);
        var issued = rig.OpenSession("alice");

        rig.Time.Now += rig.Options.SessionLifetime - TimeSpan.FromSeconds(1);
        rig.Store.TryValidateToken(issued.Token, touch: true, out _, out _).Should().BeTrue();

        rig.Time.Now += TimeSpan.FromSeconds(1);
        rig.Store.TryValidateToken(issued.Token, touch: true, out _, out var failure).Should().BeFalse();
        failure.Should().Be("session expired");
        rig.Store.ActiveSessions.Should().BeEmpty();
    }

    [TestMethod]
    public async Task APinSession_EndsAfterTheIdleTimeout_AndEachRequestStartsTheTimeoutAgain()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("olive", Operator, password: null, pin: TestSecrets.Pin);
        var issued = rig.OpenSession("olive", RoofCredentialKind.Pin, rig.Kiosk);
        var idle = rig.Options.PinSessionIdleTimeout;

        rig.Time.Now += idle - TimeSpan.FromSeconds(1);
        rig.Store.TryValidateToken(issued.Token, touch: true, out _, out _).Should().BeTrue("a request counts as activity");

        rig.Time.Now += idle - TimeSpan.FromSeconds(1);
        rig.Store.TryValidateToken(issued.Token, touch: false, out _, out _).Should().BeTrue();
        rig.Store.TryGetLiveSession(issued.Session.Id, out _).Should().BeTrue("a check that is not a request does not count as activity");

        rig.Time.Now += TimeSpan.FromSeconds(1);
        rig.Store.TryValidateToken(issued.Token, touch: true, out _, out var failure).Should().BeFalse();
        failure.Should().Be("PIN session idle for too long");
    }

    [TestMethod]
    public async Task APinSession_EndsAtItsLifetime_EvenWhileInUse()
    {
        using var rig = new IdentityRig(options: new RoofIdentityOptions
        {
            PinSessionIdleTimeout = TimeSpan.FromMinutes(10),
            PinSessionLifetime = TimeSpan.FromMinutes(25)
        });
        await rig.AddUserAsync("olive", Operator, password: null, pin: TestSecrets.Pin);
        var issued = rig.OpenSession("olive", RoofCredentialKind.Pin, rig.Kiosk);

        for (var minute = 0; minute < 24; minute += 8)
        {
            rig.Time.Now += TimeSpan.FromMinutes(8);
            rig.Store.TryValidateToken(issued.Token, touch: true, out _, out _).Should().BeTrue();
        }

        rig.Time.Now += TimeSpan.FromMinutes(1);
        rig.Store.TryValidateToken(issued.Token, touch: true, out _, out var failure).Should().BeFalse();
        failure.Should().Be("session expired");
    }

    [TestMethod]
    public async Task ASessionForASecretThatChangedSinceItWasChecked_IsNotOpened()
    {
        using var rig = new IdentityRig();
        var before = await rig.AddUserAsync("alice", Operator);
        rig.Store.UpdateUser("alice", Operator, "new-hash", null, removePassword: false, removePin: false).Succeeded.Should().BeTrue();

        var created = rig.Store.CreateSession("alice", before.Stamp, RoofCredentialKind.Session);

        created.Error.Should().Be(RoofControllerErrorCode.SignInFailed);
        rig.Store.ActiveSessions.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ANewRoleOrPassword_EndsEverySession_AndANewPin_EndsOnlyPinSessions()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator, pin: TestSecrets.Pin);
        var web = rig.OpenSession("alice");
        var kiosk = rig.OpenSession("alice", RoofCredentialKind.Pin, rig.Kiosk);

        var pinChange = rig.Store.UpdateUser("alice", Operator, null, "new-pin-hash", removePassword: false, removePin: false);

        pinChange.Value!.SessionsEnded.Should().Be(1);
        rig.IsLive(web).Should().BeTrue();
        rig.IsLive(kiosk).Should().BeFalse();

        var kiosk2 = rig.OpenSession("alice", RoofCredentialKind.Pin, rig.Kiosk);
        var sameEverything = rig.Store.UpdateUser("alice", Operator, null, null, removePassword: false, removePin: false);
        sameEverything.Value!.SessionsEnded.Should().Be(0);
        rig.IsLive(web).Should().BeTrue();

        var roleChange = rig.Store.UpdateUser("alice", Admin, null, null, removePassword: false, removePin: false);
        roleChange.Value!.SessionsEnded.Should().Be(2);
        rig.IsLive(web).Should().BeFalse();
        rig.IsLive(kiosk2).Should().BeFalse();

        var web2 = rig.OpenSession("alice");
        rig.Store.UpdateUser("alice", Admin, "other-hash", null, removePassword: false, removePin: false).Value!.SessionsEnded.Should().Be(1);
        rig.IsLive(web2).Should().BeFalse();
    }

    [TestMethod]
    public async Task UpdateUser_RefusesAPersonWithNoSecret_OrAViewerWithAPin()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator, pin: TestSecrets.Pin);

        rig.Store.UpdateUser("alice", Operator, null, null, removePassword: true, removePin: true)
            .Error.Should().Be(RoofControllerErrorCode.InvalidRequest);
        rig.Store.UpdateUser("alice", Viewer, null, null, removePassword: false, removePin: false)
            .Error.Should().Be(RoofControllerErrorCode.InvalidRequest, "a viewer cannot keep the PIN an operator had");
        rig.Store.UpdateUser("nobody", Operator, null, null, removePassword: false, removePin: false)
            .Error.Should().Be(RoofControllerErrorCode.IdentityNotFound);
        rig.Store.Users.Single().Role.Should().Be(Operator);
    }

    [TestMethod]
    public async Task ChangeOwnPassword_KeepsTheSessionThatAsked_AndEndsTheOthers()
    {
        using var rig = new IdentityRig();
        var user = await rig.AddUserAsync("alice", Operator, pin: TestSecrets.Pin);
        var asking = rig.OpenSession("alice");
        var other = rig.OpenSession("alice");
        var kiosk = rig.OpenSession("alice", RoofCredentialKind.Pin, rig.Kiosk);

        var changed = rig.Store.ChangeOwnPassword("alice", user.Stamp, "new-hash", asking.Session.Id);

        changed.Value!.SessionsEnded.Should().Be(2);
        rig.IsLive(asking).Should().BeTrue();
        rig.IsLive(other).Should().BeFalse();
        rig.IsLive(kiosk).Should().BeFalse();
        rig.Store.ChangeOwnPassword("alice", user.Stamp, "newer-hash", asking.Session.Id)
            .Error.Should().Be(RoofControllerErrorCode.SignInFailed, "the stamp read before the first change is out of date");
    }

    [TestMethod]
    public async Task RemovingAPerson_EndsTheirSessions()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);
        await rig.AddUserAsync("bob", Operator);
        var alice = rig.OpenSession("alice");
        var bob = rig.OpenSession("bob");

        var removed = rig.Store.RemoveUser("Alice");

        removed.Value!.SessionsEnded.Should().Be(1);
        rig.IsLive(alice).Should().BeFalse();
        rig.IsLive(bob).Should().BeTrue();
        rig.Store.RemoveUser("alice").Error.Should().Be(RoofControllerErrorCode.IdentityNotFound);
    }

    [TestMethod]
    public async Task EndSession_EndsOnlyThatSession()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);
        var first = rig.OpenSession("alice");
        var second = rig.OpenSession("alice");

        rig.Store.EndSession(first.Session.Id).Succeeded.Should().BeTrue();

        rig.IsLive(first).Should().BeFalse();
        rig.IsLive(second).Should().BeTrue();
        rig.Store.EndSession(first.Session.Id).Error.Should().Be(RoofControllerErrorCode.IdentityNotFound);
    }

    [TestMethod]
    public async Task APersonsOldestSessionsEnd_BeyondTheLimitPerPerson()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator);
        var sessions = new List<RoofIssuedSession>();
        for (var i = 0; i <= RoofIdentityStore.MaximumSessionsPerUser; i++)
        {
            sessions.Add(rig.OpenSession("alice"));
            rig.Time.Now += TimeSpan.FromSeconds(1);
        }

        rig.IsLive(sessions[0]).Should().BeFalse();
        sessions.Skip(1).Should().OnlyContain(session => rig.IsLive(session));
        rig.Store.ActiveSessions.Should().HaveCount(RoofIdentityStore.MaximumSessionsPerUser);
    }

    [TestMethod]
    public async Task UpgradeHash_ReplacesTheHashOnlyWhenItIsStillTheOneThatWasChecked()
    {
        using var rig = new IdentityRig();
        var user = await rig.AddUserAsync("alice", Operator, pin: TestSecrets.Pin);

        rig.Store.UpgradeHash("alice", user.PasswordHash!, "stronger", pin: false);
        rig.Store.UpgradeHash("alice", "not-the-current-pin-hash", "stronger-pin", pin: true);

        rig.Store.TryGetUser("alice", out var after).Should().BeTrue();
        after!.PasswordHash.Should().Be("stronger");
        after.PinHash.Should().Be(user.PinHash);
        after.Stamp.Should().Be(user.Stamp, "the secret is the same, so sessions stay open");
    }

    [TestMethod]
    public async Task EverySavedChange_RaisesChanged_AndARefusedOneDoesNot()
    {
        using var rig = new IdentityRig();
        var changes = 0;
        rig.Store.Changed += () => changes++;

        await rig.AddUserAsync("alice", Operator);
        rig.Store.AddUser("alice", Operator, "hash", null);

        changes.Should().Be(1);
    }

    // ---- The last admin credential --------------------------------------------------------------------------------

    [TestMethod]
    public async Task AChangeThatWouldLeaveNoAdminCredential_IsRefused()
    {
        using var rig = new IdentityRig(security: KeyStoreFactory.Monitor(
            KeyStoreFactory.Key("cfg-viewer", Viewer, "test-identity-viewer-key-not-a-real-secret")));
        await rig.AddUserAsync("root", Admin, pin: TestSecrets.Pin);
        var session = rig.OpenSession("root");

        rig.Store.RemoveUser("root").Error.Should().Be(RoofControllerErrorCode.LastAdministrator);
        rig.Store.UpdateUser("root", Operator, null, null, removePassword: false, removePin: false)
            .Error.Should().Be(RoofControllerErrorCode.LastAdministrator);
        rig.Store.UpdateUser("root", Admin, null, null, removePassword: true, removePin: false)
            .Error.Should().Be(RoofControllerErrorCode.LastAdministrator, "an admin with only a PIN can sign in at a kiosk only");
        rig.IsLive(session).Should().BeTrue("a refused change ends nothing");

        rig.Store.AddManagedKey("break-glass", Admin, kiosk: false, new string('a', 64)).Succeeded.Should().BeTrue();
        rig.Store.RemoveUser("root").Succeeded.Should().BeTrue();
        rig.Store.RemoveManagedKey("break-glass").Error.Should().Be(RoofControllerErrorCode.LastAdministrator);
    }

    [TestMethod]
    public async Task AConfiguredAdminKey_CountsAsTheAdminCredential()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("root", Admin);

        rig.Store.RemoveUser("root").Succeeded.Should().BeTrue();
    }

    // ---- Managed API keys -----------------------------------------------------------------------------------------

    [TestMethod]
    public void AKioskKey_MustBeAViewer()
    {
        using var rig = new IdentityRig();

        rig.Store.AddManagedKey("kiosk-2", Operator, kiosk: true, new string('b', 64)).Error.Should().Be(RoofControllerErrorCode.InvalidRequest);
        rig.Store.AddManagedKey("kiosk-2", Viewer, kiosk: true, new string('b', 64)).Succeeded.Should().BeTrue();
        rig.Store.UpdateManagedKey("kiosk-2", Operator, kiosk: true).Error.Should().Be(RoofControllerErrorCode.InvalidRequest);
        rig.Store.UpdateManagedKey("kiosk-2", Operator, kiosk: false).Succeeded.Should().BeTrue();
    }

    [TestMethod]
    public void AManagedKey_CannotTakeAConfiguredKeysName_AndAConfiguredKeyIsReadOnly()
    {
        using var rig = new IdentityRig();

        rig.Store.AddManagedKey("CFG-ADMIN", Viewer, kiosk: false, new string('c', 64)).Error.Should().Be(RoofControllerErrorCode.IdentityNameConflict);
        rig.Store.UpdateManagedKey("cfg-admin", Viewer, kiosk: false).Error.Should().Be(RoofControllerErrorCode.IdentityReadOnly);
        rig.Store.RotateManagedKey("cfg-kiosk", new string('d', 64)).Error.Should().Be(RoofControllerErrorCode.IdentityReadOnly);
        rig.Store.RemoveManagedKey("cfg-kiosk").Error.Should().Be(RoofControllerErrorCode.IdentityReadOnly);
        rig.Store.RemoveManagedKey("nothing").Error.Should().Be(RoofControllerErrorCode.IdentityNotFound);
    }

    [TestMethod]
    public async Task RotatingOrRemovingAKioskKey_OrTurningItsFlagOff_EndsThePinSessionsOpenedAtIt()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("olive", Operator, pin: TestSecrets.Pin);

        foreach (var change in new Func<RoofIdentityStore, bool>[]
                 {
                     store => store.RotateManagedKey("kiosk-2", new string('e', 64)).Succeeded,
                     store => store.UpdateManagedKey("kiosk-2", Viewer, kiosk: false).Succeeded,
                     store => store.RemoveManagedKey("kiosk-2").Succeeded
                 })
        {
            const string secret = "test-managed-kiosk-key-not-a-real-secret";
            rig.Store.RemoveManagedKey("kiosk-2");
            rig.Store.AddManagedKey("kiosk-2", Viewer, kiosk: true, RoofIdentityStore.HashHex(secret)).Succeeded.Should().BeTrue();
            rig.Keys.TryValidate(secret, out var kiosk).Should().BeTrue();
            var atManaged = rig.OpenSession("olive", RoofCredentialKind.Pin, kiosk);
            var atConfigured = rig.OpenSession("olive", RoofCredentialKind.Pin, rig.Kiosk);

            change(rig.Store).Should().BeTrue();

            rig.IsLive(atManaged).Should().BeFalse();
            rig.IsLive(atConfigured).Should().BeTrue();
        }
    }

    [TestMethod]
    public void NewApiKey_IsPrefixedAndRandom()
    {
        var first = RoofIdentityStore.NewApiKey();
        var second = RoofIdentityStore.NewApiKey();

        first.Should().StartWith(RoofIdentityStore.ApiKeyPrefix);
        first.Length.Should().BeGreaterThan(40);
        first.Should().NotBe(second);
        RoofIdentityFile.IsSha256Hex(RoofIdentityStore.HashHex(first)).Should().BeTrue();
    }

    // ---- The file -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AReopenedStore_HasEverything_AndItsFileIsOwnerOnly_WithNoSecretInIt()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        string token;
        string pinToken;
        const string keySecret = "test-managed-key-not-a-real-secret-0001";
        using (var rig = new IdentityRig(path))
        {
            rig.Store.IsPersistent.Should().BeTrue();
            await rig.AddUserAsync("alice", Operator, pin: TestSecrets.Pin);
            rig.Store.AddManagedKey("ci", Operator, kiosk: false, RoofIdentityStore.HashHex(keySecret)).Succeeded.Should().BeTrue();
            token = rig.OpenSession("alice").Token;
            pinToken = rig.OpenSession("alice", RoofCredentialKind.Pin, rig.Kiosk).Token;
        }

        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var text = await File.ReadAllTextAsync(path);
        text.Should().NotContain(TestSecrets.Password).And.NotContain(TestSecrets.Pin).And.NotContain(token).And.NotContain(pinToken)
            .And.NotContain(keySecret);
        Directory.GetFiles(directory.Path).Should().ContainSingle("a save leaves no temporary file behind");

        var later = IdentityRig.Start + TimeSpan.FromMinutes(30);
        using var reopened = new IdentityRig(path, time: new ManualTimeProvider(later));
        reopened.Store.Users.Should().ContainSingle(user => user.Name == "alice");
        reopened.Keys.TryValidate(keySecret, out var key).Should().BeTrue();
        key!.Source.Should().Be(RoofApiKeySource.Managed);
        reopened.Store.TryValidateToken(token, touch: true, out _, out _).Should().BeTrue();
        reopened.Store.TryValidateToken(pinToken, touch: true, out _, out _)
            .Should().BeTrue("after a restart, a PIN session starts a fresh idle period");
        (await reopened.Hasher.VerifyAsync(reopened.Store.Users[0].PasswordHash, TestSecrets.Password, CancellationToken.None))
            .Should().Be(RoofSecretCheck.Succeeded);
        reopened.StoreLogger.Contains(LogLevel.Information, "1 people, 1 managed API keys, 2 sessions").Should().BeTrue();
    }

    [TestMethod]
    public async Task AFileOthersCanRead_IsReportedAndTightenedAtTheNextSave()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes only.");
            return; // Tells the platform analyzer the Unix-only calls below are not reached on Windows.
        }

        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        using (var rig = new IdentityRig(path))
        {
            await rig.AddUserAsync("alice", Operator);
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        using var reopened = new IdentityRig(path);
        reopened.StoreLogger.Contains(LogLevel.Warning, "can be read or written by other users").Should().BeTrue();

        await reopened.AddUserAsync("bob", Operator);

        File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [TestMethod]
    public async Task ACorruptFile_MakesTheStoreUnavailable_WithoutQuotingIt()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        await File.WriteAllTextAsync(path, "{ \"users\": [ \"not-a-secret-but-quoted-nowhere\" ");

        using var rig = new IdentityRig(path);

        rig.Store.IsAvailable.Should().BeFalse();
        rig.Store.UnavailableReason.Should().Contain("not valid JSON").And.NotContain("not-a-secret-but-quoted-nowhere");
        rig.StoreLogger.Contains(LogLevel.Error, "configured API keys still work").Should().BeTrue();
        LogAssertions.ContainsNone(LogAssertions.Messages(rig.StoreLogger), "not-a-secret-but-quoted-nowhere");
        rig.Store.Users.Should().BeEmpty();
        FluentActions.Invoking(() => rig.Store.AddUser("alice", Operator, "hash", null)).Should().Throw<RoofIdentityStoreException>();
        rig.Keys.TryValidate(TestSecrets.AdminKey, out _).Should().BeTrue("configured keys keep working");
        (await File.ReadAllTextAsync(path)).Should().StartWith("{ \"users\"", "an unavailable store never overwrites the file");
    }

    [TestMethod]
    [DataRow("{ \"schemaVersion\": 2 }", "schemaVersion 2 is not supported")]
    [DataRow("null", "is empty")]
    [DataRow("{ \"schemaVersion\": 1, \"users\": [ { \"name\": \"-bad\", \"role\": \"RoofViewer\", \"passwordHash\": null, \"pinHash\": null, \"stamp\": \"s\", \"createdUtc\": \"2026-01-01T00:00:00Z\", \"updatedUtc\": \"2026-01-01T00:00:00Z\" } ] }", "users[0]")]
    [DataRow("{ \"schemaVersion\": 1, \"apiKeys\": [ { \"name\": \"k\", \"role\": \"RoofOperator\", \"kiosk\": true, \"keySha256\": \"" + "0000000000000000000000000000000000000000000000000000000000000000" + "\", \"createdUtc\": \"2026-01-01T00:00:00Z\", \"updatedUtc\": \"2026-01-01T00:00:00Z\" } ] }", "apiKeys[0]")]
    public async Task AnInvalidFile_MakesTheStoreUnavailable(string content, string reason)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        await File.WriteAllTextAsync(path, content);

        using var rig = new IdentityRig(path);

        rig.Store.IsAvailable.Should().BeFalse();
        rig.Store.UnavailableReason.Should().Contain(reason);
    }

    [TestMethod]
    public void AMissingDirectory_MakesTheStoreUnavailable()
    {
        using var directory = new TemporaryDirectory();

        using var rig = new IdentityRig(Path.Combine(directory.Path, "missing", "identity.json"));

        rig.Store.IsAvailable.Should().BeFalse();
        rig.Store.UnavailableReason.Should().Contain("does not exist");
    }

    [TestMethod]
    public async Task AFailedSave_ChangesNothing()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            Assert.Inconclusive("Needs a directory the test user cannot write to.");
            return; // Tells the platform analyzer the Unix-only calls below are not reached on Windows.
        }

        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        using var rig = new IdentityRig(path);
        await rig.AddUserAsync("alice", Operator);
        var session = rig.OpenSession("alice");
        File.SetUnixFileMode(directory.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        FluentActions.Invoking(() => rig.Store.RemoveUser("alice")).Should().Throw<RoofIdentityStoreException>()
            .WithMessage("*could not be written*");

        rig.Store.Users.Should().ContainSingle();
        rig.IsLive(session).Should().BeTrue();
        new RoofIdentityFile(path).CheckUsable().Should().ContainSingle().Which.Should().Contain("not writable");
    }

    [TestMethod]
    public async Task ALeftoverTemporaryFile_IsRemovedWhenTheStoreLoads()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        using (var rig = new IdentityRig(path))
        {
            await rig.AddUserAsync("alice", Operator);
        }

        var leftover = path + ".0123456789abcdef.tmp";
        await File.WriteAllTextAsync(leftover, "torn");

        using var reopened = new IdentityRig(path);

        reopened.Store.Users.Should().ContainSingle();
        File.Exists(leftover).Should().BeFalse();
    }

    [TestMethod]
    public void CheckUsable_PassesForAWritableDirectory_AndReportsAMissingOne()
    {
        using var directory = new TemporaryDirectory();

        new RoofIdentityFile(directory.File("identity.json")).CheckUsable().Should().BeEmpty();
        new RoofIdentityFile(Path.Combine(directory.Path, "missing", "identity.json")).CheckUsable()
            .Should().ContainSingle().Which.Should().Contain("does not exist");
        Directory.GetFiles(directory.Path).Should().BeEmpty("the probe file is removed");
    }

    // ---- Options --------------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheDefaultOptions_AreValid_AndInconsistentOnesAreReported()
    {
        new RoofIdentityOptions().Validate().Should().BeEmpty();

        var problems = new RoofIdentityOptions
        {
            SessionLifetime = TimeSpan.Zero,
            LockoutThreshold = 0,
            LockoutDuration = TimeSpan.FromHours(1),
            MaximumLockoutDuration = TimeSpan.FromMinutes(30),
            FailureMemory = TimeSpan.FromMinutes(10)
        }.Validate();

        problems.Should().HaveCount(4);
        problems.Should().Contain(problem => problem.Contains("SessionLifetime must be positive"));
        problems.Should().Contain(problem => problem.Contains("LockoutThreshold must be at least 1"));
        problems.Should().Contain(problem => problem.Contains("MaximumLockoutDuration must be at least LockoutDuration"));
        problems.Should().Contain(problem => problem.Contains("FailureMemory must be at least MaximumLockoutDuration"));
    }
}
