using System.Text.Json;
using FluentAssertions;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

[TestClass]
public sealed class AlpacaServerStatusTests
{
    [TestMethod]
    public async Task Preflight_UsesOnlyTheAlpacaProgramsConfiguration_NotAControllersWorkingDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-config-isolation-").FullName;
        try
        {
            // A controller's configuration in the caller's working directory must not turn on the Alpaca program.
            File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{\"Alpaca\":{\"Enabled\":true}}");
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(HVO.RoofControllerV4.Alpaca.Program).Assembly.Location);
            start.ArgumentList.Add("--validate-settings");
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("Alpaca__", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(name);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(output, error);
            process.ExitCode.Should().Be(0, "the server is off unless its own environment or arguments enable it");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void DeviceIdentities_ArePublicMetadataWithOnlyOwnerWritePermission()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-identity-modes-").FullName;
        try
        {
            HVO.RoofControllerV4.Alpaca.Devices.AlpacaDeviceIds.Load(directory);
            File.GetUnixFileMode(Path.Combine(directory, "alpaca-devices.json")).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                "container root preflight has no DAC_OVERRIDE; the GUIDs are public and only the Alpaca owner may change them");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void Preflight_ValidatesEnabledSettingsWithoutCreatingDataOrListening_AndRejectsInvalidIdentities()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-preflight-").FullName;
        var keyPath = Path.Combine(directory, "key");
        var data = Path.Combine(directory, "data");
        try
        {
            HVO.RoofControllerV4.Alpaca.Program.Main(
                ["--validate-settings", "--Alpaca:Enabled=true", "--Alpaca:ListenAddresses:0=127.0.0.1", "--Alpaca:ControllerApiKeyFile=" + keyPath])
                .Should().Be(2, "preflight must validate enabled settings, even when the key is missing");
            File.WriteAllText(keyPath, Controllers.TestApiKeys.Viewer);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string[] args = ["--validate-settings", "--Alpaca:Enabled=true", "--Alpaca:ListenAddresses:0=127.0.0.1",
                "--Alpaca:ControllerApiKeyFile=" + keyPath, "--Alpaca:DataPath=" + data];
            HVO.RoofControllerV4.Alpaca.Program.Main(args).Should().Be(0);
            Directory.Exists(data).Should().BeFalse("preflight must not create identities or a lock file");
            Directory.CreateDirectory(data);
            var identity = Path.Combine(data, "alpaca-devices.json");
            File.WriteAllText(identity, "{invalid");
            HVO.RoofControllerV4.Alpaca.Program.Main(args).Should().Be(2);
            File.ReadAllText(identity).Should().Be("{invalid", "preflight must preserve an unreadable identity file");
            Directory.EnumerateFiles(data).Should().ContainSingle().Which.Should().Be(identity);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task EnabledServer_PublishesOnlyItsPortIdentitiesAndCounts_AndRemovesTheHeartbeatOnStop()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-heartbeat-").FullName;
        var path = Path.Combine(directory, "status.json");
        try
        {
            await using (var host = await AlpacaTestHost.StartAsync(controller: null,
                settings: new Dictionary<string, string?>
                {
                    ["Alpaca:StatusFilePath"] = path,
                    ["Alpaca:Discovery:AdvertisedPort"] = "47677"
                }, waitForStatus: false))
            {
                await host.ConnectAsync(1);
                await host.PutDeviceAsync("safetymonitor/0", "connected", "Connected=true&ClientID=1");
                JsonElement heartbeat = default;
                var deadline = DateTime.UtcNow.AddSeconds(5);
                do
                {
                    if (File.Exists(path))
                    {
                        heartbeat = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement.Clone();
                        if (heartbeat.GetProperty("DomeConnectedClients").GetInt32() == 1
                            && heartbeat.GetProperty("SafetyMonitorConnectedClients").GetInt32() == 1) break;
                    }
                    await Task.Delay(20);
                } while (DateTime.UtcNow < deadline);

                File.Exists(path).Should().BeTrue("the supervised server reports its own state");
                if (!OperatingSystem.IsWindows()) File.GetUnixFileMode(path).Should().Be(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    "the root controller has no DAC_OVERRIDE and reads only this public metadata");
                heartbeat.GetProperty("Port").GetInt32().Should().Be(47677);
                Guid.TryParse(heartbeat.GetProperty("DomeUniqueId").GetString(), out _).Should().BeTrue();
                Guid.TryParse(heartbeat.GetProperty("SafetyMonitorUniqueId").GetString(), out _).Should().BeTrue();
                heartbeat.GetProperty("DomeConnectedClients").GetInt32().Should().Be(1);
                heartbeat.GetProperty("SafetyMonitorConnectedClients").GetInt32().Should().Be(1);
                heartbeat.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
                    "GeneratedAtUtc", "Port", "DomeUniqueId", "SafetyMonitorUniqueId", "DomeConnectedClients", "SafetyMonitorConnectedClients");
            }
            File.Exists(path).Should().BeFalse("a stopped server must not leave a live-looking heartbeat");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
