using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// A person's display name, short name and last sign-in (#209): kept in the identity store without a new schema, so a
/// store written before them loads and a rollback reads one written after; recorded or changed without ending a session.
/// </summary>
[TestClass]
public sealed class RoofIdentityProfileTests
{
    private const string Operator = RoofControllerApiContract.OperatorRole;

    [TestMethod]
    public async Task APasswordSignIn_RecordsTheLastLogin_WithoutChangingTheStampOrEndingASession()
    {
        using var rig = new IdentityRig();
        var added = await rig.AddUserAsync("alice", Operator);
        var earlier = rig.OpenSession("alice");
        rig.Store.TryGetUser("alice", out var before).Should().BeTrue();
        rig.Time.Now += TimeSpan.FromMinutes(5);

        var result = await rig.SignIn.SignInWithPasswordAsync("alice", TestSecrets.Password, "test", CancellationToken.None);

        result.Succeeded.Should().BeTrue(result.Detail);
        rig.Store.TryGetUser("alice", out var after).Should().BeTrue();
        after!.LastLoginUtc.Should().Be(rig.Time.Now);
        after.Stamp.Should().Be(before!.Stamp, "a sign-in must not stop a sign-in in flight from opening its session");
        after.UpdatedUtc.Should().Be(added.UpdatedUtc, "a sign-in is not a change to the person");
        rig.IsLive(earlier).Should().BeTrue();
    }

    [TestMethod]
    public async Task APinSignIn_RecordsTheLastLogin_AndAFailedOneDoesNot()
    {
        using var rig = new IdentityRig();
        await rig.AddUserAsync("alice", Operator, pin: TestSecrets.Pin);

        var wrong = await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "alice", TestSecrets.OtherPin, "test", CancellationToken.None);
        rig.Store.TryGetUser("alice", out var refused).Should().BeTrue();
        rig.Time.Now += TimeSpan.FromMinutes(1);
        var right = await rig.SignIn.SignInWithPinAsync(rig.Kiosk, "alice", TestSecrets.Pin, "test", CancellationToken.None);

