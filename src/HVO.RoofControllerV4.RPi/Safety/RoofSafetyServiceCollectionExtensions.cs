using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Safety;

/// <summary>Registers the safety service and the Open interlock (docs/safety-service.md).</summary>
public static class RoofSafetyServiceCollectionExtensions
{
    /// <summary>
    /// Adds the safety service, its options (<see cref="RoofSafetyOptions.SectionName"/>, validated at start) and the
    /// interlock. No source adapter is registered here yet: with none, the conditions are Unknown ("No safety sources
    /// configured"). A source is an <see cref="Common.Safety.ITelescopeParkSource"/> or
    /// <see cref="Common.Safety.IWeatherSafetySource"/> singleton.
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
}
