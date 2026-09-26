using HVO.Core.Results;
using HVO.RoofControllerV4.iPad.Configuration;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Provides persistence for the roof controller client configuration. Settings go to a JSON file in the app data
/// directory (written atomically); the API key goes to secure storage and never into the file.
/// </summary>
public interface IRoofControllerConfigurationService
{
    /// <summary>
    /// What happened when the settings file was read at startup (missing, loaded, or recovered from a bad file).
    /// </summary>
    SettingsLoadResult StartupLoadResult { get; }

    /// <summary>
    /// Returns the effective configuration: the last saved settings, or the built-in defaults.
    /// </summary>
    Task<Result<RoofControllerApiOptions>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and saves the configuration. The file is replaced atomically, so a failed save leaves the previous
    /// file intact.
    /// </summary>
    Task<Result<RoofControllerApiOptions>> SaveAsync(RoofControllerApiOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the API key from secure storage, first moving any key found in the settings file into secure storage.
    /// Never fails: when secure storage is unavailable the result carries a notice instead.
    /// </summary>
    Task<ApiKeyLoadResult> LoadApiKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the API key in secure storage; a blank key removes it.
    /// </summary>
    Task<Result<bool>> SaveApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// The API key read at startup and an optional operator notice (migration done, secure storage unavailable).
/// </summary>
public sealed record ApiKeyLoadResult(string? ApiKey, string? Notice, bool IsPersisted)
{
    /// <summary>Never includes the key.</summary>
    public override string ToString() => $"ApiKeyLoadResult {{ HasKey = {!string.IsNullOrEmpty(ApiKey)}, IsPersisted = {IsPersisted}, Notice = {Notice} }}";
}
