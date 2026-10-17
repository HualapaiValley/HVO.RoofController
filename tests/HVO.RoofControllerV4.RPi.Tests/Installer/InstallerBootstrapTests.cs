using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Installer.Deployment;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>Full installer commands on redirected files; historical binaries and services are covered by the rig/guest gates.</summary>
[TestClass, UnsupportedOSPlatform("windows")]
public sealed class InstallerBootstrapTests
{
    private const string Candidate = "4.0.1";

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public async Task NewInstallerBootstrap_PreservesExistingIdentityAndDoesNotCreateCommissioningAccess(int schema)
    {
        using var machine = await InstallerLifecycleTests.InstalledPiAsync();
        var identity = ControllerLayout.System.IdentityFile;
        var saved = new JsonObject
        {
            ["schemaVersion"] = schema,
            ["users"] = new JsonArray(new JsonObject { ["name"] = "existing-disabled", ["enabled"] = false }),
            ["linuxPrincipals"] = new JsonArray(),
            ["kioskCredential"] = null
        }.ToJsonString();
        machine.Write(identity, saved);
        var ca = machine.Read("/etc/hvo-roof/ca.crt");
        var deviceKey = machine.Read(KioskSteps.DeviceKeyFile);
        var oldRecord = InstallRecord.Parse(machine.Read(InstallPaths.SystemRecord));
        WithSchema(machine, Candidate, 3);
        // This models starting the verified newer binary directly, not an old binary relabeled as new.
        machine.InstallerVersion = Candidate;

        var run = await machine.RunAsync("upgrade", "--version", Candidate);

        run.ExitCode.Should().Be(0, run.ToString());
        machine.Launches.Should().BeEmpty("the new installer performs its own upgrade");
        machine.Read(identity).Should().Be(saved);
        machine.Read("/etc/hvo-roof/ca.crt").Should().Be(ca);
        machine.Read(KioskSteps.DeviceKeyFile).Should().Be(deviceKey);
        machine.Exists(KioskCommissioningClaimStep.ClaimFile).Should().BeFalse();
        var record = InstallRecord.Parse(machine.Read(InstallPaths.SystemRecord));
        record.Version.Should().Be(Candidate);
        record.PreviousVersion.Should().Be(oldRecord.Version);
        record.Controller.Should().BeEquivalentTo(oldRecord.Controller);
        record.Kiosk.Should().BeEquivalentTo(oldRecord.Kiosk);
        machine.Unexpected.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SchemaThreeRollback_CommandRefusesBeforeChangingFilesOrDeployments(bool planOnly)
    {
        using var machine = await InstallerLifecycleTests.InstalledPiAsync();
        WithSchema(machine, Candidate, 3);
        machine.InstallerVersion = Candidate;
        var upgrade = await machine.RunAsync("upgrade", "--version", Candidate);
        upgrade.ExitCode.Should().Be(0, upgrade.ToString());
        // A synthetic schema-3 identity and retirement history; no real credential is created or replayed.
        var identity = ControllerLayout.System.IdentityFile;
        machine.Write(identity, "{\"schemaVersion\":3,\"users\":[],\"linuxPrincipals\":[]}");
        machine.Write(identity + ".kiosk-retired", "retired fixture history");
        machine.Write(identity + ".schema1.bak", "older fixture identity must never be replayed");
        WithSchema(machine, "4.0.0", 2);
        var files = machine.Snapshot(InstallPaths.SystemLog);
        var deployments = machine.Deploys.Count;
        var launches = machine.Launches.Count;

        var run = await machine.RunAsync(planOnly ? ["rollback", "--plan"] : ["rollback"]);

        run.ExitCode.Should().Be(3, run.ToString());
        run.Error.Should().Contain("Rollback is refused before replacing the controller")
            .And.Contain("identity store here is schema 3, and release 4.0.0 reads identity schemas 1 to 2");
        machine.Snapshot(InstallPaths.SystemLog).Should().Equal(files);
        machine.Deploys.Should().HaveCount(deployments);
        machine.Launches.Should().HaveCount(launches);
        machine.Unexpected.Should().BeEmpty();
    }

    private static void WithSchema(FakeMachine machine, string version, int schema)
    {
        machine.WithRelease(version);
        var uri = ReleaseManifest.DownloadUri(version).ToString();
        var manifest = JsonNode.Parse(machine.Downloads[uri])!;
        manifest["identityStoreSchema"] = schema;
        machine.Downloads[uri] = manifest.ToJsonString();
    }
}
