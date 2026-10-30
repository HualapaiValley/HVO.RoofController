using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ManualTimeProvider = HVO.RoofControllerV4.RPi.Tests.TestSupport.ManualTimeProvider;
using CapturingLogger = HVO.RoofControllerV4.RPi.Tests.TestSupport.CapturingLogger<HVO.RoofControllerV4.RPi.Safety.RoofSafetyService>;

namespace HVO.RoofControllerV4.RPi.Tests.Safety;

/// <summary>
/// The safety service against simulated sources and a manual clock: each state (Safe, Unsafe, Unknown) with its
/// reasons, staleness per source, failed, hung and blocking reads, and the logged transitions.
/// </summary>
[TestClass]
public sealed class RoofSafetyServiceTests
{
    private static readonly RoofSafetyOptions Defaults = new()
    {
        PollInterval = TimeSpan.FromSeconds(10),
        ReadTimeout = TimeSpan.FromSeconds(5),
        MaxReadingAge = TimeSpan.FromSeconds(60)
    };

    private readonly ManualTimeProvider _time = new();
    private readonly CapturingLogger _logger = new();

    // ---- No sources ------------------------------------------------------------------------------------------------

    [TestMethod]
    public void NoSources_IsUnknown_BecauseNoneIsConfigured()
    {
        using var service = Create();

        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal(RoofSafetyStatus.NoSourcesReason);
        safety.Reasons.Should().Equal("No safety sources configured");
        safety.Sources.Should().BeEmpty();
        safety.InterlockMode.Should().Be(RoofSafetyInterlockMode.Off, "the interlock is advisory unless configured");
        safety.OverrideRole.Should().BeNull();
        safety.StateSinceUtc.Should().Be(_time.GetUtcNow());
    }

    [TestMethod]
    public async Task NoSources_WithTheInterlockOn_WarnsAtStart_ThatOpenStaysRefused()
    {
        using var service = Create(options: Options(o => o.InterlockMode = RoofSafetyInterlockMode.Block));

        await service.StartAsync(CancellationToken.None);
        // ExecuteAsync runs on the thread pool (.NET 10): wait for it before stopping, which would cancel it unstarted.
        await WaitUntilAsync(() => _logger.Entries.Count >= 2);
        await service.StopAsync(CancellationToken.None);

        _logger.Contains(LogLevel.Warning, "Safety interlock is Block with no safety sources configured").Should().BeTrue();
        _logger.Contains(LogLevel.Information, "Safety conditions Unknown (was not judged; interlock Block): No safety sources configured").Should().BeTrue();
    }

    // ---- Each state ------------------------------------------------------------------------------------------------

    [TestMethod]
    public void BeforeTheFirstRead_EverySourceIsUnknown()
    {
        var mount = new SimulatedTelescopeParkSource("Main");
        using var service = Create([mount]);

        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal("Telescope 'Main' has not been read yet");
        safety.Sources.Single().Should().BeEquivalentTo(new
        {
            Name = "Main",
            Kind = RoofSafetySourceKind.TelescopePark,
            State = RoofSafetyState.Unknown,
            LastReadingUtc = (DateTimeOffset?)null,
            IsStale = false,
            MaxReadingAgeSeconds = 60.0
        });
    }

    [TestMethod]
    public async Task EveryTelescopeParked_AndTheWeatherSafe_IsSafe_WithNoReasons()
    {
        var mount = new SimulatedTelescopeParkSource("Main");
        var second = new SimulatedTelescopeParkSource("Solar");
        var weather = new SimulatedWeatherSafetySource("Cloud sensor");
        using var service = Create([mount, second], [weather]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Safe);
        safety.Reasons.Should().BeEmpty();
        safety.Sources.Select(source => (source.Name, source.State, source.Reason)).Should().Equal(
            ("Main", RoofSafetyState.Safe, "Telescope 'Main' is parked"),
            ("Solar", RoofSafetyState.Safe, "Telescope 'Solar' is parked"),
            ("Cloud sensor", RoofSafetyState.Safe, "Weather monitor 'Cloud sensor' reports safe weather"));
        safety.Sources.Should().OnlyContain(source => source.LastReadingUtc == _time.GetUtcNow() && !source.IsStale);
        mount.Reads.Should().Be(1);
        weather.Reads.Should().Be(1);
    }

