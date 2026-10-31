using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Tests.Emulation;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// The whole safety path with the controller's default interlock (Warn, #215) and ASCOM Alpaca sources (#220, phase 2):
/// the <c>RoofSafety</c> settings name Alpaca devices, which the controller reads from the HAT emulator's Alpaca devices in
/// process, and the conditions they give reach the status and decide what Open needs. Close and Stop are never gated, the
/// conditions never move the roof, and nothing the controller logs, at any level, names a device's address.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SafetyAlpacaApiTests
{
    private const string RoofPath = "/api/v4.0/RoofControl";
    private const string SafetyPath = "/api/v4.0/Safety";

    /// <summary>A made-up address: the controller's Alpaca client is routed to the emulator in process.</summary>
    private const string Address = "http://alpaca.example.test:11111";

    private EmulatorHost? _emulator;
    private RoofApiTestHost? _host;

    [TestCleanup]
    public async Task TestCleanup()
    {
        _host?.Dispose();
        if (_emulator is not null)
        {
            await _emulator.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task WithTheMountParkedAndTheWeatherSafe_OpenNeedsNoOverride()
    {
        var host = Host();
        using var client = host.CreateApiClient(TestApiKeys.Operator);

        var safety = await WaitForAsync(client, RoofSafetyState.Safe);
        var response = await client.PostAsync($"{RoofPath}/Open", null);

        safety.InterlockMode.Should().Be(RoofSafetyInterlockMode.Warn);
        safety.WeatherState.Should().Be(RoofSafetyState.Safe);
        safety.Sources.Select(source => (source.Name, source.Kind, source.State, source.Reason, source.Counts)).Should().Equal(
            ("Main", RoofSafetySourceKind.TelescopePark, RoofSafetyState.Safe, "Telescope 'Main' is parked", true),
            ("Cloud sensor", RoofSafetySourceKind.Weather, RoofSafetyState.Safe, "Weather monitor 'Cloud sensor' reports safe weather", true));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.RoofService.Verify(service => service.Open(), Times.Once());
    }

    [TestMethod]
    public async Task AnUnparkedMount_AsksOpenForTheOverride_WhichOpens_AndCloseAndStopAreNeverGated()
    {
        var host = Host();
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Safe);

        _emulator!.Alpaca.SetTelescope(0, parked: false);
        var safety = await WaitForAsync(client, RoofSafetyState.Unsafe);
        var refused = await client.PostAsync($"{RoofPath}/Open", null);
        var close = await client.PostAsync($"{RoofPath}/Close", null);
        var stop = await client.PostAsync($"{RoofPath}/Stop", null);
        var opened = await client.PostAsync($"{RoofPath}/Open?overrideSafety=true", null);

        safety.Reasons.Should().Equal("Telescope 'Main' is not parked");
        safety.WeatherState.Should().Be(RoofSafetyState.Safe, "the weather alone leaves the mount out");
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ApiJson.ReadElementAsync(refused);
        problem.GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.SafetyOverrideRequired));
        problem.GetProperty("detail").GetString().Should().Contain("Telescope 'Main' is not parked");
        close.StatusCode.Should().Be(HttpStatusCode.OK, "closing is never gated by the conditions");
        stop.StatusCode.Should().Be(HttpStatusCode.OK, "Stop is always available");
        opened.StatusCode.Should().Be(HttpStatusCode.OK, "anyone who may open may override");
        host.RoofService.Verify(service => service.Open(), Times.Once());
        host.RoofService.Verify(service => service.Close(), Times.Once());
    }

    [TestMethod]
    public async Task AMountThatDoesNotBlockTheRoof_NeverAsksForTheOverride()
    {
        var host = Host(blocksRoof: false);
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Safe);

        _emulator!.Alpaca.SetTelescope(0, parked: false);
        var safety = await WaitForAsync(client, RoofSafetyState.Safe, until: current => current.Sources[0].State == RoofSafetyState.Unsafe);
        var response = await client.PostAsync($"{RoofPath}/Open", null);

        safety.Sources[0].Counts.Should().BeFalse();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task UnsafeWeather_NeverMovesTheRoof()
    {
        var host = Host();
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Safe);

        _emulator!.Alpaca.SetSafetyMonitor(0, safe: false);
        var safety = await WaitForAsync(client, RoofSafetyState.Unsafe);
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        safety.WeatherState.Should().Be(RoofSafetyState.Unsafe);
        safety.WeatherReasons.Should().Equal("Weather monitor 'Cloud sensor' reports unsafe weather");
        host.RoofService.Verify(service => service.Close(), Times.Never(), "unsafe weather does not close the roof by itself");
        host.RoofService.Verify(service => service.Open(), Times.Never());
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    public async Task DevicesThatStopAnswering_GoUnknown_WithWhy_AndCountAsUnsafe()
    {
        var host = Host();
        using var client = host.CreateApiClient(TestApiKeys.Admin);
        await WaitForAsync(client, RoofSafetyState.Safe);

        _emulator!.Alpaca.SetSafetyMonitor(0, connected: false);
        _emulator.Alpaca.SetTelescope(0, behaviour: EmulatedAlpacaBehaviour.Hangs);
        var safety = await WaitForAsync(client, RoofSafetyState.Unknown, until: current => current.Reasons.Count == 2);
        var refused = await client.PostAsync($"{RoofPath}/Open", null);

        safety.Reasons.Should().Equal(
            "Telescope 'Main': the last reading (parked) is older than 5 s; the last read failed (no answer within 0.5 s)",
            "Weather monitor 'Cloud sensor': the last reading (safe) is older than 5 s; the last read failed (the device is not connected)");
        safety.WeatherState.Should().Be(RoofSafetyState.Unknown);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiJson.ReadElementAsync(refused)).GetProperty("code").GetString().Should().Be(nameof(RoofControllerErrorCode.SafetyOverrideRequired),
            "Unknown counts as unsafe, and asks for the override");
        host.RoofService.Verify(service => service.Open(), Times.Never());
    }

    [TestMethod]
    public async Task AMountAlone_IsUnknown_ForWantOfARequiredWeatherSource()
    {
        var host = Host(monitor: false);
        using var client = host.CreateApiClient(TestApiKeys.Operator);

        var safety = await WaitForAsync(client, RoofSafetyState.Unknown, until: current => current.Sources[0].State == RoofSafetyState.Safe);
        var refused = await client.PostAsync($"{RoofPath}/Open", null);

        safety.Reasons.Should().Equal(RoofSafetyStatus.NoRequiredWeatherSourceReason);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, "a parked mount alone is not enough to open (#220, Q11)");
    }

    [TestMethod]
    public void AConfiguredDeviceTheControllerCannotUse_StopsTheStart_WithoutItsAddress()
    {
        var host = Host(url: "ftp://alpaca.example.test:21");

        var start = () => host.CreateApiClient(TestApiKeys.Viewer);

        var failure = start.Should().Throw<Exception>().Which;
        var text = failure.ToString();
        text.Should().Contain("RoofSafety:Telescopes:0:Url");
        text.Should().NotContain("alpaca.example.test");
    }

    [TestMethod]
    [DataRow("RoofSafety:Telescopes:0:BlocksRoof", "tru")]
    [DataRow("RoofSafety:Telescopes:0:DeviceNumber", "not-a-number")]
    [DataRow("RoofSafety:Telescopes:0:MaxReadingAge", "soon")]
    [DataRow("RoofSafety:Telescopes:0:DeviceNumer", "1")]
    [DataRow("RoofSafety:WeatherSources:0:Requirement", "Requred")]
    [DataRow("RoofSafety:WeatherSources:1:Requirement", "Requred")]
    // Review F4 (#227): the failure names the key, never the value, which could be a device's address put in the wrong
    // setting.
    [DataRow("RoofSafety:Telescopes:0:DeviceNumber", Address)]
    [DataRow("RoofSafety:Telescopes:0:BlocksRoof", Address)]
    [DataRow("RoofSafety:Telescopes:0:MaxReadingAge", Address)]
    [DataRow("RoofSafety:WeatherSources:1:Requirement", Address)]
    [DataRow("RoofSafety:PollInterval", Address)]
    [DataRow("RoofSafety:InterlockMode", Address)]
    public void ADeviceSettingThatCannotBeRead_StopsTheStart_NeverDropsTheDevice(string key, string value)
    {
        // Review F1 (#227): the configuration binder skips a whole list entry whose value does not convert, so a typo
        // could remove a blocking mount or a required weather source and leave the rest Safe.
        var logs = new RecordingLoggerProvider();
        var extra = new Dictionary<string, string?> { [key] = value };
        if (key.StartsWith("RoofSafety:WeatherSources:1:", StringComparison.Ordinal))
        {
            extra["RoofSafety:WeatherSources:1:Name"] = "Second sensor";
            extra["RoofSafety:WeatherSources:1:Url"] = Address;
        }

        var host = Host(extra: extra, logs: logs);

        var start = () => host.CreateApiClient(TestApiKeys.Viewer);

        var failure = start.Should().Throw<Exception>().Which;
        var text = failure.ToString();
        text.Should().Contain(key.EndsWith("DeviceNumer", StringComparison.Ordinal) ? "DeviceNumer" : key);
        text.Should().NotContain("alpaca.example.test", "neither the failure nor any exception inside it repeats the value");
        logs.Entries.Should().NotContain(entry => entry.Message.Contains("alpaca.example.test", StringComparison.Ordinal)
            || (entry.Exception != null && entry.Exception.ToString().Contains("alpaca.example.test", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NothingTheControllerLogs_NamesADevicesAddress_EvenAtDebug()
    {
        var logs = new RecordingLoggerProvider();
        var host = Host(logs: logs, unreachable: true);
        using var client = host.CreateApiClient(TestApiKeys.Operator);
        await WaitForAsync(client, RoofSafetyState.Unknown, until: current => current.Reasons.Count == 2 && current.Reasons.All(reason => reason.Contains("cannot be read", StringComparison.Ordinal)));
        await client.PostAsync($"{RoofPath}/Open?overrideSafety=true", null);

        logs.Entries.Should().Contain(entry => entry.Message.Contains("could not be read (the Alpaca server could not be reached", StringComparison.Ordinal));
        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Debug && entry.Message.Contains("read failure", StringComparison.Ordinal),
            "the failure is written at Debug too, and must carry no address there either");
        logs.Entries.Should().NotContain(entry => entry.Message.Contains("alpaca.example.test", StringComparison.Ordinal)
            || (entry.Exception != null && entry.Exception.ToString().Contains("alpaca.example.test", StringComparison.Ordinal)));
    }

    private RoofApiTestHost Host(
        bool monitor = true,
        bool blocksRoof = true,
        string url = Address,
        RecordingLoggerProvider? logs = null,
        bool unreachable = false,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        _emulator = new EmulatorHost();
        var emulator = _emulator;
        var settings = new Dictionary<string, string?>
        {
            // The controller's default interlock, not the test host's Off.
            [RoofApiTestHost.SafetyInterlockModeKey] = null,
            ["RoofSafety:PollInterval"] = "00:00:01",
            ["RoofSafety:ReadTimeout"] = "00:00:00.5",
            ["RoofSafety:MaxReadingAge"] = "00:00:05",
            ["RoofSafety:Telescopes:0:Name"] = "Main",
            ["RoofSafety:Telescopes:0:Url"] = url,
            ["RoofSafety:Telescopes:0:DeviceNumber"] = "0",
            ["RoofSafety:Telescopes:0:BlocksRoof"] = blocksRoof ? "true" : "false",
            ["Logging:LogLevel:HVO.RoofControllerV4.RPi.Safety"] = "Debug"
        };
        if (monitor)
        {
            settings["RoofSafety:WeatherSources:0:Name"] = "Cloud sensor";
            settings["RoofSafety:WeatherSources:0:Url"] = url;
            settings["RoofSafety:WeatherSources:0:Requirement"] = "Required";
        }

        foreach (var (key, value) in extra ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        _host = new RoofApiTestHost(settings: settings, configureServices: services =>
        {
            // The emulator in process, or (unreachable) a resolver that finds no host, so the client's own failure, which
            // names the host and port, is what the adapter must not pass on.
            services.AddHttpClient(AlpacaDevice.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => unreachable
                ? new SocketsHttpHandler { ConnectCallback = (context, _) => throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound) }
                : emulator.Server.CreateHandler());
            if (logs is not null)
            {
                services.AddSingleton<ILoggerProvider>(logs);
            }
        });
        return _host;
    }

    private static async Task<RoofSafetyStatus> WaitForAsync(HttpClient client, RoofSafetyState state, Func<RoofSafetyStatus, bool>? until = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        RoofSafetyStatus? safety = null;
        while (DateTime.UtcNow < deadline)
        {
            safety = await ApiJson.ReadAsync<RoofSafetyStatus>(await client.GetAsync(SafetyPath));
            if (safety.State == state && (until?.Invoke(safety) ?? true))
            {
                return safety;
            }

            await Task.Delay(50);
        }

        throw new AssertFailedException($"The safety state did not become {state}: {safety?.State} ({string.Join("; ", safety?.Reasons ?? [])}).");
    }
}
