using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The controller's API keys (#69): the installer's operator and admin keys, the web UI's Stop key and the kiosk's, each
/// reused when the secrets folder already has one that serves, or made at random straight into files only root reads, and
/// never shown, logged or saved anywhere else.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerKeyTests
{
    private const string Secrets = "/etc/hvo-roof/secrets";

    private static readonly ApiKeyUse[] ControllerUses = [ApiKeyUse.Operator, ApiKeyUse.Admin, ApiKeyUse.WebStop];

    [TestMethod]
    public async Task NewKeys_AreWrittenOnlyForRoot_OneFilePerSetting_ThenASecondRunChangesNothing()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        var session = await InstallerPlanTests.StartAsync(pi, InstallRole.Controller);

        var plan = await session.CheckAsync();
        Change(plan, Key(0)).Should().Be(StepCheck.Unchanged("reuses roof-operator (RoofOperator)"), "the deploy script's operator key serves");
        Change(plan, Key(1)).Should().Be(new StepCheck(StepChange.Create, "installer-admin (RoofAdmin): a new random key, never shown"));
        Change(plan, Key(2)).Should().Be(new StepCheck(StepChange.Create, "web-ui-stop (RoofViewer): a new random key, never shown"));
        await session.ApplyAsync(plan);

        pi.Read(Setting(1, "Name")).Should().Be("installer-admin");
        pi.Read(Setting(1, "Role")).Should().Be(RoofControllerApiContract.AdminRole);
        pi.Read(Setting(2, "Name")).Should().Be("web-ui-stop");
        pi.Read(Setting(2, "Role")).Should().Be(RoofControllerApiContract.ViewerRole);
        pi.Exists(Setting(1, "Local")).Should().BeFalse("adding a person needs no local credential");
        pi.Exists(Setting(1, "Kiosk")).Should().BeFalse();
        foreach (var index in new[] { 1, 2 })
        {
            var key = pi.Read(Key(index));
            key.Should().MatchRegex("^[A-Za-z0-9_-]{43}$", "32 random bytes, base64url, with no newline");
            key.Length.Should().BeGreaterThanOrEqualTo(24, "the controller ignores a shorter key");
            foreach (var field in new[] { "Name", "Role", "Key" })
            {
                pi.Mode(Setting(index, field)).Should().Be(Modes.PrivateFile, $"{index} {field}");
            }
        }

        pi.Read(Key(1)).Should().NotBe(pi.Read(Key(2)));
        Directory.GetFiles(pi.OnDisk(Secrets), "*.tmp").Should().BeEmpty();
        var again = await (await InstallerPlanTests.StartAsync(pi, InstallRole.Controller)).CheckAsync();
        again.Steps.Where(step => step.Step is ApiKeyStep).Select(step => step.Check).Should().Equal(
            StepCheck.Unchanged("reuses roof-operator (RoofOperator)"),
            StepCheck.Unchanged("reuses installer-admin (RoofAdmin)"),
            StepCheck.Unchanged("reuses web-ui-stop (RoofViewer)"));
    }

    [TestMethod]
    public async Task NoKey_IsPrinted_Logged_OrSavedAnywhereButItsFile()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        var answers = pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] });

        var runs = new[]
        {
            await pi.RunAsync("--plan", "--answers", answers),
            await pi.RunAsync("--answers", answers),
            await pi.RunAsync("--plan")
        };

        runs.Select(run => run.ExitCode).Should().Equal(0, 0, 0);
        var keys = pi.ApiKeyValues();
        keys.Should().HaveCount(3);
        var log = pi.Read(InstallPaths.SystemLog);
        log.Should().Contain("Wrote a new API key, installer-admin (RoofAdmin), to /etc/hvo-roof/secrets as entry 1 (the key is never shown).");
        foreach (var key in keys)
        {
            foreach (var run in runs)
            {
                run.ToString().Should().NotContain(key);
            }

            log.Should().NotContain(key);
            pi.Read(InstallPaths.SystemRecord).Should().NotContain(key);
            pi.Read(answers).Should().NotContain(key);
            pi.Ran.Should().NotContain(command => command.ToString().Contains(key, StringComparison.Ordinal), "no key is ever on a command line");
        }
    }

    [TestMethod]
    public async Task TheKiosksKey_IsAViewerKioskKey_MarkedLocal()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        var session = await InstallerPlanTests.StartAsync(pi, InstallRole.Controller, InstallRole.Kiosk);

        var plan = await session.CheckAsync();
        Change(plan, Key(3)).Should().Be(new StepCheck(StepChange.Create, "kiosk (RoofViewer, kiosk, local): a new random key, never shown"));
        plan.Steps.Single(step => step.Step.Target == Key(3)).Step.Purpose.Should().Be("the kiosk's key, for PIN sign-in at the touchscreen (never shown)");
        await ApplyKeysAsync(session, plan);

        pi.Read(Setting(3, "Name")).Should().Be("kiosk");
        pi.Read(Setting(3, "Role")).Should().Be(RoofControllerApiContract.ViewerRole);
        pi.Read(Setting(3, "Kiosk")).Should().Be("true");
        pi.Read(Setting(3, "Local")).Should().Be("true");
        pi.Mode(Key(3)).Should().Be(Modes.PrivateFile);
    }

    [TestMethod]
    public void KeysAlreadyThere_AreReused_WhenTheyServe()
    {
        ConfiguredApiKey[] existing =
        [
            new(0, "observatory", RoofControllerApiContract.AdminRole, false, false, true),
            new(1, "touchscreen", RoofControllerApiContract.ViewerRole, true, true, true),
            new(2, "stop-button", RoofControllerApiContract.ViewerRole, false, false, true)
        ];

        var keys = ApiKeyFiles.Allocate(existing, [.. ControllerUses, ApiKeyUse.Kiosk], webStopIndex: 2);

        keys.Should().Equal(
            new ApiKeyAllocation(ApiKeyUse.Operator, 0, "observatory", RoofControllerApiContract.AdminRole, false, false, Reused: true),
            new ApiKeyAllocation(ApiKeyUse.Admin, 0, "observatory", RoofControllerApiContract.AdminRole, false, false, Reused: true),
            new ApiKeyAllocation(ApiKeyUse.WebStop, 2, "stop-button", RoofControllerApiContract.ViewerRole, false, false, Reused: true),
            new ApiKeyAllocation(ApiKeyUse.Kiosk, 1, "touchscreen", RoofControllerApiContract.ViewerRole, true, true, Reused: true));
    }

    [TestMethod]
    public void AnOperatorKey_IsPreferredToAnAdminKey_ForTheOperatorsWork_AndTheInstallersOwnNamesFirst()
    {
        ConfiguredApiKey[] existing =
        [
            new(0, "observatory", RoofControllerApiContract.AdminRole, false, false, true),
            new(1, "operator", RoofControllerApiContract.OperatorRole, false, false, true),
            new(4, "installer-admin", RoofControllerApiContract.AdminRole, false, false, true)
        ];

        var keys = ApiKeyFiles.Allocate(existing, ControllerUses, webStopIndex: null);

        keys.Select(key => (key.Use, key.Index, key.Reused)).Should().Equal(
            (ApiKeyUse.Operator, 1, true), (ApiKeyUse.Admin, 4, true), (ApiKeyUse.WebStop, 2, false));
    }

    [TestMethod]
    public void KeysThatCannotServe_AreNotReused()
    {
        ConfiguredApiKey[] existing =
        [
            new(0, "hashed", RoofControllerApiContract.OperatorRole, false, false, HasKey: false),
            new(1, "touchscreen-admin", RoofControllerApiContract.AdminRole, true, false, true),
            new(2, null, RoofControllerApiContract.OperatorRole, false, false, true),
            new(3, "reader", RoofControllerApiContract.ViewerRole, false, false, true)
        ];

        var keys = ApiKeyFiles.Allocate(existing, [.. ControllerUses, ApiKeyUse.Kiosk], webStopIndex: 1);

        keys.Should().OnlyContain(key => !key.Reused, "a key known only by its hash, a kiosk key, a key without a name, and a Viewer key not the web UI's are no use to the installer");
        keys.Select(key => key.Index).Should().Equal(4, 5, 6, 7);
    }

    [TestMethod]
    public void NewKeys_TakeTheLowestFreeEntries_AndNamesNoOtherKeyHas()
    {
        ConfiguredApiKey[] existing =
        [
            new(1, "installer-operator", RoofControllerApiContract.ViewerRole, false, false, true),
            new(3, "WEB-UI-STOP", RoofControllerApiContract.OperatorRole, true, false, false)
        ];

        var keys = ApiKeyFiles.Allocate(existing, [.. ControllerUses, ApiKeyUse.Kiosk], webStopIndex: null, managedNames: ["kiosk", "installer-admin", "installer-admin-2"]);

        keys.Select(key => (key.Index, key.Name)).Should().Equal(
            (0, "installer-operator-2"), (2, "installer-admin-3"), (4, "web-ui-stop-2"), (5, "kiosk-2"));
        keys.Should().OnlyContain(key => !key.Reused);
    }

    [TestMethod]
    public void TheSecretsFolder_IsReadForNamesRolesAndFlags_NeverForTheKey()
    {
        using var pi = new FakeMachine()
            .WithApiKey(0, "roof-operator", RoofControllerApiContract.OperatorRole)
            .WithApiKey(2, "touchscreen", RoofControllerApiContract.ViewerRole, kiosk: true, local: true)
            .WithApiKey(10, "hashed", RoofControllerApiContract.AdminRole, key: false);
        pi.Write($"{Secrets}/Kestrel__Certificates__Default__Password", "not-a-key");
        pi.Write($"{Secrets}/RoofControllerSecurity__ApiKeys__10__KeySha256", new string('a', 64));
        pi.Write($"{Secrets}/roofcontrollersecurity__apikeys__2__name.bak", "ignored");

        var keys = ApiKeyFiles.Read(pi.Machine, Secrets);

        keys.Should().Equal(
            new ConfiguredApiKey(0, "roof-operator", RoofControllerApiContract.OperatorRole, false, false, true),
            new ConfiguredApiKey(2, "touchscreen", RoofControllerApiContract.ViewerRole, true, true, true),
            new ConfiguredApiKey(10, "hashed", RoofControllerApiContract.AdminRole, false, false, false));
        ApiKeyFiles.Read(pi.Machine, "/etc/hvo-roof/none").Should().BeEmpty();
    }

    [TestMethod]
    public void TheWebUisKeyFile_NamesItsEntry()
    {
        ApiKeyFiles.IndexOfContainerKeyFile("/run/secrets/RoofControllerSecurity__ApiKeys__2__Key").Should().Be(2);
        ApiKeyFiles.ContainerKeyFile(7).Should().Be("/run/secrets/RoofControllerSecurity__ApiKeys__7__Key");
        ApiKeyFiles.IndexOfContainerKeyFile("/run/secrets/RoofControllerSecurity__ApiKeys__2__Name").Should().BeNull();
        ApiKeyFiles.IndexOfContainerKeyFile("/etc/hvo-roof/secrets/RoofControllerSecurity__ApiKeys__2__Key").Should().BeNull();
        ApiKeyFiles.IndexOfContainerKeyFile(null).Should().BeNull();
    }

    [TestMethod]
    public async Task TheRunningWebUisKey_IsKept()
    {
        using var pi = new FakeMachine().WithPi()
            .WithContainer(MachineSurveyor.ControllerContainer, new FakeContainer
            {
                Settings = new Dictionary<string, string> { [MachineSurveyor.WebStopKeyFileSetting] = ApiKeyFiles.ContainerKeyFile(1) }
            })
            .WithCertificates()
            .WithApiKey(0, "roof-operator", RoofControllerApiContract.OperatorRole)
            .WithApiKey(1, "stop", RoofControllerApiContract.OperatorRole);
        pi.PortsInUse.UnionWith([8443, 8088]);

        var plan = await (await InstallerPlanTests.StartAsync(pi, InstallRole.Controller)).CheckAsync();

        Change(plan, Key(1)).Should().Be(StepCheck.Unchanged("reuses stop (RoofOperator)"));
        plan.Steps.Single(step => step.Step.Target == Key(1)).Step.Purpose.Should().Be("the web UI's own key for Stop (never shown)");
        Change(plan, Key(2)).Should().Be(new StepCheck(StepChange.Create, "installer-admin (RoofAdmin): a new random key, never shown"));
    }

    [TestMethod]
    public async Task AKeysLooseMode_IsTightened_AndTheKeyKept()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        var key = pi.Read(Key(0));
        File.SetUnixFileMode(pi.OnDisk(Key(0)), Modes.File);
        var session = await InstallerPlanTests.StartAsync(pi, InstallRole.Controller);

        var plan = await session.CheckAsync();
        Change(plan, Key(0)).Should().Be(new StepCheck(StepChange.Change, "roof-operator (RoofOperator): 0644 → 0600"));
        await session.ApplyAsync(plan);

        pi.Mode(Key(0)).Should().Be(Modes.PrivateFile);
        pi.Read(Key(0)).Should().Be(key);
    }

    [TestMethod]
    public async Task AKeyLeftHalfWritten_GetsItsKey_OnTheNextRun()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        var session = await InstallerPlanTests.StartAsync(pi, InstallRole.Controller);
        var plan = await session.CheckAsync();

        // The install stopped after the admin key's name and role, before its key.
        pi.WithApiKey(1, "installer-admin", RoofControllerApiContract.AdminRole, key: false);
        await ApplyKeysAsync(session, plan);

        pi.Read(Key(1)).Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
    }

    [TestMethod]
    public async Task AKeyThatChanged_SinceThePlan_StopsTheInstall()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        var session = await InstallerPlanTests.StartAsync(pi, InstallRole.Controller);
        var plan = await session.CheckAsync();

        pi.WithApiKey(1, "someone-else", RoofControllerApiContract.AdminRole);
        var install = () => session.ApplyAsync(plan);

        (await install.Should().ThrowAsync<InstallerException>()).Which.Message.Should().Contain("entry 1 changed since the plan was made: run the installer again");
        pi.Read(Setting(1, "Name")).Should().Be("someone-else", "someone else's key is left alone");
    }

    [TestMethod]
    public async Task WithoutRoot_ThePlanSaysWhatEachKeyIsFor_NotWhichEntry()
    {
        if (Environment.UserName == "root")
        {
            Assert.Inconclusive("root reads a folder whatever its mode");
        }

        using var pi = new FakeMachine(root: false).WithPi().WithApiKey(0, "roof-operator", RoofControllerApiContract.OperatorRole);
        File.SetUnixFileMode(pi.OnDisk(Secrets), UnixFileMode.None);
        try
        {
            var plan = await (await InstallerPlanTests.StartAsync(pi, InstallRole.Controller)).CheckAsync();

            var keys = plan.Steps.Where(step => step.Step is ApiKeyStep).ToArray();
            keys.Select(step => step.Step.Target).Should().AllBe($"{Secrets}/RoofControllerSecurity__ApiKeys__N__Key");
            keys.Select(step => step.Check).Should().AllBeEquivalentTo(new StepCheck(StepChange.Info, "only root can read the secrets folder: run with sudo to check it"));
        }
        finally
        {
            File.SetUnixFileMode(pi.OnDisk(Secrets), Modes.PrivateFolder);
        }
    }

    [TestMethod]
    public void TheIdentityStore_IsReadForNamesAndRoles()
    {
        using var pi = new FakeMachine();
        const string path = "/var/lib/hvo-roof/identity/identity.json";
        pi.Write(path, """
            {
              "schemaVersion": 1,
              "revision": 4,
              "users": [ { "name": "observer", "role": "RoofAdmin", "passwordHash": "not-read", "pinHash": null } ],
              "apiKeys": [ { "name": "kiosk", "role": "RoofViewer", "kiosk": true, "keySha256": "not-read" }, { "role": "RoofViewer" } ],
              "sessions": []
            }
            """);

        var identity = ControllerIdentity.Read(pi.Machine, path);

        identity.Users.Should().Equal(new IdentityEntry("observer", RoofControllerApiContract.AdminRole));
        identity.ApiKeys.Should().Equal(new IdentityEntry("kiosk", RoofControllerApiContract.ViewerRole));
        ControllerIdentity.Read(pi.Machine, "/var/lib/hvo-roof/identity/none.json").Should().Be(ControllerIdentity.Empty);
        pi.Write(path, "[1]");
        var notAStore = () => ControllerIdentity.Read(pi.Machine, path);
        notAStore.Should().Throw<InstallerException>().WithMessage($"{path} is not the controller's identity store.");
        pi.Write(path, "{");
        notAStore.Should().Throw<InstallerException>().WithMessage($"{path} is not the controller's identity store: *");
    }

    [TestMethod]
    public async Task ANewKey_NeverTakesTheNameOfAKeyAddedThroughTheApi()
    {
        using var pi = InstallerPlanTests.AdoptablePi();
        pi.Write("/var/lib/hvo-roof/identity/identity.json", """{ "users": [], "apiKeys": [ { "name": "web-ui-stop", "role": "RoofViewer" } ] }""");

        var plan = await (await InstallerPlanTests.StartAsync(pi, InstallRole.Controller)).CheckAsync();

        Change(plan, Key(2)).Should().Be(new StepCheck(StepChange.Create, "web-ui-stop-2 (RoofViewer): a new random key, never shown"));
    }

    private static async Task ApplyKeysAsync(InstallerSession session, CheckedPlan plan)
    {
        var context = session.Context;
        var keys = await new InstallPlan([.. plan.Steps.Select(step => step.Step).OfType<ApiKeyStep>()]).CheckAsync(context);
        await keys.ApplyAsync(context);
    }

    private static string Key(int index) => $"{Secrets}/RoofControllerSecurity__ApiKeys__{index}__Key";

    private static string Setting(int index, string field) => $"{Secrets}/RoofControllerSecurity__ApiKeys__{index}__{field}";

    private static StepCheck Change(CheckedPlan plan, string target) => plan.Steps.Single(step => step.Step.Target == target).Check;
}
