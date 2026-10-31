using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using ManualTimeProvider = HVO.RoofControllerV4.RPi.Tests.TestSupport.ManualTimeProvider;
using CapturingLogger = HVO.RoofControllerV4.RPi.Tests.TestSupport.CapturingLogger<HVO.RoofControllerV4.RPi.Safety.RoofSafetyService>;

namespace HVO.RoofControllerV4.RPi.Tests.Safety;

/// <summary>
/// The safety service against simulated sources and a manual clock: each state (Safe, Unsafe, Unknown) with its
/// reasons, the owner's readiness rule (a required weather source, every scope that can block the roof clear of it),
/// the weather alone, staleness per source, failed, hung and blocking reads, and the logged transitions.
/// </summary>
[TestClass]
public sealed class RoofSafetyServiceTests
{
    // The tests' baseline, with the interlock Off so the logged lines are the conditions' alone; the controller's own
    // default is Warn (TheDefaults_AreValid_AndWarn).
    private static readonly RoofSafetyOptions Defaults = new()
    {
        InterlockMode = RoofSafetyInterlockMode.Off,
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
        safety.InterlockMode.Should().Be(RoofSafetyInterlockMode.Off);
        safety.OverrideRole.Should().BeNull("only Warn takes an override");
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
        safety.Reasons.Should().Equal("Telescope 'Main' has not been read yet", RoofSafetyStatus.NoRequiredWeatherSourceReason);
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

    // ---- The readiness rule (#220): a required weather source, every scope that can block the roof clear ----------

    [TestMethod]
    public async Task AParkedMountAlone_IsNeverSafe_ForWantOfARequiredWeatherSource()
    {
        using var service = Create([new SimulatedTelescopeParkSource("Main")]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown, "Open counts as Safe only when a fresh weather source reports (#220, Q11)");
        safety.Reasons.Should().Equal(RoofSafetyStatus.NoRequiredWeatherSourceReason);
        safety.Reasons.Should().Equal("No required weather source configured");
        safety.Sources.Single().State.Should().Be(RoofSafetyState.Safe, "the mount itself is parked");
        safety.WeatherState.Should().Be(RoofSafetyState.Unknown);
        safety.WeatherReasons.Should().Equal(RoofSafetyStatus.NoRequiredWeatherSourceReason);
    }

    [TestMethod]
    public async Task AnUnparkedMount_WithNoRequiredWeather_IsUnsafe_AndSaysBoth()
    {
        using var service = Create([new SimulatedTelescopeParkSource("Main", isParked: false)]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unsafe);
        safety.Reasons.Should().Equal("Telescope 'Main' is not parked", RoofSafetyStatus.NoRequiredWeatherSourceReason);
    }

    [TestMethod]
    public async Task SafeWeather_WithNoTelescope_IsSafe()
    {
        using var service = Create(weather: [new SimulatedWeatherSafetySource("Cloud sensor")]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Safe, "with no scope configured, none can block the roof");
        safety.WeatherState.Should().Be(RoofSafetyState.Safe);
        safety.WeatherReasons.Should().BeEmpty();
    }

    [TestMethod]
    public async Task EveryRequiredWeatherSource_MustBeSafe_NotJustOne()
    {
        var first = new SimulatedWeatherSafetySource("Cloud sensor");
        var second = new SimulatedWeatherSafetySource("Rain sensor") { Behaviour = SimulatedSourceBehaviour.Fails };
        using var service = Create(weather: [first, second]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal("Weather monitor 'Rain sensor' cannot be read");
        safety.WeatherState.Should().Be(RoofSafetyState.Unknown);
        safety.WeatherReasons.Should().Equal("Weather monitor 'Rain sensor' cannot be read");
    }

    [TestMethod]
    public async Task AnIgnoredWeatherSource_IsShown_ButNeverDecides()
    {
        var required = new SimulatedWeatherSafetySource("Cloud sensor");
        var ignored = new SimulatedWeatherSafetySource("Old sensor", isSafe: false) { Requirement = RoofWeatherSourceRequirement.Ignored };
        var unread = new SimulatedWeatherSafetySource("Spare sensor") { Requirement = RoofWeatherSourceRequirement.Ignored, Behaviour = SimulatedSourceBehaviour.Fails };
        using var service = Create(weather: [required, ignored, unread]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Safe);
        safety.Reasons.Should().BeEmpty();
        safety.WeatherState.Should().Be(RoofSafetyState.Safe, "an ignored source is left out of the weather too");
        safety.Sources.Select(source => (source.Name, source.State, source.Counts, source.Reason)).Should().Equal(
            ("Cloud sensor", RoofSafetyState.Safe, true, "Weather monitor 'Cloud sensor' reports safe weather"),
            ("Old sensor", RoofSafetyState.Unsafe, false, "Weather monitor 'Old sensor' reports unsafe weather"),
            ("Spare sensor", RoofSafetyState.Unknown, false, "Weather monitor 'Spare sensor' cannot be read"));
    }

    [TestMethod]
    public async Task OnlyIgnoredWeatherSources_AreUnknown_ForWantOfARequiredOne()
    {
        var ignored = new SimulatedWeatherSafetySource("Cloud sensor") { Requirement = RoofWeatherSourceRequirement.Ignored };
        using var service = Create([new SimulatedTelescopeParkSource("Main")], [ignored]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown, "a source set Ignored never makes the conditions Safe");
        safety.Reasons.Should().Equal(RoofSafetyStatus.NoRequiredWeatherSourceReason);
        safety.WeatherState.Should().Be(RoofSafetyState.Unknown);
    }

    [TestMethod]
    public async Task AScopeThatCannotBlockTheRoof_IsShown_ButNeverDecides()
    {
        var main = new SimulatedTelescopeParkSource("Main");
        var solar = new SimulatedTelescopeParkSource("Solar", isParked: false) { BlocksRoof = false };
        var guide = new SimulatedTelescopeParkSource("Guide") { BlocksRoof = false, Behaviour = SimulatedSourceBehaviour.Fails };
        using var service = Create([main, solar, guide], [new SimulatedWeatherSafetySource("Cloud sensor")]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Safe, "only a scope that can block the roof must be clear of it");
        safety.Sources.Select(source => (source.Name, source.State, source.Counts)).Should().Equal(
            ("Main", RoofSafetyState.Safe, true),
            ("Solar", RoofSafetyState.Unsafe, false),
            ("Guide", RoofSafetyState.Unknown, false),
            ("Cloud sensor", RoofSafetyState.Safe, true));

        main.IsParked = false;
        await service.PollAsync(CancellationToken.None);
        service.GetCurrent().Reasons.Should().Equal("Telescope 'Main' is not parked");
    }

    [TestMethod]
    public async Task TheWeatherAlone_LeavesEveryTelescopeOut()
    {
        var mount = new SimulatedTelescopeParkSource("Main", isParked: false);
        var weather = new SimulatedWeatherSafetySource("Cloud sensor");
        using var service = Create([mount], [weather]);

        await service.PollAsync(CancellationToken.None);
        var unparked = service.GetCurrent();
        weather.IsSafe = false;
        await service.PollAsync(CancellationToken.None);
        var rain = service.GetCurrent();
        _time.Advance(TimeSpan.FromSeconds(61));
        var stale = service.GetCurrent();

        unparked.State.Should().Be(RoofSafetyState.Unsafe);
        unparked.WeatherState.Should().Be(RoofSafetyState.Safe, "an unparked mount is not weather: the Alpaca SafetyMonitor must not say unsafe for it (#220)");
        unparked.WeatherReasons.Should().BeEmpty();
        rain.WeatherState.Should().Be(RoofSafetyState.Unsafe);
        rain.WeatherReasons.Should().Equal("Weather monitor 'Cloud sensor' reports unsafe weather");
        stale.WeatherState.Should().Be(RoofSafetyState.Unknown);
        stale.WeatherReasons.Should().Equal("Weather monitor 'Cloud sensor': the last reading (unsafe) is older than 60 s");
    }

    [TestMethod]
    public void NoSources_LeaveTheWeatherUnknown_ForWantOfARequiredSource()
    {
        using var service = Create();

        var safety = service.GetCurrent();

        safety.WeatherState.Should().Be(RoofSafetyState.Unknown);
        safety.WeatherReasons.Should().Equal(RoofSafetyStatus.NoRequiredWeatherSourceReason);
    }

    [TestMethod]
    public void ParkedIsTheOnlyProof_ThatAScopeIsClearOfTheRoof()
    {
        TelescopeRoofClearance.Judge(new TelescopeParkReading(IsParked: true)).Should().Be((true, "parked"));
        TelescopeRoofClearance.Judge(new TelescopeParkReading(IsParked: false)).Should().Be((false, "not parked"));
    }

    [TestMethod]
    public async Task SourcesWithNoRequiredWeather_WarnAtStart_AndTheStartLogSaysWhichDoNotCount()
    {
        var solar = new SimulatedTelescopeParkSource("Solar") { BlocksRoof = false };
        var ignored = new SimulatedWeatherSafetySource("Cloud sensor") { Requirement = RoofWeatherSourceRequirement.Ignored };
        using var service = Create([solar], [ignored], options: Options(o => o.InterlockMode = RoofSafetyInterlockMode.Warn));

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _logger.Entries.Any(entry => entry.Message.StartsWith("Reading 2 safety source(s)", StringComparison.Ordinal)));
        await service.StopAsync(CancellationToken.None);

        _logger.Contains(LogLevel.Warning, "No safety source is a required weather source: the conditions stay Unknown, so Open needs an override until one is configured (interlock Warn).").Should().BeTrue();
        _logger.Contains(LogLevel.Information, "Reading 2 safety source(s) every 00:00:10 (read timeout 00:00:05): TelescopePark 'Solar' (does not block the roof), Weather 'Cloud sensor' (ignored).").Should().BeTrue();
    }

    // ---- Staleness -------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task AReading_CountsUntilItIsOlderThanTheMaximumAge_ThenTheSourceIsUnknown()
    {
        var mount = new SimulatedTelescopeParkSource("Main");
        using var service = Create([mount], [SteadyWeather()]);
        await service.PollAsync(CancellationToken.None);
        mount.Behaviour = SimulatedSourceBehaviour.Fails;

        _time.Advance(TimeSpan.FromSeconds(60));
        await service.PollAsync(CancellationToken.None);
        service.GetCurrent().State.Should().Be(RoofSafetyState.Safe, "a reading exactly the maximum age old still counts, though the read since failed");

        _time.Advance(TimeSpan.FromSeconds(1));
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal("Telescope 'Main': the last reading (parked) is older than 60 s");
        safety.Sources[0].IsStale.Should().BeTrue();
        safety.Sources[0].LastReadingUtc.Should().Be(_time.GetUtcNow() - TimeSpan.FromSeconds(61));
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
        using var service = Create([mount], [SteadyWeather()]);
        await service.PollAsync(CancellationToken.None);

        service.GetCurrent().Sources[0].LastReadingUtc.Should().Be(_time.GetUtcNow());
        _time.Advance(TimeSpan.FromSeconds(61));
        service.GetCurrent().State.Should().Be(RoofSafetyState.Unknown, "a future date cannot keep a reading fresh");
    }

    [TestMethod]
    public async Task ASourceReadAgain_IsCurrentAgain_AndTheRecoveryIsLogged()
    {
        var mount = new SimulatedTelescopeParkSource("Main");
        using var service = Create([mount], [SteadyWeather()]);
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
        using var service = Create([mount], [SteadyWeather()]);

        var poll = service.PollAsync(CancellationToken.None);
        // The weather's read too must have ended, or the clock's step would time it out as well.
        await WaitUntilAsync(() => mount.Reads == 1 && WeatherRead(service));
        poll.IsCompleted.Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(5));
        await poll.WaitAsync(TimeSpan.FromSeconds(10));

        service.GetCurrent().Reasons.Should().Equal("Telescope 'Main' cannot be read (no answer within 5 s)");
        _logger.Contains(LogLevel.Warning, "could not be read (no answer within 5 s)").Should().BeTrue();
    }

