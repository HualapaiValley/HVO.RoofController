using Avalonia.Controls;
using Avalonia.Media;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Screens;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>The kiosk roof page's safety chip: the state in its colour; the reasons are on Readings.</summary>
[TestClass, DoNotParallelize]
public sealed class KioskSafetyTests
{
    [TestMethod]
    public async Task TheRoofPage_ShowsTheSafetyState_InItsColour()
    {
        await using var harness = await KioskHarness.CreateAsync(populateUsers: false);
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, 1280, 720, 8.2);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Roof));
        await UntilAsync(() => screen.Find("safety") is not null, "the safety chip");

        await OnUiAsync(() =>
        {
            screen.Text("safety").Should().Be("Safety: Unknown", "the controller has no safety sources");
            Background().Should().Be(Color.Parse(RoofUiPalette.WarningBackground));
        });

        foreach (var (state, text, colour) in new[]
        {
            (RoofSafetyState.Unsafe, "Safety: Unsafe", RoofUiPalette.DangerBackground),
            (RoofSafetyState.Safe, "Safety: Safe", RoofUiPalette.SuccessBackground)
        })
        {
            await OnUiAsync(() =>
            {
                screen.Shell.Update(harness.Console.View with { Status = harness.Console.View.Status! with { Safety = new RoofSafetyStatus { State = state } } });
                screen.Text("safety").Should().Be(text);
                Background().Should().Be(Color.Parse(colour));
            });
        }

        await OnUiAsync(() =>
        {
            screen.Shell.Update(harness.Console.View with { StaleSince = KioskHarness.Start });
            screen.Text("safety").Should().Be("Safety: Unknown (last known)", "a stale status is shown as the last known state");
        });

        Color Background() => ((ISolidColorBrush)((Border)screen.Find("safety")!).Background!).Color;
    }
}
