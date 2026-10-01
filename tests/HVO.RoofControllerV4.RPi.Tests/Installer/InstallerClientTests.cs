using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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
/// hvo-roof and the Mac app on the person's own machine (#71): the release's program, the controller's CA saved only when
/// its fingerprint is the one given, the Mac's device key made through the controller's own API (a real one, in process),
/// the app without macOS's quarantine mark and opened once with <c>--check</c>, and the CA in the login keychain.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerClientTests
{
    private const string Admin = "ada";
    private const string Password = TestSecrets.Password;
    private const string PasswordFile = "/Users/roy/ada-password";
    private const string Credentials = "/home/roy/.config/hvo-roof/credentials.json";
    private const string Settings = "/Users/roy/Library/Application Support/HVO Roof";
    private const string App = "/Applications/HVO Roof.app";
    private const string Keychain = "/Users/roy/Library/Keychains/login.keychain-db";
    private const string KeyName = "mac-studio-roy";

    // ---- hvo-roof ---------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(InstallerOs.Linux, Architecture.X64, "/home/roy", "linux-x64", DisplayName = "Linux x64")]
    [DataRow(InstallerOs.Linux, Architecture.Arm64, "/home/roy", "linux-arm64", DisplayName = "Linux arm64")]
    [DataRow(InstallerOs.MacOS, Architecture.Arm64, "/Users/roy", "osx-arm64", DisplayName = "macOS arm64")]
    public async Task HvoRoof_IsTheReleasesProgramForThisMachine(InstallerOs os, Architecture architecture, string home, string platform)
    {
        using var machine = new FakeMachine(os, architecture, root: false, hostName: "laptop", userName: "roy");

        var run = await machine.RunAsync("--answers", machine.WriteAnswers(CliAnswers()));

        run.ExitCode.Should().Be(0, run.ToString());
        machine.Read($"{home}/.local/bin/hvo-roof").Should().Be(Encoding.UTF8.GetString(FakeMachine.CliProgram("4.0.0", platform)));
        machine.Mode($"{home}/.local/bin/hvo-roof").Should().Be(Modes.Program);
        machine.Exists($"{home}/.local/bin/hvo-roof.new").Should().BeFalse("the staged copy is moved into place");
    }

    [TestMethod]
    public async Task AnHvoRoofThere_IsReplaced_AndKeptAsPrevious()
    {
        using var laptop = Laptop().WithCli("/home/roy/.local/bin/hvo-roof");
        var session = await StartAsync(laptop, CliAnswers());

        var plan = await session.CheckAsync();
        Change(plan, "/home/roy/.local/bin/hvo-roof").Should().Be(new StepCheck(StepChange.Change, "release 4.0.0's; the one there now is kept as hvo-roof.previous"));
        await session.ApplyAsync(plan);

        laptop.Read("/home/roy/.local/bin/hvo-roof.previous").Should().Be("#!/bin/sh\n");
        laptop.Read("/home/roy/.local/bin/hvo-roof").Should().Be(Encoding.UTF8.GetString(FakeMachine.CliProgram("4.0.0", "linux-x64")));
    }

    [TestMethod]
    public async Task TheReleasesHvoRoof_WithAnotherMode_HasItsModeSet()
    {
        using var laptop = Laptop();
        laptop.Folder("/home/roy/.local/bin");
        File.WriteAllBytes(laptop.OnDisk("/home/roy/.local/bin/hvo-roof"), FakeMachine.CliProgram("4.0.0", "linux-x64"));
        File.SetUnixFileMode(laptop.OnDisk("/home/roy/.local/bin/hvo-roof"), Modes.File);
        var session = await StartAsync(laptop, CliAnswers());

        var plan = await session.CheckAsync();
        Change(plan, "/home/roy/.local/bin/hvo-roof").Should().Be(new StepCheck(StepChange.Change, "0644 → 0755"));
        await session.ApplyAsync(plan);

        laptop.Mode("/home/roy/.local/bin/hvo-roof").Should().Be(Modes.Program);
        laptop.Exists("/home/roy/.local/bin/hvo-roof.previous").Should().BeFalse("the program was not replaced");
    }

    [TestMethod]
    public async Task ASharedFolderThePersonCannotWriteTo_IsBlocked()
    {
        if (Environment.UserName == "root")
        {
            Assert.Inconclusive("root may write to any folder.");
        }

        using var laptop = Laptop();
        laptop.Folder("/usr/local/bin");
        File.SetUnixFileMode(laptop.OnDisk("/usr/local/bin"), Modes.Folder & ~UnixFileMode.UserWrite);
        try
        {
            var plan = await CheckAsync(laptop, CliAnswers() with { Cli = new CliSettings { Folder = CliSettings.SharedFolder } });

            Change(plan, "/usr/local/bin/hvo-roof").Should().Be(new StepCheck(StepChange.Blocked,
                "you cannot write to /usr/local/bin without sudo, and the installer never runs as root for your own programs: choose ~/.local/bin"));
        }
        finally
        {
            File.SetUnixFileMode(laptop.OnDisk("/usr/local/bin"), Modes.Folder);
        }
    }

    [TestMethod]
    public async Task ASharedFolderThatIsNotThere_IsBlocked()
    {
        using var laptop = Laptop();

        var plan = await CheckAsync(laptop, CliAnswers() with { Cli = new CliSettings { Folder = CliSettings.SharedFolder } });

        Change(plan, "/usr/local/bin/hvo-roof").Should().Be(new StepCheck(StepChange.Blocked, "/usr/local/bin is not there: choose ~/.local/bin"));
        plan.Steps.Should().NotContain(step => step.Step.Target == "/usr/local/bin", "the installer makes no folder outside the person's own");
    }

    [TestMethod]
    public async Task AControllerThatServesAnotherCa_IsBlocked_AndNothingChanges()
    {
        using var laptop = Laptop();
        using var other = TestCertificates.CreateAuthority("Another CA");
        laptop.FetchCa = (_, _) => Task.FromResult(X509CertificateLoader.LoadCertificate(other.RawData));
        var answers = laptop.WriteAnswers(CliAnswers());
        laptop.Folder(Path.GetDirectoryName(InstallPaths.Log(laptop.Machine))!);
        var before = laptop.Snapshot(InstallPaths.Log(laptop.Machine));

        var run = await laptop.RunAsync("--answers", answers);

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        run.Output.Should().Contain("the CA https://roofpi.local:8443/ serves (")
            .And.Contain("is not the one whose fingerprint was given: compare it with the controller's installer's Done page, or hvo-roof-install cert show on the controller");
        laptop.Snapshot(InstallPaths.Log(laptop.Machine)).Should().Equal(before, "nothing is installed");
        laptop.CaFetches.Should().ContainSingle();
    }

    [TestMethod]
    public async Task ARecordWithoutTheController_IsRefusedForAPlan_SayingHowToGiveIt()
    {
        using var laptop = Laptop();
        // An earlier release recorded hvo-roof without the controller it connects to.
        laptop.Write("/home/roy/.config/hvo-roof/install.json", (InstallerGuardTests.Record(InstallRole.Cli, null) with { Scope = InstallScope.User }).ToJson());

        var plan = await laptop.RunAsync("--plan");

        plan.ExitCode.Should().Be((int)InstallerExitCode.Refused, plan.ToString());
        plan.Error.Should().Contain("  - hvo-roof needs the controller's address")
            .And.Contain("The record of what is installed here leaves it out: plan with hvo-roof-install --plan --answers FILE, or run hvo-roof-install to be asked.")
            .And.Contain("Nothing was changed.");
    }

    [TestMethod]
    public async Task AControllerThatDoesNotAnswer_IsBlocked()
    {
        using var laptop = Laptop();
        laptop.FetchCa = (_, _) => Task.FromException<X509Certificate2>(new HttpRequestException("Connection refused (test)."));

        var plan = await CheckAsync(laptop, CliAnswers());

        Change(plan, Credentials).Should().Be(new StepCheck(StepChange.Blocked,
            "the installer could not fetch the controller's CA from https://roofpi.local:8443/: Connection refused (test)."));
    }

    [TestMethod]
    public async Task AnotherControllersConnection_IsReplaced_AndItsSignInRemoved()
    {
        using var laptop = Laptop();
        laptop.Machine.CreateDirectory("/home/roy/.config/hvo-roof", Modes.PrivateFolder);
        RoofCredentialStore.Save(laptop.OnDisk(Credentials), new RoofStoredCredentials
        {
            Controller = new Uri("https://old-roof.local:8443"),
            ApiKey = "test-old-key-not-a-real-secret"
        });
        var session = await StartAsync(laptop, CliAnswers());

        var plan = await session.CheckAsync();
        Change(plan, Credentials).Detail.Should().EndWith("; the sign-in saved for https://old-roof.local:8443/ is removed");
        await session.ApplyAsync(plan);

        var saved = RoofCredentialStore.Load(laptop.OnDisk(Credentials))!;
        saved.Controller.Should().Be(new Uri(FakeMachine.ControllerUrl));
        saved.ApiKey.Should().BeNull("a key for another controller is never sent to this one");
        saved.CaCertificate.Should().NotBeNull();
    }

    [TestMethod]
    public async Task TheSameControllersSignIn_IsKept()
    {
        using var laptop = Laptop();
        laptop.Machine.CreateDirectory("/home/roy/.config/hvo-roof", Modes.PrivateFolder);
        var session = new RoofStoredSession("test-session-token-not-a-real-secret", "roy", RoofControllerApiContract.OperatorRole, "s1", null);
        RoofCredentialStore.Save(laptop.OnDisk(Credentials), new RoofStoredCredentials { Controller = new Uri(FakeMachine.ControllerUrl), Session = session });

        var run = await laptop.RunAsync("--answers", laptop.WriteAnswers(CliAnswers()));

        run.ExitCode.Should().Be(0, run.ToString());
        var saved = RoofCredentialStore.Load(laptop.OnDisk(Credentials))!;
        saved.Session.Should().Be(session, "only how the controller is trusted changed");
        saved.CaCertificate.Should().NotBeNull();
        run.ToString().Should().NotContain(session.Token);
    }

    [TestMethod]
    public async Task ACredentialsFileThatCannotBeRead_IsBlocked()
    {
        using var laptop = Laptop();
        laptop.Machine.CreateDirectory("/home/roy/.config/hvo-roof", Modes.PrivateFolder);
        laptop.Machine.WriteAtomically(Credentials, "{ not json", Modes.PrivateFile);

        var plan = await CheckAsync(laptop, CliAnswers());

        var check = Change(plan, Credentials);
        check.Change.Should().Be(StepChange.Blocked);
        check.Detail.Should().EndWith("Correct it, or move it aside, then run the installer again");
    }

    [TestMethod]
    [DataRow("https://roofpi.local:8443", null, "https://roofpi.local:8443, trusted as this machine trusts any website", DisplayName = "A certificate of its own")]
    [DataRow("http://roofpi.local:8080", null, "http://roofpi.local:8080 over plain HTTP", DisplayName = "Plain HTTP")]
    [DataRow("https://roofpi.local:8443", "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89",
        "https://roofpi.local:8443, pinning its certificate (AB:CD:EF:01…)", DisplayName = "A self-signed certificate")]
    public async Task AControllerWithoutAPrivateCa_IsSavedWithoutOne_AndItsCaIsNotFetched(string controller, string? pin, string detail)
    {
        using var laptop = Laptop();
        var session = await StartAsync(laptop, CliAnswers() with { Client = new ClientSettings { Controller = controller, CertificateSha256 = pin } });

        var plan = await session.CheckAsync();
        Change(plan, Credentials).Should().Be(new StepCheck(StepChange.Create, detail));
        await session.ApplyAsync(plan);

        var saved = RoofCredentialStore.Load(laptop.OnDisk(Credentials))!;
        saved.CaCertificate.Should().BeNull();
        saved.CertificateSha256.Should().Be(pin);
        laptop.CaFetches.Should().BeEmpty();
    }

    // ---- the Mac app ------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheMacApp_IsInstalledWithoutQuarantine_WithItsCaKeyAndSettings_ThenChecked_AndASecondRunChangesNothing()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        using var mac = Mac(host);
        var answers = mac.WriteAnswers(MacAnswers());

        var run = await mac.RunAsync("--answers", answers, "--sign-in-password-file", PasswordFile);

        run.ExitCode.Should().Be(0, run.ToString());
        mac.Read($"{App}/Contents/MacOS/hvo-roof-mac").Should().Be(Encoding.UTF8.GetString(FakeMachine.MacProgram("4.0.0")));
        mac.Mode($"{App}/Contents/MacOS/hvo-roof-mac").Should().Be(Modes.Program);
        mac.IsQuarantined(App).Should().BeFalse();
        mac.Ran.Should().Contain(command => command.Program == "ditto" && command.Arguments.Contains("--noqtn"), "the zip is unpacked without the mark");
        Directory.GetDirectories(mac.OnDisk("/Applications")).Select(Path.GetFileName).Should().Equal(["HVO Roof.app"], "the staging folder is gone");
        mac.Mode(Settings).Should().Be(Modes.PrivateFolder);

        mac.Mode($"{Settings}/ca.crt").Should().Be(Modes.File);
        using (var authority = RoofCertificateAuthority.FromPem(mac.Read($"{Settings}/ca.crt")))
        {
            RoofCertificateAuthority.HasFingerprint(authority, FakeMachine.ControllerCaSha256).Should().BeTrue();
        }

        mac.Mode($"{Settings}/device-key").Should().Be(Modes.PrivateFile);
        var key = mac.Read($"{Settings}/device-key");
        using (var device = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(key)))
        {
            var caller = await device.Auth.GetCallerAsync();
            caller.Name.Should().Be(KeyName);
            caller.Role.Should().Be(RoofControllerApiContract.ViewerRole);
            caller.IsKiosk.Should().BeFalse("a Mac's key is an ordinary key: people sign in there with their password");
        }

        using (var admin = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin)))
        {
            (await admin.Identity.GetApiKeysAsync()).Should().ContainSingle(made => made.Name == KeyName)
                .Which.Should().Match<RoofApiKeyResponse>(made => made.Source == RoofApiKeySource.Managed && !made.Kiosk);
            (await admin.Identity.GetSessionsAsync()).Should().NotContain(open => open.Name == Admin, "the admin's session ends once the key is made");
        }

        var settings = JsonNode.Parse(mac.Read($"{Settings}/appsettings.Local.json"))!["Mac"]!;
        settings["ControllerUrl"]!.GetValue<string>().Should().Be(FakeMachine.ControllerUrl);
        settings["ServerCaCertificateFile"]!.GetValue<string>().Should().Be("ca.crt");
        settings["DeviceKeyFile"]!.GetValue<string>().Should().Be("device-key");
        settings.AsObject().ContainsKey("ServerCertificateSha256").Should().BeFalse();
        mac.Mode($"{Settings}/appsettings.Local.json").Should().Be(Modes.File);
        mac.MacChecks.Should().Equal([Settings], "the app is checked once, with the settings the installer wrote");

        run.Output.Should().Contain($"The Mac app: {App}, connected to {FakeMachine.ControllerUrl}.")
            .And.Contain($"a viewer's, {KeyName}")
            .And.Contain($"trust the controller's CA ({Settings}/ca.crt) in your login keychain")
            .And.Contain($"import the CA ({Settings}/ca.crt) in Settings → Privacy & Security → View Certificates → Authorities");
        var log = mac.Read(InstallPaths.Log(mac.Machine));
        log.Should().Contain($"Wrote {Settings}/device-key (0600): the new Viewer key {KeyName}, made by {Admin}.");
        foreach (var secret in new[] { key, Password })
        {
            run.ToString().Should().NotContain(secret);
            log.Should().NotContain(secret);
            mac.Ran.Should().NotContain(command => command.Arguments.Any(argument => argument.Contains(secret, StringComparison.Ordinal)));
            FilesHolding(mac, secret).Should().Equal([secret == key ? $"{Settings}/device-key" : PasswordFile], "a secret is only in its own file");
        }

        var before = mac.Snapshot(InstallPaths.Log(mac.Machine));
        var second = await mac.RunAsync("--answers", answers);

        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change").And.Contain($"the controller takes it ({KeyName})");
        mac.Snapshot(InstallPaths.Log(mac.Machine)).Should().Equal(before, "a second run changes nothing, and needs no password");
        mac.MacChecks.Should().HaveCount(1, "nothing changed, so the app is not checked again");
    }

    [TestMethod]
    public async Task AQuarantinedApp_HasOnlyItsMarkRemoved()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var mac = Mac(host).WithMacApp("/Applications", quarantined: true);
        var session = await StartAsync(mac, MacAnswers());

        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);

        var plan = await session.CheckAsync();
        Change(plan, App).Should().Be(new StepCheck(StepChange.Change, "macOS's quarantine mark removed, so the app opens"));
        session.Secrets.Set(InstallSecret.SignInPassword, Password);
        await session.ApplyAsync(plan);

        mac.IsQuarantined(App).Should().BeFalse();
        mac.Ran.Should().NotContain(command => command.Program == "ditto", "the app there is the release's");
        mac.Exists($"{Settings}/HVO Roof.app.previous").Should().BeFalse();
    }

    [TestMethod]
    public async Task AMarkThatStays_StopsTheInstall_WithWhatToRun()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var mac = Mac(host).WithMacApp("/Applications", quarantined: true);
        mac.QuarantineSticks = true;

        var run = await mac.RunAsync("--answers", mac.WriteAnswers(MacAnswers()), "--sign-in-password-file", PasswordFile);

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain($"macOS's quarantine mark is still on {App}: remove it (xattr -dr com.apple.quarantine \"{App}\"), then run the installer again.");
        mac.MacChecks.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ADownloadThatIsNotTheReleases_IsNeverPutInPlace()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        using var mac = Mac(host);
        mac.FileDownloads[ReleaseManifest.DownloadUri("4.0.0", FakeMachine.MacAssetName("4.0.0")).ToString()] =
            FakeMachine.MacAppZip("4.0.0", Encoding.UTF8.GetBytes("#!/bin/sh\necho 'not the release'\n"));

        var run = await mac.RunAsync("--answers", mac.WriteAnswers(MacAnswers()), "--sign-in-password-file", PasswordFile);

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain("could not download HVO-Roof-4.0.0.zip", "a file of another size is refused as it comes");
        mac.Exists(App).Should().BeFalse();
        Directory.GetFileSystemEntries(mac.OnDisk("/Applications")).Should().BeEmpty("nothing is left behind");
    }

    [TestMethod]
    public async Task AnOlderApp_IsReplaced_AndKeptOutOfTheWay()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var mac = Mac(host).WithMacApp("/Applications", version: "3.9.0");
        var session = await StartAsync(mac, MacAnswers());

        var plan = await session.CheckAsync();
        Change(plan, App).Should().Be(new StepCheck(StepChange.Change, $"release 4.0.0's; the one there now is kept as {Settings}/HVO Roof.app.previous"));
        session.Secrets.Set(InstallSecret.SignInPassword, Password);
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        await session.ApplyAsync(plan);

        mac.Read($"{App}/Contents/MacOS/hvo-roof-mac").Should().Be(Encoding.UTF8.GetString(FakeMachine.MacProgram("4.0.0")));
        mac.Read($"{Settings}/HVO Roof.app.previous/Contents/MacOS/hvo-roof-mac").Should().Be(Encoding.UTF8.GetString(FakeMachine.MacProgram("3.9.0")));
    }

    [TestMethod]
    public async Task ADeviceKeyTheControllerTakes_IsKept_AndNoPasswordIsNeeded()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var mac = Mac(host);
        mac.Machine.CreateDirectory(Settings, Modes.PrivateFolder);
        mac.Machine.WriteAtomically($"{Settings}/device-key", TestApiKeys.Viewer + "\n", Modes.PrivateFile);
        var session = await StartAsync(mac, MacAnswers());

        var plan = await session.CheckAsync();

        Change(plan, $"{Settings}/device-key").Change.Should().Be(StepChange.Unchanged);
        session.MissingSecrets(plan).Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("test-refused-key-not-a-real-secret-00", "the controller refuses the key there", DisplayName = "Refused")]
    [DataRow(RoofClientApiTests.KioskKey, "the key there is a kiosk key, and a Mac's is a Viewer's", DisplayName = "A kiosk key")]
    [DataRow(TestApiKeys.Operator, "the key there is a " + RoofControllerApiContract.OperatorRole + " key, and a Mac's is a Viewer's", DisplayName = "An operator's key")]
    [DataRow("two\nlines", "the file there does not hold one key on one line", DisplayName = "Not a key")]
    public async Task ADeviceKeyThatIsNotAMacs_IsReplaced(string there, string why)
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        using var mac = Mac(host);
        mac.Machine.CreateDirectory(Settings, Modes.PrivateFolder);
        mac.Machine.WriteAtomically($"{Settings}/device-key", there, Modes.PrivateFile);
        var session = await StartAsync(mac, MacAnswers());

        var plan = await session.CheckAsync();
        var check = Change(plan, $"{Settings}/device-key");
        check.Change.Should().Be(StepChange.Change);
        check.Detail.Should().EndWith(": " + why);
        session.MissingSecrets(plan).Should().Equal(InstallSecret.SignInPassword);
        session.Secrets.Set(InstallSecret.SignInPassword, Password);
        await session.ApplyAsync(plan);

        var key = mac.Read($"{Settings}/device-key");
        key.Should().NotBe(there);
        using var device = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(key));
        (await device.Auth.GetCallerAsync()).Name.Should().Be(KeyName);
    }

    [TestMethod]
    public async Task AMacThatHadAKey_GetsANewSecretForIt()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        string old;
        using (var admin = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin)))
        {
            old = (await admin.Identity.AddApiKeyAsync(new RoofApiKeyCreateRequest { Name = KeyName, Role = RoofControllerApiContract.ViewerRole })).Secret;
        }

        using var mac = Mac(host);
        var run = await mac.RunAsync("--answers", mac.WriteAnswers(MacAnswers()), "--sign-in-password-file", PasswordFile);

        run.ExitCode.Should().Be(0, run.ToString());
        mac.Read(InstallPaths.Log(mac.Machine)).Should().Contain($"a new secret for Viewer key {KeyName}");
        using var stale = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(old));
        (await RoofClientApiTests.RefusedAsync(() => stale.Auth.GetCallerAsync())).StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized, "the old secret is gone");
    }

    [TestMethod]
    public async Task AKeyOfTheMacsNameThatIsNotAMacs_IsLeftAlone()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        using (var admin = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin)))
        {
            await admin.Identity.AddApiKeyAsync(new RoofApiKeyCreateRequest { Name = KeyName, Role = RoofControllerApiContract.OperatorRole });
        }

        using var mac = Mac(host);
        var run = await mac.RunAsync("--answers", mac.WriteAnswers(MacAnswers()), "--sign-in-password-file", PasswordFile);

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain($"The controller has a key named {KeyName} that is not a Mac's");
        mac.Exists($"{Settings}/device-key").Should().BeFalse();
    }

    [TestMethod]
    [DataRow(Admin, "not the password", "The controller refused ada's name or password, so the Mac's device key was not made.", DisplayName = "A wrong password")]
    [DataRow("vic", Password, "vic may not add API keys, so the Mac's device key was not made: give an admin's name", DisplayName = "Not an admin")]
    public async Task AnAdminWhoCannotMakeTheKey_StopsTheInstall_AndNoKeyIsWritten(string name, string password, string expected)
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        await RoofClientApiTests.AddUserAsync(host, "vic", RoofControllerApiContract.ViewerRole);
        using var mac = Mac(host, password);

        var run = await mac.RunAsync("--answers", mac.WriteAnswers(MacAnswers(name)), "--sign-in-password-file", PasswordFile);

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain(expected);
        mac.Exists($"{Settings}/device-key").Should().BeFalse();
        mac.MacChecks.Should().BeEmpty();
        using var admin = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        (await admin.Identity.GetSessionsAsync()).Should().BeEmpty("a session the installer opened is ended");
    }

    [TestMethod]
    public async Task OverSsh_TheAppIsNotOpened_AndThePersonIsTold()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        using var mac = Mac(host);
        mac.MacSession = "Background";

        var run = await mac.RunAsync("--answers", mac.WriteAnswers(MacAnswers()), "--sign-in-password-file", PasswordFile);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("not run: this is not the Mac's own screen (over SSH, say): open HVO Roof to check it");
        mac.MacChecks.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(78, "The Mac app did not start with its settings", DisplayName = "Its settings")]
    [DataRow(1, "The Mac app did not open its window", DisplayName = "Its window")]
    public async Task ACheckThatFails_StopsTheInstall(int exitCode, string expected)
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        using var mac = Mac(host);
        mac.MacCheckExitCode = exitCode;

        var run = await mac.RunAsync("--answers", mac.WriteAnswers(MacAnswers()), "--sign-in-password-file", PasswordFile);

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain(expected);
        mac.MacChecks.Should().HaveCount(1);

        // Run again with the app, its key and settings all in place: it is checked again until a check passes.
        var again = await mac.RunAsync("--answers", mac.WriteAnswers(MacAnswers()), "--sign-in-password-file", PasswordFile);

        again.ExitCode.Should().Be((int)InstallerExitCode.Failed, again.ToString());
        mac.MacChecks.Should().HaveCount(2);
        mac.MacCheckExitCode = 0;
        var fixedRun = await mac.RunAsync("--answers", mac.WriteAnswers(MacAnswers()), "--sign-in-password-file", PasswordFile);
        fixedRun.ExitCode.Should().Be((int)InstallerExitCode.Success, fixedRun.ToString());
        mac.MacChecks.Should().HaveCount(3);
        var last = await mac.RunAsync("--answers", mac.WriteAnswers(MacAnswers()));
        last.ExitCode.Should().Be((int)InstallerExitCode.Success, last.ToString());
        last.Output.Should().Contain("Nothing to change");
        mac.MacChecks.Should().HaveCount(3, "the record shows it checked");
    }

    [TestMethod]
    public async Task SettingsAPersonChanged_AreKept()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var mac = Mac(host);
        mac.Machine.CreateDirectory(Settings, Modes.PrivateFolder);
        mac.Machine.WriteAtomically($"{Settings}/appsettings.Local.json", """
            {
              // The person's own.
              "Logging": { "LogLevel": { "Default": "Debug" } },
              "Mac": {
                "ControllerUrl": "https://ROOFPI.local:8443/",
                "ServerCertificateSha256": "AB:CD",
                "StopTimeoutSeconds": 20,
              },
            }
            """, Modes.File);
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        var session = await StartAsync(mac, MacAnswers());

        var plan = await session.CheckAsync();
        Change(plan, $"{Settings}/appsettings.Local.json").Change.Should().Be(StepChange.Change);
        session.Secrets.Set(InstallSecret.SignInPassword, Password);
        await session.ApplyAsync(plan);

        var settings = JsonNode.Parse(mac.Read($"{Settings}/appsettings.Local.json"))!;
        settings["Logging"]!["LogLevel"]!["Default"]!.GetValue<string>().Should().Be("Debug");
        settings["Mac"]!["ControllerUrl"]!.GetValue<string>().Should().Be("https://ROOFPI.local:8443/", "the same address, as the person wrote it");
        settings["Mac"]!["StopTimeoutSeconds"]!.GetValue<int>().Should().Be(20);
        settings["Mac"]!.AsObject().TryGetPropertyValue("ServerCertificateSha256", out var pin).Should().BeTrue();
        pin.Should().BeNull("a pin is not used with a CA");
    }

    [TestMethod]
    public async Task SettingsThatAreNotTheMacApps_AreBlocked()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var mac = Mac(host);
        mac.Machine.CreateDirectory(Settings, Modes.PrivateFolder);
        mac.Machine.WriteAtomically($"{Settings}/appsettings.Local.json", """{ "Mac": "roofpi" }""", Modes.File);

        var plan = await CheckAsync(mac, MacAnswers());

        Change(plan, $"{Settings}/appsettings.Local.json").Should().Be(new StepCheck(StepChange.Blocked,
            "it is not settings the Mac app reads: correct it, or move it aside, then run the installer again"));
    }

    // ---- the keychain -----------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task TheCa_IsTrustedInTheLoginKeychain_OnTheMacsOwnScreen()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");
        var answers = mac.WriteAnswers(CliAnswers() with { Client = FakeMachine.ClientAnswers with { TrustInKeychain = true } });

        var run = await mac.RunAsync("--answers", answers);

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("macOS asks for your password to trust the controller's CA…")
            .And.Contain("Safari and Chrome trust the controller's CA: it is in your login keychain.");
        mac.KeychainTrusted.Should().ContainKey(FakeMachine.ControllerCaSha256.Replace(":", string.Empty, StringComparison.Ordinal))
            .WhoseValue.Should().Be(FakeMachine.ControllerCaName);
        mac.Ran.Should().ContainSingle(command => command.Program == "security" && command.Arguments[0] == "add-trusted-cert")
            .Which.Arguments.Should().Contain(Keychain);

        var second = await mac.RunAsync("--answers", answers);
        second.Output.Should().Contain("Nothing to change");
        mac.Ran.Count(command => command.Program == "security" && command.Arguments[0] == "add-trusted-cert").Should().Be(1);
    }

    [TestMethod]
    public async Task OverSsh_TheKeychainIsBlocked()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");
        mac.MacSession = "Background";

        var plan = await CheckAsync(mac, CliAnswers() with { Client = FakeMachine.ClientAnswers with { TrustInKeychain = true } });

        Change(plan, Keychain).Should().Be(new StepCheck(StepChange.Blocked,
            "macOS asks for your password to trust a CA, on the Mac's own screen, and this is not it (over SSH, say): run the installer there, or leave the keychain out"));
    }

    [TestMethod]
    public async Task AKeychainThatRefuses_StopsTheInstall()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");
        mac.KeychainRefusal = "SecTrustSettingsSetTrustSettings: The authorization was canceled by the user.";

        var run = await mac.RunAsync("--answers", mac.WriteAnswers(CliAnswers() with { Client = FakeMachine.ClientAnswers with { TrustInKeychain = true } }));

        run.ExitCode.Should().Be((int)InstallerExitCode.Failed, run.ToString());
        run.Error.Should().Contain("Could not trust the controller's CA in your login keychain").And.Contain("The authorization was canceled by the user.");
    }

    [TestMethod]
    public async Task WithoutTheKeychain_ThePersonIsToldHowToTrustTheCa()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");

        var run = await mac.RunAsync("--answers", mac.WriteAnswers(CliAnswers()));

        run.ExitCode.Should().Be(0, run.ToString());
        run.Output.Should().Contain("For the web UI in Safari and Chrome, trust the controller's CA (https://roofpi.local:8443/ca.crt) in your login keychain");
        mac.Ran.Should().NotContain(command => command.Program == "security");
    }

    [TestMethod]
    public async Task TheCaTrustedInTheKeychain_IsFoundByItsFingerprint_WithTheControllerOutOfReach()
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");
        var answers = mac.WriteAnswers(CliAnswers() with { Client = FakeMachine.ClientAnswers with { TrustInKeychain = true } });
        (await mac.RunAsync("--answers", answers)).ExitCode.Should().Be(0);
        var fetches = mac.CaFetches.Count;

        // Away from the observatory: nothing to change, and the controller is not asked.
        mac.FetchCa = (_, _) => throw new HttpRequestException("Name or service not known (roofpi.local:8443)");
        var second = await mac.RunAsync("--answers", answers);

        second.ExitCode.Should().Be(0, second.ToString());
        second.Output.Should().Contain("Nothing to change");
        mac.CaFetches.Count.Should().Be(fetches);
    }

    [TestMethod]
    [DataRow(null, DisplayName = "In the keychain, not trusted")]
    [DataRow(3, DisplayName = "Denied there")]
    public async Task ACaOfTheSameName_TrustedThere_IsNotTakenForTheControllers(int? trust)
    {
        using var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy");
        using var authority = RoofCertificateAuthority.FromPem(FakeMachine.ControllerCaPem);
        var sha1 = Convert.ToHexString(authority.GetCertHash(HashAlgorithmName.SHA1));

        // Another CA made the same day on the controller's host: the same name, trusted.
        mac.KeychainCertificates.Add((new string('A', 64), new string('B', 40), FakeMachine.ControllerCaName));
        mac.KeychainTrust[new string('B', 40)] = 1;
        mac.KeychainCertificates.Add((Convert.ToHexString(authority.GetCertHash(HashAlgorithmName.SHA256)), sha1, FakeMachine.ControllerCaName));
        if (trust is { } result)
        {
            mac.KeychainTrust[sha1] = result;
        }

        var plan = await CheckAsync(mac, CliAnswers() with { Client = FakeMachine.ClientAnswers with { TrustInKeychain = true } });

        Change(plan, Keychain).Should().Be(new StepCheck(StepChange.Change, $"{FakeMachine.ControllerCaName}, trusted for websites: macOS asks for your password"));
    }

    [TestMethod]
    public void TheKeychainsCertificates_AreReadByBothTheirHashes()
    {
        const string Output = """
            SHA-256 hash: 9A114025197C5BB95D94E63D55CD43790847B646B23CDF11ADA4A00EFFC15E8F
            SHA-1 hash: D69B561148F01C77C54578C10926DF5B856976AD
            keychain: "/Users/roy/Library/Keychains/login.keychain-db"
            version: 512
            class: 0x80001000
            attributes:
                "alis"<blob>="GlobalSign"
                "hpky"<blob>=0x8FF04FBE2C7DCB4D4A5B07D1E9A7AA0E6C8E0A0B  "\217\360O\276"
            SHA-1 hash: 0123456789ABCDEF0123456789ABCDEF01234567
            keychain: "/Users/roy/Library/Keychains/login.keychain-db"
            SHA-256 hash: fb268be2342052affbd8051487ebb5d64ad86d4e65bc3526c7d231112ad1c076
            SHA-1 hash: 1111111111111111111111111111111111111111
            keychain: "/Users/roy/Library/Keychains/login.keychain-db"
            """;

        KeychainTrustStep.KeychainHashes(Output).Should().Equal(
            ("9A114025197C5BB95D94E63D55CD43790847B646B23CDF11ADA4A00EFFC15E8F", "D69B561148F01C77C54578C10926DF5B856976AD"),
            ("FB268BE2342052AFFBD8051487EBB5D64AD86D4E65BC3526C7D231112AD1C076", "1111111111111111111111111111111111111111"));
    }

    [TestMethod]
    public void TrustForWebsites_IsReadFromTheTrustSettings()
    {
        static string Entry(string sha1, string usages) => $"<key>{sha1}</key><dict><key>trustSettings</key><array>{usages}</array></dict>";
        static string Usage(string? name, int? result) => "<dict>"
            + (name is null ? string.Empty : $"<key>kSecTrustSettingsPolicyName</key><string>{name}</string>")
            + (result is null ? string.Empty : $"<key>kSecTrustSettingsResult</key><integer>{result}</integer>")
            + "</dict>";
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>trustList</key><dict>
            {Entry("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", string.Empty)}
            {Entry("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Usage("sslServer", 1))}
            {Entry("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", Usage("sslServer", 3))}
            {Entry("DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD", Usage("smime", 1))}
            {Entry("EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE", Usage(null, null))}
            {Entry("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", Usage(null, 1) + Usage("sslServer", 3))}
            {Entry("1111111111111111111111111111111111111111", Usage("sslServer", 4))}
            {Entry("2222222222222222222222222222222222222222", "<dict><key>kSecTrustSettingsPolicy</key><data>KoZIhvdjZAED</data><key>kSecTrustSettingsResult</key><integer>2</integer></dict>")}
            </dict><key>trustVersion</key><integer>1</integer></dict></plist>
            """;

        KeychainTrustStep.TrustedForWebsites(plist).Should().BeEquivalentTo(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE",
            "2222222222222222222222222222222222222222");
        KeychainTrustStep.TrustedForWebsites("""<?xml version="1.0"?><plist version="1.0"><dict><key>trustVersion</key><integer>1</integer></dict></plist>""").Should().BeEmpty();
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static FakeMachine Laptop() => new(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy");

    // A Mac with its Applications folder, whose API calls reach host, and the admin's password in a file only they read.
    private static FakeMachine Mac(RoofApiTestHost host, string password = Password)
    {
        var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy")
        {
            ApiHandler = () => ClientTestSupport.CreateHandler(() => host.Server)
        };
        mac.Folder("/Applications");
        mac.Machine.WriteAtomically(PasswordFile, password, Modes.PrivateFile);
        return mac;
    }

    private static InstallAnswers CliAnswers() => new() { Roles = [InstallRole.Cli], Client = FakeMachine.ClientAnswers };

    private static InstallAnswers MacAnswers(string admin = Admin)
        => new() { Roles = [InstallRole.MacApp], MacApp = new MacAppSettings { Admin = admin }, Client = FakeMachine.ClientAnswers };

    private static async Task<InstallerSession> StartAsync(FakeMachine machine, InstallAnswers answers)
    {
        var session = await InstallerSession.StartAsync(machine.Machine, InstallLog.None, "4.0.0+0123456789abcdef", FakeMachine.Clock);
        session.Answers = answers.Normalised();
        return session;
    }

    private static async Task<CheckedPlan> CheckAsync(FakeMachine machine, InstallAnswers answers) => await (await StartAsync(machine, answers)).CheckAsync();

    // The files on the machine that hold the secret, by their path there.
    private static string[] FilesHolding(FakeMachine machine, string secret)
        => [.. Directory.EnumerateFiles(machine.OnDisk("/"), "*", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains(secret, StringComparison.Ordinal))
            .Select(file => "/" + Path.GetRelativePath(machine.OnDisk("/"), file))];

    private static StepCheck Change(CheckedPlan plan, string target) => plan.Steps.Single(step => step.Step.Target == target).Check;
}
