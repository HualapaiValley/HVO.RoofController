using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Alpaca.Discovery;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Alpaca.Roof;
using HVO.RoofControllerV4.Alpaca.Security;
using HVO.RoofControllerV4.Client;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.RoofControllerV4.Alpaca;

/// <summary>
/// The roof's ASCOM Alpaca server (#220): a process of its own, apart from the controller and the web UI, that serves
/// the roof as an Alpaca Dome and reaches the controller only through the client library (its REST API and status
/// hub), with its own API key. It is off unless <c>Alpaca:Enabled</c> is true, listens only on the addresses it is
/// given, and answers only its LAN. This phase reads the roof and stops it; it never opens or closes it.
/// </summary>
/// <remarks>
/// <c>--discovery-only</c> runs the discovery responder alone, for a computer whose Alpaca server runs in a container
/// that cannot receive the LAN's broadcasts (docs/alpaca.md).
/// </remarks>
public class Program
{
    /// <summary>Runs only the discovery responder.</summary>
    public const string DiscoveryOnlyArgument = "--discovery-only";

    /// <summary>What the program says when it is off.</summary>
    public const string OffMessage =
        "The Alpaca server is off (Alpaca:Enabled is not true), so nothing listens and discovery is not answered. docs/alpaca.md says how to turn it on.";

    /// <summary>
    /// The defaults a settings file would hold (the server has none), below every other source, so the environment
    /// (Logging__LogLevel__*) still overrides them.
    /// </summary>
    private static readonly Dictionary<string, string?> DefaultSettings = new()
    {
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
    };

    public static int Main(string[] args)
    {
        var discoveryOnly = args.Contains(DiscoveryOnlyArgument, StringComparer.Ordinal);
        var rest = args.Where(argument => argument != DiscoveryOnlyArgument).ToArray();
        try
        {
            if (discoveryOnly)
            {
                using var host = BuildDiscoveryHost(rest);
                if (host is null)
                {
                    Console.Error.WriteLine(OffMessage);
                    return 0;
                }

                host.Run();
                return 0;
            }

            var app = BuildApp(rest);
            if (app is null)
            {
                Console.Error.WriteLine(OffMessage);
                return 0;
            }

            app.Run();
            return 0;
        }
        catch (AlpacaSettingsException ex)
        {
            Console.Error.WriteLine($"The Alpaca server did not start: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Builds the server from <paramref name="args"/> and the environment, or returns null when it is off.
    /// <paramref name="customize"/> runs after the services are registered (tests replace the controller client and the
    /// host there).
    /// </summary>
    /// <exception cref="AlpacaSettingsException">The settings cannot be used.</exception>
    internal static WebApplication? BuildApp(string[] args, Action<WebApplicationBuilder>? customize = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = DefaultSettings });
        var options = ReadOptions(builder.Configuration, discoveryOnly: false);
        if (options is null)
        {
            return null;
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            foreach (var address in options.ParsedListenAddresses)
            {
                kestrel.Listen(address, options.Port, listen => listen.Protocols = HttpProtocols.Http1);
            }
        });

        var key = LoadKey(options.ControllerApiKeyFile!);
        var authority = LoadAuthority(options.ControllerCaCertificatePath);
        var ids = AlpacaDeviceIds.Load(options.EffectiveDataPath);
        ConfigureServices(builder.Services, options, key, authority, ids);
        customize?.Invoke(builder);

        var app = builder.Build();
        Configure(app);
        return app;
    }

