using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>The optional Alpaca listener is a separate account and preserves its explicit install choices.</summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerAlpacaTests
{
    private const string EnabledAnswers = """
        {"roles":["controller"],"controller":{"alpaca":{"enabled":true,"lanAddress":"192.168.1.50","port":11111}}}
        """;

    [TestMethod]
    public async Task Alpaca_DefaultsOff_WithoutAllocatingAnotherKey()
    {
        using var pi = new FakeMachine().WithPi();
        var run = await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }));

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Single().GetValueOrDefault("ALPACA_ENABLED").Should().Be("false");
        ApiKeyFiles.Read(pi.Machine, ControllerLayout.System.Secrets).Should().HaveCount(3);
        pi.Exists(ControllerLayout.System.AlpacaData).Should().BeFalse();
    }

    [TestMethod]
    public async Task AFreshAlpacaInstall_CreatesItsReadableDataBindSourceBeforeInvokingDeploy()
    {
        using var pi = new FakeMachine().WithPi();
        pi.DuringDeploy = () =>
        {
            var source = pi.Deploys.Last()["ALPACA_DATA_DIR"];
            pi.Machine.DirectoryExists(source).Should().BeTrue("Docker --mount requires an existing bind source");
            pi.Mode(source).Should().Be(Modes.Folder, "the root preflight must read the public device identities");
            new DirectoryInfo(pi.OnDisk(source)).LinkTarget.Should().BeNull();
        };

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(InstallAnswers.Parse(EnabledAnswers)));

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Should().ContainSingle();
    }

    [TestMethod]
    [DataRow("foreign-owner")]
    [DataRow("group-writable")]
    [DataRow("symlink")]
    public async Task Alpaca_RefusesAnUnsafeDataBindSourceBeforeDeployOrKeyCreation(string problem)
    {
        using var pi = new FakeMachine().WithPi();
        var folder = ControllerLayout.System.AlpacaData;
        pi.Folder(ControllerLayout.System.Data);
        if (problem == "symlink")
        {
            pi.Folder("/srv/outside");
            Directory.CreateSymbolicLink(pi.OnDisk(folder), pi.OnDisk("/srv/outside"));
        }
        else
        {
            pi.Folder(folder);
            if (problem == "foreign-owner")
            {
                pi.Owners[folder] = "someone:else";
                pi.NumericOwners[folder] = "1234:1234";
            }
            else File.SetUnixFileMode(pi.OnDisk(folder), Modes.Folder | UnixFileMode.GroupWrite);
        }

        var run = await pi.RunAsync("--answers", pi.WriteAnswers(InstallAnswers.Parse(EnabledAnswers)));

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        (run.Output + run.Error).Should().Contain("Alpaca data directory");
        pi.Deploys.Should().BeEmpty();
        pi.ApiKeyValues().Should().BeEmpty();
    }

    [TestMethod]
    public async Task Alpaca_UsesItsOwnServerAccount_KeyFileAndPersistentData_AndKeepsTheChoice()
    {
        using var pi = new FakeMachine().WithPi();
        var answers = InstallAnswers.Parse(EnabledAnswers);
        var path = pi.WriteAnswers(answers);
        var run = await pi.RunAsync("--answers", path);

        run.ExitCode.Should().Be(0, run.ToString());
        var key = ApiKeyFiles.Read(pi.Machine, ControllerLayout.System.Secrets).Should().ContainSingle(item => item.Role == RoofAlpacaContract.ServerRole).Which;
        key.IsUsable.Should().BeTrue();
        pi.Deploys.Should().ContainSingle();
        var deployment = pi.Deploys.Single();
        deployment.Should().Contain(new Dictionary<string, string>
        {
            ["ALPACA_ENABLED"] = "true",
            ["ALPACA_LAN_ADDRESS"] = "192.168.1.50",
            ["ALPACA_PORT"] = "11111",
            ["ALPACA_API_KEY_FILE"] = ApiKeyFiles.KeyFile(ControllerLayout.System.Secrets, key.Index),
            ["ALPACA_DATA_DIR"] = "/var/lib/hvo-roof/alpaca"
        });
        deployment.Keys.Should().NotContain(name => name.Contains("AllowCommands", StringComparison.Ordinal));
        InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).ToAnswers().ToJson().Should().Contain("\"alpaca\"").And.Contain("\"enabled\": true");
        InstallerLifecycleTests.ShowsNoSecret(pi, run);

        var data = ControllerLayout.System.AlpacaData;
        pi.Owners[data] = "alpaca:alpaca";
        pi.NumericOwners[data] = "1655:1655";
        var identities = "{\"dome/0\":\"00000000-0000-0000-0000-000000000001\"}";
        pi.Write(ControllerLayout.System.AlpacaDevicesFile, identities);
        var again = await pi.RunAsync("--answers", path);
        again.ExitCode.Should().Be(0, again.ToString());
        again.Output.Should().Contain("Nothing to change");
        pi.Deploys.Should().ContainSingle();
        ApiKeyFiles.Read(pi.Machine, ControllerLayout.System.Secrets).Should().HaveCount(4);
        pi.Owners[data].Should().Be("alpaca:alpaca");
        pi.Read(ControllerLayout.System.AlpacaDevicesFile).Should().Be(identities);
    }

    [TestMethod]
    [DataRow("0.0.0.0")]
    [DataRow("127.0.0.1")]
    [DataRow("100.64.0.1")]
    [DataRow("8.8.8.8")]
    [DataRow("::")]
    [DataRow("10.1")]
    [DataRow("10.0.0.01")]
    public void Alpaca_RefusesNonLanPublishAddresses(string address)
    {
        var json = EnabledAnswers.Replace("192.168.1.50", address, StringComparison.Ordinal);
        Action parse = () => InstallAnswers.Parse(json);
        parse.Should().Throw<InstallerUsageException>().WithMessage("*RFC1918*LAN*");
    }

    [TestMethod]
    [DataRow(8088)]
    [DataRow(8443)]
    [DataRow(0)]
    [DataRow(65536)]
    public void Alpaca_RequiresItsOwnValidPort(int port)
    {
        Action parse = () => InstallAnswers.Parse(EnabledAnswers.Replace("11111", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        parse.Should().Throw<InstallerUsageException>().WithMessage("*Alpaca*port*");
    }

    [TestMethod]
    public async Task Alpaca_CanBeExplicitlyDisabled_WithoutDeletingItsKeyOrIdentities()
    {
        using var pi = new FakeMachine().WithPi();
        var enabled = InstallAnswers.Parse(EnabledAnswers);
        (await pi.RunAsync("--answers", pi.WriteAnswers(enabled))).ExitCode.Should().Be(0);
        pi.Write("/var/lib/hvo-roof/alpaca/alpaca-devices.json", "{\"dome/0\":\"00000000-0000-0000-0000-000000000001\"}");
        var keys = pi.ApiKeyValues().ToArray();

        var disabled = enabled with { Controller = enabled.Controller! with { Alpaca = enabled.Controller.Alpaca! with { Enabled = false } } };
        var run = await pi.RunAsync("--answers", pi.WriteAnswers(disabled));

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Last()["ALPACA_ENABLED"].Should().Be("false");
        pi.ApiKeyValues().Should().BeEquivalentTo(keys);
        pi.Read("/var/lib/hvo-roof/alpaca/alpaca-devices.json").Should().Contain("00000000-0000-0000-0000-000000000001");
    }

    [TestMethod]
    public async Task AnUpgrade_PreservesTheAlpacaChoice_AccountKeyAndDeviceIdentities()
    {
        using var pi = new FakeMachine().WithPi();
        var enabled = InstallAnswers.Parse(EnabledAnswers);
        (await pi.RunAsync("--answers", pi.WriteAnswers(enabled))).ExitCode.Should().Be(0);
        const string path = "/var/lib/hvo-roof/alpaca/alpaca-devices.json";
        var ids = "{\"dome/0\":\"00000000-0000-0000-0000-000000000001\",\"safetymonitor/0\":\"00000000-0000-0000-0000-000000000002\"}";
        pi.Write(path, ids);
        pi.Owners[ControllerLayout.System.AlpacaData] = "1655:1655";
        pi.NumericOwners[ControllerLayout.System.AlpacaData] = "1655:1655";
        var keys = pi.ApiKeyValues().ToArray();
        pi.WithRelease("4.0.1", latest: true);

        var run = await pi.RunAsync("upgrade");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Deploys.Last()["ALPACA_ENABLED"].Should().Be("true");
        pi.Deploys.Last()["ALPACA_PORT"].Should().Be("11111");
        pi.ApiKeyValues().Should().BeEquivalentTo(keys);
        pi.Read(path).Should().Be(ids);
        pi.Owners[ControllerLayout.System.AlpacaData].Should().Be("1655:1655");
        InstallRecord.Parse(pi.Read(InstallPaths.SystemRecord)).Controller!.Alpaca.Should().Be(enabled.Controller!.Alpaca);
        InstallerLifecycleTests.ShowsNoSecret(pi, run);
    }

    [TestMethod]
    [DataRow("exited")]
    [DataRow("missing")]
    public async Task Uninstall_RemovesOwnedDiscovery_EvenWhenTheControllerIsStoppedOrMissing(string state)
    {
        using var pi = new FakeMachine().WithPi();
        (await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }))).ExitCode.Should().Be(0);
        var name = MachineSurveyor.ControllerContainer;
        if (state == "missing") pi.Containers.Remove(name);
        else pi.Containers[name] = pi.Containers[name] with { State = state };
        pi.Containers[name + "-discovery"] = new FakeContainer { DiscoveryFor = name };

        var run = await pi.RunAsync("uninstall", "--yes");

        run.ExitCode.Should().Be(0, run.ToString());
        pi.Containers.Should().NotContainKey(name + "-discovery");
    }

    [TestMethod]
    public async Task Uninstall_RefusesAForeignDiscoveryName_BeforeRemovingAnything()
    {
        using var pi = new FakeMachine().WithPi();
        (await pi.RunAsync("--answers", pi.WriteAnswers(new InstallAnswers { Roles = [InstallRole.Controller] }))).ExitCode.Should().Be(0);
        var name = MachineSurveyor.ControllerContainer;
        pi.Containers[name + "-discovery"] = new FakeContainer { DiscoveryFor = "another-controller" };

        var run = await pi.RunAsync("uninstall", "--yes");

        run.ExitCode.Should().Be((int)InstallerExitCode.Refused, run.ToString());
        (run.Output + run.Error).Should().Contain("discovery").And.Contain("owned");
        pi.Containers.Should().ContainKey(name).And.ContainKey(name + "-discovery");
    }
}
