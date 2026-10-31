using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Screens;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// Changing your own PIN on the kiosk's PIN pad (#205), headless against the controller's real API: the person who
/// unlocked the kiosk with a PIN confirms the current PIN, then types the new one twice. The kiosk stays unlocked, a
/// wrong current PIN ends nothing, a new PIN that cannot be right is never sent, every PIN typed is forgotten on Cancel,
/// on another page and when the kiosk locks, and Stop stays whole on every step. The Mac app, signed in with a password,
/// never offers it.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class KioskPinChangeTests
{
    private const string NewPin = "8642";
    private const string ChangePath = "api/v4.0/Auth/Pin/Change";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task ThePersonWhoUnlocked_ChangesTheirPinOnThePad_AndTheKioskStaysUnlocked(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await OnUiAsync(() => screen.Visible("nav-change-pin").Should().BeTrue("the kiosk is unlocked with a PIN"));

        await screen.TouchAsync(screen.Find("nav-change-pin")!);

        await OnUiAsync(() =>
        {
            screen.Shell.Page.Should().Be(KioskPage.ChangePin);
            screen.Visible("pin-person").Should().BeFalse("the PIN changed is the unlocked person's own");
            screen.Text("pin-prompt").Should().Be("Change your PIN: first enter your current PIN.");
            screen.Text("pin-unlock").Should().Be("Next");
        });
        await TypeAsync(screen, TestSecrets.Pin);
        await OnUiAsync(() => screen.Text("pin-masked").Should().Be("●●●●", "a PIN is never shown"));
        await screen.RenderAsync("change-pin", TestContext);
        await screen.TouchAsync(screen.Find("pin-unlock")!);
        await OnUiAsync(() => screen.Text("pin-prompt").Should().Be("Choose your new four-digit PIN."));
        await TypeAsync(screen, NewPin);
        await screen.TouchAsync(screen.Find("pin-unlock")!);
        await OnUiAsync(() =>
        {
            screen.Text("pin-prompt").Should().Be("Enter your new PIN again to confirm.");
            screen.Text("pin-unlock").Should().Be("Change PIN");
        });
        await TypeAsync(screen, NewPin);
        await screen.RenderAsync("change-pin-confirm", TestContext);
        harness.Sent(HttpMethod.Post, ChangePath).Should().Be(0, "nothing is sent until the new PIN is confirmed");
        await screen.TouchAsync(screen.Find("pin-unlock")!);

        await UntilAsync(() => screen.Shell.PinPad.PinChanged, "the PIN changed", TimeSpan.FromSeconds(30));
        await OnUiAsync(() => screen.Text("pin-changed").Should().Be(RoofIdentityText.OwnPinChanged));
        await screen.RenderAsync("change-pin-done", TestContext);
        harness.Sent(HttpMethod.Post, ChangePath).Should().Be(1);
        harness.Console.View.UnlockedBy.Should().Be(KioskHarness.Operator, "changing your PIN never locks the kiosk");
        (await harness.Client.Auth.GetCallerAsync()).Kind.Should().Be(RoofCredentialKind.Pin, "the PIN session is kept");
        (await PinSignInAsync(harness, TestSecrets.Pin)).Should().Be(HttpStatusCode.Unauthorized, "the old PIN no longer unlocks");
        (await PinSignInAsync(harness, NewPin)).Should().Be(HttpStatusCode.OK);

        await screen.TouchAsync(screen.Find("pin-done")!);

        await OnUiAsync(() =>
        {
            screen.Shell.Page.Should().Be(KioskPage.Roof);
            screen.Shell.PinPad.ChangingPin.Should().BeFalse();
            screen.Shell.PinPad.PinChanged.Should().BeFalse();
            screen.Shell.PinPad.Length.Should().Be(0);
            screen.Visible("nav-change-pin").Should().BeTrue();
        });
        harness.Console.View.UnlockedBy.Should().Be(KioskHarness.Operator);
    }

    [TestMethod]
    public async Task AWrongCurrentPin_IsSaid_TheKioskStaysUnlocked_AndTryingAgainStartsOver()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, 1280, 720, 8.2);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.ChangePin));

        await EnterAsync(screen, TestSecrets.OtherPin, NewPin, NewPin);

        await UntilAsync(() => screen.Shell.PinPad.Error is not null, "the refusal");
        await OnUiAsync(() =>
        {
            screen.Text("pin-error").Should().Be(KioskPinPad.WrongCurrentPin);
            screen.Visible("pin-retry").Should().BeTrue();
            screen.Shell.PinPad.PinChanged.Should().BeFalse();
        });
        await screen.RenderAsync("change-pin-wrong", TestContext);
        harness.Console.View.UnlockedBy.Should().Be(KioskHarness.Operator, "a wrong current PIN ends no session");

        await screen.TouchAsync(screen.Find("pin-retry")!);
        await OnUiAsync(() =>
        {
            screen.Text("pin-prompt").Should().Be("Change your PIN: first enter your current PIN.", "trying again starts over");
            screen.Shell.PinPad.Length.Should().Be(0);
        });
        await EnterAsync(screen, TestSecrets.Pin, NewPin, NewPin);
        await UntilAsync(() => screen.Shell.PinPad.PinChanged, "the PIN changed", TimeSpan.FromSeconds(30));
        (await PinSignInAsync(harness, NewPin)).Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task ANewPinThatCannotBeRight_IsNeverSent_AndTwoThatDiffer_AreChosenAgain()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, 800, 480, 5.2);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.ChangePin));
        await TypeAsync(screen, TestSecrets.Pin);
        await screen.TouchAsync(screen.Find("pin-unlock")!);

        foreach (var (pin, problem) in new[]
        {
            ("0000", RoofIdentityText.PinRule),
            ("12345", RoofIdentityText.PinRule),
            (TestSecrets.Pin, "Choose a new PIN different from your current one.")
        })
        {
            await TypeAsync(screen, pin);
            await screen.TouchAsync(screen.Find("pin-unlock")!);
            await OnUiAsync(() => screen.Text("pin-error").Should().Be(problem));
            await screen.TouchAsync(screen.Find("pin-retry")!);
            await OnUiAsync(() => screen.Text("pin-prompt").Should().Be("Choose your new four-digit PIN.", "the current PIN is kept; the new one is asked again"));
        }

        await TypeAsync(screen, NewPin);
        await screen.TouchAsync(screen.Find("pin-unlock")!);
        await TypeAsync(screen, TestSecrets.OtherPin);
        await screen.TouchAsync(screen.Find("pin-unlock")!);

        await OnUiAsync(() => screen.Text("pin-error").Should().Be("The two PINs differ. Choose your new PIN again."));
        await screen.TouchAsync(screen.Find("pin-retry")!);
        await OnUiAsync(() => screen.Text("pin-prompt").Should().Be("Choose your new four-digit PIN."));
        harness.Sent(HttpMethod.Post, ChangePath).Should().Be(0, "a new PIN that cannot be right is refused before anything is sent");
        (await PinSignInAsync(harness, TestSecrets.Pin)).Should().Be(HttpStatusCode.OK, "nothing changed");
    }

    [TestMethod]
    public async Task Cancel_AnotherPage_AndLocking_ForgetEveryPinTyped()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, 1280, 720, 8.2);
        var pad = screen.Shell.PinPad;

        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.ChangePin));
        await TypeAsync(screen, "24");
        await screen.TouchAsync(screen.Find("pin-cancel")!);
        await OnUiAsync(() =>
        {
            screen.Shell.Page.Should().Be(KioskPage.Roof);
            pad.ChangingPin.Should().BeFalse();
            pad.Length.Should().Be(0, "Cancel forgets the digits typed");
        });

        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.ChangePin));
        await TypeAsync(screen, TestSecrets.Pin);
        await screen.TouchAsync(screen.Find("pin-unlock")!);
        await TypeAsync(screen, "86");
        await screen.TouchAsync(screen.Find("nav-readings")!);
        await OnUiAsync(() =>
        {
            pad.ChangingPin.Should().BeFalse("another page ends the change");
            pad.Length.Should().Be(0);
        });
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.ChangePin));
        await OnUiAsync(() => screen.Text("pin-prompt").Should().Be("Change your PIN: first enter your current PIN.", "nothing typed before is kept"));
        await TypeAsync(screen, TestSecrets.Pin);
        await screen.TouchAsync(screen.Find("pin-unlock")!);

        await harness.Console.LockAsync();
        await UntilAsync(() => screen.Shell.Page == KioskPage.Roof, "the locked kiosk's roof page");
        await OnUiAsync(() =>
        {
            pad.ChangingPin.Should().BeFalse("locking ends the change");
            pad.Length.Should().Be(0);
            screen.Visible("nav-change-pin").Should().BeFalse("only a kiosk unlocked with a PIN offers it");
            screen.Shell.ShowPage(KioskPage.ChangePin);
            screen.Shell.Page.Should().Be(KioskPage.Roof, "a locked kiosk has no PIN to change");
        });
        harness.Sent(HttpMethod.Post, ChangePath).Should().Be(0);
    }

    [TestMethod]
    public async Task ThePinPage_KeepsStopWhole_AndEveryKeyATouchTarget_OnEveryStep()
    {
        foreach (var (width, height, density) in new[] { (1280, 720, 8.2), (800, 480, 5.2) })
        {
            await using var harness = await KioskHarness.CreateAsync();
            await harness.StartLiveAsync();
            await harness.UnlockAsync(KioskHarness.Operator);
            await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
            await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.ChangePin));
            foreach (var step in new[] { TestSecrets.Pin, NewPin })
            {
                await TypeAsync(screen, step);
                await screen.CheckAsync($"change-pin {step.Length} digits at {width}x{height}");
                await OnUiAsync(() =>
                {
                    screen.OffScreen(name => name.StartsWith("pin-", StringComparison.Ordinal) || name.StartsWith("nav-", StringComparison.Ordinal))
                        .Should().BeEmpty();
                    screen.Shell.Stop.IsEffectivelyVisible.Should().BeTrue();
                    screen.Shell.Stop.IsEffectivelyEnabled.Should().BeTrue("Stop is never disabled, not while a PIN is changed");
                });
                await screen.TouchAsync(screen.Find("pin-unlock")!);
            }

            // Stop works from the change-PIN page, and the change goes on.
            await screen.TouchAsync(screen.Shell.Stop);
            await UntilAsync(() => harness.Calls("Stop") == 1, "Stop from the change-PIN page");
            await OnUiAsync(() => screen.Shell.Page.Should().Be(KioskPage.ChangePin));
        }
    }

    [TestMethod]
    public async Task TheMacApp_SignedInWithAPassword_NeverOffersIt()
    {
        await using var harness = await KioskHarness.CreateAsync(
            settings: new Dictionary<string, string?> { ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "false" },
            options: new KioskConsoleOptions { Wording = KioskWording.Desktop, Blanking = false });
        await harness.StartLiveAsync();
        await harness.PasswordAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, 1280, 800, KioskMetrics.DeskPixelsPerMillimetre);

        await OnUiAsync(() =>
        {
            screen.Visible("nav-change-pin").Should().BeFalse("the app signs in with a password: PINs are the kiosk's");
            screen.Shell.ShowPage(KioskPage.ChangePin);
            screen.Shell.Page.Should().Be(KioskPage.Roof);
            screen.Shell.PinPad.ChangingPin.Should().BeFalse();
        });
    }

    [TestMethod]
    public async Task TheOwnerOfTheCommissionedPin_ChangesItOnThePad()
    {
        const string owner = "11111111111111111111111111111111";
        const string stamp = "22222222222222222222222222222222";
        using var directory = new TemporaryDirectory();
        var path = directory.File("identity.json");
        using (var hasher = IdentityRig.CreateHasher(1_000))
        {
            new RoofIdentityFile(path).Save(new RoofIdentityDocument
            {
                SchemaVersion = 3,
                LinuxPrincipals = [new(owner, "33333333-3333-4333-8333-333333333333", "fixture-owner",
                    RoofControllerApiContract.AdminRole, "44444444-4444-4444-8444-444444444444", stamp, true)],
                KioskCredential = new(owner, stamp, "fixture-stamp", "complete", await hasher.HashAsync(TestSecrets.Pin, default),
                    KioskHarness.Start, KioskHarness.Start.AddHours(24))
            }).Should().BeTrue();
        }

        // A fake native worker that says the Linux account is current; no native account or PAM is involved.
        var socket = directory.File("native.sock");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socket));
        await using var worker = builder.Build();
        worker.MapPost("/v1/revalidate", () => HVO.RoofControllerV4.LinuxAuth.Program.RevalidationResult(new(true, "fixture-owner")));
        await worker.StartAsync();
        await using var harness = await KioskHarness.CreateAsync(settings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"] = path,
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true",
            ["RoofControllerSecurity:Identity:LinuxAuth:SocketPath"] = socket
        }, populateUsers: false);
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, 1280, 720, 8.2);
        var pad = screen.Shell.PinPad;
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Unlock));
        await UntilAsync(() => pad.Users.Count == 1, "the commissioned owner");
        await OnUiAsync(() => pad.Select(owner));
        await TypeAsync(screen, TestSecrets.Pin);
        await screen.TouchAsync(screen.Find("pin-unlock")!);
        await harness.WaitForAsync(view => view.UnlockedBy == "fixture-owner", "the owner's PIN session");

        await screen.TouchAsync(screen.Find("nav-change-pin")!);
        await EnterAsync(screen, TestSecrets.Pin, NewPin, NewPin);

        await UntilAsync(() => pad.PinChanged, "the commissioned PIN changed", TimeSpan.FromSeconds(30));
        harness.Console.View.UnlockedBy.Should().Be("fixture-owner");
        using var kiosk = harness.Host.CreateApiClient(RoofClientApiTests.KioskKey);
        (await kiosk.PostAsJsonAsync("/api/v4.0/Auth/KioskPin", new RoofKioskPinRequest(owner, TestSecrets.Pin)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the old commissioned PIN no longer unlocks");
        (await kiosk.PostAsJsonAsync("/api/v4.0/Auth/KioskPin", new RoofKioskPinRequest(owner, NewPin)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public void AFailedChange_IsSaidInTheKiosksWords()
    {
        KioskPinPad.DescribeChangeFailure(new RoofApiException(HttpStatusCode.Unauthorized, RoofControllerErrorCode.SignInFailed, detail: "The current PIN is not correct."))
            .Should().Be(KioskPinPad.WrongCurrentPin);
        KioskPinPad.DescribeChangeFailure(new RoofApiException((HttpStatusCode)429, RoofControllerErrorCode.SignInLockedOut))
            .Should().StartWith(KioskPinPad.PinNotChanged).And.Contain("Too many failed sign-ins");
        KioskPinPad.DescribeChangeFailure(new RoofApiException(HttpStatusCode.BadRequest, RoofControllerErrorCode.InvalidRequest, detail: "You have no PIN to change."))
            .Should().Be($"{KioskPinPad.PinNotChanged} You have no PIN to change.");
        KioskPinPad.DescribeChangeFailure(new TimeoutException())
            .Should().EndWith(KioskPinPad.PinMayHaveChanged, "an answer that never came may hide a change");
    }

    /// <summary>Types <paramref name="pin"/> on the drawn keypad, a touch on each digit.</summary>
    private static async Task TypeAsync(KioskScreen screen, string pin)
    {
        foreach (var digit in pin)
        {
            await screen.TouchAsync(screen.Find($"pin-digit-{digit}")!);
        }
    }

    /// <summary>The whole change on the keypad: the current PIN, the new one, and the new one again.</summary>
    private static async Task EnterAsync(KioskScreen screen, string current, string replacement, string again)
    {
        foreach (var pin in new[] { current, replacement, again })
        {
            await TypeAsync(screen, pin);
            await screen.TouchAsync(screen.Find("pin-unlock")!);
        }
    }

    /// <summary>How the controller answers a PIN sign-in as the operator at this kiosk, with the device key alone.</summary>
    private static async Task<HttpStatusCode> PinSignInAsync(KioskHarness harness, string pin)
    {
        using var kiosk = harness.Host.CreateApiClient(RoofClientApiTests.KioskKey);
        var response = await kiosk.PostAsJsonAsync("/api/v4.0/Auth/Pin", new RoofPinSignInRequest { Name = KioskHarness.Operator, Pin = pin });
        return response.StatusCode;
    }
}
