using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Web.Components;
using HVO.RoofControllerV4.Web.Supervision;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.Web;

/// <summary>
/// The roof controller's web UI: a separate process in the controller's container, started by its supervisor
/// (container/roof-supervisor.sh), on its own port (8088). It reaches the controller only through the client library,
/// over the container's loopback.
/// </summary>
public class Program
{
    /// <summary>The web UI's liveness: answers whenever the process does (the container's health check reports it).</summary>
    public const string HealthLivePath = "/health/live";

    /// <summary>
    /// The defaults a settings file would hold (the web UI has none), below every other source, so the environment
    /// (Logging__LogLevel__*) still overrides them. Request logging stays off: the health check calls every 30 s.
    /// </summary>
    private static readonly Dictionary<string, string?> DefaultSettings = new()
    {
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
    };

    public static int Main(string[] args)
    {
        WebApplication app;
        try
        {
            app = BuildApp(args);
        }
        catch (RoofWebSettingsException ex)
        {
            Console.Error.WriteLine($"The roof controller's web UI did not start: {ex.Message}");
            return 1;
        }

        app.Run();
        return 0;
    }

    /// <summary>
    /// Builds the web UI from <paramref name="args"/> and the environment. <paramref name="customize"/> runs after the
    /// services are registered (tests replace the controller client and the host there).
    /// </summary>
    internal static WebApplication BuildApp(string[] args, Action<WebApplicationBuilder>? customize = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = DefaultSettings });

        var options = builder.Configuration.GetSection(RoofWebOptions.SectionName).Get<RoofWebOptions>() ?? new RoofWebOptions();
        var problems = options.Validate(builder.Environment.IsDevelopment());
        if (problems.Count > 0)
        {
            throw new RoofWebSettingsException(string.Join(" ", problems));
        }

        builder.WebHost.UseUrls(options.Urls);
        if (!string.IsNullOrWhiteSpace(options.Certificate.Path))
        {
            var certificate = LoadCertificate(options.Certificate);
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
        }

        ConfigureServices(builder);
        customize?.Invoke(builder);

        var app = builder.Build();
        Configure(app);
        return app;
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<RoofWebOptions>().Bind(builder.Configuration.GetSection(RoofWebOptions.SectionName));

        // The supervisor waits HVO_SUPERVISOR_UI_STOP_SECONDS (2 s) for the web UI after docker stop, then kills it.
        services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(1));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = provider.GetRequiredService<IOptions<RoofWebOptions>>().Value.ControllerUrl,
            // Over loopback an answer is quick; a controller that takes longer is reported as not answering.
            RequestTimeout = TimeSpan.FromSeconds(3),
            LoggerFactory = provider.GetRequiredService<ILoggerFactory>(),
        }));
        services.AddSingleton<SupervisorStateReader>();
        services.AddSingleton<ControllerForcedRestart>();
        services.AddSingleton<RoofWebStatusProbe>();

        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddHealthChecks();
    }

    private static void Configure(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            await next(context);
        });

        app.UseAntiforgery();
        app.MapStaticAssets();
        app.MapHealthChecks(HealthLivePath);
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
    }

    /// <summary>Loads the HTTPS certificate. Its password never appears in a message.</summary>
    internal static X509Certificate2 LoadCertificate(RoofWebCertificateOptions certificate)
    {
        string? password = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(certificate.PasswordFile))
            {
                password = File.ReadAllText(certificate.PasswordFile).TrimEnd('\r', '\n');
            }

            return X509CertificateLoader.LoadPkcs12FromFile(certificate.Path!, password);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new RoofWebSettingsException(
                $"The certificate {certificate.Path} could not be loaded ({ex.GetType().Name}). Check that it is a .pfx file and that {RoofWebOptions.SectionName}:Certificate:PasswordFile holds its password.");
        }
    }
}

/// <summary>The web UI's settings cannot be used. The message names the settings, never a secret.</summary>
public sealed class RoofWebSettingsException(string message) : Exception(message);
