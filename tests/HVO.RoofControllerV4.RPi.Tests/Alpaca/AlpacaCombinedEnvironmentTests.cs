using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The controller and the Alpaca server share one container's environment (#220, §15 W4c, and #229): each starts with
/// the other's section present and ignores it, the controller reading <c>RoofController:Alpaca</c> and
/// <c>RoofController:Lease</c>, the server <c>Alpaca</c>. And the motion hook the fencing tests use is never set in the
/// production host, nor anywhere in the shipped code (§16 Y5).
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AlpacaCombinedEnvironmentTests
{
    private const string ExternalKey = "test-combined-external-key-not-a-real-secret";
    private const string ServerKey = "test-combined-server-key-not-a-real-secret-0";

    /// <summary>Both processes' settings, as one container's environment would carry them.</summary>
    private static Dictionary<string, string?> Combined() => new()
    {
        // The controller's.
        ["RoofController:Alpaca:AllowCommands"] = "CloseOnly",
        ["RoofController:Alpaca:ServerAddresses:0"] = "127.0.0.1",
        ["RoofController:Alpaca:OverrideWindowMinutes"] = "7",
        ["RoofController:Alpaca:ScopeRereadBudgetMs"] = "900",
        ["RoofController:Lease:OperatorLeaseIdleMinutes"] = "12",
        ["RoofSafety:OwnAlpacaUniqueIds:0"] = "6f1f6b2e-1d0c-4c55-9b3a-2d6c0f9e1a11",
        ["RoofControllerSecurity:ApiKeys:4:Name"] = "alpaca-external",
        ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofAlpacaContract.CommanderRole,
        ["RoofControllerSecurity:ApiKeys:4:Key"] = ExternalKey,
        ["RoofControllerSecurity:ApiKeys:5:Name"] = "alpaca-server",
        ["RoofControllerSecurity:ApiKeys:5:Role"] = RoofAlpacaContract.ServerRole,
        ["RoofControllerSecurity:ApiKeys:5:Key"] = ServerKey,
        // The Alpaca server's.
        ["Alpaca:Enabled"] = "true",
        ["Alpaca:Port"] = "11111",
        ["Alpaca:ListenAddresses:0"] = "127.0.0.1",
        ["Alpaca:AllowedNetworks:0"] = "192.168.1.0/24", // AlpacaTestHost.ClientAddress's network
        ["Alpaca:RequestsPerSecondPerAddress"] = "50",
        ["Alpaca:MaxConnectedClients"] = "8",
        ["Alpaca:Discovery:Enabled"] = "false",
        ["Alpaca:Location"] = "Test observatory",
    };

    [TestMethod]
    public async Task BothProcesses_StartWithTheCombinedEnvironment_EachReadingOnlyItsOwnSection()
    {
        var combined = Combined();
        await using var rig = await EmulatedRoofRig.StartAsync(new EmulatedRoofRigOptions { Settings = combined, RemoteAddress = IPAddress.Loopback });
        await using var alpaca = await AlpacaTestHost.StartAsync(() => rig.App.Server, combined, ServerKey);

        using var viewer = rig.CreateApiClient(TestApiKeys.Viewer);
        var status = await ApiJson.ReadAsync<RoofStatusResponse>(await viewer.GetAsync("api/v4.0/RoofControl/Status"));
        status.Alpaca.Should().NotBeNull();
        status.Alpaca!.Mode.Should().Be(RoofAlpacaCommandMode.CloseOnly);
        status.Alpaca.OverrideWindowMinutes.Should().Be(7);
        status.Alpaca.RereadBudgetMilliseconds.Should().Be(900);

        await alpaca.ConnectAsync(7);
        var shutter = await alpaca.GetAsync("shutterstatus", "ClientID=7&ClientTransactionID=1");
        shutter.GetProperty("ErrorNumber").GetInt32().Should().Be(0, shutter.ToString());
        shutter.GetProperty("Value").GetInt32().Should().Be(1, "Closed, read through the server's own section's settings");

        // The roof starts Closed: a Close with the External key through the server is a no-op success, after the hold.
        using var admin = rig.CreateApiClient(TestApiKeys.Admin);
        (await admin.PostAsJsonAsync("api/v4.0/Identity/Users", new RoofUserCreateRequest { Name = "ada", Role = RoofControllerApiContract.OperatorRole, Password = HVO.RoofControllerV4.RPi.Tests.Security.TestSecrets.Password }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        using var anonymous = rig.CreateApiClient();
        var session = await ApiJson.ReadAsync<RoofSessionResponse>(await anonymous.PostAsJsonAsync("api/v4.0/Auth/Session", new RoofSignInRequest { Name = "ada", Password = HVO.RoofControllerV4.RPi.Tests.Security.TestSecrets.Password }));
        using var person = rig.CreateApiClient();
        person.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(RoofIdentityContract.BearerScheme, session.Token);
        (await person.DeleteAsync("api/v4.0/alpaca/hold")).StatusCode.Should().Be(HttpStatusCode.OK);

        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ExternalKey}:{ExternalKey}"));
        var close = await alpaca.PutAsync("closeshutter", "ClientID=7&ClientTransactionID=2", new Dictionary<string, string> { ["Authorization"] = basic });
        close.GetProperty("ErrorNumber").GetInt32().Should().Be(0, close.ToString());
        rig.Plant.RelayRegister.Should().Be(0, "already closed: nothing is switched");
    }

    [TestMethod]
    public async Task TheProductionHost_SetsNoMotionHook()
    {
        await using var rig = await EmulatedRoofRig.StartAsync();

        rig.Controller.Should().BeOfType<RoofControllerServiceV4>()
            .Which.BeforeEnergizeHook.Should().BeNull("the hook is a test seam, never set in production");
        rig.App.Services.GetRequiredService<HVO.RoofControllerV4.RPi.Alpaca.RoofAlpacaCommands>().BeforeGrantInstallHook
            .Should().BeNull("the grant hook is a test seam too (PR #232 review S1)");
    }

    [TestMethod]
    public void TheShippedCode_NeverSetsTheGrantHook()
    {
        var src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ProductionOptions.AppSettingsPath)!, ".."));
        var uses = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => File.ReadAllLines(path).Select((line, index) => (Path: Path.GetFileName(path), Line: index + 1, Text: line)))
            .Where(line => line.Text.Contains("BeforeGrantInstallHook", StringComparison.Ordinal))
            .ToArray();

        uses.Should().NotBeEmpty("the scan must find the declaration");
        uses.Should().OnlyContain(line => line.Path == "RoofAlpacaCommands.cs", "the hook is declared and invoked there, and set only by tests");
        uses.Where(line => line.Text.Contains("BeforeGrantInstallHook =", StringComparison.Ordinal) || line.Text.Contains("BeforeGrantInstallHook=", StringComparison.Ordinal))
            .Should().BeEmpty("no shipped code sets it");
    }

    [TestMethod]
    public void TheShippedCode_NeverSetsTheMotionHook()
    {
        var src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ProductionOptions.AppSettingsPath)!, ".."));
        var uses = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => File.ReadAllLines(path).Select((line, index) => (Path: Path.GetFileName(path), Line: index + 1, Text: line)))
            .Where(line => line.Text.Contains("BeforeEnergizeHook", StringComparison.Ordinal))
            .ToArray();

        uses.Should().NotBeEmpty("the scan must find the declaration");
        uses.Should().OnlyContain(line => line.Path == "RoofControllerServiceV4.Motion.cs",
            "the hook is declared and invoked there, and set only by tests");
        uses.Where(line => line.Text.Contains("BeforeEnergizeHook =", StringComparison.Ordinal) || line.Text.Contains("BeforeEnergizeHook=", StringComparison.Ordinal))
            .Should().BeEmpty("no shipped code sets it");
    }
}
