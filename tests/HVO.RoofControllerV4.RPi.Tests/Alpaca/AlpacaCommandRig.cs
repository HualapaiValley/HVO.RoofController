using System.Security.Claims;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The controller's Alpaca command rules in process (#220): a real controller service on the fake HAT, a real safety
/// service reading scripted telescope and weather sources, and <see cref="RoofAlpacaCommands"/> with an External Alpaca
/// account key and an Alpaca server key. Real time; the roof starts mid-travel, idle, the Stop hold set (as at start).
/// </summary>
internal sealed class AlpacaCommandRig : IAsyncDisposable
{
    public const string CommanderKey = "alpaca-external-account-key-for-tests-01";
    public const string ServerKey = "alpaca-server-own-account-key-for-tests-02";
    public const string RotatedCommanderKey = "alpaca-external-account-key-for-tests-03";

    private AlpacaCommandRig(
        SimulatedRoofControllerService controller,
        FakeRoofHat hat,
        RoofSafetyService safety,
        RoofAlpacaCommands commands,
        TestOptionsMonitor<RoofAlpacaCommandOptions> options,
        TestOptionsMonitor<RoofOperatorLeaseOptions> lease,
        TestOptionsMonitor<RoofSafetyOptions> safetyOptions,
        TestOptionsMonitor<RoofControllerSecurityOptions> keys,
        RoofApiKeyStore keyStore,
        IReadOnlyList<ScriptedTelescopeSource> scopes,
        SimulatedWeather? weather)
    {
        Controller = controller;
        Hat = hat;
        Safety = safety;
        Commands = commands;
        Options = options;
        Lease = lease;
        SafetyOptions = safetyOptions;
        Keys = keys;
        KeyStore = keyStore;
        Scopes = scopes;
        Weather = weather;
    }

    public SimulatedRoofControllerService Controller { get; }

    public FakeRoofHat Hat { get; }

    public RoofSafetyService Safety { get; }

    public RoofAlpacaCommands Commands { get; }

    public TestOptionsMonitor<RoofAlpacaCommandOptions> Options { get; }

    public TestOptionsMonitor<RoofOperatorLeaseOptions> Lease { get; }

    public TestOptionsMonitor<RoofSafetyOptions> SafetyOptions { get; }

    public TestOptionsMonitor<RoofControllerSecurityOptions> Keys { get; }

    public RoofApiKeyStore KeyStore { get; }

    public IReadOnlyList<ScriptedTelescopeSource> Scopes { get; }

    public ScriptedTelescopeSource Scope => Scopes[0];

    public SimulatedWeather? Weather { get; }

    /// <summary>The External Alpaca account's principal (its key, as the controller authenticates it).</summary>
    public ClaimsPrincipal Commander => Principal(CommanderKey);

    /// <summary>The Alpaca server's own account's principal.</summary>
    public ClaimsPrincipal Server => Principal(ServerKey);

    public static readonly RoofAlpacaClient Client = new(7, 41, "192.168.1.50", "basic");

    /// <summary>
    /// Starts the rig: <paramref name="scopes"/> blocking telescopes named Main, Guide, ... (parked unless said), the
    /// commands mode, the delay, and an optional weather source.
    /// </summary>
    public static async Task<AlpacaCommandRig> StartAsync(
        int scopes = 1,
        bool parked = true,
        RoofAlpacaCommandMode mode = RoofAlpacaCommandMode.CloseOnly,
        TimeSpan? directionChangeDelay = null,
        bool? weatherSafe = null,
        int budgetMs = 600,
        TimeSpan? operatorLeaseTimeout = null,
        TimeSpan? watchdog = null,
        bool startSafety = true,
        IReadOnlyList<string>? configuredTelescopes = null,
        TimeProvider? time = null,
        IReadOnlyList<ITelescopeRereadSource>? telescopes = null,
        Action<RoofControllerOptionsV4>? configureController = null,
        bool initialize = true,
        TimeSpan? readTimeout = null)
    {
        time ??= TimeProvider.System;
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false); // mid-travel, healthy (NC limits: HIGH = not active)
        var controller = SimulatedRoofControllerService.Create(hat, time, options =>
        {
            options.DirectionChangeDelay = directionChangeDelay ?? TimeSpan.Zero;
            options.OperatorLeaseTimeout = operatorLeaseTimeout;
            options.SafetyWatchdogTimeout = watchdog ?? TimeSpan.FromSeconds(30);
            configureController?.Invoke(options);
        });
        if (initialize)
        {
            (await controller.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        }

        var names = new[] { "Main", "Guide", "Third" };
        var scripted = Enumerable.Range(0, telescopes is null ? scopes : 0).Select(index => new ScriptedTelescopeSource(names[index], parked)).ToArray();
        ITelescopeRereadSource[] sources = telescopes is null ? scripted : [.. telescopes];
        var weather = weatherSafe is { } safe ? new SimulatedWeather(safe) : null;
        var safetyOptions = new TestOptionsMonitor<RoofSafetyOptions>(new RoofSafetyOptions
        {
            InterlockMode = RoofSafetyInterlockMode.Warn,
            PollInterval = RoofSafetyOptions.MaxPollInterval,
            ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(2),
            MaxReadingAge = TimeSpan.FromSeconds(60),
            Telescopes = [.. (configuredTelescopes ?? []).Select(name => new RoofSafetyTelescopeOptions { Name = name, Url = "http://192.0.2.1:11111", BlocksRoof = true })]
        });
        var safety = new RoofSafetyService(sources, weather is null ? [] : [weather], safetyOptions, time, NullLogger<RoofSafetyService>.Instance);
        if (startSafety)
        {
            await safety.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => safety.GetCurrent().Sources.All(source => source.LastReadingUtc is not null || source.Kind != RoofSafetySourceKind.TelescopePark)
                && scripted.All(scope => scope.Reads > 0), "the safety service's first poll");
        }

