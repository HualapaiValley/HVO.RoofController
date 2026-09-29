using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Configuration;

namespace HVO.RoofControllerV4.RPi.Settings;

/// <summary>
/// One setting the API can read and change: its key, rules and value conversions. Values are held as
/// <see cref="bool"/>, <see cref="int"/>, <see cref="double"/>, <see cref="TimeSpan"/>, <see cref="string"/> (also an
/// enum's canonical name), a read-only list of strings, or null.
/// </summary>
internal sealed class RoofSettingDefinition
{
    /// <summary>Most items in a list setting.</summary>
    public const int MaximumListItems = 32;

    /// <summary>Longest item in a list setting.</summary>
    public const int MaximumListItemLength = 256;

    /// <summary>Longest string setting without its own maximum.</summary>
    public const int DefaultMaximumStringLength = 256;

    public required string Key { get; init; }

    public required string Group { get; init; }

    public required RoofSettingType Type { get; init; }

    public required string Description { get; init; }

    public string WriteRole { get; init; } = RoofControllerApiContract.AdminRole;

    public string ReadRole { get; init; } = RoofControllerApiContract.AdminRole;

    public RoofSettingSafety Safety { get; init; }

    public bool AppliesAfterRestart { get; init; }

    public bool LocalOnly { get; init; }

    public bool Secret { get; init; }

    public bool Nullable { get; init; }

    /// <summary>Smallest value (seconds for a duration); for a string, the shortest length.</summary>
    public double? Minimum { get; init; }

    /// <summary>Largest value (seconds for a duration); for a string, the longest length.</summary>
    public double? Maximum { get; init; }

    public IReadOnlyList<string>? AllowedValues { get; init; }

    public string? Unit { get; init; }

    /// <summary>The value when no configuration sets the key.</summary>
    public object? CodeDefault { get; init; }

    /// <summary>For the roof group: the <see cref="RoofControllerOptionsV4"/> property the service applies.</summary>
    public PropertyInfo? RoofProperty { get; init; }

    /// <summary>The last segment of the key.</summary>
    public string Name => Key[(Key.LastIndexOf(':') + 1)..];

    public bool IsList => Type == RoofSettingType.StringList;

    /// <summary>The value the running roof service uses (roof group only).</summary>
    public object? GetRoofValue(RoofControllerOptionsV4 options)
        => RoofProperty is null ? throw new InvalidOperationException($"{Key} is not a roof setting.") : RoofProperty.GetValue(options);

    /// <summary>Sets the value on roof options (roof group only).</summary>
    public void SetRoofValue(RoofControllerOptionsV4 options, object? value)
    {
        if (RoofProperty is null)
        {
            throw new InvalidOperationException($"{Key} is not a roof setting.");
        }

        RoofProperty.SetValue(options, value);
    }

    /// <summary>
    /// Reads the value from <paramref name="view"/>. Returns false with a problem (naming the setting, never the value)
    /// when the configured text cannot be used.
    /// </summary>
    public bool TryRead(RoofConfigurationView view, out object? value, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(view);
        value = CodeDefault;
        problem = null;
        if (IsList)
        {
            var items = view.GetListItems(Key);
            if (items is null)
            {
                return true;
            }

            value = items;
            problem = Check(items);
            return problem is null;
        }

        if (!view.TryGet(Key, out var raw) || raw is null)
        {
            return true;
        }

        if (!TryParseText(raw, out value, out problem))
        {
            value = CodeDefault;
            return false;
        }

        problem = Check(value);
        return problem is null;
    }

    /// <summary>Parses a value sent through the API. The problem names the setting and the rule, never the value.</summary>
    public bool TryParseJson(JsonElement element, out object? value, out string? problem)
    {
        value = null;
        problem = null;
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (!Nullable)
            {
                problem = $"{Key} cannot be null.";
                return false;
            }

            return true;
        }

