using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.RoofControllerV4.Common.Models;

/// <summary>
/// Wire-level rules for remote configuration (<c>api/v4.0/Settings</c>) and the controller restart
/// (<c>POST api/v4.0/System/Restart</c>).
/// </summary>
/// <remarks>
/// <para>Values travel as JSON in the setting's type: <see cref="RoofSettingType.Boolean"/> as true or false,
/// <see cref="RoofSettingType.Integer"/> and <see cref="RoofSettingType.Number"/> as numbers,
/// <see cref="RoofSettingType.Duration"/> as a number of seconds, <see cref="RoofSettingType.String"/> and
/// <see cref="RoofSettingType.Enum"/> as strings, and <see cref="RoofSettingType.StringList"/> as an array of strings.
/// A nullable setting accepts null, which turns it off or leaves it unset.</para>
/// <para>Secret settings are write-only: they are never returned, only whether they are set.</para>
/// </remarks>
public static class RoofSettingsContract
{
    /// <summary>Base route of the settings endpoints.</summary>
    public const string SettingsRoute = "api/v{version:apiVersion}/Settings";

    /// <summary>
    /// Exit code of a controller that stopped because <c>POST System/Restart</c> asked it to (EX_TEMPFAIL): whatever
    /// supervises the process should start it again.
    /// </summary>
    public const int RestartExitCode = 75;

    /// <summary>Roof motion and safety supervision (<c>RoofControllerOptionsV4</c>). Admin.</summary>
    public const string RoofGroup = "roof";

    /// <summary>Controller host settings (<c>RoofControllerHostOptionsV4</c>). Admin.</summary>
    public const string ControllerGroup = "controller";

    /// <summary>The Blue Iris camera proxy (<c>BlueIris</c>). Admin.</summary>
    public const string CameraGroup = "camera";

    /// <summary>Transport and browser rules (<c>RoofControllerSecurity</c>). Admin.</summary>
    public const string SecurityGroup = "security";

    /// <summary>Session lifetimes and sign-in limits (<c>RoofControllerSecurity:Identity</c>). Admin.</summary>
    public const string IdentityGroup = "identity";

    /// <summary>Log levels. Admin.</summary>
    public const string LoggingGroup = "logging";

    /// <summary>Shared client preferences (<c>RoofControllerUi</c>). Operator writes, viewer reads.</summary>
    public const string UiGroup = "ui";
}

/// <summary>How a setting's value is written on the wire.</summary>
public enum RoofSettingType
{
    Boolean = 0,
    Integer = 1,
    Number = 2,
    String = 3,

    /// <summary>A duration in seconds.</summary>
    Duration = 4,

    /// <summary>One of <see cref="RoofSettingDescriptor.AllowedValues"/>.</summary>
    Enum = 5,

    /// <summary>An array of strings.</summary>
    StringList = 6
}

/// <summary>When a change to a setting is safety-critical and needs <c>ConfirmSafetyCriticalChange</c>.</summary>
public enum RoofSettingSafety
{
    /// <summary>Never.</summary>
    None = 0,

    /// <summary>Every change.</summary>
    Always = 1,

    /// <summary>Only a change to null, which turns the protection off.</summary>
    WhenTurnedOff = 2
}

/// <summary>One setting in <c>GET Settings/Catalogue</c>.</summary>
/// <param name="Key">Full configuration key, for example <c>RoofControllerOptionsV4:SafetyWatchdogTimeout</c>.</param>
/// <param name="Group">The settings group, one of the <see cref="RoofSettingsContract"/> group names.</param>
/// <param name="Type">How the value is written on the wire.</param>
/// <param name="Description">What the setting does, for people.</param>
/// <param name="WriteRole">The lowest role that may change it.</param>
/// <param name="ReadRole">The lowest role that may read it.</param>
/// <param name="Safety">When a change needs <c>ConfirmSafetyCriticalChange</c>.</param>
/// <param name="AppliesAfterRestart">True when a change takes effect only after the controller restarts.</param>
/// <param name="LocalOnly">
/// True when only a local credential may change it: a configured API key marked <c>Local</c>, or a PIN session at a
/// kiosk whose configured key is marked <c>Local</c>.
/// </param>
/// <param name="Secret">True for a write-only value, kept in the managed secrets file and never returned.</param>
/// <param name="Nullable">True when null is allowed.</param>
/// <param name="Minimum">Smallest allowed value (numbers, and durations in seconds).</param>
/// <param name="Maximum">Largest allowed value (numbers, and durations in seconds).</param>
/// <param name="AllowedValues">The allowed values of an <see cref="RoofSettingType.Enum"/>.</param>
/// <param name="Unit">Unit shown with the value, for example <c>s</c>.</param>
/// <param name="Default">The value when the settings file does not set it (null for a secret).</param>
public sealed record RoofSettingDescriptor(
    string Key,
    string Group,
    RoofSettingType Type,
    string Description,
    string WriteRole,
    string ReadRole,
    RoofSettingSafety Safety,
    bool AppliesAfterRestart,
    bool LocalOnly,
    bool Secret,
    bool Nullable,
    double? Minimum,
    double? Maximum,
    IReadOnlyList<string>? AllowedValues,
    string? Unit,
    JsonElement? Default);

/// <summary>One settings group in <c>GET Settings/Catalogue</c>.</summary>
public sealed record RoofSettingsGroupDescriptor(string Name, string Title, string Description, IReadOnlyList<RoofSettingDescriptor> Settings);

/// <summary>Body of <c>GET Settings/Catalogue</c>: the groups and settings the caller may read.</summary>
public sealed record RoofSettingsCatalogueResponse(IReadOnlyList<RoofSettingsGroupDescriptor> Groups);

