using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Screens;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

[TestClass]
[DoNotParallelize]
public sealed class KioskTerminalRenderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(800, 480, 5.2)]
    [DataRow(1280, 720, 8.2)]
    [DataRow(1920, 1200, 8.2)]
    [DataRow(1920, 1200, 9.0)]
    public async Task Terminal_ExplainsLoginAndReturn_WithBoundedConfirmationErrorsAndStop(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        var exit = new KioskTerminalExit(harness.Console, () => Task.FromResult(false));
        await using var screen = await KioskScreen.ShowAsync(harness.Console, width, height, density, exit);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Terminal));
        await CapturePages("terminal-locked");
        await OnUiAsync(exit.Ask);
        await CapturePages("terminal-denied");
        await harness.UnlockAsync(KioskHarness.Operator);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Terminal));
        await OnUiAsync(exit.Ask);
        await CapturePages("terminal-confirm");
        var confirming = await OnUiAsync(() => exit.ConfirmAsync());
        await UntilAsync(() => confirming.IsCompleted, "the exit error");
        await confirming;
        exit.Message.Should().Contain("could not exit");
        await CapturePages("terminal-error");
        await screen.ClickAsync(screen.Shell.Stop);
        await harness.WaitForAsync(view => view.StopOutcome == RoofStopOutcome.Acknowledged, "Stop from the terminal page");
        await OnUiAsync(exit.Cancel);
        await CapturePages("terminal-cancelled");

        async Task CapturePages(string name)
        {
            await screen.DrawAsync();
            var pager = await OnUiAsync(() => screen.Shell.GetVisualDescendants().OfType<KioskPager>().Single(p => p.Name == "terminal-pages"));
            await OnUiAsync(() => pager.Move(-pager.PageCount));
            for (var index = 0; index < pager.PageCount; index++)
            {
                await screen.RenderAsync($"{name}-{density:0.0}-p{index + 1}", TestContext);
                await OnUiAsync(() => pager.Move(1));
            }
            await OnUiAsync(() => screen.Shell.Stop.IsEffectivelyEnabled.Should().BeTrue());
        }
    }
}
