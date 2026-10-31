using FluentAssertions;
using HVO.RoofControllerV4.Cli.Ui;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Terminal.Gui.Input;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// The roof command history on the terminal interface's Roof page (#206): History shows it in place of the
/// controller's readings, newest first, the caller's own or everyone's for an admin, and Readings shows them again.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RoofTerminalUiHistoryTests
{
    [TestMethod]
    public void History_ShowsTheCommandsInPlaceOfTheReadings_AndReadingsShowsThemAgain()
    {
        using var host = RoofClientApiTests.CreateHost();
        using (var operatorClient = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Operator)))
        {
            operatorClient.Roof.OpenAsync().GetAwaiter().GetResult();
        }

        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.Describe().Should().NotContain("COMMAND HISTORY");

        page.HistoryButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the history", () => page.Describe().Contains("Everyone's roof commands (admin).", StringComparison.Ordinal));

        page.ShowsHistory.Should().BeTrue();
        page.HistoryButton.Text.Should().Be("Readings");
        tui.Screen.Should().Contain("COMMAND HISTORY");
        page.Describe().Should().MatchRegex(@"\d{4}-\d\d-\d\d \d\d:\d\d:\d\dZ  Open  Accepted")
            .And.Contain("test-operator (API key); roof Closed; from ")
            .And.Contain("1–1 of 1 command. 'hvo-roof history' reads more.");

        page.HistoryButton.InvokeCommand(Command.Accept);
        tui.Pump();

        page.ShowsHistory.Should().BeFalse();
        page.HistoryButton.Text.Should().Be("History");
        tui.Screen.Should().Contain("CONTROLLER READINGS");
        page.Describe().Should().NotContain("Everyone's roof commands");
    }

    [TestMethod]
    public void History_OfAnOperator_IsTheirOwn_AndRefreshReadsItAgain()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.HistoryButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the empty history", () => page.Describe().Contains(RoofHistoryText.Empty, StringComparison.Ordinal));
        page.Describe().Should().Contain("Your own roof commands.");

        tui.Press(Key.F9);
        tui.WaitIdle("the Stop answer", () => tui.Ui.StopResult != RoofStopText.Sending);
        tui.Ui.CurrentPage.SubViews.OfType<Terminal.Gui.Views.Button>().Single(button => button.Text == "Refresh").InvokeCommand(Command.Accept);
        tui.WaitIdle("the Stop in the history", () => page.Describe().Contains("Stop  Accepted", StringComparison.Ordinal));
    }

    private static TuiDriver Started(CliRig rig)
    {
        var tui = new TuiDriver(rig);
        try
        {
            tui.WaitIdle("the first status", () => tui.Ui.Status is not null && !tui.Ui.IsStale && tui.Ui.Caller is not null);
            return tui;
        }
        catch
        {
            tui.Dispose();
            throw;
        }
    }
}
