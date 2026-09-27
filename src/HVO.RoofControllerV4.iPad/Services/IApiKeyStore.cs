namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Storage for the controller API key. The key is kept out of the JSON settings file.
/// </summary>
public interface IApiKeyStore
{
    /// <summary>Returns the stored key, or null when none is stored. Throws when secure storage is unavailable.</summary>
    Task<string?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the key; a null or blank key removes it. Throws when secure storage is unavailable.</summary>
    Task SetAsync(string? apiKey, CancellationToken cancellationToken = default);
}
