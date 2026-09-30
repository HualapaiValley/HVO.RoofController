using Avalonia.Platform;
using FluentAssertions;
using HVO.RoofControllerV4.Kiosk;
using HVO.RoofControllerV4.RPi.Tests.Web;
using HVO.RoofControllerV4.Screens;
using Microsoft.Extensions.Logging;
using KioskProgram = HVO.RoofControllerV4.Kiosk.Program;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The kiosk program's settings, device key, display and backlight: what it starts with, and what it refuses to start
/// with (it exits with <see cref="KioskProgram.SettingsExitCode"/>, which systemd does not restart).
/// </summary>
[TestClass]
public sealed class KioskProgramTests
{
    private const string Pin = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";

    // ---- Settings -------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheDefaults_NeedOnlyTheDeviceKeyFile()
    {
        var options = new KioskOptions();

        options.Validate().Should().ContainSingle().Which.Should().StartWith("Kiosk:DeviceKeyFile must name the file");

        options.DeviceKeyFile = "/etc/hvo-roof-kiosk/device-key";
        options.Validate().Should().BeEmpty();
        options.ControllerUrl.Should().Be(new Uri("http://localhost:8080"));
        options.IdleLockSeconds.Should().Be(120);
        options.PixelsPerMillimetre.Should().Be(KioskOptions.DefaultPixelsPerMillimetre);
    }

    [TestMethod]
    public void EverySettingThatIsWrong_IsNamed()
    {
        var options = new KioskOptions
        {
            ControllerUrl = new Uri("ftp://roof-pi/"),
            ServerCertificateSha256 = "not-a-hash",
            DeviceKeyFile = " ",
            IdleLockSeconds = 5,
            PixelsPerMillimetre = double.NaN,
            Rotation = 45,
            Card = "/dev/fb0",
            InputDevices = ["/dev/input/event0", "/tmp/event1"],
            BacklightFile = "/sys/class/backlight/10-0045/bl_power",
            BacklightOff = "",
            Window = true,
            WindowSize = "big"
        };

        options.Validate().Select(problem => problem[..problem.IndexOf(' ', StringComparison.Ordinal)]).Should().Equal(
            "Kiosk:ControllerUrl",
            "Kiosk:ServerCertificateSha256",
            "Kiosk:DeviceKeyFile",
            "Kiosk:IdleLockSeconds",
            "Kiosk:PixelsPerMillimetre",
            "Kiosk:Rotation",
            "Kiosk:Card",
            "Kiosk:InputDevices",
            "Kiosk:BacklightOn",
            "Kiosk:WindowSize");
        options.Validate().Should().Contain("Kiosk:InputDevices has /tmp/event1, which is not an input device under /dev/input/.");
    }

    [TestMethod]
    [DataRow(10, true)]
    [DataRow(3600, true)]
    [DataRow(9, false)]
    [DataRow(3601, false)]
    public void TheIdleLock_IsFrom10SecondsToAnHour(int seconds, bool valid)
        => new KioskOptions { DeviceKeyFile = "key", IdleLockSeconds = seconds }.Validate().Should().HaveCount(valid ? 0 : 1);

    [TestMethod]
    [DataRow("1280x720", 1280, 720)]
    [DataRow("800X480", 800, 480)]
    [DataRow("200x8000", 200, 8000)]
    [DataRow("199x480", 0, 0)]
    [DataRow("800x8001", 0, 0)]
    [DataRow("-800x480", 0, 0)]
    [DataRow("800x480x2", 0, 0)]
    [DataRow("", 0, 0)]
    [DataRow(null, 0, 0)]
    public void AWindowSize_IsWidthByHeight(string? text, int width, int height)
    {
        KioskOptions.TryParseSize(text, out var parsedWidth, out var parsedHeight).Should().Be(width > 0);
        if (width > 0)
        {
            (parsedWidth, parsedHeight).Should().Be((width, height));
        }
    }

    [TestMethod]
    public void TheSettings_AreReadFromTheFile_ThenTheCommandLine()
    {
        using var directory = new WebTestSupport.TempDirectory();
        File.WriteAllText(directory.File(KioskProgram.SettingsFile), $$"""
            {
              "Kiosk": {
                "ControllerUrl": "https://localhost:8443/",
                "ServerCertificateSha256": "{{Pin}}",
                "DeviceKeyFile": "/etc/hvo-roof-kiosk/device-key",
                "IdleLockSeconds": 300,
                "Rotation": 90,
                "InputDevices": [ "/dev/input/event4" ]
              }
            }
            """);

        var options = KioskProgram.ReadOptions(KioskProgram.BuildConfiguration(["--Kiosk:IdleLockSeconds=60", "--window"], directory.Path));

        options.ControllerUrl.Should().Be(new Uri("https://localhost:8443/"));
        options.ServerCertificateSha256.Should().Be(Pin);
        options.IdleLockSeconds.Should().Be(60, "the command line wins");
        options.Rotation.Should().Be(90);
        options.InputDevices.Should().Equal("/dev/input/event4");
        options.Window.Should().BeTrue("--window is short for --Kiosk:Window=true");
    }