    [TestMethod]
    public async Task ASourceWhoseReadIgnoresCancellation_IsNotReadAgainUntilThatReadEnds()
    {
        var mount = new SimulatedTelescopeParkSource("Main") { Behaviour = SimulatedSourceBehaviour.IgnoresCancellation };
        using var service = Create([mount], [SteadyWeather()]);
        var poll = service.PollAsync(CancellationToken.None);
        await WaitUntilAsync(() => mount.Reads == 1 && WeatherRead(service));
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
        using var service = Create([mount], [SteadyWeather()]);

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
        using var service = Create([mount], [SteadyWeather()]);
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
    public async Task ASourcesLongWords_AreCut()
    {
        var mount = new WordedSource("Main") { Failure = new RoofSafetySourceException(new string('w', 1000)) };
        using var service = Create([mount], [SteadyWeather()]);

        await service.PollAsync(CancellationToken.None);

        service.GetCurrent().Reasons.Single().Should().Be($"Telescope 'Main' cannot be read ({new string('w', RoofSafetyService.MaxDetailLength)})");
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
    public void TheDefaults_AreValid_AndWarn()
    {
        var defaults = new RoofSafetyOptions();

        new RoofSafetyOptionsValidator().Validate(null, defaults).Succeeded.Should().BeTrue();
        defaults.InterlockMode.Should().Be(RoofSafetyInterlockMode.Warn, "the owner's policy: the safety check warns, and asks Open for an override (#215)");
        defaults.OverrideRole.Should().Be(RoofSafetyOverrideRole.Operator, "anyone who may open may override: the safety check warns, it does not decide");
        default(RoofSafetyOverrideRole).Should().Be(RoofSafetyOverrideRole.Operator);
    }

    [TestMethod]
    public void TheDefaults_WithNoSource_AreUnknown_AndAskOpenForAnOverrideFromAnyoneWhoMayOpen()
    {
        using var service = new RoofSafetyService([], [], new TestOptionsMonitor<RoofSafetyOptions>(new RoofSafetyOptions()), _time, _logger);

        service.GetCurrent().Should().BeEquivalentTo(new
        {
            State = RoofSafetyState.Unknown,
            InterlockMode = RoofSafetyInterlockMode.Warn,
            OverrideRole = RoofControllerApiContract.OperatorRole
        });
    }

    [TestMethod]
    public async Task ASourcesOwnWords_AreInItsReason_AndTheLog_AndGoWhenItIsReadAgain()
    {
        var mount = new WordedSource("Main") { Failure = new RoofSafetySourceException("the device is not connected") };
        using var service = Create([mount], [SteadyWeather()]);

        await service.PollAsync(CancellationToken.None);
        service.GetCurrent().Reasons.Should().Equal("Telescope 'Main' cannot be read (the device is not connected)");
        _logger.Contains(LogLevel.Warning, "Safety source TelescopePark 'Main' could not be read (the device is not connected)").Should().BeTrue();

        mount.Failure = null;
        await service.PollAsync(CancellationToken.None);
        mount.Failure = new RoofSafetySourceException("the device answered HTTP 500");
        _time.Advance(TimeSpan.FromSeconds(30));
        await service.PollAsync(CancellationToken.None);
        service.GetCurrent().Reasons.Should().BeEmpty("the reading 30 s old still counts");

        _time.Advance(TimeSpan.FromSeconds(31));
        service.GetCurrent().Reasons.Should().Equal("Telescope 'Main': the last reading (parked) is older than 60 s; the last read failed (the device answered HTTP 500)");

        mount.Failure = new IOException("cannot reach 192.0.2.7");
        await service.PollAsync(CancellationToken.None);
        // Another failure's message is not shown: it could carry the device's address.
        service.GetCurrent().Reasons.Should().Equal("Telescope 'Main': the last reading (parked) is older than 60 s");
        _logger.Entries.Should().NotContain(entry => entry.Message.Contains("192.0.2.7", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TheSources_AreNamedAtStart_NeverByAddress()
    {
        using var service = Create([new SimulatedTelescopeParkSource("Main")], [new SimulatedWeatherSafetySource("Cloud sensor")]);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => _logger.Entries.Any(entry => entry.Message.StartsWith("Reading 2 safety source(s)", StringComparison.Ordinal)));
        await service.StopAsync(CancellationToken.None);

        _logger.Contains(LogLevel.Information, "Reading 2 safety source(s) every 00:00:10 (read timeout 00:00:05): TelescopePark 'Main', Weather 'Cloud sensor'.").Should().BeTrue();
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

    private static IConfigurationSection Section(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(RoofSafetyOptions.SectionName);

    /// <summary>True once the service holds a reading of the weather source (the second source in these tests).</summary>
    private static bool WeatherRead(RoofSafetyService service) => service.GetCurrent().Sources[1].LastReadingUtc is not null;

    /// <summary>A required weather source that stays safe, with a maximum age (5 min) longer than any test's wait.</summary>
    private static SimulatedWeatherSafetySource SteadyWeather() => new("Cloud sensor", maxReadingAge: TimeSpan.FromMinutes(5));

    // ---- The configured Alpaca devices' settings --------------------------------------------------------------------

    [TestMethod]
    public void ConfiguredDevices_AreValid_AndDefaultToBlockingTheRoof_AndRequired()
    {
        var options = Options(o =>
        {
            o.Telescopes = [new RoofSafetyTelescopeOptions { Name = "Main", Url = "http://192.0.2.10:11111" }];
            o.WeatherSources = [new RoofSafetyWeatherSourceOptions { Name = "Cloud sensor", Url = "https://192.0.2.11:11111/alpaca/", DeviceNumber = 2, MaxReadingAge = TimeSpan.FromMinutes(5) }];
        });

        new RoofSafetyOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
        options.Telescopes[0].BlocksRoof.Should().BeTrue("a scope can block the roof unless its setting says it cannot (fail safe)");
        options.WeatherSources[0].Requirement.Should().Be(RoofWeatherSourceRequirement.Required, "a weather source counts unless set Ignored (fail safe)");
    }

    [TestMethod]
    [DataRow("Telescopes:0:Name", "")]
    [DataRow("Telescopes:0:Name", "a name far too long to be quoted in every reason that every client shows, as this one is")]
    [DataRow("Telescopes:1:Name", "MAIN")]
    [DataRow("Telescopes:0:Url", "ftp://198.51.100.10:21")]
    [DataRow("Telescopes:0:Url", "http://user:secret@198.51.100.10:11111")]
    [DataRow("Telescopes:0:Url", "http://198.51.100.10:11111/?device=0")]
    [DataRow("Telescopes:0:Url", "198.51.100.10:11111")]
    [DataRow("Telescopes:0:DeviceNumber", "-1")]
    [DataRow("Telescopes:0:MaxReadingAge", "00:00:15")]
    [DataRow("WeatherSources:0:Url", "")]
    [DataRow("WeatherSources:0:Requirement", "7")]
    [DataRow("WeatherSources:0:MaxReadingAge", "02:00:00")]
    public void AnUnusableDeviceSetting_IsRefused_ByItsKey_WithoutRepeatingTheAddress(string key, string value)
    {
        var telescopes = new List<RoofSafetyTelescopeOptions> { new() { Name = "Main", Url = "http://198.51.100.10:11111" }, new() { Name = "Solar", Url = "http://198.51.100.10:11111", DeviceNumber = 1 } };
        var weather = new List<RoofSafetyWeatherSourceOptions> { new() { Name = "Cloud sensor", Url = "http://198.51.100.11:11111" } };
        var parts = key.Split(':');
        RoofSafetyAlpacaDeviceOptions device = parts[0] == "Telescopes" ? telescopes[int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)] : weather[0];
        switch (parts[2])
        {
            case "Name": device.Name = value; break;
            case "Url": device.Url = value; break;
            case "DeviceNumber": device.DeviceNumber = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "MaxReadingAge": device.MaxReadingAge = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "Requirement": ((RoofSafetyWeatherSourceOptions)device).Requirement = (RoofWeatherSourceRequirement)int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
        }

        var result = new RoofSafetyOptionsValidator().Validate(null, Options(o =>
        {
            o.Telescopes = telescopes;
            o.WeatherSources = weather;
        }));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain($"RoofSafety:{key}");
        result.FailureMessage.Should().NotContain("secret").And.NotContain("198.51.100", "a failure names the setting, never the address it holds");
    }

    [TestMethod]
    public void EveryLegitimateSetting_BindsStrictly_WhateverItsKeysCase()
    {
        var section = Section(new Dictionary<string, string?>
        {
            ["RoofSafety:interlockmode"] = "block",
            ["RoofSafety:OverrideRole"] = "Admin",
            ["RoofSafety:PollInterval"] = "00:00:05",
            ["RoofSafety:ReadTimeout"] = "00:00:02",
            ["RoofSafety:MaxReadingAge"] = "00:02:00",
            ["RoofSafety:Telescopes:0:Name"] = "Main",
            ["RoofSafety:Telescopes:0:Url"] = "http://198.51.100.10:11111",
            ["RoofSafety:Telescopes:0:DeviceNumber"] = "1",
            ["RoofSafety:Telescopes:0:MaxReadingAge"] = "00:03:00",
            ["RoofSafety:Telescopes:0:blocksroof"] = "False",
            ["RoofSafety:WeatherSources:0:Name"] = "Cloud sensor",
            ["RoofSafety:WeatherSources:0:Url"] = "http://198.51.100.11:11111",
            ["RoofSafety:WeatherSources:0:requirement"] = "ignored"
        });
        var options = new RoofSafetyOptions();

        RoofSafetyOptionsValidator.FindUnreadable(section).Should().BeEmpty();
        RoofSafetyServiceCollectionExtensions.BindStrictly(section, options);

        options.Should().BeEquivalentTo(new
        {
            InterlockMode = RoofSafetyInterlockMode.Block,
            OverrideRole = RoofSafetyOverrideRole.Admin,
            Telescopes = new[] { new { Name = "Main", DeviceNumber = 1, MaxReadingAge = (TimeSpan?)TimeSpan.FromMinutes(3), BlocksRoof = false } },
            WeatherSources = new[] { new { Name = "Cloud sensor", DeviceNumber = 0, Requirement = RoofWeatherSourceRequirement.Ignored } }
        }, o => o.ExcludingMissingMembers());
    }

    [TestMethod]
    [DataRow("Telescopes:0:DeviceNumber", new[] { "1", "0x1", "&h1", "#1", "-1", " 1", "+1", "1.0", "one", "0x", "99999999999", "http://198.51.100.9:11111" })]
    [DataRow("WeatherSources:0:DeviceNumber", new[] { "1", "0x1", "&h1", "#1", "one", "http://198.51.100.9:11111" })]
    [DataRow("Telescopes:0:BlocksRoof", new[] { "true", "False", "TRUE", " true", "yes", "1", "tru", "http://198.51.100.9:11111" })]
    [DataRow("Telescopes:0:MaxReadingAge", new[] { "00:05:00", "5", "1.00:00:00", "00:05", "5m", "soon", "http://198.51.100.9:11111" })]
    [DataRow("WeatherSources:0:Requirement", new[] { "Required", "ignored", "1", "7", "Required, Ignored", "Requred", "http://198.51.100.9:11111" })]
    [DataRow("InterlockMode", new[] { "Warn", "block", "0", "Warn, Block", "Blok", "http://198.51.100.9:11111" })]
    [DataRow("OverrideRole", new[] { "Operator", "admin", "1", "Admins" })]
    [DataRow("PollInterval", new[] { "00:00:10", "10", "00:00:10.5", "ten", "http://198.51.100.9:11111" })]
    public void WhatTheScanRefuses_IsExactlyWhatStrictBindingCannotBind(string key, string[] values)
    {
        // Review F5 (#227): the scan names what the binder cannot bind; it adds no rule of its own (a device number of 0x1
        // binds as 1, so it must not be refused).
        foreach (var value in values)
        {
            var settings = new Dictionary<string, string?>
            {
                ["RoofSafety:Telescopes:0:Name"] = "Main",
                ["RoofSafety:Telescopes:0:Url"] = "http://198.51.100.10:11111",
                ["RoofSafety:WeatherSources:0:Name"] = "Cloud sensor",
                ["RoofSafety:WeatherSources:0:Url"] = "http://198.51.100.11:11111",
                [$"RoofSafety:{key}"] = value
            };
            var section = Section(settings);
            var bindable = true;
            try
            {
                section.Bind(new RoofSafetyOptions(), binder => binder.ErrorOnUnknownConfiguration = true);
            }
            catch (InvalidOperationException)
            {
                bindable = false;
            }

            RoofSafetyOptionsValidator.FindUnreadable(section).Should().HaveCount(bindable ? 0 : 1,
                "'{0}' for {1} {2} by the binder", value, key, bindable ? "is bound" : "cannot be bound");
        }
    }

    [TestMethod]
    public void WhatCannotBeBound_IsNamedByKey_NeverByValue()
    {
        const string misplaced = "http://198.51.100.99:11111";
        var section = Section(new Dictionary<string, string?>
        {
            ["RoofSafety:Telescopes:0:Name"] = "Main",
            ["RoofSafety:Telescopes:0:Url"] = "http://198.51.100.10:11111",
            ["RoofSafety:Telescopes:0:DeviceNumber"] = misplaced,
            ["RoofSafety:Telescopes:0:Requirement"] = "Required",
            ["RoofSafety:Telescopes:1"] = misplaced,
            ["RoofSafety:WeatherSources:0:MaxReadingAge:Minutes"] = "5",
            ["RoofSafety:WeatherSources:0:BlocksRoof"] = "true",
            ["RoofSafety:PollIntervall"] = "00:00:10"
        });

        var failures = RoofSafetyOptionsValidator.FindUnreadable(section);
        var bind = () => RoofSafetyServiceCollectionExtensions.BindStrictly(section, new RoofSafetyOptions());

        failures.Should().BeEquivalentTo(
            "RoofSafety:Telescopes:0:DeviceNumber must be a whole number.",
            "RoofSafety:Telescopes:0:Requirement is not a setting of a telescope.",
            "RoofSafety:Telescopes:1 must be a telescope: give its Name, Url and DeviceNumber.",
            "RoofSafety:WeatherSources:0:MaxReadingAge must be a duration such as 00:05:00, not a section.",
            "RoofSafety:WeatherSources:0:BlocksRoof is not a setting of a weather source.",
            "RoofSafety:PollIntervall is not a RoofSafety setting.");
        var refused = bind.Should().Throw<OptionsValidationException>().Which;
        refused.InnerException.Should().BeNull();
        refused.ToString().Should().NotContain("198.51.100.99");
    }

    [TestMethod]
    public void MoreDevicesThanTheServiceReads_AreRefused()
    {
        var result = new RoofSafetyOptionsValidator().Validate(null, Options(o => o.WeatherSources =
            [.. Enumerable.Range(0, RoofSafetyOptionsValidator.MaxDevicesPerKind + 1).Select(i => new RoofSafetyWeatherSourceOptions { Name = $"Sensor {i}", Url = "http://192.0.2.11:11111", DeviceNumber = i })]));

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("RoofSafety:WeatherSources lists 17 devices; at most 16 are read.");
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

    /// <summary>A mount that answers parked, or throws <see cref="Failure"/>.</summary>
    private sealed class WordedSource(string name) : ITelescopeParkSource
    {
        public string Name { get; } = name;

        public TimeSpan? MaxReadingAge => null;

        public Exception? Failure { get; set; }

        public Task<TelescopeParkReading> ReadParkStateAsync(CancellationToken cancellationToken)
            => Failure is { } failure ? Task.FromException<TelescopeParkReading>(failure) : Task.FromResult(new TelescopeParkReading(true));
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