        switch (Type)
        {
            case RoofSettingType.Boolean when element.ValueKind is JsonValueKind.True or JsonValueKind.False:
                value = element.GetBoolean();
                break;
            case RoofSettingType.Integer when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var integer):
                value = integer;
                break;
            case RoofSettingType.Number when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) && double.IsFinite(number):
                value = number;
                break;
            case RoofSettingType.Duration when element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var seconds) && double.IsFinite(seconds):
                // Range first, so the conversion cannot overflow.
                if (OutOfRange(seconds))
                {
                    problem = RangeProblem();
                    return false;
                }

                value = TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
                break;
            case RoofSettingType.String when element.ValueKind == JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                value = Nullable && text.Length == 0 ? null : text;
                break;
            case RoofSettingType.Enum when element.ValueKind == JsonValueKind.String:
                var name = element.GetString() ?? string.Empty;
                value = AllowedValues?.FirstOrDefault(allowed => string.Equals(allowed, name, StringComparison.OrdinalIgnoreCase));
                if (value is null)
                {
                    problem = $"{Key} must be one of {string.Join(", ", AllowedValues ?? [])}.";
                    return false;
                }

                break;
            case RoofSettingType.StringList when element.ValueKind == JsonValueKind.Array:
                var items = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        problem = $"{Key} must be an array of strings.";
                        return false;
                    }

                    items.Add(item.GetString() ?? string.Empty);
                }

                value = items;
                break;
            default:
                problem = $"{Key} must be {DescribeType()}.";
                return false;
        }

        problem = Check(value);
        return problem is null;
    }

    /// <summary>The rule a value breaks, or null. Null is allowed only for a nullable setting.</summary>
    public string? Check(object? value)
    {
        if (value is null)
        {
            return Nullable ? null : $"{Key} cannot be empty.";
        }

        switch (value)
        {
            case int integer when OutOfRange(integer):
            case double number when OutOfRange(number):
            case TimeSpan duration when OutOfRange(duration.TotalSeconds):
                return RangeProblem();
            case string text when Type == RoofSettingType.String:
                var maximum = (int)(Maximum ?? DefaultMaximumStringLength);
                if (text.Length > maximum || text.Length < (int)(Minimum ?? 0))
                {
                    return Minimum is > 0
                        ? $"{Key} must be {Minimum:0} to {maximum} characters long."
                        : $"{Key} must be at most {maximum} characters long.";
                }

                return text.Any(char.IsControl) ? $"{Key} cannot contain control characters." : null;
            case IReadOnlyList<string> items:
                if (items.Count > MaximumListItems)
                {
                    return $"{Key} can have at most {MaximumListItems} entries.";
                }

                if (items.Any(string.IsNullOrWhiteSpace))
                {
                    return $"{Key} cannot have blank entries.";
                }

                if (items.Any(item => item.Length > MaximumListItemLength || item.Any(char.IsControl)))
                {
                    return $"{Key} entries must be at most {MaximumListItemLength} characters long, without control characters.";
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>The value on the wire (null for null).</summary>
    public JsonElement? ToJson(object? value) => value switch
    {
        null => null,
        TimeSpan duration => JsonSerializer.SerializeToElement(duration.TotalSeconds),
        IReadOnlyList<string> items => JsonSerializer.SerializeToElement(items),
        _ => JsonSerializer.SerializeToElement(value, value.GetType())
    };

    /// <summary>
    /// The value as the settings file holds it. A list is written as it is: the settings file's list replaces the list
    /// below it (<see cref="RoofSettingsFileProvider.GetChildKeys"/>).
    /// </summary>
    public JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        bool flag => JsonValue.Create(flag),
        int integer => JsonValue.Create(integer),
        double number => JsonValue.Create(number),
        TimeSpan duration => JsonValue.Create(duration.ToString("c", CultureInfo.InvariantCulture)),
        string text => JsonValue.Create(text),
        IReadOnlyList<string> items => new JsonArray(items.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
        _ => throw new InvalidOperationException($"{Key} has an unexpected value type {value.GetType().Name}.")
    };

    /// <summary>The configuration entries that give this value (keys under the setting are replaced by them).</summary>
    public IEnumerable<KeyValuePair<string, string?>> ToConfigurationEntries(object? value)
    {
        if (value is IReadOnlyList<string> items)
        {
            yield return new(Key, null);
            for (var index = 0; index < items.Count; index++)
            {
                yield return new($"{Key}{ConfigurationPath.KeyDelimiter}{index.ToString(CultureInfo.InvariantCulture)}", items[index]);
            }

            yield break;
        }

        yield return new(Key, Format(value));
    }

    /// <summary>Configuration text of a value ("" for null).</summary>
    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "true" : "false",
        int integer => integer.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
        string text => text,
        _ => throw new InvalidOperationException($"Unexpected setting value type {value.GetType().Name}.")
    };

    public static bool ValuesEqual(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (IReadOnlyList<string> x, IReadOnlyList<string> y) => x.SequenceEqual(y, StringComparer.Ordinal),
        (null, _) or (_, null) => false,
        _ => a.Equals(b)
    };

    /// <summary>For audit entries: a secret is never written, only whether it is set.</summary>
    public string Describe(object? value)
    {
        if (Secret)
        {
            return value is null ? "(not set)" : "(set)";
        }

        return value switch
        {
            null => "null",
            TimeSpan duration => $"{duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s",
            _ => ToJson(value)?.GetRawText() ?? "null"
        };
    }

    public RoofSettingDescriptor ToDescriptor(object? defaultValue) => new(
        Key,
        Group,
        Type,
        Description,
        WriteRole,
        ReadRole,
        Safety,
        AppliesAfterRestart,
        LocalOnly,
        Secret,
        Nullable,
        Minimum,
        Maximum,
        AllowedValues,
        Unit,
        Secret ? null : ToJson(defaultValue));

    /// <summary>True when changing <paramref name="from"/> to <paramref name="to"/> needs confirmation.</summary>
    public bool IsSafetyCritical(object? from, object? to) => Safety switch
    {
        RoofSettingSafety.Always => !ValuesEqual(from, to),
        RoofSettingSafety.WhenTurnedOff => from is not null && to is null,
        _ => false
    };

    private bool TryParseText(string raw, out object? value, out string? problem)
    {
        value = null;
        problem = null;
        if (raw.Length == 0 && Type != RoofSettingType.String)
        {
            if (Nullable)
            {
                return true;
            }

            problem = $"{Key} is empty; remove it to use the default, or set {DescribeType()}.";
            return false;
        }

        switch (Type)
        {
            case RoofSettingType.Boolean when bool.TryParse(raw, out var flag):
                value = flag;
                return true;
            case RoofSettingType.Integer when int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                value = integer;
                return true;
            case RoofSettingType.Number when double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number):
                value = number;
                return true;
            case RoofSettingType.Duration when TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var duration):
                // The configuration binder's rules: "00:01:30" is 90 seconds, but a bare "90" is 90 days.
                value = duration;
                return true;
            case RoofSettingType.String:
                value = Nullable && raw.Length == 0 ? null : raw;
                return true;
            case RoofSettingType.Enum:
                value = AllowedValues?.FirstOrDefault(allowed => string.Equals(allowed, raw.Trim(), StringComparison.OrdinalIgnoreCase));
                if (value is not null)
                {
                    return true;
                }

                problem = $"{Key} must be one of {string.Join(", ", AllowedValues ?? [])}.";
                return false;
            default:
                problem = $"{Key} is not {DescribeType()}" +
                    (Type == RoofSettingType.Duration ? " (write a duration as hh:mm:ss, for example 00:01:30)." : ".");
                return false;
        }
    }

    private bool OutOfRange(double value) => value < (Minimum ?? double.MinValue) || value > (Maximum ?? double.MaxValue);

    private string RangeProblem()
    {
        var unit = Type == RoofSettingType.Duration ? " seconds" : Unit is null ? string.Empty : $" {Unit}";
        return (Minimum, Maximum) switch
        {
            ({ } min, { } max) => $"{Key} must be between {Number(min)} and {Number(max)}{unit}.",
            ({ } min, null) => $"{Key} must be at least {Number(min)}{unit}.",
            (null, { } max) => $"{Key} must be at most {Number(max)}{unit}.",
            _ => $"{Key} is out of range."
        };

        static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private string DescribeType() => Type switch
    {
        RoofSettingType.Boolean => "true or false",
        RoofSettingType.Integer => "a whole number",
        RoofSettingType.Number => "a number",
        RoofSettingType.Duration => "a duration in seconds",
        RoofSettingType.String => "a string",
        RoofSettingType.Enum => "one of " + string.Join(", ", AllowedValues ?? []),
        RoofSettingType.StringList => "an array of strings",
        _ => Type.ToString()
    };
}

