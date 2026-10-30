using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The kiosk's hook for a person's recent roof commands (#206): nothing while it is locked, and once someone unlocks it,
/// that person's own commands only, an admin's PIN included.
/// </summary>
[TestClass]
public sealed class KioskHistoryTests
{
    [TestMethod]
    public async Task ALockedKiosk_ReadsNoOnesHistory()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.Console.StopAsync();

        (await harness.Console.ReadRecentHistoryAsync()).Should().BeNull("nobody has unlocked it");
    }

    [TestMethod]
    [DataRow(KioskHarness.Operator)]
    [DataRow(KioskHarness.Admin)]
    public async Task AnUnlockedKiosk_ReadsThePersonsOwnRecentCommands(string person)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        using (var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin)))
        {
            await admin.StopAsync();
        }

        await harness.UnlockAsync(person);
        await harness.Console.StopAsync();
        await harness.Console.StopAsync();

        var page = await harness.Console.ReadRecentHistoryAsync(take: 1);

        page.Should().NotBeNull();
        page!.TotalCount.Should().Be(2, "the admin key's Stop is not the person's");
        page.Entries.Should().ContainSingle().Which.Should().Match<RoofCommandHistoryEntry>(entry =>
            entry.User == person && entry.Command == RoofHistoryCommand.Stop && entry.CredentialKind == RoofCredentialKind.Pin
            && entry.Device == "test-kiosk");
    }
}
