using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Camera;
using HVO.RoofControllerV4.Simulation.Emulator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>What an <see cref="EmulatedRoofRig"/> starts.</summary>
internal sealed record EmulatedRoofRigOptions
{
    /// <summary>
    /// The host environment. Development uses appsettings.Development.json; Production uses the production roof
    /// settings with the consent the compose <c>emulator</c> profile gives (<c>HatEmulator:AllowOutsideDevelopment</c>)
    /// and plain HTTP, as that profile does.
    /// </summary>
    public string Environment { get; init; } = "Development";

    /// <summary>Distance between the limits' operating points, metres (40 cm: 4 s of travel at real time).</summary>
    public double TravelMeters { get; init; } = 0.4;

    /// <summary>How many times as fast as real time the roof runs. The controller's timing is not scaled.</summary>
    public double TimeScale { get; init; } = 2;

    /// <summary>Changes to the documented installation (drive settings, wiring, start position) before the plant starts.</summary>
    public Func<RoofPlantOptions, RoofPlantOptions>? Plant { get; init; }

    /// <summary>Settings for the controller host, applied last.</summary>
    public IReadOnlyDictionary<string, string?>? Settings { get; init; }

    /// <summary>The register port is in an outage when the controller starts, and the rig does not wait for it to initialize.</summary>
    public bool StartWithTheLinkDown { get; init; }

    /// <summary>
    /// The status the rig waits for once the controller has initialized: Closed for the documented installation, which
    /// starts at the closed limit; null to wait only for initialization (a wiring or setting that reads the limits or
    /// the drive differently).
    /// </summary>
    public RoofControllerStatus? InitialStatus { get; init; } = RoofControllerStatus.Closed;

    /// <summary>Serve the controller on a real Kestrel port on loopback (browser tests), not the in-memory test server.</summary>
    public bool Kestrel { get; init; }

    /// <summary>Runs on the plant after it is built and before the controller starts (a relay a dead controller left on).</summary>
    public Action<HatEmulatorSession>? BeforeTheControllerStarts { get; init; }

    /// <summary>
    /// Serve an emulated MJPEG camera of the plant on loopback (<see cref="EmulatedRoofRig.Camera"/>) and point the
    /// controller's camera proxy at it, without credentials. Otherwise the proxy is not configured (503).
    /// </summary>
    public bool Camera { get; init; }

    /// <summary>
    /// Keep only this many of the latest log entries in <see cref="EmulatedRoofRig.Logs"/> (and every Warning or above),
    /// for a run long enough that recording every entry would grow the heap it measures (the soak). Null keeps all.
    /// </summary>
    public int? LogCapacity { get; init; }
}

/// <summary>
/// The whole controller in HAT emulator mode: the production host (controller, HAT library, health check, console, API)
/// with <c>HatEmulator:Enabled</c>, its socket client talking over TCP to an emulator session and register server in
/// the test process. Nothing is mocked between the API and the emulated plant.
/// </summary>
/// <remarks>
/// Test classes that use it are <c>[DoNotParallelize]</c>: each rig starts a whole host, whose blocking start and HAT
/// polling hold pool threads that the in-process emulator needs to answer within the request timeout. (The deployed
/// emulator is a separate process.)
/// </remarks>
internal sealed class EmulatedRoofRig : IAsyncDisposable
{
    public static readonly TimeSpan MotionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The fewest pool worker threads a test process with a rig starts with.</summary>
    private const int MinWorkerThreads = 32;

