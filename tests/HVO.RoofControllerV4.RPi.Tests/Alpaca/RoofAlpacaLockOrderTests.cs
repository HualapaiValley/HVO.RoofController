using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The lock order between the Alpaca command state's gate and the controller's lock (#220, PR #230 review F1): an admission
/// holds the controller's lock and then takes the gate, so nothing may hold the gate while it waits for the controller's
/// lock. A ticket refused while a start holds the controller's lock must leave the gate free, or that admission, Stop and
/// every protective stop would wait for each other for good.
/// </summary>
[TestClass]
public sealed class RoofAlpacaLockOrderTests
{
    [TestMethod]
    [DataRow("unknown")]
    [DataRow("mismatch")]
    [DataRow("expired")]
    public async Task ATicketRefusedWhileAStartHoldsTheControllerLock_LeavesTheGateFree(string kind)
    {
        var time = new ManualTimeProvider();
        await using var rig = (await AlpacaCommandRig.StartAsync(time: time)).Ready();
        var ticket = rig.Ticket();
        var request = kind switch
        {
            "unknown" => new RoofAlpacaCommandRequest("never-issued-ticket", ticket.InstanceId, ticket.Generation),
            "mismatch" => new RoofAlpacaCommandRequest(ticket.TicketId, ticket.InstanceId, ticket.Generation + 7),
            _ => new RoofAlpacaCommandRequest(ticket.TicketId, ticket.InstanceId, ticket.Generation)
        };
        if (kind == "expired")
        {
            time.Advance(RoofAlpacaContract.TicketLifetime + TimeSpan.FromSeconds(1));
        }

        Task<RoofAlpacaCommandOutcome>? refused = null;
        var gateFree = false;
        rig.Controller.BeforeEnergizeHook = () =>
        {
            rig.Controller.BeforeEnergizeHook = null;

            // The refusal arrives while this start holds the controller's lock; then something needs the gate.
            refused = Task.Run(() => rig.Commands.CloseAsync(request, rig.Commander, AlpacaCommandRig.Client));
            Thread.Sleep(200);
            gateFree = Task.Run(() => rig.Commands.GetStatus()).Wait(TimeSpan.FromSeconds(2));
        };

        var close = await Task.Run(() => rig.Controller.Close());

        gateFree.Should().BeTrue("a refusal never holds the gate while it waits for the controller's lock");
        close.IsSuccessful.Should().BeTrue();
        (await refused!.WaitAsync(TimeSpan.FromSeconds(10))).Rule().Should().Be(kind switch
        {
            "unknown" => "TicketUnknown",
            "mismatch" => "TicketMismatch",
            _ => "TicketExpired"
        });
    }
}
