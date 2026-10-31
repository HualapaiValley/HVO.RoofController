using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Tests.Emulation;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CapturingLogger = HVO.RoofControllerV4.RPi.Tests.TestSupport.CapturingLogger<HVO.RoofControllerV4.RPi.Safety.RoofSafetyService>;
using ManualTimeProvider = HVO.RoofControllerV4.RPi.Tests.TestSupport.ManualTimeProvider;

namespace HVO.RoofControllerV4.RPi.Tests.Safety;

/// <summary>
/// The controller's ASCOM Alpaca safety sources (#220, phase 2) against the HAT emulator's Alpaca devices (modelled on the
/// Alpaca API, not on the adapter), and against answers no device should give: every answer that is not a clean reading
/// is a failed read with words fit to show, never a value; the adapter only reads (GET); and the device's address appears
/// in no failure, not even in an inner exception the debug log would write.
/// </summary>
[TestClass]
public sealed class AlpacaSafetySourceTests
{
    /// <summary>A made-up address: every request goes to the emulator in process, and no failure may name it.</summary>
    private const string Server = "http://alpaca.example.test:11111";

    [TestMethod]
    public async Task AParkedMount_AndSafeWeather_ReadAsSuch_AndAnUnparkedMount_AndUnsafeWeather_Too()
    {
        await using var emulator = new EmulatorHost();
        var mount = new AlpacaTelescopeParkSource(Telescope("Main"), Clients(emulator));
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), Clients(emulator));

        (await mount.ReadParkStateAsync(CancellationToken.None)).IsParked.Should().BeTrue();
        (await weather.ReadWeatherSafetyAsync(CancellationToken.None)).IsSafe.Should().BeTrue();

        emulator.Alpaca.SetTelescope(0, parked: false);
        emulator.Alpaca.SetSafetyMonitor(0, safe: false);

        (await mount.ReadParkStateAsync(CancellationToken.None)).IsParked.Should().BeFalse();
        (await weather.ReadWeatherSafetyAsync(CancellationToken.None)).IsSafe.Should().BeFalse();
        mount.Name.Should().Be("Main");
        weather.Name.Should().Be("Cloud sensor");
    }

    [TestMethod]
    public void TheSettingsBlocksRoof_AndRequirement_ReachTheSources()
    {
        var clients = new Factory(() => new SocketsHttpHandler());

        new AlpacaTelescopeParkSource(Telescope("Main"), clients).BlocksRoof.Should().BeTrue();
        new AlpacaTelescopeParkSource(Telescope("Solar", blocksRoof: false), clients).BlocksRoof.Should().BeFalse();
        new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), clients).Requirement.Should().Be(RoofWeatherSourceRequirement.Required);
        new AlpacaSafetyMonitorSource(Weather("Old sensor", requirement: RoofWeatherSourceRequirement.Ignored), clients).Requirement
            .Should().Be(RoofWeatherSourceRequirement.Ignored);
    }

    [TestMethod]
    public async Task EachRead_OnlyGets_ConnectedFirst_WithTheClientsNumbers_AndTheTransactionCounting()
    {
        await using var emulator = new EmulatorHost();
        var recorder = new RecordingHandler(emulator.Server.CreateHandler());
        var mount = new AlpacaTelescopeParkSource(Telescope("Main"), new Factory(() => recorder));
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), new Factory(() => recorder));

        await mount.ReadParkStateAsync(CancellationToken.None);
        await mount.ReadParkStateAsync(CancellationToken.None);
        await weather.ReadWeatherSafetyAsync(CancellationToken.None);

        recorder.Requests.Select(request => request.Method).Should().OnlyContain(method => method == HttpMethod.Get,
            "the controller never writes Connected, parks or moves another program's device");
        recorder.Requests.Select(request => request.Uri.AbsolutePath).Should().Equal(
            "/api/v1/telescope/0/connected", "/api/v1/telescope/0/canpark", "/api/v1/telescope/0/atpark",
            "/api/v1/telescope/0/connected", "/api/v1/telescope/0/canpark", "/api/v1/telescope/0/atpark",
            "/api/v1/safetymonitor/0/connected", "/api/v1/safetymonitor/0/issafe");
        var queries = recorder.Requests.Select(request => HttpUtility.ParseQueryString(request.Uri.Query)).ToList();
        queries.Select(query => query["ClientID"]).Distinct().Should().ContainSingle()
            .Which.Should().Match(id => uint.Parse(id!, CultureInfo.InvariantCulture) >= 1 && uint.Parse(id!, CultureInfo.InvariantCulture) <= 65535,
                "a client chooses an ID from 1 to 65535 at start");
        var transactions = queries.Select(query => uint.Parse(query["ClientTransactionID"]!, CultureInfo.InvariantCulture)).ToList();
        transactions.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems().And.NotContain(0u);
        emulator.Alpaca.GetStatus().Telescopes[0].Should().BeEquivalentTo(new { Connected = true, Parked = (bool?)true }, "reading changed nothing");
    }

    [TestMethod]
    public async Task AMountNotConnected_IsAFailedRead_NeverNotParked()
    {
        await using var emulator = new EmulatorHost();
        emulator.Alpaca.SetTelescope(0, parked: false, connected: false);
        var mount = new AlpacaTelescopeParkSource(Telescope("Main"), Clients(emulator));

        var read = () => mount.ReadParkStateAsync(CancellationToken.None);

        (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which.Message.Should().Be("the device is not connected");
    }

    [TestMethod]
    public async Task AMonitorNotConnected_IsAFailedRead_NeverUnsafeWeather()
    {
        await using var emulator = new EmulatorHost();
        emulator.Alpaca.SetSafetyMonitor(0, connected: false);
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), Clients(emulator));

        var read = () => weather.ReadWeatherSafetyAsync(CancellationToken.None);

        (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which.Message.Should().Be("the device is not connected");
    }

    [TestMethod]
    public async Task AMountThatCannotPark_IsAConfigurationError_NeverNotParked()
    {
        await using var emulator = new EmulatorHost();
        emulator.Alpaca.SetTelescope(0, canPark: false);
        var mount = new AlpacaTelescopeParkSource(Telescope("Main"), Clients(emulator));

        var read = () => mount.ReadParkStateAsync(CancellationToken.None);

        (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which.Message.Should().Be(AlpacaTelescopeParkSource.CannotParkFailure);
        AlpacaTelescopeParkSource.CannotParkFailure.Should().Be("configuration error: the mount reports it cannot park (CanPark is false)");
    }

    [TestMethod]
    public async Task AMountWithoutAtPark_IsAConfigurationError()
    {
        var mount = new AlpacaTelescopeParkSource(Telescope("Main"), Answering("{\"ErrorNumber\":1024,\"ErrorMessage\":\"not implemented\"}"));

        var read = () => mount.ReadParkStateAsync(CancellationToken.None);

        (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which.Message.Should().Be("configuration error: the mount does not implement AtPark");
    }

    [TestMethod]
    [DataRow(EmulatedAlpacaBehaviour.DriverError, "the device reports Alpaca error 0x500 (driver error)")]
    [DataRow(EmulatedAlpacaBehaviour.ServerError, "the Alpaca server answered HTTP 500")]
    public async Task ADeviceThatFails_IsAFailedRead_InWordsFitToShow(EmulatedAlpacaBehaviour behaviour, string words)
    {
        await using var emulator = new EmulatorHost();
        emulator.Alpaca.SetTelescope(0, behaviour: behaviour);
        var mount = new AlpacaTelescopeParkSource(Telescope("Main"), Clients(emulator));

        var read = () => mount.ReadParkStateAsync(CancellationToken.None);

        var failure = (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which;
        failure.Message.Should().Be(words);
        failure.Message.Should().NotContain("example.test").And.NotContain("emulator fault injection", "the device's own text is never quoted");
        failure.InnerException.Should().BeNull();
    }

    [TestMethod]
    public async Task ADeviceNumberTheServerDoesNotHave_IsAFailedRead()
    {
        await using var emulator = new EmulatorHost();
        var mount = new AlpacaTelescopeParkSource(Telescope("Second", deviceNumber: 3), Clients(emulator));

        var read = () => mount.ReadParkStateAsync(CancellationToken.None);

        (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which.Message.Should().Be("the Alpaca server answered HTTP 400");
    }

    [TestMethod]
    public async Task ADeviceThatHangs_EndsWithTheReadsCancellation()
    {
        await using var emulator = new EmulatorHost();
        emulator.Alpaca.SetSafetyMonitor(0, behaviour: EmulatedAlpacaBehaviour.Hangs);
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), Clients(emulator));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var read = () => weather.ReadWeatherSafetyAsync(timeout.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task AServerThatCannotBeReached_IsAFailedRead_ThatSaysWhy_WithoutItsAddress()
    {
        // A loopback port nothing listens on.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var mount = new AlpacaTelescopeParkSource(Telescope("Main", url: $"http://127.0.0.1:{port}"), new Factory(() => new SocketsHttpHandler()));

        var read = () => mount.ReadParkStateAsync(CancellationToken.None);

        var failure = (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which;
        failure.Message.Should().Be("the Alpaca server could not be reached (connection refused)");
        failure.ToString().Should().NotContain(port.ToString(CultureInfo.InvariantCulture)).And.NotContain("127.0.0.1",
            "the HTTP client's own message names the host and port, so it is not kept, even as the inner exception");
    }

    [TestMethod]
    [DataRow("not json", "the Alpaca server's answer is not JSON")]
    [DataRow("[true]", "the Alpaca server's answer is not an Alpaca answer")]
    [DataRow("{\"Value\":true}", "the Alpaca server's answer has no ErrorNumber")]
    [DataRow("{\"Value\":true,\"ErrorNumber\":\"0\"}", "the Alpaca server's answer has no ErrorNumber")]
    [DataRow("{\"Value\":\"true\",\"ErrorNumber\":0}", "the Alpaca server's answer has no true or false Value")]
    [DataRow("{\"ErrorNumber\":0,\"ErrorMessage\":\"\"}", "the Alpaca server's answer has no true or false Value")]
    [DataRow("{\"Value\":true,\"ErrorNumber\":1031,\"ErrorMessage\":\"at 192.0.2.7\"}", "the device is not connected (Alpaca error 0x407)")]
    [DataRow("{\"Value\":true,\"ErrorNumber\":1035,\"ErrorMessage\":\"\"}", "the device reports Alpaca error 0x40B (invalid operation)")]
    public async Task AnAnswerThatIsNotACleanReading_IsAFailedRead(string body, string words)
    {
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), Answering(body));

        var read = () => weather.ReadWeatherSafetyAsync(CancellationToken.None);

        var failure = (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which;
        failure.Message.Should().Be(words);
        failure.Message.Should().NotContain("192.0.2.7", "the device's ErrorMessage is never quoted");
        failure.InnerException.Should().BeNull();
    }

    [TestMethod]
    public async Task TheAnswersNames_AreReadWhateverTheirCase()
    {
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), Answering("{\"value\":false,\"errornumber\":0,\"errormessage\":\"\"}"));

        (await weather.ReadWeatherSafetyAsync(CancellationToken.None)).IsSafe.Should().BeFalse();
    }

    [TestMethod]
    public async Task AnAnswerTooLongToBeAnAlpacaAnswer_IsAFailedRead()
    {
        var body = "{\"Value\":true,\"ErrorNumber\":0,\"ErrorMessage\":\"" + new string('x', AlpacaDevice.MaxAnswerBytes) + "\"}";
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), Answering(body, sendLength: false));

        var read = () => weather.ReadWeatherSafetyAsync(CancellationToken.None);

        (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which.Message.Should().Be("the Alpaca server's answer is too long to be an Alpaca answer");
    }

    [TestMethod]
    public async Task AServerWithAPath_KeepsIt_AndTheDeviceTypeAndMemberAreLowerCase()
    {
        var recorder = new RecordingHandler(new AnsweringHandler("{\"Value\":true,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}", sendLength: true));
        var device = new AlpacaDevice("http://alpaca.example.test:8080/proxy/alpaca", "SafetyMonitor", 2, new Factory(() => recorder));

        (await device.ReadBooleanAsync("IsSafe", CancellationToken.None)).Should().BeTrue();

        recorder.Requests.Single().Uri.AbsolutePath.Should().Be("/proxy/alpaca/api/v1/safetymonitor/2/issafe");
    }

    [TestMethod]
    [DataRow("ftp://alpaca.example.test:21")]
    [DataRow("http://user:secret@alpaca.example.test:11111")]
    [DataRow("http://alpaca.example.test:11111/?x=1")]
    [DataRow("alpaca.example.test:11111")]
    [DataRow("")]
    public void AnAddressTheAdapterCannotUse_IsRefused_WithoutRepeatingIt(string url)
    {
        var make = () => new AlpacaTelescopeParkSource(Telescope("Main", url: url), new Factory(() => new SocketsHttpHandler()));

        make.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("secret").And.NotContain("example.test");
    }

    // ---- With the safety service ---------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheService_SaysWhyAnAlpacaSourceCannotBeRead_AndLogsNoAddress()
    {
        await using var emulator = new EmulatorHost();
        emulator.Alpaca.SetTelescope(0, connected: false);
        var logger = new CapturingLogger();
        using var service = Service(emulator, logger, new ManualTimeProvider(), telescopes: [Telescope("Main")], weather: [Weather("Cloud sensor")]);

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal("Telescope 'Main' cannot be read (the device is not connected)");
        safety.WeatherState.Should().Be(RoofSafetyState.Safe, "the weather alone does not wait on the mount");
        safety.Sources.Select(source => (source.Name, source.Kind, source.State)).Should().Equal(
            ("Main", RoofSafetySourceKind.TelescopePark, RoofSafetyState.Unknown),
            ("Cloud sensor", RoofSafetySourceKind.Weather, RoofSafetyState.Safe));
        logger.Contains(LogLevel.Warning, "Safety source TelescopePark 'Main' could not be read (the device is not connected)").Should().BeTrue();
        logger.Entries.Should().NotContain(entry => entry.Message.Contains("example.test", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task TheService_ReadsAnUnparkedMount_AsUnsafe_AndAMountThatStopsAnswering_GoesStale_WithWhy()
    {
        await using var emulator = new EmulatorHost();
        emulator.Alpaca.SetTelescope(0, parked: false);
        var time = new ManualTimeProvider();
        using var service = Service(emulator, new CapturingLogger(), time, telescopes: [Telescope("Main")], weather: [Weather("Cloud sensor")]);

        await service.PollAsync(CancellationToken.None);
        service.GetCurrent().Reasons.Should().Equal("Telescope 'Main' is not parked");

        emulator.Alpaca.SetTelescope(0, behaviour: EmulatedAlpacaBehaviour.ServerError);
        time.Advance(TimeSpan.FromSeconds(30));
        await service.PollAsync(CancellationToken.None);
        service.GetCurrent().State.Should().Be(RoofSafetyState.Unsafe, "the last reading counts until it is too old");

        time.Advance(TimeSpan.FromSeconds(31));
        var safety = service.GetCurrent();
        safety.State.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal("Telescope 'Main': the last reading (not parked) is older than 60 s; the last read failed (the Alpaca server answered HTTP 500)");
    }

    [TestMethod]
    public async Task TheService_TimesOutAMonitorThatHangs_AndSaysSo()
    {
        await using var emulator = new EmulatorHost();
        emulator.Alpaca.SetSafetyMonitor(0, behaviour: EmulatedAlpacaBehaviour.Hangs);
        var time = new ManualTimeProvider();
        using var service = Service(emulator, new CapturingLogger(), time, weather: [Weather("Cloud sensor")]);

        var poll = service.PollAsync(CancellationToken.None);
        await Client.ClientTestSupport.WaitUntilAsync(() => emulator.Alpaca.GetStatus().SafetyMonitors[0].Requests >= 1, "the monitor to be asked");
        time.Advance(TimeSpan.FromSeconds(5));
        await poll.WaitAsync(TimeSpan.FromSeconds(10));

        var safety = service.GetCurrent();
        safety.Reasons.Should().Equal("Weather monitor 'Cloud sensor' cannot be read (no answer within 5 s)");
        safety.WeatherReasons.Should().Equal("Weather monitor 'Cloud sensor' cannot be read (no answer within 5 s)");
    }

    [TestMethod]
    public async Task TheService_ReadsTheConfiguredDevices_AfterTheRegisteredSources_WithTheirSettings()
    {
        await using var emulator = new EmulatorHost(new Dictionary<string, string?> { ["Emulator:AlpacaTelescopes"] = "2" });
        emulator.Alpaca.SetTelescope(1, parked: false);
        using var service = Service(
            emulator,
            new CapturingLogger(),
            new ManualTimeProvider(),
            telescopes: [Telescope("Main"), Telescope("Solar", deviceNumber: 1, blocksRoof: false)],
            weather: [Weather("Cloud sensor"), Weather("Old sensor", requirement: RoofWeatherSourceRequirement.Ignored)],
            registered: new SimulatedTelescopeParkSource("Simulated"));

        await service.PollAsync(CancellationToken.None);
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Safe, "Solar does not block the roof");
        safety.Sources.Select(source => (source.Name, source.State, source.Counts)).Should().Equal(
            ("Simulated", RoofSafetyState.Safe, true),
            ("Main", RoofSafetyState.Safe, true),
            ("Solar", RoofSafetyState.Unsafe, false),
            ("Cloud sensor", RoofSafetyState.Safe, true),
            ("Old sensor", RoofSafetyState.Safe, false));
    }

    // ---- Support ---------------------------------------------------------------------------------------------------

    internal static RoofSafetyTelescopeOptions Telescope(string name, int deviceNumber = 0, string url = Server, bool blocksRoof = true)
        => new() { Name = name, Url = url, DeviceNumber = deviceNumber, BlocksRoof = blocksRoof };

    internal static RoofSafetyWeatherSourceOptions Weather(string name, int deviceNumber = 0, string url = Server, RoofWeatherSourceRequirement requirement = RoofWeatherSourceRequirement.Required)
        => new() { Name = name, Url = url, DeviceNumber = deviceNumber, Requirement = requirement };

    internal static IHttpClientFactory Clients(EmulatorHost emulator) => new Factory(() => emulator.Server.CreateHandler());

    private static RoofSafetyService Service(
        EmulatorHost emulator,
        CapturingLogger logger,
        ManualTimeProvider time,
        RoofSafetyTelescopeOptions[]? telescopes = null,
        RoofSafetyWeatherSourceOptions[]? weather = null,
        ITelescopeParkSource? registered = null)
    {
        var options = new RoofSafetyOptions
        {
            InterlockMode = RoofSafetyInterlockMode.Off,
            PollInterval = TimeSpan.FromSeconds(10),
            ReadTimeout = TimeSpan.FromSeconds(5),
            MaxReadingAge = TimeSpan.FromSeconds(60),
            Telescopes = [.. telescopes ?? []],
            WeatherSources = [.. weather ?? []]
        };
        new RoofSafetyOptionsValidator().Validate(null, options).Succeeded.Should().BeTrue();
        var configured = new RoofSafetyConfiguredSources(Options.Create(options), Clients(emulator));
        return new RoofSafetyService(registered is null ? [] : [registered], [], new TestOptionsMonitor<RoofSafetyOptions>(options), time, logger, configured);
    }

    private static IHttpClientFactory Answering(string body, bool sendLength = true) => new Factory(() => new AnsweringHandler(body, sendLength));

    /// <summary>Makes every client on the handler given, as the controller's named client would be.</summary>
    internal sealed class Factory(Func<HttpMessageHandler> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler(), disposeHandler: false);
    }

    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add((request.Method, request.RequestUri!));
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>An answer whose length is not known before it is read, as a chunked one.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Answers <c>connected</c> and <c>canpark</c> true, and every other member with the body given.</summary>
    private sealed class AnsweringHandler(string body, bool sendLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var answer = path.EndsWith("/connected", StringComparison.Ordinal) || path.EndsWith("/canpark", StringComparison.Ordinal)
                ? "{\"Value\":true,\"ClientTransactionID\":1,\"ServerTransactionID\":1,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}"
                : body;
            HttpContent content = sendLength
                ? new StringContent(answer, Encoding.UTF8, "application/json")
                : new StreamContent(new ForwardOnlyStream(Encoding.UTF8.GetBytes(answer)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
