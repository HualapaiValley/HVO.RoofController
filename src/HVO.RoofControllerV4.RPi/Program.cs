using System;
using System.Text.Json.Serialization;
using Asp.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Scalar.AspNetCore;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.HostedServices;
using HVO.RoofControllerV4.RPi.Middleware;
using HVO.RoofControllerV4.RPi.HealthChecks;
using HVO.Iot.Devices.Abstractions;
using HVO.Iot.Devices.Implementation;

using System.Runtime.Loader;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.RPi.Logging;
using HVO.RoofControllerV4.RPi.Services;

namespace HVO.RoofControllerV4.RPi;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ApplyHardwareDetectionOverrides(builder.Configuration);
        ConfigureServices(builder.Services, builder.Configuration, builder.Environment);

        var app = builder.Build();

        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() => Console.WriteLine("IHostApplicationLifetime - ApplicationStopping"));

        Configure(app);

        app.Run();
    }


    private static void ConfigureServices(IServiceCollection services, ConfigurationManager Configuration, IWebHostEnvironment Environment)
    {
        services.AddOptions();
        services.Configure<RoofControllerOptionsV4>(Configuration.GetSection(nameof(RoofControllerOptionsV4)));
        services.AddSingleton<IValidateOptions<RoofControllerOptionsV4>, RoofControllerOptionsV4Validator>();
        services.Configure<RoofControllerHostOptionsV4>(Configuration.GetSection(nameof(RoofControllerHostOptionsV4)));

        // Add Razor Components for Blazor Server
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        services.AddSingleton<IGpioControllerClient>(_ => GpioControllerClientFactory.CreateAutoSelecting());

        services.AddFourRelayFourInputHat(options =>
        {
            options.DigitalInputPollInterval = TimeSpan.FromMilliseconds(25);
        });


    services.AddHostedService<RoofControllerServiceV4Host>();

    // Register RoofController based on configuration
    services.AddSingleton<IRoofControllerServiceV4, RoofControllerServiceV4>();
    services.AddScoped<FooterStatusService>();

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
            .AddCheck<RoofControllerHealthCheck>("roof_controller", tags: ["roof", "hardware"]);

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

        // Add HttpContextAccessor for Blazor components
        services.AddHttpContextAccessor();
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
        // Add exception handling middleware
        app.UseExceptionHandler();

        // Add Problem Details middleware for consistent error responses
        app.UseStatusCodePages();

        // Built-in OpenAPI endpoint - provides automatic API documentation
        // Available at: /openapi/v4.json
        app.MapOpenApi();

        if (app.Environment.IsDevelopment())
        {
            // Built-in interactive API documentation - provides Scalar UI
            // Available at: /scalar/v1 (interactive API explorer)
            app.MapScalarApiReference();
            app.UseDeveloperExceptionPage();
        }

        // In Development, disable HTTPS redirection to avoid cert prompts and warnings
        if (!app.Environment.IsDevelopment())
        {
            app.UseHttpsRedirection();
        }

        // Serve static web assets (including the generated .styles.css bundle)
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAntiforgery();

        app.UseAuthorization();

        // Add health check endpoints
        // IMPORTANT: These are the RECOMMENDED ASP.NET Core health check endpoints
        // Do NOT duplicate these with custom controllers - use these built-in endpoints:

        // Detailed health endpoint with comprehensive information
        // Use this for: monitoring dashboards, detailed health reporting, troubleshooting
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
                        exception = x.Value.Exception?.Message,
                        tags = x.Value.Tags
                    }),
                    totalDuration = report.TotalDuration.ToString()
                };
                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(response));
            }
        });

        // Readiness probe endpoint for load balancers and orchestration
        // Use this for: Kubernetes readiness probes, load balancer health checks
        // Only checks hardware-tagged components to determine if service is ready to serve traffic
        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("hardware")
        });

        // Liveness probe endpoint for container orchestration
        // Use this for: Kubernetes liveness probes, container restart decisions
        // Always returns healthy if the application is running (no specific checks)
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => false // Always returns healthy for liveness
        });

        // Map Razor components for Blazor Server
        app.MapRazorComponents<Components.App>()
            .AddInteractiveServerRenderMode();

        app.MapControllers();
    }
}
