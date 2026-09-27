using System.ComponentModel.DataAnnotations;
using HVO.Core.Results;
using HVO.RoofControllerV4.iPad.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Persists roof controller configuration overrides to the local app data directory and the API key to secure storage.
/// </summary>
public sealed class RoofControllerConfigurationService : IRoofControllerConfigurationService
{
    private readonly SettingsFileStore _store;
    private readonly IApiKeyStore _apiKeyStore;
    private readonly ILogger<RoofControllerConfigurationService> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _sync = new();
    private RoofControllerApiOptions _current;
    private bool _embeddedKeyHandled;

    public RoofControllerConfigurationService(
        SettingsFileStore store,
        SettingsLoadResult startupLoadResult,
        IApiKeyStore apiKeyStore,
        IOptions<RoofControllerApiOptions> effectiveOptions,
        ILogger<RoofControllerConfigurationService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        StartupLoadResult = startupLoadResult ?? throw new ArgumentNullException(nameof(startupLoadResult));
        _apiKeyStore = apiKeyStore ?? throw new ArgumentNullException(nameof(apiKeyStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _current = (effectiveOptions ?? throw new ArgumentNullException(nameof(effectiveOptions))).Value.Clone();
    }

    /// <inheritdoc />
    public SettingsLoadResult StartupLoadResult { get; }

    /// <inheritdoc />
    public Task<Result<RoofControllerApiOptions>> LoadAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            return Task.FromResult(Result<RoofControllerApiOptions>.Success(_current.Clone()));
        }
    }

    /// <inheritdoc />
    public async Task<Result<RoofControllerApiOptions>> SaveAsync(RoofControllerApiOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var snapshot = options.Clone();
        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(snapshot, new ValidationContext(snapshot), validationResults, validateAllProperties: true))
        {
            return Result<RoofControllerApiOptions>.Failure(new ValidationException(string.Join(" ", validationResults.Select(r => r.ErrorMessage))));
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = RoofControllerSettingsLoader.Serialize(snapshot);
            await Task.Run(() => _store.WriteAtomically(json), cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _current = snapshot.Clone();
            }

            _logger.LogInformation("Saved roof controller configuration to {ConfigurationPath}", _store.FilePath);
            return Result<RoofControllerApiOptions>.Success(snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to save roof controller configuration to {ConfigurationPath}", _store.FilePath);
            return Result<RoofControllerApiOptions>.Failure(ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ApiKeyLoadResult> LoadApiKeyAsync(CancellationToken cancellationToken = default)
    {
        string? stored;
        try
        {
            stored = await _apiKeyStore.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Secure storage is unavailable; the API key cannot be read");
            var sessionKey = TakeEmbeddedKey();
            var notice = sessionKey is null
                ? "Secure storage is unavailable, so the API key could not be loaded. Enter it on the Configuration tab; it will only be kept until the app closes."
                : "Secure storage is unavailable. The API key found in the settings file is used for this session only; enter it again on the Configuration tab after the next launch.";
            return new ApiKeyLoadResult(sessionKey, notice, IsPersisted: false);
        }

        var embedded = TakeEmbeddedKey();
        if (embedded is null)
        {
            return new ApiKeyLoadResult(stored, null, IsPersisted: stored is not null);
        }

        string migrationNotice;
        if (stored is null)
        {
            try
            {
                await _apiKeyStore.SetAsync(embedded, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not move the API key from the settings file into secure storage");
                return new ApiKeyLoadResult(embedded, "The API key in the settings file could not be moved to secure storage. It is used for this session only.", IsPersisted: false);
            }

            stored = embedded;
            migrationNotice = "The API key was moved from the settings file into the iOS Keychain.";
        }
        else
        {
            migrationNotice = string.Equals(stored, embedded, StringComparison.Ordinal)
                ? "A copy of the API key was removed from the settings file; the Keychain copy is used."
                : "An API key found in the settings file was ignored and removed; the key already in the Keychain is used.";
        }

        // Only now that the key is safely in secure storage, rewrite the file without it.
        RoofControllerApiOptions current;
        lock (_sync)
        {
            current = _current.Clone();
        }

        var rewrite = await SaveAsync(current, cancellationToken).ConfigureAwait(false);
        if (rewrite.IsFailure)
        {
            migrationNotice += " The settings file could not be rewritten yet; it will be on the next save.";
        }

        return new ApiKeyLoadResult(stored, migrationNotice, IsPersisted: true);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> SaveApiKeyAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        try
        {
            await _apiKeyStore.SetAsync(string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(), cancellationToken).ConfigureAwait(false);
            return Result<bool>.Success(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not save the API key to secure storage");
            return Result<bool>.Failure(ex);
        }
    }

    private string? TakeEmbeddedKey()
    {
        lock (_sync)
        {
            if (_embeddedKeyHandled)
            {
                return null;
            }

            _embeddedKeyHandled = true;
            return StartupLoadResult.EmbeddedApiKey;
        }
    }
}