        var keys = new TestOptionsMonitor<RoofControllerSecurityOptions>(new RoofControllerSecurityOptions
        {
            ApiKeys =
            [
                new RoofApiKeyOptions { Name = "alpaca-external", Role = RoofAlpacaContract.CommanderRole, Key = CommanderKey },
                new RoofApiKeyOptions { Name = "alpaca-server", Role = RoofAlpacaContract.ServerRole, Key = ServerKey }
            ]
        });
        var keyStore = new RoofApiKeyStore(keys, NullLogger<RoofApiKeyStore>.Instance);
        var options = new TestOptionsMonitor<RoofAlpacaCommandOptions>(new RoofAlpacaCommandOptions { AllowCommands = mode, ScopeRereadBudgetMs = budgetMs });
        var lease = new TestOptionsMonitor<RoofOperatorLeaseOptions>(new RoofOperatorLeaseOptions());
        var commands = new RoofAlpacaCommands(controller, safety, options, lease, safetyOptions, keyStore, sources, time, NullLogger<RoofAlpacaCommands>.Instance);
        return new AlpacaCommandRig(controller, hat, safety, commands, options, lease, safetyOptions, keys, keyStore, scripted, weather);
    }

    /// <summary>A principal for <paramref name="key"/> as the controller authenticates it.</summary>
    public ClaimsPrincipal Principal(string key)
    {
        KeyStore.TryValidate(key, out var identity).Should().BeTrue();
        return RoofPrincipalFactory.Create(identity!, RoofControllerSecurityDefaults.ApiKeyScheme);
    }

    /// <summary>A person's password session (or PIN session) with <paramref name="role"/>.</summary>
    public static ClaimsPrincipal Person(string name = "ada", string role = RoofControllerApiContract.OperatorRole, bool pin = false, string? sessionId = null)
        => RoofPrincipalFactory.CreateForSession(
            new StoredSession(sessionId ?? $"session-{name}", new string('0', 64), name, role, pin ? RoofCredentialKind.Pin : RoofCredentialKind.Session,
                pin ? "kiosk-1" : null, pin ? "kiosk-key-id" : null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)),
            RoofControllerSecurityDefaults.SessionScheme);

    /// <summary>A person clears the Stop hold that the controller's start set.</summary>
    public AlpacaCommandRig Ready()
    {
        Commands.ClearHold(Person("hold-clearer"));
        return this;
    }

    /// <summary>A ticket then the Close, as the Alpaca server sends them, with the External account's key.</summary>
    public async Task<RoofAlpacaCommandOutcome> CloseAsync(ClaimsPrincipal? commander = null, RoofAlpacaClient? client = null)
    {
        var user = commander ?? Commander;
        var ticket = Commands.IssueTicket(RoofAlpacaCommandKind.Close, user, client ?? Client).Ticket;
        ticket.Should().NotBeNull();
        return await Commands.CloseAsync(new RoofAlpacaCommandRequest(ticket!.TicketId, ticket.InstanceId, ticket.Generation), user, client ?? Client);
    }

    /// <summary>Sends a ticketed Close whose ticket is <paramref name="ticket"/>.</summary>
    public Task<RoofAlpacaCommandOutcome> CloseAsync(RoofAlpacaTicket ticket, ClaimsPrincipal? commander = null)
        => Commands.CloseAsync(new RoofAlpacaCommandRequest(ticket.TicketId, ticket.InstanceId, ticket.Generation), commander ?? Commander, Client);

    public RoofAlpacaTicket Ticket(ClaimsPrincipal? commander = null)
        => Commands.IssueTicket(RoofAlpacaCommandKind.Close, commander ?? Commander, Client).Ticket!;

    /// <summary>Rotates the External account's key (an epoch change).</summary>
    public void RotateCommanderKey()
        => Keys.Set(new RoofControllerSecurityOptions
        {
            ApiKeys =
            [
                new RoofApiKeyOptions { Name = "alpaca-external", Role = RoofAlpacaContract.CommanderRole, Key = RotatedCommanderKey },
                new RoofApiKeyOptions { Name = "alpaca-server", Role = RoofAlpacaContract.ServerRole, Key = ServerKey }
            ]
        });

    /// <summary>Releases every re-read the first telescope holds.</summary>
    public void ReleaseHeldRereads(bool? parked) => Scope.ReleaseAll(parked);

    public int RelayOnWrites(byte relay) => Hat.RelayWriteLog.Count(write => write.Register == 1 && write.Value == relay);

    public static async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Commands.Dispose();
        await Safety.StopAsync(CancellationToken.None);
        Safety.Dispose();
        KeyStore.Dispose();
        await Controller.DisposeAsync();
    }
}

