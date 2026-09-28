using HVO.RoofControllerV4.Simulation.Emulator;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Emulator;

/// <summary>
/// The HAT emulator: the emulated roof plant (SM-I-010 HAT, SMVector drive, ME-8108 limit switches, the documented
/// wiring) served on a TCP register port for the controller's <c>HatEmulator</c> mode, with an HTTP control API for
/// injecting faults and reading the plant. For development and tests only; it never touches hardware.
/// </summary>
public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        {
            // Loopback unless ASPNETCORE_URLS or --urls says otherwise (the container listens on all interfaces).
            builder.WebHost.UseUrls(EmulatorHostOptions.DefaultControlUrl);
        }

        ConfigureServices(builder.Services, builder.Configuration);
        var app = builder.Build();

        // The session and the register port check the Emulator settings as they are built: build them now, so bad
        // settings stop the process before it listens rather than part way through the host start.
        app.Services.GetRequiredService<HatEmulatorServer>();

        app.MapGet("/", () => Results.Redirect(EmulatorApi.Prefix + "/status"));
        EmulatorApi.Map(app);
        app.Run();
    }

    internal static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmulatorHostOptions>(configuration.GetSection(EmulatorHostOptions.SectionName));
        // Enum values by name only: a number, or a list of names for an enum that is not [Flags], would reach the plant
        // as a value it may not model or one the request did not name.
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new NamedEnumConverter()));
        services.AddProblemDetails();

        services.AddSingleton(sp => new HatEmulatorSession(sp.GetRequiredService<IOptions<EmulatorHostOptions>>().Value.SessionOptions()));
        services.AddSingleton(sp =>
        {
            var session = sp.GetRequiredService<HatEmulatorSession>();
            var endpoint = sp.GetRequiredService<IOptions<EmulatorHostOptions>>().Value.RegisterEndPoint();
            return new HatEmulatorServer(session.CurrentClient, endpoint, sp.GetRequiredService<ILogger<HatEmulatorServer>>());
        });
        services.AddHostedService<HatEmulatorHostedService>();
    }
}

/// <summary>Starts the register port with the host and stops it first on shutdown.</summary>
internal sealed class HatEmulatorHostedService(HatEmulatorServer server, HatEmulatorSession session, ILogger<HatEmulatorHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        server.Start();
        logger.LogWarning(
            "HAT EMULATOR: register port {Endpoint}, time scale {Scale}. This process emulates the roof, the drive and the HAT; it controls no hardware.",
            server.LocalEndPoint,
            session.Clock.Scale);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await server.DisposeAsync().ConfigureAwait(false);
}
