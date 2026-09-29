using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// Turns what a person types into a setting's wire value and back, with the catalogue's type, limits and allowed values,
/// so every client accepts and shows values the same way. The controller checks every change again.
/// </summary>
public static class RoofSettingValues
{
    /// <summary>Shown for a secret that is set. Its value is never returned.</summary>
    public const string SecretSet = "(set)";

    /// <summary>Shown for a secret that is not set.</summary>
    public const string SecretNotSet = "(not set)";

    /// <summary>Shown for a secret a change sets: always a change, even over a secret that is set.</summary>
    public const string SecretNewValue = "(new value)";

    /// <summary>Shown for a null value.</summary>
    public const string None = "(none)";

    private static readonly string[] TrueWords = ["true", "yes", "on", "1"];
    private static readonly string[] FalseWords = ["false", "no", "off", "0"];
    private static readonly Dictionary<string, string> Acronyms = new(StringComparer.Ordinal)
    {
        ["Api"] = "API",
        ["Http"] = "HTTP",
        ["Https"] = "HTTPS",
        ["Id"] = "ID",
        ["Ip"] = "IP",
        ["Pin"] = "PIN",
        ["Ui"] = "UI",
        ["Url"] = "URL"
    };

    /// <summary>JSON null, the value that clears a nullable setting.</summary>
    public static JsonElement Null { get; } = JsonSerializer.SerializeToElement<object?>(null);

    /// <summary>
    /// Parses <paramref name="text"/> as a value of <paramref name="setting"/>. Empty text is null for a nullable setting
    /// and an empty string for a string. Durations take seconds (<c>90</c>, <c>1.5</c>), a number with a unit (<c>500ms</c>,
    /// <c>90s</c>, <c>5m</c>, <c>2h</c>, <c>1d</c>) or <c>[d.]hh:mm:ss</c>. Returns false with a message for a value the
    /// setting cannot take.
    /// </summary>
    public static bool TryParse(RoofSettingDescriptor setting, string? text, out JsonElement value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(setting);
        value = Null;
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 && (setting.Nullable || setting.Type != RoofSettingType.String))
        {
            error = setting.Nullable ? null : "A value is required.";
            return error is null;
        }

        switch (setting.Type)
        {
            case RoofSettingType.Boolean:
                if (TrueWords.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    value = JsonSerializer.SerializeToElement(true);
                }
                else if (FalseWords.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    value = JsonSerializer.SerializeToElement(false);
                }
                else
                {
                    error = "Enter true or false.";
                    return false;
                }

                break;

            case RoofSettingType.Integer:
                if (!long.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
                {
                    error = "Enter a whole number.";
                    return false;
                }

                value = JsonSerializer.SerializeToElement(whole);
                break;

            case RoofSettingType.Number:
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                {
                    error = "Enter a number.";
                    return false;
                }

                value = JsonSerializer.SerializeToElement(number);
                break;

            case RoofSettingType.Duration:
                if (!TryParseDuration(trimmed, out var seconds))
                {
                    error = "Enter a duration in seconds (for example 90 or 1.5), with a unit (500ms, 5m, 2h), or as hh:mm:ss.";
                    return false;
                }

                value = JsonSerializer.SerializeToElement(seconds);
                break;

            case RoofSettingType.Enum:
                var allowed = setting.AllowedValues ?? [];
                var match = allowed.FirstOrDefault(candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    error = $"Enter one of: {string.Join(", ", allowed)}.";
                    return false;
                }

                value = JsonSerializer.SerializeToElement(match);
                break;

            default:
                value = JsonSerializer.SerializeToElement(text ?? string.Empty);
                break;
        }

        error = Validate(setting, value);
        return error is null;
    }

    /// <summary>
    /// Checks a wire value against the setting's type, limits and allowed values. Returns null when it may be sent, or
    /// why not.
    /// </summary>
    public static string? Validate(RoofSettingDescriptor setting, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return setting.Nullable ? null : "A value is required.";
        }

        switch (setting.Type)
        {
            case RoofSettingType.Boolean:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "Enter true or false.";

            case RoofSettingType.Integer:
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var whole)
                    ? CheckRange(setting, whole)
                    : "Enter a whole number.";

