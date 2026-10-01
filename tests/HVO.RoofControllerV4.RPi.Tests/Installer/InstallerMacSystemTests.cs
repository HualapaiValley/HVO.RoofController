using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The Mac app's install (#71) on a real Mac, as the person: hvo-roof-install with <c>--answers</c> and <c>--release</c>
/// puts the release's <c>HVO Roof.app</c>, downloaded with macOS's quarantine mark, in <c>~/Applications</c> without it
/// (the real ditto and xattr), still signed; writes the controller's CA, the device key (0600) and valid settings; and
/// the app opens its window with them (<c>--check</c>). A second run changes nothing. CI's installer-mac job runs it on a
/// macOS runner, with <c>HVO_MAC_INSTALL=1</c> and <c>HVO_MAC_APP_ZIP</c>, the app zipped as the release has it; anywhere
/// else it is skipped. The home is the test's own, so the person's apps and settings are untouched. The controller is
/// its API in process (the device key is made through it), and its CA the one the fake machine's tests serve: the app
/// never reaches it, and <c>--check</c> needs it not to.
/// </summary>
[TestClass]
[TestCategory("MacInstall")]
[SupportedOSPlatform("macos")]
public sealed class InstallerMacSystemTests
{
    private const string Version = "4.0.0";
    private const string Admin = "ada";

    // What macOS gives a file a browser downloaded (flags;time;agent;id): Gatekeeper stops an app that carries it.
    private const string Downloaded = "0083;66fb6a00;Safari;";