    [TestMethod]
    public void NoSettingsFile_IsAllowed_ButTheDeviceKeyFileIsNot()
    {
        using var directory = new WebTestSupport.TempDirectory();

        var configuration = KioskProgram.BuildConfiguration([], directory.Path);

        FluentActions.Invoking(() => KioskProgram.ReadOptions(configuration))
            .Should().Throw<KioskSettingsException>().WithMessage("Kiosk:DeviceKeyFile must name the file*");
        configuration["Logging:LogLevel:Default"].Should().Be("Information");
    }

    [TestMethod]
    public void ASettingsFileThatIsNotJson_OrAValueOfTheWrongType_StopsTheKiosk()
    {
        using var directory = new WebTestSupport.TempDirectory();
        File.WriteAllText(directory.File(KioskProgram.SettingsFile), "{ \"Kiosk\": { \"IdleLockSeconds\": ");

        FluentActions.Invoking(() => KioskProgram.BuildConfiguration([], directory.Path))
            .Should().Throw<KioskSettingsException>().WithMessage("The settings could not be read: *");

        File.WriteAllText(directory.File(KioskProgram.SettingsFile), "{ \"Kiosk\": { \"DeviceKeyFile\": \"key\", \"IdleLockSeconds\": \"soon\" } }");
        var configuration = KioskProgram.BuildConfiguration([], directory.Path);

        FluentActions.Invoking(() => KioskProgram.ReadOptions(configuration))
            .Should().Throw<KioskSettingsException>().WithMessage("The Kiosk settings could not be read: *");
    }

    [TestMethod]
    public void TheExampleSettings_AreValid()
    {
        var example = Path.Combine(RepositoryRoot(), "src", "HVO.RoofControllerV4.Kiosk", "deploy", "appsettings.Local.example.json");
        using var directory = new WebTestSupport.TempDirectory();
        File.Copy(example, directory.File(KioskProgram.SettingsFile));

        var options = KioskProgram.ReadOptions(KioskProgram.BuildConfiguration([], directory.Path));

        options.DeviceKeyFile.Should().NotBeNullOrWhiteSpace();
        options.Window.Should().BeFalse();
        options.Rotation.Should().Be(90, "the example is for the Touch Display 2, whose panel is portrait");
    }

    // ---- The device key -------------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheDeviceKey_IsOneLine_AndItsLineBreakIsIgnored()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var path = directory.File("device-key");
        File.WriteAllText(path, "test-kiosk-key-not-a-real-secret-05\r\n");

