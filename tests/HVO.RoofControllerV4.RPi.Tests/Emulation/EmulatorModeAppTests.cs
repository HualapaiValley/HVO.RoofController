using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Emulator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace HVO.RoofControllerV4.RPi.Tests.Emulation;

/// <summary>
/// The whole controller in HAT emulator mode: the production host (controller, HAT library, health check, console, API)
/// with <c>HatEmulator:Enabled</c>, its socket client talking over TCP to an emulator session and server in the test
/// process. Nothing is mocked between the API and the emulated plant.
/// </summary>
/// <remarks>
/// Serialized: each test starts a whole host, whose blocking start and HAT polling hold pool threads that the
/// in-process emulator needs to answer within the request timeout. (The deployed emulator is a separate process.)
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class EmulatorModeAppTests
{
    private const string RoofApi = "/api/v4.0/RoofControl";
    private static readonly TimeSpan MotionTimeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task TheRoof_OpensAndCloses_ThroughTheApi_AgainstTheEmulator()
    {
        await using var rig = await EmulatorModeRig.StartAsync();
        using var client = rig.App.CreateApiClient(TestApiKeys.Operator);

        using (var open = await client.PostAsync($"{RoofApi}/Open", content: null))
        {
            open.StatusCode.Should().Be(HttpStatusCode.OK, await open.Content.ReadAsStringAsync());
        }

        var opened = await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Open && !s.IsMoving, MotionTimeout, "the roof to open");
        opened.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        rig.Plant.OpenLimitActuated.Should().BeTrue();
        await rig.WaitForPlantAsync(p => p.RelayRegister == 0 && p.VelocityMetersPerSecond == 0, "the relays to drop and the roof to stop");

        using (var close = await client.PostAsync($"{RoofApi}/Close", content: null))
        {
            close.StatusCode.Should().Be(HttpStatusCode.OK, await close.Content.ReadAsStringAsync());
        }

        var closed = await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Closed && !s.IsMoving, MotionTimeout, "the roof to close");
        closed.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
        closed.IsFaultLatched.Should().BeFalse();
        rig.Plant.ClosedLimitActuated.Should().BeTrue();
        rig.Session.Plant.Violations.Should().BeEmpty("the controller stopped at each limit before any hard stop");

        using var status = await client.GetAsync($"{RoofApi}/Status");
        var snapshot = await ApiJson.ReadAsync<RoofStatusResponse>(status);
        snapshot.HatMode.Should().Be(RoofHatMode.Emulated);
        snapshot.IsUsingPhysicalHardware.Should().BeTrue("the controller drives the HAT library's hardware path");
        snapshot.Status.Should().Be(RoofControllerStatus.Closed);
    }

    [TestMethod]
    public async Task ALinkOutageWhileMoving_StopsTheRoof_AndLatchesAFault_UntilCleared()
    {
        await using var rig = await EmulatorModeRig.StartAsync();
        using var client = rig.App.CreateApiClient(TestApiKeys.Operator);
        using (var open = await client.PostAsync($"{RoofApi}/Open", content: null))
        {
            open.StatusCode.Should().Be(HttpStatusCode.OK, await open.Content.ReadAsStringAsync());
        }

        await rig.WaitForPlantAsync(p => p.PositionMeters > 0.05 && p.VelocityMetersPerSecond > 0, "the roof to leave the closed limit");
        rig.Server.Outage = true;

        // Supervision reads the inputs and the relay register each cycle. The register misses reach their limit (2) before
        // the input misses reach theirs (3 in appsettings.json), so the stop is a relay verification failure.
        var stopped = await rig.WaitForControllerAsync(s => s.CommandedMotion == RoofMotionDirection.None && s.IsFaultLatched, MotionTimeout, "the controller to stop");
        stopped.LastStopReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        stopped.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        stopped.RelayRegisterState.Should().Be(RoofRelayRegisterState.Unverified, "the all-off writes cannot reach the HAT");
        stopped.InputsHealthy.Should().BeFalse();

        // The HAT keeps the relays it was last given: with the link down, the limit's NC contact stops the drive.
        await rig.WaitForPlantAsync(p => p.OpenLimitActuated && p.VelocityMetersPerSecond == 0, "the open limit to stop the drive");
        rig.Session.Plant.Violations.Should().BeEmpty("the hardwired limit stops the drive before the hard stop");

        rig.Server.Outage = false;

        await rig.WaitForPlantAsync(p => p.RelayRegister == 0, "the controller to drop the relays once the link is back");
        var recovered = await rig.WaitForControllerAsync(
            s => s.InputsHealthy && s.RelayRegisterState == RoofRelayRegisterState.Verified && s.IsOpenLimitActive == true,
            MotionTimeout,
            "the controller to verify the relays off and read the open limit");
        recovered.IsFaultLatched.Should().BeTrue("a latched fault waits for the operator");
        recovered.Status.Should().Be(RoofControllerStatus.Error, "the status shows the latched fault until it is cleared");
        using (var refused = await client.PostAsync($"{RoofApi}/Close", content: null))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, "motion is refused while the fault is latched");
        }

        using (var clear = await client.PostAsync($"{RoofApi}/ClearFault", content: null))
        {
            clear.StatusCode.Should().Be(HttpStatusCode.OK, await clear.Content.ReadAsStringAsync());
            var cleared = await ApiJson.ReadAsync<RoofStatusResponse>(clear);
            cleared.IsFaultLatched.Should().BeFalse();
            cleared.Status.Should().Be(RoofControllerStatus.Open);
        }

        using (var close = await client.PostAsync($"{RoofApi}/Close", content: null))
        {
            close.StatusCode.Should().Be(HttpStatusCode.OK, await close.Content.ReadAsStringAsync());
        }

        await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Closed && !s.IsMoving, MotionTimeout, "the roof to close after the clear");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task AControllerStartedBeforeTheEmulator_InitializesWithALatchedFault_UntilCleared()
    {
        // The link is down from the start, as when the controller starts first. The host retries every second.
        await using var rig = await EmulatorModeRig.StartAsync(
            new() { ["RoofControllerOptionsV4:RestartOnFailureWaitTime"] = "1" },
            startWithTheLinkDown: true);
        using var client = rig.App.CreateApiClient(TestApiKeys.Operator);

        var failed = await rig.WaitForControllerAsync(s => s.IsFaultLatched, MotionTimeout, "the first initialization to fail");
        failed.IsInitialized.Should().BeFalse();
        failed.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed, "initialization could not verify the relays off");

        rig.Server.Outage = false;

        var initialized = await rig.WaitForControllerAsync(s => s.IsInitialized, MotionTimeout, "the controller to initialize once the emulator answers");
        initialized.IsFaultLatched.Should().BeTrue("the failed attempt's fault stays latched until the operator clears it");
        initialized.LatchedFaultReason.Should().Be(RoofControllerStopReason.RelayVerificationFailed);
        initialized.Status.Should().Be(RoofControllerStatus.Error);
        initialized.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
        rig.Logs.Entries.Should().Contain(e => e.Message == $"Connected to the HAT emulator at {rig.Endpoint}");
        using (var refused = await client.PostAsync($"{RoofApi}/Open", content: null))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, "motion is refused while the fault is latched");
        }

        rig.Plant.RelayRegister.Should().Be(0);

        using (var clear = await client.PostAsync($"{RoofApi}/ClearFault", content: null))
        {
            clear.StatusCode.Should().Be(HttpStatusCode.OK, await clear.Content.ReadAsStringAsync());
            var cleared = await ApiJson.ReadAsync<RoofStatusResponse>(clear);
            cleared.IsFaultLatched.Should().BeFalse();
            cleared.Status.Should().Be(RoofControllerStatus.Closed);
        }

        using (var open = await client.PostAsync($"{RoofApi}/Open", content: null))
        {
            open.StatusCode.Should().Be(HttpStatusCode.OK, await open.Content.ReadAsStringAsync());
        }

        await rig.WaitForControllerAsync(s => s.Status == RoofControllerStatus.Open && !s.IsMoving, MotionTimeout, "the roof to open after the clear");
        rig.Session.Plant.Violations.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Health_IsDegraded_AndNamesTheEmulator()
    {
        await using var rig = await EmulatorModeRig.StartAsync();
        using var client = rig.App.CreateApiClient(TestApiKeys.Viewer);

        using var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await ApiJson.ReadElementAsync(response);
        report.GetProperty("status").GetString().Should().Be("Degraded");
        var roof = report.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "roof_controller");
        roof.GetProperty("status").GetString().Should().Be("Degraded");
        roof.GetProperty("description").GetString().Should().Be($"Roof controller is running against the HAT emulator ({rig.Endpoint}), not the physical HAT");
        roof.GetProperty("data").GetProperty("HardwareMode").GetString().Should().Be("Emulated");
        roof.GetProperty("data").GetProperty("HatEmulatorEndpoint").GetString().Should().Be(rig.Endpoint);
    }

    [TestMethod]
    public async Task TheSignInPage_CarriesTheEmulatedHatBanner()
    {
        // The sign-in page has its own layout; EmulatedHatDisplayTests covers the main layout, which serves the console.
        await using var rig = await EmulatorModeRig.StartAsync();
        using var client = rig.App.CreateApiClient();

        var login = await client.GetStringAsync("/login");

        login.Should().Contain("data-testid=\"emulated-hat-banner\"")
            .And.Contain("EMULATED HAT")
            .And.Contain($"<code>{rig.Endpoint}</code>")
            .And.Contain("The observatory roof does not move.");
    }

    [TestMethod]
    public async Task ThePhysicalHat_HasNoBannerOnTheSignInPage()
    {
        await using var host = new RoofApiTestHost();
        using var client = host.CreateApiClient();

        var login = await client.GetStringAsync("/login");

        login.Should().NotContain("emulated-hat-banner").And.NotContain("EMULATED HAT");
    }

    [TestMethod]
    public async Task TheStart_WarnsThatTheHatIsEmulated()
    {
        await using var rig = await EmulatorModeRig.StartAsync();

        rig.Logs.Entries.Should().Contain(e =>
            e.Category == HatEmulatorStartup.LoggerCategory
            && e.Level == LogLevel.Warning
            && e.Message.StartsWith($"EMULATED HAT: the roof controller is connected to the HAT emulator at {rig.Endpoint}, not the physical HAT.", StringComparison.Ordinal));
        rig.Logs.Entries.Should().Contain(e =>
            e.Category == typeof(RoofControllerServiceV4).FullName
            && e.Level == LogLevel.Warning
            && e.Message.StartsWith($"RoofControllerServiceV4 using the HAT emulator at {rig.Endpoint} for the relay HAT, not the physical HAT.", StringComparison.Ordinal));
        rig.Logs.Entries.Should().Contain(e => e.Message == $"Connected to the HAT emulator at {rig.Endpoint}");
    }

    [TestMethod]
    public async Task Telemetry_MarksTheRunAsEmulated()
    {
        // An exporter endpoint nothing listens on: telemetry is set up, and its exports fail quietly.
        await using var rig = await EmulatorModeRig.StartAsync(new() { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1" });

        var resource = rig.App.Services.GetRequiredService<TracerProvider>().GetResource();

        var attributes = resource.Attributes.ToDictionary(a => a.Key, a => a.Value);
        attributes.Should().ContainKey("hvo.roof.hat.mode").WhoseValue.Should().Be("emulated");
        attributes.Should().ContainKey("hvo.roof.hat.emulator.endpoint").WhoseValue.Should().Be(rig.Endpoint);
    }

    [TestMethod]
    [DataRow("Production")]
    [DataRow("Staging")]
    public async Task OutsideDevelopment_WithoutConsent_TheControllerDoesNotStart(string environment)
    {
        await using var app = new EmulatorModeApp(new Dictionary<string, string?>
        {
            ["HatEmulator:Enabled"] = "true",
            ["HatEmulator:Host"] = "127.0.0.1",
            ["HatEmulator:Port"] = "9"
        }, environment, new RecordingLoggerProvider());

        var start = FluentActions.Invoking(() => app.CreateApiClient());

        start.Should().Throw<InvalidOperationException>()
            .WithMessage($"HAT emulator mode (HatEmulator:Enabled) is refused in the {environment} environment.*");
    }

    /// <summary>An emulator session and register server on a free loopback port, and the controller host pointed at it.</summary>
    private sealed class EmulatorModeRig : IAsyncDisposable
    {
        private EmulatorModeRig(HatEmulatorSession session, HatEmulatorServer server, EmulatorModeApp app, RecordingLoggerProvider logs)
        {
            Session = session;
            Server = server;
            App = app;
            Logs = logs;
        }

        public HatEmulatorSession Session { get; }

        public HatEmulatorServer Server { get; }

        public EmulatorModeApp App { get; }

        public RecordingLoggerProvider Logs { get; }

        public string Endpoint => $"127.0.0.1:{Server.LocalEndPoint.Port}";

        public HatEmulatorStatus Plant => Session.GetStatus();

        public IRoofControllerServiceV4 Controller => App.Services.GetRequiredService<IRoofControllerServiceV4>();

        /// <summary>
        /// Starts the emulator (40 cm of travel at twice real time, so a full run takes about 2 s), then the controller
        /// with the documented wiring (normally open limits, not ignored), and waits for it to initialize. With
        /// <paramref name="startWithTheLinkDown"/>, the link is in an outage when the controller starts, and the rig
        /// does not wait.
        /// </summary>
        public static async Task<EmulatorModeRig> StartAsync(Dictionary<string, string?>? settings = null, bool startWithTheLinkDown = false)
        {
            var plant = new RoofPlantOptions();
            var session = new HatEmulatorSession(new HatEmulatorSessionOptions
            {
                Plant = plant with { Mechanics = plant.Mechanics with { TravelMeters = 0.4 } },
                TimeScale = 2
            });
            var server = new HatEmulatorServer(session.CurrentClient, new IPEndPoint(IPAddress.Loopback, 0)) { Outage = startWithTheLinkDown };
            server.Start();

            var values = new Dictionary<string, string?>
            {
                ["HatEmulator:Enabled"] = "true",
                ["HatEmulator:Host"] = "127.0.0.1",
                ["HatEmulator:Port"] = server.LocalEndPoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["RoofControllerOptionsV4:UseNormallyClosedLimitSwitches"] = "false",
                ["RoofControllerOptionsV4:IgnorePhysicalLimitSwitches"] = "false"
            };
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                values[key] = value;
            }

            var logs = new RecordingLoggerProvider();
            var rig = new EmulatorModeRig(session, server, new EmulatorModeApp(values, "Development", logs), logs);
            try
            {
                rig.App.CreateApiClient().Dispose();
                if (startWithTheLinkDown)
                {
                    return rig;
                }

                await rig.WaitForControllerAsync(s => s.IsInitialized && s.Status == RoofControllerStatus.Closed, TimeSpan.FromSeconds(20), "the controller to initialize");
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        public async Task<RoofStatusResponse> WaitForControllerAsync(Func<RoofStatusResponse, bool> condition, TimeSpan timeout, string what)
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var snapshot = Controller.GetCurrentStatusSnapshot();
                if (condition(snapshot))
                {
                    return snapshot;
                }

                if (clock.Elapsed > timeout)
                {
                    throw new AssertFailedException($"Timed out waiting for {what}. Controller: {snapshot}. Plant: {Plant}.");
                }

                await Task.Delay(20);
            }
        }

        public async Task WaitForPlantAsync(Func<HatEmulatorStatus, bool> condition, string what)
        {
            var clock = Stopwatch.StartNew();
            while (!condition(Plant))
            {
                if (clock.Elapsed > MotionTimeout)
                {
                    throw new AssertFailedException($"Timed out waiting for {what}. Plant: {Plant}. Controller: {Controller.GetCurrentStatusSnapshot()}.");
                }

                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            // The controller first, so its shutdown stop reaches the emulator.
            await App.DisposeAsync();
            await Server.DisposeAsync();
            Session.Dispose();
        }
    }

    /// <summary>The production host with the settings given and the test API keys; nothing is replaced.</summary>
    private sealed class EmulatorModeApp(Dictionary<string, string?> settings, string environment, RecordingLoggerProvider logs) : WebApplicationFactory<Program>
    {
        public HttpClient CreateApiClient(string? apiKey = null)
        {
            var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
            if (apiKey is not null)
            {
                client.DefaultRequestHeaders.Add(RoofControllerApiContract.ApiKeyHeaderName, apiKey);
            }

            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var values = RoofApiTestHost.DefaultKeySettings();
            values["BlueIris:BaseUrl"] = string.Empty;
            foreach (var (key, value) in settings)
            {
                values[key] = value;
            }

            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values));

            // Program reads the exporter endpoint while it adds services, before the configuration above applies.
            if (values.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out var otlpEndpoint))
            {
                builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
            }
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }
    }
}
