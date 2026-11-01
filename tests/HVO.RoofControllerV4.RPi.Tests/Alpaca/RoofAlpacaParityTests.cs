using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// An Alpaca Close goes through the controller's own Close path, with every check an ordinary Close has (#220, §2 O6):
/// each hard refusal an ordinary Close gives, the Alpaca Close gives too, with the same code, and neither switches a
/// relay. (The repeat and reversal rules differ by design and are tested on their own.)
/// </summary>
[TestClass]
public sealed class RoofAlpacaParityTests
{
    [TestMethod]
    [DataRow("fault latched", RoofControllerErrorCode.FaultLatched)]
    [DataRow("inputs unreadable", RoofControllerErrorCode.HardwareUnavailable)]
    [DataRow("drive fault active", RoofControllerErrorCode.InterlockActive)]
    [DataRow("contradictory limits", RoofControllerErrorCode.InterlockActive)]
    [DataRow("clear-fault pulse", RoofControllerErrorCode.OperationInProgress)]
    [DataRow("shutting down", RoofControllerErrorCode.ShuttingDown)]
    public async Task EveryHardRefusalOfAnOrdinaryClose_IsTheAlpacaClosesToo(string condition, RoofControllerErrorCode expected)
    {
        // Each on a rig of its own: a refusal can itself latch, which would change what the other one meets.
        await using var ordinaryRig = (await AlpacaCommandRig.StartAsync()).Ready();
        var ordinaryPulse = await PrepareAsync(ordinaryRig, condition);
        var ordinary = await ordinaryRig.Controller.StartMotionAsync(new RoofMotionRequest(RoofMotionDirection.Closing) { Owner = new RoofMotionOwner(RoofMotionOwnerKind.Person, "ada") });

        await using var alpacaRig = (await AlpacaCommandRig.StartAsync()).Ready();
        var alpacaPulse = await PrepareAsync(alpacaRig, condition);
        var alpaca = await alpacaRig.CloseAsync();

        ordinary.ErrorCode().Should().Be(expected, ordinary.Error?.Message);
        alpaca.Code().Should().Be(expected, alpaca.Result.Error?.Message);
        foreach (var rig in new[] { ordinaryRig, alpacaRig })
        {
            rig.RelayOnWrites(1).Should().Be(0);
            rig.RelayOnWrites(2).Should().Be(0);
        }

        await Task.WhenAll(new[] { ordinaryPulse, alpacaPulse }.OfType<Task>());
    }

    private static async Task<Task?> PrepareAsync(AlpacaCommandRig rig, string condition)
    {
        Task? pulse = null;
        switch (condition)
        {
            case "fault latched":
                rig.Hat.SetInputs(true, true, true, false);
                rig.Controller.ForceStatusRefresh();
                rig.Hat.SetInputs(true, true, false, false); // the input recovered; the latch stays until cleared
                rig.Controller.ForceStatusRefresh();
                break;
            case "inputs unreadable":
                rig.Hat.Registers.FailInputReads = true;
                break;
            case "drive fault active":
                rig.Hat.SetInputs(true, true, true, false);
                break;
            case "contradictory limits":
                rig.Hat.SetInputs(false, false, false, false); // both limits active (NC: LOW)
                break;
            case "clear-fault pulse":
                pulse = rig.Controller.ClearFault(RoofControllerLimits.MaxClearFaultPulseMilliseconds);
                await Task.Delay(200);
                break;
            default:
                await rig.Controller.ShutdownAsync(CancellationToken.None);
                break;
        }

        rig.Hat.ClearRelayWriteLog();
        return pulse;
    }
}
