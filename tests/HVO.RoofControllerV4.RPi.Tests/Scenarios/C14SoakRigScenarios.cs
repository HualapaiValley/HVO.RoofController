using FluentAssertions;
using HVO.RoofControllerV4.RPi.Storage;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Scenarios;

/// <summary>
/// The rig C14 soaks (<see cref="C14SoakScenarios.RigOptions"/>), checked in seconds rather than hours: whatever it keeps
/// the latest entries of fills during the warm-up, so a bounded record growing toward its limit is never read as a leak
/// (#220, PR #232).
/// </summary>
[TestClass]
[TestCategory(Scenario.Category)]
public sealed class C14SoakRigScenarios
{
    [TestMethod]
    public async Task TheSoaksCommandHistory_StopsGrowingAtItsLimit_WithinTheFirstCycles()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(C14SoakScenarios.RigOptions([]));
        using var operatorClient = rig.CreateApiClient(TestApiKeys.Operator);
        var history = rig.App.Services.GetRequiredService<RoofCommandHistory>();

        history.MaximumEntries.Should().Be(C14SoakScenarios.CommandHistoryEntries);
        for (var command = 0; command < C14SoakScenarios.CommandHistoryEntries + 20; command++)
        {
            await operatorClient.AcceptedAsync("Stop");
        }

        history.Count.Should().Be(C14SoakScenarios.CommandHistoryEntries,
            "a cycle records four commands, so the history is full 25 cycles in, well inside the soak's warm-up");
    }
}
