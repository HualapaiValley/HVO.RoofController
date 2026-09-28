using System;
using System.Net.Http;
using System.Text.Json.Serialization;
using Asp.Versioning;
using HVO.Enterprise.Telemetry;
using HVO.Enterprise.Telemetry.OpenTelemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.HostedServices;
using HVO.RoofControllerV4.RPi.Middleware;
using HVO.RoofControllerV4.RPi.HealthChecks;
using HVO.RoofControllerV4.RPi.Hubs;
using HVO.Iot.Devices.Abstractions;
using HVO.Iot.Devices.Implementation;

using System.Runtime.Loader;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.RPi.Logging;
using HVO.RoofControllerV4.RPi.Services;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using HVO.RoofControllerV4.RPi.Controllers.Camera;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace HVO.RoofControllerV4.RPi;

public class Program
{
    /// <summary>Docker secrets directory: each file becomes the configuration key it is named after.</summary>
    internal const string SecretsDirectory = "/run/secrets";

    public static int Main(string[] args)
    {
        if (args.Contains(DeploymentValidator.CommandLineSwitch, StringComparer.Ordinal))
        {
            // Pre-deployment check (deploy-roofcontroller-rpi.sh): reads the same configuration sources as the controller,
            // but never builds the host, opens a listener or touches the HAT.
            return RunValidation(args, SecretsDirectory, Console.Out);
        }

        var builder = CreateBuilder(args, SecretsDirectory);

        ApplyHardwareDetectionOverrides(builder.Configuration);
        ConfigureServices(builder.Services, builder.Configuration, builder.Environment);

        var app = builder.Build();

        // The roof stop on shutdown is registered by RoofControllerServiceV4Host (ApplicationStopping -> ShutdownAsync).
        Configure(app);

        app.Run();
        return 0;
    }

    /// <summary>
    /// Runs the deployment check against the configuration the controller would load from <paramref name="args"/>
    /// (without <see cref="DeploymentValidator.CommandLineSwitch"/>) and <paramref name="secretsDirectory"/>, writes the
    /// report to <paramref name="output"/> and returns the exit code. A configuration that cannot be loaded at all (for
    /// example malformed JSON) is reported as a problem with the exception type and message, never with values.
    /// </summary>
    internal static int RunValidation(string[] args, string secretsDirectory, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        ConfigurationManager? configuration = null;
        try
        {
            // The switch is not a setting: left in, the command-line provider would take the next argument as its value.
            var builder = CreateBuilder(args.Where(arg => arg != DeploymentValidator.CommandLineSwitch).ToArray(), secretsDirectory);
            configuration = builder.Configuration;
            return DeploymentValidator.Run(configuration, builder.Environment, TimeProvider.System, output);
        }
        catch (Exception ex)
        {
            return DeploymentValidator.ReportFailure(ex, output);
        }
        finally
        {
            // Releases the file watchers of the appsettings and secrets providers; the host is never built.
            configuration?.Dispose();
        }
    }

