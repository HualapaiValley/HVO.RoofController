using System.Xml.Linq;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Mac;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Web;
using HVO.RoofControllerV4.Screens;
using Microsoft.Extensions.Logging.Abstractions;
using MacProgram = HVO.RoofControllerV4.Mac.Program;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The Mac app's settings, settings folder and device key: what it starts with, and what it refuses to start with (it
/// exits with <see cref="MacProgram.SettingsExitCode"/> and, opened from the Finder, shows why). The program itself runs
/// here only with settings it refuses; CI runs the signed app on a Mac with <see cref="MacProgram.CheckFlag"/>.
/// </summary>
[TestClass]
public sealed class MacProgramTests
{
    private const string Pin = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99";
    private const string Key = "test-mac-key-not-a-real-secret-05";

    // ---- Settings -------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheDefaults_NeedOnlyTheControllerUrl()
    {
        var options = new MacOptions();

        options.Validate().Should().ContainSingle().Which.Should().StartWith("Mac:ControllerUrl must be the controller's address");

        options.ControllerUrl = new Uri("https://roof-pi.local:8443/");
        options.Validate().Should().BeEmpty();
        options.DeviceKeyFile.Should().BeNull();
        options.IdleLockSeconds.Should().Be(900);
        options.PixelsPerMillimetre.Should().Be(KioskMetrics.DeskPixelsPerMillimetre);
        new KioskMetrics(options.PixelsPerMillimetre).Should().BeEquivalentTo(KioskMetrics.Desk);
    }

    [TestMethod]
    public void EverySettingThatIsWrong_IsNamed()
    {
        var options = new MacOptions
        {
            ControllerUrl = new Uri("ftp://roof-pi/"),
            ServerCertificateSha256 = "not-a-hash",
            IdleLockSeconds = 5,
            PixelsPerMillimetre = double.NaN
        };

        options.Validate().Select(problem => problem[..problem.IndexOf(' ', StringComparison.Ordinal)]).Should().Equal(
            "Mac:ControllerUrl",
            "Mac:ServerCertificateSha256",
            "Mac:IdleLockSeconds",
            "Mac:PixelsPerMillimetre");
        options.ControllerUrl = new Uri("roof-pi/api", UriKind.Relative);
        options.Validate()[0].Should().StartWith("Mac:ControllerUrl must be an absolute http or https URL");
    }

    [TestMethod]
    [DataRow(10, true)]
    [DataRow(3600, true)]
    [DataRow(9, false)]
    [DataRow(3601, false)]
    public void TheIdleLock_IsFrom10SecondsToAnHour(int seconds, bool valid)
        => new MacOptions { ControllerUrl = new Uri("http://roof-pi:8080/"), IdleLockSeconds = seconds }.Validate().Should().HaveCount(valid ? 0 : 1);

    [TestMethod]
    public void TheSettings_AreReadFromTheFile_ThenTheEnvironment_ThenTheCommandLine()
    {
        using var directory = new WebTestSupport.TempDirectory();
        File.WriteAllText(directory.File(MacProgram.SettingsFile), $$"""
            {
              "Mac": {
                "ControllerUrl": "https://roof-pi.local:8443/",
                "ServerCertificateSha256": "{{Pin}}",
                "DeviceKeyFile": "keys/roof",
                "IdleLockSeconds": 300,
                "PixelsPerMillimetre": 5
              }
            }
            """);

        var options = MacProgram.ReadOptions(MacProgram.BuildConfiguration(["--Mac:IdleLockSeconds=60"], directory.Path));

        options.ControllerUrl.Should().Be(new Uri("https://roof-pi.local:8443/"));
        options.ServerCertificateSha256.Should().Be(Pin);
        options.IdleLockSeconds.Should().Be(60, "the command line wins");
        options.PixelsPerMillimetre.Should().Be(5);
        options.DeviceKeyPath(directory.Path).Should().Be(Path.Combine(directory.Path, "keys", "roof"));
    }

    [TestMethod]
    public void NoSettingsFile_IsAllowed_ButNoControllerUrlIsNot()
    {
        using var directory = new WebTestSupport.TempDirectory();

        var configuration = MacProgram.BuildConfiguration([], directory.Path);

        FluentActions.Invoking(() => MacProgram.ReadOptions(configuration))
            .Should().Throw<KioskSettingsException>().WithMessage("Mac:ControllerUrl must be the controller's address*");
        configuration["Logging:LogLevel:Default"].Should().Be("Information");
    }

