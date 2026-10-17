using System.Diagnostics;
using System.Net;
using FluentAssertions;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services;
using HVO.RoofControllerV4.RPi.Settings;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using HVO.RoofControllerV4.Simulation.Emulator;
using HVO.RoofControllerV4.Simulation.Hat;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// <c>--relays-off</c> as <c>Program.Main</c> runs it for the container's supervisor: the real configuration sources
/// (command line, content root appsettings, the settings file, key-per-file secrets) and the controller's HAT access, to
/// the HAT emulator's server in the test process. The relays start as the controller leaves them during an open (RLY1 and
/// the STOP permit RLY4), as a controller that dies during a move leaves them.
/// </summary>
[TestClass]
public sealed class RelaysOffCommandTests
{
    private const byte OpeningRelays = 0x09;

    private string _contentRoot = null!;
    private string _secrets = null!;
    private RoofPlant _plant = null!;
    private EmulatedHatRegisterClient _hat = null!;
    private HatEmulatorServer _server = null!;

    [TestInitialize]
    public void Start()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-relays-off-" + Guid.NewGuid().ToString("N"));
        _contentRoot = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        _secrets = Directory.CreateDirectory(Path.Combine(root, "secrets")).FullName;
        _plant = new RoofPlant(new RoofPlantOptions(), new ManualTimeProvider());
        _hat = new EmulatedHatRegisterClient(_plant, timing: EmulatedBusTiming.Instant);
        _server = new HatEmulatorServer(() => _hat, new IPEndPoint(IPAddress.Loopback, 0));
        _server.Start();
        _hat.WriteByte(SmI010Board.RelayValueRegister, OpeningRelays);
    }

    [TestCleanup]
    public async Task Stop()
    {
        await _server.DisposeAsync();
        _hat.Dispose();
        Directory.Delete(Path.GetDirectoryName(_contentRoot)!, recursive: true);
    }

    [TestMethod]
    public void EnergizedRelays_AreTurnedOff_AndVerifiedByReadBack()
    {
        var writesBefore = _hat.WriteCount;

        var (exitCode, output) = RunRelaysOff();

        exitCode.Should().Be(RelaysOffCommand.VerifiedExitCode, output);
        _plant.Hat.RelayRegister.Should().Be(0);
        output.Should().Contain(
            $"[relays-off] Every relay is off on the HAT emulator at 127.0.0.1:{_server.LocalEndPoint.Port}: the relay register reads back 0x00 (attempt 1, ");
        (_hat.WriteCount - writesBefore).Should().Be(1, "one write of the relay register turns all four relays off");
    }

    [TestMethod]
    public void NoRelayIsEverTurnedOn()
    {
        // Every write to the HAT, whatever the relays were: only the relay register, and only zero.
        foreach (var register in new byte[] { 0x00, 0x01, 0x02, 0x04, 0x08, 0x0A, 0x0F })
        {
            _hat.FailWhen = null;
            _hat.WriteByte(SmI010Board.RelayValueRegister, register);
            var writes = new List<HatBusAccess>();
            _hat.FailWhen = access =>
            {
                if (!access.IsRead)
                {
                    lock (writes)
                    {
                        writes.Add(access);
                    }
                }

                return false;
            };

            var (exitCode, output) = RunRelaysOff();

            exitCode.Should().Be(RelaysOffCommand.VerifiedExitCode, output);
            writes.Should().Equal([new HatBusAccess(SmI010Board.RelayValueRegister, 1, IsRead: false, Value: 0)], $"the relays were 0x{register:X2}");
            _plant.Hat.RelayRegister.Should().Be(0);
        }
    }

    [TestMethod]
    [DataRow("""{ "RoofControllerOptionsV4": { "SafetyWatchdogTimeout": "00:00:01" } }""", "SafetyWatchdogTimeout must be between 5 and 600 seconds")]
    [DataRow("""{ "RoofControllerOptionsV4": { "OpenRelayId": 9 } }""", "OpenRelayId")]
    public void SettingsTheControllerRefusesToStartWith_DoNotStopIt(string content, string refusal)
    {
        // The controller's start (Program.Main) refuses these before it touches the HAT; a crash loop follows.
        var settingsPath = WriteSettingsFile(content);
        var arguments = SettingsFileArguments(settingsPath);
        using var configuration = new ConfigurationManager();
        configuration[RoofSettingsConfiguration.FilePathKey] = arguments[1];
        configuration[RoofSettingsConfiguration.SecretsFilePathKey] = arguments[3];
        configuration.AddRoofSettingsFiles("Production", _contentRoot);
        FluentActions.Invoking(() => new RoofSettingsValidator([new RoofControllerOptionsV4Validator()], new TestHostEnvironment("Production"))
                .ValidateStartup(configuration))
            .Should().Throw<RoofSettingsFileException>().Which.Message.Should().Contain(refusal);

        var (exitCode, output) = RunRelaysOff(arguments);

        exitCode.Should().Be(RelaysOffCommand.VerifiedExitCode, output);
        _plant.Hat.RelayRegister.Should().Be(0);
        output.Should().NotContain("could not be read");
    }

    [TestMethod]
    [DataRow("appsettings.json")]
    [DataRow("appsettings.Production.json")]
    public void AShippedSettingsFileThatCannotBeRead_LeavesTheHatUnidentified_AndNothingIsWritten(string file)
    {
        // A settings file the API does not manage can select the HAT, so without it the controller's HAT is not known:
        // nothing is written, not even to the emulator the command line names, rather than to a HAT it may not use.
        File.WriteAllText(Path.Combine(_contentRoot, file), "{ \"HatEmulator\": { \"Port\": ");

        var (exitCode, output) = RunRelaysOff();

        exitCode.Should().Be(RelaysOffCommand.NotVerifiedExitCode, output);
        output.Should().Contain(
            "[relays-off] FAILED: the controller's HAT could not be identified, so nothing was written: its settings could not be read (InvalidDataException: ");
        _plant.Hat.RelayRegister.Should().Be(OpeningRelays);
        _server.AcceptedConnections.Should().Be(0);
    }

    [TestMethod]
    [DataRow("settings", "{ \"RoofControllerOptionsV4\": { \"SafetyWatchdogTimeout\": ", "is not valid JSON")]
    [DataRow("secrets", """{ "RoofControllerOptionsV4": { "SafetyWatchdogTimeout": "00:03:00" } }""", "which is not a secret setting")]
    public void AnApiSettingsFileThatCannotBeUsed_IsLeftOutAlone_AndTheHatTheOtherFilesSelectIsKept(string kind, string content, string problem)
    {
        // #139 review F2: the emulator is selected by a settings file of the deployment's (as appsettings.Development.json
        // selects it in Development), not by the command line. Leaving out the file the API manages, which cannot select the
        // HAT, must not lose that selection and turn to the I2C bus.
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.Production.json"), EmulatorSettingsJson(allowOutsideDevelopment: true));
        var arguments = SettingsFileArguments(WriteSettingsFile(kind == "settings" ? content : "{}"));
        if (kind == "secrets")
        {
            File.WriteAllText(arguments[3], content);
        }

        var (exitCode, output) = Run([RelaysOffCommand.CommandLineSwitch, "--contentRoot", _contentRoot, "--environment", "Production", .. arguments]);

        exitCode.Should().Be(RelaysOffCommand.VerifiedExitCode, output);
        _plant.Hat.RelayRegister.Should().Be(0);
        output.Should().Contain("[relays-off] Left out, as the controller cannot use it: ").And.Contain(problem)
            .And.Contain($"Every relay is off on the HAT emulator at 127.0.0.1:{_server.LocalEndPoint.Port}");
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow("Development", "Production")]
    [DataRow("Production", "Development")]
    public void TheEnvironment_IsTheOneTheControllersHostBuilderResolves_WithConflictingVariables(string dotnetEnvironment, string aspNetCoreEnvironment)
    {
        // #139 review F3. Emulator mode without consent outside Development is accepted only in Development, so whether the
        // relays go off shows the environment used. A settings file the controller cannot use is there too: the helper
        // must not resolve the environment another way then. Serial: it sets the process's environment.
        var dotnetBefore = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var aspNetCoreBefore = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnetEnvironment);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspNetCoreEnvironment);
        try
        {
            // What the controller's own host builder makes of the two.
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = ["--contentRoot", _contentRoot, "--hostBuilder:reloadConfigOnChange=false"]
            });
            var controllers = builder.Environment.EnvironmentName;
            ((IDisposable)builder.Configuration).Dispose();
            var arguments = SettingsFileArguments(WriteSettingsFile("{ \"RoofControllerOptionsV4\": "));

            var (exitCode, output) = Run(
            [
                RelaysOffCommand.CommandLineSwitch, "--contentRoot", _contentRoot,
                "--HatEmulator:Enabled", "true", "--HatEmulator:Host", "127.0.0.1",
                "--HatEmulator:Port", _server.LocalEndPoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), .. arguments
            ]);

            if (controllers == Environments.Development)
            {
                exitCode.Should().Be(RelaysOffCommand.VerifiedExitCode, output);
                _plant.Hat.RelayRegister.Should().Be(0);
            }
            else
            {
                exitCode.Should().Be(RelaysOffCommand.NotVerifiedExitCode, output);
                output.Should().Contain($"HAT emulator mode (HatEmulator:Enabled) is refused in the {controllers} environment.");
                _plant.Hat.RelayRegister.Should().Be(OpeningRelays);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", dotnetBefore);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", aspNetCoreBefore);
        }
    }

    [TestMethod]
    public void ASettingsFileThatIsNotJson_IsLeftOut_AsTheControllerCannotReadIt()
    {
        var settingsPath = WriteSettingsFile("{ \"RoofControllerOptionsV4\": { \"SafetyWatchdogTimeout\": ");

        var (exitCode, output) = RunRelaysOff(SettingsFileArguments(settingsPath));

        exitCode.Should().Be(RelaysOffCommand.VerifiedExitCode, output);
        _plant.Hat.RelayRegister.Should().Be(0);
        output.Should().Contain($"[relays-off] Left out, as the controller cannot use it: The settings file '{settingsPath}' is not valid JSON");
    }

    [TestMethod]
    public void AWriteThatFailsOnce_IsTriedAgain_AndVerified()
    {
        _hat.FailNextWrites = 1;

        var (exitCode, output) = RunRelaysOff();

        exitCode.Should().Be(RelaysOffCommand.VerifiedExitCode, output);
        _plant.Hat.RelayRegister.Should().Be(0);
        output.Should().Contain("Attempt 1 of 3 on the HAT emulator at ").And.Contain("the relay register reads back 0x09 after the write failed (IOException: ")
            .And.Contain("the relay register reads back 0x00 (attempt 2, ");
    }

    [TestMethod]
    public void WritesThatKeepFailing_AreNotVerified()
    {
        _hat.FailWrites = true;

        var (exitCode, output) = RunRelaysOff();

        exitCode.Should().Be(RelaysOffCommand.NotVerifiedExitCode, output);
        _plant.Hat.RelayRegister.Should().Be(OpeningRelays);
        output.Should().Contain("Attempt 3 of 3").And.Contain(
            $"[relays-off] FAILED: the relays on the HAT emulator at 127.0.0.1:{_server.LocalEndPoint.Port} could not be verified off after 3 attempts. The relays are not verified off.");
        output.Should().NotContain("Every relay is off");
    }

    [TestMethod]
    public void ARegisterThatReadsBackARelayOn_IsNotVerified()
    {
        // A register bit that stays set whatever is written: the read-back, not the write, decides.
        _plant.Hat.StuckOnRelayBits = 0x08;

        var (exitCode, output) = RunRelaysOff();

        exitCode.Should().Be(RelaysOffCommand.NotVerifiedExitCode, output);
        output.Should().Contain("the relay register reads back 0x08.").And.Contain("FAILED");
    }

    [TestMethod]
    public void ReadBacksThatFail_AreNotVerified()
    {
        _hat.FailReads = true;

        var (exitCode, output) = RunRelaysOff();

        exitCode.Should().Be(RelaysOffCommand.NotVerifiedExitCode, output);
        output.Should().Contain("the read-back failed (IOException: ").And.Contain("FAILED");
    }

    [TestMethod]
    public void AnEmulatorThatCannotBeReached_IsNotVerified_WithinItsTimeouts()
    {
        var port = _server.LocalEndPoint.Port;
        _server.Outage = true;
        var watch = Stopwatch.StartNew();

        var (exitCode, output) = RunRelaysOff(port, "--HatEmulator:ConnectTimeout", "00:00:00.2", "--HatEmulator:RequestTimeout", "00:00:00.2");

        exitCode.Should().Be(RelaysOffCommand.NotVerifiedExitCode, output);
        output.Should().Contain("the read-back failed (IOException: ").And.Contain("FAILED");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "every access is bounded by the emulator settings' timeouts");
        _plant.Hat.RelayRegister.Should().Be(OpeningRelays);
    }

    [TestMethod]
    public void EmulatorModeTheControllerWouldRefuse_WritesNothing()
    {
        // Outside Development without HatEmulator:AllowOutsideDevelopment the controller does not start in emulator mode.
        var (exitCode, output) = Run(
            RelaysOffCommand.CommandLineSwitch, "--contentRoot", _contentRoot, "--environment", "Production",
            "--HatEmulator:Enabled", "true", "--HatEmulator:Host", "127.0.0.1",
            "--HatEmulator:Port", _server.LocalEndPoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        exitCode.Should().Be(RelaysOffCommand.NotVerifiedExitCode, output);
        output.Should().Contain("[relays-off] FAILED: the HAT emulator settings cannot be used, so nothing was written: HAT emulator mode (HatEmulator:Enabled) is refused in the Production environment.");
        _plant.Hat.RelayRegister.Should().Be(OpeningRelays);
        _server.AcceptedConnections.Should().Be(0);
    }

    [TestMethod]
    public void WithoutAnI2cBus_NothingIsWritten_AndTheRelaysAreNotVerified()
    {
        // A bus that does not exist: the HAT library falls back to its in-memory simulation, which is not the HAT.
        var configuration = new ConfigurationBuilder().Build();
        using var output = new StringWriter();

        var exitCode = RelaysOffCommand.Run(configuration, new TestHostEnvironment(), output, new FourRelayFourInputHatOptions { I2cBusId = 250 });

        exitCode.Should().Be(RelaysOffCommand.NotVerifiedExitCode, output.ToString());
        output.ToString().Should().Contain("[relays-off] FAILED: I2C bus 250 could not be opened, so no HAT was written");
    }

    private string EmulatorSettingsJson(bool allowOutsideDevelopment)
        => "{ \"HatEmulator\": { \"Enabled\": true, \"Host\": \"127.0.0.1\", \"Port\": "
           + _server.LocalEndPoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + (allowOutsideDevelopment ? ", \"AllowOutsideDevelopment\": true" : string.Empty) + " } }";

    private string WriteSettingsFile(string content)
    {
        var settingsPath = Path.Combine(_contentRoot, "config", "appsettings.Local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, content);
        return settingsPath;
    }

    private string[] SettingsFileArguments(string settingsPath)
    {
        var secretsDirectory = Directory.CreateDirectory(Path.Combine(_contentRoot, "managed-secrets")).FullName;
        return ["--RoofControllerSettings:FilePath", settingsPath,
            "--RoofControllerSettings:SecretsFilePath", Path.Combine(secretsDirectory, "managed-secrets.json")];
    }

    // The deployment's emulator settings (the deploy script's HAT_EMULATOR_ENDPOINT with ALLOW_EMULATED_HAT=true), in
    // Production, with any other arguments.
    private (int ExitCode, string Output) RunRelaysOff(params string[] extra) => RunRelaysOff(_server.LocalEndPoint.Port, extra);

    private (int ExitCode, string Output) RunRelaysOff(int port, params string[] extra) => Run(
    [
        RelaysOffCommand.CommandLineSwitch, "--contentRoot", _contentRoot, "--environment", "Production",
        "--HatEmulator:Enabled", "true", "--HatEmulator:Host", "127.0.0.1",
        "--HatEmulator:Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--HatEmulator:AllowOutsideDevelopment", "true", .. extra
    ]);

    private (int ExitCode, string Output) Run(params string[] args)
    {
        using var output = new StringWriter();
        var exitCode = Program.RunRelaysOff(args, _secrets, output);
        return (exitCode, output.ToString());
    }
}
