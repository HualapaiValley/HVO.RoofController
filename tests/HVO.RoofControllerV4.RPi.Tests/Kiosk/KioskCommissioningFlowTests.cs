using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Screens;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

[TestClass, DoNotParallelize]
public sealed class KioskCommissioningFlowTests
{
    public TestContext TestContext { get; set; } = null!;
    private static async Task UiTask(Func<Task> action)
    {
        var pending = await OnUiAsync(action);
        await UntilAsync(() => pending.IsCompleted, "commissioning UI action");
        await pending;
    }
    private const string Owner = "11111111111111111111111111111111";
    private static void Seed(string path)
    {
        var stamp = "22222222222222222222222222222222";
        new RoofIdentityFile(path).Save(new RoofIdentityDocument
        {
            SchemaVersion = 3,
            LinuxPrincipals = [new(Owner, "33333333-3333-4333-8333-333333333333", "fixture-owner",
                RoofControllerApiContract.AdminRole, "44444444-4444-4444-8444-444444444444", stamp, true)],
            KioskCredential = new(Owner, stamp, "fixture-stamp", "pending", null, KioskHarness.Start, KioskHarness.Start.AddHours(24))
        });
    }

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task SetupStaysLockedChooseConfirmRequiresFreshLoginAndPreservesStop(int width, int height, double scale)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json"); Seed(path);
        var socket = directory.File("native.sock");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.ListenUnixSocket(socket));
        await using var worker = builder.Build();
        worker.MapPost("/v1/revalidate", () => HVO.RoofControllerV4.LinuxAuth.Program.RevalidationResult(new(true, "fixture-owner")));
        await worker.StartAsync();
        await using var harness = await KioskHarness.CreateAsync(settings: new Dictionary<string,string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"] = path,
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true",
            ["RoofControllerSecurity:Identity:LinuxAuth:SocketPath"] = socket
        }, populateUsers:false);
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness,width,height,scale);
        var pad = screen.Shell.PinPad;
        await UiTask(() => pad.LoadUsersAsync());
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Unlock));
        await OnUiAsync(() => pad.Select(Owner));
        async Task Enter(string pin) => await UiTask(async () =>
        {
            foreach (var digit in pin) pad.Press(digit);
            await pad.UnlockAsync();
        });
        await Enter("0000");
        pad.ChoosingPin.Should().BeTrue(pad.Error);
        harness.Console.View.UnlockedBy.Should().BeNull();
        await screen.RenderAsync("commissioning-choose", TestContext);
        await Enter(TestSecrets.Pin);
        pad.ActionLabel.Should().Be("Confirm PIN");
        await screen.RenderAsync("commissioning-confirm", TestContext);
        await Enter(TestSecrets.OtherPin);
        pad.ActionLabel.Should().Be("Choose PIN");
        pad.Error.Should().Contain("did not match");
        await Enter(TestSecrets.Pin);
        await Enter(TestSecrets.Pin);
        pad.ChoosingPin.Should().BeFalse();
        harness.Console.View.UnlockedBy.Should().BeNull();
        pad.Error.Should().Contain("Sign in with your new PIN");
        await screen.RenderAsync("commissioning-saved", TestContext);
        await Enter("0000");
        harness.Console.View.UnlockedBy.Should().BeNull();
        await Enter(TestSecrets.Pin);
        await harness.WaitForAsync(v => v.UnlockedBy == "fixture-owner", "owner PIN login");
        var store = harness.Host.Services.GetRequiredService<RoofIdentityStore>();
        store.ActiveSessions.Should().HaveCount(1);
        using var client = harness.Host.CreateApiClient();
        // The client library installed only the personal session; setup never installed one.
        await OnUiAsync(() => screen.Visible("nav-settings").Should().BeFalse());
        await OnUiAsync(() => screen.Shell.RoofPage.Open.IsEffectivelyVisible.Should().BeTrue());
        harness.Calls("Open").Should().Be(0);
    }

    [TestMethod]
    public async Task CompletedBearerObservesNativeRevocationAndNeverRevives()
    {
        using var directory=new TemporaryDirectory();
        var path=directory.File("identity.json"); Seed(path);
        var socket=directory.File("native.sock"); bool? valid=true;
        var builder=WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o=>o.ListenUnixSocket(socket));
        await using var worker=builder.Build();
        worker.MapPost("/v1/revalidate",()=>HVO.RoofControllerV4.LinuxAuth.Program.RevalidationResult(new(valid,valid==true?"fixture-owner":null)));
        await worker.StartAsync();
        await using var harness=await KioskHarness.CreateAsync(settings:new Dictionary<string,string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"]=path,
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"]="true",
            ["RoofControllerSecurity:Identity:LinuxAuth:SocketPath"]=socket
        },populateUsers:false);
        var setup=await harness.Client.Auth.SignInWithPrincipalPinAsync(Owner,"0000");
        setup.Session.Should().BeNull();
        await harness.Client.Auth.CompleteKioskSetupAsync(setup.SetupToken!,TestSecrets.Pin,TestSecrets.Pin);
        var login=await harness.Client.Auth.SignInWithPrincipalPinAsync(Owner,TestSecrets.Pin);
        login.Session!.Role.Should().Be(RoofControllerApiContract.OperatorRole);
        using var client=harness.Host.CreateApiClient();
        client.DefaultRequestHeaders.Authorization=new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",login.Session.Token);
        (await client.GetAsync("/api/v4.0/Auth/Me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v4.0/Identity/Users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        valid=null;
        (await client.GetAsync("/api/v4.0/Auth/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        valid=true;
        (await client.GetAsync("/api/v4.0/Auth/Me")).StatusCode.Should().Be(HttpStatusCode.OK);
        valid=false;
        (await client.GetAsync("/api/v4.0/Auth/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        valid=true;
        (await client.GetAsync("/api/v4.0/Auth/Me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var store=harness.Host.Services.GetRequiredService<RoofIdentityStore>();
        store.GetKioskCredential().Should().BeNull();
        store.ActiveSessions.Should().BeEmpty();
        await Assert.ThrowsAsync<Exception>(async()=>await harness.Client.Auth.SignInWithPrincipalPinAsync(Owner,TestSecrets.Pin));
        new RoofIdentityFile(path).Load().KioskCredential!.State.Should().Be("retired");
    }

    [TestMethod]
    public async Task WorkerUnavailableDoesNotRetireButConfirmedRevocationDoesPermanently()
    {
        using var directory = new TemporaryDirectory();
        var path=directory.File("identity.json"); Seed(path);
        var socket=directory.File("native.sock");
        bool? valid=null;
        var builder=WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o=>o.ListenUnixSocket(socket));
        await using var worker=builder.Build();
        worker.MapPost("/v1/revalidate",()=>HVO.RoofControllerV4.LinuxAuth.Program.RevalidationResult(new(valid, valid==true?"fixture-owner":null)));
        await worker.StartAsync();
        await using var harness=await KioskHarness.CreateAsync(settings:new Dictionary<string,string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"]=path,
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"]="true",
            ["RoofControllerSecurity:Identity:LinuxAuth:SocketPath"]=socket
        },populateUsers:false);
        var providers=harness.Host.Services.GetRequiredService<RoofProviderAuthService>();
        var store=harness.Host.Services.GetRequiredService<RoofIdentityStore>();
        providers.KioskPrincipalIsCurrent(Owner).Should().BeFalse();
        store.GetKioskCredential().Should().NotBeNull();
        valid=true;
        providers.KioskPrincipalIsCurrent(Owner).Should().BeTrue();
        valid=false;
        providers.KioskPrincipalIsCurrent(Owner).Should().BeFalse();
        store.GetKioskCredential().Should().BeNull();
        valid=true;
        store.GetKioskCredential().Should().BeNull();
        new RoofIdentityFile(path).Load().KioskCredential!.State.Should().Be("retired");
    }
}
