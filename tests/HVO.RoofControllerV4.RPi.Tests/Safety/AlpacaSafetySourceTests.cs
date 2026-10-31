using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Alpaca;
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
            "/api/v1/safetymonitor/0/driverinfo", "/management/v1/description", "/api/v1/safetymonitor/0/connected", "/api/v1/safetymonitor/0/issafe");
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
        var mount = new AlpacaTelescopeParkSource(Telescope("Main"), Answering(Envelope("\"ErrorNumber\":1024,\"ErrorMessage\":\"not implemented\"")));

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
    [DataRow("{\"Value\":true,\"ClientTransactionID\":$TX,\"ServerTransactionID\":1,\"ErrorMessage\":\"\"}", "the Alpaca server's answer has no ErrorNumber")]
    [DataRow("{\"Value\":true,\"ClientTransactionID\":$TX,\"ServerTransactionID\":1,\"ErrorNumber\":\"0\",\"ErrorMessage\":\"\"}", "the Alpaca server's answer has no ErrorNumber")]
    [DataRow("{\"Value\":\"true\",\"ClientTransactionID\":$TX,\"ServerTransactionID\":1,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}", "the Alpaca server's answer has no true or false Value")]
    [DataRow("{\"ClientTransactionID\":$TX,\"ServerTransactionID\":1,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}", "the Alpaca server's answer has no true or false Value")]
    [DataRow("{\"Value\":true,\"ClientTransactionID\":$TX,\"ServerTransactionID\":1,\"ErrorNumber\":1031,\"ErrorMessage\":\"at 192.0.2.7\"}", "the device is not connected (Alpaca error 0x407)")]
    [DataRow("{\"Value\":true,\"ClientTransactionID\":$TX,\"ServerTransactionID\":1,\"ErrorNumber\":1035,\"ErrorMessage\":\"\"}", "the device reports Alpaca error 0x40B (invalid operation)")]
    // Review F2 (#227): a success must be unambiguous: ErrorNumber 0 with an empty ErrorMessage, every field once, and the
    // answer to this request. Anything else is a failed read, so it can neither make nor refresh a Safe reading.
    [DataRow("{\"Value\":true,\"ClientTransactionID\":$TX,\"ServerTransactionID\":2,\"ErrorNumber\":0,\"ErrorMessage\":\"sensor read failed; cached value retained\"}", "the Alpaca server's answer reports success with an error message")]
    [DataRow("{\"Value\":true,\"ClientTransactionID\":$TX,\"ServerTransactionID\":2,\"ErrorNumber\":0}", "the Alpaca server's answer has no ErrorMessage")]
    [DataRow("{\"Value\":true,\"ClientTransactionID\":$TX,\"ServerTransactionID\":2,\"ErrorNumber\":0,\"ErrorMessage\":0}", "the Alpaca server's answer has no ErrorMessage")]
    [DataRow("{\"Value\":true,\"ClientTransactionID\":$TX,\"ServerTransactionID\":2,\"ErrorNumber\":0,\"ErrorNumber\":1280,\"Value\":false,\"ErrorMessage\":\"sensor read failed\"}", "the Alpaca server's answer repeats a field")]
    [DataRow("{\"Value\":true,\"value\":false,\"ClientTransactionID\":$TX,\"ServerTransactionID\":2,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}", "the Alpaca server's answer repeats a field")]
    [DataRow("{\"Value\":true,\"ServerTransactionID\":2,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}", "the Alpaca server's answer is not the answer to this request")]
    [DataRow("{\"Value\":true,\"ClientTransactionID\":4000000000,\"ServerTransactionID\":2,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}", "the Alpaca server's answer is not the answer to this request")]
    [DataRow("{\"Value\":true,\"ClientTransactionID\":$TX,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}", "the Alpaca server's answer has no ServerTransactionID")]
    public async Task AnAnswerThatIsNotACleanReading_IsAFailedRead(string body, string words)
    {
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), Answering(body));

        var read = () => weather.ReadWeatherSafetyAsync(CancellationToken.None);

        var failure = (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which;
        failure.Message.Should().Be(words);
        failure.Message.Should().NotContain("192.0.2.7", "the device's ErrorMessage is never quoted").And.NotContain("sensor read failed");
        failure.InnerException.Should().BeNull();
    }

    [TestMethod]
    public async Task AnAnswerThatIsNotACleanReading_NeverRefreshesTheLastReading()
    {
        var handler = new AnsweringHandler(Envelope("\"Value\":true,\"ErrorNumber\":0,\"ErrorMessage\":\"\""), sendLength: true);
        var time = new ManualTimeProvider();
        var options = new RoofSafetyOptions
        {
            InterlockMode = RoofSafetyInterlockMode.Off,
            WeatherSources = [Weather("Cloud sensor")]
        };
        var configured = new RoofSafetyConfiguredSources(Options.Create(options), new Factory(() => handler));
        using var service = new RoofSafetyService([], [], new TestOptionsMonitor<RoofSafetyOptions>(options), time, new CapturingLogger(), configured);
        await service.PollAsync(CancellationToken.None);
        service.GetCurrent().State.Should().Be(RoofSafetyState.Safe);

        handler.Body = Envelope("\"Value\":true,\"ErrorNumber\":0,\"ErrorMessage\":\"sensor read failed\"");
        time.Advance(TimeSpan.FromSeconds(30));
        await service.PollAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(31));
        var safety = service.GetCurrent();

        safety.State.Should().Be(RoofSafetyState.Unknown, "the error-bearing answer did not refresh the reading made 61 s ago");
        safety.WeatherState.Should().Be(RoofSafetyState.Unknown);
        safety.Reasons.Should().Equal("Weather monitor 'Cloud sensor': the last reading (safe) is older than 60 s; the last read failed (the Alpaca server's answer reports success with an error message)");
    }

    [TestMethod]
    public async Task TheAnswersNames_AreReadWhateverTheirCase()
    {
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), Answering("{\"value\":false,\"errornumber\":0,\"errormessage\":\"\",\"clienttransactionid\":$TX,\"servertransactionid\":1}"));

        (await weather.ReadWeatherSafetyAsync(CancellationToken.None)).IsSafe.Should().BeFalse();
    }

    [TestMethod]
    public async Task AnAnswerTooLongToBeAnAlpacaAnswer_IsAFailedRead()
    {
        var body = Envelope("\"Value\":true,\"ErrorNumber\":0,\"ErrorMessage\":\"" + new string('x', AlpacaDevice.MaxAnswerBytes) + "\"");
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), Answering(body, sendLength: false));

        var read = () => weather.ReadWeatherSafetyAsync(CancellationToken.None);

        (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which.Message.Should().Be("the Alpaca server's answer is too long to be an Alpaca answer");
    }

    [TestMethod]
    public async Task AServerWithAPath_KeepsIt_AndTheDeviceTypeAndMemberAreLowerCase()
    {
        var recorder = new RecordingHandler(new AnsweringHandler(Envelope("\"Value\":true,\"ErrorNumber\":0,\"ErrorMessage\":\"\""), sendLength: true));
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

    // ---- Never this roof controller's own SafetyMonitor (#220): no loop ------------------------------------------

    [TestMethod]
    public async Task OurOwnSafetyMonitor_ByItsDriverInfo_IsRefused_AndItsIsSafeNeverRead()
    {
        // As through a hub that relays our device: the server is another, the device says it is ours.
        var server = new FakeAlpacaServer { DriverInfo = AlpacaServerIdentity.ServerName + " 4.4.0: the observatory's weather" };
        var weather = new AlpacaSafetyMonitorSource(Weather("Loop"), new Factory(() => server));

        var read = () => weather.ReadWeatherSafetyAsync(CancellationToken.None);

        (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which.Message.Should().Be(AlpacaSafetyMonitorSource.OwnDeviceFailure);
        server.Paths.Should().NotContain(path => path.EndsWith("/issafe", StringComparison.Ordinal), "a loop is refused before it is read");
    }

    [TestMethod]
    public async Task OurOwnAlpacaServer_ByTheUniqueIdItListsForTheDevice_IsRefused()
    {
        // The device's own words are another's, but its server is ours and lists the device's UniqueID as its own.
        var server = new FakeAlpacaServer { ServerName = AlpacaServerIdentity.ServerName, UniqueId = "0d5f6a1c-2b3e-4c5d-9e8f-102938475610" };
        var weather = new AlpacaSafetyMonitorSource(Weather("Loop"), new Factory(() => server));

        var read = () => weather.ReadWeatherSafetyAsync(CancellationToken.None);

        (await read.Should().ThrowAsync<RoofSafetySourceException>()).Which.Message.Should().Be(AlpacaSafetyMonitorSource.OwnDeviceFailure);
        server.Paths.Should().Contain("/management/v1/configureddevices").And.NotContain(path => path.EndsWith("/issafe", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AnotherServersDevice_IsChecked_OncePerConnection_AndAgainAfterAFailedRead()
    {
        var server = new FakeAlpacaServer();
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), new Factory(() => server));

        (await weather.ReadWeatherSafetyAsync(CancellationToken.None)).IsSafe.Should().BeTrue();
        (await weather.ReadWeatherSafetyAsync(CancellationToken.None)).IsSafe.Should().BeTrue();
        server.Fail = true;
        var failed = () => weather.ReadWeatherSafetyAsync(CancellationToken.None);
        await failed.Should().ThrowAsync<RoofSafetySourceException>();
        server.Fail = false;
        (await weather.ReadWeatherSafetyAsync(CancellationToken.None)).IsSafe.Should().BeTrue();

        server.Paths.Count(path => path.EndsWith("/driverinfo", StringComparison.Ordinal)).Should().Be(2, "checked at first contact, and again after the failed read");
        server.Paths.Count(path => path == "/management/v1/description").Should().Be(2);
        server.Paths.Count(path => path.EndsWith("/issafe", StringComparison.Ordinal)).Should().Be(3);
        server.Methods.Should().OnlyContain(method => method == HttpMethod.Get);
    }

    [TestMethod]
    public async Task AServerWithoutTheManagementApi_IsStillRead_ByItsDevicesOwnWords()
    {
        var server = new FakeAlpacaServer { ServesManagement = false };
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), new Factory(() => server));

        (await weather.ReadWeatherSafetyAsync(CancellationToken.None)).IsSafe.Should().BeTrue();
    }

    [TestMethod]
    public async Task TheEmulatorsSafetyMonitor_IsNotOurs()
    {
        await using var emulator = new EmulatorHost();
        var weather = new AlpacaSafetyMonitorSource(Weather("Cloud sensor"), Clients(emulator));

        (await weather.ReadWeatherSafetyAsync(CancellationToken.None)).IsSafe.Should().BeTrue();
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

    /// <summary>An Alpaca answer with these fields, the request's ClientTransactionID and a ServerTransactionID.</summary>
    private static string Envelope(string fields) => "{" + fields + ",\"ClientTransactionID\":$TX,\"ServerTransactionID\":1}";

    /// <summary>
    /// An Alpaca server with one safety monitor, answering cleanly by path: its DriverInfo and its management API's
    /// description and configured devices as set, IsSafe true, or (<see cref="Fail"/>) HTTP 500 to every device request.
    /// </summary>
    private sealed class FakeAlpacaServer : HttpMessageHandler
    {
        public string DriverInfo { get; init; } = "Another vendor's driver 2.1";

        public string ServerName { get; init; } = "Another Alpaca server";

        public string UniqueId { get; init; } = "7c9e6679-7425-40de-944b-e07fc1f90ae7";

        public bool ServesManagement { get; init; } = true;

        public bool Fail { get; set; }

        public List<string> Paths { get; } = [];

        public List<HttpMethod> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths)
            {
                Paths.Add(path);
                Methods.Add(request.Method);
            }

            var transaction = HttpUtility.ParseQueryString(request.RequestUri.Query)["ClientTransactionID"] ?? "0";
            string? value = path switch
            {
                "/management/v1/description" when ServesManagement => "{\"ServerName\":\"" + ServerName + "\",\"Manufacturer\":\"Someone\",\"ManufacturerVersion\":\"1\",\"Location\":\"\"}",
                "/management/v1/configureddevices" when ServesManagement => "[{\"DeviceName\":\"Weather\",\"DeviceType\":\"SafetyMonitor\",\"DeviceNumber\":0,\"UniqueID\":\"" + UniqueId + "\"}]",
                "/api/v1/safetymonitor/0/driverinfo" when !Fail => "\"" + DriverInfo + "\"",
                "/api/v1/safetymonitor/0/connected" or "/api/v1/safetymonitor/0/issafe" when !Fail => "true",
                _ => null
            };
            if (value is null)
            {
                return Task.FromResult(new HttpResponseMessage(path.StartsWith("/management", StringComparison.Ordinal) ? HttpStatusCode.BadRequest : HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("no", Encoding.UTF8, "text/plain")
                });
            }

            var body = "{\"Value\":" + value + ",\"ClientTransactionID\":" + transaction + ",\"ServerTransactionID\":1,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

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

    /// <summary>
    /// Answers <c>connected</c> and <c>canpark</c> true and <c>driverinfo</c> with another vendor's words, serves no
    /// management API, and answers every other member with <see cref="Body"/>, in which <c>$TX</c> stands for the
    /// request's ClientTransactionID.
    /// </summary>
    private sealed class AnsweringHandler(string body, bool sendLength) : HttpMessageHandler
    {
        public string Body { get; set; } = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/management/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("not served", Encoding.UTF8, "text/plain") });
            }

            var transaction = HttpUtility.ParseQueryString(request.RequestUri.Query)["ClientTransactionID"] ?? "0";
            var answer = (path.EndsWith("/connected", StringComparison.Ordinal) || path.EndsWith("/canpark", StringComparison.Ordinal)
                ? "{\"Value\":true,\"ClientTransactionID\":$TX,\"ServerTransactionID\":1,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}"
                : path.EndsWith("/driverinfo", StringComparison.Ordinal)
                    ? "{\"Value\":\"Another vendor's driver\",\"ClientTransactionID\":$TX,\"ServerTransactionID\":1,\"ErrorNumber\":0,\"ErrorMessage\":\"\"}"
                    : Body).Replace("$TX", transaction, StringComparison.Ordinal);
            HttpContent content = sendLength
                ? new StringContent(answer, Encoding.UTF8, "application/json")
                : new StreamContent(new ForwardOnlyStream(Encoding.UTF8.GetBytes(answer)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
