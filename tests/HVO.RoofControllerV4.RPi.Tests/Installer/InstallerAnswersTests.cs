using System.Runtime.Versioning;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>Answers files and install records (#67): what they hold, how they read back, and that neither holds a secret.</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerAnswersTests
{
    [TestMethod]
    public void Answers_ReadBackAsTheyWereSaved()
    {
        var answers = new InstallAnswers
        {
            Roles = [InstallRole.Cli, InstallRole.Rig],
            Controller = new ControllerSettings { Connection = ConnectionMode.SelfSigned, HttpsPort = 9443, WebPort = 9088 },
            Cli = new CliSettings { Folder = CliSettings.SharedFolder }
        };

        var json = answers.ToJson();
        var read = InstallAnswers.Parse(json);

        read.Should().BeEquivalentTo(answers.Normalised());
        read.Roles.Should().Equal([InstallRole.Rig, InstallRole.Cli], "roles are kept in the order the installer offers them");
        read.ToJson().Should().Be(json);
    }

    [TestMethod]
    public void Answers_NameRolesAndChoicesInKebabCase_AndMembersInCamelCase()
    {
        var json = new InstallAnswers
        {
            Roles = [InstallRole.MacApp, InstallRole.Rig],
            Controller = new ControllerSettings { Connection = ConnectionMode.OwnCertificate }
        }.ToJson();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("schema").GetInt32().Should().Be(1);
        root.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).Should().Equal("rig", "mac-app");
        root.GetProperty("controller").GetProperty("connection").GetString().Should().Be("own-certificate");
        root.GetProperty("controller").EnumerateObject().Select(member => member.Name).Should().Equal(
            ["connection", "httpsPort", "httpPort", "webPort", "hostNames", "domains", "rig"], "only the choices are saved, not what follows from them");
        root.GetProperty("controller").GetProperty("httpsPort").GetInt32().Should().Be(8443);
        root.GetProperty("macApp").GetProperty("folder").GetString().Should().Be("/Applications");
        root.TryGetProperty("cli", out _).Should().BeFalse("a role not chosen has no section");
        InstallAnswers.Parse(json).Controller!.Connection.Should().Be(ConnectionMode.OwnCertificate);
        new InstallAnswers { Roles = [InstallRole.Controller] }.ToJson().Should().Contain("\"private-ca\"");
    }

    [TestMethod]
    public void ASectionForARoleNotChosen_IsLeftOut()
    {
        var answers = new InstallAnswers
        {
            Roles = [InstallRole.Cli],
            Controller = new ControllerSettings(),
            MacApp = new MacAppSettings()
        }.Normalised();

        answers.Controller.Should().BeNull();
        answers.MacApp.Should().BeNull();
        answers.Cli.Should().Be(new CliSettings(), "a chosen role gets its defaults");
    }

    [TestMethod]
    public void TheRigsConfirmation_IsNeverSaved_ButAnAnswersFileWrittenForTheMachineMayGiveIt()
    {
        var answers = new InstallAnswers { Roles = [InstallRole.Rig], RigConfirmation = "bench-pi" };

        var json = answers.ToJson();

        json.Should().NotContain("rigConfirmation").And.NotContain("bench-pi");
        InstallAnswers.Parse("""{ "roles": ["rig"], "rigConfirmation": "bench-pi" }""").RigConfirmation.Should().Be("bench-pi");
        InstallAnswers.Parse("""{ "roles": ["cli"], "rigConfirmation": "bench-pi" }""").RigConfirmation.Should().BeNull("it applies to a rig only");
    }

    [TestMethod]
    [DataRow("""{ "role": ["cli"] }""", "is not valid", DisplayName = "A misspelt member")]
    [DataRow("""{ "roles": ["cli"], "cli": { "folder": "~/.local/bin", "mode": "0755" } }""", "is not valid", DisplayName = "An unknown member in a section")]
    [DataRow("""{ "roles": ["the-controller"] }""", "is not valid", DisplayName = "An unknown role")]
    [DataRow("""{ "roles": [3] }""", "is not valid", DisplayName = "A role by number")]
    [DataRow("""{ "roles": ["cli"], }""", "is not valid", DisplayName = "A trailing comma")]
    [DataRow("""{ "roles": [] }""", "names no roles", DisplayName = "No roles")]
    [DataRow("""{ }""", "names no roles", DisplayName = "Nothing")]
    [DataRow("""null""", "is empty", DisplayName = "null")]
    [DataRow("""{ "schema": 2, "roles": ["cli"] }""", "The answers file's schema is 2; this installer reads schema 1.", DisplayName = "A newer schema")]
    public void AnAnswersFileTheInstallerCannotRead_IsAUsageError(string json, string expected)
    {
        var parse = () => InstallAnswers.Parse(json);

        parse.Should().Throw<InstallerUsageException>().Which.Message.Should().Contain(expected);
    }

    [TestMethod]
    public void AnAnswersFile_MayHaveComments()
    {
        InstallAnswers.Parse("""
            {
              // hvo-roof for the observatory's laptop
              "roles": ["cli"]
            }
            """).Roles.Should().Equal(InstallRole.Cli);
    }

    [TestMethod]
    public void ChoicesThatCannotWork_AreProblems()
    {
        new ControllerSettings { HttpsPort = 0 }.Problems().Should().Equal("The HTTPS port must be from 1 to 65535, not 0.");
        new ControllerSettings { WebPort = 70000 }.Problems().Should().Equal("The web UI's port must be from 1 to 65535, not 70000.");
        new ControllerSettings { WebPort = 8443 }.Problems().Should().Equal("The web UI needs a port of its own: 8443 is the controller's API's port too.");
        new ControllerSettings { Connection = ConnectionMode.Http, WebPort = 8443 }.Problems().Should().BeEmpty("over HTTP the API is on the HTTP port");
        new InstallAnswers { Roles = [InstallRole.Cli], Cli = new CliSettings { Folder = "/opt/bin" } }.Problems()
            .Should().Equal("hvo-roof's folder must be ~/.local/bin or /usr/local/bin, not '/opt/bin'.");
        new InstallAnswers { Roles = [InstallRole.MacApp], MacApp = new MacAppSettings { Folder = "~/Desktop" } }.Problems()
            .Should().Equal("The Mac app's folder must be /Applications or ~/Applications, not '~/Desktop'.");

        var parse = () => InstallAnswers.Parse("""{ "roles": ["controller"], "controller": { "httpsPort": 8088 } }""");
        parse.Should().Throw<InstallerUsageException>().WithMessage("The web UI needs a port of its own: 8088 is the controller's API's port too.");
    }

    [TestMethod]
    public void TheControllersOtherChoices_ReadBack_WithNoSecretAmongThem()
    {
        var answers = new InstallAnswers
        {
            Roles = [InstallRole.Controller],
            Controller = new ControllerSettings
            {
                FirstAdmin = new FirstAdminSettings { Name = " observer ", Pin = true },
                Camera = new CameraSettings { BaseUrl = "http://192.168.0.4:81", UserName = "roof-viewer" },
                TelemetryEndpoint = "http://collector:4318"
            }
        };

        var json = answers.ToJson();
        var read = InstallAnswers.Parse(json);

        read.Should().BeEquivalentTo(answers.Normalised());
        read.Controller!.FirstAdmin.Should().Be(new FirstAdminSettings { Name = "observer", Pin = true }, "the name is trimmed");
        read.Controller.Camera.Should().Be(new CameraSettings { BaseUrl = "http://192.168.0.4:81", UserName = "roof-viewer" });
        read.Controller.TelemetryEndpoint.Should().Be("http://collector:4318");
        read.Controller.Rig.Should().BeNull("the controller has no rig's choices");
        using var document = JsonDocument.Parse(json);
        var controller = document.RootElement.GetProperty("controller");
        controller.GetProperty("firstAdmin").EnumerateObject().Select(member => member.Name).Should().Equal("name", "pin");
        controller.GetProperty("camera").EnumerateObject().Select(member => member.Name).Should().Equal("baseUrl", "userName");
        json.Should().NotContainAny(["password", "Password", "\"key\""]);
    }

    [TestMethod]
    [DataRow("""{ "roles": ["controller"], "controller": { "firstAdmin": { "name": "observer", "password": "correct horse battery" } } }""", DisplayName = "The first admin's password")]
    [DataRow("""{ "roles": ["controller"], "controller": { "firstAdmin": { "name": "observer", "pinCode": "123456" } } }""", DisplayName = "The first admin's PIN")]
    [DataRow("""{ "roles": ["controller"], "controller": { "camera": { "baseUrl": "http://192.168.0.4:81", "userName": "v", "password": "p" } } }""", DisplayName = "The camera's password")]
    public void AnAnswersFile_HasNoPlaceForASecret(string json)
    {
        var parse = () => InstallAnswers.Parse(json);

        parse.Should().Throw<InstallerUsageException>().Which.Message.Should().Contain("is not valid").And.NotContain("correct horse").And.NotContain("123456");
    }

    [TestMethod]
    public void ARig_HasItsEmulatorsChoices_AndNoCamera()
    {
        var rig = new InstallAnswers
        {
            Roles = [InstallRole.Rig],
            Controller = new ControllerSettings { Camera = new CameraSettings { BaseUrl = "http://192.168.0.4:81" } }
        }.Normalised();

        rig.Controller!.Camera.Should().BeNull("a rig shows the HAT emulator's camera");
        rig.Controller.Rig.Should().Be(new RigSettings { TimeScale = 1, CameraFramesPerSecond = 5, OpenToLan = false });

        var read = InstallAnswers.Parse("""{ "roles": ["rig"], "controller": { "rig": { "timeScale": 10, "cameraFramesPerSecond": 2, "openToLan": true } } }""");
        read.Controller!.Rig.Should().Be(new RigSettings { TimeScale = 10, CameraFramesPerSecond = 2, OpenToLan = true });
        InstallAnswers.Parse(read.ToJson()).Should().BeEquivalentTo(read);

        new InstallAnswers { Roles = [InstallRole.Controller], Controller = new ControllerSettings { Rig = new RigSettings() } }.Normalised()
            .Controller!.Rig.Should().BeNull("the controller has no rig's choices");
    }

    [TestMethod]
    [DataRow("""{ "roles": ["rig"], "controller": { "camera": { "baseUrl": "http://192.168.0.4:81" } } }""", "A rig shows the HAT emulator's camera: leave out controller.camera.", DisplayName = "A rig with a camera")]
    [DataRow("""{ "roles": ["controller"], "controller": { "rig": { "timeScale": 2 } } }""", "controller.rig is for a test rig: leave it out for the controller.", DisplayName = "The controller with a rig's choices")]
    [DataRow("""{ "roles": ["rig"], "controller": { "connection": "http", "rig": { "openToLan": true } } }""", "A rig opened to the network needs HTTPS", DisplayName = "A rig open to the network over HTTP")]
    [DataRow("""{ "roles": ["rig"], "controller": { "rig": { "timeScale": 0 } } }""", "The rig's time scale must be from 0.1 to 100, not 0.", DisplayName = "A time scale of 0")]
    [DataRow("""{ "roles": ["rig"], "controller": { "rig": { "timeScale": 1000 } } }""", "The rig's time scale must be from 0.1 to 100, not 1000.", DisplayName = "A time scale of 1000")]
    [DataRow("""{ "roles": ["rig"], "controller": { "rig": { "cameraFramesPerSecond": 60 } } }""", "The rig's camera frame rate must be from 0.1 to 30 frames a second, not 60.", DisplayName = "A frame rate of 60")]
    [DataRow("""{ "roles": ["controller"], "controller": { "firstAdmin": { "name": "-admin" } } }""", "'-admin' is not a name the controller takes for its first admin", DisplayName = "A first admin's name starting with a hyphen")]
    [DataRow("""{ "roles": ["controller"], "controller": { "firstAdmin": { "name": "" } } }""", "'' is not a name the controller takes", DisplayName = "No first admin's name")]
    [DataRow("""{ "roles": ["controller"], "controller": { "camera": { "baseUrl": "192.168.0.4" } } }""", "The camera's address must be an absolute http or https address", DisplayName = "A camera without a scheme")]
    [DataRow("""{ "roles": ["controller"], "controller": { "camera": { "baseUrl": "rtsp://192.168.0.4" } } }""", "The camera's address must be an absolute http or https address", DisplayName = "A camera over RTSP")]
    [DataRow("""{ "roles": ["controller"], "controller": { "camera": { "baseUrl": "http://viewer:hunter2@192.168.0.4:81" } } }""", "The camera's address may not hold a user name or password", DisplayName = "A camera's password in its address")]
    [DataRow("""{ "roles": ["controller"], "controller": { "camera": { "baseUrl": "http://192.168.0.4:81/mjpg" } } }""", "The camera's address must be the server alone, with no path.", DisplayName = "A camera's path")]
    [DataRow("""{ "roles": ["controller"], "controller": { "camera": { "baseUrl": "http://192.168.0.4:81/?user=v" } } }""", "The camera's address may not have a query or a fragment.", DisplayName = "A camera's query")]
    [DataRow("""{ "roles": ["controller"], "controller": { "camera": { "baseUrl": "http://192.168.0.4:81", "userName": "a:b" } } }""", "The camera's user name may not hold a colon", DisplayName = "A camera's user name with a colon")]
    [DataRow("""{ "roles": ["controller"], "controller": { "telemetryEndpoint": "collector:4318" } }""", "The telemetry endpoint must be an absolute http or https address", DisplayName = "Telemetry without a scheme")]
    [DataRow("""{ "roles": ["controller"], "controller": { "telemetryEndpoint": "https://token:hunter2@collector:4318" } }""", "The telemetry endpoint may not hold a user name or password", DisplayName = "Telemetry with a password")]
    public void ChoicesThatCannotWork_AreRefused(string json, string expected)
    {
        var parse = () => InstallAnswers.Parse(json);

        parse.Should().Throw<InstallerUsageException>().Which.Message.Should().Contain(expected).And.NotContain("hunter2", "an answers file's problem never repeats a password");
    }

    [TestMethod]
    public void BlankOptionalChoices_AreNone()
    {
        var answers = InstallAnswers.Parse("""{ "roles": ["controller"], "controller": { "telemetryEndpoint": " ", "camera": { "baseUrl": " http://192.168.0.4:81 ", "userName": " " } } }""");

        answers.Controller!.TelemetryEndpoint.Should().BeNull("a blank endpoint turns telemetry off");
        answers.Controller.Camera.Should().Be(new CameraSettings { BaseUrl = "http://192.168.0.4:81" }, "a server that asks for no user");
        InstallAnswers.Parse("""{ "roles": ["controller"], "controller": { "telemetryEndpoint": "http://collector:4318/v1" } }""")
            .Controller!.TelemetryEndpoint.Should().Be("http://collector:4318/v1", "a collector may be under a path");
    }

    [TestMethod]
    public async Task ARunningControllersTelemetry_AndARigsPace_AreTheDefaults()
    {
        using var bench = new FakeMachine(architecture: System.Runtime.InteropServices.Architecture.X64, hostName: "bench")
            .WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer
            {
                Emulated = true,
                Settings = new Dictionary<string, string> { [MachineSurveyor.TelemetryEndpointSetting] = "http://collector:4318" }
            })
            .WithContainer(MachineSurveyor.HatEmulatorContainer, new FakeContainer
            {
                Settings = new Dictionary<string, string>
                {
                    [MachineSurveyor.EmulatorTimeScaleSetting] = "10",
                    [MachineSurveyor.EmulatorCameraFramesSetting] = "2.5"
                }
            });

        var session = await InstallerSession.StartAsync(bench.Machine, InstallLog.None, "4.0.0", FakeMachine.Clock);

        session.DefaultController.TelemetryEndpoint.Should().Be("http://collector:4318", "adopting a controller keeps its telemetry");
        session.DefaultController.Rig.Should().Be(new RigSettings { TimeScale = 10, CameraFramesPerSecond = 2.5, OpenToLan = true }, "a rig published on every address stays open to the network");

        bench.Containers[MachineSurveyor.ControllerContainer] = bench.Containers[MachineSurveyor.ControllerContainer] with { PublishAddress = "127.0.0.1" };
        var local = await InstallerSession.StartAsync(bench.Machine, InstallLog.None, "4.0.0", FakeMachine.Clock);
        local.DefaultController.Rig!.OpenToLan.Should().BeFalse("a rig on 127.0.0.1 stays there");

        using var bare = new FakeMachine();
        var fresh = await InstallerSession.StartAsync(bare.Machine, InstallLog.None, "4.0.0", FakeMachine.Clock);
        fresh.DefaultController.TelemetryEndpoint.Should().BeNull("export is off until an endpoint is given");
        fresh.DefaultController.Rig.Should().Be(new RigSettings());
    }

    [TestMethod]
    public void TheRecord_ReadsBack_AndSaysWhatIsInstalled()
    {
        var record = InstallerGuardTests.Record(InstallRole.Controller, HatMode.Real) with
        {
            Roles = [InstallRole.Controller, InstallRole.Kiosk],
            Kiosk = new KioskSettings { HideCursor = false }
        };

        var read = InstallRecord.Parse(record.ToJson());

        read.Should().BeEquivalentTo(record);
        read.SameAs(record).Should().BeTrue();
        read.DrivesRealHat.Should().BeTrue();
        read.ToAnswers().Should().BeEquivalentTo(new InstallAnswers
        {
            Roles = [InstallRole.Controller, InstallRole.Kiosk],
            Controller = new ControllerSettings(),
            Kiosk = new KioskSettings { HideCursor = false }
        });
        using var document = JsonDocument.Parse(record.ToJson());
        document.RootElement.EnumerateObject().Select(member => member.Name).Should().Equal(
            "schema", "scope", "roles", "hat", "version", "installerVersion", "installedAt", "updatedAt", "controller", "kiosk");
        document.RootElement.GetProperty("kiosk").GetProperty("hideCursor").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("scope").GetString().Should().Be("system");
        document.RootElement.GetProperty("hat").GetString().Should().Be("real");
    }

    [TestMethod]
    public void TheRecord_IsTheSame_WhenOnlyItsTimesDiffer()
    {
        var record = InstallerGuardTests.Record(InstallRole.Rig, HatMode.Emulated);

        record.SameAs(record with { InstalledAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.MaxValue }).Should().BeTrue();
        record.SameAs(record with { Version = "4.0.1" }).Should().BeFalse();
        record.SameAs(record with { Controller = new ControllerSettings { WebPort = 9000 } }).Should().BeFalse();
        record.SameAs(null).Should().BeFalse();
    }

    [TestMethod]
    public void TheRecord_DrivesTheRealHat_WhenItSaysSo_OrNamesTheController()
    {
        InstallerGuardTests.Record(InstallRole.Rig, HatMode.Emulated).DrivesRealHat.Should().BeFalse();
        InstallerGuardTests.Record(InstallRole.Rig, HatMode.Real).DrivesRealHat.Should().BeTrue();
        InstallerGuardTests.Record(InstallRole.Controller, null).DrivesRealHat.Should().BeTrue();
        InstallerGuardTests.Record(InstallRole.Cli, null).DrivesRealHat.Should().BeFalse();
    }

    [TestMethod]
    public void ARecordTheInstallerCannotRead_IsAProblem_NotAnError()
    {
        using var pi = new FakeMachine().WithPi();

        InstallRecord.Load(pi.Machine, InstallPaths.SystemRecord).Should().Be(((InstallRecord?)null, (string?)null), "no record is nothing installed");

        pi.Write(InstallPaths.SystemRecord, "{ not json");
        InstallRecord.Load(pi.Machine, InstallPaths.SystemRecord).Problem.Should().StartWith("/etc/hvo-roof/install.json is not a valid install record: ");

        pi.Write(InstallPaths.SystemRecord, InstallerGuardTests.Record(InstallRole.Controller, HatMode.Real).ToJson().Replace("\"schema\": 1", "\"schema\": 2", StringComparison.Ordinal));
        var (record, problem) = InstallRecord.Load(pi.Machine, InstallPaths.SystemRecord);
        record.Should().BeNull();
        problem.Should().Contain("written by a newer installer (schema 2); this one reads schema 1");

        // A comment, which the record's own reading skips, does not hide the schema.
        pi.Write(InstallPaths.SystemRecord, "{ /* kept by hand */ \"schema\": 2, \"scope\": \"system\", \"roles\": [\"controller\"] }");
        InstallRecord.SchemaOf(pi.Machine, InstallPaths.SystemRecord).Should().Be(2);
        InstallRecord.Load(pi.Machine, InstallPaths.SystemRecord).Problem.Should().Contain("written by a newer installer (schema 2)");
    }

    [TestMethod]
    public async Task ARecordTheInstallerCannotRead_IsShownOnTheMachinePage()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Write(InstallPaths.SystemRecord, "[]");

        var session = await InstallerSession.StartAsync(pi.Machine, InstallLog.None, "4.0.0", FakeMachine.Clock);

        session.Survey.SystemRecord.Should().BeNull();
        InstallerSession.DescribeSurvey(session.Survey, FakeMachine.Today).Should().Contain(line => line.StartsWith("             /etc/hvo-roof/install.json is not a valid install record", StringComparison.Ordinal));
    }
}
