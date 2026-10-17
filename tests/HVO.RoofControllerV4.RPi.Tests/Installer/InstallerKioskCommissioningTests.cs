using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Survey;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

[TestClass, System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class InstallerKioskCommissioningTests
{
    private static LinuxAuthSettings Settings(string name="fixture-owner") => new()
    {
        Enabled=true, Users=[new(){ Name=name, Role=RoofControllerApiContract.AdminRole }]
    };
    private sealed class Runner(FakeMachine machine):ICommandRunner
    {
        public bool FailSync;
        public string Shell="/bin/bash";
        public string? Find(string program)=>"/usr/bin/"+program;
        public Task<CommandResult> RunAsync(CommandLine command,CancellationToken token=default)
            => command.Program switch
            {
                "sync"=>Task.FromResult(new CommandResult(FailSync?1:0,"","")),
                "getent"=>Task.FromResult(new CommandResult(0,$"{command.Arguments[1]}:x:26001:26001::/home/fixture-owner:{Shell}\n","")),
                _=>machine.RunAsync(command,token)
            };
    }
    private static async Task<InstallContext> Context(FakeMachine machine,Runner runner) => new()
    {
        Machine=machine.Machine.WithCommands(runner), Log=InstallLog.None,
        Survey=await MachineSurveyor.SurveyAsync(machine.Machine),Answers=new(),Version="4.1.1"
    };

    [TestMethod]
    public void SchemaThreeRefusesOlderRollbackWithoutReplacingIdentity()
    {
        using var machine=new FakeMachine().WithPi();
        var layout=ControllerLayout.System;
        machine.Write(layout.IdentityFile, "{\"schemaVersion\":3}");
        var release=HVO.RoofControllerV4.Installer.Deployment.ReleaseManifest.Parse(FakeMachine.ReleaseJson(), "4.0.0");
        foreach(var schema in new[]{1,2})
            StateContractGuard.Problem(machine.Machine,layout,release with {IdentityStoreSchema=schema}).Should().Contain("refused");
        StateContractGuard.Problem(machine.Machine,layout,release with {IdentityStoreSchema=3}).Should().BeNull();
        machine.Read(layout.IdentityFile).Should().Be("{\"schemaVersion\":3}");
    }

    [TestMethod]
    public async Task FreshClaimSeedsStableHumanWithoutChangingLinuxAccountAndCannotRepeat()
    {
        using var machine=new FakeMachine().WithPi(); var runner=new Runner(machine);
        var context=await Context(machine,runner); var layout=ControllerLayout.System;
        var claim=new KioskCommissioningClaimStep(layout);
        await claim.ApplyAsync(context,await claim.CheckAsync(context,default),default);
        machine.Folder(layout.Identity);
        machine.Folder(LinuxAuthStep.ConfigurationFolder);
        machine.Write(LinuxAuthStep.EnrollmentFile,JsonSerializer.Serialize(new[]{
            new LinuxAuthStep.Enrollment(26001,"fixture-owner","33333333-3333-4333-8333-333333333333","44444444-4444-4444-8444-444444444444",true)
        },LinuxAuthStep.Json));
        var seed=new KioskCommissioningSeedStep(layout,Settings(),claim);
        (await seed.CheckAsync(context,default)).Change.Should().Be(StepChange.Create);
        await seed.ApplyAsync(context,await seed.CheckAsync(context,default),default);
        using var json=JsonDocument.Parse(machine.Read(layout.IdentityFile));
        json.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(3);
        var principal=json.RootElement.GetProperty("linuxPrincipals")[0];
        json.RootElement.GetProperty("kioskCredential").GetProperty("principalId").GetString().Should().Be(principal.GetProperty("id").GetString());
        principal.GetProperty("name").GetString().Should().Be("fixture-owner");
        (await seed.CheckAsync(context,default)).Change.Should().Be(StepChange.Blocked);
        machine.Machine.DeleteFile(layout.IdentityFile);
        (await new KioskCommissioningClaimStep(layout).CheckAsync(context,default)).Change.Should().Be(StepChange.Blocked);
        var retry=new KioskCommissioningSeedStep(layout,Settings(),new KioskCommissioningClaimStep(layout));
        await Assert.ThrowsExactlyAsync<InstallerException>(()=>retry.ApplyAsync(context,new(StepChange.Create),default));
    }

    [TestMethod]
    public async Task ExistingDataServiceOwnerMultipleHumansAndFailedFlushAreRefused()
    {
        using var machine=new FakeMachine().WithPi(); var runner=new Runner(machine);
        var context=await Context(machine,runner); var layout=ControllerLayout.System;
        var claim=new KioskCommissioningClaimStep(layout);
        (await new KioskCommissioningSeedStep(layout,Settings(KioskSteps.User),claim).CheckAsync(context,default)).Change.Should().Be(StepChange.Blocked);
        var many=Settings() with {Users=[..Settings().Users, new(){Name="fixture-other",Role=RoofControllerApiContract.OperatorRole}]};
        (await new KioskCommissioningSeedStep(layout,many,claim).CheckAsync(context,default)).Change.Should().Be(StepChange.Blocked);
        runner.FailSync=true;
        await Assert.ThrowsExactlyAsync<InstallerException>(()=>claim.ApplyAsync(context,new(StepChange.Create),default));
        claim.Claimed.Should().BeFalse();
        (await new KioskCommissioningClaimStep(layout).CheckAsync(context,default)).Change.Should().Be(StepChange.Blocked);
        machine.Exists(layout.IdentityFile).Should().BeFalse();
        using var existing=new FakeMachine().WithPi();
        existing.Folder(layout.Data);
        var existingContext=await Context(existing,new Runner(existing));
        (await new KioskCommissioningClaimStep(layout).CheckAsync(existingContext,default)).Change.Should().Be(StepChange.Blocked);
    }
}
