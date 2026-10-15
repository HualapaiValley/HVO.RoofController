using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>Installer plumbing on redirected files/fake system commands; actual PAM/systemd belongs to guest tests.</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerLinuxAuthTests
{
    private static LinuxAuthSettings Settings => new()
    {
        Enabled = true,
        Users = [new() { Name = "fixture-admin", Role = RoofControllerApiContract.AdminRole, LocalAdmin = true },
                 new() { Name = "fixture-viewer", Role = RoofControllerApiContract.ViewerRole }]
    };

    [TestMethod]
    public async Task OptIn_IsAbsentFromLegacyPlan_AndValidatesNamesAndRoles()
    {
        using var machine = new FakeMachine().WithPi();
        var session = await InstallerPlanTests.StartAsync(machine, InstallRole.Controller);
        PlanBuilder.Build(session.Machine, session.Survey, session.Answers).Steps.Should().NotContain(step => step.GetType() == typeof(LinuxAuthStep) || step.GetType() == typeof(LinuxAuthEnrollmentStep));
        var answers = new InstallAnswers { Roles = [InstallRole.Rig], Controller = new() { LinuxAuth = Settings } };
        InstallAnswers.Parse(answers.ToJson()).Controller!.LinuxAuth.Should().Be(Settings);
        new LinuxAuthSettings { Users = Settings.Users }.Problems().Should().NotBeEmpty();
        new LinuxAuthSettings { Enabled = true, Users = [new() { Name = "root" }] }.Problems().Should().NotBeEmpty();
        new LinuxAuthSettings { Enabled = true, Users = [new() { Name = "bad,name", Role = "owner" }] }.Problems().Should().HaveCount(2);
    }

    [TestMethod]
    public async Task ExplicitLinuxAdministratorBootstrap_NeedsNoVirtualPasswordOrPin()
    {
        using var fixture = await Fixture.Create();
        var session = await InstallerSession.StartAsync(fixture.Context.Machine, InstallLog.None, "4.0.0", TimeProvider.System);
        session.Answers = new InstallAnswers
        {
            Roles = [InstallRole.Rig],
            Controller = new ControllerSettings
            {
                LinuxAuth = Settings with { Users = [Settings.Users[0]] }
            }
        };
        var plan = await session.CheckAsync();
        plan.Steps.Should().NotContain(step => step.Check.Change == StepChange.Blocked);
        session.MissingSecrets(plan).Should().BeEmpty("the selected existing Linux administrator does not need a virtual bootstrap password or PIN");
        session.Answers.Controller!.FirstAdmin.Should().BeNull();
        fixture.Runner.Commands.Should().NotContain(command =>
            command.Program == "useradd" || command.Program == "passwd" || command.Program == "chpasswd");
    }

    [TestMethod]
    public async Task Rerun_PreservesKeyAndSubjects_AndUninstallRetainsPrivateRecoveryData()
    {
        using var fixture = await Fixture.Create();
        var step = new LinuxAuthStep(Settings);
        var first = await step.CheckAsync(fixture.Context, default);
        first.Change.Should().Be(StepChange.Create);
        await step.ApplyAsync(fixture.Context, first, default);
        var key = fixture.Pi.Machine.ReadBytes(LinuxAuthStep.KeyFile)!;
        var enrollment = fixture.Pi.Read(LinuxAuthStep.EnrollmentFile);
        key.Should().HaveCount(32);
        fixture.Pi.Mode(LinuxAuthStep.KeyFile).Should().Be(Modes.PrivateFile);
        fixture.Pi.Read(LinuxAuthStep.Sudoers).Should().Contain("fixture-admin ALL=(root) NOPASSWD: /usr/local/libexec/hvo-roof-local-auth \"\"")
            .And.NotContain("fixture-viewer ALL=");
        (await step.CheckAsync(fixture.Context, default)).Change.Should().Be(StepChange.Unchanged);
        fixture.Pi.Machine.ReadBytes(LinuxAuthStep.KeyFile).Should().Equal(key);
        fixture.Pi.Read(LinuxAuthStep.EnrollmentFile).Should().Be(enrollment);
        var removal = new LinuxAuthRemovalStep();
        await removal.ApplyAsync(fixture.Context, await removal.CheckAsync(fixture.Context, default), default);
        fixture.Pi.Exists(LinuxAuthStep.Program).Should().BeFalse();
        fixture.Pi.Exists(LinuxAuthStep.Sudoers).Should().BeFalse();
        fixture.Pi.Machine.ReadBytes(LinuxAuthStep.KeyFile).Should().Equal(key);
        (await removal.CheckAsync(fixture.Context, default)).Change.Should().Be(StepChange.Unchanged);
        await step.ApplyAsync(fixture.Context, await step.CheckAsync(fixture.Context, default), default);
        fixture.Pi.Read(LinuxAuthStep.EnrollmentFile).Should().Be(enrollment);
        fixture.Pi.Machine.ReadBytes(LinuxAuthStep.KeyFile).Should().Equal(key);
    }

    [TestMethod]
    public async Task ModifiedPolicy_UnownedFile_AndWritableParent_AreRefused()
    {
        using var fixture = await Fixture.Create();
        fixture.Pi.Write(LinuxAuthStep.Helper, "operator-owned");
        (await new LinuxAuthStep(Settings).CheckAsync(fixture.Context, default)).Change.Should().Be(StepChange.Blocked);
        fixture.Pi.Machine.DeleteFile(LinuxAuthStep.Helper);
        var step = new LinuxAuthStep(Settings);
        await step.ApplyAsync(fixture.Context, await step.CheckAsync(fixture.Context, default), default);
        fixture.Pi.Machine.SetMode(LinuxAuthStep.Sudoers, Modes.PrivateFile);
        fixture.Pi.Write(LinuxAuthStep.Sudoers, "changed outside installer");
        (await step.CheckAsync(fixture.Context, default)).Change.Should().Be(StepChange.Blocked);
        (await new LinuxAuthRemovalStep().CheckAsync(fixture.Context, default)).Change.Should().Be(StepChange.Blocked);
        fixture.Pi.Machine.SetMode(LinuxAuthStep.ConfigurationFolder, Modes.Folder | UnixFileMode.OtherWrite);
        (await step.CheckAsync(fixture.Context, default)).Detail.Should().Contain("writable");
    }

    [TestMethod]
    public async Task UnknownOrReassignedLinuxAccount_IsNeverCreatedOrAdopted()
    {
        using var fixture = await Fixture.Create();
        fixture.Runner.MissingUser = true;
        (await new LinuxAuthStep(Settings).CheckAsync(fixture.Context, default)).Change.Should().Be(StepChange.Blocked);
        fixture.Runner.MissingUser = false;
        var step = new LinuxAuthStep(Settings);
        await step.ApplyAsync(fixture.Context, await step.CheckAsync(fixture.Context, default), default);
        fixture.Runner.UidOffset = 20;
        (await step.CheckAsync(fixture.Context, default)).Detail.Should().Contain("UID/name");
        fixture.Runner.Commands.Should().NotContain(command => command.Program == "useradd");
    }

    [TestMethod]
    public async Task WorkerFailure_RestoresFilesAndPreviouslyInactiveService()
    {
        using var fixture = await Fixture.Create();
        var step = new LinuxAuthStep(Settings);
        await step.ApplyAsync(fixture.Context, await step.CheckAsync(fixture.Context, default), default);
        var before = fixture.Pi.Read(LinuxAuthStep.EnrollmentFile);
        fixture.Runner.Active = false;
        fixture.Runner.Enabled = false;
        fixture.Runner.FailRestart = true;
        var changed = new LinuxAuthStep(Settings with { Users = [Settings.Users[0]] });
        var apply = () => changed.ApplyAsync(fixture.Context, new(StepChange.Change), default);
        await apply.Should().ThrowAsync<InstallerException>();
        fixture.Pi.Read(LinuxAuthStep.EnrollmentFile).Should().Be(before);
        fixture.Runner.Active.Should().BeFalse();
        fixture.Runner.Enabled.Should().BeFalse();
    }

    [TestMethod]
    public async Task RevokedOrChangedApplicationPrincipal_IsNeverRestoredByInstaller()
    {
        using var fixture = await Fixture.Create();
        var native = new LinuxAuthStep(Settings);
        await native.ApplyAsync(fixture.Context, await native.CheckAsync(fixture.Context, default), default);
        var enrollments = JsonNode.Parse(fixture.Pi.Read(LinuxAuthStep.EnrollmentFile))!.AsArray();
        var user = enrollments[0]!;
        fixture.Pi.Write(ControllerLayout.System.IdentityFile, new JsonObject
        {
            ["schemaVersion"] = 2,
            ["linuxPrincipals"] = new JsonArray(new JsonObject
            {
                ["name"] = user["userName"]!.DeepClone(), ["subject"] = user["subject"]!.DeepClone(),
                ["enrollmentStamp"] = user["enrollmentStamp"]!.DeepClone(), ["role"] = "RoofAdmin", ["enabled"] = false
            })
        }.ToJsonString());
        var key = new ApiKeyAllocation(ApiKeyUse.Admin, 0, "installer-admin", "RoofAdmin", false, false, Reused: false);
        var check = await new LinuxAuthEnrollmentStep(ControllerLayout.System, Settings, key).CheckAsync(fixture.Context, default);
        check.Change.Should().Be(StepChange.Blocked);
        check.Detail.Should().Contain("never restores");
    }

    [TestMethod]
    public void IdentitySchemaDowngrade_IsRefusedWithoutReplayingBackup()
    {
        using var pi = new FakeMachine();
        var release = ReleaseManifest.Parse(FakeMachine.ReleaseJson(), "4.0.0");
        pi.Write(ControllerLayout.System.IdentityFile, "{\"schemaVersion\":2,\"linuxPrincipals\":[]}");
        pi.Write(ControllerLayout.System.IdentityFile + ".schema1.bak", "{\"schemaVersion\":1}");
        IdentitySchemaGuard.Problem(pi.Machine, ControllerLayout.System.IdentityFile, release).Should().Contain("refused");
        IdentitySchemaGuard.Problem(pi.Machine, ControllerLayout.System.IdentityFile, release with { IdentityStoreSchema = 2 }).Should().BeNull();
        pi.Read(ControllerLayout.System.IdentityFile).Should().Contain("\"schemaVersion\":2");
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{\"schemaVersion\":null}")]
    [DataRow("{\"schemaVersion\":\"2\"}")]
    [DataRow("{\"schemaVersion\":true}")]
    [DataRow("{\"schemaVersion\":0}")]
    [DataRow("{\"schemaVersion\":-1}")]
    [DataRow("{\"schemaVersion\":3}")]
    [DataRow("{\"schemaVersion\":2.5}")]
    [DataRow("{\"schemaVersion\":1.0}")]
    [DataRow("{\"schemaVersion\":2e0}")]
    [DataRow("{\"schemaVersion\":\"invalid\",\"schemaVersion\":1}")]
    [DataRow("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [DataRow("{\"schemaVersion\":1,\"ignored\":NaN}")]
    [DataRow("{")]
    public void InvalidIdentitySchema_IsRefusedWithoutChangingData(string text)
    {
        using var pi = new FakeMachine();
        var release = ReleaseManifest.Parse(FakeMachine.ReleaseJson(), "4.0.0") with { IdentityStoreSchema = 2 };
        pi.Write(ControllerLayout.System.IdentityFile, text);
        pi.Write(ControllerLayout.System.IdentityFile + ".schema1.bak", "{\"schemaVersion\":1}");
        IdentitySchemaGuard.Problem(pi.Machine, ControllerLayout.System.IdentityFile, release).Should().NotBeNull();
        pi.Read(ControllerLayout.System.IdentityFile).Should().Be(text);
        pi.Read(ControllerLayout.System.IdentityFile + ".schema1.bak").Should().Be("{\"schemaVersion\":1}");
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"schemaVersion\":1,\"linuxPrincipals\":[]}")]
    public void LegacyIdentitySchema_RemainsCompatible(string text)
    {
        using var pi = new FakeMachine();
        var release = ReleaseManifest.Parse(FakeMachine.ReleaseJson(), "4.0.0");
        pi.Write(ControllerLayout.System.IdentityFile, text);
        IdentitySchemaGuard.Problem(pi.Machine, ControllerLayout.System.IdentityFile, release).Should().BeNull();
    }

    private sealed class Fixture(FakeMachine pi, Runner runner, InstallContext context) : IDisposable
    {
        public FakeMachine Pi { get; } = pi;
        public Runner Runner { get; } = runner;
        public InstallContext Context { get; } = context;
        public void Dispose() => Pi.Dispose();
        public static async Task<Fixture> Create()
        {
            var pi = new FakeMachine();
            // The development user's umask may make redirected test directories group-writable.
            // Model root-owned system parents explicitly; never relax the production path check.
            foreach (var folder in new[] { "/", "/etc", "/opt", "/run", "/usr", "/usr/local", "/usr/local/libexec",
                "/etc/systemd", "/etc/systemd/system", "/etc/pam.d", "/etc/sudoers.d", "/etc/hvo-roof" })
                pi.Machine.CreateDirectory(folder, Modes.Folder);
            var release = JsonNode.Parse(FakeMachine.ReleaseJson())!;
            var bytes = Encoding.UTF8.GetBytes("inert worker artifact for command-fake tests");
            const string name = "hvo-roof-linux-auth-linux-arm64";
            release["assets"]!.AsArray().Add(new JsonObject { ["name"] = name, ["kind"] = "linux-auth", ["platform"] = "linux-arm64",
                ["size"] = bytes.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)) });
            pi.Downloads[ReleaseManifest.DownloadUri("4.0.0").ToString()] = release.ToJsonString();
            pi.FileDownloads[ReleaseManifest.DownloadUri("4.0.0", name).ToString()] = bytes;
            var runner = new Runner(pi);
            var context = new InstallContext { Machine = pi.Machine.WithCommands(runner), Log = InstallLog.None,
                Survey = await MachineSurveyor.SurveyAsync(pi.Machine), Answers = new(), Version = "4.0.0" };
            return new(pi, runner, context);
        }
    }

    private sealed class Runner(FakeMachine pi) : ICommandRunner
    {
        public bool Active { get; set; }
        public bool Enabled { get; set; }
        public bool MissingUser { get; set; }
        public bool FailRestart { get; set; }
        public int UidOffset { get; set; }
        public List<CommandLine> Commands { get; } = [];
        public string? Find(string program) => "/usr/bin/" + program;
        public Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (command.Program == "getent")
            {
                var name = command.Arguments[1];
                var uid = (name == "fixture-admin" ? 26001 : 26002) + UidOffset;
                return Task.FromResult(MissingUser ? new CommandResult(2, "", "") : new CommandResult(0, $"{name}:x:{uid}:{uid}::/nonexistent:/usr/sbin/nologin\n", ""));
            }
            if (command.Program == "visudo") return Task.FromResult(new CommandResult(0, "", ""));
            if (command.Program == "systemctl")
            {
                var action = command.Arguments[0];
                if (action == "is-active") return Task.FromResult(new CommandResult(Active ? 0 : 3, "", ""));
                if (action == "is-enabled") return Task.FromResult(new CommandResult(Enabled ? 0 : 1, "", ""));
                if (action == "enable") Enabled = true;
                if (action == "disable") { Enabled = false; if (command.Arguments.Contains("--now")) Active = false; }
                if (action is "start" or "restart") { if (FailRestart) return Task.FromResult(new CommandResult(1, "", "")); Active = true; }
                if (action == "stop") Active = false;
                return Task.FromResult(new CommandResult(0, "", ""));
            }
            return pi.RunAsync(command, cancellationToken);
        }
    }
}