    private static WebApplicationBuilder CreateBuilder(string[] args, string secretsDirectory)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Docker secrets: a file named e.g. RoofControllerSecurity__ApiKeys__0__Key or BlueIris__Password under
        // the secrets directory becomes that configuration key. Never commit keys or passwords to appsettings.
        builder.Configuration.AddKeyPerFile(secretsDirectory, optional: true);
        return builder;
    }


    private static void ConfigureServices(IServiceCollection services, ConfigurationManager Configuration, IWebHostEnvironment Environment)
    {
        services.AddOptions();

        // Give the hosted-service stop path (verified roof stop) and long-lived camera streams time to finish on
        // SIGTERM; docker-compose stop_grace_period (30 s) must stay above this.
        services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(20));

        services.Configure<RoofControllerOptionsV4>(Configuration.GetSection(nameof(RoofControllerOptionsV4)));
        services.AddSingleton<IValidateOptions<RoofControllerOptionsV4>, RoofControllerOptionsV4Validator>();
        services.Configure<RoofControllerHostOptionsV4>(Configuration.GetSection(nameof(RoofControllerHostOptionsV4)));
        ConfigureTelemetry(services, Configuration, Environment);

        // API keys, console cookie, policies and authentication state for Blazor (docs/security.md)
        services.AddRoofControllerSecurity(Configuration);

        // Add Razor Components for Blazor Server
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // One per circuit: tells the console when the browser connection drops, so it stops renewing the operator lease.
        services.AddScoped<ConsoleCircuitMonitor>();
        services.AddScoped<CircuitHandler>(serviceProvider => serviceProvider.GetRequiredService<ConsoleCircuitMonitor>());

        services.AddSingleton<IGpioControllerClient>(_ => GpioControllerClientFactory.CreateAutoSelecting());

        // The HAT answers on the Pi's I2C bus, or on the HAT emulator in emulator mode (HatEmulator:Enabled, refused
        // outside Development unless HatEmulator:AllowOutsideDevelopment is set).
        services.AddOptions<HatEmulatorOptions>().Bind(Configuration.GetSection(HatEmulatorOptions.SectionName));
        services.AddSingleton<RoofHatConnection>();
        services.AddFourRelayFourInputHat(options =>
        {
            options.DigitalInputPollInterval = TimeSpan.FromMilliseconds(25);
            options.OwnsClientFromFactory = true;
            options.ClientFactory = serviceProvider => serviceProvider.GetRequiredService<RoofHatConnection>()
                .CreateClient(options, serviceProvider.GetRequiredService<ILoggerFactory>());
        });

        services.AddHostedService<RoofControllerServiceV4Host>();

        // Register RoofController based on configuration
        services.AddSingleton<IRoofControllerServiceV4, RoofControllerServiceV4>();
        services.AddScoped<FooterStatusService>();

        // Live status hub (/hubs/roof) for the separate clients: JSON with string enums, as the REST API writes them.
        services.AddSignalR()
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .AddHubOptions<RoofStatusHub>(options =>
            {
                options.SupportedProtocols = [RoofStatusHubContract.Protocol];
                // Clients send nothing but the handshake and pings.
                options.MaximumReceiveMessageSize = 4 * 1024;
            });
        services.AddSingleton<IRoofStatusSender, HubRoofStatusSender>();
        services.AddSingleton<RoofStatusBroadcaster>();
        services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<RoofStatusBroadcaster>());

        services.Configure<ConsoleLogBufferOptions>(Configuration.GetSection("ConsoleLogBuffer"));
        services.AddSingleton<ConsoleLogBuffer>();
        services.AddSingleton<ILoggerProvider, ConsoleLogLoggerProvider>();

        // Add exception handling middleware
        // NOTE: Use built-in exception handling instead of custom error controllers
        // This provides consistent error responses and integrates with Problem Details
        services.AddExceptionHandler<HvoServiceExceptionHandler>();

        // Add health checks
        // NOTE: Use built-in ASP.NET Core health check endpoints instead of creating custom controllers
        // The MapHealthChecks middleware below provides all necessary endpoints:
        // - /health (detailed health information)
        // - /health/ready (readiness probes for load balancers)  
        // - /health/live (liveness probes for container orchestration)
        // Do NOT create duplicate HealthController - use the built-in functionality
        services.AddHealthChecks()
            .AddCheck<RoofControllerHealthCheck>("roof_controller", tags: ["roof", "hardware"])
            .AddCheck<RoofIdentityStoreHealthCheck>("identity_store", tags: ["security"]);

        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        // NOTE: Use built-in OpenAPI/Swagger functionality instead of custom documentation endpoints
        // This provides automatic API documentation generation from controller attributes
        services.AddOpenApi("v4");

        services.AddApiVersioning(setup =>
        {
            setup.DefaultApiVersion = new ApiVersion(4, 0);
            setup.AssumeDefaultVersionWhenUnspecified = true;
            setup.ReportApiVersions = true;
            setup.ApiVersionReader = new UrlSegmentApiVersionReader(); // ApiVersionReader.Combine(new QueryStringApiVersionReader("version"), new HeaderApiVersionReader("api-version"), new MediaTypeApiVersionReader("version")); 
        }).AddApiExplorer(options =>
        {
            options.GroupNameFormat = "'v'VVV";
            options.SubstituteApiVersionInUrl = true;
        });

        // Configure Problem Details for consistent error responses
        services.AddProblemDetails(configure =>
        {
            configure.CustomizeProblemDetails = context =>
            {
                // Add common properties to all problem details
                context.ProblemDetails.Instance = $"{context.HttpContext.Request.Method} {context.HttpContext.Request.Path}";
                context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
                context.ProblemDetails.Extensions["timestamp"] = DateTime.UtcNow;

                // Add request information for debugging
                if (context.HttpContext.Request.Headers.ContainsKey("User-Agent"))
                {
                    context.ProblemDetails.Extensions["userAgent"] = context.HttpContext.Request.Headers["User-Agent"].ToString();
                }
            };
        });

        // Enable endpoints API explorer for OpenAPI
        services.AddEndpointsApiExplorer();

        // Add MVC + Views + JSON enum string serialization (single registration to avoid overriding options)
        services.AddControllersWithViews()
            .AddJsonOptions(options =>
            {
                if (!options.JsonSerializerOptions.Converters.Any(c => c is JsonStringEnumConverter))
                {
                    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                }
            });

        // Add HttpClient for API calls
        services.AddHttpClient();

        // Blue Iris MJPEG proxy: credentials from configuration (BlueIris section, secrets via env/Docker secrets),
        // finite connect timeout; header and idle timeouts are enforced per stream by CameraController.
        services.Configure<BlueIrisOptions>(Configuration.GetSection(BlueIrisOptions.SectionName));
        services.AddSingleton<CameraStreamLimiter>();
        services.AddHttpClient<BlueIrisCameraClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
            {
                var connectTimeout = serviceProvider.GetRequiredService<IOptions<BlueIrisOptions>>().Value.ConnectTimeout;
                return new SocketsHttpHandler
                {
                    ConnectTimeout = connectTimeout > TimeSpan.Zero ? connectTimeout : TimeSpan.FromSeconds(5),
                    AllowAutoRedirect = false,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
                };
            })
            .RedactLoggedHeaders(_ => true);

        // Add HttpContextAccessor for Blazor components
        services.AddHttpContextAccessor();
    }

    private static void ConfigureTelemetry(
        IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        services.AddTelemetry(configuration.GetSection("Telemetry"));

        var endpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        services.AddOpenTelemetryExport(options =>
        {
            options.EnableTraceExport = false;
            options.EnableMetricsExport = false;
            options.EnableStandardMeters = true;
            options.AdditionalActivitySources.Add(RoofControllerTelemetry.InstrumentationName);
            options.AdditionalMeterNames.Add(RoofControllerTelemetry.InstrumentationName);
        });

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddDetector(serviceProvider =>
                new RoofHatModeResourceDetector(serviceProvider.GetRequiredService<RoofHatConnection>())))
            .WithTracing(tracerProvider => tracerProvider
                .AddSource(RoofControllerTelemetry.InstrumentationName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(meterProvider => meterProvider
                .AddMeter(RoofControllerTelemetry.InstrumentationName)
                .AddOtlpExporter());
    }

    private static void ApplyHardwareDetectionOverrides(ConfigurationManager configuration)
    {
        var section = configuration.GetSection("HardwareDetection");
        if (!section.Exists())
        {
            return;
        }

        SetIfUnset("HVO_FORCE_RASPBERRY_PI", section["ForceRaspberryPi"]);
        SetIfUnset("HVO_CONTAINER_RPI_HINT", section["ContainerRpiHint"]);
        SetIfUnset(IGpioControllerClient.UseRealHardwareEnvironmentVariable, section["UseRealGpio"]);

        static void SetIfUnset(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                return;
            }

            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static void Configure(WebApplication app)
    {
        var apiKeyViewer = new AuthorizeAttribute(RoofControllerSecurityDefaults.ViewerPolicy)
        {
            AuthenticationSchemes = RoofSecurityServiceCollectionExtensions.ApiKeyOrCookieSchemes
        };

        // Add exception handling middleware
        app.UseExceptionHandler();

        // Add Problem Details middleware for consistent error responses
        app.UseStatusCodePages();

        if (app.Environment.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else if (RoofSecurityStartup.IsHttpsConfigured(app.Configuration))
        {
            // HSTS only when an HTTPS endpoint actually exists; otherwise browsers would be pinned to a dead port.
            app.UseHsts();
        }

        // No UseHttpsRedirection: API clients must not silently follow a redirect with their key. Plain-HTTP requests
        // from other hosts get 403 https_required instead (loopback and /health/live|ready are exempt).
        app.UseMiddleware<RequireHttpsMiddleware>();

        // Serve static web assets (including the generated .styles.css bundle)
        app.UseStaticFiles();
        app.UseRouting();

        app.UseMiddleware<OriginCheckMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<BlazorHubAuthorizationMiddleware>();
        app.UseAuthorization();
        app.UseAntiforgery();

        // OpenAPI document (/openapi/v4.json): open in Development, Admin API key elsewhere. Scalar UI is Development-only.
        var openApi = app.MapOpenApi();
        if (app.Environment.IsDevelopment())
        {
            app.MapScalarApiReference();
        }
        else
        {
            openApi.RequireAuthorization(new AuthorizeAttribute(RoofControllerSecurityDefaults.AdminPolicy)
            {
                AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme
            });
        }

        // Health endpoints. Do NOT duplicate these with custom controllers.
        // /health: detailed report for people (Viewer key or signed-in console). Keeps 503 for Unhealthy so HTTP-only
        // monitors still see failures; clients must read the JSON body on 503 as well.
        app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var response = new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.Select(x => new
                    {
                        name = x.Key,
                        status = x.Value.Status.ToString(),
                        description = x.Value.Description,
                        data = x.Value.Data,
                        duration = x.Value.Duration.ToString(),
                        // Type name only: exception messages can carry paths, bus errors or upstream URLs.
                        exception = x.Value.Exception?.GetType().Name,
                        tags = x.Value.Tags
                    }),
                    totalDuration = report.TotalDuration.ToString()
                };
                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(response));
            }
        }).RequireAuthorization(apiKeyViewer);

        // Readiness probe (anonymous, status text only): hardware-tagged checks. Used by the Docker HEALTHCHECK and deploy.
        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("hardware")
        }).AllowAnonymous();

        // Liveness probe (anonymous): healthy whenever the process answers.
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => false
        }).AllowAnonymous();

        // POST /account/login and /account/logout for the web console (the /login page itself is a Razor component).
        app.MapRoofAccountEndpoints();

        // POST /console/stop: Stop from the reconnect dialog while the Blazor circuit is down.
        app.MapRoofConsoleEndpoints();

        // Map Razor components for Blazor Server
        app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode();

        app.MapControllers();

        // Live status for API clients: the Viewer policy with an API key or a session (never the console cookie).
        app.MapHub<RoofStatusHub>(RoofStatusHubContract.Path)
            .RequireAuthorization(new AuthorizeAttribute(RoofControllerSecurityDefaults.ViewerPolicy)
            {
                AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme
            });

        RoofSecurityStartup.ReportSecurityPosture(app.Services, app.Configuration, app.Environment);
        HatEmulatorStartup.ReportHatMode(app.Services);
    }
}
