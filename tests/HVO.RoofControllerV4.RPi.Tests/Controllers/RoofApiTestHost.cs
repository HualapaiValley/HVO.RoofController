using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>Obviously fake API keys used only by the tests (each at least 24 characters).</summary>
internal static class TestApiKeys
{
    public const string Viewer = "test-viewer-key-not-a-real-secret-01";
    public const string Operator = "test-operator-key-not-a-real-secret-02";
    public const string Admin = "test-admin-key-not-a-real-secret-03";

    /// <summary>Configured through KeySha256 only (the plain value is never in configuration).</summary>
    public const string HashedViewer = "test-hashed-viewer-key-not-a-real-secret-04";

    public static string Sha256Hex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// WebApplicationFactory for the RPi host with a strict roof-service mock, the hardware host service removed, and test
/// API keys configured. Development environment by default (RequireHttps off), Production on request.
/// </summary>
internal sealed class RoofApiTestHost : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;
    private readonly string _environment;
    private readonly IPAddress? _remoteIp;
    private readonly Action<IServiceCollection>? _configureServices;

    public RoofApiTestHost(
        Mock<IRoofControllerServiceV4>? roofService = null,
        IDictionary<string, string?>? settings = null,
        string environment = "Development",
        IPAddress? remoteIp = null,
        Action<IServiceCollection>? configureServices = null,
        bool includeDefaultKeys = true)
    {
        RoofService = roofService ?? RoofServiceMock.Create();
        _settings = includeDefaultKeys ? DefaultKeySettings() : new Dictionary<string, string?>();
        _settings["BlueIris:BaseUrl"] = string.Empty;
        if (settings is not null)
        {
            foreach (var (key, value) in settings)
            {
                _settings[key] = value;
            }
        }

        _environment = environment;
        _remoteIp = remoteIp;
        _configureServices = configureServices;
    }

    public Mock<IRoofControllerServiceV4> RoofService { get; }

    public static Dictionary<string, string?> DefaultKeySettings() => new()
    {
        ["RoofControllerSecurity:ApiKeys:0:Name"] = "test-viewer",
        ["RoofControllerSecurity:ApiKeys:0:Role"] = RoofControllerApiContract.ViewerRole,
        ["RoofControllerSecurity:ApiKeys:0:Key"] = TestApiKeys.Viewer,
        ["RoofControllerSecurity:ApiKeys:1:Name"] = "test-operator",
        ["RoofControllerSecurity:ApiKeys:1:Role"] = RoofControllerApiContract.OperatorRole,
        ["RoofControllerSecurity:ApiKeys:1:Key"] = TestApiKeys.Operator,
        ["RoofControllerSecurity:ApiKeys:2:Name"] = "test-admin",
        ["RoofControllerSecurity:ApiKeys:2:Role"] = RoofControllerApiContract.AdminRole,
        ["RoofControllerSecurity:ApiKeys:2:Key"] = TestApiKeys.Admin,
        ["RoofControllerSecurity:ApiKeys:3:Name"] = "test-hashed-viewer",
        ["RoofControllerSecurity:ApiKeys:3:Role"] = "roofviewer",
        ["RoofControllerSecurity:ApiKeys:3:KeySha256"] = TestApiKeys.Sha256Hex(TestApiKeys.HashedViewer)
    };

