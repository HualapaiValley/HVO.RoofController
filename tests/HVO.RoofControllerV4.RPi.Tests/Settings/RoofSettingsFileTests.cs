using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Middleware;
using HVO.RoofControllerV4.RPi.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace HVO.RoofControllerV4.RPi.Tests.Settings;

/// <summary>
/// The settings file and the managed secrets file on their own: what a file may hold, its metadata, the messages for a
/// file that cannot be used (which name the file and never a value), and the atomic write.
/// </summary>
[TestClass]
public sealed class RoofSettingsFileTests
{
    private const string Path = "/etc/hvo-roof/config/appsettings.Local.json";

    private string _directory = null!;

    [TestInitialize]
    public void CreateDirectory()
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hvo-settings-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void DeleteDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            Unlock(_directory);
        }

        Directory.Delete(_directory, recursive: true);

        // Top down, so a directory a test locked can be searched before its children are listed.
        static void Unlock(string directory)
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var child in Directory.GetDirectories(directory))
            {
                Unlock(child);
            }
        }
    }

    [TestMethod]
    public void AFileWithoutMetadata_IsVersion1_AndItsSettingsAreFlattened()
    {
        var document = Parse("""
            // Comments and trailing commas are allowed, as in appsettings.json.
            {
              "RoofControllerUi": { "DefaultCamera": "Roof", },
              "RoofControllerSecurity": { "AllowedOrigins": [ "https://roof.example", "https://kiosk.example" ] }
            }
            """);

        document.Exists.Should().BeTrue();
        document.Version.Should().Be(1);
        document.SavedAtUtc.Should().BeNull();
        document.SavedBy.Should().BeNull();
        document.Data.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["RoofControllerUi:DefaultCamera"] = "Roof",
            ["RoofControllerSecurity:AllowedOrigins:0"] = "https://roof.example",
            ["RoofControllerSecurity:AllowedOrigins:1"] = "https://kiosk.example"
        });
        document.Data.Should().ContainKey("roofcontrollerui:defaultcamera", "configuration keys are case-insensitive");
    }

    [TestMethod]
    public void TheMetadata_IsReadAndKeptOutOfTheSettings()
    {
        var document = Parse("""
            { "hvoroofsettings": { "Version": 7, "SavedAtUtc": "2026-09-01T12:00:00Z", "SavedBy": "ada" },
              "RoofControllerUi": { "DefaultCamera": "Roof" } }
            """);

        document.Version.Should().Be(7);
        document.SavedAtUtc.Should().Be(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        document.SavedBy.Should().Be("ada");
        document.Data.Keys.Should().Equal("RoofControllerUi:DefaultCamera");
        document.Body.Select(property => property.Key).Should().Equal("RoofControllerUi");
    }

    [TestMethod]
    [DataRow("{ \"HvoRoofSettings\": 3 }", "HvoRoofSettings must be an object")]
    [DataRow("{ \"HvoRoofSettings\": { \"Version\": 0 } }", "HvoRoofSettings:Version must be a whole number of at least 1")]
    [DataRow("{ \"HvoRoofSettings\": { \"Version\": \"7\" } }", "HvoRoofSettings:Version must be a whole number of at least 1")]
    [DataRow("{ \"HvoRoofSettings\": { \"SavedAtUtc\": \"yesterday\" } }", "HvoRoofSettings:SavedAtUtc must be a date and time")]
    [DataRow("{ \"HvoRoofSettings\": {}, \"hvoRoofSettings\": {} }", "has more than one HvoRoofSettings object")]
    public void BadMetadata_IsRefused(string content, string expected)
        => FluentActions.Invoking(() => Parse(content)).Should().Throw<RoofSettingsFileException>().WithMessage($"*{expected}*");

    [TestMethod]
    [DataRow("{ \"RoofControllerUi\": { \"DefaultCamera\": \"quoted-value", "is not valid JSON (line 1, position ")]
    [DataRow("", "is not valid JSON")]
    [DataRow("\"quoted-value\"", "must hold a JSON object, like appsettings.json")]
    [DataRow("{ \"RoofControllerUi\": { \"DefaultCamera\": \"quoted-value\", \"DEFAULTCAMERA\": \"x\" } }", "is not valid: ")]
    public void AFileThatIsNotASettingsObject_IsRefused_NamingTheFileButNoValue(string content, string expected)
    {
        var message = FluentActions.Invoking(() => Parse(content)).Should().Throw<RoofSettingsFileException>().Which.Message;

        message.Should().StartWith($"The settings file '{Path}'").And.Contain(expected).And.NotContain("quoted-value");
    }

    [TestMethod]
    [DataRow("""{ "BlueIris": { "Password": "quoted-value" } }""", "BlueIris:Password",
        "set it through the API (it is kept in the managed secrets file) or as a file in the secrets directory.")]
    [DataRow("""{ "RoofControllerSecurity": { "ApiKeys": [ { "Name": "ops", "Role": "RoofOperator", "Key": "quoted-value" } ] } }""",
        "RoofControllerSecurity:ApiKeys:0:Key", "set it as a file in the secrets directory.")]
    [DataRow("""{ "Kestrel": { "Certificates": { "Default": { "Path": "/https/roof.pfx", "Password": "quoted-value" } } } }""",
        "Kestrel:Certificates:Default:Password", "set it as a file in the secrets directory.")]
    public void ASecret_IsRefusedInTheSettingsFile_NamingTheKeyButNeverTheValue(string content, string key, string where)
    {
        FluentActions.Invoking(() => Parse(content)).Should().Throw<RoofSettingsFileException>()
            .Which.Message.Should().StartWith($"The settings file '{Path}' sets {key}, which is a secret.")
            .And.EndWith(where)
            .And.NotContain("quoted-value");
    }

    [TestMethod]
    public void ASecret_IsRefusedInTheSettingsFile_AndOnlySecretsAreAllowedInTheSecretsFile()
    {
        const string secret = """{ "BlueIris": { "Password": "quoted-value" } }""";
        const string setting = """{ "RoofControllerUi": { "DefaultCamera": "quoted-value" } }""";

        FluentActions.Invoking(() => Parse(secret)).Should().Throw<RoofSettingsFileException>()
            .Which.Message.Should().Contain("sets BlueIris:Password, which is a secret").And.NotContain("quoted-value");
        FluentActions.Invoking(() => Parse(setting, RoofSettingsFileKind.Secrets)).Should().Throw<RoofSettingsFileException>()
            .Which.Message.Should().StartWith("The managed secrets file").And.Contain("which is not a secret setting");
        Parse(secret, RoofSettingsFileKind.Secrets).Data.Should().ContainKey("BlueIris:Password");
    }

    [TestMethod]
    [DataRow("""{ "Kestrel": { "Endpoints": { "Http": { "Url": "http://quoted-value:80" } } } }""", "Kestrel:Endpoints:Http:Url")]
    [DataRow("""{ "RoofControllerSecurity": { "Identity": { "StorePath": "/quoted-value" } } }""", "RoofControllerSecurity:Identity:StorePath")]
    [DataRow("""{ "RoofControllerSecurity": { "AllowedOrigins": [ { "Url": "quoted-value" } ] } }""", "RoofControllerSecurity:AllowedOrigins:0:Url")]
    [DataRow("""{ "Logging": { "LogLevel": { "HVO": "quoted-value" } } }""", "Logging:LogLevel:HVO")]
    [DataRow("""{ "BlueIris": "quoted-value" }""", "BlueIris")]
    public void AKeyOutsideTheCatalogue_IsRefused_NamingTheKeyButNeverTheValue(string content, string key)
        => FluentActions.Invoking(() => Parse(content)).Should().Throw<RoofSettingsFileException>()
            .Which.Message.Should().StartWith($"The settings file '{Path}' sets {key}, which is not in the settings catalogue")
            .And.EndWith("set other configuration in the deployment's environment (docs/deployment.md).")
            .And.NotContain("quoted-value");

    [TestMethod]
    public void CatalogueSettings_ListItems_AndEmptySections_AreAccepted()
    {
        var document = Parse("""
            {
              "RoofControllerSecurity": { "AllowedOrigins": [ "https://a.example", "https://b.example" ], "Identity": {} },
              "BlueIris": {},
              "RoofControllerUi": { "DefaultCamera": "Yard" }
            }
            """);

        document.Data.Should().Contain("RoofControllerSecurity:AllowedOrigins:1", "https://b.example")
            .And.Contain("RoofControllerUi:DefaultCamera", "Yard");
        Parse("""{ "RoofControllerSecurity": { "AllowedOrigins": [] } }""").Data.Should().ContainKey("RoofControllerSecurity:AllowedOrigins");
        FluentActions.Invoking(() => Parse("""{ "BlueIris": { "BaseUrl": "http://192.168.0.4:80" } }""", RoofSettingsFileKind.Secrets))
            .Should().Throw<RoofSettingsFileException>().WithMessage("*sets BlueIris:BaseUrl, which is not a secret setting.*");
    }

    [TestMethod]
    public void AMissingFile_IsEmpty()
    {
        var document = RoofSettingsFile.Read(System.IO.Path.Combine(_directory, "absent.json"), RoofSettingsFileKind.Settings);

        document.Should().BeSameAs(RoofSettingsDocument.Empty);
        document.Exists.Should().BeFalse();
        document.Hash.Should().Be(RoofSettingsDocument.AbsentHash);
        RoofSettingsFile.CurrentHash(System.IO.Path.Combine(_directory, "absent.json"), RoofSettingsFileKind.Settings)
            .Should().Be(RoofSettingsDocument.AbsentHash);
    }

    [TestMethod]
    [DataRow(false, "settings file", "appsettings.Local.json")]
    [DataRow(true, "managed secrets file", "secrets.json")]
    public void ADirectoryAtThePath_IsRefused_NeverReadAsAMissingFile(bool secrets, string name, string file)
    {
        var kind = secrets ? RoofSettingsFileKind.Secrets : RoofSettingsFileKind.Settings;
        var path = System.IO.Path.Combine(_directory, "mounted-as-a-directory");
        Directory.CreateDirectory(path);
        var expected = $"The {name} path '{path}' is a directory. It must name the file inside it, for example " +
            $"'{System.IO.Path.Combine(path, file)}'.";

        FluentActions.Invoking(() => RoofSettingsFile.Read(path, kind)).Should().Throw<RoofSettingsFileException>().WithMessage(expected);
        FluentActions.Invoking(() => RoofSettingsFile.CurrentHash(path, kind)).Should().Throw<RoofSettingsFileException>().WithMessage(expected);
        RoofSettingsFile.CheckWritable(path, kind).Should().Be(expected);
        Directory.GetFileSystemEntries(_directory).Should().Equal([path], "the check leaves no probe");
    }

    [TestMethod]
    public void AFileInADirectoryTheControllerCannotSearch_IsRefused_NeverReadAsAMissingFile()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes only.");
            return;
        }

        var locked = System.IO.Path.Combine(_directory, "locked");
        var path = System.IO.Path.Combine(locked, "appsettings.Local.json");
        Directory.CreateDirectory(locked);
        File.WriteAllText(path, """{ "RoofControllerOptionsV4": { "CloseRelayId": 2 } }""");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        if (File.Exists(path))
        {
            Assert.Inconclusive("The directory can still be searched (the tests run as root).");
        }

        var expected = $"The settings file '{path}' could not be read: UnauthorizedAccessException.";
        FluentActions.Invoking(() => RoofSettingsFile.Read(path, RoofSettingsFileKind.Settings))
            .Should().Throw<RoofSettingsFileException>().WithMessage(expected);
        FluentActions.Invoking(() => RoofSettingsFile.CurrentHash(path, RoofSettingsFileKind.Settings))
            .Should().Throw<RoofSettingsFileException>().WithMessage(expected);
    }

    [TestMethod]
    public void AWrittenFile_ReadsBackWithItsMetadata_AndTheSameHash()
    {
        var path = System.IO.Path.Combine(_directory, "appsettings.Local.json");
        var body = new JsonObject { ["RoofControllerUi"] = new JsonObject { ["DefaultCamera"] = "Roof", ["KioskScreenTimeout"] = null } };
        var saved = new DateTimeOffset(2026, 9, 1, 14, 0, 0, TimeSpan.FromHours(2));
        var bytes = RoofSettingsFile.Serialize(body, 4, saved, "ada");

        RoofSettingsFile.Write(path, RoofSettingsFileKind.Settings, bytes);

        var document = RoofSettingsFile.Read(path, RoofSettingsFileKind.Settings);
        document.Version.Should().Be(4);
        document.SavedAtUtc.Should().Be(saved.ToUniversalTime());
        document.SavedBy.Should().Be("ada");
        document.Data.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["RoofControllerUi:DefaultCamera"] = "Roof",
            ["RoofControllerUi:KioskScreenTimeout"] = null
        });
        document.Hash.Should().Be(RoofSettingsFile.CurrentHash(path, RoofSettingsFileKind.Settings));
        Encoding.UTF8.GetString(bytes).Should().StartWith("{\n  \"HvoRoofSettings\": {", "the metadata comes first, indented for hand editing");
        Directory.GetFiles(_directory).Should().Equal([path], "the temporary file is renamed over the file");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    [TestMethod]
    public void TheSecretsFile_IsWrittenOwnerOnly_InAnOwnerOnlyDirectoryItCreates()
    {
        var path = System.IO.Path.Combine(_directory, "secrets", "managed-secrets.json");
        var body = new JsonObject { ["BlueIris"] = new JsonObject { ["Password"] = "quoted-value" } };

        RoofSettingsFile.Write(path, RoofSettingsFileKind.Secrets, RoofSettingsFile.Serialize(body, 2, DateTimeOffset.UtcNow, "ada"));

        RoofSettingsFile.Read(path, RoofSettingsFileKind.Secrets).Data["BlueIris:Password"].Should().Be("quoted-value");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(System.IO.Path.GetDirectoryName(path)!)
                .Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    public void AWriteThatFails_LeavesTheFileAndNoTemporaryFile()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes only.");
            return;
        }

        var path = System.IO.Path.Combine(_directory, "appsettings.Local.json");
        RoofSettingsFile.Write(path, RoofSettingsFileKind.Settings, RoofSettingsFile.Serialize(new JsonObject(), 1, DateTimeOffset.UtcNow, null));
        var before = File.ReadAllBytes(path);
        File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        if (RoofSettingsFile.CheckWritable(path, RoofSettingsFileKind.Settings) is null)
        {
            Assert.Inconclusive("The directory is still writable (the tests run as root).");
        }

        FluentActions.Invoking(() => RoofSettingsFile.Write(path, RoofSettingsFileKind.Settings, [1, 2, 3]))
            .Should().Throw<RoofSettingsFileException>()
            .WithMessage($"The settings file '{path}' could not be written: UnauthorizedAccessException.");

        File.ReadAllBytes(path).Should().Equal(before);
        Directory.GetFiles(_directory).Should().Equal([path]);
    }

    [TestMethod]
    public void CheckWritable_NamesAMissingOrReadOnlyDirectory_AndLeavesNoProbe()
    {
        var path = System.IO.Path.Combine(_directory, "appsettings.Local.json");
        RoofSettingsFile.CheckWritable(path, RoofSettingsFileKind.Settings).Should().BeNull();
        Directory.GetFileSystemEntries(_directory).Should().BeEmpty();

        RoofSettingsFile.CheckWritable(System.IO.Path.Combine(_directory, "missing", "x.json"), RoofSettingsFileKind.Secrets)
            .Should().StartWith("The managed secrets file directory '").And.Contain("does not exist");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var problem = RoofSettingsFile.CheckWritable(path, RoofSettingsFileKind.Settings);
            if (problem is null)
            {
                Assert.Inconclusive("The directory is still writable (the tests run as root).");
            }

            problem.Should().Contain("is not writable by the controller").And.Contain("root in the controller's image");
        }
    }

    [TestMethod]
    public void TheView_ReadsTheTopLayer_CountsListSlots_AndFlattens()
    {
        var view = new RoofConfigurationView(
        [
            Layer(new()
            {
                ["RoofControllerUi:DefaultCamera"] = "Shipped",
                ["RoofControllerSecurity:AllowedOrigins:0"] = "https://a.example",
                ["RoofControllerSecurity:AllowedOrigins:1"] = "https://b.example",
                ["RoofControllerSecurity:AllowedOrigins:2"] = "https://c.example",
                ["RoofControllerSecurity:ApiKeys:99999:Name"] = "stray"
            }),
            Layer(new()
            {
                ["RoofControllerUi:DefaultCamera"] = "Mine",
                ["RoofControllerSecurity:AllowedOrigins:0"] = "https://mine.example",
                ["RoofControllerSecurity:AllowedOrigins:1"] = " "
            })
        ]);

        view.TryGet("roofcontrollerui:defaultcamera", out var camera).Should().BeTrue();
        camera.Should().Be("Mine");
        view.TryGet("RoofControllerUi:Nothing", out _).Should().BeFalse();
        view.GetListItems("RoofControllerSecurity:AllowedOrigins").Should().Equal(
            ["https://mine.example", "https://c.example"], "a blank entry is no entry, and a shorter list above leaves the rest showing");
        view.GetListItems("RoofControllerSecurity:Nothing").Should().BeNull();
        view.CountListSlots("RoofControllerSecurity:AllowedOrigins").Should().Be(3);
        view.CountListSlots("RoofControllerSecurity:ApiKeys").Should().Be(0, "an index past the limit does not count");
        view.Flatten().Should().Contain("RoofControllerUi:DefaultCamera", "Mine")
            .And.Contain("RoofControllerSecurity:AllowedOrigins:2", "https://c.example")
            .And.NotContainKey("RoofControllerUi");
    }

    [TestMethod]
    [DataRow("https://roof.example", true)]
    [DataRow("http://roof.example:8080", true)]
    [DataRow("https://roof.example/", true)]
    [DataRow("https://roof.example/kiosk", false)]
    [DataRow("https://roof.example?x=1", false)]
    [DataRow("https://user@roof.example", false)]
    [DataRow("ftp://roof.example", false)]
    [DataRow("roof.example", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void AnAllowedOrigin_IsAnHttpUrlWithNothingAfterTheHost(string? origin, bool valid)
        => OriginCheckMiddleware.IsValidAllowedOrigin(origin).Should().Be(valid);

    private static RoofSettingsDocument Parse(string content, RoofSettingsFileKind kind = RoofSettingsFileKind.Settings)
        => RoofSettingsFile.Parse(Encoding.UTF8.GetBytes(content), kind == RoofSettingsFileKind.Settings ? Path : "/managed/secrets.json", kind);

    private static IConfigurationProvider Layer(Dictionary<string, string?> values)
    {
        var provider = new MemoryConfigurationSource { InitialData = values }.Build(new ConfigurationBuilder());
        provider.Load();
        return provider;
    }
}
