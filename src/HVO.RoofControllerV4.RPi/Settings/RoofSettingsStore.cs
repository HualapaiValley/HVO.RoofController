using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Controllers.Camera;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.KeyPerFile;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Settings;

/// <summary>What a settings request came to: a response, validation errors (400) or a refusal.</summary>
public sealed class RoofSettingsOutcome
{
    private RoofSettingsOutcome(
        RoofSettingsResponse? response,
        long version,
        RoofControllerErrorCode? error,
        string? detail,
        IReadOnlyList<string>? validationErrors)
    {
        Response = response;
        Version = version;
        Error = error;
        Detail = detail;
        ValidationErrors = validationErrors;
    }

    public RoofSettingsResponse? Response { get; }

    /// <summary>The settings version after the request.</summary>
    public long Version { get; }

    public RoofControllerErrorCode? Error { get; }

    public string? Detail { get; }

    public IReadOnlyList<string>? ValidationErrors { get; }

    public bool Succeeded => Error is null && ValidationErrors is null;

    public static RoofSettingsOutcome Success(RoofSettingsResponse? response, long version) => new(response, version, null, null, null);

    public static RoofSettingsOutcome Invalid(IReadOnlyList<string> errors) => new(null, 0, null, null, errors);

    public static RoofSettingsOutcome Refused(RoofControllerErrorCode code, string detail) => new(null, 0, code, detail, null);
}

/// <summary>
/// Reads and changes the settings in <see cref="RoofSettingsCatalogue"/>. A change is checked (role, local
/// credential, layers above the settings file, rules, safety-critical confirmation), applied to the roof service when
/// it is a roof setting, saved atomically to the settings file (secrets to the managed secrets file), and published
/// to the configuration so every options monitor sees it. An edit made to the files outside the API is detected,
/// shown as pending, and blocks changes until it is reloaded (checked like a change) or discarded.
/// </summary>
/// <remarks>
/// The settings and secrets providers are looked up on every call: the configuration can rebuild its providers
/// (tests add sources after the controller's own), and the store must always publish to the live ones.
/// </remarks>
public sealed class RoofSettingsStore
{
    private const string DefaultSource = "default";
    private const string CameraBaseUrlKey = BlueIrisOptions.SectionName + ":" + nameof(BlueIrisOptions.BaseUrl);

    private readonly object _gate = new();
    private readonly IConfigurationRoot _configuration;
    private readonly IRoofControllerServiceV4 _roof;
    private readonly RoofSettingsValidator _validator;
    private readonly RoofApiKeyStore _keys;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _time;
    private readonly ILogger<RoofSettingsStore> _logger;
    private readonly Dictionary<string, object?> _startupValues = new(StringComparer.OrdinalIgnoreCase);
    private string? _unsavedRoofWarning;

    public RoofSettingsStore(
        IConfiguration configuration,
        IRoofControllerServiceV4 roof,
        RoofSettingsValidator validator,
        RoofApiKeyStore keys,
        IHostEnvironment environment,
        TimeProvider time,
        ILogger<RoofSettingsStore> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration as IConfigurationRoot
            ?? throw new ArgumentException("The settings store needs the configuration root.", nameof(configuration));
        _roof = roof ?? throw new ArgumentNullException(nameof(roof));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Every change goes through this store, so the values when it is created are the ones the controller started
        // with: a setting read only at startup is pending a restart once it differs from them.
        var view = new RoofConfigurationView(_configuration.Providers);
        foreach (var definition in RoofSettingsCatalogue.All.Where(definition => definition.AppliesAfterRestart))
        {
            definition.TryRead(view, out var value, out _);
            _startupValues[definition.Key] = value;
        }
    }