    [TestMethod]
    public void ASettingsFileThatIsNotJson_OrAValueOfTheWrongType_IsRefused()
    {
        using var directory = new WebTestSupport.TempDirectory();
        File.WriteAllText(directory.File(MacProgram.SettingsFile), "{ \"Mac\": { \"IdleLockSeconds\": ");

        FluentActions.Invoking(() => MacProgram.BuildConfiguration([], directory.Path))
            .Should().Throw<KioskSettingsException>().WithMessage("The settings could not be read: *");

        File.WriteAllText(directory.File(MacProgram.SettingsFile), "{ \"Mac\": { \"ControllerUrl\": \"http://roof-pi:8080/\", \"IdleLockSeconds\": \"soon\" } }");
        var configuration = MacProgram.BuildConfiguration([], directory.Path);

        FluentActions.Invoking(() => MacProgram.ReadOptions(configuration))
            .Should().Throw<KioskSettingsException>().WithMessage("The Mac settings could not be read: *");
    }

    // ---- The settings folder and the device key -------------------------------------------------------------------------

    [TestMethod]
    public void OnAMac_TheSettingsAreInApplicationSupport()
        => MacSettingsFolder.Find(_ => null, "/Users/observer", isMac: true)
            .Should().Be(Path.Combine("/Users/observer", "Library", "Application Support", "HVO Roof"));

    [TestMethod]
    [DataRow(null, "/home/observer/.config/hvo-roof-mac")]
    [DataRow("", "/home/observer/.config/hvo-roof-mac")]
    [DataRow("relative/config", "/home/observer/.config/hvo-roof-mac")]
    [DataRow("/srv/config", "/srv/config/hvo-roof-mac")]
    public void Elsewhere_TheSettingsAreInXdgConfigHome_WhenItIsAbsolute(string? xdg, string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix paths only.");
            return;
        }

        MacSettingsFolder.Find(name => name == "XDG_CONFIG_HOME" ? xdg : null, "/home/observer", isMac: false).Should().Be(folder);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void TheVariable_NamesAnotherFolder(bool isMac)
    {
        using var directory = new WebTestSupport.TempDirectory();
        var environment = new Dictionary<string, string?> { [MacSettingsFolder.Variable] = directory.Path, ["XDG_CONFIG_HOME"] = "/srv/config" };

        MacSettingsFolder.Find(name => environment.GetValueOrDefault(name), "/home/observer", isMac).Should().Be(directory.Path);
        environment[MacSettingsFolder.Variable] = "  ";
        MacSettingsFolder.Find(name => environment.GetValueOrDefault(name), "/home/observer", isMac).Should().NotBe(directory.Path, "a blank variable names nothing");
    }

