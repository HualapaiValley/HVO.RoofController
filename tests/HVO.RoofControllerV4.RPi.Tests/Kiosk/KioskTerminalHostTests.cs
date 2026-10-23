using FluentAssertions;
using HVO.RoofControllerV4.Screens;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;
using KioskProgram = HVO.RoofControllerV4.Kiosk.Program;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

[TestClass]
[DoNotParallelize]
public sealed class KioskTerminalHostTests
{
    [TestMethod]
    [DataRow(true, "1")]
    [DataRow(false, null)]
    [DataRow(false, "0")]
    public async Task WindowedAndUnmanagedHosts_DoNotOfferTerminalExit(bool window, string? enabled)
    {
        await using var harness = await KioskHarness.CreateAsync();
        KioskProgram.CreateTerminalExit(harness.Console, window, enabled, () => null).Should().BeNull();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstalledHost_VerifiesThePin_ThenSynchronouslyExitsItsUiLifetime_WithTheTerminalCode(bool lifetimeAvailable)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        var lifetime = new Lifetime();
        var exit = KioskProgram.CreateTerminalExit(harness.Console, false, "1", () => lifetimeAvailable ? lifetime.Shutdown : null)!;
        await OnUiAsync(exit.Ask);
        lifetime.Exits.Should().Be(0);
        var confirming = await OnUiAsync(() => exit.ConfirmAsync());
        await UntilAsync(() => confirming.IsCompleted, "host authorization and shutdown to complete");
        await confirming;
        if (lifetimeAvailable)
        {
            lifetime.Exits.Should().Be(1, "the callback commits shutdown in the same UI turn");
            lifetime.ExitCode.Should().Be(KioskProgram.TerminalExitCode);
        }
        else
        {
            lifetime.Exits.Should().Be(0);
            exit.Message.Should().Contain("could not exit");
        }
    }

    private sealed class Lifetime
    {
        public int Exits { get; private set; }
        public int ExitCode { get; private set; }
        public void Shutdown(int exitCode) { Exits++; ExitCode = exitCode; }
    }
}
