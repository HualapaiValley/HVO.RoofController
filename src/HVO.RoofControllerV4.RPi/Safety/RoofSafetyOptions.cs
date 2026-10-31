using System.ComponentModel;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Safety;

/// <summary>Who may override the safety interlock in <see cref="RoofSafetyInterlockMode.Warn"/>.</summary>
public enum RoofSafetyOverrideRole
{
    /// <summary>Anyone who may open the roof (the Operator role, which admins hold too): the default.</summary>
    Operator = 0,

    /// <summary>Only a caller with the Admin role (and, for a scoped key, the configure scope): a narrower choice.</summary>
    Admin = 1
}

/// <summary>
/// The safety service (<c>RoofSafety</c> section): the sources it reads, how it reads them, and how Open treats the
/// result (docs/safety-service.md). Read from the controller's configuration; not in the settings catalogue.
/// </summary>
public sealed class RoofSafetyOptions
{
    public const string SectionName = "RoofSafety";

    public static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MinReadTimeout = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaxReadTimeout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxMaxReadingAge = TimeSpan.FromHours(1);

    /// <summary>
    /// What Open does with the safety state. Warn (the default, the owner's policy, #215) asks for an override while the
    /// conditions are not Safe; Off only reports them; Block refuses Open.
    /// </summary>
    public RoofSafetyInterlockMode InterlockMode { get; set; } = RoofSafetyInterlockMode.Warn;

    /// <summary>
    /// Who may override in <see cref="RoofSafetyInterlockMode.Warn"/>: by default anyone who may open the roof, since the
    /// owner's policy is that the safety check warns and does not decide (#207).
    /// </summary>
    public RoofSafetyOverrideRole OverrideRole { get; set; } = RoofSafetyOverrideRole.Operator;