    [TestMethod]
    public void TheDeviceKey_IsInTheSettingsFolder_UnlessItIsNamedElsewhere()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        new MacOptions().DeviceKeyPath(directory.Path).Should().Be(directory.File(MacOptions.DefaultDeviceKeyFile));
        new MacOptions { DeviceKeyFile = " " }.DeviceKeyPath(directory.Path).Should().Be(directory.File(MacOptions.DefaultDeviceKeyFile));
        new MacOptions { DeviceKeyFile = "roof-2.key" }.DeviceKeyPath(directory.Path).Should().Be(directory.File("roof-2.key"));
        new MacOptions { DeviceKeyFile = "~/keys/roof" }.DeviceKeyPath(directory.Path).Should().Be(Path.Combine(home, "keys", "roof"));
        var elsewhere = Path.Combine(Path.GetTempPath(), "roof.key");
        new MacOptions { DeviceKeyFile = elsewhere }.DeviceKeyPath(directory.Path).Should().Be(elsewhere);
    }

    [TestMethod]
    public void TheCaCertificate_IsInTheSettingsFolder_UnlessItIsNamedElsewhere()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        new MacOptions().CaCertificatePath(directory.Path).Should().BeNull();
        new MacOptions { ServerCaCertificateFile = " " }.CaCertificatePath(directory.Path).Should().BeNull();
        new MacOptions { ServerCaCertificateFile = "roof-ca.pem" }.CaCertificatePath(directory.Path).Should().Be(directory.File("roof-ca.pem"));
        new MacOptions { ServerCaCertificateFile = "~/certs/roof-ca.pem" }.CaCertificatePath(directory.Path).Should().Be(Path.Combine(home, "certs", "roof-ca.pem"));
        var elsewhere = Path.Combine(Path.GetTempPath(), "roof-ca.pem");
        new MacOptions { ServerCaCertificateFile = elsewhere }.CaCertificatePath(directory.Path).Should().Be(elsewhere);
    }

    [TestMethod]
    public void TheCaCertificate_IsReadFromTheSettings_AndGivenToTheClient()
    {
        using var directory = new WebTestSupport.TempDirectory();
        using var authority = TestCertificates.CreateAuthority("HVO Roof test CA");
        File.WriteAllBytes(directory.File("roof-ca.der"), authority.RawData);
        using var error = new StringWriter();

        var options = MacProgram.ReadOptions(MacProgram.BuildConfiguration(
            ["--Mac:ControllerUrl=https://roof-pi.local:8443/", "--Mac:ServerCaCertificateFile= roof-ca.der "], directory.Path));
        using var loaded = KioskCaCertificate.Load(options.CaCertificatePath(directory.Path)!, "Mac:ServerCaCertificateFile", "you");
        using var client = MacProgram.Connect(options, loaded, Key, NullLoggerFactory.Instance, error);

        options.ServerCaCertificateFile.Should().Be("roof-ca.der");
        loaded.RawData.Should().Equal(authority.RawData);
        client.Should().NotBeNull();
        error.ToString().Should().BeEmpty();
        MacProgram.ReadOptions(MacProgram.BuildConfiguration(["--Mac:ControllerUrl=https://roof-pi.local:8443/", "--Mac:ServerCaCertificateFile="], directory.Path))
            .ServerCaCertificateFile.Should().BeNull("an empty file name is no CA");
    }

    // ---- Refusing to start ----------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(null, "", "Mac:ControllerUrl must be the controller's address*", DisplayName = "no settings")]
    [DataRow("{ \"Mac\": { \"IdleLockSeconds\": ", "", "The settings could not be read: *", DisplayName = "not JSON")]
    [DataRow("{ \"Mac\": { \"ControllerUrl\": \"http://roof-pi:8080/\", \"IdleLockSeconds\": \"soon\" } }", "", "The Mac settings could not be read: *", DisplayName = "a value of the wrong type")]
    [DataRow("{ \"Mac\": { \"ControllerUrl\": \"http://roof-pi:8080/\" } }", "--Mac:ServerCertificateSha256=not-a-hash", "Mac:ServerCertificateSha256 *", DisplayName = "a pin that is not a hash")]
    [DataRow("{ \"Mac\": { \"ControllerUrl\": \"http://roof-pi:8080/\", \"DeviceKeyFile\": \"missing\" } }", "", "Mac:DeviceKeyFile names *missing, which could not be read (FileNotFoundException). It must be readable by you.", DisplayName = "no device key file")]
    [DataRow("{ \"Mac\": { \"ControllerUrl\": \"http://roof-pi:8080/\", \"DeviceKeyFile\": \"two-lines\" } }", "", "Mac:DeviceKeyFile names *two-lines, which must hold one API key on one line.*", DisplayName = "a device key file that is not one key")]
    [DataRow("{ \"Mac\": { \"ControllerUrl\": \"https://roof-pi:8443/\", \"ServerCaCertificateFile\": \"roof-ca.pem\" } }", "", "Mac:ServerCaCertificateFile names *roof-ca.pem, which could not be read (FileNotFoundException). It must be readable by you.", DisplayName = "no CA certificate file")]
    [DataRow("{ \"Mac\": { \"ControllerUrl\": \"https://roof-pi:8443/\", \"ServerCaCertificateFile\": \"two-lines\" } }", "", "Mac:ServerCaCertificateFile cannot be used: *two-lines does not hold a certificate in PEM or DER form.", DisplayName = "a CA certificate file that is not a certificate")]
    [DataRow("{ \"Mac\": { \"ControllerUrl\": \"https://roof-pi:8443/\", \"ServerCaCertificateFile\": \"two-lines\" } }", "--Mac:ServerCertificateSha256=" + Pin, "Mac:ServerCaCertificateFile and Mac:ServerCertificateSha256 are both set. Set one: *", DisplayName = "a CA and a pin")]
    public void TheApp_ExitsWithTheSettingsCode_SaysWhy_AndShowsIt(string? settings, string arg, string reason)
    {
        using var directory = new WebTestSupport.TempDirectory();
        File.WriteAllText(directory.File(MacOptions.DefaultDeviceKeyFile), Key);
        File.WriteAllText(directory.File("two-lines"), $"{Key}\n{Key}");
        if (settings is not null)
        {
            File.WriteAllText(directory.File(MacProgram.SettingsFile), settings);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        var shown = new List<(string Reason, string File)>();

        MacProgram.Run(arg.Length > 0 ? [arg] : [], directory.Path, output, error, (why, file) => shown.Add((why, file)))
            .Should().Be(MacProgram.SettingsExitCode);

        var lines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(2);
        lines[0].Should().Match($"HVO Roof did not start: {reason}");
        lines[1].Should().Be(settings is null
            ? $"Its settings file is {directory.File(MacProgram.SettingsFile)}, which does not exist yet."
            : $"Its settings file is {directory.File(MacProgram.SettingsFile)}.");
        shown.Should().ContainSingle().Which.Should().Be((lines[0]["HVO Roof did not start: ".Length..], directory.File(MacProgram.SettingsFile)));
        error.ToString().Should().NotContain("secret-05");
        output.ToString().Should().BeEmpty();
    }

    [TestMethod]
    public void AsACheck_ARefusalIsOnlyWritten()
    {
        using var directory = new WebTestSupport.TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var shown = 0;

        MacProgram.Run([MacProgram.CheckFlag], directory.Path, output, error, (_, _) => shown++).Should().Be(MacProgram.SettingsExitCode);

        shown.Should().Be(0, "a check has no one to show it to");
        error.ToString().Should().StartWith("HVO Roof did not start: Mac:ControllerUrl must be the controller's address");
    }

    [TestMethod]
    public void TheCheckFlag_IsNotASetting()
    {
        using var directory = new WebTestSupport.TempDirectory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        // Were --check passed on to the command line settings, it would take the next argument as its value, and the
        // idle lock would not be read.
        MacProgram.Run([MacProgram.CheckFlag, "--Mac:IdleLockSeconds=5"], directory.Path, output, error).Should().Be(MacProgram.SettingsExitCode);

        error.ToString().Should().Contain("Mac:ControllerUrl must be").And.Contain("Mac:IdleLockSeconds must be between 10 and 3600, got 5.");
    }

    [TestMethod]
    [DataRow("--Mac:ServerCertificateSha256=", DisplayName = "empty on the command line")]
    [DataRow("--Mac:ServerCertificateSha256=   ", DisplayName = "blank on the command line")]
    public void AnEmptyCertificatePin_IsNoPin(string arg)
    {
        using var directory = new WebTestSupport.TempDirectory();
        using var error = new StringWriter();

        var options = MacProgram.ReadOptions(MacProgram.BuildConfiguration([arg, "--Mac:ControllerUrl=https://roof-pi.local:8443/"], directory.Path));
        using var client = MacProgram.Connect(options, null, Key, NullLoggerFactory.Instance, error);

        options.ServerCertificateSha256.Should().BeNull();
        client.Should().NotBeNull();
        error.ToString().Should().BeEmpty();
    }

    [TestMethod]
    public void SettingsTheClientRefuses_AreSaid_NotThrown()
    {
        using var error = new StringWriter();
        var options = new MacOptions { ControllerUrl = new Uri("https://roof-pi.local:8443/"), ServerCertificateSha256 = "" };

        MacProgram.Connect(options, null, Key, NullLoggerFactory.Instance, error).Should().BeNull();

        error.ToString().Should().StartWith("HVO Roof did not start: The certificate pin must be a SHA-256 hash");
    }

    [TestMethod]
    public void ACaThatIsNotOne_IsSaid_NotThrown()
    {
        using var authority = TestCertificates.CreateAuthority("HVO Roof test CA");
        using var issued = TestCertificates.Issue(authority);
        using var error = new StringWriter();
        var options = new MacOptions { ControllerUrl = new Uri("https://roof-pi.local:8443/") };

        MacProgram.Connect(options, issued, Key, NullLoggerFactory.Instance, error).Should().BeNull();

        error.ToString().Should().StartWith("HVO Roof did not start: The CA certificate is not a CA's: its basic constraints do not say CA.");
    }

    // ---- The bundle -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public void TheBundlesInfoPlist_NamesTheProgram_TheIcon_AndItsPlaceholders()
    {
        var plist = XDocument.Load(BundleFile("Info.plist"));
        var entries = plist.Root!.Element("dict")!.Elements().ToList();
        var values = new Dictionary<string, XElement>();
        for (var index = 0; index + 1 < entries.Count; index += 2)
        {
            entries[index].Name.LocalName.Should().Be("key");
            values.Add(entries[index].Value, entries[index + 1]);
        }

        values["CFBundleExecutable"].Value.Should().Be("hvo-roof-mac", "the program the project publishes");
        values["CFBundleName"].Value.Should().Be(MacProgram.Title);
        values["CFBundleDisplayName"].Value.Should().Be(MacProgram.Title);
        values["CFBundleIconFile"].Value.Should().Be("AppIcon");
        values["CFBundlePackageType"].Value.Should().Be("APPL");
        values["CFBundleShortVersionString"].Value.Should().Be("@VERSION@");
        values["CFBundleVersion"].Value.Should().Be("@BUILD@");
        values["LSMinimumSystemVersion"].Value.Should().Be("@MINIMUM_SYSTEM@", "bundle.py writes the oldest macOS the program and its libraries run on");
        values["NSHighResolutionCapable"].Name.LocalName.Should().Be("true");
        File.Exists(BundleFile("AppIcon.icns")).Should().BeTrue();
        typeof(MacProgram).Assembly.GetName().Name.Should().Be("hvo-roof-mac");
    }

    internal static string BundleFile(string name)
        => Path.Combine(ControllerForcedRestartTests.RepositoryRoot(), "src", "HVO.RoofControllerV4.Mac", "bundle", name);
}
