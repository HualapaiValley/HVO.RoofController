using System.Text.Json;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Client;

/// <summary>
/// The settings a caller can see, built from the catalogue and the current values, in the catalogue's groups and order.
/// Every client builds its settings pages from this, and changes a group through <see cref="Edit"/>.
/// </summary>
public sealed class RoofSettingsForm
{
    private readonly Dictionary<string, RoofSettingsFormField> _fields;

    private RoofSettingsForm(RoofSettingsResponse settings, IReadOnlyList<RoofSettingsFormGroup> groups)
    {
        Settings = settings;
        Groups = groups;
        _fields = groups.SelectMany(group => group.Fields).ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The answer the form was built from.</summary>
    public RoofSettingsResponse Settings { get; }

    /// <summary>The version to send back with a change.</summary>
    public long Version => Settings.Version;

    /// <summary>An edit of the settings file that is not in effect yet. While one is pending, changes are refused.</summary>
    public RoofSettingsHandEdit? PendingHandEdit => Settings.PendingHandEdit;

    public IReadOnlyList<RoofSettingsFormGroup> Groups { get; }

    public IEnumerable<RoofSettingsFormField> Fields => Groups.SelectMany(group => group.Fields);

    /// <summary>Builds the form. Settings the catalogue lists but the answer does not are shown read-only, without a value.</summary>
    public static RoofSettingsForm Create(RoofSettingsCatalogueResponse catalogue, RoofSettingsResponse settings)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(settings);
        var states = settings.Settings.ToDictionary(state => state.Key, StringComparer.OrdinalIgnoreCase);
        var groups = catalogue.Groups
            .Select(group => new RoofSettingsFormGroup(
                group,
                group.Settings.Select(setting => new RoofSettingsFormField(setting, states.GetValueOrDefault(setting.Key))).ToArray()))
            .ToArray();
        return new RoofSettingsForm(settings, groups);
    }

    /// <summary>The field with this key (the full configuration key; case is ignored), or null.</summary>
    public RoofSettingsFormField? FindField(string key) => _fields.GetValueOrDefault(key);

    /// <summary>The group with this name, or null.</summary>
    public RoofSettingsFormGroup? FindGroup(string name)
        => Groups.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts a change to one group.</summary>
    public RoofSettingsEdit Edit(string group)
        => new(this, FindGroup(group) ?? throw new ArgumentException($"There is no settings group '{group}'.", nameof(group)));
}

/// <summary>One group of settings.</summary>
public sealed class RoofSettingsFormGroup
{
    internal RoofSettingsFormGroup(RoofSettingsGroupDescriptor group, IReadOnlyList<RoofSettingsFormField> fields)
    {
        Name = group.Name;
        Title = group.Title;
        Description = group.Description;
        Fields = fields;
    }

    /// <summary>The group's name in the route, for example <c>roof</c>.</summary>
    public string Name { get; }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<RoofSettingsFormField> Fields { get; }

    /// <summary>True when the caller may change at least one setting in the group.</summary>
    public bool CanWrite => Fields.Any(candidate => candidate.CanWrite);
}

/// <summary>One setting: what the catalogue says about it, and its current state.</summary>
public sealed class RoofSettingsFormField
{
    internal RoofSettingsFormField(RoofSettingDescriptor setting, RoofSettingState? state)
    {
        Setting = setting;
        State = state;
        Label = RoofSettingValues.GetLabel(setting.Key);
    }

    public RoofSettingDescriptor Setting { get; }

    /// <summary>The current state, or null when the controller did not return it.</summary>
    public RoofSettingState? State { get; }

    /// <summary>The full configuration key, for example <c>RoofControllerOptionsV4:SafetyWatchdogTimeout</c>.</summary>
    public string Key => Setting.Key;

    /// <summary>A short name for the setting, made from its key.</summary>
    public string Label { get; }

    public string Description => Setting.Description;