        wrong.Succeeded.Should().BeFalse();
        refused!.LastLoginUtc.Should().BeNull("a refused PIN is not a sign-in");
        right.Succeeded.Should().BeTrue(right.Detail);
        rig.Store.TryGetUser("alice", out var after).Should().BeTrue();
        after!.LastLoginUtc.Should().Be(rig.Time.Now);
    }

    [TestMethod]
    public async Task Names_AreKept_LeftAlone_ReplacedAndRemoved_WithoutEndingASession()
    {
        using var rig = new IdentityRig();
        var hash = await rig.Hasher.HashAsync(TestSecrets.Password, CancellationToken.None);
        rig.Store.AddUser("alice", Operator, hash, null, displayName: "Alice Example", shortName: "Ali").Succeeded.Should().BeTrue();
        var session = rig.OpenSession("alice");
        rig.Store.TryGetUser("alice", out var added).Should().BeTrue();

        var untouched = rig.Store.UpdateUser("alice", Operator, null, null, false, false).Value!.User;
        untouched.DisplayName.Should().Be("Alice Example");
        untouched.ShortName.Should().Be("Ali");

        var renamed = rig.Store.UpdateUser("alice", Operator, null, null, false, false, displayName: "Alice B. Example", shortName: string.Empty);
        renamed.Value!.User.DisplayName.Should().Be("Alice B. Example");
        renamed.Value.User.ShortName.Should().BeNull("an empty short name removes it");
        renamed.Value.SessionsEnded.Should().Be(0);
        renamed.Value.User.Stamp.Should().Be(added!.Stamp);
        rig.IsLive(session).Should().BeTrue("a new name ends no session");
    }

    [TestMethod]
    public async Task AStoreWrittenBeforeProfiles_Loads_AndKeepsItsSchema_WhenTheyAreRecorded()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        using (var rig = new IdentityRig(path))
        {
            await rig.AddUserAsync("alice", Operator);
            await rig.AddUserAsync("bob", Operator);
        }

        // As a controller before #209 wrote it: no profile field at all.
        var written = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        foreach (var user in written["users"]!.AsArray())
        {
            user!.AsObject().Select(property => property.Key).Should().NotContain(["displayName", "shortName", "lastLoginUtc"]);
        }

        using (var upgraded = new IdentityRig(path))
        {
            upgraded.Store.Users.Should().OnlyContain(user => user.DisplayName == null && user.ShortName == null && user.LastLoginUtc == null);
            upgraded.Store.UpdateUser("alice", Operator, null, null, false, false, displayName: "Alice Example", shortName: "Ali")
                .Succeeded.Should().BeTrue();
            (await upgraded.SignIn.SignInWithPasswordAsync("alice", TestSecrets.Password, "test", CancellationToken.None))
                .Succeeded.Should().BeTrue();
        }

        var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        saved["schemaVersion"]!.GetValue<int>().Should().Be(RoofIdentityDocument.CurrentSchemaVersion,
            "optional fields need no new schema, so a rollback still reads the store");
        var users = saved["users"]!.AsArray().Select(user => user!.AsObject()).ToDictionary(user => user["name"]!.GetValue<string>());
        users["alice"]["displayName"]!.GetValue<string>().Should().Be("Alice Example");
        users["alice"]["shortName"]!.GetValue<string>().Should().Be("Ali");
        users["alice"].ContainsKey("lastLoginUtc").Should().BeTrue();
        users["bob"].Select(property => property.Key).Should().NotContain(["displayName", "shortName", "lastLoginUtc"],
            "a field that is not set is left out");

        using var reopened = new IdentityRig(path);
        reopened.Store.TryGetUser("alice", out var alice).Should().BeTrue();
        alice!.DisplayName.Should().Be("Alice Example");
        alice.ShortName.Should().Be("Ali");
        alice.LastLoginUtc.Should().Be(IdentityRig.Start);
    }

    [TestMethod]
    public void ALinuxPrincipalsSignIn_IsRecordedOnThePrincipal()
    {
        using var directory = new TemporaryDirectory();
        using var rig = new IdentityRig(directory.File("identity.json"), new RoofIdentityOptions { LinuxAuth = new() { Enabled = true } });
        var enrollment = new RoofPrincipalEnrollRequest
        {
            Provider = "linux",
            Subject = Guid.NewGuid().ToString("N"),
            Name = "fixture",
            EnrollmentStamp = Guid.NewGuid().ToString("N"),
            Role = Operator
        };
        var principal = rig.Store.EnrollLinux(enrollment).Value!;
        var linux = rig.Store.FindLinux(enrollment.Subject)!;
        principal.LastLoginUtc.Should().BeNull();
        rig.Time.Now += TimeSpan.FromMinutes(3);

        rig.Store.CreateProviderSession(principal.Id, linux.Stamp, "read",
            new StoredAssertionNonce(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), rig.Time.Now.AddSeconds(30)))
            .Succeeded.Should().BeTrue();

        rig.Store.FindLinux(enrollment.Subject)!.LastLoginUtc.Should().Be(rig.Time.Now);
        rig.Store.FindLinux(enrollment.Subject)!.Stamp.Should().Be(linux.Stamp);
        rig.Store.ProviderPrincipals.Single(candidate => candidate.Id == principal.Id).LastLoginUtc.Should().Be(rig.Time.Now);
    }

    [TestMethod]
    [DataRow("Ana", true)]
    [DataRow("Dr. Ana O'Brien-Smith", true)]
    [DataRow("Zoë Ångström", true)]
    [DataRow("観測者", true)]
    [DataRow("", false)]
    [DataRow(" Ana", false)]
    [DataRow("Ana ", false)]
    [DataRow("Ana\nExample", false)]
    [DataRow("Ana\u202Eelpmaxe", false)]
    [DataRow("Ana\u200BE", false)]
    public void ADisplayName_IsPrintableText(string value, bool valid)
        => RoofIdentityContract.IsValidDisplayName(value).Should().Be(valid);

    [TestMethod]
    public void TheNames_HaveTheirLengthLimits()
    {
        RoofIdentityContract.IsValidDisplayName(new string('a', RoofIdentityContract.MaximumDisplayNameLength)).Should().BeTrue();
        RoofIdentityContract.IsValidDisplayName(new string('a', RoofIdentityContract.MaximumDisplayNameLength + 1)).Should().BeFalse();
        RoofIdentityContract.IsValidShortName(new string('a', RoofIdentityContract.MaximumShortNameLength)).Should().BeTrue();
        RoofIdentityContract.IsValidShortName(new string('a', RoofIdentityContract.MaximumShortNameLength + 1)).Should().BeFalse();
        RoofIdentityContract.IsValidDisplayName("Ana\uD800").Should().BeFalse("a lone surrogate is not text");
        RoofIdentityContract.NormalizeLabel("  Ana  ").Should().Be("Ana");
        RoofIdentityContract.NormalizeLabel("   ").Should().BeNull();
        RoofIdentityContract.NormalizeLabel(null).Should().BeNull();
    }

    [TestMethod]
    [DataRow(null, RoofLoginTypes.Local)]
    [DataRow("virtual", RoofLoginTypes.Local)]
    [DataRow("linux", RoofLoginTypes.Linux)]
    [DataRow("LINUX", RoofLoginTypes.Linux)]
    public void TheLoginType_IsDerivedFromTheProvider(string? provider, string expected)
    {
        RoofLoginTypes.FromProvider(provider).Should().Be(expected);
        new RoofPinUserResponse("alice", Operator) { Provider = provider }.LoginType.Should().Be(expected);
    }
}
