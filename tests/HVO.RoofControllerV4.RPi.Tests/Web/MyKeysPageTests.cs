using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Web.Components.Pages;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>Personal keys through a password session and the real in-process API, with private one-time values.</summary>
[TestClass]
public sealed class MyKeysPageTests
{
    [TestMethod]
    public async Task ProviderDisabled_IsExplained_WithoutOfferingCreation()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = context.Render<MyKeys>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=page-message]").TextContent.Should().Contain("Provider accounts are not enabled"));
        cut.FindAll("#owned-name").Should().BeEmpty();
    }

    [TestMethod]
    public async Task Empty_Invalid_Create_PrivateDone_AndRevoke_KeepTheirActualResults()
    {
        using var directory = new TemporaryDirectory();
        using var harness = new WebAdminHarness(new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"] = directory.File("identity.json"),
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true"
        });
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = context.Render<MyKeys>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=owned-keys-empty]").TextContent.Should().Contain("No personal keys"));
        cut.FindAll("button").Single(b => b.TextContent == "Create key").Click();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=page-message]").TextContent.Should().Contain("Give a name"));
        cut.Change("#owned-name", "capture-fixture");
        cut.FindAll("button").Single(b => b.TextContent == "Create key").Click();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=owned-key-secret]").TextContent.Should().Contain("New key: capture-fixture"));
        cut.Find("#owned-secret").GetAttribute("type").Should().Be("password");
        var secret = cut.Find("#owned-secret").GetAttribute("value");
        string.IsNullOrWhiteSpace(secret).Should().BeFalse();
        cut.FindAll("button").Single(b => b.TextContent == "Done").Click();
        await cut.WaitForAssertionAsync(() =>
        {
            cut.FindAll("#owned-secret").Count.Should().Be(0);
            cut.Markup.Contains(secret!, StringComparison.Ordinal).Should().BeFalse("Done removes the one-time value");
        });
        cut.FindAll("button").Single(b => b.TextContent == "Revoke").Click();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=owned-key]").TextContent.Should().Contain("Revoked"));
        cut.Find("[data-testid=page-message]").TextContent.Should().Contain("Key revoked.");
    }
}