/// <summary>
/// Configuration keys as a stack of providers sees them (top first), for reading a setting from some of the layers:
/// the layers below the settings file give a setting's base value.
/// </summary>
internal sealed class RoofConfigurationView
{
    private readonly IReadOnlyList<IConfigurationProvider> _bottomUp;
    private readonly IReadOnlyList<IConfigurationProvider> _topDown;

    /// <param name="providers">Providers in configuration order (lowest first), as <c>IConfigurationRoot.Providers</c>.</param>
    public RoofConfigurationView(IEnumerable<IConfigurationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _bottomUp = providers.ToList();
        _topDown = _bottomUp.Reverse().ToList();
    }

    public bool TryGet(string key, out string? value)
    {
        foreach (var provider in _topDown)
        {
            if (provider.TryGet(key, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// The non-blank values under <paramref name="key"/> in index order, or null when no layer sets the key or any
    /// entry under it.
    /// </summary>
    public IReadOnlyList<string>? GetListItems(string key)
    {
        var childKeys = ChildKeys(key).Order(ConfigurationKeyComparer.Instance).ToList();
        if (childKeys.Count == 0)
        {
            return TryGet(key, out _) ? Array.Empty<string>() : null;
        }

        var items = new List<string>();
        foreach (var child in childKeys)
        {
            if (TryGet(ConfigurationPath.Combine(key, child), out var value) && !string.IsNullOrWhiteSpace(value))
            {
                items.Add(value);
            }
        }

        return items;
    }

    /// <summary>
    /// Every key the layers set and its effective value, as <c>IConfiguration.AsEnumerable()</c> would list them. Used
    /// to build a candidate configuration without touching the live providers.
    /// </summary>
    public Dictionary<string, string?> Flatten()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Walk(null);
        return data;

        void Walk(string? parent)
        {
            foreach (var child in ChildKeys(parent))
            {
                var key = parent is null ? child : ConfigurationPath.Combine(parent, child);
                if (TryGet(key, out var value))
                {
                    data[key] = value;
                }

                Walk(key);
            }
        }
    }

    /// <summary>
    /// The keys directly under <paramref name="parent"/>, gathered in configuration order as <c>IConfiguration</c>
    /// does, so a layer can hide the entries below it (a list in the settings file replaces the list below).
    /// </summary>
    private List<string> ChildKeys(string? parent)
        => _bottomUp
            .Aggregate(Enumerable.Empty<string>(), (seed, provider) => provider.GetChildKeys(seed, parent))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>True when a layer sets <paramref name="key"/>, or (for a list) any entry under it.</summary>
    public static bool Sets(IConfigurationProvider provider, string key, bool includeChildren)
        => provider.TryGet(key, out _) || (includeChildren && provider.GetChildKeys([], key).Any());
}