    /// <summary>Client that does not follow redirects and does not store cookies.</summary>
    public HttpClient CreateApiClient(string? apiKey = null, bool https = false)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri(https ? "https://localhost" : "http://localhost")
        });
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add(RoofControllerApiContract.ApiKeyHeaderName, apiKey);
        }

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        builder.ConfigureServices(services =>
        {
            // No hardware host service: it would call Initialize/ShutdownAsync on the mock.
            foreach (var descriptor in services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name == "RoofControllerServiceV4Host")
                .ToList())
            {
                services.Remove(descriptor);
            }

            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IRoofControllerServiceV4)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(RoofService.Object);
            services.PostConfigure<RoofControllerHostOptionsV4>(options => options.RestartOnFailureWaitTime = 42);

            if (_remoteIp is not null)
            {
                services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(_remoteIp));
            }

            _configureServices?.Invoke(services);
        });
    }

    /// <summary>TestServer reports no remote address (treated as loopback); this simulates a LAN client.</summary>
    private sealed class RemoteIpStartupFilter : IStartupFilter
    {
        private readonly IPAddress _address;

        public RemoteIpStartupFilter(IPAddress address) => _address = address;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = _address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>Strict roof-service mock with the members every request path may touch.</summary>
internal static class RoofServiceMock
{
    public static RoofStatusResponse Snapshot(
        RoofControllerStatus status = RoofControllerStatus.Closed,
        RoofMotionDirection motion = RoofMotionDirection.None,
        bool faultLatched = false,
        RoofRelayRegisterState relayState = RoofRelayRegisterState.Verified,
        int? relayMask = 0)
        => new(
            status,
            motion != RoofMotionDirection.None,
            RoofControllerStopReason.NormalStop,
            DateTimeOffset.UtcNow,
            IsWatchdogActive: motion != RoofMotionDirection.None,
            WatchdogSecondsRemaining: motion != RoofMotionDirection.None ? 59.5 : null,
            IsAtSpeed: false,
            IsUsingPhysicalHardware: true,
            IsIgnoringPhysicalLimitSwitches: false)
        {
            StatusVersion = 11,
            SnapshotUtc = DateTimeOffset.UtcNow,
            CommandedMotion = motion,
            RelayRegisterState = relayState,
            RelayRegisterMask = relayMask,
            IsFaultLatched = faultLatched,
            IsInitialized = true,
            InputsHealthy = true
        };

    public static Mock<IRoofControllerServiceV4> Create()
    {
        var mock = new Mock<IRoofControllerServiceV4>(MockBehavior.Strict);
        mock.SetupGet(s => s.Status).Returns(RoofControllerStatus.Closed);
        mock.SetupGet(s => s.IsInitialized).Returns(true);
        mock.SetupGet(s => s.IsServiceDisposed).Returns(false);
        mock.SetupGet(s => s.IsShuttingDown).Returns(false);
        mock.SetupGet(s => s.IsMoving).Returns(false);
        mock.SetupGet(s => s.LastStopReason).Returns(RoofControllerStopReason.NormalStop);
        mock.SetupGet(s => s.IsWatchdogActive).Returns(false);
        mock.SetupGet(s => s.WatchdogSecondsRemaining).Returns((double?)null);
        mock.SetupGet(s => s.IsAtSpeed).Returns(false);
        mock.SetupGet(s => s.IsUsingPhysicalHardware).Returns(true);
        mock.SetupGet(s => s.IsIgnoringPhysicalLimitSwitches).Returns(false);
        mock.SetupGet(s => s.LastTransitionUtc).Returns(DateTimeOffset.UtcNow);
        mock.Setup(s => s.RefreshStatus(It.IsAny<bool>()));
        mock.Setup(s => s.GetCurrentStatusSnapshot()).Returns(() => Snapshot());
        mock.Setup(s => s.GetConfigurationSnapshot()).Returns(new RoofControllerOptionsV4());
        mock.Setup(s => s.GetConfigurationState()).Returns(new RoofControllerConfigurationState(new RoofControllerOptionsV4(), 7));
        mock.Setup(s => s.Open()).Returns(Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening));
        mock.Setup(s => s.Close()).Returns(Result<RoofControllerStatus>.Success(RoofControllerStatus.Closing));
        mock.Setup(s => s.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Success(RoofControllerStatus.Stopped));
        mock.Setup(s => s.RenewLease()).Returns(() => Result<RoofStatusResponse>.Success(Snapshot()));
        mock.Setup(s => s.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Result<bool>.Success(true));
        return mock;
    }
}

/// <summary>JSON helpers matching the server (web defaults, string enums).</summary>
internal static class ApiJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(Options);
        return value ?? throw new AssertFailedException($"Response body was empty ({(int)response.StatusCode}).");
    }

    public static async Task<JsonElement> ReadElementAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new AssertFailedException($"Response ({(int)response.StatusCode}) is not JSON: {text}", ex);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Scriptable primary handler standing in for Blue Iris.</summary>
internal sealed class FakeUpstreamHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    public FakeUpstreamHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public List<(Uri? Uri, AuthenticationHeaderValue? Authorization)> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add((request.RequestUri, request.Headers.Authorization));
        }

        return _respond(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        // Shared across handler rotations in a test; nothing to release.
    }
}