    [TestMethod]
    public async Task ATelescopeNotParked_IsUnsafe()
    {
        var mount = new SimulatedTelescopeParkSource("Main", isParked: false);
        using var service = Create([mount], [new SimulatedWeatherSafetySource("Cloud sensor")]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unsafe);
        safety.Reasons.Should().Equal("Telescope 'Main' is not parked");
    }

    [TestMethod]
    public async Task UnsafeWeather_IsUnsafe_AndKeepsTheSourcesDetail()
    {
        var weather = new SimulatedWeatherSafetySource("Cloud sensor", isSafe: false) { Detail = "rain detected" };
        using var service = Create([new SimulatedTelescopeParkSource("Main")], [weather]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unsafe);
        safety.Reasons.Should().Equal("Weather monitor 'Cloud sensor' reports unsafe weather");
        safety.Sources.Single(source => source.Kind == RoofSafetySourceKind.Weather).Detail.Should().Be("rain detected");
    }

    [TestMethod]
    public async Task UnsafeOutranksUnknown_AndItsReasonsComeFirst()
    {
        var unread = new SimulatedTelescopeParkSource("Solar") { Behaviour = SimulatedSourceBehaviour.Fails };
        var weather = new SimulatedWeatherSafetySource("Cloud sensor", isSafe: false);
        using var service = Create([unread], [weather]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unsafe);
        safety.Reasons.Should().Equal("Weather monitor 'Cloud sensor' reports unsafe weather", "Telescope 'Solar' cannot be read");
    }

    [TestMethod]
    public async Task OneUnknownSource_MakesTheWholeUnknown_EvenWhenTheRestAreSafe()
    {
        var failing = new SimulatedWeatherSafetySource("Cloud sensor") { Behaviour = SimulatedSourceBehaviour.Fails };
        using var service = Create([new SimulatedTelescopeParkSource("Main")], [failing]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal("Weather monitor 'Cloud sensor' cannot be read");
        _logger.Contains(LogLevel.Warning, "Safety source Weather 'Cloud sensor' could not be read (IOException)").Should().BeTrue();
        _logger.Entries.Should().NotContain(entry => entry.Message.Contains("cannot be reached"), "only the failure's type is logged, never its message");
    }

    // ---- Staleness -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AReading_CountsUntilItIsOlderThanTheMaximumAge_ThenTheSourceIsUnknown()
    {
        var mount = new SimulatedTelescopeParkSource("Main");
        using var service = Create([mount]);
        await service.PollAsync(CancellationToken.None);
        mount.Behaviour = SimulatedSourceBehaviour.Fails;

        _time.Advance(TimeSpan.FromSeconds(60));
        await service.PollAsync(CancellationToken.None);
        service.GetCurrent().State.Should().Be(RoofSafetyState.Safe, "a reading exactly the maximum age old still counts, though the read since failed");

        _time.Advance(TimeSpan.FromSeconds(1));
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal("Telescope 'Main': the last reading (parked) is older than 60 s");
        safety.Sources.Single().IsStale.Should().BeTrue();
        safety.Sources.Single().LastReadingUtc.Should().Be(_time.GetUtcNow() - TimeSpan.FromSeconds(61));
    }

    [TestMethod]
    public async Task AStaleUnsafeReading_IsUnknown_NotUnsafe_AndSaysWhatItLastSaid()
    {
        var weather = new SimulatedWeatherSafetySource("Cloud sensor", isSafe: false);
        using var service = Create(weather: [weather]);
        await service.PollAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(61));
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal("Weather monitor 'Cloud sensor': the last reading (unsafe) is older than 60 s");
    }

    [TestMethod]
    public async Task EachSource_HasItsOwnMaximumAge()
    {
        var mount = new SimulatedTelescopeParkSource("Main", maxReadingAge: TimeSpan.FromSeconds(20));
        var weather = new SimulatedWeatherSafetySource("Cloud sensor", maxReadingAge: TimeSpan.FromMinutes(5));
        using var service = Create([mount], [weather]);
        await service.PollAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(21));
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal("Telescope 'Main': the last reading (parked) is older than 20 s");
        safety.Sources.Select(source => (source.Name, source.State, source.MaxReadingAgeSeconds)).Should().Equal(
            ("Main", RoofSafetyState.Unknown, 20.0),
            ("Cloud sensor", RoofSafetyState.Safe, 300.0));

