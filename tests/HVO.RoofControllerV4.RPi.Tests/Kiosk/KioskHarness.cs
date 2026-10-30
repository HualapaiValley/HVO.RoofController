using System.Collections.Concurrent;
using System.Net.WebSockets;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using ManualTimeProvider = HVO.RoofControllerV4.RPi.Tests.TestSupport.ManualTimeProvider;
using HVO.RoofControllerV4.RPi.Tests.Web;
using HVO.RoofControllerV4.Screens;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The kiosk's <see cref="KioskConsole"/> against the controller's real API in process, with its device key: a mocked
/// roof (the settings tests' roof double, so settings can be changed too), a settings file in a temporary directory, and
/// an operator and an admin who each have a PIN (a viewer cannot have one). The controller's clock is frozen, so it sends no heartbeat and
/// never ends a PIN session by itself; the kiosk's clock moves only when a test advances it (the idle lock, the screen
/// timeout, the lease and the feed's staleness follow it). <see cref="Reachable"/> takes the controller off the network.
/// </summary>
internal sealed class KioskHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    public const string Operator = "olga";

    public const string Admin = "ada";

    private readonly ConcurrentDictionary<string, int> _sent = new();
    private readonly ConcurrentDictionary<string, int> _answered = new();
    private RoofStatusResponse _current = Snapshot();
    private long _version = 11;
    private volatile bool _reachable = true;

    private KioskHarness(bool localKey, IDictionary<string, string?>? settings, KioskConsoleOptions? options, TimeSpan? requestTimeout)
    {
        Directory = new WebTestSupport.TempDirectory();
        Roof = new SettingsApiTests.RoofDouble();
        Roof.Mock.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => Current);
        Roof.Mock.Setup(service => service.RenewLease()).Returns(() => Result<RoofStatusResponse>.Success(Report(Current)));
        Roof.Mock.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(() =>
        {
            Report(Snapshot(RoofControllerStatus.Stopped));
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Stopped);
        });
        Roof.Mock.Setup(service => service.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            Report(Current with { IsFaultLatched = false, Status = RoofControllerStatus.Stopped });
            return Result<bool>.Success(true);
        });

        var all = new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:ApiKeys:4:Name"] = "test-kiosk",
            ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.ViewerRole,
            ["RoofControllerSecurity:ApiKeys:4:Key"] = RoofClientApiTests.KioskKey,
            ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "true",
            ["RoofControllerSecurity:ApiKeys:4:Local"] = localKey ? "true" : "false"
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }

        Host = new RoofApiTestHost(
            Roof.Mock,
            all,
            configureServices: services =>
            {
                // A cheap hash so the tests run quickly; production uses the ASP.NET Core default.
                services.Configure<PasswordHasherOptions>(hasher => hasher.IterationCount = 1_000);
                services.AddSingleton<TimeProvider>(ServerClock);
            },
            settingsFilePath: SettingsPath,
            secretsFilePath: Path.Combine(Directory.Path, "secrets", "managed-secrets.json"));
        Roof.StartFrom(Host);
        Logs = new RecordingLoggerProvider();
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => builder.AddProvider(Logs));
        Credential = new RoofKioskCredential(RoofClientApiTests.KioskKey);
        Client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = ClientTestSupport.BaseAddress,
            Credential = Credential,
            CreateHandler = () => new GateHandler(this, ClientTestSupport.CreateHandler(() => Host.Server)),
            WebSocketFactory = ConnectAsync,
            StatusFeed = ClientTestSupport.FastFeed,
            TimeProvider = Clock,
            StopTimeout = TimeSpan.FromSeconds(10),
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30),
            LoggerFactory = LoggerFactory,
            // The program's name and version, as the installed kiosk or Mac app sends them, so the command history says
            // "kiosk" or "Mac app".
            ProgramAssembly = options?.Wording.Device == KioskWording.Desktop.Device
                ? typeof(HVO.RoofControllerV4.Mac.Program).Assembly
                : typeof(HVO.RoofControllerV4.Kiosk.Program).Assembly
        });
        Console = new KioskConsole(Client, options, LoggerFactory.CreateLogger<KioskConsole>());
    }

    /// <summary>The roof the controller reports: its commands, its lease renewal and the hub's first message read it.</summary>
    public RoofStatusResponse Current => Volatile.Read(ref _current);

    public WebTestSupport.TempDirectory Directory { get; }

    public string SettingsPath => Path.Combine(Directory.Path, "config", "appsettings.Local.json");

    public SettingsApiTests.RoofDouble Roof { get; }

    public RoofApiTestHost Host { get; }

    public ManualTimeProvider ServerClock { get; } = new(Start);

    /// <summary>The kiosk's clock.</summary>
    public ManualTimeProvider Clock { get; } = new(Start);

    public RecordingLoggerProvider Logs { get; }

    public ILoggerFactory LoggerFactory { get; }

    public RoofKioskCredential Credential { get; }

    public RoofControllerClient Client { get; }

    public KioskConsole Console { get; }

    /// <summary>
    /// False: every request and every connection to the controller fails as a refused connection does, as when the
    /// controller's Pi is off the network. A status feed already connected stays connected.
    /// </summary>
    public bool Reachable
    {
        get => _reachable;
        set => _reachable = value;
    }

    /// <summary>
    /// When it returns true for a request, the controller handles the request but its answer never arrives: the
    /// connection ends first.
    /// </summary>
    public Func<HttpRequestMessage, bool>? CutAnswer { get; set; }

    /// <summary>Changes a request before the controller has it, as an older controller would read it (a query it ignores).</summary>
    public Action<HttpRequestMessage>? AlterRequest { get; set; }

    /// <summary>Optional controller build metadata override for cross-version UI tests, after normal authorization.</summary>
    public string? ControllerVersion { get; set; }

    /// <summary>
    /// The figures every summary the controller answers carries (#209), in place of this machine's: a render is the same
    /// on every machine, and never shows its addresses. Where the web UI is reached is as the installer writes it for a Pi
    /// open to the network, with a documentation address (RFC 5737); the container's own address is never shown.
    /// The version is the controller's, or <see cref="ControllerVersion"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonNode?> SummaryFigures = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>
    {
        ["uptimeSeconds"] = 7_380,
        ["hostUptimeSeconds"] = 356_460,
        ["processorCount"] = 4,
        ["cpuUsagePercent"] = 12.5,
        ["loadAverage1Minute"] = 0.42,
        ["loadAverage5Minutes"] = 0.35,
        ["loadAverage15Minutes"] = 0.3,
        ["cpuTemperatureCelsius"] = 47.2,
        ["memoryTotalBytes"] = 4L << 30,
        ["memoryAvailableBytes"] = 3_113_851_289L,
        ["diskTotalBytes"] = 31_268_536_320L,
        ["diskFreeBytes"] = 18L << 30,
        ["webUiUrls"] = new System.Text.Json.Nodes.JsonArray("https://192.0.2.10:8088/"),
        ["webUiAccess"] = "Network",
        ["hostAddresses"] = new System.Text.Json.Nodes.JsonArray("192.0.2.10"),
        ["ipAddresses"] = new System.Text.Json.Nodes.JsonArray("172.17.0.2"),
        ["runningInContainer"] = true
    };

    /// <summary>
    /// Awaited after the controller handled each request and before its answer reaches the kiosk: a slow network, or
    /// (with a task the test completes) an answer held on its way.
    /// </summary>
    public Func<HttpRequestMessage, CancellationToken, Task>? DelayAnswer { get; set; }

    /// <summary>
    /// How many <paramref name="method"/> requests to <paramref name="path"/> the kiosk has sent, counted as each is sent
    /// (before any answer, and whether or not the controller is <see cref="Reachable"/>).
    /// </summary>
    public int Sent(HttpMethod method, string path) => _sent.GetValueOrDefault(Key(method, path));

    /// <summary>How many of those <see cref="Sent"/> requests the kiosk has had an answer to, or a failure.</summary>
    public int Answered(HttpMethod method, string path) => _answered.GetValueOrDefault(Key(method, path));

    /// <summary>
    /// A kiosk, not started, with an operator (<see cref="Operator"/>) and an admin (<see cref="Admin"/>) who each have
    /// <see cref="TestSecrets.Pin"/>.
    /// </summary>
    /// <param name="localKey">True: the controller marks the kiosk's key local, so an admin may change local-only settings.</param>
    public static async Task<KioskHarness> CreateAsync(
        bool localKey = false,
        IDictionary<string, string?>? settings = null,
        KioskConsoleOptions? options = null,
        TimeSpan? requestTimeout = null, bool populateUsers = true)
    {
        var harness = new KioskHarness(localKey, settings, options, requestTimeout);
        if (populateUsers)
        {
            await RoofClientApiTests.AddUserAsync(harness.Host, Operator, RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
            await RoofClientApiTests.AddUserAsync(harness.Host, Admin, RoofControllerApiContract.AdminRole, TestSecrets.Pin);
        }
        return harness;
    }

    /// <summary>The name the controller sends when none is set, as the kiosk shows it on a controller as installed.</summary>
    public static readonly string ControllerName = new RoofControllerHostOptionsV4().ControllerName;

    /// <summary>A roof status as the controller reports it, taken at <see cref="Start"/>.</summary>
    public static RoofStatusResponse Snapshot(
        RoofControllerStatus status = RoofControllerStatus.Closed,
        RoofMotionDirection motion = RoofMotionDirection.None,
        bool faultLatched = false,
        RoofRelayRegisterState relayState = RoofRelayRegisterState.Verified)
        => RoofServiceMock.Snapshot(status, motion, faultLatched, relayState) with { SnapshotUtc = Start, LastTransitionUtc = Start, ControllerName = ControllerName };

    /// <summary>
    /// A roof that is opening, with <paramref name="leaseSeconds"/> of operator lease left: 6 s is renewed every 2 s,
    /// within the feed's 3 s before a quiet feed is stale.
    /// </summary>
    public static RoofStatusResponse Opening(double? leaseSeconds = 6)
        => Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = leaseSeconds };

    /// <summary>Starts the console and waits for the first status from the hub.</summary>
    public async Task StartLiveAsync()
    {
        Console.Start();
        await WaitForAsync(view => view.FeedLabel == "live", "the live status");
    }

    /// <summary>Unlocks the kiosk for <paramref name="name"/> with the right PIN, and waits for the view to say so.</summary>
    public async Task UnlockAsync(string name = Operator)
    {
        var refusal = await Console.UnlockAsync(name, TestSecrets.Pin);
        refusal.Should().BeNull();
        await WaitForAsync(view => view.UnlockedBy == name, $"the kiosk unlocked by {name}");
    }

    public async Task PasswordAsync(string name)
    {
        (await Console.SignInAsync(name, TestSecrets.Password)).Should().BeNull();
        await WaitForAsync(view => view.UnlockedBy == name, "password session");
    }

    public Task WaitForAsync(Func<KioskView, bool> condition, string what)
        => ClientTestSupport.WaitUntilAsync(() => condition(Console.View), what);

    /// <summary>The roof is now <paramref name="status"/>, as the controller's next snapshot (a newer version).</summary>
    public RoofStatusResponse Report(RoofStatusResponse status)
    {
        var reported = status with { StatusVersion = Interlocked.Increment(ref _version) };
        Volatile.Write(ref _current, reported);
        return reported;
    }

    /// <summary>The controller reports a new status; the hub pushes it.</summary>
    public void Push(RoofStatusResponse status)
        => Roof.Mock.Raise(service => service.StatusChanged += null, new RoofStatusChangedEventArgs(Report(status)));

    /// <summary>Open sets the roof opening, with the operator lease of <see cref="Opening"/>.</summary>
    public void OpensTheRoof(Action? whileOpening = null)
        => Roof.Mock.Setup(service => service.Open()).Returns(() =>
        {
            whileOpening?.Invoke();
            Report(Opening());
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });

    /// <summary>How many times the roof service's <paramref name="method"/> was called.</summary>
    public int Calls(string method) => Roof.Mock.Invocations.Count(invocation => invocation.Method.Name == method);

    /// <summary>Moves the kiosk's clock one tick at a time, so each tick sees the requests the previous one started answered.</summary>
    public async Task AdvanceAsync(TimeSpan by, TimeSpan? step = null)
    {
        var each = step ?? TimeSpan.FromSeconds(1);
        for (var moved = TimeSpan.Zero; moved < by; moved += each)
        {
            Clock.Advance(by - moved < each ? by - moved : each);
            await Task.Yield();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Console.DisposeAsync();
        Client.Dispose();
        Host.Dispose();
        LoggerFactory.Dispose();
        Directory.Dispose();
    }

    private async ValueTask<WebSocket> ConnectAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        if (!Reachable)
        {
            throw new HttpRequestException("Connection refused (test).");
        }

        return await ClientTestSupport.WebSocketFactory(() => Host.Server)(uri, headers, cancellationToken);
    }

    private static string Key(HttpMethod method, string path) => $"{method.Method} {path.TrimStart('/')}";

    private sealed class GateHandler(KioskHarness harness, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = Key(request.Method, request.RequestUri!.AbsolutePath);
            harness._sent.AddOrUpdate(key, 1, static (_, count) => count + 1);
            try
            {
                if (!harness.Reachable)
                {
                    throw new HttpRequestException("Connection refused (test).");
                }

                harness.AlterRequest?.Invoke(request);
                var response = await base.SendAsync(request, cancellationToken);
                if (harness.CutAnswer?.Invoke(request) == true)
                {
                    response.Dispose();
                    throw new HttpRequestException(HttpRequestError.ResponseEnded, "The connection ended before the answer arrived.");
                }

                if (harness.DelayAnswer is { } delay)
                {
                    try
                    {
                        await delay(request, cancellationToken);
                    }
                    catch
                    {
                        response.Dispose();
                        throw;
                    }
                }

                var path = request.RequestUri.AbsolutePath.TrimStart('/');
                if (response.IsSuccessStatusCode && harness.ControllerVersion is { } version && path == RoofApiRoutes.SystemInformation)
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                    json["applicationVersion"] = version;
                    response.Content.Dispose();
                    response.Content = new StringContent(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                }

                if (response.IsSuccessStatusCode && path == RoofApiRoutes.SystemSummary)
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
                    foreach (var (name, value) in SummaryFigures)
                    {
                        json[name] = value?.DeepClone();
                    }

                    if (harness.ControllerVersion is { } summaryVersion)
                    {
                        json["applicationVersion"] = summaryVersion;
                    }

                    response.Content.Dispose();
                    response.Content = new StringContent(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                }
                return response;
            }
            finally
            {
                harness._answered.AddOrUpdate(key, 1, static (_, count) => count + 1);
            }
        }
    }
}