    /// <summary>The value in effect; null for a secret and for a null value.</summary>
    public JsonElement? Value => State?.Value is { ValueKind: not JsonValueKind.Null } value ? value : null;

    /// <summary>True when the caller may change it now.</summary>
    public bool CanWrite => State?.CanWrite ?? false;

    /// <summary>Why the caller may not change it.</summary>
    public string? ReadOnlyReason => CanWrite ? null : State?.ReadOnlyReason ?? "The controller did not return this setting.";

    /// <summary>
    /// A change needs a local credential: a configured admin key marked <c>Local</c>, or an admin PIN at a local kiosk.
    /// </summary>
    public bool NeedsLocalCredential => Setting.LocalOnly;

    /// <summary>The value for display, with its unit; for a secret, only whether it is set.</summary>
    public string DisplayValue => Setting.Secret
        ? State?.IsSet == true ? RoofSettingValues.SecretSet : RoofSettingValues.SecretNotSet
        : RoofSettingValues.Describe(Setting, Value);

    /// <summary>
    /// The value as text to edit. Empty for a secret, which is never returned; <see cref="RoofSettingsEdit.TrySet"/> keeps a
    /// secret whose text is left empty.
    /// </summary>
    public string EditText => Setting.Secret ? string.Empty : RoofSettingValues.Format(Setting, Value);

    /// <summary>The default value for display.</summary>
    public string DefaultValue => Setting.Secret ? string.Empty : RoofSettingValues.Describe(Setting, Setting.Default);

    /// <summary>Parses text typed for this setting. See <see cref="RoofSettingValues.TryParse"/>.</summary>
    public bool TryParse(string? text, out JsonElement value, out string? error) => RoofSettingValues.TryParse(Setting, text, out value, out error);

    /// <summary>True when <paramref name="value"/> would change the setting. Sending a secret always counts as a change.</summary>
    public bool IsChangedBy(JsonElement value) => Setting.Secret || !JsonElement.DeepEquals(value, Value ?? RoofSettingValues.Null);

    /// <summary>
    /// True when changing the setting to <paramref name="value"/> needs <c>ConfirmSafetyCriticalChange</c>: every change of
    /// an <see cref="RoofSettingSafety.Always"/> setting, and turning a <see cref="RoofSettingSafety.WhenTurnedOff"/> one off.
    /// </summary>
    public bool NeedsConfirmation(JsonElement value) => IsChangedBy(value) && Setting.Safety switch
    {
        RoofSettingSafety.Always => true,
        RoofSettingSafety.WhenTurnedOff => value.ValueKind == JsonValueKind.Null,
        _ => false
    };
}

/// <summary>
/// A change to one settings group. Set the values that change; <see cref="ToRequest"/> sends them with the current value
/// of every other setting the caller may change, as the controller requires.
/// </summary>
public sealed class RoofSettingsEdit
{
    private readonly RoofSettingsForm _form;
    private readonly Dictionary<string, JsonElement> _changes = new(StringComparer.OrdinalIgnoreCase);

    internal RoofSettingsEdit(RoofSettingsForm form, RoofSettingsFormGroup group)
    {
        _form = form;
        Group = group;
    }

    public RoofSettingsFormGroup Group { get; }

    /// <summary>The changed settings and their new values.</summary>
    public IReadOnlyDictionary<string, JsonElement> Changes => _changes;

    public bool HasChanges => _changes.Count > 0;

    /// <summary>The changed settings that need <c>ConfirmSafetyCriticalChange</c>.</summary>
    public IReadOnlyList<RoofSettingsFormField> SafetyCriticalChanges
        => ChangedFields.Where(changed => changed.NeedsConfirmation(_changes[changed.Key])).ToArray();

    /// <summary>True when the change must be confirmed as safety-critical.</summary>
    public bool NeedsConfirmation => SafetyCriticalChanges.Count > 0;

    /// <summary>True when the change includes a local-only setting.</summary>
    public bool NeedsLocalCredential => ChangedFields.Any(changed => changed.NeedsLocalCredential);

