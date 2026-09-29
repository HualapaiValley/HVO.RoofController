using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The colours of the interfaces that are not web pages (the terminal interface, the kiosk and the Mac app) are the web
/// UI's: each <see cref="RoofUiPalette"/> value is checked against the HVO Dark stylesheet, and Stop, Open and Close
/// against the web UI's buttons. A new theme copied into HVO.WebSite.Themes fails here until the palette
/// follows it.
/// </summary>
[TestClass]
public sealed partial class RoofUiPaletteTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ProductionOptions.AppSettingsPath)!, "..", ".."));

    private static readonly string Theme = File.ReadAllText(
        Path.Combine(RepositoryRoot, "src", "HVO.WebSite.Themes", "wwwroot", "css", "themes", "hvo-dark.css"));

    private static readonly string Web = Path.Combine(RepositoryRoot, "src", "HVO.RoofControllerV4.Web");

    [TestMethod]
    [DataRow(RoofUiPalette.Background, "--hvo-body-bg")]
    [DataRow(RoofUiPalette.Text, "--hvo-body-color")]
    [DataRow(RoofUiPalette.Muted, "--hvo-muted")]
    [DataRow(RoofUiPalette.MutedWeak, "--hvo-muted-weak")]
    [DataRow(RoofUiPalette.Surface, "--hvo-surface")]
    [DataRow(RoofUiPalette.Border, "--hvo-border-stronger")]
    [DataRow(RoofUiPalette.Badge, "--hvo-nav-badge-bg")]
    [DataRow(RoofUiPalette.Accent, "--hvo-accent")]
    [DataRow(RoofUiPalette.AccentStrong, "--hvo-accent-strong")]
    [DataRow(RoofUiPalette.Success, "--hvo-success-strong")]
    [DataRow(RoofUiPalette.SuccessText, "--hvo-success-fg")]
    [DataRow(RoofUiPalette.SuccessBackground, "--hvo-success-bg")]
    [DataRow(RoofUiPalette.Warning, "--hvo-warning-strong")]
    [DataRow(RoofUiPalette.WarningText, "--hvo-warning-fg")]
    [DataRow(RoofUiPalette.WarningBackground, "--hvo-warning-bg")]
    [DataRow(RoofUiPalette.Danger, "--hvo-danger-strong")]
    [DataRow(RoofUiPalette.DangerText, "--hvo-danger-fg")]
    [DataRow(RoofUiPalette.DangerBackground, "--hvo-danger-bg")]
    [DataRow(RoofUiPalette.Info, "--hvo-info-strong")]
    [DataRow(RoofUiPalette.InfoText, "--hvo-info-fg")]
    [DataRow(RoofUiPalette.InfoBackground, "--hvo-info-bg")]
    public void EachColour_IsTheThemesToken(string colour, string token)
    {
        var tokens = ThemeTokens();
        tokens.Should().ContainKey(token);
        colour.Should().Be(AsDrawn(tokens[token], tokens["--hvo-body-bg"]), $"{token} is {tokens[token]} in hvo-dark.css");
    }

    [TestMethod]
    public void TheFocusRing_IsTheThemes()
        => Theme.Should().Contain($"outline: 2px solid {RoofUiPalette.FocusRing};");

    [TestMethod]
    [DataRow("stop", "btn-warning", RoofUiPalette.StopButton, RoofUiPalette.StopButtonText)]
    [DataRow("open", "btn-success", RoofUiPalette.OpenButton, RoofUiPalette.OpenButtonText)]
    [DataRow("close", "btn-danger", RoofUiPalette.CloseButton, RoofUiPalette.CloseButtonText)]
    public void StopOpenAndClose_HaveTheWebUisButtonColours(string testId, string buttonClass, string background, string text)
    {
        // Stop is on every page (App.razor); Open and Close are on the dashboard.
        var page = File.ReadAllText(Path.Combine(Web, "Components", "App.razor"))
            + File.ReadAllText(Path.Combine(Web, "Components", "Pages", "Dashboard.razor"));
        page.Should().MatchRegex($"<button class=\"btn {buttonClass} [^\"]*\"[^>]*data-testid=\"{testId}\"");

        // The theme leaves these buttons to Bootstrap.
        Theme.Should().NotContain($".{buttonClass} {{");
        var bootstrap = File.ReadAllText(Path.Combine(Web, "wwwroot", "lib", "bootstrap", "css", "bootstrap.min.css"));
        var rule = Regex.Match(bootstrap, $@"\.{buttonClass}\{{([^}}]*)\}}");
        rule.Success.Should().BeTrue();
        Normalise(Property(rule.Groups[1].Value, "--bs-btn-bg")).Should().Be(background);
        Normalise(Property(rule.Groups[1].Value, "--bs-btn-color")).Should().Be(text);
    }

    [TestMethod]
    public void TheTheme_IsTheOneTheWebUiLoads()
    {
        var app = File.ReadAllText(Path.Combine(Web, "Components", "App.razor"));
        app.Should().Contain("_content/HVO.WebSite.Themes/css/themes/hvo-dark.css");
        app.Should().Contain("data-theme=\"hvo-dark\"");
    }

    private static Dictionary<string, string> ThemeTokens()
    {
        var root = RootBlock().Match(Theme);
        root.Success.Should().BeTrue("hvo-dark.css starts with its :root tokens");
        return TokenLine().Matches(root.Groups[1].Value).ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value.Trim());
    }

    // A colour as the page shows it: rgba() over the page background, rounded to whole channels.
    private static string AsDrawn(string value, string background)
    {
        var rgba = Rgba().Match(value);
        if (!rgba.Success)
        {
            return Normalise(value);
        }

        var under = Channels(Normalise(background));
        var alpha = double.Parse(rgba.Groups[4].Value, CultureInfo.InvariantCulture);
        var channels = Enumerable.Range(0, 3)
            .Select(i => (int)Math.Round(alpha * int.Parse(rgba.Groups[i + 1].Value, CultureInfo.InvariantCulture) + (1 - alpha) * under[i], MidpointRounding.AwayFromZero));
        return "#" + string.Concat(channels.Select(channel => channel.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static int[] Channels(string hex) => [Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16)];

    // #rgb as #rrggbb, lower case.
    private static string Normalise(string hex)
    {
        hex = hex.Trim().ToLowerInvariant();
        return hex.Length == 4 ? $"#{hex[1]}{hex[1]}{hex[2]}{hex[2]}{hex[3]}{hex[3]}" : hex;
    }

    private static string Property(string declarations, string name)
        => declarations.Split(';').Select(part => part.Split(':', 2)).Single(pair => pair[0].Trim() == name)[1];

    [GeneratedRegex(@"^:root\[data-theme=""hvo-dark""\]\s*\{([^}]*)\}")]
    private static partial Regex RootBlock();

    [GeneratedRegex(@"(--[a-z0-9-]+)\s*:\s*([^;]+);")]
    private static partial Regex TokenLine();

    [GeneratedRegex(@"^rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([0-9.]+)\s*\)$")]
    private static partial Regex Rgba();
}
