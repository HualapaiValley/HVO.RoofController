using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

[TestClass]
public sealed class RoofApiKeyStoreTests
{
    private const string OperatorKey = "test-operator-key-for-store-tests-000001";
    private const string AdminKey = "test-admin-key-for-store-tests-0000000002";

    [TestMethod]
    public void TryValidate_ConfiguredKey_ReturnsIdentityWithCanonicalRoleAndOpaqueKeyId()
    {
        using var store = KeyStoreFactory.Create(KeyStoreFactory.Monitor(KeyStoreFactory.Key(" console-operator ", "roofoperator", OperatorKey)));

        var valid = store.TryValidate(OperatorKey, out var identity);

        Assert.IsTrue(valid);
        Assert.IsNotNull(identity);
        Assert.AreEqual("console-operator", identity.Name);
        Assert.AreEqual(RoofControllerApiContract.OperatorRole, identity.Role);
        identity.KeyId.Should().MatchRegex("^[0-9a-f]{32}$");
        identity.KeyId.Should().NotContain(OperatorKey);
        Assert.AreEqual(1, store.KeyCount);
        Assert.IsTrue(store.HasKeys);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("test-operator-key-for-store-tests-000002")]
    [DataRow("TEST-OPERATOR-KEY-FOR-STORE-TESTS-000001")]
    public void TryValidate_WrongOrMissingKey_Fails(string? presented)
    {
        using var store = KeyStoreFactory.Create(KeyStoreFactory.Monitor(KeyStoreFactory.Key("console-operator", "RoofOperator", OperatorKey)));

        Assert.IsFalse(store.TryValidate(presented, out var identity));
        Assert.IsNull(identity);
    }

    [TestMethod]
    public void TryValidate_OverlongKey_FailsWithoutHashing()
    {
        using var store = KeyStoreFactory.Create(KeyStoreFactory.Monitor(KeyStoreFactory.Key("console-operator", "RoofOperator", OperatorKey)));

        Assert.IsFalse(store.TryValidate(new string('k', RoofApiKeyStore.MaximumPresentedKeyLength + 1), out _));
    }

    [TestMethod]
    public void TryValidate_KeySha256Entry_AcceptsThePlainKeyInAnyHexCase()
    {
        var monitor = KeyStoreFactory.Monitor(new RoofApiKeyOptions
        {
            Name = "deploy",
            Role = "RoofOperator",
            KeySha256 = TestApiKeys.Sha256Hex(OperatorKey).ToUpperInvariant()
        });
        using var store = KeyStoreFactory.Create(monitor);

        Assert.IsTrue(store.TryValidate(OperatorKey, out var identity));
        Assert.AreEqual("deploy", identity.Name);
    }

    [TestMethod]
    public void Build_InvalidEntries_AreSkippedAndReportedWithoutKeyValues()
    {
        const string shortKey = "short-key-value-xyz";
        const string bothKey = "test-both-key-and-hash-000000000000001";
        var monitor = KeyStoreFactory.Monitor(
            KeyStoreFactory.Key("", "RoofViewer", "test-nameless-key-00000000000000001"),
            KeyStoreFactory.Key("bad-role", "Superuser", "test-bad-role-key-00000000000000001"),
            KeyStoreFactory.Key("too-short", "RoofViewer", shortKey),
            new RoofApiKeyOptions { Name = "both", Role = "RoofViewer", Key = bothKey, KeySha256 = TestApiKeys.Sha256Hex(bothKey) },
            new RoofApiKeyOptions { Name = "neither", Role = "RoofViewer" },
            new RoofApiKeyOptions { Name = "bad-hash", Role = "RoofViewer", KeySha256 = "not-hex" },
            KeyStoreFactory.Key("good", "RoofAdmin", AdminKey));
        using var store = KeyStoreFactory.Create(monitor);

        Assert.AreEqual(1, store.KeyCount);
        store.ConfigurationProblems.Should().HaveCount(6);
        store.ConfigurationProblems.Should().OnlyContain(p => p.StartsWith("ApiKeys[", StringComparison.Ordinal) && p.Contains("ignored", StringComparison.Ordinal));
        store.ConfigurationProblems.Should().Contain(p => p.Contains("ApiKeys[4] ('neither')", StringComparison.Ordinal) && p.Contains("RoofControllerSecurity__ApiKeys__4__Key", StringComparison.Ordinal));
        foreach (var secret in new[] { shortKey, bothKey, "test-nameless-key-00000000000000001", "test-bad-role-key-00000000000000001", AdminKey })
        {
            store.ConfigurationProblems.Should().NotContain(p => p.Contains(secret, StringComparison.Ordinal));
        }

        Assert.IsFalse(store.TryValidate(shortKey, out _));
        Assert.IsFalse(store.TryValidate(bothKey, out _));
        Assert.IsTrue(store.TryValidate(AdminKey, out _));
    }

    [TestMethod]
    public void Build_NoKeys_HasKeysIsFalse()
    {
        using var store = KeyStoreFactory.Create(KeyStoreFactory.Monitor());

        Assert.IsFalse(store.HasKeys);
        store.ConfigurationProblems.Should().BeEmpty();
        Assert.IsFalse(store.TryValidate(OperatorKey, out _));
    }

    [TestMethod]
    public void OnChange_ReloadedKeys_TakeEffectImmediately()
    {
        var monitor = KeyStoreFactory.Monitor(KeyStoreFactory.Key("old", "RoofOperator", OperatorKey));
        using var store = KeyStoreFactory.Create(monitor);
        Assert.IsTrue(store.TryValidate(OperatorKey, out _));

        monitor.Set(new RoofControllerSecurityOptions { ApiKeys = [KeyStoreFactory.Key("new", "RoofAdmin", AdminKey)] });

        Assert.IsFalse(store.TryValidate(OperatorKey, out _), "a removed key is revoked on reload");
        Assert.IsTrue(store.TryValidate(AdminKey, out var identity));
        Assert.AreEqual("new", identity.Name);
    }

    [TestMethod]
    public void TryFindByKeyId_FindsOnlyCurrentKeys()
    {
        var monitor = KeyStoreFactory.Monitor(KeyStoreFactory.Key("console-operator", "RoofOperator", OperatorKey));
        using var store = KeyStoreFactory.Create(monitor);
        store.TryValidate(OperatorKey, out var identity);

        Assert.IsTrue(store.TryFindByKeyId(identity!.KeyId, out var found));
        Assert.AreEqual(identity, found);
        Assert.IsFalse(store.TryFindByKeyId(null, out _));
        Assert.IsFalse(store.TryFindByKeyId("0123456789abcdef0123456789abcdef", out _));

        monitor.Set(new RoofControllerSecurityOptions());
        Assert.IsFalse(store.TryFindByKeyId(identity.KeyId, out _));
    }
}
