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

        var section = configuration.GetSection(RoofSafetyOptions.SectionName);
        services.AddOptions<RoofSafetyOptions>()
            .Configure(options => BindStrictly(section, options))
            .ValidateOnStart();
        services.AddSingleton<IOptionsChangeTokenSource<RoofSafetyOptions>>(new ConfigurationChangeTokenSource<RoofSafetyOptions>(section));
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

    /// <summary>
    /// Binds the <c>RoofSafety</c> section, refusing what cannot be bound: a value that does not convert, or a key no setting
    /// has. By default the binder drops a whole device from its list for one bad value, which could remove a blocking mount
    /// or a required weather source unseen. The refusal names each key and never its value, which could be a device's
    /// address put in the wrong setting: <see cref="RoofSafetyOptionsValidator.FindUnreadable"/> finds the keys first, and
    /// the binder's own exception, which repeats the value, is never passed on.
    /// </summary>
    internal static void BindStrictly(IConfigurationSection section, RoofSafetyOptions options)
    {
        var unreadable = RoofSafetyOptionsValidator.FindUnreadable(section);
        if (unreadable.Count > 0)
        {
            throw new OptionsValidationException(RoofSafetyOptions.SectionName, typeof(RoofSafetyOptions), unreadable);
        }

        try
        {
            section.Bind(options, binder => binder.ErrorOnUnknownConfiguration = true);
        }
        catch (InvalidOperationException)
        {
            // Not expected once the keys above are checked. Its message and inner exceptions repeat the value: not passed on.
            throw new OptionsValidationException(RoofSafetyOptions.SectionName, typeof(RoofSafetyOptions),
            [
                $"The {RoofSafetyOptions.SectionName} settings cannot be bound: a value is not of its setting's type, or a key is not a setting. "
                + "The value is not repeated, since it could be a device's address."
            ]);
        }
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
/// <see cref="RoofSafetyOptions.WeatherSources"/>, in the order configured, and the own UniqueIDs they refuse. Made once,
/// from the validated options (settings the controller cannot use stop the start, <c>ValidateOnStart</c>): a change to the
/// devices or the own UniqueIDs takes a restart.
/// </summary>
public sealed class RoofSafetyConfiguredSources
{
    public RoofSafetyConfiguredSources(IOptions<RoofSafetyOptions> options, IHttpClientFactory clients)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clients);
        var value = options.Value;
        OwnAlpacaUniqueIds = [.. (value.OwnAlpacaUniqueIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())];
        Telescopes = [.. (value.Telescopes ?? []).Select(device => new AlpacaTelescopeParkSource(device, clients))];
        WeatherSources = [.. (value.WeatherSources ?? []).Select(device => new AlpacaSafetyMonitorSource(device, clients, OwnAlpacaUniqueIds))];
    }

    /// <summary>
    /// This installation's own Alpaca devices' UniqueIDs as the weather sources enforce them: read at start with the
    /// devices, so the list the status publishes (and the Alpaca server's SafetyMonitor trusts) is always the one refused.
    /// </summary>
    public IReadOnlyList<string> OwnAlpacaUniqueIds { get; }

    /// <summary>The configured mounts.</summary>
    public IReadOnlyList<ITelescopeParkSource> Telescopes { get; }

    /// <summary>The configured weather sources.</summary>
    public IReadOnlyList<IWeatherSafetySource> WeatherSources { get; }
}
