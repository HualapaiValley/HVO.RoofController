using System.Net.Sockets;
using System.Security.Cryptography;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Web.Components;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The "Your PIN" page (#205): a person signed in with a password changes their own kiosk PIN through the real
/// in-process API, confirming the current one. This session stays signed in; their PIN sessions at the kiosks end. A
/// person without a PIN, and a Linux account, are told why there is nothing to change.
/// </summary>
[TestClass]
public sealed class YourPinPageTests
{
    private const string PinChangePath = "api/v4.0/Auth/Pin/Change";
    private const string KioskKey = "test-kiosk-key-not-a-real-secret-05";
    private const string NewPin = "8642";

    [TestMethod]
    public async Task APerson_ChangesTheirPin_StaysSignedIn_AndTheirPinSessionsEnd()
    {
        using var harness = Harness();
        var session = await SignInWithPinAsync(harness, "olive");
        using var kiosk = Kiosk(harness);
        await kiosk.Auth.SignInWithPinAsync("olive", TestSecrets.Pin);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        cut.Find("[data-testid=pin-form]").TextContent.Should().Contain(RoofIdentityText.OwnPinChangeEndsSessions);

        Change(cut, TestSecrets.Pin, NewPin, NewPin);

        cut.WaitForAssertion(() => Message(cut).Should().Be(RoofIdentityText.OwnPinChanged));
        cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Success));
        FieldsAreEmpty(cut);
        session.IsOpen.Should().BeTrue("changing your PIN ends no password session");
        using var admin = harness.Admin();
        (await admin.Identity.GetSessionsAsync()).Where(open => open.Name == "olive").Select(open => open.Kind)
            .Should().Equal([RoofCredentialKind.Session], "the PIN session at the kiosk ended; this one did not");
        using var again = Kiosk(harness);
        (await again.Auth.SignInWithPinAsync("olive", NewPin)).Name.Should().Be("olive", "the new PIN unlocks a kiosk");
    }

    [TestMethod]
    public async Task AWrongCurrentPin_SaysSo_AndRepeatedOnesLockThePin()
    {
        using var harness = Harness();
        var session = await SignInWithPinAsync(harness, "olive");
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        Change(cut, TestSecrets.OtherPin, NewPin, NewPin);

        // Each change takes the last result away, so a result is this change's own.
        cut.WaitForAssertion(() => Message(cut).Should().Be(YourPin.WrongCurrentPin));
        cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Danger));
        FieldsAreEmpty(cut);
        for (var i = 0; i < 3; i++)
        {
            Change(cut, TestSecrets.OtherPin, NewPin, NewPin);
            cut.WaitForAssertion(() => Message(cut).Should().Be(YourPin.WrongCurrentPin));
        }

        Change(cut, TestSecrets.OtherPin, NewPin, NewPin);

        cut.WaitForAssertion(() => Message(cut).Should().Be(YourPin.LockedOut));
        session.IsOpen.Should().BeTrue("a wrong PIN is not a refused session");
        using var kiosk = Kiosk(harness);
        (await RoofClientApiTests.RefusedAsync(() => kiosk.Auth.SignInWithPinAsync("olive", TestSecrets.Pin)))
            .Code.Should().Be(RoofControllerErrorCode.SignInLockedOut, "the lockout is the PIN sign-in's");
    }

    [TestMethod]
    [DataRow("", NewPin, NewPin, "Enter your current PIN.")]
    [DataRow(TestSecrets.Pin, "", "", "Enter your new PIN.")]
    [DataRow(TestSecrets.Pin, "12", "12", "PinRule")]
    [DataRow(TestSecrets.Pin, "0000", "0000", "PinRule")]
    [DataRow(TestSecrets.Pin, NewPin, "1111", "The two PINs differ.")]
    [DataRow(TestSecrets.Pin, TestSecrets.Pin, TestSecrets.Pin, "Choose a new PIN different from your current one.")]
    public async Task AChangeThatCannotBeRight_IsNotSent(string current, string pin, string again, string message)
    {
        using var harness = Harness();
        var session = await SignInWithPinAsync(harness, "olive");
        var sent = 0;
        harness.LoseAnswer = request =>
        {
            if (request.RequestUri!.AbsolutePath.TrimStart('/') == PinChangePath)
            {
                Interlocked.Increment(ref sent);
            }

            return false;
        };
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        Change(cut, current, pin, again);

        cut.WaitForAssertion(() => Message(cut).Should().Be(message == "PinRule" ? RoofIdentityText.PinRule : message));
        cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Danger));
        sent.Should().Be(0);
    }

    [TestMethod]
    public async Task AChangeOnItsWay_ShowsNoEarlierResult_UntilItsOwnAnswerComes()
    {
        using var harness = Harness();
        var session = await SignInWithPinAsync(harness, "olive");
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        Change(cut, TestSecrets.OtherPin, NewPin, NewPin);
        cut.WaitForAssertion(() => Message(cut).Should().Be(YourPin.WrongCurrentPin));
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.HoldAnswer = request => request.RequestUri!.AbsolutePath.TrimStart('/') == PinChangePath ? answer.Task : Task.CompletedTask;

        Change(cut, TestSecrets.Pin, NewPin, NewPin);

        cut.FindAll("[data-testid=page-message]").Should().BeEmpty("the last change's result is not this change's");
        cut.Find("[data-testid=pin-change]").HasAttribute("disabled").Should().BeTrue("the change is on its way");
        answer.SetResult();
        cut.WaitForAssertion(() => Message(cut).Should().Be(RoofIdentityText.OwnPinChanged));
    }

    [TestMethod]
    public async Task AChangeWhoseAnswerIsLost_SaysThePinMayHaveChanged()
    {
        using var harness = Harness();
        var session = await SignInWithPinAsync(harness, "olive");
        harness.LoseAnswer = request => request.RequestUri!.AbsolutePath.TrimStart('/') == PinChangePath;
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        Change(cut, TestSecrets.Pin, NewPin, NewPin);

        cut.WaitForAssertion(() => Message(cut).Should().Be($"{RoofText.Unreachable} {YourPin.MayHaveChanged}"));
        cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Warning));
        FieldsAreEmpty(cut);
    }

    [TestMethod]
    public async Task APersonWithoutAPin_IsToldWhoGivesTheFirstOne()
    {
        using var harness = Harness();
        var session = await harness.SignInAsync("paula", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);

        var cut = context.Render<YourPin>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pin-none]").TextContent.Should().Contain(RoofIdentityText.NoPinYet));
        cut.FindAll("input").Should().BeEmpty();
        cut.FindAll("[data-testid=pin-change]").Should().BeEmpty();
    }

    [TestMethod]
    public async Task AViewer_IsToldOnlyOperatorsAndAdminsHaveAPin()
    {
        using var harness = Harness();
        var session = await harness.SignInAsync("vera", RoofControllerApiContract.ViewerRole);
        await using var context = harness.Context(session);

        var cut = context.Render<YourPin>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pin-viewer]").TextContent.Should().Contain(YourPin.ViewerHasNoPin));
        cut.FindAll("input").Should().BeEmpty();
    }

    [TestMethod]
    public async Task ALinuxAccount_HasNoPinToChange()
    {
        if (!Socket.OSSupportsUnixDomainSockets) Assert.Inconclusive("The native worker needs Unix sockets.");
        using var directory = new TemporaryDirectory();
        var socketPath = directory.File("worker.sock");
        await using var worker = await StartWorkerAsync(socketPath);
        using var harness = new WebAdminHarness(new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:Identity:StorePath"] = directory.File("identity.json"),
            ["RoofControllerSecurity:Identity:LinuxAuth:Enabled"] = "true",
            ["RoofControllerSecurity:Identity:LinuxAuth:SocketPath"] = socketPath
        });
        var store = harness.Host.Services.GetRequiredService<RoofIdentityStore>();
        var enrollment = new RoofPrincipalEnrollRequest
        {
            Provider = "linux",
            Subject = Guid.NewGuid().ToString("N"),
            Name = "fixture",
            EnrollmentStamp = Guid.NewGuid().ToString("N"),
            Role = RoofControllerApiContract.OperatorRole
        };
        var principal = store.EnrollLinux(enrollment).Value!;
        var linux = store.FindLinux(enrollment.Subject)!;
        var issued = store.CreateProviderSession(principal.Id, linux.Stamp,
            assertion: new StoredAssertionNonce(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddSeconds(30))).Value!;
        var session = harness.Store.Open(new RoofSessionCredential(
            issued.Token, "fixture", RoofControllerApiContract.OperatorRole, issued.Session.Id, issued.Session.ExpiresUtc));
        await using var context = harness.Context(session);

        var cut = context.Render<YourPin>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pin-linux]").TextContent.Should().Contain(RoofIdentityText.LinuxHasNoPin));
        cut.FindAll("input").Should().BeEmpty("a Linux account has no PIN here to change");
    }

    /// <summary>A harness whose controller has a kiosk key, so people can sign in with their PIN at a kiosk.</summary>
    private static WebAdminHarness Harness() => new(new Dictionary<string, string?>
    {
        ["RoofControllerSecurity:ApiKeys:4:Name"] = "test-kiosk",
        ["RoofControllerSecurity:ApiKeys:4:Role"] = RoofControllerApiContract.ViewerRole,
        ["RoofControllerSecurity:ApiKeys:4:Key"] = KioskKey,
        ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "true"
    });

    /// <summary>An operator with a password and <see cref="TestSecrets.Pin"/>, signed in to the web UI with the password.</summary>
    private static async Task<WebSession> SignInWithPinAsync(WebAdminHarness harness, string name)
    {
        var session = await harness.SignInAsync(name, RoofControllerApiContract.OperatorRole);
        using var admin = harness.Admin();
        await admin.Identity.SetPinAsync(name, TestSecrets.Pin);
        return session;
    }

    private static RoofControllerClient Kiosk(WebAdminHarness harness)
        => ClientTestSupport.CreateClient(harness.Host, new RoofKioskCredential(KioskKey));

    private static IRenderedComponent<YourPin> Loaded(BunitContext context)
    {
        var cut = context.Render<YourPin>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pin-form]").Should().NotBeEmpty(
            cut.FindAll("[data-testid=page-message]").FirstOrDefault()?.TextContent ?? "the account to be read"));
        return cut;
    }

    private static void Change(IRenderedComponent<YourPin> cut, string current, string pin, string again)
    {
        cut.Change("[data-testid=pin-current]", current);
        cut.Change("[data-testid=pin-new]", pin);
        cut.Change("[data-testid=pin-again]", again);
        cut.Submit("[data-testid=pin-change-form]");
    }

    private static void FieldsAreEmpty(IRenderedComponent<YourPin> cut)
    {
        foreach (var field in new[] { "pin-current", "pin-new", "pin-again" })
        {
            cut.Find($"[data-testid={field}]").GetAttribute("value").Should().BeNullOrEmpty($"the {field} field is forgotten after a send");
        }
    }

    private static string Message(IRenderedComponent<YourPin> cut)
        => string.Join(' ', cut.Find("[data-testid=page-message]").QuerySelectorAll("p").Select(line => line.TextContent));

    /// <summary>A fake native worker: revalidation always current. No native PAM or account is involved.</summary>
    private static async Task<WebApplication> StartWorkerAsync(string socketPath)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socketPath));
        var worker = builder.Build();
        worker.MapPost("/v1/revalidate", () => HVO.RoofControllerV4.LinuxAuth.Program.RevalidationResult(new(true, "fixture")));
        worker.MapPost("/v1/profile", () => Results.Json(new { fullName = "Fixture Owner" }));
        await worker.StartAsync();
        return worker;
    }
}
