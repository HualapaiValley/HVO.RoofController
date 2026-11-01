using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Emulation;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Hat;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// A late ON after transport loss (#220, §20 N3, §21 required test 3), on the production host against the HAT emulator
/// over TCP: an ON the transport accepted is held past the client's request timeout and past the link coming back. No
/// Stop reports success while it is still pending, the relay never energizes after a Stop that did, and the fault latch
/// cannot be cleared until a read-back shows every relay off. The emulator's guarantee is the client-tagged Hello fence
/// (<c>HatEmulatorServer</c>): once the client's next connection is greeted, no write it gave up on runs.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory(Scenario.Category)]
public sealed class RelayTransportScenarios
{
    private static readonly IReadOnlyDictionary<string, string?> ShortRequests = new Dictionary<string, string?>
    {
        ["HatEmulator:RequestTimeout"] = "00:00:00.300",
        ["HatEmulator:ConnectTimeout"] = "00:00:00.300",
    };

    [TestMethod]
    public async Task AnOnHeldPastTheTimeoutAndTheLinksReturn_NeverEnergizesAfterAStopThatReportedSuccess()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(settings: ShortRequests));
        var server = rig.Server;
        const byte openRelay = 1;
        server.WriteHold = TimeSpan.FromSeconds(3);
        server.HoldWhen = frame =>
        {
            if (frame.Register != SmI010Board.RelaySetRegister || frame.Payload.Span[0] != openRelay)
            {
                return false;
            }

            // The open relay's ON is accepted, then the link drops before it runs: the client cannot know its fate.
            server.Outage = true;
            return true;
        };

        var open = rig.Controller.Open();
        open.IsSuccessful.Should().BeFalse("the open relay's write did not complete");
        var pending = Stopwatch.StartNew();

        // While the ON is pending and the link is down, no Stop may report success, and the latch stays.
        rig.Controller.Stop().ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified);
        var latched = rig.Controller.GetCurrentStatusSnapshot();
        latched.IsFaultLatched.Should().BeTrue();
        latched.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified);
        (await rig.Controller.ClearFault()).ErrorCode().Should().Be(RoofControllerErrorCode.RelayStateUnverified, "the relays cannot be read back off yet");

        // The link returns while the ON is still held; the Stop's new connection is greeted first, so it fences the ON.
        server.Outage = false;
        var stop = rig.Controller.Stop();
        stop.IsSuccessful.Should().BeTrue("the relays read back off on the new connection");
        pending.Elapsed.Should().BeLessThan(server.WriteHold, "the held ON is still pending when the Stop reports success");

        // From the successful Stop until well after the held ON's turn, the open relay never energizes.
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < server.WriteHold + TimeSpan.FromSeconds(1))
        {
            (rig.Plant.RelayRegister & 0x01).Should().Be(0, "the ON the client gave up on must never land after a Stop that reported success");
            await Task.Delay(25);
        }

        rig.Plant.RelayRegister.Should().Be(0);
        server.HoldWhen = null;
        server.WriteHold = TimeSpan.Zero;

        // The latch clears only now that a read-back shows every relay off.
        (await rig.Controller.ClearFault()).IsSuccessful.Should().BeTrue();
        rig.Controller.GetCurrentStatusSnapshot().IsFaultLatched.Should().BeFalse();
        rig.Session.Plant.Violations.Should().BeEmpty();
    }
}