    /// <summary>The settings version (1 before anything is saved).</summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return GetLayers().Settings?.Document.Version ?? 1;
            }
        }
    }

    public RoofSettingsCatalogueResponse GetCatalogue(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            var layers = GetLayers();
            var groups = RoofSettingsCatalogue.Groups
                .Select(group => new RoofSettingsGroupDescriptor(
                    group.Name,
                    group.Title,
                    group.Description,
                    RoofSettingsCatalogue.InGroup(group.Name)
                        .Where(definition => user.IsInRole(definition.ReadRole))
                        .Select(definition => definition.ToDescriptor(BaseValue(layers, definition)))
                        .ToList()))
                .Where(group => group.Settings.Count > 0)
                .ToList();
            return new RoofSettingsCatalogueResponse(groups);
        }
    }

    public RoofSettingsResponse Get(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            var layers = GetLayers();
            return BuildResponse(layers, user, DetectHandEdit(layers));
        }
    }

    /// <summary>Changes every setting of <paramref name="group"/> the request sends (<c>POST Settings/{group}</c>).</summary>
    public RoofSettingsOutcome Update(string group, RoofSettingsUpdateRequest request, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        var name = RoofSettingsCatalogue.Groups
            .Select(entry => entry.Name)
            .FirstOrDefault(entry => string.Equals(entry, group, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return RoofSettingsOutcome.Refused(
                RoofControllerErrorCode.SettingNotFound,
                $"There is no such settings group. The groups are {string.Join(", ", RoofSettingsCatalogue.Groups.Select(entry => entry.Name))}.");
        }

        var definitions = RoofSettingsCatalogue.InGroup(name);
        if (!definitions.Any(definition => user.IsInRole(definition.WriteRole)))
        {
            return RoofSettingsOutcome.Refused(
                RoofControllerErrorCode.SettingNotPermitted,
                $"Changing the {name} settings needs the {string.Join(" or ", definitions.Select(definition => definition.WriteRole).Distinct())} role.");
        }

        lock (_gate)
        {
            var layers = GetLayers();
            if (Unavailable(layers) is { } unavailable)
            {
                return unavailable;
            }

            if (Blocked(layers, request.ExpectedVersion) is { } blocked)
            {
                return blocked;
            }

            var errors = new List<string>();
            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, element) in request.Values ?? [])
            {
                if (!values.TryAdd(key, element))
                {
                    errors.Add($"{Shorten(key)} is sent more than once.");
                }
                else if (RoofSettingsCatalogue.Find(key) is not { } definition || definition.Group != name)
                {
                    errors.Add($"{Shorten(key)} is not a setting of the {name} group.");
                }
            }

            var local = RoofLocalCredential.IsLocal(user, _keys);
            var view = new RoofConfigurationView(layers.Providers);
            var roof = _roof.GetConfigurationSnapshot();
            var changes = new List<Change>();
            var forbidden = new List<string>();
            foreach (var definition in definitions)
            {
                var refusal = WriteRefusal(layers, definition, user, local);
                if (!values.TryGetValue(definition.Key, out var element))
                {
                    if (refusal is null && !definition.Secret)
                    {
                        errors.Add($"{definition.Key} is required: send every setting of the group you may change.");
                    }

                    continue;
                }

                if (!definition.TryParseJson(element, out var value, out var problem))
                {
                    errors.Add(problem!);
                    continue;
                }

                var current = CurrentValue(definition, view, roof);

                // A secret sent is always a change, so neither the answer nor the version tells whether a guess matched.
                if (!definition.Secret && RoofSettingDefinition.ValuesEqual(current, value))
                {
                    continue;
                }

                if (refusal is not null)
                {
                    forbidden.Add(refusal);
                    continue;
                }

                changes.Add(new Change(definition, current, value));
            }

            if (errors.Count > 0)
            {
                return RoofSettingsOutcome.Invalid(errors);
            }

            if (forbidden.Count > 0)
            {
                return RoofSettingsOutcome.Refused(RoofControllerErrorCode.SettingNotPermitted, string.Join(" ", forbidden));
            }

            var caller = RoofPrincipalFactory.DescribeCaller(user);
            var critical = changes.Where(change => change.IsSafetyCritical).ToList();
            if (critical.Count > 0 && !request.ConfirmSafetyCriticalChange)
            {
                _logger.LogWarning(
                    "Settings change by {Caller} rejected: safety-critical change not confirmed ({Changes})",
                    caller,
                    Describe(critical));
                return RoofSettingsOutcome.Refused(
                    RoofControllerErrorCode.ConfigurationRejected,
                    $"Changing {string.Join(", ", critical.Select(change => change.Definition.Key))} is safety-critical. " +
                    "Check the wiring, or that the protection should really be off, then resend with ConfirmSafetyCriticalChange=true.");
            }

            if (changes.Count == 0)
            {
                return RoofSettingsOutcome.Success(BuildResponse(layers, user, null), layers.Settings!.Document.Version);
            }

            var candidate = new RoofConfigurationView([new MemoryConfigurationProvider(new MemoryConfigurationSource
            {
                InitialData = Overlay(view.Flatten(), changes)
            })]);
            var problems = _validator.Validate(
                changes.Select(change => change.Definition.Group),
                candidate,
                () => WithChanges(roof, changes));
            if (problems.Count > 0)
            {
                return RoofSettingsOutcome.Invalid(problems.Select(problem => problem.Message).ToList());
            }

            if (_validator.CheckHttpsLockout(view, candidate, _configuration) is { } lockout)
            {
                return RoofSettingsOutcome.Refused(RoofControllerErrorCode.ConfigurationRejected, lockout);
            }

            if (CheckCameraServerChange(changes, view) is { } credentials)
            {
                _logger.LogWarning("Settings change by {Caller} rejected: {Problem}", caller, credentials);
                return RoofSettingsOutcome.Refused(RoofControllerErrorCode.ConfigurationRejected, credentials);
            }

            var outcome = Apply(layers, changes, local, caller, "changed", critical.Count > 0, audit: true,
                previousVersion => WriteChanges(layers, changes, previousVersion + 1, caller));
            return outcome ?? RoofSettingsOutcome.Success(BuildResponse(GetLayers(), user, null), GetLayers().Settings!.Document.Version);
        }
    }

    /// <summary>
    /// The roof settings of <c>POST RoofControl/Configuration</c>, which has already checked the request's rules and
    /// confirmation and writes its own audit entry. The local-only roof settings are never changed through it.
    /// </summary>
    public RoofSettingsOutcome UpdateRoofFromAlias(RoofControllerOptionsV4 updated, long expectedVersion, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            var layers = GetLayers();
            if (Unavailable(layers) is { } unavailable)
            {
                return unavailable;
            }

            if (Blocked(layers, expectedVersion) is { } blocked)
            {
                return blocked;
            }

            var roof = _roof.GetConfigurationSnapshot();
            var changes = new List<Change>();
            var forbidden = new List<string>();
            foreach (var definition in RoofSettingsCatalogue.InGroup(RoofSettingsContract.RoofGroup).Where(definition => !definition.LocalOnly))
            {
                var from = definition.GetRoofValue(roof);
                var to = definition.GetRoofValue(updated);
                if (RoofSettingDefinition.ValuesEqual(from, to))
                {
                    continue;
                }

                if (WriteRefusal(layers, definition, user, local: false) is { } refusal)
                {
                    forbidden.Add(refusal);
                    continue;
                }

                changes.Add(new Change(definition, from, to));
            }

            if (forbidden.Count > 0)
            {
                return RoofSettingsOutcome.Refused(RoofControllerErrorCode.SettingNotPermitted, string.Join(" ", forbidden));
            }

            var version = layers.Settings!.Document.Version;
            if (changes.Count == 0)
            {
                return RoofSettingsOutcome.Success(null, version);
            }

            var caller = RoofPrincipalFactory.DescribeCaller(user);
            var outcome = Apply(layers, changes, includeLocalOnlySettings: false, caller, "changed", safetyCritical: false, audit: false,
                previousVersion => WriteChanges(layers, changes, previousVersion + 1, caller));
            return outcome ?? RoofSettingsOutcome.Success(null, GetLayers().Settings!.Document.Version);
        }
    }

    /// <summary>Applies the pending hand edit, checked like a change through the API (<c>POST Settings/Reload</c>).</summary>
    public RoofSettingsOutcome Reload(RoofSettingsHandEditRequest request, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            var layers = GetLayers();
            if (Unavailable(layers) is { } unavailable)
            {
                return unavailable;
            }

            var edit = DetectHandEdit(layers);
            if (CheckToken(edit, request.Token) is { } mismatch)
            {
                return mismatch;
            }

            if (edit!.FileProblem is { } fileProblem)
            {
                return RoofSettingsOutcome.Refused(
                    RoofControllerErrorCode.ConfigurationRejected,
                    $"The edit cannot be reloaded: {fileProblem} Fix the file, or discard the edit.");
            }

            if (edit.Problems.Count > 0)
            {
                return RoofSettingsOutcome.Invalid(edit.Problems.Select(problem => problem.Message).ToList());
            }

            var critical = edit.Changes.Where(change => change.IsSafetyCritical).ToList();
            if (critical.Count > 0 && !request.ConfirmSafetyCriticalChange)
            {
                return RoofSettingsOutcome.Refused(
                    RoofControllerErrorCode.ConfigurationRejected,
                    $"The edit changes safety-critical settings ({string.Join(", ", critical.Select(change => change.Definition.Key))}). " +
                    "Review it, then resend with ConfirmSafetyCriticalChange=true.");
            }

            var local = RoofLocalCredential.IsLocal(user, _keys);
            var localOnly = edit.Changes.Where(change => change.Definition.LocalOnly).ToList();
            if (localOnly.Count > 0 && !local)
            {
                return RoofSettingsOutcome.Refused(
                    RoofControllerErrorCode.SettingNotPermitted,
                    $"The edit changes local-only settings ({string.Join(", ", localOnly.Select(change => change.Definition.Key))}). " +
                    "Reload it at the controller with a local credential, or discard it.");
            }

            var caller = RoofPrincipalFactory.DescribeCaller(user);

            // The files' own values: the local-only ones in them are the controller's own configuration.
            var outcome = Apply(layers, edit.Changes, includeLocalOnlySettings: true, caller, "hand edit reloaded", critical.Count > 0, audit: true,
                previousVersion =>
                {
                    var version = Math.Max(previousVersion, edit.Settings.Document!.Version) + 1;
                    WriteDocuments(layers, edit.Settings.Document.Body, edit.SecretsChanged ? edit.Secrets.Document!.Body : null, version, caller);
                    return version;
                });
            return outcome ?? RoofSettingsOutcome.Success(BuildResponse(GetLayers(), user, null), GetLayers().Settings!.Document.Version);
        }
    }

    /// <summary>Overwrites the pending hand edit with the settings in effect (<c>POST Settings/Discard</c>).</summary>
    public RoofSettingsOutcome Discard(RoofSettingsHandEditRequest request, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            var layers = GetLayers();
            if (Unavailable(layers) is { } unavailable)
            {
                return unavailable;
            }

            var edit = DetectHandEdit(layers);
            if (CheckToken(edit, request.Token) is { } mismatch)
            {
                return mismatch;
            }

            var caller = RoofPrincipalFactory.DescribeCaller(user);
            var settings = layers.Settings!;
            var previousVersion = settings.Document.Version;
            var version = Math.Max(previousVersion, edit!.Settings.Document?.Version ?? 0) + 1;
            try
            {
                WriteDocuments(layers, settings.Document.Body, edit.SecretsChanged ? layers.Secrets!.Document.Body : null, version, caller);
            }
            catch (RoofSettingsFileException ex)
            {
                _logger.LogError("Hand edit discard by {Caller} not saved: {Problem}", caller, ex.Message);
                return RoofSettingsOutcome.Refused(
                    RoofControllerErrorCode.SettingsStoreUnavailable,
                    "The settings could not be saved, so the edit is still there. The controller log has the reason.");
            }

            _logger.LogWarning(
                "AUDIT settings hand edit discarded by {Caller} (version {OldVersion} -> {NewVersion}): {Changes}",
                caller,
                previousVersion,
                version,
                edit.FileProblem is not null
                    ? "a file that could not be read"
                    : Describe(edit.Changes));
            return RoofSettingsOutcome.Success(BuildResponse(GetLayers(), user, null), version);
        }
    }

    /// <summary>
    /// Why the controller must not restart now, or null: a pending hand edit, which the restart would load, cannot be
    /// used, or needs a confirmation or a local credential the request does not have.
    /// </summary>
    public string? CheckRestart(RoofRestartRequest request, ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            var edit = DetectHandEdit(GetLayers());
            if (edit is null)
            {
                return null;
            }

            if (edit.FileProblem is { } fileProblem)
            {
                return $"The controller would not start: {fileProblem} Fix the file, or discard the edit (Settings/Discard).";
            }

            if (edit.Problems.Count > 0)
            {
                return "A pending hand edit, which a restart would load, has settings that cannot be used: " +
                    string.Join(" ", edit.Problems.Select(problem => problem.Message)) + " Fix them, or discard the edit.";
            }

            var critical = edit.Changes.Where(change => change.IsSafetyCritical).ToList();
            if (critical.Count > 0 && !request.ConfirmSafetyCriticalChange)
            {
                return $"A pending hand edit, which a restart would load, changes safety-critical settings " +
                    $"({string.Join(", ", critical.Select(change => change.Definition.Key))}). Review it, then resend with " +
                    "ConfirmSafetyCriticalChange=true.";
            }

            var localOnly = edit.Changes.Where(change => change.Definition.LocalOnly).ToList();
            if (localOnly.Count > 0 && !RoofLocalCredential.IsLocal(user, _keys))
            {
                return $"A pending hand edit, which a restart would load, changes local-only settings " +
                    $"({string.Join(", ", localOnly.Select(change => change.Definition.Key))}). Restart with a local credential, " +
                    "or discard the edit.";
            }

            return null;
        }
    }

    private RoofSettingsResponse BuildResponse(Layers layers, ClaimsPrincipal user, HandEdit? edit)
    {
        var admin = user.IsInRole(RoofControllerApiContract.AdminRole);
        var local = RoofLocalCredential.IsLocal(user, _keys);
        var view = new RoofConfigurationView(layers.Providers);
        var roof = _roof.GetConfigurationSnapshot();
        var states = new List<RoofSettingState>();
        foreach (var definition in RoofSettingsCatalogue.All.Where(definition => user.IsInRole(definition.ReadRole)))
        {
            object? value;
            string? problem = null;
            if (definition.RoofProperty is not null)
            {
                value = definition.GetRoofValue(roof);
            }
            else
            {
                definition.TryRead(view, out value, out problem);
            }

            var refusal = WriteRefusal(layers, definition, user, local)
                ?? (edit is null ? null : "The settings file was edited outside the API; an admin must reload or discard that edit first.");
            var restartPending = definition.AppliesAfterRestart
                && _startupValues.TryGetValue(definition.Key, out var startup)
                && !RoofSettingDefinition.ValuesEqual(startup, value);
            states.Add(new RoofSettingState(
                definition.Key,
                definition.Group,
                definition.Secret ? null : definition.ToJson(value),
                IsSet(value),
                Source(layers, definition),
                refusal is null,
                refusal,
                restartPending,
                problem));
        }

        var warnings = new List<string>();
        if (layers.Settings is null || layers.Secrets is null)
        {
            warnings.Add("The settings store is not available: settings can be read but not changed.");
        }
        else
        {
            if (layers.Settings.Path is null)
            {
                warnings.Add("Settings changes are held in memory only and are lost when the controller restarts " +
                    $"({RoofSettingsConfiguration.FilePathKey} is not set).");
            }
            else if (layers.Secrets.Path is null)
            {
                warnings.Add("Secrets set through the API are held in memory only and are lost when the controller restarts " +
                    $"({RoofSettingsConfiguration.SecretsFilePathKey} is not set).");
            }
        }

        if (admin && _unsavedRoofWarning is { } unsaved)
        {
            warnings.Add(unsaved);
        }

        if (admin && edit is not null)
        {
            warnings.Add("The settings file was edited outside the API. Review the pending edit, then reload or discard it.");
        }

        var document = layers.Settings?.Document ?? RoofSettingsDocument.Empty;
        return new RoofSettingsResponse(
            document.Version,
            document.SavedAtUtc,
            document.SavedBy,
            layers.Settings?.Path is not null,
            admin ? layers.Settings?.Path : null,
            admin ? edit?.ToContract() : null,
            warnings,
            states);
    }

    /// <summary>
    /// Applies <paramref name="changes"/>: the roof service first (it refuses while the roof moves), then
    /// <paramref name="persist"/>, which saves and publishes and returns the new version. When saving fails the roof
    /// service is put back. Returns null on success, else the refusal.
    /// </summary>
    private RoofSettingsOutcome? Apply(
        Layers layers,
        IReadOnlyList<Change> changes,
        bool includeLocalOnlySettings,
        string caller,
        string action,
        bool safetyCritical,
        bool audit,
        Func<long, long> persist)
    {
        var previousVersion = layers.Settings!.Document.Version;
        RoofControllerOptionsV4? previousRoof = null;
        var roofChanges = changes.Where(change => change.Definition.RoofProperty is not null).ToList();
        if (roofChanges.Count > 0)
        {
            previousRoof = _roof.GetConfigurationSnapshot();
            var result = _roof.ApplyConfiguration(WithChanges(previousRoof, roofChanges), includeLocalOnlySettings);
            if (!result.IsSuccessful)
            {
                var error = result.Error as RoofControllerException;
                _logger.LogWarning("Settings change by {Caller} refused by the roof controller: {Error}", caller, result.Error?.Message);
                return RoofSettingsOutcome.Refused(
                    error?.Code ?? RoofControllerErrorCode.Unknown,
                    error?.Message ?? "The roof controller could not apply the change. See the controller log.");
            }
        }

        long version;
        try
        {
            version = persist(previousVersion);
        }
        catch (RoofSettingsFileException ex)
        {
            _logger.LogError("Settings change by {Caller} not saved: {Problem}", caller, ex.Message);
            if (previousRoof is not null)
            {
                RevertRoof(previousRoof, caller);
            }

            return RoofSettingsOutcome.Refused(
                RoofControllerErrorCode.SettingsStoreUnavailable,
                "The settings could not be saved, so nothing was changed. The controller log has the reason.");
        }

        if (roofChanges.Count > 0)
        {
            _unsavedRoofWarning = null;
        }

        if (audit)
        {
            _logger.Log(
                safetyCritical ? LogLevel.Warning : LogLevel.Information,
                "AUDIT settings {Action} by {Caller} (version {OldVersion} -> {NewVersion}, safety-critical: {SafetyCritical}): {Changes}",
                action,
                caller,
                previousVersion,
                version,
                safetyCritical,
                Describe(changes));
        }

        return null;
    }

    private void RevertRoof(RoofControllerOptionsV4 previous, string caller)
    {
        var revert = _roof.ApplyConfiguration(previous, includeLocalOnlySettings: true);
        if (revert.IsSuccessful)
        {
            return;
        }

        _unsavedRoofWarning = "The roof controller runs roof settings that could not be saved and could not be put back " +
            "(" + (revert.Error?.Message ?? "unknown error") + "). They are lost at the next restart; change them again once the " +
            "settings file can be written.";
        _logger.LogCritical(
            "Roof settings changed by {Caller} could not be saved or put back: the roof controller runs unsaved settings ({Error})",
            caller,
            revert.Error?.Message);
    }

    /// <summary>Saves <paramref name="changes"/> over the files' current contents and returns <paramref name="version"/>.</summary>
    private long WriteChanges(Layers layers, IReadOnlyList<Change> changes, long version, string caller)
    {
        var settingsBody = (JsonObject)layers.Settings!.Document.Body.DeepClone();
        var secretsBody = (JsonObject)layers.Secrets!.Document.Body.DeepClone();
        var secretsChanged = false;
        foreach (var change in changes)
        {
            var definition = change.Definition;
            var layer = definition.Secret ? layers.Secrets : layers.Settings;
            var body = definition.Secret ? secretsBody : settingsBody;
            secretsChanged |= definition.Secret;
            RemovePath(body, definition.Key);

            // The file holds only what differs from the layers below it, so later shipped defaults still take effect.
            var below = layers.Below(layer);
            var baseReadable = definition.TryRead(below, out var baseValue, out _);
            if (!baseReadable || !RoofSettingDefinition.ValuesEqual(baseValue, change.To))
            {
                SetPath(body, definition.Key, definition.ToNode(change.To));
            }
        }

        WriteDocuments(layers, settingsBody, secretsChanged ? secretsBody : null, version, caller);
        return version;
    }

    /// <summary>
    /// Writes the settings file (and the secrets file when <paramref name="secretsBody"/> is given) with
    /// <paramref name="version"/>, then publishes both to the configuration with one change notification. Without a
    /// path a file is held in memory only.
    /// </summary>
    private void WriteDocuments(Layers layers, JsonObject settingsBody, JsonObject? secretsBody, long version, string caller)
    {
        var settings = layers.Settings!;
        var secrets = layers.Secrets!;
        var now = _time.GetUtcNow();
        var settingsBytes = RoofSettingsFile.Serialize(settingsBody, version, now, caller);
        var secretsBytes = secretsBody is null ? null : RoofSettingsFile.Serialize(secretsBody, version, now, caller);

        // Parsed before anything is written: what is saved is exactly what the next start reads.
        var settingsDocument = RoofSettingsFile.Parse(settingsBytes, settings.Path ?? "(memory)", RoofSettingsFileKind.Settings);
        var secretsDocument = secretsBytes is null
            ? null
            : RoofSettingsFile.Parse(secretsBytes, secrets.Path ?? "(memory)", RoofSettingsFileKind.Secrets);

        byte[]? previousSecrets = null;
        if (secretsBytes is not null && secrets.Path is not null)
        {
            previousSecrets = RoofSettingsFile.ReadBytes(secrets.Path, RoofSettingsFileKind.Secrets);
            Written(secrets.Path, RoofSettingsFile.Write(secrets.Path, RoofSettingsFileKind.Secrets, secretsBytes));
        }

        if (settings.Path is not null)
        {
            try
            {
                Written(settings.Path, RoofSettingsFile.Write(settings.Path, RoofSettingsFileKind.Settings, settingsBytes));
            }
            catch (RoofSettingsFileException)
            {
                if (secretsBytes is not null && secrets.Path is not null)
                {
                    RestoreSecrets(secrets.Path, previousSecrets);
                }

                throw;
            }
        }

        if (secretsDocument is not null)
        {
            secrets.Replace(secretsDocument, notify: false);
        }

        settings.Replace(settingsDocument);
    }


    private void RestoreSecrets(string path, byte[]? previous)
    {
        try
        {
            if (previous is null)
            {
                File.Delete(path);
            }
            else
            {
                RoofSettingsFile.Write(path, RoofSettingsFileKind.Secrets, previous);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RoofSettingsFileException)
        {
            // The secrets file now holds a change the settings file does not: the next start loads it.
            _logger.LogCritical(
                "The managed secrets file {Path} could not be put back after the settings file failed to save ({Error}); " +
                "it holds the new secrets, which the next start will load",
                path,
                ex.GetType().Name);
        }
    }

    private void Written(string path, bool flushed)
    {
        if (!flushed && OperatingSystem.IsLinux())
        {
            _logger.LogWarning(
                "{Path} was saved, but its directory could not be flushed to disk; a power cut in the next seconds could undo the change",
                path);
        }
    }

    /// <summary>
    /// The edit made to the files outside the API since they were loaded, or null. Each call reads the files again,
    /// so a pending edit is always the current one.
    /// </summary>
    private HandEdit? DetectHandEdit(Layers layers)
    {
        if (layers.Settings is null || layers.Secrets is null)
        {
            return null;
        }

        var settingsDisk = ReadDisk(layers.Settings);
        var secretsDisk = ReadDisk(layers.Secrets);
        if (settingsDisk.Hash == layers.Settings.Document.Hash && secretsDisk.Hash == layers.Secrets.Document.Hash)
        {
            return null;
        }

        var token = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(settingsDisk.Hash + "\n" + secretsDisk.Hash)));
        var secretsChanged = secretsDisk.Hash != layers.Secrets.Document.Hash;
        if ((settingsDisk.Problem ?? secretsDisk.Problem) is { } fileProblem)
        {
            return new HandEdit(token, settingsDisk, secretsDisk, secretsChanged, [], [], fileProblem);
        }

        var candidateProviders = layers.Providers
            .Select(provider => ReferenceEquals(provider, layers.Settings)
                ? RoofSettingsFileProvider.Detached(RoofSettingsFileKind.Settings, settingsDisk.Document!)
                : ReferenceEquals(provider, layers.Secrets)
                    ? RoofSettingsFileProvider.Detached(RoofSettingsFileKind.Secrets, secretsDisk.Document!)
                    : provider)
            .ToList();
        var before = new RoofConfigurationView(layers.Providers);
        var after = new RoofConfigurationView(candidateProviders);

        var changes = new List<Change>();
        var problems = new List<RoofSettingProblem>();
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in RoofSettingsCatalogue.All)
        {
            var wasReadable = definition.TryRead(before, out var from, out var oldProblem);
            if (!definition.TryRead(after, out var to, out var problem))
            {
                // A value that was already unusable before the edit is not the edit's problem.
                if (wasReadable || oldProblem != problem)
                {
                    problems.Add(new RoofSettingProblem(definition.Key, problem!));
                }

                continue;
            }

            if (!RoofSettingDefinition.ValuesEqual(from, to))
            {
                changes.Add(new Change(definition, from, to));
                groups.Add(definition.Group);
            }
        }

        if (problems.Count == 0 && groups.Count > 0)
        {
            var running = _roof.GetConfigurationSnapshot();
            problems.AddRange(_validator.Validate(groups, after, () => WithChanges(
                running,
                changes.Where(change => change.Definition.RoofProperty is not null).ToList())));
            if (_validator.CheckHttpsLockout(before, after, _configuration) is { } lockout)
            {
                problems.Add(new RoofSettingProblem(RoofSettingsContract.SecurityGroup, lockout));
            }
        }

        return new HandEdit(token, settingsDisk, secretsDisk, secretsChanged, changes, problems, FileProblem: null);
    }

    private static DiskState ReadDisk(RoofSettingsFileProvider provider)
    {
        if (provider.Path is null)
        {
            return new DiskState(provider.Document.Hash, provider.Document, null);
        }

        try
        {
            var document = RoofSettingsFile.Read(provider.Path, provider.Kind);
            return new DiskState(document.Hash, document, null);
        }
        catch (RoofSettingsFileException ex)
        {
            string hash;
            try
            {
                hash = RoofSettingsFile.CurrentHash(provider.Path, provider.Kind);
            }
            catch (RoofSettingsFileException)
            {
                // Unreadable: a hash that matches nothing, so the edit shows until the file can be read again.
                hash = "unreadable";
            }

            return new DiskState(hash, null, ex.Message);
        }
    }

    private static RoofSettingsOutcome? CheckToken(HandEdit? edit, string? token)
    {
        if (edit is null)
        {
            return RoofSettingsOutcome.Refused(RoofControllerErrorCode.InvalidRequest, "There is no pending hand edit.");
        }

        var expected = Encoding.UTF8.GetBytes(edit.Token);
        var presented = Encoding.UTF8.GetBytes(token ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(expected, presented)
            ? null
            : RoofSettingsOutcome.Refused(
                RoofControllerErrorCode.ConfigurationVersionConflict,
                "The files changed again since the pending edit was read. Read the settings again, review the edit, and retry.");
    }

    private RoofSettingsOutcome? Blocked(Layers layers, long? expectedVersion)
    {
        if (DetectHandEdit(layers) is not null)
        {
            return RoofSettingsOutcome.Refused(
                RoofControllerErrorCode.SettingsHandEditPending,
                "The settings file was edited outside the API since it was loaded. An admin must reload or discard that " +
                "edit (Settings/Reload or Settings/Discard) before settings can be changed through the API.");
        }

        var version = layers.Settings!.Document.Version;
        return expectedVersion == version
            ? null
            : RoofSettingsOutcome.Refused(
                RoofControllerErrorCode.ConfigurationVersionConflict,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The settings changed since they were read (expected version {expectedVersion}, current version {version}). Read them again and retry."));
    }

    private static RoofSettingsOutcome? Unavailable(Layers layers)
        => layers.Settings is null || layers.Secrets is null
            ? RoofSettingsOutcome.Refused(
                RoofControllerErrorCode.SettingsStoreUnavailable,
                "The settings store is not available: settings can be read but not changed.")
            : null;

    /// <summary>Why <paramref name="user"/> may not change <paramref name="definition"/>, or null.</summary>
    private string? WriteRefusal(Layers layers, RoofSettingDefinition definition, ClaimsPrincipal user, bool local)
    {
        if (!user.IsInRole(definition.WriteRole))
        {
            return $"{definition.Key} needs the {definition.WriteRole} role.";
        }

        var layer = layers.LayerOf(definition);
        if (layer is null)
        {
            return "The settings store is not available.";
        }

        var index = layers.IndexOf(layer);
        for (var i = layers.Providers.Count - 1; i > index; i--)
        {
            if (RoofConfigurationView.Sets(layers.Providers[i], definition.Key, definition.IsList))
            {
                return $"{definition.Key} is set by the {Label(layers.Providers[i])}, which takes precedence over the " +
                    "settings file; change it there.";
            }
        }

        if (definition.LocalOnly && !local)
        {
            return $"{definition.Key} is local-only: change it at the controller, from the kiosk with an admin PIN or " +
                "with a local admin API key.";
        }

        return null;
    }

    private string Source(Layers layers, RoofSettingDefinition definition)
    {
        for (var i = layers.Providers.Count - 1; i >= 0; i--)
        {
            if (RoofConfigurationView.Sets(layers.Providers[i], definition.Key, definition.IsList))
            {
                return Label(layers.Providers[i]);
            }
        }

        return DefaultSource;
    }

    private string Label(IConfigurationProvider provider) => provider switch
    {
        RoofSettingsFileProvider { Kind: RoofSettingsFileKind.Settings } => "settings file",
        RoofSettingsFileProvider => "managed secrets",
        JsonConfigurationProvider json => JsonLabel(json.Source.Path),
        EnvironmentVariablesConfigurationProvider => "environment",
        CommandLineConfigurationProvider => "command line",
        KeyPerFileConfigurationProvider => "secrets directory",
        MemoryConfigurationProvider => "memory",
        _ => "configuration"
    };

    private string JsonLabel(string? path)
    {
        var name = Path.GetFileName(path ?? string.Empty);
        if (string.Equals(name, "appsettings.json", StringComparison.OrdinalIgnoreCase))
        {
            return "shipped defaults";
        }

        if (string.Equals(name, $"appsettings.{_environment.EnvironmentName}.json", StringComparison.OrdinalIgnoreCase))
        {
            return $"shipped {_environment.EnvironmentName} defaults";
        }

        if (string.Equals(name, "secrets.json", StringComparison.OrdinalIgnoreCase))
        {
            return "user secrets";
        }

        return name.Length == 0 ? "configuration" : name;
    }

    /// <summary>The value the layers below the setting's file give it: what the setting is when the file does not set it.</summary>
    private static object? BaseValue(Layers layers, RoofSettingDefinition definition)
    {
        if (layers.LayerOf(definition) is not { } layer)
        {
            return definition.CodeDefault;
        }

        definition.TryRead(layers.Below(layer), out var value, out _);
        return value;
    }

    private static object? CurrentValue(RoofSettingDefinition definition, RoofConfigurationView view, RoofControllerOptionsV4 roof)
    {
        if (definition.RoofProperty is not null)
        {
            return definition.GetRoofValue(roof);
        }

        definition.TryRead(view, out var value, out _);
        return value;
    }

    private static RoofControllerOptionsV4 WithChanges(RoofControllerOptionsV4 options, IEnumerable<Change> changes)
    {
        var updated = options with { };
        foreach (var change in changes.Where(change => change.Definition.RoofProperty is not null))
        {
            change.Definition.SetRoofValue(updated, change.To);
        }

        return updated;
    }

    /// <summary><paramref name="data"/> with each change's keys replaced by its new value's.</summary>
    private static Dictionary<string, string?> Overlay(Dictionary<string, string?> data, IEnumerable<Change> changes)
    {
        foreach (var change in changes)
        {
            var key = change.Definition.Key;
            foreach (var existing in data.Keys.Where(existing => Covers(key, existing)).ToList())
            {
                data.Remove(existing);
            }

            foreach (var (entryKey, value) in change.Definition.ToConfigurationEntries(change.To))
            {
                data[entryKey] = value;
            }
        }

        return data;
    }

    private static bool Covers(string settingKey, string key)
        => string.Equals(settingKey, key, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(settingKey + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes the key's property from <paramref name="root"/>, and the objects left empty above it.</summary>
    private static void RemovePath(JsonObject root, string key)
    {
        var segments = key.Split(ConfigurationPath.KeyDelimiter);
        var path = new List<(JsonObject Parent, string Name)>();
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            if (FindName(current, segments[i]) is not { } name)
            {
                return;
            }

            path.Add((current, name));
            if (i == segments.Length - 1)
            {
                break;
            }

            if (current[name] is not JsonObject next)
            {
                return;
            }

            current = next;
        }

        var (parent, last) = path[^1];
        parent.Remove(last);
        for (var i = path.Count - 2; i >= 0; i--)
        {
            var (owner, name) = path[i];
            if (owner[name] is JsonObject { Count: 0 })
            {
                owner.Remove(name);
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>Sets the key's property in <paramref name="root"/>, creating (or replacing non-object) parents.</summary>
    private static void SetPath(JsonObject root, string key, JsonNode? node)
    {
        var segments = key.Split(ConfigurationPath.KeyDelimiter);
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var name = FindName(current, segments[i]);
            if (name is not null && current[name] is JsonObject next)
            {
                current = next;
                continue;
            }

            if (name is not null)
            {
                current.Remove(name);
            }

            var created = new JsonObject(RoofSettingsFile.NodeOptions);
            current[segments[i]] = created;
            current = created;
        }

        if (FindName(current, segments[^1]) is { } existing)
        {
            current.Remove(existing);
        }

        current[segments[^1]] = node;
    }

    private static string? FindName(JsonObject node, string name)
        => node.Select(property => property.Key).FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Refuses pointing the camera proxy at another server (scheme, host or port) while it holds a Blue Iris user or
    /// password, unless the same change sends both: the proxy would otherwise hand them to a server they were not set for.
    /// </summary>
    private static string? CheckCameraServerChange(IReadOnlyList<Change> changes, RoofConfigurationView view)
    {
        if (changes.FirstOrDefault(change => change.Definition.Key == CameraBaseUrlKey) is not { } move
            || move.To is not string to
            || string.IsNullOrWhiteSpace(to)
            || SameServer(move.From as string, to))
        {
            return null;
        }

        var credentials = RoofSettingsCatalogue.InGroup(RoofSettingsContract.CameraGroup).Where(definition => definition.Secret).ToList();
        var sent = credentials.All(definition => changes.Any(change => change.Definition == definition));
        var held = credentials.Any(definition => definition.TryRead(view, out var value, out _) && IsSet(value));
        return sent || !held
            ? null
            : $"Moving the camera proxy to another server ({CameraBaseUrlKey}) would send it the Blue Iris user and password set " +
                $"for the current one. Send {string.Join(" and ", credentials.Select(definition => definition.Key))} with it " +
                "(null clears them). Credentials provisioned in the secrets directory cannot be sent through the API: change the " +
                "server where they are set.";

        static bool SameServer(string? from, string to)
            => Uri.TryCreate(from, UriKind.Absolute, out var before)
                && Uri.TryCreate(to, UriKind.Absolute, out var after)
                && Uri.Compare(before, after, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static bool IsSet(object? value) => value switch
    {
        null => false,
        string text => text.Length > 0,
        IReadOnlyList<string> items => items.Count > 0,
        _ => true
    };

    private static string Describe(IEnumerable<Change> changes)
    {
        var parts = changes
            .Select(change => $"{change.Definition.Key}: {change.Definition.Describe(change.From)} -> {change.Definition.Describe(change.To)}")
            .ToList();
        return parts.Count == 0 ? "none" : string.Join("; ", parts);
    }

    /// <summary>A key sent by a caller, cut short for a message.</summary>
    private static string Shorten(string key) => key.Length <= 100 ? key : key[..100] + "...";

    private Layers GetLayers()
    {
        var providers = _configuration.Providers.ToList();
        return new Layers(
            providers,
            providers.OfType<RoofSettingsFileProvider>().LastOrDefault(provider => provider.Kind == RoofSettingsFileKind.Settings),
            providers.OfType<RoofSettingsFileProvider>().LastOrDefault(provider => provider.Kind == RoofSettingsFileKind.Secrets));
    }

    /// <summary>The configuration's providers (lowest first) and the two the store writes.</summary>
    private sealed record Layers(IReadOnlyList<IConfigurationProvider> Providers, RoofSettingsFileProvider? Settings, RoofSettingsFileProvider? Secrets)
    {
        public RoofSettingsFileProvider? LayerOf(RoofSettingDefinition definition) => definition.Secret ? Secrets : Settings;

        public int IndexOf(IConfigurationProvider provider)
        {
            for (var i = 0; i < Providers.Count; i++)
            {
                if (ReferenceEquals(Providers[i], provider))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>The layers below <paramref name="provider"/>.</summary>
        public RoofConfigurationView Below(IConfigurationProvider provider) => new(Providers.Take(IndexOf(provider)));
    }

    private sealed record Change(RoofSettingDefinition Definition, object? From, object? To)
    {
        public bool IsSafetyCritical => Definition.IsSafetyCritical(From, To);
    }

    private sealed record DiskState(string Hash, RoofSettingsDocument? Document, string? Problem);

    private sealed record HandEdit(
        string Token,
        DiskState Settings,
        DiskState Secrets,
        bool SecretsChanged,
        IReadOnlyList<Change> Changes,
        IReadOnlyList<RoofSettingProblem> Problems,
        string? FileProblem)
    {
        public RoofSettingsHandEdit ToContract() => new(
            Token,
            Changes
                .Select(change => new RoofSettingChange(
                    change.Definition.Key,
                    change.Definition.Secret ? null : change.Definition.ToJson(change.From),
                    change.Definition.Secret ? null : change.Definition.ToJson(change.To),
                    change.Definition.Secret))
                .ToList(),
            Problems,
            FileProblem,
            Changes.Any(change => change.IsSafetyCritical),
            Changes.Any(change => change.Definition.LocalOnly));
    }
}
