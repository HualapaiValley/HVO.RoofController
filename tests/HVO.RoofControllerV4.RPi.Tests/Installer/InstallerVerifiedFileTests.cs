using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>Release consumers use the bytes checked, even when someone changes the offline source before use (#145).</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerVerifiedFileTests
{
    [TestMethod]
    [DataRow("cli", true)]
    [DataRow("cli", false)]
    [DataRow("installer", true)]
    [DataRow("installer", false)]
    [DataRow("kiosk", true)]
    [DataRow("kiosk", false)]
    [DataRow("mac", true)]
    [DataRow("mac", false)]
    public async Task AReleaseFile_ChangedAfterVerification_IsNeverWhatItsConsumerReads(string consumer, bool replaced)
    {
        using var machine = new FakeMachine(os: consumer == "mac" ? InstallerOs.MacOS : InstallerOs.Linux, root: consumer != "mac");
        var folder = machine.ReleaseFolder("/release");
        var platform = machine.Machine.RuntimeIdentifier;
        var name = consumer switch
        {
            "cli" => FakeMachine.CliAssetName(platform),
            "installer" => FakeMachine.InstallerAssetName(platform),
            "kiosk" => FakeMachine.KioskAssetName("4.0.0"),
            _ => FakeMachine.MacAssetName("4.0.0")
        };
        PlanStep step = consumer switch
        {
            "cli" => new CliProgramStep("/programs", planned: false, "choose another folder"),
            "installer" => new InstallerProgramStep(InstallScope.System, machine.Machine),
            "kiosk" => new KioskProgramStep(),
            _ => new MacAppStep("/Applications", "/settings", planned: false, "choose another folder")
        };
        var program = consumer switch
        {
            "cli" => FakeMachine.CliProgram("4.0.0", platform),
            "installer" => FakeMachine.InstallerProgram("4.0.0", platform),
            "kiosk" => FakeMachine.KioskProgram("4.0.0"),
            _ => FakeMachine.MacProgram("4.0.0")
        };
        // The Mac replacement keeps the verified main executable but changes Info.plist: checking just that program
        // after extracting an unverified zip does not protect the rest of the app.
        var other = consumer == "mac" ? FakeMachine.MacAppZip("6.6.6", program) : Encoding.UTF8.GetBytes("not the release's file");
        var oldProgram = Encoding.UTF8.GetBytes("the program already installed");
        machine.Folder(Path.GetDirectoryName(step.Target)!);
        if (consumer == "mac")
            machine.WithMacApp("/Applications", "3.9.9");
        else
            machine.Machine.WriteAtomically(step.Target, oldProgram, Modes.Program);
        machine.Folder("/settings");
        var change = ChangeBeforeUse(machine, folder, name, other, replaced);
        var context = await ContextAsync(machine, folder);

        await step.ApplyAsync(context, new StepCheck(StepChange.Change), default);

        change.AssertUsedPrivateCopy();
        var target = consumer == "mac" ? ClientSteps.MacProgram(step.Target) : step.Target;
        machine.Machine.ReadBytes(target).Should().Equal(program);
        if (step is MacAppStep mac)
        {
            machine.Read(Path.Join(mac.Target, "Contents/Info.plist")).Should().Contain("4.0.0").And.NotContain("6.6.6");
            machine.Machine.ReadBytes(ClientSteps.MacProgram(mac.Previous)).Should().Equal(FakeMachine.MacProgram("3.9.9"));
        }
        else
        {
            machine.Mode(step.Target).Should().Be(Modes.Program);
            machine.Machine.ReadBytes(step.Target + ".previous").Should().Equal(oldProgram);
            machine.Exists(step.Target + ".new").Should().BeFalse();
        }
        machine.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow("SHA-256")]
    [DataRow("short file")]
    [DataRow("long file")]
    [DataRow("cancellation")]
    [DataRow("consumer read failure")]
    public async Task AReleaseFile_ThatCannotBeVerifiedOrConsumed_PreservesInstalledProgramsAndRemovesTemporaryFiles(string failure)
    {
        using var machine = new FakeMachine();
        using var cancellation = new CancellationTokenSource();
        var folder = machine.ReleaseFolder();
        var context = await ContextAsync(machine, folder);
        var step = new CliProgramStep("/programs", planned: false, "choose another folder");
        machine.Folder("/programs");
        machine.Write(step.Target, "installed program");
        machine.Write(step.Target + ".previous", "kept program");
        var name = FakeMachine.CliAssetName(machine.Machine.RuntimeIdentifier);
        var source = Path.Join(folder, name);
        var content = machine.Machine.ReadBytes(source)!;
        if (failure == "SHA-256") content[0] ^= 1;
        if (failure == "short file") content = content[..^1];
        if (failure == "long file") content = [.. content, 0];
        File.WriteAllBytes(machine.OnDisk(source), content);
        machine.BeforeRead = path =>
        {
            if (failure == "cancellation" && path == source) cancellation.Cancel();
            if (failure == "consumer read failure" && Path.GetFileName(path) == name && path != source)
                throw new IOException("injected consumer read failure");
        };
        var apply = () => step.ApplyAsync(context, new StepCheck(StepChange.Change), cancellation.Token);

        if (failure == "cancellation")
            await apply.Should().ThrowAsync<OperationCanceledException>();
        else if (failure == "consumer read failure")
            await apply.Should().ThrowAsync<IOException>().WithMessage("injected consumer read failure");
        else
            await apply.Should().ThrowAsync<InstallerException>().WithMessage("*its size or SHA-256 differs from release.json's.");

        machine.Read(step.Target).Should().Be("installed program");
        machine.Read(step.Target + ".previous").Should().Be("kept program");
        Directory.EnumerateFiles(machine.OnDisk("/programs")).Select(Path.GetFileName).Should()
            .BeEquivalentTo(new[] { "hvo-roof", "hvo-roof.previous" }, "no staged file is promoted or left behind");
        Directory.EnumerateDirectories(machine.OnDisk(machine.TemporaryDirectory), "hvo-roof-install-*").Should()
            .BeEmpty("the private copy is removed even after cancellation or a consumer failure");
    }

    private static async Task<InstallContext> ContextAsync(FakeMachine machine, string folder) => new()
    {
        Machine = machine.Machine,
        Log = InstallLog.None,
        Survey = await MachineSurveyor.SurveyAsync(machine.Machine),
        Answers = new InstallAnswers(),
        Version = "4.0.0",
        Release = ReleaseSource.Folder(folder)
    };

    internal static SourceChange ChangeBeforeUse(FakeMachine machine, string folder, string name, byte[] other, bool replaced)
        => new(machine, Path.Join(folder, name), other, replaced);

    internal sealed class SourceChange
    {
        private readonly FakeMachine _machine;
        private readonly string _source;
        private readonly byte[] _other;
        private readonly List<string> _reads = [];
        private UnixFileMode? _copyMode, _folderMode;

        internal SourceChange(FakeMachine machine, string source, byte[] other, bool replaced)
        {
            _machine = machine;
            _source = source;
            _other = other;
            machine.BeforeRead = path =>
            {
                if (Path.GetFileName(path) != Path.GetFileName(source)) return;
                _reads.Add(path);
                if (_reads.Count != 2) return;
                _copyMode = machine.Mode(path);
                _folderMode = machine.Mode(Path.GetDirectoryName(path)!);
                if (replaced)
                {
                    File.WriteAllBytes(machine.OnDisk(source + ".new"), other);
                    File.Move(machine.OnDisk(source + ".new"), machine.OnDisk(source), overwrite: true);
                }
                else
                {
                    using var stream = new FileStream(machine.OnDisk(source), FileMode.Open, FileAccess.Write);
                    stream.SetLength(0);
                    stream.Write(other);
                }
            };
        }

        internal void AssertUsedPrivateCopy()
        {
            File.ReadAllBytes(_machine.OnDisk(_source)).Should().Equal(_other, "the source changed before its consumer opened the file");
            _reads.Should().HaveCount(2, "the source is read once to verify and copy it, and its consumer reads only that copy");
            _reads[0].Should().Be(_source);
            _reads[1].Should().StartWith($"{_machine.TemporaryDirectory}/hvo-roof-install-").And.NotBe(_source);
            _copyMode.Should().Be(Modes.PrivateFile);
            _folderMode.Should().Be(Modes.PrivateFolder);
            _machine.Exists(Path.GetDirectoryName(_reads[1])!).Should().BeFalse("the work folder is removed after use");
        }
    }
}
