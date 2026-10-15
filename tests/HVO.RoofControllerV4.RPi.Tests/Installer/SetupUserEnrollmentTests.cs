using FluentAssertions;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

[TestClass]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class SetupUserEnrollmentTests
{
    private sealed class Accounts(string result) : ICommandRunner
    {
        public string? Find(string program) => "/usr/bin/" + program;
        public Task<CommandResult> RunAsync(CommandLine command, CancellationToken cancellationToken = default)
        {
            command.Program.Should().Be("getent");
            command.Arguments.Should().Equal("passwd", "1001");
            return Task.FromResult(new CommandResult(0, result, ""));
        }
    }

    [TestMethod]
    public async Task SudoCaller_IsResolvedByUid_AndOnlyThatAccountIsEnrolled()
    {
        using var fake = new FakeMachine();
        fake.Environment["SUDO_USER"] = "fixture-admin";
        fake.Environment["SUDO_UID"] = "1001";
        var settings = new LinuxAuthSettings { Enabled = true, EnrollSetupUser = true };
        var result = await SetupUserEnrollment.ResolveAsync(fake.Machine.WithCommands(new Accounts("fixture-admin:x:1001:1001::/home/fixture-admin:/bin/sh")), settings);
        result.EnrollSetupUser.Should().BeFalse("the saved record must pin the resolved user, never reenroll a different sudo caller on upgrade");
        result.Users.Should().ContainSingle().Which.Should().Be(new LinuxAuthUserSettings { Name = "fixture-admin", Role = "RoofAdmin", LocalAdmin = true });
        var conflicting = settings with { Users = [new() { Name = "fixture-admin", Role = "RoofViewer" }] };
        await FluentActions.Awaiting(() => SetupUserEnrollment.ResolveAsync(fake.Machine.WithCommands(new Accounts("fixture-admin:x:1001:1001::/home/fixture-admin:/bin/sh")), conflicting)).Should().ThrowAsync<InstallerUsageException>();
    }

    [TestMethod]
    [DataRow("root", "0", "root:x:0:0::/root:/bin/sh")]
    [DataRow("fixture-admin", "1001", "someone-else:x:1001:1001::/home/else:/bin/sh")]
    [DataRow("fixture-admin", "bad", "")]
    [DataRow("fixture-admin", "1001", "fixture-admin:x:1002:1002::/home/fixture-admin:/bin/sh")]
    public async Task RootMissingOrMismatchedIdentity_IsRefused(string name, string uid, string account)
    {
        using var fake = new FakeMachine();
        fake.Environment["SUDO_USER"] = name;
        fake.Environment["SUDO_UID"] = uid;
        await FluentActions.Awaiting(() => SetupUserEnrollment.ResolveAsync(fake.Machine.WithCommands(new Accounts(account)), new() { Enabled = true, EnrollSetupUser = true })).Should().ThrowAsync<InstallerUsageException>();
    }
}
