using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Controllers.Camera;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Settings;

/// <summary>
/// Every setting the API can read and change. Deployment settings (paths, listeners, certificates, allowed hosts,
/// telemetry, the HAT emulator) and API keys are not here: they are set where the controller is deployed, and API keys
/// through the identity endpoints.
/// </summary>
internal static class RoofSettingsCatalogue
{
    private const string Admin = RoofControllerApiContract.AdminRole;
    private const string Operator = RoofControllerApiContract.OperatorRole;
    private const string Viewer = RoofControllerApiContract.ViewerRole;

    private static readonly string[] LogLevels = Enum.GetNames<LogLevel>();

    // AUDIT entries for changes that are not safety-critical are written at Information.
    private static readonly string[] AuditedLogLevels = [nameof(LogLevel.Trace), nameof(LogLevel.Debug), nameof(LogLevel.Information)];

    /// <summary>Groups in display order: (name, title, description).</summary>
    public static IReadOnlyList<(string Name, string Title, string Description)> Groups { get; } =
    [
        (RoofSettingsContract.RoofGroup, "Roof",
            "Motion and safety supervision. Applied at once, and refused while the roof moves or a fault is being cleared."),
        (RoofSettingsContract.ControllerGroup, "Controller", "The controller host. Changes apply after a restart."),
        (RoofSettingsContract.CameraGroup, "Camera", "The Blue Iris camera proxy."),
        (RoofSettingsContract.SecurityGroup, "Security", "Transport and Stop rules."),
        (RoofSettingsContract.IdentityGroup, "Sign-in", "Session lifetimes, lockouts and sign-in rate limits."),
        (RoofSettingsContract.LoggingGroup, "Logging", "Log levels."),
        (RoofSettingsContract.UiGroup, "Clients", "Preferences shared by the web UI, the kiosk and the command line.")
    ];

    public static IReadOnlyList<RoofSettingDefinition> All { get; } = Build();

    private static readonly Dictionary<string, RoofSettingDefinition> ByKey =
        All.ToDictionary(definition => definition.Key, StringComparer.OrdinalIgnoreCase);

    public static RoofSettingDefinition? Find(string key) => ByKey.GetValueOrDefault(key);

    public static IReadOnlyList<RoofSettingDefinition> InGroup(string group)
        => All.Where(definition => string.Equals(definition.Group, group, StringComparison.Ordinal)).ToList();

    public static bool IsGroup(string group) => Groups.Any(entry => string.Equals(entry.Name, group, StringComparison.Ordinal));