        _time.Advance(TimeSpan.FromMinutes(5));
        service.GetCurrent().Sources.Should().OnlyContain(source => source.IsStale);
    }

    [TestMethod]
    public async Task AnObservationTheDeviceDatesEarlier_IsThatMuchOlder()
    {
        var weather = new SimulatedWeatherSafetySource("Cloud sensor") { ObservedUtc = _time.GetUtcNow() - TimeSpan.FromSeconds(50) };
        using var service = Create(weather: [weather]);
        await service.PollAsync(CancellationToken.None);

        service.GetCurrent().Sources.Single().LastReadingUtc.Should().Be(_time.GetUtcNow() - TimeSpan.FromSeconds(50));
        service.GetCurrent().State.Should().Be(RoofSafetyState.Safe);

        _time.Advance(TimeSpan.FromSeconds(11));
        service.GetCurrent().State.Should().Be(RoofSafetyState.Unknown, "the reading was observed 61 s ago, though read 11 s ago");
    }

    [TestMethod]
    public async Task AnObservationDatedInTheFuture_CountsAsNow()
    {
        var mount = new SimulatedTelescopeParkSource("Main") { ObservedUtc = _time.GetUtcNow() + TimeSpan.FromHours(1) };
        using var service = Create([mount]);
        await service.PollAsync(CancellationToken.None);

        service.GetCurrent().Sources.Single().LastReadingUtc.Should().Be(_time.GetUtcNow());
        _time.Advance(TimeSpan.FromSeconds(61));
        service.GetCurrent().State.Should().Be(RoofSafetyState.Unknown, "a future date cannot keep a reading fresh");
    }

    [TestMethod]
    public async Task ASourceReadAgain_IsCurrentAgain_AndTheRecoveryIsLogged()
    {
        var mount = new SimulatedTelescopeParkSource("Main");
        using var service = Create([mount]);
        await service.PollAsync(CancellationToken.None);
        mount.Behaviour = SimulatedSourceBehaviour.Fails;
        _time.Advance(TimeSpan.FromSeconds(61));
        await service.PollAsync(CancellationToken.None);
        service.GetCurrent().State.Should().Be(RoofSafetyState.Unknown);

        mount.Behaviour = SimulatedSourceBehaviour.Answers;
        _time.Advance(TimeSpan.FromSeconds(10));
        await service.PollAsync(CancellationToken.None);

        service.GetCurrent().State.Should().Be(RoofSafetyState.Safe);
        _logger.Contains(LogLevel.Information, "Safety source TelescopePark 'Main' read again after 1 failed read(s).").Should().BeTrue();
        _logger.Entries.Count(entry => entry.Message.Contains("could not be read")).Should().Be(1, "a failure is logged when it starts, not on every read");
    }

    // ---- Reads that do not answer ----------------------------------------------------------------------------------

    [TestMethod]
    public async Task AReadThatDoesNotAnswer_FailsAtTheReadTimeout()
    {
        var mount = new SimulatedTelescopeParkSource("Main") { Behaviour = SimulatedSourceBehaviour.Hangs };
        using var service = Create([mount]);

        var poll = service.PollAsync(CancellationToken.None);
        await WaitUntilAsync(() => mount.Reads == 1);
        poll.IsCompleted.Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(5));
        await poll.WaitAsync(TimeSpan.FromSeconds(10));

        service.GetCurrent().Reasons.Should().Equal("Telescope 'Main' cannot be read");
        _logger.Contains(LogLevel.Warning, "could not be read (no answer within 5 s)").Should().BeTrue();
    }

    [TestMethod]
    public async Task ASourceWhoseReadIgnoresCancellation_IsNotReadAgainUntilThatReadEnds()
    {
        var mount = new SimulatedTelescopeParkSource("Main") { Behaviour = SimulatedSourceBehaviour.IgnoresCancellation };
        using var service = Create([mount]);
        var poll = service.PollAsync(CancellationToken.None);
        await WaitUntilAsync(() => mount.Reads == 1);
        _time.Advance(TimeSpan.FromSeconds(5));
        await poll.WaitAsync(TimeSpan.FromSeconds(10));

        await service.PollAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        mount.Reads.Should().Be(1, "the first read has not ended, so a second would only pile up behind it");
        service.GetCurrent().State.Should().Be(RoofSafetyState.Unknown);

        mount.Behaviour = SimulatedSourceBehaviour.Answers;
        mount.Release();
        await WaitUntilAsync(() =>
        {
            service.PollAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(10));
            return service.GetCurrent().State == RoofSafetyState.Safe;
        });
        mount.Reads.Should().Be(2);
    }

    // ---- The background loop ---------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheService_ReadsAtStart_ThenOncePerPollInterval()
    {
        var mount = new SimulatedTelescopeParkSource("Main");
        using var service = Create([mount]);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => mount.Reads == 1 && service.GetCurrent().State == RoofSafetyState.Safe);
        mount.IsParked = false;
        await WaitUntilAsync(() =>
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            return mount.Reads >= 2;
        });
        await WaitUntilAsync(() => service.GetCurrent().State == RoofSafetyState.Unsafe);
        await service.StopAsync(CancellationToken.None);

        _time.GetUtcNow().Should().BeOnOrAfter(new DateTimeOffset(2026, 1, 1, 0, 0, 10, TimeSpan.Zero), "the second read waited for the poll interval");
    }

    // ---- Logged transitions and the state's start time -------------------------------------------------------------

    [TestMethod]
    public async Task EveryChange_IsLoggedOnce_AtItsLevel_AndStateSinceMovesOnlyWithTheState()
    {
        var mount = new SimulatedTelescopeParkSource("Main");
        var weather = new SimulatedWeatherSafetySource("Cloud sensor");
        using var service = Create([mount], [weather]);
        var started = _time.GetUtcNow();
        service.GetCurrent().StateSinceUtc.Should().Be(started);

        _time.Advance(TimeSpan.FromSeconds(1));
        await service.PollAsync(CancellationToken.None);
        service.GetCurrent();
        var safeSince = _time.GetUtcNow();

        _time.Advance(TimeSpan.FromSeconds(1));
        mount.IsParked = false;
        await service.PollAsync(CancellationToken.None);
        var unsafeSince = _time.GetUtcNow();

        _time.Advance(TimeSpan.FromSeconds(1));
        weather.IsSafe = false;
        await service.PollAsync(CancellationToken.None);
        service.GetCurrent().StateSinceUtc.Should().Be(unsafeSince, "the state stayed Unsafe; only its reasons changed");

        _logger.Entries.Select(entry => (entry.Level, entry.Message)).Should().Equal(
            (LogLevel.Information, "Safety conditions Unknown (was not judged; interlock Off): Telescope 'Main' has not been read yet; Weather monitor 'Cloud sensor' has not been read yet."),
            (LogLevel.Information, "Safety conditions Safe (was Unknown; interlock Off)."),
            (LogLevel.Warning, "Safety conditions Unsafe (was Safe; interlock Off): Telescope 'Main' is not parked."),
            (LogLevel.Warning, "Safety conditions still Unsafe, now because: Telescope 'Main' is not parked; Weather monitor 'Cloud sensor' reports unsafe weather (interlock Off)."));
        safeSince.Should().BeBefore(unsafeSince);
    }

    [TestMethod]
    public async Task GoingUnknownFromSafe_IsAWarning()
    {
        var mount = new SimulatedTelescopeParkSource("Main");
        using var service = Create([mount]);
        await service.PollAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(61));
        service.GetCurrent();

        _logger.Entries.Last().Should().Be((LogLevel.Warning, "Safety conditions Unknown (was Safe; interlock Off): Telescope 'Main': the last reading (parked) is older than 60 s.", (Exception?)null));
    }

    [TestMethod]
    public void TheModeAndTheOverrideRole_AreReported()
    {
        var options = new TestOptionsMonitor<RoofSafetyOptions>(Options(o => o.InterlockMode = RoofSafetyInterlockMode.Warn));
        using var service = Create(options: options.CurrentValue, monitor: options);

        service.GetCurrent().Should().BeEquivalentTo(new { InterlockMode = RoofSafetyInterlockMode.Warn, OverrideRole = RoofControllerApiContract.OperatorRole });

        options.Set(Options(o =>
        {
            o.InterlockMode = RoofSafetyInterlockMode.Warn;
            o.OverrideRole = RoofSafetyOverrideRole.Admin;
        }));
        service.GetCurrent().OverrideRole.Should().Be(RoofControllerApiContract.AdminRole);

        options.Set(Options(o => o.InterlockMode = RoofSafetyInterlockMode.Block));
        service.GetCurrent().Should().BeEquivalentTo(new { InterlockMode = RoofSafetyInterlockMode.Block, OverrideRole = (string?)null });
    }

    [TestMethod]
    public void SettingsThatCannotBeUsed_OnAReload_LeaveTheLastGoodOnesInForce()
    {
        var monitor = new Mock<IOptionsMonitor<RoofSafetyOptions>>();
        var current = Options(o => o.InterlockMode = RoofSafetyInterlockMode.Block);
        var refused = false;
        monitor.SetupGet(m => m.CurrentValue).Returns(() => refused
            ? throw new OptionsValidationException("RoofSafety", typeof(RoofSafetyOptions), ["refused"])
            : current);
        using var service = new RoofSafetyService([], [], monitor.Object, _time, _logger);
        service.GetCurrent().InterlockMode.Should().Be(RoofSafetyInterlockMode.Block);

        refused = true;
        var safety = service.GetCurrent();
        service.GetCurrent();

        safety.InterlockMode.Should().Be(RoofSafetyInterlockMode.Block, "the status, and the interlock that reads it, never fail for a bad reload");
        _logger.Entries.Count(entry => entry.Level == LogLevel.Error).Should().Be(1, "it is logged once, not on every read");
        _logger.Contains(LogLevel.Error, "The RoofSafety settings cannot be used (OptionsValidationException); the last good ones stay in force (interlock Block).").Should().BeTrue();
    }

    [TestMethod]
    public async Task ALongDetail_IsCut()
    {
        var weather = new SimulatedWeatherSafetySource("Cloud sensor") { Detail = new string('x', 1000) };
        using var service = Create(weather: [weather]);

        await service.PollAsync(CancellationToken.None);

        service.GetCurrent().Sources.Single().Detail.Should().HaveLength(RoofSafetyService.MaxDetailLength);
    }

    // ---- Options ---------------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheDefaults_AreValid_AndOff()
    {
        var defaults = new RoofSafetyOptions();

        new RoofSafetyOptionsValidator().Validate(null, defaults).Succeeded.Should().BeTrue();
        defaults.InterlockMode.Should().Be(RoofSafetyInterlockMode.Off);
        defaults.OverrideRole.Should().Be(RoofSafetyOverrideRole.Operator, "anyone who may open may override: the safety check warns, it does not decide");
        default(RoofSafetyOverrideRole).Should().Be(RoofSafetyOverrideRole.Operator);
    }

    [TestMethod]
    [DataRow("InterlockMode")]
    [DataRow("OverrideRole")]
    [DataRow("PollInterval")]
    [DataRow("ReadTimeout")]
    [DataRow("MaxReadingAge")]
    public void AnUnusableSetting_IsRefused(string setting)
    {
        var options = Options(o =>
        {
            switch (setting)
            {
                case "InterlockMode": o.InterlockMode = (RoofSafetyInterlockMode)7; break;
                case "OverrideRole": o.OverrideRole = (RoofSafetyOverrideRole)7; break;
                case "PollInterval": o.PollInterval = TimeSpan.FromMilliseconds(500); break;
                case "ReadTimeout": o.ReadTimeout = TimeSpan.FromMinutes(2); break;
                case "MaxReadingAge": o.MaxReadingAge = o.PollInterval + o.ReadTimeout; break;
            }
        });

        var result = new RoofSafetyOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain($"RoofSafety:{setting}");
    }

    private RoofSafetyService Create(
        ITelescopeParkSource[]? telescopes = null,
        IWeatherSafetySource[]? weather = null,
        RoofSafetyOptions? options = null,
        TestOptionsMonitor<RoofSafetyOptions>? monitor = null)
        => new(telescopes ?? [], weather ?? [], monitor ?? new TestOptionsMonitor<RoofSafetyOptions>(options ?? Options()), _time, _logger);

    private static RoofSafetyOptions Options(Action<RoofSafetyOptions>? change = null)
    {
        var options = new RoofSafetyOptions
        {
            InterlockMode = Defaults.InterlockMode,
            OverrideRole = Defaults.OverrideRole,
            PollInterval = Defaults.PollInterval,
            ReadTimeout = Defaults.ReadTimeout,
            MaxReadingAge = Defaults.MaxReadingAge
        };
        change?.Invoke(options);
        return options;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new AssertFailedException("The condition was not met within 10 s.");
            }

            await Task.Delay(10);
        }
    }
}
