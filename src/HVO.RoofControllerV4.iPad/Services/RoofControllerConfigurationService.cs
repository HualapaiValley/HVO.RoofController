using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.Core.Results;
using HVO.RoofControllerV4.iPad.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Storage;

namespace HVO.RoofControllerV4.iPad.Services;

/// <summary>
/// Persists roof controller configuration overrides to the local app data directory.
/// </summary>
public sealed class RoofControllerConfigurationService : IRoofControllerConfigurationService
{
    private const string ConfigurationFileName = "roofcontroller.settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _configurationPath;
    private readonly ILogger<RoofControllerConfigurationService> _logger;
    private readonly IOptions<RoofControllerApiOptions> _defaultOptions;

    public RoofControllerConfigurationService(
        ILogger<RoofControllerConfigurationService> logger,
        IOptions<RoofControllerApiOptions> defaultOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _defaultOptions = defaultOptions ?? throw new ArgumentNullException(nameof(defaultOptions));
        _configurationPath = Path.Combine(FileSystem.AppDataDirectory, ConfigurationFileName);
    }

    /// <inheritdoc />
    public async Task<Result<RoofControllerApiOptions>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_configurationPath))
            {
                _logger.LogTrace("No user configuration found at {ConfigurationPath}; using defaults.", _configurationPath);
                return Result<RoofControllerApiOptions>.Success(CloneOptions(_defaultOptions.Value));
            }

            await using var stream = File.OpenRead(_configurationPath);
            var payload = await JsonSerializer.DeserializeAsync<ConfigurationPayload>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            RoofControllerApiOptions? options;

            if (payload?.RoofControllerApi is not null)
            {
                options = payload.RoofControllerApi;
            }
            else
            {
                stream.Position = 0;
                options = await JsonSerializer.DeserializeAsync<RoofControllerApiOptions>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }

            if (options is null)
            {
                return Result<RoofControllerApiOptions>.Failure(new InvalidOperationException("Configuration file is empty."));
            }

            _logger.LogDebug("Loaded roof controller configuration from {ConfigurationPath}", _configurationPath);
            return Result<RoofControllerApiOptions>.Success(options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load roof controller configuration from {ConfigurationPath}", _configurationPath);
            return Result<RoofControllerApiOptions>.Failure(ex);
        }
    }

    /// <inheritdoc />
    public async Task<Result<RoofControllerApiOptions>> SaveAsync(RoofControllerApiOptions options, CancellationToken cancellationToken = default)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        try
        {
            Directory.CreateDirectory(FileSystem.AppDataDirectory);
            await using var stream = File.Create(_configurationPath);
            var payload = new ConfigurationPayload { RoofControllerApi = options };
            await JsonSerializer.SerializeAsync(stream, payload, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Saved roof controller configuration to {ConfigurationPath}", _configurationPath);
            return Result<RoofControllerApiOptions>.Success(options);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save roof controller configuration to {ConfigurationPath}", _configurationPath);
            return Result<RoofControllerApiOptions>.Failure(ex);
        }
    }

    private static RoofControllerApiOptions CloneOptions(RoofControllerApiOptions source)
    {
        return new RoofControllerApiOptions
        {
            BaseUrl = source.BaseUrl,
            StatusPollIntervalSeconds = source.StatusPollIntervalSeconds,
            CameraStreamUrl = source.CameraStreamUrl,
            ClearFaultPulseMs = source.ClearFaultPulseMs,
            SafetyWatchdogTimeoutSeconds = source.SafetyWatchdogTimeoutSeconds,
            RequestRetryCount = source.RequestRetryCount,
            ConnectionFailurePromptThreshold = source.ConnectionFailurePromptThreshold
        };
    }
}

file sealed class ConfigurationPayload
{
    public RoofControllerApiOptions? RoofControllerApi { get; set; }
}