/// <summary>
/// A blocking telescope whose every read (background or re-read) answers as the test scripts: parked or not, after an
/// optional delay, or failing; it counts reads and re-reads, and can hold a read until released.
/// </summary>
internal sealed class ScriptedTelescopeSource(string name, bool parked) : ITelescopeRereadSource
{
    private readonly object _gate = new();
    private int _reads;
    private int _rereads;
    private readonly Queue<TaskCompletionSource<bool?>> _held = new();
    private readonly List<CancellationToken> _rereadTokens = [];

    public string Name { get; } = name;

    public TimeSpan? MaxReadingAge => null;

    public bool BlocksRoof => true;

    /// <summary>What the next answers say.</summary>
    public bool Parked { get; set; } = parked;

    /// <summary>When true, every read fails (a device that cannot be reached).</summary>
    public bool Fails { get; set; }

    /// <summary>How long each re-read takes.</summary>
    public TimeSpan RereadDelay { get; set; }

    /// <summary>When true, each re-read waits for <see cref="ReleaseOne"/> and answers what that gives.</summary>
    public bool HoldRereads { get; set; }

    /// <summary>When true, each background read hangs until it is cancelled (a poll stuck in flight).</summary>
    public bool HoldPolls { get; set; }

    public int Reads => Volatile.Read(ref _reads);

    public int Rereads => Volatile.Read(ref _rereads);

    public IReadOnlyList<TimeSpan> RecentLatencies { get; set; } = [];

    public int Held
    {
        get { lock (_gate) { return _held.Count; } }
    }

    /// <summary>The cancellation token each re-read was given, in the order they came.</summary>
    public IReadOnlyList<CancellationToken> RereadTokens
    {
        get { lock (_gate) { return [.. _rereadTokens]; } }
    }

    /// <summary>Releases every re-read held so far (the oldest first), each answering <paramref name="parked"/>.</summary>
    public void ReleaseAll(bool? parked)
    {
        while (true)
        {
            TaskCompletionSource<bool?> held;
            lock (_gate)
            {
                if (_held.Count == 0)
                {
                    return;
                }

                held = _held.Dequeue();
            }

            held.TrySetResult(parked);
        }
    }

    /// <summary>Ends the oldest held re-read with <paramref name="parked"/> (null: it fails).</summary>
    public void ReleaseOne(bool? parked)
    {
        TaskCompletionSource<bool?> held;
        lock (_gate)
        {
            held = _held.Dequeue();
        }

        held.TrySetResult(parked);
    }

    public async Task<TelescopeParkReading> ReadParkStateAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        if (HoldPolls)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        return Fails ? throw new IOException("Scripted telescope cannot be reached.") : new TelescopeParkReading(Parked);
    }

    public async Task<TelescopeParkReading> RereadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _reads);
        Interlocked.Increment(ref _rereads);
        lock (_gate)
        {
            _rereadTokens.Add(cancellationToken);
        }

        if (HoldRereads)
        {
            var held = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _held.Enqueue(held);
            }

            var answer = await held.Task.WaitAsync(cancellationToken);
            return answer is { } value ? new TelescopeParkReading(value) : throw new IOException("Scripted telescope failed.");
        }

        if (RereadDelay > TimeSpan.Zero)
        {
            await Task.Delay(RereadDelay, cancellationToken);
        }

        return Fails ? throw new IOException("Scripted telescope cannot be reached.") : new TelescopeParkReading(Parked);
    }
}

/// <summary>A weather source whose answer a test sets.</summary>
internal sealed class SimulatedWeather(bool safe) : IWeatherSafetySource
{
    public string Name => "Weather";

    public TimeSpan? MaxReadingAge => null;

    public bool Safe { get; set; } = safe;

    public Task<WeatherSafetyReading> ReadWeatherSafetyAsync(CancellationToken cancellationToken) => Task.FromResult(new WeatherSafetyReading(Safe));
}

internal static class AlpacaOutcomeExtensions
{
    public static string? Rule(this RoofAlpacaCommandOutcome outcome)
        => outcome.Result.IsSuccessful ? null : (outcome.Result.Error as RoofControllerException)?.AlpacaRefusal;

    public static RoofControllerErrorCode? Code(this RoofAlpacaCommandOutcome outcome) => outcome.Result.ErrorCode();
}
