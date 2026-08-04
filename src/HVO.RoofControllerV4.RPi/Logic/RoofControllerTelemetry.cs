using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Collections.Generic;
using System.Threading;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Logic;

internal enum RoofSafetyStopSource
{
    Unknown,
    OpenLimitSwitch,
    ClosedLimitSwitch,
    Fault,
    Watchdog
}

internal static class RoofControllerTelemetry
{
    internal const string InstrumentationName = "HVO.RoofController.RPi";

    private static readonly ActivitySource ActivitySource = new(InstrumentationName);
    private static readonly Meter Meter = new(InstrumentationName);
    private static readonly Counter<long> CommandCounter = Meter.CreateCounter<long>("roof.controller.commands");
    private static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>("roof.controller.command.duration", unit: "s");
    private static readonly Counter<long> SafetyStopCounter = Meter.CreateCounter<long>("roof.controller.safety.stops");
    private static readonly Counter<long> LimitSwitchEventCounter = Meter.CreateCounter<long>("roof.controller.limit.switch.events");
    private static readonly Counter<long> FaultEventCounter = Meter.CreateCounter<long>("roof.controller.fault.events");
    private static RoofControllerTelemetryState _currentState = RoofControllerTelemetryState.Initial;
    private static readonly ObservableGauge<long> LimitSwitchStateGauge = Meter.CreateObservableGauge("roof.controller.limit.switch.state", ObserveLimitSwitchStates);
    private static readonly ObservableGauge<long> FaultActiveGauge = Meter.CreateObservableGauge("roof.controller.fault.active", ObserveFaultActive);
    private static readonly ObservableGauge<long> WatchdogActiveGauge = Meter.CreateObservableGauge("roof.controller.watchdog.active", ObserveWatchdogActive);
    private static readonly ObservableGauge<double> WatchdogRemainingGauge = Meter.CreateObservableGauge("roof.controller.watchdog.remaining", ObserveWatchdogRemaining, unit: "s");
    private static readonly ObservableGauge<long> AtSpeedGauge = Meter.CreateObservableGauge("roof.controller.drive.at_speed", ObserveAtSpeed);
    private static readonly ObservableGauge<long> StatusGauge = Meter.CreateObservableGauge("roof.controller.status", ObserveStatus);

    internal static Activity? StartCommand(string command)
    {
        var activity = ActivitySource.StartActivity("roof.command", ActivityKind.Internal);
        activity?.SetTag("roof.command", command);
        return activity;
    }

    internal static void CompleteCommand(Activity? activity, string command, bool succeeded, long startTimestamp)
    {
        var outcome = succeeded ? "success" : "failure";
        var duration = Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;

        activity?.SetTag("roof.outcome", outcome);
        activity?.SetStatus(succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error);

        var tags = new TagList
        {
            { "roof.command", command },
            { "roof.outcome", outcome }
        };

        CommandCounter.Add(1, tags);
        CommandDuration.Record(duration, tags);
    }

    internal static void RecordSafetyStop(RoofControllerStopReason reason, RoofSafetyStopSource source)
    {
        if (reason is not (RoofControllerStopReason.LimitSwitchReached
            or RoofControllerStopReason.SafetyWatchdogTimeout
            or RoofControllerStopReason.EmergencyStop))
        {
            return;
        }

        SafetyStopCounter.Add(1, new TagList
        {
            { "roof.stop.reason", reason.ToString() },
            { "roof.stop.source", GetSafetyStopSourceTag(source) }
        });
    }

    internal static void RecordLimitSwitchTransition(string limitSwitch, bool reached)
    {
        LimitSwitchEventCounter.Add(1, new TagList
        {
            { "roof.limit.switch", limitSwitch },
            { "roof.limit.state", reached ? "reached" : "cleared" }
        });
    }

    internal static void RecordFaultTransition(bool active)
    {
        FaultEventCounter.Add(1, new TagList
        {
            { "roof.fault.state", active ? "active" : "cleared" }
        });
    }

    internal static void RecordControllerState(
        bool openLimitReached,
        bool closedLimitReached,
        bool faultActive,
        bool atSpeed,
        bool watchdogActive,
        double? watchdogSecondsRemaining,
        RoofControllerStatus status)
    {
        Volatile.Write(ref _currentState, new RoofControllerTelemetryState(
            openLimitReached,
            closedLimitReached,
            faultActive,
            atSpeed,
            watchdogActive,
            watchdogSecondsRemaining ?? 0,
            Stopwatch.GetTimestamp(),
            status));
    }

    private static IEnumerable<Measurement<long>> ObserveLimitSwitchStates()
    {
        var state = Volatile.Read(ref _currentState);
        return
        [
            new Measurement<long>(state.OpenLimitReached ? 1 : 0, new KeyValuePair<string, object?>("roof.limit.switch", "open")),
            new Measurement<long>(state.ClosedLimitReached ? 1 : 0, new KeyValuePair<string, object?>("roof.limit.switch", "closed"))
        ];
    }

    private static Measurement<long> ObserveFaultActive()
    {
        var state = Volatile.Read(ref _currentState);
        return new Measurement<long>(state.FaultActive ? 1 : 0);
    }

    private static Measurement<long> ObserveWatchdogActive()
    {
        var state = Volatile.Read(ref _currentState);
        return new Measurement<long>(state.WatchdogActive ? 1 : 0);
    }

    private static Measurement<double> ObserveWatchdogRemaining()
    {
        var state = Volatile.Read(ref _currentState);
        var elapsed = Stopwatch.GetElapsedTime(state.ObservedTimestamp).TotalSeconds;
        var remaining = state.WatchdogActive ? Math.Max(0, state.WatchdogSecondsRemaining - elapsed) : 0;
        return new Measurement<double>(remaining);
    }

    private static Measurement<long> ObserveAtSpeed()
    {
        var state = Volatile.Read(ref _currentState);
        return new Measurement<long>(state.AtSpeed ? 1 : 0);
    }

    private static Measurement<long> ObserveStatus()
    {
        var state = Volatile.Read(ref _currentState);
        return new Measurement<long>(1, new KeyValuePair<string, object?>("roof.status", state.Status.ToString()));
    }

    private static string GetSafetyStopSourceTag(RoofSafetyStopSource source) => source switch
    {
        RoofSafetyStopSource.OpenLimitSwitch => "open-limit",
        RoofSafetyStopSource.ClosedLimitSwitch => "closed-limit",
        RoofSafetyStopSource.Fault => "fault",
        RoofSafetyStopSource.Watchdog => "watchdog",
        _ => "unknown"
    };

    private sealed record RoofControllerTelemetryState(
        bool OpenLimitReached,
        bool ClosedLimitReached,
        bool FaultActive,
        bool AtSpeed,
        bool WatchdogActive,
        double WatchdogSecondsRemaining,
        long ObservedTimestamp,
        RoofControllerStatus Status)
    {
        internal static RoofControllerTelemetryState Initial { get; } = new(
            false,
            false,
            false,
            false,
            false,
            0,
            Stopwatch.GetTimestamp(),
            RoofControllerStatus.Stopped);
    }
}