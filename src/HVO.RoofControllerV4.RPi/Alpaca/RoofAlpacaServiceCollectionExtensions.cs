using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Alpaca;

/// <summary>Registers the controller's Alpaca command state and its settings (#220).</summary>
public static class RoofAlpacaServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="RoofAlpacaCommands"/> (also a hosted service, for its periodic check) and its settings,
    /// <c>RoofController:Alpaca</c> and <c>RoofController:Lease</c>, validated at start.
    /// </summary>
    public static IServiceCollection AddRoofAlpacaCommands(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<RoofAlpacaCommandOptions>()
            .Bind(configuration.GetSection(RoofAlpacaCommandOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RoofAlpacaCommandOptions>, RoofAlpacaCommandOptionsValidator>();
        services.AddOptions<RoofOperatorLeaseOptions>()
            .Bind(configuration.GetSection(RoofOperatorLeaseOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RoofOperatorLeaseOptions>, RoofOperatorLeaseOptionsValidator>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RoofAlpacaCommands>();
        services.AddHostedService(provider => provider.GetRequiredService<RoofAlpacaCommands>());

        // A refused credential on an Alpaca Close's routes raises the UIs' alert.
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, Security.RoofAlpacaAuthorizationResultHandler>();
        return services;
    }
}
