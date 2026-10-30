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
        RoofUiPalette.OpenButton.Should().Be(RoofUiPalette.MotionAvailable);
        RoofUiPalette.CloseButton.Should().Be(RoofUiPalette.MotionAvailable);
        RoofUiPalette.OpenButtonText.Should().Be(RoofUiPalette.MotionAvailableText);
        RoofUiPalette.CloseButtonText.Should().Be(RoofUiPalette.MotionAvailableText);
    }

    [TestMethod]
    public void ActionStateColours_AreCanonicalRoles_MatchTheContract_AndReadable()
    {
        // #208: Stop dim idle, bright while moving; Open/Close green offered, bright green running, neutral disabled.
        RoofUiPalette.StopButtonBorder.Should().Be(RoofUiPalette.Danger);
        RoofUiPalette.StopButtonActive.Should().Be(RoofUiPalette.Danger);
        RoofUiPalette.StopButtonActiveText.Should().Be(RoofUiPalette.Background);
        RoofUiPalette.StopButtonActiveBorder.Should().Be(RoofUiPalette.DangerText);
        RoofUiPalette.MotionAvailable.Should().Be(RoofUiPalette.SuccessBackground);
        RoofUiPalette.MotionAvailableText.Should().Be(RoofUiPalette.SuccessText);
        RoofUiPalette.MotionAvailableBorder.Should().Be(RoofUiPalette.Success);
        RoofUiPalette.MotionRunning.Should().Be(RoofUiPalette.Success);
        RoofUiPalette.MotionRunningText.Should().Be(RoofUiPalette.Background);
        RoofUiPalette.MotionRunningBorder.Should().Be(RoofUiPalette.SuccessText);
        RoofUiPalette.MotionDisabled.Should().Be(RoofUiPalette.Surface);
        RoofUiPalette.MotionDisabledText.Should().Be(RoofUiPalette.Muted);
        RoofUiPalette.MotionDisabledBorder.Should().Be(RoofUiPalette.Border);

        using var data = JsonDocument.Parse(File.ReadAllText(Contract));
        var roles = data.RootElement.GetProperty("roles").EnumerateArray()
            .ToDictionary(role => role.GetProperty("role").GetString()!, role => role.GetProperty("value").GetString()!);
        var actions = data.RootElement.GetProperty("roof_actions");
        foreach (var (action, background, text, border) in new[]
        {
            ("stop", RoofUiPalette.StopButton, RoofUiPalette.StopButtonText, RoofUiPalette.StopButtonBorder),
            ("stop-moving", RoofUiPalette.StopButtonActive, RoofUiPalette.StopButtonActiveText, RoofUiPalette.StopButtonActiveBorder),
            ("open", RoofUiPalette.MotionAvailable, RoofUiPalette.MotionAvailableText, RoofUiPalette.MotionAvailableBorder),
            ("close", RoofUiPalette.MotionAvailable, RoofUiPalette.MotionAvailableText, RoofUiPalette.MotionAvailableBorder),
            ("motion-running", RoofUiPalette.MotionRunning, RoofUiPalette.MotionRunningText, RoofUiPalette.MotionRunningBorder),
            ("motion-disabled", RoofUiPalette.MotionDisabled, RoofUiPalette.MotionDisabledText, RoofUiPalette.MotionDisabledBorder)
        })
        {
            var contract = actions.GetProperty(action);
            roles[contract.GetProperty("background").GetString()!].Should().Be(background, $"{action}'s background follows tokens.json");
            roles[contract.GetProperty("foreground").GetString()!].Should().Be(text, $"{action}'s text follows tokens.json");
            roles[contract.GetProperty("border").GetString()!].Should().Be(border, $"{action}'s edge follows tokens.json");
            Contrast(text, background).Should().BeGreaterThanOrEqualTo(4.5, $"{action}'s label reads as normal text");
        }

        Contrast(RoofUiPalette.MotionAvailable, RoofUiPalette.MotionRunning).Should().BeGreaterThanOrEqualTo(3, "running stands apart from offered");
        Contrast(RoofUiPalette.StopButton, RoofUiPalette.StopButtonActive).Should().BeGreaterThanOrEqualTo(3, "bright Stop stands apart from idle");
    }

    // WCAG 2 contrast ratio of two #rrggbb colours.
    private static double Contrast(string first, string second)
    {
        static double Luminance(string colour)
        {
            double Channel(int at)
            {
                var value = Convert.ToInt32(colour.Substring(at, 2), 16) / 255.0;
                return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Channel(1)) + (0.7152 * Channel(3)) + (0.0722 * Channel(5));
        }

        var (a, b) = (Luminance(first), Luminance(second));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
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
