using System.Net.Sockets;
using System.Security.Cryptography;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Web.Components;
using HVO.RoofControllerV4.Web.Components.Pages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The "Your names" page (#209): a person signed in with a password changes their own display and short name, with a
/// keyboard, through the real in-process API; a Linux account's names come from the operating system and are shown
/// read-only.
/// </summary>
[TestClass]
public sealed class YourNamesPageTests
{
    private const string NamesPath = "api/v4.0/Auth/Names";

    [TestMethod]
    public async Task APerson_SavesAndClearsTheirOwnNames_AndStaysSignedIn()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        cut.Find("[data-testid=names-display-name]").GetAttribute("value").Should().BeEmpty();

        cut.Input("[data-testid=names-display-name]", "  Ada Example ");
        cut.Input("[data-testid=names-short-name]", "Ada");
        cut.Click("[data-testid=names-save]");

        cut.WaitForAssertion(() => Message(cut).Should().Be("Your names were saved."));
        cut.Find("[data-testid=names-display-name]").GetAttribute("value").Should().Be("Ada Example", "the page shows the names as kept");
        using var admin = harness.Admin();
        var saved = await admin.Identity.GetUserAsync("ada");
        saved.DisplayName.Should().Be("Ada Example");
        saved.ShortName.Should().Be("Ada");

        cut.Input("[data-testid=names-short-name]", "");
        cut.Click("[data-testid=names-save]");

        // The click took the first save's result away; it comes back only when this save has been made. (The short name
        // is empty as soon as it is typed, so it says nothing about the save.)
        cut.WaitForAssertion(() => Message(cut).Should().Be("Your names were saved."));
        cut.Find("[data-testid=names-short-name]").GetAttribute("value").Should().BeEmpty();
        var cleared = await admin.Identity.GetUserAsync("ada");
        cleared.DisplayName.Should().Be("Ada Example");
        cleared.ShortName.Should().BeNull("an empty name removes it");
        session.IsOpen.Should().BeTrue("changing your names ends no session");
        (await session.Client.Auth.GetCallerAsync()).DisplayName.Should().Be("Ada Example");
    }

    [TestMethod]
    public async Task ASecondSave_ShowsNoResult_UntilItsOwnAnswerComes()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        cut.Input("[data-testid=names-display-name]", "Ada Example");
        cut.Click("[data-testid=names-save]");
        cut.WaitForAssertion(() => Message(cut).Should().Be("Your names were saved."));
        var answer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.HoldAnswer = request => request.RequestUri!.AbsolutePath.TrimStart('/') == NamesPath ? answer.Task : Task.CompletedTask;

        cut.Input("[data-testid=names-short-name]", "Ada");
        cut.Click("[data-testid=names-save]");

        cut.FindAll("[data-testid=page-message]").Should().BeEmpty("the first save's result is not this save's");
        cut.Find("[data-testid=names-save]").HasAttribute("disabled").Should().BeTrue("the save is on its way");
        answer.SetResult();
        cut.WaitForAssertion(() => Message(cut).Should().Be("Your names were saved."));
        using var admin = harness.Admin();
        (await admin.Identity.GetUserAsync("ada")).ShortName.Should().Be("Ada");
    }

    [TestMethod]
    public async Task AnInvalidName_IsRefusedBeforeAnythingIsSent()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.OperatorRole);
        var sent = 0;
        harness.LoseAnswer = request =>
        {
            if (request.RequestUri!.AbsolutePath.TrimStart('/') == NamesPath)
            {
                Interlocked.Increment(ref sent);
            }

            return false;
        };
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        cut.Find("[data-testid=names-short-name]").Input("A short name too long");
        cut.Click("[data-testid=names-save]");

        cut.WaitForAssertion(() => Message(cut).Should().Be(RoofIdentityText.ShortNameRule));
        cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Danger));
        sent.Should().Be(0);
    }

    [TestMethod]
    public async Task ASaveWhoseAnswerIsLost_SaysTheNamesWereNotChanged_AndWhy()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.OperatorRole);
        harness.LoseAnswer = request => request.RequestUri!.AbsolutePath.TrimStart('/') == NamesPath;
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        cut.Find("[data-testid=names-display-name]").Input("Ada Example");
        cut.Click("[data-testid=names-save]");

        cut.WaitForAssertion(() => Message(cut).Should().Be($"Your names were not changed. {RoofText.Unreachable}"));
        cut.Find("[data-testid=page-message]").GetAttribute("data-level").Should().Be(nameof(WebMessageLevel.Danger));
    }

    [TestMethod]
    public async Task ALinuxAccountsNames_AreTheOperatingSystems_AndReadOnly()
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
            assertion: new StoredAssertionNonce(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddSeconds(30)),
            osDisplayName: "Fixture Owner").Value!;
        var session = harness.Store.Open(new RoofSessionCredential(
            issued.Token, "fixture", RoofControllerApiContract.OperatorRole, issued.Session.Id, issued.Session.ExpiresUtc));
        await using var context = harness.Context(session);

        var cut = context.Render<YourNames>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=names-read-only]").TextContent.Should().Contain(RoofIdentityText.LinuxNamesReadOnly));
        cut.Find("[data-testid=names-display-name]").TextContent.Should().Be("Fixture Owner");
        cut.Find("[data-testid=names-short-name]").TextContent.Should().Be("fixture");
        cut.FindAll("input").Should().BeEmpty("a Linux account's names are changed on the controller's machine, not here");
        cut.FindAll("[data-testid=names-save]").Should().BeEmpty();
    }

    private static IRenderedComponent<YourNames> Loaded(BunitContext context)
    {
        var cut = context.Render<YourNames>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=names-form]").Should().NotBeEmpty(
            cut.FindAll("[data-testid=page-message]").FirstOrDefault()?.TextContent ?? "the account to be read"));
        return cut;
    }

    private static string Message(IRenderedComponent<YourNames> cut)
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
