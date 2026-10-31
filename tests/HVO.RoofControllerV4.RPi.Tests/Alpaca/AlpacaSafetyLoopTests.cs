using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.Extensions.DependencyInjection;
using HVO.RoofControllerV4.Common.Safety;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The controller can never read its own weather back (#220): a weather source configured as this controller's own
/// Alpaca SafetyMonitor is refused with why, so it never decides the conditions, and the SafetyMonitor, which then has no
/// fresh required weather to publish, reads unsafe. Everything runs in memory: the controller, and the Alpaca server in
/// front of it that its source is pointed at.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AlpacaSafetyLoopTests
{
    [TestMethod]
    public async Task OurOwnSafetyMonitor_AsAWeatherSource_IsRefused_AndNeverMakesTheWeatherSafe()
    {
        var alpacaServer = new LateHandler();
        var controller = RoofClientApiTests.CreateHost(
            configureServices: services => services.AddHttpClient(AlpacaDevice.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => alpacaServer),
            settings: new Dictionary<string, string?>
            {
                ["RoofSafety:PollInterval"] = "00:00:01",
                ["RoofSafety:ReadTimeout"] = "00:00:00.5",
                ["RoofSafety:MaxReadingAge"] = "00:00:05",
                // The Alpaca server in front of this very controller: an address its Host check accepts.
                ["RoofSafety:WeatherSources:0:Name"] = "Our own",
                ["RoofSafety:WeatherSources:0:Url"] = "http://127.0.0.1:11111",
            });
        await using var alpaca = await AlpacaTestHost.StartAsync(controller);
        alpacaServer.Target = alpaca.CreateHandler();
        using var api = controller.CreateApiClient(TestApiKeys.Viewer);

        var safety = await WaitForAsync(api, current => current.Sources[0].Reason.Contains(AlpacaSafetyMonitorSource.OwnDeviceFailure, StringComparison.Ordinal));
        (await alpaca.PutDeviceAsync("safetymonitor/0", "connected", "Connected=true&ClientID=1")).GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        var isSafe = await alpaca.GetDeviceAsync("safetymonitor/0", "issafe", "ClientID=1");

        safety.Sources[0].Reason.Should().Be($"Weather monitor 'Our own' cannot be read ({AlpacaSafetyMonitorSource.OwnDeviceFailure})");
        safety.WeatherState.Should().Be(RoofSafetyState.Unknown);
        safety.State.Should().Be(RoofSafetyState.Unknown);
        isSafe.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        isSafe.GetProperty("Value").GetBoolean().Should().BeFalse("with its only weather source refused, the weather is not known");
        alpacaServer.Paths.Should().NotContain(path => path.EndsWith("/issafe", StringComparison.Ordinal), "the controller never read its own IsSafe");
    }

    [TestMethod]
    public async Task OurOwnSafetyMonitor_BesideARealWeatherSource_IsShownRefused_WhileTheRealOneDecides()
    {
        var alpacaServer = new LateHandler();
        var weather = new SimulatedWeatherSafetySource("Cloud sensor");
        var controller = RoofClientApiTests.CreateHost(
            configureServices: services =>
            {
                services.AddSingleton<IWeatherSafetySource>(weather);
                services.AddHttpClient(AlpacaDevice.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => alpacaServer);
            },
            settings: new Dictionary<string, string?>
            {
                ["RoofSafety:PollInterval"] = "00:00:01",
                ["RoofSafety:ReadTimeout"] = "00:00:00.5",
                ["RoofSafety:MaxReadingAge"] = "00:00:05",
                ["RoofSafety:WeatherSources:0:Name"] = "Our own",
                ["RoofSafety:WeatherSources:0:Url"] = "http://127.0.0.1:11111",
                ["RoofSafety:WeatherSources:0:Requirement"] = "Ignored",
            });
        await using var alpaca = await AlpacaTestHost.StartAsync(controller);
        alpacaServer.Target = alpaca.CreateHandler();
        using var api = controller.CreateApiClient(TestApiKeys.Viewer);

        var safety = await WaitForAsync(api, current => current.Sources.Count == 2
            && current.Sources[1].Reason.Contains(AlpacaSafetyMonitorSource.OwnDeviceFailure, StringComparison.Ordinal)
            && current.WeatherState == RoofSafetyState.Safe);

        safety.WeatherState.Should().Be(RoofSafetyState.Safe, "the real weather source decides; the refused one is set Ignored");
        safety.Sources[1].Should().BeEquivalentTo(new { Name = "Our own", State = RoofSafetyState.Unknown, Counts = false });
    }

    private static async Task<RoofSafetyStatus> WaitForAsync(HttpClient api, Func<RoofSafetyStatus, bool> until)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        RoofSafetyStatus? safety = null;
        while (DateTime.UtcNow < deadline)
        {
            safety = await ApiJson.ReadAsync<RoofSafetyStatus>(await api.GetAsync("/api/v4.0/Safety"));
            if (safety.Sources.Count > 0 && until(safety))
            {
                return safety;
            }

            await Task.Delay(50);
        }

        throw new AssertFailedException($"The safety conditions did not get there: {string.Join("; ", safety?.Sources.Select(source => source.Reason) ?? [])}.");
    }

    /// <summary>
    /// The controller's Alpaca client's handler, sending to the Alpaca server once it exists (the server is started in
    /// front of the controller, so after it); until then a request cannot be sent.
    /// </summary>
    private sealed class LateHandler : HttpMessageHandler
    {
        private HttpMessageInvoker? _target;

        public List<string> Paths { get; } = [];

        public HttpMessageHandler Target
        {
            set => _target = new HttpMessageInvoker(value, disposeHandler: false);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths)
            {
                Paths.Add(request.RequestUri!.AbsolutePath);
            }

            return _target is { } target
                ? target.SendAsync(request, cancellationToken)
                : throw new HttpRequestException("The Alpaca server has not started yet (test).", null, HttpStatusCode.ServiceUnavailable);
        }
    }
}