    /// <summary>Builds the discovery responder alone, or returns null when the server is off.</summary>
    /// <exception cref="AlpacaSettingsException">The settings cannot be used.</exception>
    internal static IHost? BuildDiscoveryHost(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });
        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = DefaultSettings });
        var options = ReadOptions(builder.Configuration, discoveryOnly: true);
        if (options is null)
        {
            return null;
        }

        if (!options.Discovery.Enabled)
        {
            throw new AlpacaSettingsException($"{DiscoveryOnlyArgument} answers discovery, but {AlpacaOptions.SectionName}:Discovery:Enabled is false.");
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddHostedService(provider => new AlpacaDiscoveryResponder(options, provider.GetRequiredService<ILogger<AlpacaDiscoveryResponder>>(), discoveryOnly: true));
        return builder.Build();
    }

    /// <summary>The settings, checked; null when the server is off.</summary>
    /// <exception cref="AlpacaSettingsException">They cannot be used.</exception>
    internal static AlpacaOptions? ReadOptions(IConfiguration configuration, bool discoveryOnly)
    {
        AlpacaOptions options;
        try
        {
            options = configuration.GetSection(AlpacaOptions.SectionName).Get<AlpacaOptions>() ?? new AlpacaOptions();
        }
        catch (InvalidOperationException ex)
        {
            throw new AlpacaSettingsException($"The {AlpacaOptions.SectionName} settings cannot be read: {ex.Message}");
        }

        if (!options.Enabled)
        {
            return null;
        }

        var problems = options.Validate(discoveryOnly);
        return problems.Count > 0 ? throw new AlpacaSettingsException(string.Join(" ", problems)) : options;
    }

    /// <summary>
    /// Reads the server's API key for the controller. The file must hold one key on one line, and only its owner may read
    /// or change it. The key never appears in a message.
    /// </summary>
    /// <exception cref="AlpacaSettingsException">The file cannot be read, others can read it, or it holds more than a key.</exception>
    internal static string LoadKey(string path)
    {
        string key;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
                {
                    throw new AlpacaSettingsException(
                        $"{AlpacaOptions.SectionName}:ControllerApiKeyFile names {path}, which other users can read or change. Run: chmod 600 {path}");
                }
            }

            key = File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AlpacaSettingsException($"{AlpacaOptions.SectionName}:ControllerApiKeyFile names {path}, which could not be read ({ex.GetType().Name}).");
        }

        if (key.Length == 0 || key.Any(c => c is < ' ' or > '~') || key.Trim().Length != key.Length)
        {
            throw new AlpacaSettingsException(
                $"{AlpacaOptions.SectionName}:ControllerApiKeyFile names {path}, which must hold one API key on one line. {RoofCredential.InvalidHeaderValue}");
        }

        return key;
    }

    private static X509Certificate2? LoadAuthority(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return RoofCertificateAuthority.Load(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new AlpacaSettingsException(
                $"{AlpacaOptions.SectionName}:ControllerCaCertificatePath names {path}, which is not a CA certificate that can be read ({ex.GetType().Name}).");
        }
    }

    private static void ConfigureServices(IServiceCollection services, AlpacaOptions options, string key, X509Certificate2? authority, AlpacaDeviceIds ids)
    {
        services.AddSingleton(options);
        services.AddSingleton(ids);
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(2));

        services.AddSingleton(provider => new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = options.ControllerUrl,
            Credential = new RoofApiKeyCredential(key),
            ServerCertificateSha256 = string.IsNullOrWhiteSpace(options.ControllerCertificateSha256) ? null : options.ControllerCertificateSha256,
            ServerCaCertificate = authority,
            // Alpaca clients wait seconds, not tens of seconds; a controller that takes longer is reported as unavailable.
            RequestTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(5),
            TimeProvider = provider.GetRequiredService<TimeProvider>(),
            ProgramAssembly = typeof(Program).Assembly,
            LoggerFactory = provider.GetRequiredService<ILoggerFactory>(),
        }));
        services.AddSingleton<AlpacaRoof>();
        services.AddHostedService(provider => provider.GetRequiredService<AlpacaRoof>());
        services.AddSingleton<AlpacaConnections>();
        services.AddSingleton<AlpacaDome>();
        services.AddSingleton<AlpacaTransactions>();
        if (options.Discovery.Enabled)
        {
            services.AddHostedService(provider => new AlpacaDiscoveryResponder(options, provider.GetRequiredService<ILogger<AlpacaDiscoveryResponder>>()));
        }

        // Polling is limited per client address; AbortSlew never is. Routing has run by then (WebApplication routes first),
        // so the exemption is the member the device route matched, however the path was written.
        services.AddRateLimiter(limiter =>
        {
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, IPAddress>(context =>
                IsAbortSlew(context)
                    ? RateLimitPartition.GetNoLimiter(IPAddress.None)
                    : RateLimitPartition.GetTokenBucketLimiter(context.Connection.RemoteIpAddress ?? IPAddress.None, _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = options.RequestsPerSecondPerAddress * 2,
                        TokensPerPeriod = options.RequestsPerSecondPerAddress,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            limiter.OnRejected = (rejected, cancellationToken) =>
            {
                rejected.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                rejected.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                return new ValueTask(rejected.HttpContext.Response.WriteAsync("Too many requests from this address: slow down.", cancellationToken));
            };
        });
    }

    /// <summary>True for a request the device route takes as AbortSlew: a PUT whose matched member is <c>abortslew</c>.</summary>
    internal static bool IsAbortSlew(HttpContext context)
        => HttpMethods.IsPut(context.Request.Method)
            && context.GetEndpoint() is not null
            && context.GetRouteValue(AlpacaEndpoints.MemberRouteValue) is string member
            && string.Equals(member, AlpacaDome.AbortSlewMember, StringComparison.Ordinal);

    private static void Configure(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            await next(context);
        });

        // Refused addresses, hosts and origins first, before anything reads the request.
        app.UseMiddleware<AlpacaAccessFilter>();
        app.UseRateLimiter();
        app.MapAlpaca();
    }
}
