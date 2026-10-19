using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Cli.Ui;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.TerminalUi;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>Protocol, cell geometry and actual bounded page/prompt renders, independently of the owner's terminal.</summary>
[TestClass]
[DoNotParallelize]
public sealed class TerminalDesignTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("truecolor", "xterm-256color", "C.UTF-8", null, null, TerminalColours.TrueColour, false)]
    [DataRow("24bit", "xterm", "en_US.utf8", null, null, TerminalColours.TrueColour, false)]
    [DataRow(null, "xterm-256color", "C.UTF-8", null, null, TerminalColours.Ansi16, false)]
    [DataRow("truecolor", "dumb", "C.UTF-8", null, null, TerminalColours.None, true)]
    [DataRow("truecolor", "vt100", "C.UTF-8", null, null, TerminalColours.TrueColour, true)]
    [DataRow("truecolor", "xterm", "C", null, null, TerminalColours.TrueColour, true)]
    [DataRow(null, "xterm", "C", "truecolor,unicode", null, TerminalColours.TrueColour, false)]
    [DataRow("truecolor", "xterm", "C.UTF-8", "ansi16,ascii", "", TerminalColours.Ansi16, true)]
    [DataRow("truecolor", "xterm", "C.UTF-8", "truecolor", "0", TerminalColours.None, false)]
    public void Capabilities_AreExplicit(string? colour, string term, string locale, string? overrides, string? noColour, TerminalColours expected, bool ascii)
    {
        var env = new Dictionary<string, string?> { ["COLORTERM"] = colour, ["TERM"] = term, ["LANG"] = locale, ["HVO_TERMINAL"] = overrides, ["NO_COLOR"] = noColour };
        TerminalCapabilities.Detect(env.GetValueOrDefault).Should().Be(new TerminalCapabilities(expected, ascii));
        env["LC_CTYPE"] = "C";
        env["LC_ALL"] = "en_US.UTF-8";
        TerminalCapabilities.Detect(env.GetValueOrDefault).Ascii.Should().Be(overrides?.Contains("ascii") == true || term is "dumb" or "vt100");
    }

    [TestMethod]
    [DataRow("é", 1)]
    [DataRow("屋根", 4)]
    [DataRow("👩🏽‍🔬", 2)]
    [DataRow("🇬🇧", 2)]
    [DataRow("1️⃣", 2)]
    public void Geometry_KeepsWholeGraphemes(string text, int cells)
    {
        TerminalCells.Width(text).Should().Be(cells);
        TerminalCells.Width(TerminalCells.PadRight(text, cells + 3)).Should().Be(cells + 3);
        var source = string.Concat(Enumerable.Repeat(text, 8));
        var wrapped = TerminalCells.Wrap(source, cells + 1).ToArray();
        wrapped.Should().OnlyContain(line => TerminalCells.Width(line) <= cells + 1);
        string.Concat(wrapped).Should().Be(source);
        TerminalCells.Graphemes(source).Should().HaveCount(text == "屋根" ? 16 : 8);
    }

    [TestMethod]
    public void PlainTables_AlignWideAndCombiningValues_WithoutAnsi()
    {
        using var output = new StringWriter();
        RoofCliFormat.WriteTable(output, ["NAME", "ROLE"], [["屋根", "viewer"], ["é", "admin"]]);
        var rows = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        TerminalCells.Width(rows[1][..rows[1].IndexOf("viewer", StringComparison.Ordinal)]).Should().Be(6);
        TerminalCells.Width(rows[2][..rows[2].IndexOf("admin", StringComparison.Ordinal)]).Should().Be(6);
        output.ToString().Should().NotContain("\u001b");
    }

    [TestMethod]
    [DataRow("truecolor,unicode", 80, 24)]
    [DataRow("ansi16,unicode", 80, 24)]
    [DataRow("nocolor,unicode", 80, 24)]
    [DataRow("ansi16,ascii", 80, 24)]
    [DataRow("truecolor,unicode", 120, 32)]
    [DataRow("ansi16,unicode", 120, 32)]
    [DataRow("nocolor,unicode", 120, 32)]
    [DataRow("nocolor,ascii", 120, 32)]
    public void EveryPageAndLongPrivatePrompt_KeepStopAndNewestResultVisible(string mode, int width, int height)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Environment["HVO_TERMINAL"] = mode;
        using var tui = new TuiDriver(rig, width, height);
        tui.WaitIdle("live status", () => tui.Ui.Caller is not null && tui.Ui.Status is not null && !tui.Ui.IsStale);
        var tag = mode.Replace(',', '-');
        for (var index = 0; index < 5; index++)
        {
            tui.Ui.ShowPage(index);
            tui.WaitIdle("page data");
            tui.Screen.Should().Contain($"F{index + 1} {tui.Ui.CurrentPage.Title}").And.Contain("Stop roof (F9)");
            if (mode.EndsWith("ascii", StringComparison.Ordinal))
                tui.Screen.Should().NotContainAny("┌", "─", "│", "╭", "╰", "⟦", "⟧");
            tui.Render(TestContext, $"176-{tag}-{width}x{height}-page{index + 1}");
            tui.Ui.Ask(new RoofUiPrompt("LONG / PRIVATE REVIEW", string.Join(' ', Enumerable.Repeat("屋根 é 👩🏽‍🔬 Long explanation with a correction and safe cancellation.", 12)),
                Enumerable.Range(0, 8).Select(n => new RoofUiField($"Private entry {n + 1}", Secret: true)).ToArray(),
                [new RoofUiAction("Review", _ => "Correct the value; nothing was sent.")]));
            var panel = tui.Ui.Panel!;
            foreach (var field in panel.Fields) field.Text = "masked-fixture-value";
            panel.Fields[^1].SetFocus();
            tui.Screen.Should().NotContain("masked-fixture-value");
            var last = panel.Fields[^1].FrameToScreen();
            last.Y.Should().BeGreaterThan(panel.Frame.Y).And.BeLessThan(tui.Ui.StopButton.FrameToScreen().Y);
            panel.Press("Review");
            panel.Error.Should().Be("Correct the value; nothing was sent.");
            tui.Press(Key.F9);
            tui.WaitIdle("Stop above private form", () => tui.Ui.StopResult != RoofStopText.Sending);
            tui.Ui.StopResult.Should().Be(RoofStopText.AcknowledgedVerified);
            tui.Screen.Should().Contain("Stop acknowledged.").And.Contain("Relay register verified de-energized.");
            tui.Ui.StopButton.SetFocus();
            var colours = tui.Ui.StopButton.GetScheme();
            if (mode.StartsWith("ansi16", StringComparison.Ordinal))
            {
                colours.Normal.Background.Should().Be(new Color(ColorName16.Red));
                colours.Focus.Background.Should().Be(colours.Normal.Background);
                tui.App.Driver!.Force16Colors.Should().BeTrue();
            }
            if (mode.StartsWith("nocolor", StringComparison.Ordinal)) tui.DrawnAttributes().Should().OnlyContain(a => a.Foreground == Color.None && a.Background == Color.None);
            tui.Render(TestContext, $"176-{tag}-{width}x{height}-prompt{index + 1}");
            tui.Press(Key.Esc);
            tui.Ui.Panel.Should().BeNull();
        }
    }

    [TestMethod]
    public void Ansi16_StopFeedbackUsesStandaloneRoles_AndDefaultsRemainUncoloured()
    {
        var theme = RoofUiTheme.For(key => key == "HVO_TERMINAL" ? "ansi16,unicode" : null);
        foreach (var (outcome, colour) in new[] { (RoofStopOutcome.Sent, ColorName16.BrightCyan), (RoofStopOutcome.Acknowledged, ColorName16.BrightGreen), (RoofStopOutcome.RelayUnverified, ColorName16.BrightYellow), (RoofStopOutcome.Failed, ColorName16.BrightRed) })
        {
            var attr = theme.ForStop(outcome).Normal;
            attr.Foreground.Should().Be(new Color(colour));
            attr.Background.Should().Be(new Color(ColorName16.Black));
            attr.Foreground.Should().NotBe(attr.Background);
        }
        theme.Warning.Normal.Foreground.Should().Be(new Color(ColorName16.Black));
        theme.Warning.Normal.Background.Should().Be(new Color(ColorName16.Yellow));
    }
}