/// <summary>A setting's current state in <c>GET Settings</c>.</summary>
/// <param name="Key">Full configuration key.</param>
/// <param name="Group">The settings group.</param>
/// <param name="Value">The value in effect; always null for a secret (see <paramref name="IsSet"/>).</param>
/// <param name="IsSet">True when the value is not null or empty; for a secret, whether it is set at all.</param>
/// <param name="Source">
/// Where the value comes from: <c>settings file</c>, <c>managed secrets</c>, <c>shipped defaults</c>,
/// <c>environment</c>, <c>secrets directory</c>, <c>command line</c> or <c>default</c>.
/// </param>
/// <param name="CanWrite">True when the caller may change it now.</param>
/// <param name="ReadOnlyReason">Why the caller may not change it, when <paramref name="CanWrite"/> is false.</param>
/// <param name="RestartPending">True when a saved change takes effect only after a restart.</param>
/// <param name="Problem">Why the stored value cannot be used, when it cannot.</param>
public sealed record RoofSettingState(
    string Key,
    string Group,
    JsonElement? Value,
    bool IsSet,
    string Source,
    bool CanWrite,
    string? ReadOnlyReason,
    bool RestartPending,
    string? Problem);

/// <summary>One changed setting in a pending hand edit. Both values are null for a secret.</summary>
public sealed record RoofSettingChange(string Key, JsonElement? From, JsonElement? To, bool Secret);

/// <summary>A setting whose stored value cannot be used.</summary>
public sealed record RoofSettingProblem(string Key, string Message);

/// <summary>
/// An edit made to the settings file (or the managed secrets file) outside the API since the controller loaded it. It
/// is not in effect: <c>POST Settings/Reload</c> applies it, <c>POST Settings/Discard</c> overwrites it, and until
/// then every change through the API is refused with <see cref="RoofControllerErrorCode.SettingsHandEditPending"/>.
/// </summary>
/// <param name="Token">Identifies this edit; Reload and Discard must send it back.</param>
/// <param name="Changes">Catalogue settings the edit changes.</param>
/// <param name="Problems">Values that cannot be used; the edit cannot be reloaded until they are fixed.</param>
/// <param name="FileProblem">Why the file cannot be read at all, for example invalid JSON.</param>
/// <param name="RequiresConfirmation">True when reloading needs <c>ConfirmSafetyCriticalChange</c>.</param>
/// <param name="RequiresLocalCredential">True when reloading changes a local-only setting.</param>
public sealed record RoofSettingsHandEdit(
    string Token,
    IReadOnlyList<RoofSettingChange> Changes,
    IReadOnlyList<RoofSettingProblem> Problems,
    string? FileProblem,
    bool RequiresConfirmation,
    bool RequiresLocalCredential);

/// <summary>Body of <c>GET Settings</c> and of a successful change.</summary>
/// <param name="Version">Settings version; send it back as <c>ExpectedVersion</c>.</param>
/// <param name="SavedAtUtc">When the settings were last saved through the API.</param>
/// <param name="SavedBy">Who last saved them.</param>
/// <param name="FileBacked">False when changes are kept in memory only and are lost at a restart.</param>
/// <param name="FilePath">The settings file (admins only).</param>
/// <param name="PendingHandEdit">An edit made outside the API that is not in effect yet.</param>
/// <param name="Warnings">Conditions an admin should know about.</param>
/// <param name="Settings">The settings the caller may read.</param>
public sealed record RoofSettingsResponse(
    long Version,
    DateTimeOffset? SavedAtUtc,
    string? SavedBy,
    bool FileBacked,
    string? FilePath,
    RoofSettingsHandEdit? PendingHandEdit,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<RoofSettingState> Settings);

/// <summary>
/// Body of <c>POST Settings/{group}</c>: every setting of the group the caller may change, keyed by its full key.
/// Settings the caller may not change may be left out, or sent with their current value. Secrets may be left out
/// (unchanged); null clears one.
/// </summary>
public sealed record class RoofSettingsUpdateRequest
{
    /// <summary>The <see cref="RoofSettingsResponse.Version"/> the caller last read.</summary>
    [Required]
    [JsonRequired]
    public long? ExpectedVersion { get; init; }

    /// <summary>Must be true when the change is safety-critical (see <see cref="RoofSettingDescriptor.Safety"/>).</summary>
    public bool ConfirmSafetyCriticalChange { get; init; }

    [Required]
    [JsonRequired]
    public Dictionary<string, JsonElement>? Values { get; init; }
}

/// <summary>Body of <c>POST Settings/Reload</c> and <c>POST Settings/Discard</c>.</summary>
public sealed record class RoofSettingsHandEditRequest
{
    /// <summary>The <see cref="RoofSettingsHandEdit.Token"/> the caller reviewed.</summary>
    [Required]
    [JsonRequired]
    public string? Token { get; init; }

    /// <summary>Must be true to reload an edit with a safety-critical change.</summary>
    public bool ConfirmSafetyCriticalChange { get; init; }
}

/// <summary>Optional body of <c>POST System/Restart</c>.</summary>
public sealed record class RoofRestartRequest
{
    /// <summary>
    /// Must be true when a pending hand edit, which the restart would load, has a safety-critical change.
    /// </summary>
    public bool ConfirmSafetyCriticalChange { get; init; }
}

/// <summary>Body of an accepted <c>POST System/Restart</c> (202).</summary>
/// <param name="Message">What happens next.</param>
/// <param name="ExitCode">The exit code the controller stops with.</param>
public sealed record RoofRestartResponse(string Message, int ExitCode);