    /// <summary>How often each source is read (1 s to 10 min).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one read may take before it counts as failed (0.1 s to 1 min).</summary>
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How old a reading may be and still count, for a source that does not set its own (more than
    /// <see cref="PollInterval"/> plus <see cref="ReadTimeout"/>, the longest gap between two reads; at most 1 h). An older
    /// reading makes its source Unknown.
    /// </summary>
    public TimeSpan MaxReadingAge { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The telescope mounts read, each an ASCOM Alpaca <c>Telescope</c> (its <c>AtPark</c>), in the order the reasons list
    /// them. Read at start: a change takes effect when the controller starts again.
    /// </summary>
    public List<RoofSafetyTelescopeOptions> Telescopes { get; set; } = [];

    /// <summary>
    /// The weather sources read, each an ASCOM Alpaca <c>SafetyMonitor</c> (its <c>IsSafe</c>), set Required or Ignored.
    /// Read at start: a change takes effect when the controller starts again.
    /// </summary>
    public List<RoofSafetyWeatherSourceOptions> WeatherSources { get; set; } = [];
}

/// <summary>
/// One ASCOM Alpaca device the safety service reads. The controller only reads it (HTTP GET): it never connects,
/// disconnects, parks or moves the device. Its address goes in the controller's local settings, never in the repository,
/// and is never logged or shown.
/// </summary>
public abstract class RoofSafetyAlpacaDeviceOptions
{
    /// <summary>The longest name a reason quotes.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The name every client's reasons quote ("Main", "Cloud sensor"). Each device of a kind needs its own.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The Alpaca device server's address: http or https, the host and the port (for example <c>http://192.0.2.10:11111</c>),
    /// with a path only when a proxy puts the Alpaca API under one. No credentials, query or fragment. The address is
    /// pinned: the controller never looks for the device elsewhere (no Alpaca discovery).
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The device's Alpaca device number on that server (0 for the first of its type).</summary>
    public int DeviceNumber { get; set; }

    /// <summary>How old this device's reading may be and still count; null takes <see cref="RoofSafetyOptions.MaxReadingAge"/>.</summary>
    public TimeSpan? MaxReadingAge { get; set; }
}

/// <summary>One telescope mount (<c>RoofSafety:Telescopes</c>), read through its ASCOM Alpaca <c>Telescope</c>.</summary>
public sealed class RoofSafetyTelescopeOptions : RoofSafetyAlpacaDeviceOptions
{
    /// <summary>
    /// True (the default) when the mount can reach the roof's path: the roof may open only while it is shown clear of the
    /// roof, which today means parked. False for a mount that cannot, which is shown but never decides the conditions.
    /// </summary>
    public bool BlocksRoof { get; set; } = true;
}

/// <summary>One weather source (<c>RoofSafety:WeatherSources</c>), read through its ASCOM Alpaca <c>SafetyMonitor</c>.</summary>
public sealed class RoofSafetyWeatherSourceOptions : RoofSafetyAlpacaDeviceOptions
{
    /// <summary>
    /// Required (the default): the roof may open only while this source has a recent reading that says safe. Ignored: shown,
    /// but it never decides the conditions (a sensor being serviced, say).
    /// </summary>
    public RoofWeatherSourceRequirement Requirement { get; set; } = RoofWeatherSourceRequirement.Required;
}

/// <summary>Refuses <see cref="RoofSafetyOptions"/> the service cannot use, so the controller does not start with them.</summary>
public sealed class RoofSafetyOptionsValidator : IValidateOptions<RoofSafetyOptions>
{
    public ValidateOptionsResult Validate(string? name, RoofSafetyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (!Enum.IsDefined(options.InterlockMode))
        {
            failures.Add($"{RoofSafetyOptions.SectionName}:{nameof(RoofSafetyOptions.InterlockMode)} must be Off, Warn or Block.");
        }

        if (!Enum.IsDefined(options.OverrideRole))
        {
            failures.Add($"{RoofSafetyOptions.SectionName}:{nameof(RoofSafetyOptions.OverrideRole)} must be Operator or Admin.");
        }

        if (options.PollInterval < RoofSafetyOptions.MinPollInterval || options.PollInterval > RoofSafetyOptions.MaxPollInterval)
        {
            failures.Add($"{RoofSafetyOptions.SectionName}:{nameof(RoofSafetyOptions.PollInterval)} must be between 1 s and 10 min.");
        }

        if (options.ReadTimeout < RoofSafetyOptions.MinReadTimeout || options.ReadTimeout > RoofSafetyOptions.MaxReadTimeout)
        {
            failures.Add($"{RoofSafetyOptions.SectionName}:{nameof(RoofSafetyOptions.ReadTimeout)} must be between 0.1 s and 1 min.");
        }

        if (options.MaxReadingAge <= options.PollInterval + options.ReadTimeout || options.MaxReadingAge > RoofSafetyOptions.MaxMaxReadingAge)
        {
            failures.Add($"{RoofSafetyOptions.SectionName}:{nameof(RoofSafetyOptions.MaxReadingAge)} must be longer than {nameof(RoofSafetyOptions.PollInterval)} plus {nameof(RoofSafetyOptions.ReadTimeout)} and at most 1 h, or a reading goes stale before the next one.");
        }

        ValidateDevices(options, nameof(RoofSafetyOptions.Telescopes), options.Telescopes, failures);
        ValidateDevices(options, nameof(RoofSafetyOptions.WeatherSources), options.WeatherSources, failures);
        for (var i = 0; i < (options.WeatherSources?.Count ?? 0); i++)
        {
            if (options.WeatherSources![i] is { } source && !Enum.IsDefined(source.Requirement))
            {
                failures.Add($"{RoofSafetyOptions.SectionName}:{nameof(RoofSafetyOptions.WeatherSources)}:{i}:{nameof(RoofSafetyWeatherSourceOptions.Requirement)} must be Required or Ignored.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>The most devices of one kind the service reads.</summary>
    public const int MaxDevicesPerKind = 16;

    /// <summary>
    /// The <c>RoofSafety</c> keys that cannot be bound, read from the raw configuration before binding: a key no setting
    /// has, and a value that is not of its setting's type. Each failure names the key and never the value, which could be
    /// a device's address put in the wrong setting. The binder would otherwise drop a whole device from its list for one
    /// such value, or repeat the value in its exception. A value is of its type when the binder's own conversion (the
    /// type's <see cref="TypeConverter"/>, invariant culture) reads it, so nothing the binder accepts is refused here (a
    /// device number of <c>0x1</c> is 1).
    /// </summary>
    public static IReadOnlyList<string> FindUnreadable(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var failures = new List<string>();
        foreach (var child in section.GetChildren())
        {
            switch (child.Key.ToUpperInvariant())
            {
                case "INTERLOCKMODE":
                    Check(child, Converts<RoofSafetyInterlockMode>, "must be Off, Warn or Block", failures);
                    break;
                case "OVERRIDEROLE":
                    Check(child, Converts<RoofSafetyOverrideRole>, "must be Operator or Admin", failures);
                    break;
                case "POLLINTERVAL" or "READTIMEOUT" or "MAXREADINGAGE":
                    Check(child, Converts<TimeSpan>, "must be a duration such as 00:00:10", failures);
                    break;
                case "TELESCOPES":
                    FindUnreadableDevices(child, "telescope", "BLOCKSROOF", Converts<bool>, "must be true or false", failures);
                    break;
                case "WEATHERSOURCES":
                    FindUnreadableDevices(child, "weather source", "REQUIREMENT", Converts<RoofWeatherSourceRequirement>, "must be Required or Ignored", failures);
                    break;
                default:
                    failures.Add($"{child.Path} is not a {RoofSafetyOptions.SectionName} setting.");
                    break;
            }
        }

        return failures;
    }

    private static void FindUnreadableDevices(
        IConfigurationSection list,
        string kind,
        string ownKey,
        Func<string, bool> ownIsValid,
        string ownMeaning,
        List<string> failures)
    {
        if (list.Value is { Length: > 0 })
        {
            failures.Add($"{list.Path} must be a list of devices, not a value.");
            return;
        }

        foreach (var device in list.GetChildren())
        {
            if (device.Value is { Length: > 0 })
            {
                failures.Add($"{device.Path} must be a {kind}: give its Name, Url and DeviceNumber.");
                continue;
            }

            foreach (var field in device.GetChildren())
            {
                var key = field.Key.ToUpperInvariant();
                if (key is "NAME" or "URL")
                {
                    Check(field, _ => true, "must be text", failures);
                }
                else if (key == "DEVICENUMBER")
                {
                    Check(field, Converts<int>, "must be a whole number", failures);
                }
                else if (key == "MAXREADINGAGE")
                {
                    Check(field, Converts<TimeSpan>, "must be a duration such as 00:05:00", failures);
                }
                else if (key == ownKey)
                {
                    Check(field, ownIsValid, ownMeaning, failures);
                }
                else
                {
                    failures.Add($"{field.Path} is not a setting of a {kind}.");
                }
            }
        }
    }

    // A value must be a value (not a section of its own) of its type; one not given (null or empty) is left to the binder.
    private static void Check(IConfigurationSection setting, Func<string, bool> isValid, string meaning, List<string> failures)
    {
        if (setting.Value is null && setting.GetChildren().Any())
        {
            failures.Add($"{setting.Path} {meaning}, not a section.");
        }
        else if (!string.IsNullOrEmpty(setting.Value) && !isValid(setting.Value))
        {
            failures.Add($"{setting.Path} {meaning}.");
        }
    }

    /// <summary>
    /// True when the configuration binder can read <paramref name="value"/> as a <typeparamref name="T"/>: it converts with
    /// the type's converter in the invariant culture. The converter's exception, which repeats the value, is dropped.
    /// </summary>
    private static bool Converts<T>(string value)
    {
        try
        {
            TypeDescriptor.GetConverter(typeof(T)).ConvertFromInvariantString(value);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException or OverflowException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>An absolute http or https address with a host, and no credentials, query or fragment.</summary>
    internal static bool IsUsableUrl(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0;

    // A failure names the setting by its index, never the address it holds.
    private static void ValidateDevices<T>(RoofSafetyOptions options, string kind, List<T>? devices, List<string> failures)
        where T : RoofSafetyAlpacaDeviceOptions
    {
        if (devices is null)
        {
            return;
        }

        if (devices.Count > MaxDevicesPerKind)
        {
            failures.Add($"{RoofSafetyOptions.SectionName}:{kind} lists {devices.Count} devices; at most {MaxDevicesPerKind} are read.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < devices.Count; i++)
        {
            var key = $"{RoofSafetyOptions.SectionName}:{kind}:{i}";
            var device = devices[i];
            if (device is null)
            {
                failures.Add($"{key} is empty: give its Name, Url and DeviceNumber.");
                continue;
            }

            var name = device.Name?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Length > RoofSafetyAlpacaDeviceOptions.MaxNameLength || name.Any(char.IsControl))
            {
                failures.Add($"{key}:{nameof(RoofSafetyAlpacaDeviceOptions.Name)} must be 1 to {RoofSafetyAlpacaDeviceOptions.MaxNameLength} characters, with no control characters.");
            }
            else if (!names.Add(name))
            {
                failures.Add($"{key}:{nameof(RoofSafetyAlpacaDeviceOptions.Name)} repeats the name of another of the {kind}: each needs its own, or their reasons cannot be told apart.");
            }

            if (!IsUsableUrl(device.Url))
            {
                failures.Add($"{key}:{nameof(RoofSafetyAlpacaDeviceOptions.Url)} must be the Alpaca device server's http or https address, such as http://192.0.2.10:11111, with no credentials, query or fragment.");
            }

            if (device.DeviceNumber < 0)
            {
                failures.Add($"{key}:{nameof(RoofSafetyAlpacaDeviceOptions.DeviceNumber)} must be 0 or more.");
            }

            if (device.MaxReadingAge is { } age
                && (age <= options.PollInterval + options.ReadTimeout || age > RoofSafetyOptions.MaxMaxReadingAge))
            {
                failures.Add($"{key}:{nameof(RoofSafetyAlpacaDeviceOptions.MaxReadingAge)} must be longer than {nameof(RoofSafetyOptions.PollInterval)} plus {nameof(RoofSafetyOptions.ReadTimeout)} and at most 1 h, or its reading goes stale before the next one.");
            }
        }
    }
}
