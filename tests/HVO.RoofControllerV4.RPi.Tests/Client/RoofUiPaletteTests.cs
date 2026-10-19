using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>All twenty shared roles follow the pinned Sky contract; Web parity converges at its owned CSS migration.</summary>
[TestClass]
public sealed class RoofUiPaletteTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ProductionOptions.AppSettingsPath)!, "..", ".."));
    private static readonly string Contract = Path.Combine(RepositoryRoot, "docs", "ui-redesign", "tokens.json");

    [TestMethod]
    public void EveryCanonicalRole_MatchesItsProductionPaletteMember()
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Contract));
        var roles = data.RootElement.GetProperty("roles").EnumerateArray().ToArray();
        roles.Should().HaveCount(20);
        foreach (var role in roles)
        {
            var name = role.GetProperty("role").GetString();
            var member = name == "surface-raised" ? "SurfaceRaised" : role.GetProperty("roof_palette_member").GetString();
            var field = typeof(RoofUiPalette).GetField(member!, BindingFlags.Public | BindingFlags.Static);
            field.Should().NotBeNull($"{name} has a consumed shared palette member");
            field!.GetRawConstantValue().Should().Be(role.GetProperty("value").GetString(), $"{name} mirrors pinned Sky bytes");
        }
    }

    [TestMethod]
    public void CompatibilityAliases_KeepSpecializedConsumersWithinCanonicalRoles()
    {
        RoofUiPalette.Badge.Should().Be(RoofUiPalette.SurfaceRaised);
        RoofUiPalette.MutedWeak.Should().Be(RoofUiPalette.Muted);
        RoofUiPalette.FocusRing.Should().Be(RoofUiPalette.Accent);
        RoofUiPalette.StopButton.Should().Be(RoofUiPalette.DangerBackground);
        RoofUiPalette.StopButtonText.Should().Be(RoofUiPalette.DangerText);
        RoofUiPalette.OpenButton.Should().Be(RoofUiPalette.SurfaceRaised);
        RoofUiPalette.CloseButton.Should().Be(RoofUiPalette.SurfaceRaised);
        RoofUiPalette.OpenButtonText.Should().Be(RoofUiPalette.Text);
        RoofUiPalette.CloseButtonText.Should().Be(RoofUiPalette.Text);
    }

    [TestMethod]
    public void ActiveWebSource_HasItsDeclaredPalette_AndMigratedSourceMustMatchAllCanonicalRoles()
    {
        var web = Path.Combine(RepositoryRoot, "src", "HVO.RoofControllerV4.Web");
        var migrated = Path.Combine(web, "wwwroot", "css", "hvo-dark.css");
        var app = File.ReadAllText(Path.Combine(web, "Components", "App.razor"));
        if (!File.Exists(migrated))
        {
            // Explicit staged dependency on #174: this branch has not changed the legacy Web product. This is a
            // source-presence assertion, never a claim that its old Bootstrap actions already match the new palette.
            app.Should().Contain("_content/HVO.WebSite.Themes/css/themes/hvo-dark.css");
            File.Exists(Path.Combine(RepositoryRoot, "src", "HVO.WebSite.Themes", "wwwroot", "css", "themes", "hvo-dark.css")).Should().BeTrue();
            return;
        }
        app.Should().NotContain("_content/HVO.WebSite.Themes");
        app.Should().Contain("css/hvo-dark.css");
        var css = File.ReadAllText(migrated);
        using var data = JsonDocument.Parse(File.ReadAllText(Contract));
        foreach (var role in data.RootElement.GetProperty("roles").EnumerateArray())
        {
            var name = role.GetProperty("role").GetString();
            var value = role.GetProperty("value").GetString();
            css.Should().MatchRegex($@"--roof-{Regex.Escape(name!)}\s*:\s*{Regex.Escape(value!)}\s*;", $"active Web role {name} agrees with native and shared Client");
        }
    }
}