    [TestMethod]
    public async Task TheMacApp_IsInstalledWithoutQuarantine_WithItsKeyAndSettings_AndOpens_AndASecondRunChangesNothing()
    {
        var zip = Environment.GetEnvironmentVariable("HVO_MAC_APP_ZIP");
        if (Environment.GetEnvironmentVariable("HVO_MAC_INSTALL") != "1" || string.IsNullOrEmpty(zip))
        {
            Assert.Inconclusive("Installs the Mac app on this Mac: set HVO_MAC_INSTALL=1 and HVO_MAC_APP_ZIP, as CI's installer-mac job does.");
        }

        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        var work = Directory.CreateTempSubdirectory("hvo-mac-install-");
        try
        {
            var home = Path.Join(work.FullName, "home");
            Directory.CreateDirectory(home);
            var machine = Mac(home, () => ClientTestSupport.CreateHandler(() => host.Server));
            var release = MakeRelease(work.FullName, zip!);
            var answers = Path.Join(work.FullName, "mac.json");
            File.WriteAllText(answers, new InstallAnswers
            {
                Roles = [InstallRole.MacApp],
                MacApp = new MacAppSettings { Folder = MacAppSettings.HomeFolder, Admin = Admin },
                Client = FakeMachine.ClientAnswers
            }.ToJson());
            var passwordFile = Path.Join(work.FullName, "ada-password");
            File.WriteAllText(passwordFile, TestSecrets.Password + "\n");
            File.SetUnixFileMode(passwordFile, Modes.PrivateFile);

            var run = await RunAsync(machine, "--answers", answers, "--release", release, "--sign-in-password-file", passwordFile);

            run.Exit.Should().Be(0, run.Output);
            var app = Path.Join(home, "Applications", MachineSurveyor.MacAppBundle);
            var settings = ClientSteps.MacSettingsFolder(machine);
            Run("xattr", "-r", app).Output.Should().NotContain(ClientSteps.QuarantineAttribute, "the app is put in place without macOS's quarantine mark");
            var signature = Run("codesign", "--verify", "--deep", "--strict", app);
            signature.Exit.Should().Be(0, $"the app is still signed as the release built it: {signature.Output}");
            File.GetUnixFileMode(ClientSteps.MacProgram(app)).Should().Be(Modes.Program);
            Directory.GetFileSystemEntries(Path.Join(home, "Applications")).Select(Path.GetFileName).Should().Equal([MachineSurveyor.MacAppBundle], "the staging folder is gone");

            File.GetUnixFileMode(settings).Should().Be(Modes.PrivateFolder);
            File.GetUnixFileMode(Path.Join(settings, ClientSteps.MacDeviceKeyFile)).Should().Be(Modes.PrivateFile);
            File.GetUnixFileMode(Path.Join(settings, ClientSteps.MacCaFile)).Should().Be(Modes.File);
            using (var authority = RoofCertificateAuthority.FromPem(File.ReadAllText(Path.Join(settings, ClientSteps.MacCaFile))))
            {
                RoofCertificateAuthority.HasFingerprint(authority, FakeMachine.ControllerCaSha256).Should().BeTrue();
            }

            var mac = JsonNode.Parse(File.ReadAllText(Path.Join(settings, ClientSteps.MacSettingsFile)))!["Mac"]!;
            mac["ControllerUrl"]!.GetValue<string>().Should().Be(FakeMachine.ControllerUrl);
            mac["ServerCaCertificateFile"]!.GetValue<string>().Should().Be(ClientSteps.MacCaFile);
            mac["DeviceKeyFile"]!.GetValue<string>().Should().Be(ClientSteps.MacDeviceKeyFile);

            var key = File.ReadAllText(Path.Join(settings, ClientSteps.MacDeviceKeyFile)).Trim();
            using (var device = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(key)))
            {
                var caller = await device.Auth.GetCallerAsync();
                caller.Name.Should().StartWith("mac-");
                caller.Role.Should().Be(RoofControllerApiContract.ViewerRole);
                caller.IsKiosk.Should().BeFalse();
            }

            // The installer opens the app itself on the Mac's own screen; a runner's session may not be that, so the
            // test opens it too, with the settings the installer wrote, whatever the installer could do.
            var check = Run(ClientSteps.MacProgram(app), [("HVO_ROOF_MAC_SETTINGS", settings)], "--check");
            check.Exit.Should().Be(0, check.Output);
            check.Output.Should().Contain("HVO Roof check: the window opened and was drawn");
            var log = File.ReadAllText(InstallPaths.Log(machine));
            if (Run("launchctl", "managername").Output.Trim() == "Aqua")
            {
                log.Should().Contain($"Checked {MachineSurveyor.MacAppBundle}: it opened its window with its settings.");
            }

            foreach (var secret in new[] { key, TestSecrets.Password })
            {
                run.Output.Should().NotContain(secret);
                log.Should().NotContain(secret);
            }

            var second = await RunAsync(machine, "--answers", answers, "--release", release);
            second.Exit.Should().Be(0, second.Output);
            second.Output.Should().Contain("Nothing to change");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The keychain step reads what macOS's own tools print: the controller's CA in a keychain of the test's own, found by
    /// its SHA-256 with its SHA-1 beside it; then trusted for SSL the way <c>add-trusted-cert</c> trusts it in the person's
    /// settings, but written to a file of the test's own (<c>-o</c>), the same form <c>trust-settings-export</c> gives, so
    /// the Mac's trust settings are untouched and no authorization dialog waits for someone.
    /// </summary>
    [TestMethod]
    public void TheKeychainsListingAndTrustSettings_AreReadAsMacOsWritesThem()
    {
        if (Environment.GetEnvironmentVariable("HVO_MAC_INSTALL") != "1")
        {
            Assert.Inconclusive("Reads a keychain and trust settings on this Mac: set HVO_MAC_INSTALL=1, as CI's installer-mac job does.");
        }

        var work = Directory.CreateTempSubdirectory("hvo-mac-keychain-");
        var keychain = Path.Join(work.FullName, "test.keychain-db");
        var ca = Path.Join(work.FullName, "ca.crt");
        File.WriteAllText(ca, FakeMachine.ControllerCaPem);
        using var authority = RoofCertificateAuthority.FromPem(FakeMachine.ControllerCaPem);
        var sha256 = Convert.ToHexString(authority.GetCertHash(HashAlgorithmName.SHA256));
        var sha1 = Convert.ToHexString(authority.GetCertHash(HashAlgorithmName.SHA1));
        try
        {
            Run("security", "create-keychain", "-p", Guid.NewGuid().ToString("N"), keychain).Exit.Should().Be(0);
            var added = Run("security", "add-certificates", "-k", keychain, ca);
            added.Exit.Should().Be(0, added.Output);

            var found = Run("security", "find-certificate", "-a", "-Z", keychain);
            found.Exit.Should().Be(0, found.Output);
            KeychainTrustStep.KeychainHashes(found.Output).Should().Equal([(sha256, sha1)], found.Output);

            var exported = Path.Join(work.FullName, "trust-settings.plist");
            var trust = Run("security", "add-trusted-cert", "-r", "trustRoot", "-p", "ssl", "-k", keychain, "-o", exported, ca);
            trust.Exit.Should().Be(0, trust.Output);
            var xml = Run("plutil", "-convert", "xml1", "-o", "-", exported);
            xml.Exit.Should().Be(0, xml.Output);
            KeychainTrustStep.TrustedForWebsites(xml.Output).Should().Contain(sha1, xml.Output);
        }
        finally
        {
            Run("security", "delete-keychain", keychain);
            work.Delete(recursive: true);
        }
    }

    // This Mac as the person, but with home as their home, and the controller's API and CA the test's.
    private static InstallerMachine Mac(string home, Func<HttpMessageHandler> api)
    {
        var current = InstallerMachine.Current();
        current.Os.Should().Be(InstallerOs.MacOS);
        current.IsRoot.Should().BeFalse("the Mac app is the person's: the installer runs as them");
        return new InstallerMachine
        {
            Root = current.Root,
            Commands = current.Commands,
            Os = current.Os,
            Architecture = current.Architecture,
            IsRoot = false,
            UserName = current.UserName,
            Home = home,
            HostName = current.HostName,
            CurrentDirectory = home,
            // The person's own settings folder and XDG folders are not this test's.
            Environment = name => name is ClientSteps.MacSettingsVariable or "XDG_CONFIG_HOME" or "XDG_STATE_HOME" ? null : Environment.GetEnvironmentVariable(name),
            FetchCaAsync = (_, _) => Task.FromResult(RoofCertificateAuthority.FromPem(FakeMachine.ControllerCaPem)),
            ApiHandler = api
        };
    }

    // The release as a folder, as --release takes one: release.json, and the Mac app's zip, marked as downloaded.
    private static string MakeRelease(string work, string zip)
    {
        var folder = Path.Join(work, "release");
        Directory.CreateDirectory(folder);
        var name = FakeMachine.MacAssetName(Version);
        var path = Path.Join(folder, name);
        File.Copy(zip, path);
        Run("xattr", "-w", ClientSteps.QuarantineAttribute, Downloaded, path).Exit.Should().Be(0);
        Run("xattr", "-p", ClientSteps.QuarantineAttribute, path).Output.Should().StartWith("0083;", "the zip is marked as a browser marks a download");

        byte[] program;
        using (var archive = ZipFile.OpenRead(path))
        {
            var entry = archive.GetEntry($"{MachineSurveyor.MacAppBundle}/Contents/MacOS/{ClientSteps.MacProgramName}");
            entry.Should().NotBeNull($"{zip} holds {MachineSurveyor.MacAppBundle} at its top, as the release zips it");
            using var stream = entry!.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            program = copy.ToArray();
        }

        var bytes = File.ReadAllBytes(path);
        var json = JsonNode.Parse(FakeMachine.ReleaseJson(Version))!;
        json["assets"] = new JsonArray(new JsonObject
        {
            ["name"] = name,
            ["kind"] = ClientSteps.MacAssetKind,
            ["platform"] = ClientSteps.MacAssetPlatform,
            ["size"] = bytes.LongLength,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ["files"] = new JsonObject { [ClientSteps.MacProgramName] = Convert.ToHexStringLower(SHA256.HashData(program)) }
        });
        File.WriteAllText(Path.Join(folder, ReleaseManifest.FileName), json.ToJsonString());
        return folder;
    }

    private static async Task<(int Exit, string Output)> RunAsync(InstallerMachine machine, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new InstallerHost
        {
            Out = output,
            Error = error,
            Machine = machine,
            Version = $"{Version}+0123456789abcdef0123456789abcdef01234567"
        };
        var exit = await HVO.RoofControllerV4.Installer.Installer.RunAsync(args, host, CancellationToken.None);
        return (exit, $"{output}{error}");
    }

    private static (int Exit, string Output) Run(string command, params string[] arguments) => Run(command, [], arguments);

    private static (int Exit, string Output) Run(string command, (string Name, string Value)[] environment, params string[] arguments)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        // A tool that waits for someone (an authorization dialog) fails the test instead of the job's timeout.
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            Assert.Fail($"{command} {string.Join(' ', arguments)} did not finish in 2 minutes.");
        }

        return (process.ExitCode, output.Result + error.Result);
    }
}
