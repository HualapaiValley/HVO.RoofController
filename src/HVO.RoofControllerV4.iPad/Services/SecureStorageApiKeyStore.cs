using Microsoft.Maui.Storage;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Keeps the API key in the platform secure store (the iOS Keychain).
/// </summary>
public sealed class SecureStorageApiKeyStore : IApiKeyStore
{
    private const string StorageKey = "hvo.roofcontroller.apikey";

    public async Task<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await SecureStorage.Default.GetAsync(StorageKey).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public async Task SetAsync(string? apiKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SecureStorage.Default.Remove(StorageKey);
            return;
        }

        await SecureStorage.Default.SetAsync(StorageKey, apiKey.Trim()).ConfigureAwait(false);
    }
}
