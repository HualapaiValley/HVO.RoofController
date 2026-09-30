using System.Runtime.Versioning;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;

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
            ["connection", "httpsPort", "httpPort", "webPort"], "only the choices are saved, not what follows from them");
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
    public void TheRecord_ReadsBack_AndSaysWhatIsInstalled()
    {
        var record = InstallerGuardTests.Record(InstallRole.Controller, HatMode.Real) with { Roles = [InstallRole.Controller, InstallRole.Kiosk] };

        var read = InstallRecord.Parse(record.ToJson());

        read.Should().BeEquivalentTo(record);
        read.SameAs(record).Should().BeTrue();
        read.DrivesRealHat.Should().BeTrue();
        read.ToAnswers().Should().BeEquivalentTo(new InstallAnswers { Roles = [InstallRole.Controller, InstallRole.Kiosk], Controller = new ControllerSettings() });
        using var document = JsonDocument.Parse(record.ToJson());
        document.RootElement.EnumerateObject().Select(member => member.Name).Should().Equal(
            "schema", "scope", "roles", "hat", "version", "installerVersion", "installedAt", "updatedAt", "controller");
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
    }

    [TestMethod]
    public async Task ARecordTheInstallerCannotRead_IsShownOnTheMachinePage()
    {
        using var pi = new FakeMachine().WithPi();
        pi.Write(InstallPaths.SystemRecord, "[]");

        var session = await InstallerSession.StartAsync(pi.Machine, InstallLog.None, "4.0.0", TimeProvider.System);

        session.Survey.SystemRecord.Should().BeNull();
        InstallerSession.DescribeSurvey(session.Survey).Should().Contain(line => line.StartsWith("             /etc/hvo-roof/install.json is not a valid install record", StringComparison.Ordinal));
    }
}
