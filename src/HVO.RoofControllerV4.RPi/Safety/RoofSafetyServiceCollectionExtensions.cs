using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Safety;

/// <summary>Registers the safety service, its sources and the Open interlock (docs/safety-service.md).</summary>
public static class RoofSafetyServiceCollectionExtensions
{
    /// <summary>
    /// Adds the safety service, its options (<see cref="RoofSafetyOptions.SectionName"/>, validated at start), the
    /// interlock, and the configured ASCOM Alpaca devices' sources (<see cref="RoofSafetyConfiguredSources"/>). With no
    /// source, the conditions are Unknown ("No safety sources configured"). Any other source is an
    /// <see cref="ITelescopeParkSource"/> or <see cref="IWeatherSafetySource"/> singleton (the tests' simulated ones),
    /// read before the configured ones.
    /// </summary>
    public static IServiceCollection AddRoofSafety(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RoofSafetyOptions>()
            .Bind(configuration.GetSection(RoofSafetyOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RoofSafetyOptions>, RoofSafetyOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        AddAlpacaClient(services);
        services.AddSingleton<RoofSafetyConfiguredSources>();
        services.AddSingleton<RoofSafetyService>();
        services.AddSingleton<IRoofSafetyService>(provider => provider.GetRequiredService<RoofSafetyService>());
        services.AddHostedService(provider => provider.GetRequiredService<RoofSafetyService>());
        services.AddSingleton<RoofSafetyInterlock>();
        return services;
    }

    /// <summary><paramref name="status"/> with the safety conditions now (<see cref="RoofStatusResponse.Safety"/>).</summary>
    public static RoofStatusResponse WithSafety(this IRoofSafetyService safety, RoofStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(safety);
        ArgumentNullException.ThrowIfNull(status);
        return status with { Safety = safety.GetCurrent() };
    }

    private static void AddAlpacaClient(IServiceCollection services)
        => services.AddHttpClient(AlpacaDevice.HttpClientName, client =>
            {
                // The service's own read timeout cancels every read first; this only bounds a read made without one.
                client.Timeout = RoofSafetyOptions.MaxReadTimeout + TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = AlpacaDevice.MaxAnswerBytes;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // The devices are on the observatory's network: no proxy, and no redirect to anywhere else.
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = RoofSafetyOptions.MaxReadTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            })
            // Its request lines would log the devices' addresses with every read.
            .RemoveAllLoggers();
}

/// <summary>
/// The sources the <c>RoofSafety</c> section configures: an <see cref="AlpacaTelescopeParkSource"/> for each of
/// <see cref="RoofSafetyOptions.Telescopes"/> and an <see cref="AlpacaSafetyMonitorSource"/> for each of
/// <see cref="RoofSafetyOptions.WeatherSources"/>, in the order configured. Made once, from the validated options
/// (settings the controller cannot use stop the start, <c>ValidateOnStart</c>): a change to the devices takes a restart.
/// </summary>
public sealed class RoofSafetyConfiguredSources
{
    public RoofSafetyConfiguredSources(IOptions<RoofSafetyOptions> options, IHttpClientFactory clients)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clients);
        var value = options.Value;
        Telescopes = [.. (value.Telescopes ?? []).Select(device => new AlpacaTelescopeParkSource(device, clients))];
        WeatherSources = [.. (value.WeatherSources ?? []).Select(device => new AlpacaSafetyMonitorSource(device, clients))];
    }

    /// <summary>The configured mounts.</summary>
    public IReadOnlyList<ITelescopeParkSource> Telescopes { get; }

    /// <summary>The configured weather sources.</summary>
    public IReadOnlyList<IWeatherSafetySource> WeatherSources { get; }
}
