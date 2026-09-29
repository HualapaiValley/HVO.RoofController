using System.Security.Claims;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Controllers;
using HVO.RoofControllerV4.RPi.Security;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

[TestClass]
public sealed class RoofPrincipalTests
{
    private const string OperatorKey = "test-operator-key-for-principal-tests-1";

    [TestMethod]
    [DataRow("RoofAdmin", true, true, true)]
    [DataRow("roofadmin", true, true, true)]
    [DataRow("RoofOperator", false, true, true)]
    [DataRow("RoofViewer", false, false, true)]
    public void Create_GrantsImpliedRoles(string role, bool admin, bool @operator, bool viewer)
    {
        var principal = RoofPrincipalFactory.Create(new RoofApiKeyIdentity("key", role, "id"), "Test");

        Assert.AreEqual(admin, principal.IsInRole(RoofControllerApiContract.AdminRole));
        Assert.AreEqual(@operator, principal.IsInRole(RoofControllerApiContract.OperatorRole));
        Assert.AreEqual(viewer, principal.IsInRole(RoofControllerApiContract.ViewerRole));
        Assert.AreEqual("key", principal.Identity?.Name);
        Assert.AreEqual("id", principal.FindFirst(RoofPrincipalFactory.KeyIdClaimType)?.Value);
    }

    [TestMethod]
    public void UnknownRole_GrantsNothing()
    {
        Assert.IsNull(RoofPrincipalFactory.NormalizeRole("Superuser"));
        RoofPrincipalFactory.GetImpliedRoles("Superuser").Should().BeEmpty();
        Assert.IsNull(RoofPrincipalFactory.GetHighestRole(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.IsNull(RoofPrincipalFactory.GetHighestRole(null));
    }

    [TestMethod]
    public void GetHighestRole_ReturnsTopRole()
    {
        var principal = RoofPrincipalFactory.Create(new RoofApiKeyIdentity("key", "RoofOperator", "id"), "Test");

        Assert.AreEqual(RoofControllerApiContract.OperatorRole, RoofPrincipalFactory.GetHighestRole(principal));
    }

    [TestMethod]
    public void DescribeCaller_UsesKeyNameOrAnonymous()
    {
        var principal = RoofPrincipalFactory.Create(new RoofApiKeyIdentity("console-operator", "RoofViewer", "id"), "Test");

        Assert.AreEqual("console-operator", RoofPrincipalFactory.DescribeCaller(principal));
        Assert.AreEqual("anonymous", RoofPrincipalFactory.DescribeCaller(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.AreEqual("anonymous", RoofPrincipalFactory.DescribeCaller(null));
    }

    [TestMethod]
    public void IsStillValid_TracksKeyRemovalAndRoleChanges()
    {
        var monitor = KeyStoreFactory.Monitor(KeyStoreFactory.Key("operator", "RoofOperator", OperatorKey));
        using var store = KeyStoreFactory.Create(monitor);
        store.TryValidate(OperatorKey, out var identity);
        var principal = RoofPrincipalFactory.Create(identity!, RoofControllerSecurityDefaults.ApiKeyScheme);

        Assert.IsTrue(RoofCredentialValidator.ForKeysOnly(store).IsStillValid(principal));

        monitor.Set(new RoofControllerSecurityOptions { ApiKeys = [KeyStoreFactory.Key("operator", "RoofViewer", OperatorKey)] });
        Assert.IsFalse(RoofCredentialValidator.ForKeysOnly(store).IsStillValid(principal), "a demoted key ends the session");

        monitor.Set(new RoofControllerSecurityOptions { ApiKeys = [KeyStoreFactory.Key("renamed", "RoofOperator", OperatorKey)] });
        Assert.IsFalse(RoofCredentialValidator.ForKeysOnly(store).IsStillValid(principal), "a renamed key ends the session");

        monitor.Set(new RoofControllerSecurityOptions());
        Assert.IsFalse(RoofCredentialValidator.ForKeysOnly(store).IsStillValid(principal), "a removed key ends the session");
    }

    [TestMethod]
    public void IsStillValid_Anonymous_IsTrue()
    {
        using var store = KeyStoreFactory.Create(KeyStoreFactory.Monitor());

        Assert.IsTrue(RoofCredentialValidator.ForKeysOnly(store).IsStillValid(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.IsTrue(RoofCredentialValidator.ForKeysOnly(store).IsStillValid(null));
    }

    [TestMethod]
    public void DescribeChanges_ListsOnlyChangedSettings()
    {
        var before = new RoofControllerOptionsV4();
        var after = before with { OpenRelayId = 2, CloseRelayId = 1, SafetyWatchdogTimeout = TimeSpan.FromSeconds(120) };

        Assert.AreEqual("none", RoofController.DescribeChanges(before, before with { }));
        var changes = RoofController.DescribeChanges(before, after);
        changes.Split("; ").Should().BeEquivalentTo(
        [
            "SafetyWatchdogTimeout: 00:01:30 -> 00:02:00",
            "OpenRelayId: 1 -> 2",
            "CloseRelayId: 2 -> 1"
        ]);
    }
}