        KioskDeviceKey.Load(path).Should().Be("test-kiosk-key-not-a-real-secret-05");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("\n")]
    [DataRow(" test-kiosk-key-not-a-real-secret-05")]
    [DataRow("test-kiosk-key-not-a-real-secret-05 ")]
    [DataRow("test-kiosk-key\nnot-a-real-secret-05")]
    [DataRow("test-kiosk-key-not-a-real-secret-05\t")]
    [DataRow("test-kiosk-key-nöt-a-real-secret-05")]
    public void ADeviceKeyFile_ThatHoldsMoreOrLessThanAKey_StopsTheKiosk_WithoutShowingIt(string content)
    {
        using var directory = new WebTestSupport.TempDirectory();
        var path = directory.File("device-key");
        File.WriteAllText(path, content);

        var error = FluentActions.Invoking(() => KioskDeviceKey.Load(path)).Should().Throw<KioskSettingsException>().Which;

        error.Message.Should().StartWith($"Kiosk:DeviceKeyFile names {path}, which must hold one API key on one line.");
        if (content.Trim().Length > 0)
        {
            error.Message.Should().NotContain("secret-05");
        }
    }

    [TestMethod]
    public void ADeviceKeyFile_ThatCannotBeRead_StopsTheKiosk()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var path = directory.File("missing");

        FluentActions.Invoking(() => KioskDeviceKey.Load(path)).Should().Throw<KioskSettingsException>()
            .WithMessage($"Kiosk:DeviceKeyFile names {path}, which could not be read (FileNotFoundException). It must be readable by the kiosk's user.");
    }

    [TestMethod]
    public void ADeviceKeyFile_OthersMayRead_IsFound()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix permissions only.");
            return;
        }

        using var directory = new WebTestSupport.TempDirectory();
        var path = directory.File("device-key");
        File.WriteAllText(path, "test-kiosk-key-not-a-real-secret-05");

        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        KioskDeviceKey.IsOpenToOthers(path).Should().BeFalse();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        KioskDeviceKey.IsOpenToOthers(path).Should().BeFalse();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
        KioskDeviceKey.IsOpenToOthers(path).Should().BeTrue();
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.OtherRead);
        KioskDeviceKey.IsOpenToOthers(path).Should().BeTrue();
    }

    // ---- The display ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheDisplay_IsTheFirstCardWithAConnectedScreen()
    {
        using var sys = new WebTestSupport.TempDirectory();
        // A Pi 5: card0 is the render-only v3d (no connectors), card1 the display controller with HDMI and DSI.
        Directory.CreateDirectory(Path.Combine(sys.Path, "card0"));
        Connector(sys, "card1-HDMI-A-1", "disconnected");
        Connector(sys, "card1-HDMI-A-2", "disconnected");
        Connector(sys, "card2-DSI-1", "connected\n");
        Connector(sys, "card12-DSI-2", "connected");
        Directory.CreateDirectory(Path.Combine(sys.Path, "renderD128"));
        Directory.CreateDirectory(Path.Combine(sys.Path, "cardX-DSI-1"));

        KioskDisplay.FindCard(sys.Path).Should().Be("/dev/dri/card2");

        Connector(sys, "card1-HDMI-A-2", "connected");
        KioskDisplay.FindCard(sys.Path).Should().Be("/dev/dri/card1");
    }

    [TestMethod]
    public void NoConnectedScreen_OrNoDrm_FindsNoCard()
    {
        using var sys = new WebTestSupport.TempDirectory();
        Connector(sys, "card1-HDMI-A-1", "disconnected");
        Directory.CreateDirectory(Path.Combine(sys.Path, "card1-DSI-1"));

        KioskDisplay.FindCard(sys.Path).Should().BeNull("a connector without a status is passed over");
        KioskDisplay.FindCard(Path.Combine(sys.Path, "missing")).Should().BeNull();
    }

    [TestMethod]
    [DataRow(0, SurfaceOrientation.Rotation0)]
    [DataRow(90, SurfaceOrientation.Rotation90)]
    [DataRow(180, SurfaceOrientation.Rotation180)]
    [DataRow(270, SurfaceOrientation.Rotation270)]
    public void TheRotation_IsAvalonias(int rotation, SurfaceOrientation orientation)
        => KioskDisplay.Orientation(rotation).Should().Be(orientation);

    // ---- The backlight --------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheBacklight_GoesOffWithTheScreen_AndOnWithATouch_AndIsLeftOn()
    {
        await using var harness = await KioskHarness.CreateAsync(
            settings: new Dictionary<string, string?> { ["RoofControllerUi:KioskScreenTimeout"] = "00:00:30" });
        var path = Path.Combine(harness.Directory.Path, "bl_power");
        File.WriteAllText(path, "4");
        using (new KioskBacklight(harness.Console, path, "0", "4", harness.LoggerFactory.CreateLogger<KioskBacklight>()))
        {
            File.ReadAllText(path).Should().Be("0", "the kiosk starts with the screen on");
            await harness.StartLiveAsync();
            await harness.WaitForAsync(view => view.ScreenTimeout == TimeSpan.FromSeconds(30), "the controller's screen timeout");

            await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
            await harness.WaitForAsync(view => view.IsBlank, "the blank screen");
            File.ReadAllText(path).Should().Be("4");

            harness.Console.Touch().Should().BeTrue();
            File.ReadAllText(path).Should().Be("0");

            await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
            await harness.WaitForAsync(view => view.IsBlank, "the blank screen again");
            File.ReadAllText(path).Should().Be("4");
        }

        File.ReadAllText(path).Should().Be("0", "a kiosk that is not running leaves the backlight on");
        harness.Logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Warning && entry.Category.EndsWith(nameof(KioskBacklight), StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ABacklightThatCannotBeWritten_IsLoggedOnce_AndItsRecoveryToo()
    {
        await using var harness = await KioskHarness.CreateAsync(
            settings: new Dictionary<string, string?> { ["RoofControllerUi:KioskScreenTimeout"] = "00:00:30" });
        var control = Path.Combine(harness.Directory.Path, "backlight");
        var path = Path.Combine(control, "bl_power");
        using var backlight = new KioskBacklight(harness.Console, path, "0", "4", harness.LoggerFactory.CreateLogger<KioskBacklight>());
        await harness.StartLiveAsync();
        await harness.WaitForAsync(view => view.ScreenTimeout == TimeSpan.FromSeconds(30), "the controller's screen timeout");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        await harness.WaitForAsync(view => view.IsBlank, "the blank screen");

        Warnings().Should().ContainSingle().Which.Message.Should().Be(
            $"Backlight control {path} could not be written; a blank screen is drawn black with the backlight on");

        Directory.CreateDirectory(control);
        harness.Console.Touch();

        File.ReadAllText(path).Should().Be("0");
        harness.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Information && entry.Message == $"Backlight control {path} written again");
        Warnings().Should().ContainSingle();

        IEnumerable<(string Category, LogLevel Level, string Message, Exception? Exception)> Warnings()
            => harness.Logs.Entries.Where(entry => entry.Level == LogLevel.Warning && entry.Category.EndsWith(nameof(KioskBacklight), StringComparison.Ordinal));
    }

    private static void Connector(WebTestSupport.TempDirectory sys, string name, string status)
    {
        var connector = Path.Combine(sys.Path, name);
        Directory.CreateDirectory(connector);
        File.WriteAllText(Path.Combine(connector, "status"), status);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found.");
    }
}