            case RoofSettingType.Number:
            case RoofSettingType.Duration:
                return value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble())
                    ? CheckRange(setting, value.GetDouble())
                    : setting.Type == RoofSettingType.Duration ? "Enter a duration in seconds." : "Enter a number.";

            case RoofSettingType.Enum:
                return value.ValueKind == JsonValueKind.String
                    && (setting.AllowedValues ?? []).Contains(value.GetString(), StringComparer.Ordinal)
                        ? null
                        : $"Enter one of: {string.Join(", ", setting.AllowedValues ?? [])}.";

            default:
                if (value.ValueKind != JsonValueKind.String)
                {
                    return "Enter text.";
                }

                return setting.Maximum is { } maximum && value.GetString()!.Length > maximum
                    ? $"Enter at most {Format(maximum)} characters."
                    : null;
        }
    }

    /// <summary>
    /// The value as a person would type it: <c>true</c>, <c>42</c>, <c>1.5</c> (a duration in seconds) or the text. Null
    /// is an empty string.
    /// </summary>
    public static string Format(RoofSettingDescriptor setting, JsonElement? value)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (value is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => element.TryGetInt64(out var whole) ? whole.ToString(CultureInfo.InvariantCulture) : Format(element.GetDouble()),
            JsonValueKind.String => element.GetString()!,
            _ => element.GetRawText()
        };
    }

    /// <summary>
    /// The value for display: <see cref="Format"/> with the unit, a duration also as <c>hh:mm:ss</c> when it is a minute
    /// or longer, and <see cref="None"/> for null.
    /// </summary>
    public static string Describe(RoofSettingDescriptor setting, JsonElement? value)
    {
        var text = Format(setting, value);
        if (text.Length == 0)
        {
            return None;
        }

        if (setting.Type == RoofSettingType.Duration && value!.Value.ValueKind == JsonValueKind.Number)
        {
            var seconds = value.Value.GetDouble();
            return seconds >= 60 && seconds < TimeSpan.MaxValue.TotalSeconds
                ? $"{text} s ({TimeSpan.FromSeconds(seconds).ToString("c", CultureInfo.InvariantCulture)})"
                : $"{text} s";
        }

        return string.IsNullOrEmpty(setting.Unit) || setting.Type == RoofSettingType.String ? text : $"{text} {setting.Unit}";
    }

    /// <summary>Parses a duration: seconds, a number with a unit (ms, s, m, min, h, d), or <c>[d.]hh:mm:ss[.fff]</c>.</summary>
    public static bool TryParseDuration(string? text, out double seconds)
    {
        seconds = 0;
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.Count(c => c == ':') == 2)
        {
            if (!TimeSpan.TryParseExact(trimmed, ["c", "g"], CultureInfo.InvariantCulture, out var span) || span < TimeSpan.Zero)
            {
                return false;
            }

            seconds = span.TotalSeconds;
            return true;
        }

        var split = trimmed.Length;
        while (split > 0 && char.IsLetter(trimmed[split - 1]))
        {
            split--;
        }

        var scale = trimmed[split..].ToLowerInvariant() switch
        {
            "" or "s" or "sec" => 1d,
            "ms" => 0.001,
            "m" or "min" => 60d,
            "h" => 3600d,
            "d" => 86400d,
            _ => double.NaN
        };
        if (double.IsNaN(scale)
            || !double.TryParse(trimmed[..split].TrimEnd(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || !double.IsFinite(amount * scale))
        {
            return false;
        }

        seconds = amount * scale;
        return true;
    }

    /// <summary>
    /// A label for a setting's key: its last part in words (<c>SafetyWatchdogTimeout</c> is "Safety watchdog timeout"),
    /// or, when the last part is not a name, the part before it with the last part in brackets (<c>Log level (Default)</c>).
    /// </summary>
    public static string GetLabel(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        var parts = key.Split(':');
        var last = parts[^1];
        if (parts.Length > 1 && (last == "Default" || !last.All(char.IsLetterOrDigit)))
        {
            return $"{Humanize(parts[^2])} ({last})";
        }

        return Humanize(last);
    }

    private static string Humanize(string name)
    {
        var words = new List<string>();
        var start = 0;
        for (var i = 1; i <= name.Length; i++)
        {
            if (i == name.Length || (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) || (char.IsDigit(name[i]) != char.IsDigit(name[i - 1])))
            {
                words.Add(name[start..i]);
                start = i;
            }
        }

        var text = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (Acronyms.TryGetValue(word, out var acronym))
            {
                word = acronym;
            }
            else if (i > 0 && !word.All(char.IsUpper))
            {
                word = word.ToLowerInvariant();
            }

            text.Append(i == 0 ? string.Empty : " ").Append(word);
        }

        return text.ToString();
    }

    private static string? CheckRange(RoofSettingDescriptor setting, double value)
    {
        var unit = string.IsNullOrEmpty(setting.Unit) ? string.Empty : " " + setting.Unit;
        if (setting.Minimum is { } minimum && value < minimum)
        {
            return $"Enter at least {Format(minimum)}{unit}.";
        }

        return setting.Maximum is { } maximum && value > maximum ? $"Enter at most {Format(maximum)}{unit}." : null;
    }

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}