    /// <summary>True when part of the change takes effect only after the controller restarts.</summary>
    public bool NeedsRestart => ChangedFields.Any(changed => changed.Setting.AppliesAfterRestart);

    private IEnumerable<RoofSettingsFormField> ChangedFields => Group.Fields.Where(candidate => _changes.ContainsKey(candidate.Key));

    /// <summary>
    /// Parses and sets a value. Returns false with a message when the text is not a value the setting can take. Empty
    /// text for a secret leaves the secret as it is, because a form shows every secret as an empty box; use
    /// <see cref="ClearSecret"/> to clear one.
    /// </summary>
    public bool TrySet(string key, string? text, out string? error)
    {
        var field = GetWritableField(key, out error);
        if (field is null)
        {
            return false;
        }

        if (field.Setting.Secret && string.IsNullOrWhiteSpace(text))
        {
            _changes.Remove(field.Key);
            return true;
        }

        if (!field.TryParse(text, out var value, out error))
        {
            return false;
        }

        Apply(field, value);
        return true;
    }

    /// <summary>Clears a secret (sends null). Throws <see cref="ArgumentException"/> when the setting is not a secret, or must have a value.</summary>
    public void ClearSecret(string key)
    {
        var field = GetWritableField(key, out var error) ?? throw new ArgumentException(error, nameof(key));
        if (!field.Setting.Secret)
        {
            throw new ArgumentException($"{field.Label} is not a secret.", nameof(key));
        }

        error = RoofSettingValues.Validate(field.Setting, RoofSettingValues.Null);
        if (error is not null)
        {
            throw new ArgumentException($"{field.Label}: {error}", nameof(key));
        }

        _changes[field.Key] = RoofSettingValues.Null;
    }

    /// <summary>Sets a wire value. Throws <see cref="ArgumentException"/> when the setting cannot take it.</summary>
    public void Set(string key, JsonElement value)
    {
        var field = GetWritableField(key, out var error) ?? throw new ArgumentException(error, nameof(key));
        error = RoofSettingValues.Validate(field.Setting, value);
        if (error is not null)
        {
            throw new ArgumentException($"{field.Label}: {error}", nameof(value));
        }

        Apply(field, value.Clone());
    }

    /// <summary>Drops the change to a setting. Returns false when it was not changed.</summary>
    public bool Reset(string key) => _changes.Remove(key);

    /// <summary>
    /// The request to send: the changes, and the current value of every other setting in the group the caller may change.
    /// Secrets are sent only when changed.
    /// </summary>
    public RoofSettingsUpdateRequest ToRequest(bool confirmSafetyCriticalChange = false)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var field in Group.Fields)
        {
            if (_changes.TryGetValue(field.Key, out var changed))
            {
                values[field.Key] = changed;
            }
            else if (field.CanWrite && !field.Setting.Secret)
            {
                values[field.Key] = field.Value ?? RoofSettingValues.Null;
            }
        }

        return new RoofSettingsUpdateRequest
        {
            ExpectedVersion = _form.Version,
            ConfirmSafetyCriticalChange = confirmSafetyCriticalChange,
            Values = values
        };
    }

    private RoofSettingsFormField? GetWritableField(string key, out string? error)
    {
        var field = Group.Fields.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            error = _form.FindField(key) is { } other
                ? $"{other.Label} is in the {other.Setting.Group} group, not {Group.Name}."
                : $"There is no setting '{key}'.";
            return null;
        }

        error = field.CanWrite ? null : $"{field.Label} cannot be changed: {field.ReadOnlyReason}";
        return field.CanWrite ? field : null;
    }

    private void Apply(RoofSettingsFormField field, JsonElement value)
    {
        if (field.IsChangedBy(value))
        {
            _changes[field.Key] = value;
        }
        else
        {
            _changes.Remove(field.Key);
        }
    }
}
