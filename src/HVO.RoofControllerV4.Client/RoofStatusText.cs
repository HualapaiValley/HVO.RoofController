using System.Globalization;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// The words every client uses for a status's parts, so the command line, the terminal interface, the web UI and the
/// kiosk describe the roof alike. Every claim comes from a field the controller sent: a verified relay register is not
/// reported as contacts that moved, and an unknown input is shown as unknown. Times are shown in UTC, marked Z, since a
/// client and the controller may be in different time zones.
/// </summary>
public static class RoofStatusText
{
    /// <summary>The mode warning for a controller on the HAT emulator.</summary>
    public const string EmulatedHat = "EMULATED HAT: the controller drives the HAT emulator, not the physical HAT. The observatory roof does not move.";

    /// <summary>The mode warning for a controller on simulated registers.</summary>
    public const string Simulation = "SIMULATION: the controller drives simulated registers, not the HAT. The observatory roof does not move.";

    /// <summary>The mode warning for a controller that ignores the limit switches.</summary>
    public const string LimitsIgnored = "LIMITS IGNORED: the controller is ignoring the physical limit switches (development only).";

    /// <summary>The status as label and value rows, in the order every client lists them.</summary>
    public static IReadOnlyList<(string Label, string Value)> DescribeRows(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var rows = new List<(string, string)>
        {
            ("Roof", DescribeRoof(status)),
            ("Last stop", DescribeLastStop(status)),
            ("Fault", DescribeFault(status)),
            ("Limits", $"open {DescribeInput(status.IsOpenLimitActive)}, closed {DescribeInput(status.IsClosedLimitActive)}"),
            ("Drive fault", DescribeInput(status.IsDriveFaultActive)),
            ("Inputs", DescribeInputs(status)),
            ("Relays", DescribeRelays(status))
        };

        if (status.LeaseSecondsRemaining is { } lease)
        {
            rows.Add(("Lease", $"{Seconds(lease)} left"));
        }

        if (status.IsWatchdogActive && status.WatchdogSecondsRemaining is { } watchdog)
        {
            rows.Add(("Watchdog", $"{Seconds(watchdog)} left"));
        }

        rows.Add(("Controller", DescribeController(status)));
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            rows.Add(("Last error", status.LastError!));
        }

        return rows;
    }

    /// <summary>
    /// What every page warns of while the controller does not drive the observatory roof as it would in normal use: the
    /// HAT emulator or simulated registers (the roof does not move), and limit switches it ignores. Empty in normal use.
    /// </summary>
    public static IReadOnlyList<string> DescribeModeWarnings(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return DescribeModeWarnings(new RoofModeResponse(status.HatMode, status.IsIgnoringPhysicalLimitSwitches));
    }

    /// <summary>The same warnings from the controller's anonymous mode read, for a page nobody has signed in to.</summary>
    public static IReadOnlyList<string> DescribeModeWarnings(RoofModeResponse mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        var warnings = new List<string>();
        switch (mode.HatMode)
        {
            case RoofHatMode.Emulated:
                warnings.Add(EmulatedHat);
                break;
            case RoofHatMode.Simulation:
                warnings.Add(Simulation);
                break;
        }

        if (mode.IsIgnoringPhysicalLimitSwitches)
        {
            warnings.Add(LimitsIgnored);
        }

        return warnings;
    }

    /// <summary>The position, the commanded motion, and a controller that is not ready.</summary>
    public static string DescribeRoof(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var position = RoofText.DescribePosition(status.Status);
        var motion = status.CommandedMotion switch
        {
            RoofMotionDirection.Opening => ", commanded to open",
            RoofMotionDirection.Closing => ", commanded to close",
            _ => string.Empty
        };
        var state = !status.IsInitialized ? " (controller not initialized)"
            : status.IsShuttingDown ? " (controller shutting down)"
            : string.Empty;
        return position + motion + state;
    }

    /// <summary>none, clearing, LATCHED, or LATCHED with the reason.</summary>
    public static string DescribeFault(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.IsClearFaultInProgress)
        {
            return "clearing";
        }

        if (!status.IsFaultLatched)
        {
            return "none";
        }

        return status.LatchedFaultReason is { } reason ? $"LATCHED: {RoofText.DescribeStopReason(reason)}" : "LATCHED";
    }

    /// <summary>Why the roof last stopped, and when.</summary>
    public static string DescribeLastStop(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var text = RoofText.DescribeStopReason(status.LastStopReason);
        return status.LastTransitionUtc is { } at ? $"{text}, {Time(at)}" : text;
    }

    /// <summary>The controller's name, what it drives, and the snapshot.</summary>
    public static string DescribeController(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var name = string.IsNullOrWhiteSpace(status.ControllerName) ? "controller" : status.ControllerName;
        return $"{name}, {DescribeHat(status.HatMode)}, snapshot {status.StatusVersion} at {Time(status.SnapshotUtc)}";
    }

    /// <summary>What the relay register's read-back showed, and failing reads.</summary>
    public static string DescribeRelays(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var state = status.RelayRegisterState switch
        {
            RoofRelayRegisterState.Verified => "register read-back matched",
            RoofRelayRegisterState.Unverified => "UNVERIFIED: confirm at the roof that the motor has stopped",
            _ => "not read back yet"
        };
        return status.RelayRegisterReadsHealthy
            ? state
            : $"{state}; {DescribeFailures("register reads failing", status.ConsecutiveRelayReadFailures)}";
    }

    /// <summary>Whether the safety inputs (limits and drive fault) are being read.</summary>
    public static string DescribeInputs(RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.InputsHealthy ? "read OK" : DescribeFailures("reads failing", status.ConsecutiveInputReadFailures);
    }

    /// <summary>active, inactive, or unknown for an input the controller could not read.</summary>
    public static string DescribeInput(bool? active) => active switch
    {
        true => "active",
        false => "inactive",
        null => "unknown"
    };

    /// <summary>What the controller drives: the physical HAT, the HAT emulator, or simulated registers.</summary>
    public static string DescribeHat(RoofHatMode mode) => mode switch
    {
        RoofHatMode.Physical => "physical HAT",
        RoofHatMode.Emulated => "emulated HAT",
        RoofHatMode.Simulation => "simulated registers",
        _ => "HAT not reported"
    };

    /// <summary>A time as every client shows it: UTC, to the second, marked Z.</summary>
    public static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Whole seconds, never negative: "12 s".</summary>
    public static string Seconds(double seconds) => $"{Math.Max(0, seconds).ToString("0", CultureInfo.InvariantCulture)} s";

    private static string DescribeFailures(string text, int count) => count > 0 ? $"{text} ({count} in a row)" : text;
}