    static EmulatedRoofRig()
    {
        // The in-process emulator answers on pool threads, while the controller's HAT client blocks pool threads until
        // it answers. On a 2-core runner the pool starts with 2 workers and adds about 2 a second, so a burst (a
        // command's writes and read-backs, the 25 ms input poll, status polls, the camera, the exporter) can hold the
        // emulator's answer past the 1 s request timeout. The deployed emulator is a separate process and never
        // shares the controller's pool.
        ThreadPool.GetMinThreads(out var workers, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, MinWorkerThreads), completionPorts);
    }

    private readonly Dictionary<string, string?> _settings;
    private readonly EmulatedCameraHost? _camera;

    private EmulatedRoofRig(
        EmulatedRoofRigOptions options,
        HatEmulatorSession session,
        HatEmulatorServer server,
        EmulatedCameraHost? camera,
        Dictionary<string, string?> settings)
    {
        Options = options;
        Session = session;
        Server = server;
        _camera = camera;
        _settings = settings;
        Logs = new RecordingLoggerProvider(options.LogCapacity);
        App = CreateApp(Logs);
    }

    public EmulatedRoofRigOptions Options { get; }

    public HatEmulatorSession Session { get; }

    public HatEmulatorServer Server { get; }

    /// <summary>The emulated camera the proxy reads, with <see cref="EmulatedRoofRigOptions.Camera"/>.</summary>
    public EmulatedCamera Camera => _camera?.Camera ?? throw new InvalidOperationException("The rig was started without a camera.");

    /// <summary>The controller host now running (a new one after <see cref="RestartControllerAsync"/>).</summary>
    public EmulatedRoofApp App { get; private set; }

    /// <summary>How long the last <see cref="RestartControllerAsync"/> took to stop the old host.</summary>
    public TimeSpan LastStopDuration { get; private set; }

    /// <summary>What the controller host now running has logged.</summary>
    public RecordingLoggerProvider Logs { get; private set; }

    /// <summary>The register endpoint the controller connects to.</summary>
    public string Endpoint => $"127.0.0.1:{Server.LocalEndPoint.Port}";

    public HatEmulatorStatus Plant => Session.GetStatus();

    public IRoofControllerServiceV4 Controller => App.Services.GetRequiredService<IRoofControllerServiceV4>();

    /// <summary>The controller's base address: its loopback port with <see cref="EmulatedRoofRigOptions.Kestrel"/>.</summary>
    public Uri BaseAddress => Options.Kestrel
        ? new Uri(App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First())
        : App.ClientOptions.BaseAddress;

    /// <summary>
    /// Starts the emulator with the documented installation (changed by <see cref="EmulatedRoofRigOptions.Plant"/>),
    /// then the controller with the documented wiring (normally open limits, not ignored), and waits for it to
    /// initialize with the roof closed, unless the link starts down.
    /// </summary>
    public static async Task<EmulatedRoofRig> StartAsync(EmulatedRoofRigOptions? options = null)
    {
        options ??= new EmulatedRoofRigOptions();
        var plant = new RoofPlantOptions();
        plant = plant with { Mechanics = plant.Mechanics with { TravelMeters = options.TravelMeters } };
        plant = options.Plant?.Invoke(plant) ?? plant;
        var session = new HatEmulatorSession(new HatEmulatorSessionOptions { Plant = plant, TimeScale = options.TimeScale });
        var server = new HatEmulatorServer(session.CurrentClient, new IPEndPoint(IPAddress.Loopback, 0)) { Outage = options.StartWithTheLinkDown };
        options.BeforeTheControllerStarts?.Invoke(session);
        server.Start();
        var camera = options.Camera ? await EmulatedCameraHost.StartAsync(session.GetStatus) : null;

        var values = new Dictionary<string, string?>
        {
            ["HatEmulator:Enabled"] = "true",
            ["HatEmulator:Host"] = "127.0.0.1",
            ["HatEmulator:Port"] = server.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture),
            ["RoofControllerOptionsV4:UseNormallyClosedLimitSwitches"] = "false",
            ["RoofControllerOptionsV4:IgnorePhysicalLimitSwitches"] = "false"
        };
        if (!string.Equals(options.Environment, "Development", StringComparison.Ordinal))
        {
            // As the compose emulator profile: the consent to run the emulator outside Development, and plain HTTP.
            values["HatEmulator:AllowOutsideDevelopment"] = "true";
            values["RoofControllerSecurity:RequireHttps"] = "false";
        }

        if (camera is not null)
        {
            // No credentials: appsettings.json has none, but a developer's user secrets might.
            values["BlueIris:BaseUrl"] = camera.BaseAddress.ToString();
            values["BlueIris:UserName"] = string.Empty;
            values["BlueIris:Password"] = string.Empty;
        }

        foreach (var (key, value) in options.Settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var rig = new EmulatedRoofRig(options, session, server, camera, values);
        try
        {
            await rig.StartControllerAsync(waitForInitialization: !options.StartWithTheLinkDown, options.InitialStatus);
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops the controller host and starts a new one against the same plant, as a container restart does. With
    /// <paramref name="crash"/>, the register port is down while the host stops, so its shutdown stop never reaches the
    /// HAT: the process died and the HAT keeps the relays it was last given. The new host starts with the link back, and
    /// the rig waits for it to initialize wherever the roof is.
    /// </summary>
    public async Task RestartControllerAsync(bool crash, Action<HatEmulatorSession>? whileStopped = null)
    {
        if (crash)
        {
            Server.Outage = true;
        }

        var stopping = Stopwatch.StartNew();
        await App.DisposeAsync();
        LastStopDuration = stopping.Elapsed;
        whileStopped?.Invoke(Session);
        Server.Outage = false;
        Logs = new RecordingLoggerProvider(Options.LogCapacity);
        App = CreateApp(Logs);
        await StartControllerAsync(waitForInitialization: true, status: null);
    }

    private EmulatedRoofApp CreateApp(RecordingLoggerProvider logs)
    {
        var app = new EmulatedRoofApp(_settings, Options.Environment, logs);
        if (Options.Kestrel)
        {
            app.UseKestrel(0);
        }

        return app;
    }

    private async Task StartControllerAsync(bool waitForInitialization, RoofControllerStatus? status)
    {
        if (Options.Kestrel)
        {
            App.StartServer();
        }
        else
        {
            App.CreateApiClient().Dispose();
        }

        if (waitForInitialization)
        {
            await WaitForControllerAsync(
                s => s.IsInitialized && (status is null || s.Status == status),
                TimeSpan.FromSeconds(20),
                status is null ? "the controller to initialize" : $"the controller to initialize {status}");
        }
    }

    public HttpClient CreateApiClient(string? apiKey = null)
    {
        if (!Options.Kestrel)
        {
            return App.CreateApiClient(apiKey);
        }

        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = BaseAddress };
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add(RoofControllerApiContract.ApiKeyHeaderName, apiKey);
        }

        return client;
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

    public Task<RoofStatusResponse> WaitForControllerAsync(Func<RoofStatusResponse, bool> condition, string what)
        => WaitForControllerAsync(condition, MotionTimeout, what);

    public async Task<HatEmulatorStatus> WaitForPlantAsync(Func<HatEmulatorStatus, bool> condition, string what, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var plant = Plant;
            if (condition(plant))
            {
                return plant;
            }

            if (clock.Elapsed > (timeout ?? MotionTimeout))
            {
                throw new AssertFailedException($"Timed out waiting for {what}. Plant: {plant}. Controller: {Controller.GetCurrentStatusSnapshot()}.");
            }

            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The controller first, so its shutdown stop reaches the emulator.
        await App.DisposeAsync();
        if (_camera is not null)
        {
            await _camera.DisposeAsync();
        }

        await Server.DisposeAsync();
        Session.Dispose();
    }
}

/// <summary>The production host with the settings given and the test API keys; nothing is replaced.</summary>
internal sealed class EmulatedRoofApp(Dictionary<string, string?> settings, string environment, RecordingLoggerProvider logs) : WebApplicationFactory<Program>
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

        // The console's scripts and styles are the build's static web assets, which the host serves by itself only in
        // Development (a published image has them in wwwroot). The browser tests run the console in Production.
        if (!string.Equals(environment, Environments.Development, StringComparison.Ordinal))
        {
            builder.UseStaticWebAssets();
        }

        // Program reads the exporter endpoint while it adds services, before the configuration above applies.
        if (values.TryGetValue("OTEL_EXPORTER_OTLP_ENDPOINT", out var otlpEndpoint))
        {
            builder.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint);
        }

        builder.ConfigureLogging(logging => logging.AddProvider(logs));
    }
}