    /// <summary>True for a section above catalogue settings, such as <c>BlueIris</c>, which an empty object leaves.</summary>
    public static bool IsSection(string key)
        => All.Any(definition => definition.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Settings an earlier version had, which a settings file it saved may still set, each with why it went. They no
    /// longer configure anything, so the file is read without them, and the next change through the API leaves them out.
    /// </summary>
    public static IReadOnlyList<(string Key, string Reason)> Retired { get; } =
    [
        (RoofControllerSecurityOptions.SectionName + ":AllowedOrigins",
            "the controller serves no pages since the web UI replaced its console; the web UI's own is RoofWeb:AllowedOrigins"),
        ("ConsoleLogBuffer:MinimumLevel", "the controller keeps no log view since the web UI replaced its console")
    ];

    /// <summary>
    /// The retired setting a key read from a settings file belongs to: the setting, an item of it (a list's
    /// <c>RoofControllerSecurity:AllowedOrigins:0</c>), or a section only it was in (an empty <c>ConsoleLogBuffer</c>).
    /// Null for any other key.
    /// </summary>
    public static string? FindRetired(string key)
    {
        foreach (var (retired, _) in Retired)
        {
            if (string.Equals(key, retired, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(retired + ":", StringComparison.OrdinalIgnoreCase)
                || (retired.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase) && !IsSection(key)))
            {
                return retired;
            }
        }

        return null;
    }

    /// <summary>What to tell an admin about the retired settings a settings file sets; null when it sets none.</summary>
    public static string? DescribeRetired(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
        {
            return null;
        }

        var settings = keys.Select(key => $"{key} ({Retired.FirstOrDefault(entry => entry.Key == key).Reason ?? "retired"})");
        return $"The settings file sets {string.Join(" and ", settings)}, which this version no longer uses: ignored, " +
            "and left out of the file at the next change through the API.";
    }

    /// <summary>
    /// True for a key that holds a secret: a secret setting, an API key, or any key whose last segment is
    /// <c>Key</c> or <c>Password</c> (certificate passwords). Such keys never belong in the settings file.
    /// </summary>
    public static bool IsSecretKey(string key)
    {
        if (Find(key) is { Secret: true })
        {
            return true;
        }

        var name = key[(key.LastIndexOf(':') + 1)..];
        return string.Equals(name, "Key", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Password", StringComparison.OrdinalIgnoreCase);
    }

    private static List<RoofSettingDefinition> Build()
    {
        var roof = new RoofControllerOptionsV4();
        var host = new RoofControllerHostOptionsV4();
        var camera = new BlueIrisOptions();
        var identity = new RoofIdentityOptions();
        var ui = new RoofControllerUiOptions();
        var list = new List<RoofSettingDefinition>();

        // Roof: the running service's options (RoofControllerOptionsV4).
        Roof(nameof(roof.SafetyWatchdogTimeout), RoofSettingType.Duration,
            "Longest continuous run in either direction before the safety watchdog stops the roof. This is the absolute movement cap.",
            min: RoofControllerLimits.MinSafetyWatchdogTimeoutSeconds, max: RoofControllerLimits.MaxSafetyWatchdogTimeoutSeconds, unit: "s");
        Roof(nameof(roof.OpenRelayId), RoofSettingType.Integer, "Relay that runs the drive forward (open).", min: 1, max: 4, safety: RoofSettingSafety.Always);
        Roof(nameof(roof.CloseRelayId), RoofSettingType.Integer, "Relay that runs the drive in reverse (close).", min: 1, max: 4, safety: RoofSettingSafety.Always);
        Roof(nameof(roof.ClearFaultRelayId), RoofSettingType.Integer, "Relay pulsed to clear a drive fault.", min: 1, max: 4, safety: RoofSettingSafety.Always);
        Roof(nameof(roof.StopRelayId), RoofSettingType.Integer, "Relay of the fail-safe stop and enable path.", min: 1, max: 4, safety: RoofSettingSafety.Always);
        Roof(nameof(roof.EnableDigitalInputPolling), RoofSettingType.Boolean,
            "Poll the limit and fault inputs in the background. Polling, periodic verification or both must be on.");
        Roof(nameof(roof.DigitalInputPollInterval), RoofSettingType.Duration, "Time between input polls.",
            min: RoofControllerLimits.MinDigitalInputPollIntervalMilliseconds / 1000, max: RoofControllerLimits.MaxDigitalInputPollIntervalMilliseconds / 1000, unit: "s");
        Roof(nameof(roof.EnablePeriodicVerificationWhileMoving), RoofSettingType.Boolean,
            "Read the inputs directly on a schedule while the roof moves, to catch missed input changes.");
        Roof(nameof(roof.PeriodicVerificationInterval), RoofSettingType.Duration, "Time between direct input reads while the roof moves.",
            min: RoofControllerLimits.MinPeriodicVerificationIntervalMilliseconds / 1000, max: RoofControllerLimits.MaxPeriodicVerificationIntervalMilliseconds / 1000, unit: "s");
        Roof(nameof(roof.UseNormallyClosedLimitSwitches), RoofSettingType.Boolean,
            "True for normally closed limit switches (an open circuit means the limit is reached). Must match the wiring.",
            safety: RoofSettingSafety.Always);
        Roof(nameof(roof.LimitSwitchDebounce), RoofSettingType.Duration,
            "How long a departing limit must stay released before its return counts as the roof coming back. Never delays a stop.",
            min: 0, max: RoofControllerLimits.MaxLimitSwitchDebounceMilliseconds / 1000, unit: "s");
        Roof(nameof(roof.IgnorePhysicalLimitSwitches), RoofSettingType.Boolean,
            "Ignore the limit switches (development only). Refused on physical hardware unless the local-only consent below is on.",
            safety: RoofSettingSafety.Always);
        Roof(nameof(roof.AllowIgnoringLimitSwitchesOnPhysicalHardware), RoofSettingType.Boolean,
            "Consent to ignoring the limit switches on physical hardware. Local credential only.",
            safety: RoofSettingSafety.Always, localOnly: true);
        Roof(nameof(roof.FaultInputActiveHigh), RoofSettingType.Boolean,
            "True when a high level on the drive fault input (IN3) means a fault. Must match the wiring.", safety: RoofSettingSafety.Always);
        Roof(nameof(roof.MaxConsecutiveInputReadFailures), RoofSettingType.Integer,
            "Failed input reads in a row tolerated while moving before a safety stop.",
            min: RoofControllerLimits.MinConsecutiveInputReadFailures, max: RoofControllerLimits.MaxConsecutiveInputReadFailures);
        Roof(nameof(roof.OperatorLeaseTimeout), RoofSettingType.Duration,
            "Motion stops unless a client renews the operator lease within this time. Null turns the lease off.",
            min: RoofControllerLimits.MinOperatorLeaseSeconds, max: RoofControllerLimits.MaxOperatorLeaseSeconds, unit: "s",
            nullable: true, safety: RoofSettingSafety.WhenTurnedOff);
        Roof(nameof(roof.AtSpeedConfirmationTimeout), RoofSettingType.Duration,
            "Motion stops unless the drive reports running (IN4) within this time. Null turns the drive run interlock off.",
            min: RoofControllerLimits.MinAtSpeedConfirmationSeconds, max: RoofControllerLimits.MaxAtSpeedConfirmationSeconds, unit: "s",
            nullable: true, safety: RoofSettingSafety.WhenTurnedOff);
        Roof(nameof(roof.DriveStopConfirmationTimeout), RoofSettingType.Duration,
            "Time allowed for the drive to stop reporting running after a stop. Null uses the at-speed time. Local credential only.",
            min: RoofControllerLimits.MinDriveStopConfirmationSeconds, max: RoofControllerLimits.MaxDriveStopConfirmationSeconds, unit: "s",
            nullable: true, safety: RoofSettingSafety.WhenTurnedOff, localOnly: true);
        Roof(nameof(roof.DepartureReleaseTimeout), RoofSettingType.Duration,
            "Motion stops unless the starting limit releases within this time (a jammed or reversed roof). Null turns it off. Local credential only.",
            min: RoofControllerLimits.MinDepartureReleaseSeconds, max: RoofControllerLimits.MaxDepartureReleaseSeconds, unit: "s",
            nullable: true, safety: RoofSettingSafety.WhenTurnedOff, localOnly: true);

        // Controller host.
        Add(nameof(RoofControllerHostOptionsV4) + ":" + nameof(host.RestartOnFailureWaitTime), RoofSettingsContract.ControllerGroup,
            RoofSettingType.Integer, "Seconds to wait before retrying when the roof service fails to start.",
            host.RestartOnFailureWaitTime, min: 1, max: 3600, unit: "s", afterRestart: true);
        Add(nameof(RoofControllerHostOptionsV4) + ":" + nameof(host.ControllerName), RoofSettingsContract.ControllerGroup,
            RoofSettingType.String, "Name the clients show for this controller.", host.ControllerName, max: 64, afterRestart: true);

        // Camera.
        const string cameraSection = BlueIrisOptions.SectionName + ":";
        Add(cameraSection + nameof(camera.BaseUrl), RoofSettingsContract.CameraGroup, RoofSettingType.String,
            "Blue Iris server address, for example http://192.168.0.4:80. Null turns the camera proxy off.", camera.BaseUrl,
            nullable: true, max: 256);
        Add(cameraSection + nameof(camera.UserName), RoofSettingsContract.CameraGroup, RoofSettingType.String,
            "Blue Iris user. Write-only; set together with the password.", null, nullable: true, secret: true, max: 256);
        Add(cameraSection + nameof(camera.Password), RoofSettingsContract.CameraGroup, RoofSettingType.String,
            "Blue Iris password. Write-only; set together with the user.", null, nullable: true, secret: true, max: 256);
        Add(cameraSection + nameof(camera.ResponseHeadersTimeout), RoofSettingsContract.CameraGroup, RoofSettingType.Duration,
            "Time allowed for Blue Iris to answer a stream request.", camera.ResponseHeadersTimeout, min: 1, max: 120, unit: "s");
        Add(cameraSection + nameof(camera.StreamIdleTimeout), RoofSettingsContract.CameraGroup, RoofSettingType.Duration,
            "A stream that delivers nothing for this long is closed.", camera.StreamIdleTimeout, min: 1, max: 600, unit: "s");
        Add(cameraSection + nameof(camera.MaxConcurrentStreams), RoofSettingsContract.CameraGroup, RoofSettingType.Integer,
            "Most camera streams at once; more get 503.", camera.MaxConcurrentStreams, min: 1, max: 32, afterRestart: true);
        Add(cameraSection + nameof(camera.ConnectTimeout), RoofSettingsContract.CameraGroup, RoofSettingType.Duration,
            "Time allowed to connect to Blue Iris.", camera.ConnectTimeout, min: 1, max: 60, unit: "s", afterRestart: true);

        // Security.
        const string securitySection = RoofControllerSecurityOptions.SectionName + ":";
        Add(securitySection + nameof(RoofControllerSecurityOptions.AllowAnonymousStop), RoofSettingsContract.SecurityGroup,
            RoofSettingType.Boolean, "Accept Stop without credentials. Stop never starts motion, but anyone who can reach the controller could interrupt an operator.",
            false, safety: RoofSettingSafety.Always);
        Add(securitySection + nameof(RoofControllerSecurityOptions.RequireHttps), RoofSettingsContract.SecurityGroup,
            RoofSettingType.Boolean, "Refuse plain HTTP from other hosts. Null means on outside Development. Cannot be turned on without an HTTPS listener.",
            null, nullable: true, safety: RoofSettingSafety.Always);

        // Sign-in.
        const string identitySection = RoofIdentityOptions.SectionName + ":";
        Identity(nameof(identity.SessionLifetime), RoofSettingType.Duration, "How long a name-and-password session lasts.", identity.SessionLifetime, 60, 30 * 86400);
        Identity(nameof(identity.PinSessionIdleTimeout), RoofSettingType.Duration, "A PIN session ends after this long without a request.", identity.PinSessionIdleTimeout, 30, 86400);
        Identity(nameof(identity.PinSessionLifetime), RoofSettingType.Duration, "A PIN session ends after this long even while in use.", identity.PinSessionLifetime, 60, 30 * 86400);
        Identity(nameof(identity.LockoutThreshold), RoofSettingType.Integer, "Failed sign-ins before a lockout.", identity.LockoutThreshold, 1, 100);
        Identity(nameof(identity.LockoutDuration), RoofSettingType.Duration, "The first lockout's length; each further lockout doubles it.", identity.LockoutDuration, 1, 30 * 86400);
        Identity(nameof(identity.MaximumLockoutDuration), RoofSettingType.Duration, "The longest lockout.", identity.MaximumLockoutDuration, 1, 30 * 86400);
        Identity(nameof(identity.FailureMemory), RoofSettingType.Duration, "After this long without a failure, a name or kiosk starts again from none.", identity.FailureMemory, 1, 365 * 86400);
        Identity(nameof(identity.SignInAttemptsPerMinute), RoofSettingType.Integer, "Sign-in attempts accepted from one address per minute. 0 turns the limit off.", identity.SignInAttemptsPerMinute, 0, 10000);

        // Logging.
        Add("Logging:LogLevel:Default", RoofSettingsContract.LoggingGroup, RoofSettingType.Enum,
            "Lowest level written to the log. No higher than Information, so every AUDIT entry is written.", nameof(LogLevel.Information),
            allowed: AuditedLogLevels);
        Add("Logging:LogLevel:Microsoft.AspNetCore", RoofSettingsContract.LoggingGroup, RoofSettingType.Enum,
            "Lowest level written for the web server.", nameof(LogLevel.Warning), allowed: LogLevels);

        // Clients.
        const string uiSection = RoofControllerUiOptions.SectionName + ":";
        Add(uiSection + nameof(ui.DefaultCamera), RoofSettingsContract.UiGroup, RoofSettingType.String,
            "The camera the clients show first (its Blue Iris short name). Null shows the first camera.", ui.DefaultCamera,
            nullable: true, max: RoofControllerUiOptions.MaximumCameraNameLength, writeRole: Operator, readRole: Viewer);
        Add(uiSection + nameof(ui.KioskScreenTimeout), RoofSettingsContract.UiGroup, RoofSettingType.Duration,
            "How long the kiosk screen stays on without a touch. 0 keeps it on.", ui.KioskScreenTimeout,
            min: 0, max: RoofControllerUiOptions.MaximumKioskScreenTimeout.TotalSeconds, unit: "s", writeRole: Operator, readRole: Viewer);

        return list;

        void Roof(string name, RoofSettingType type, string description, double? min = null, double? max = null, string? unit = null,
            bool nullable = false, RoofSettingSafety safety = RoofSettingSafety.None, bool localOnly = false)
        {
            var property = typeof(RoofControllerOptionsV4).GetProperty(name)
                ?? throw new InvalidOperationException($"RoofControllerOptionsV4 has no property {name}.");
            list.Add(new RoofSettingDefinition
            {
                Key = nameof(RoofControllerOptionsV4) + ":" + name,
                Group = RoofSettingsContract.RoofGroup,
                Type = type,
                Description = description,
                Safety = safety,
                LocalOnly = localOnly,
                Nullable = nullable,
                Minimum = min,
                Maximum = max,
                Unit = unit,
                CodeDefault = property.GetValue(roof),
                RoofProperty = property
            });
        }

        void Identity(string name, RoofSettingType type, string description, object value, double min, double max)
            => Add(identitySection + name, RoofSettingsContract.IdentityGroup, type, description, value, min: min, max: max,
                unit: type == RoofSettingType.Duration ? "s" : null);

        void Add(string key, string group, RoofSettingType type, string description, object? codeDefault,
            double? min = null, double? max = null, string? unit = null, bool nullable = false, bool secret = false,
            bool afterRestart = false, RoofSettingSafety safety = RoofSettingSafety.None, IReadOnlyList<string>? allowed = null,
            string writeRole = Admin, string readRole = Admin)
            => list.Add(new RoofSettingDefinition
            {
                Key = key,
                Group = group,
                Type = type,
                Description = description,
                WriteRole = writeRole,
                ReadRole = readRole,
                Safety = safety,
                AppliesAfterRestart = afterRestart,
                Secret = secret,
                Nullable = nullable,
                Minimum = min,
                Maximum = max,
                AllowedValues = allowed,
                Unit = unit,
                CodeDefault = codeDefault
            });
    }
}
