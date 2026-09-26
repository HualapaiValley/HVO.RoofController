using System.Security.Claims;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Controllers;
using HVO.RoofControllerV4.RPi.Security;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

[TestClass]
public sealed class RoofPrincipalAndAccountTests
{
    private const string ConsoleKey = "test-console-key-for-principal-tests-01";

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
        var principal = RoofPrincipalFactory.Create(new RoofApiKeyIdentity("ipad-dome", "RoofViewer", "id"), "Test");

        Assert.AreEqual("ipad-dome", RoofPrincipalFactory.DescribeCaller(principal));
        Assert.AreEqual("anonymous", RoofPrincipalFactory.DescribeCaller(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.AreEqual("anonymous", RoofPrincipalFactory.DescribeCaller(null));
    }

    [TestMethod]
    [DataRow(null, "/")]
    [DataRow("", "/")]
    [DataRow("   ", "/")]
    [DataRow("/", "/")]
    [DataRow("/roof", "/roof")]
    [DataRow("/roof?tab=camera", "/roof?tab=camera")]
    [DataRow("roof", "/")]
    [DataRow("//evil.example", "/")]
    [DataRow("/\\evil.example", "/")]
    [DataRow("https://evil.example/", "/")]
    [DataRow("javascript:alert(1)", "/")]
    [DataRow("/roof\\..\\x", "/")]
    [DataRow("/roof\r\nSet-Cookie: x=1", "/")]
    [DataRow("/account/logout", "/")]
    [DataRow("/Account/Login", "/")]
    [DataRow("/account", "/")]
    public void GetSafeReturnUrl_AllowsOnlyLocalPaths(string? input, string expected)
    {
        Assert.AreEqual(expected, RoofAccountEndpoints.GetSafeReturnUrl(input));
    }

    [TestMethod]
    public void GetSafeReturnUrl_OverlongUrl_FallsBackToRoot()
    {
        Assert.AreEqual("/", RoofAccountEndpoints.GetSafeReturnUrl("/" + new string('a', 5000)));
    }

    [TestMethod]
    [DataRow("1", "/", "/login?error=1")]
    [DataRow("2", "/roof", "/login?error=2&returnUrl=%2Froof")]
    [DataRow("1", "/roof?a=1&b=2", "/login?error=1&returnUrl=%2Froof%3Fa%3D1%26b%3D2")]
    public void BuildLoginRedirect_EscapesReturnUrl(string error, string returnUrl, string expected)
    {
        Assert.AreEqual(expected, RoofAccountEndpoints.BuildLoginRedirect(error, returnUrl));
    }

    [TestMethod]
    public void IsStillValid_TracksKeyRemovalAndRoleChanges()
    {
        var monitor = KeyStoreFactory.Monitor(KeyStoreFactory.Key("console", "RoofOperator", ConsoleKey));
        using var store = KeyStoreFactory.Create(monitor);
        store.TryValidate(ConsoleKey, out var identity);
        var principal = RoofPrincipalFactory.Create(identity!, RoofControllerSecurityDefaults.CookieScheme);

        Assert.IsTrue(RoofConsoleAuthenticationStateProvider.IsStillValid(store, principal));

        monitor.Set(new RoofControllerSecurityOptions { ApiKeys = [KeyStoreFactory.Key("console", "RoofViewer", ConsoleKey)] });
        Assert.IsFalse(RoofConsoleAuthenticationStateProvider.IsStillValid(store, principal), "a demoted key ends the session");

        monitor.Set(new RoofControllerSecurityOptions { ApiKeys = [KeyStoreFactory.Key("renamed", "RoofOperator", ConsoleKey)] });
        Assert.IsFalse(RoofConsoleAuthenticationStateProvider.IsStillValid(store, principal), "a renamed key ends the session");

        monitor.Set(new RoofControllerSecurityOptions());
        Assert.IsFalse(RoofConsoleAuthenticationStateProvider.IsStillValid(store, principal), "a removed key ends the session");
    }

    [TestMethod]
    public void IsStillValid_Anonymous_IsTrue()
    {
        using var store = KeyStoreFactory.Create(KeyStoreFactory.Monitor());

        Assert.IsTrue(RoofConsoleAuthenticationStateProvider.IsStillValid(store, new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.IsTrue(RoofConsoleAuthenticationStateProvider.IsStillValid(store, null));
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
