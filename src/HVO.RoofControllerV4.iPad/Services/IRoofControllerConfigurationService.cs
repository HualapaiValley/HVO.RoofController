using HVO.Core.Results;
using HVO.RoofControllerV4.iPad.Configuration;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Provides persistence for the roof controller client configuration.
/// </summary>
public interface IRoofControllerConfigurationService
{
    /// <summary>
    /// Loads the persisted configuration, falling back to defaults when a user override is not available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the asynchronous operation.</param>
    /// <returns>The currently effective configuration.</returns>
    Task<Result<RoofControllerApiOptions>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the provided configuration to durable storage.
    /// </summary>
    /// <param name="options">The configuration values to persist.</param>
    /// <param name="cancellationToken">Cancellation token for the asynchronous operation.</param>
    /// <returns>The persisted configuration when successful.</returns>
    Task<Result<RoofControllerApiOptions>> SaveAsync(RoofControllerApiOptions options, CancellationToken cancellationToken = default);
}
