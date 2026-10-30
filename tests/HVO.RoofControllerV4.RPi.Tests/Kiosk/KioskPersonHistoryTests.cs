using Avalonia.Controls;
using Avalonia.VisualTree;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Screens;
using Moq;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// #208's people and History pages, drawn headless against the controller's real API: the people step's read-only person
/// cards (names, role, login type, short name, last sign-in), which only choose; and History, offered only while someone
/// has unlocked the kiosk (or signed in to the app), with their details and their own recent roof commands (#206).
/// Nothing on either page edits a name: names change in the web UI and the terminal only.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class KioskPersonHistoryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void APersonWithAPin_IsDescribedByNameRoleLoginTypeShortNameAndLastSignIn()
    {
        var signedIn = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var ada = KioskPerson.Describe(new RoofPinUserResponse("ada", RoofControllerApiContract.AdminRole));
        ada.Heading.Should().Be("ada");
        ada.Chips.Should().Equal(new KioskPersonChip("Admin", KioskNoticeLevel.Info), new KioskPersonChip("Local account"));
        ada.Details.Should().Equal($"Last sign-in: {RoofIdentityText.NoSignInRecorded}");

        var olga = KioskPerson.Describe(new RoofPinUserResponse("olga", RoofControllerApiContract.OperatorRole)
        {
            DisplayName = "Olga Petrova", LastLoginUtc = signedIn
        });
        olga.Heading.Should().Be("Olga Petrova (olga)", "the display name, with the name they sign in by, as the PIN prompt says");
        olga.Details.Should().Equal($"Last sign-in: {RoofStatusText.Time(signedIn)}");

        var roy = KioskPerson.Describe(new RoofPinUserResponse("roys", RoofControllerApiContract.OperatorRole)
        {
            Provider = "linux", PrincipalId = "p2", DisplayName = "Roy Salisbury", ShortName = "Roy", SetupPending = true
        });
        roy.Heading.Should().Be("Roy Salisbury (roys)");
        roy.Chips.Select(chip => chip.Text).Should().Equal("Operator", "Linux account", "First-time setup");
        roy.Chips[2].Level.Should().Be(KioskNoticeLevel.Warning, "a setup still to finish stands out");
        roy.Details.Should().Equal("Short name: Roy", $"Last sign-in: {RoofIdentityText.NoSignInRecorded}");
        roy.ToString().Should().Be($"Roy Salisbury (roys) · Operator · Linux account · First-time setup · Short name: Roy · Last sign-in: {RoofIdentityText.NoSignInRecorded}");
    }

    [TestMethod]
    public void TheSignedInPerson_IsDescribedWithHowTheySignedIn_AndWhereTheirNamesAreChanged()
    {
        var olga = KioskPerson.Describe(new RoofCallerResponse("olga", RoofControllerApiContract.OperatorRole, RoofCredentialKind.Pin, "kiosk-1", "s1", null, false)
        {
            DisplayName = "Olga Petrova", ShortName = "Olga", LoginType = RoofLoginTypes.Local
        });
        olga.Heading.Should().Be("Olga Petrova (olga)");
        olga.Chips.Select(chip => chip.Text).Should().Equal("Operator", "Local account", "Signed in with a PIN");
        olga.Details.Should().Equal("Short name: Olga");
        olga.Note.Should().Be(RoofIdentityText.OwnNamesWhere, "names are changed in the web UI or the terminal, never at the kiosk");

        var roy = KioskPerson.Describe(new RoofCallerResponse("roys", RoofControllerApiContract.AdminRole, RoofCredentialKind.Session, null, "s2", null, false)
        {
            LoginType = RoofLoginTypes.Linux
        });
        roy.Chips.Select(chip => chip.Text).Should().Equal("Admin", "Linux account", "Signed in with a password");
        roy.Details.Should().BeEmpty();
        roy.Note.Should().Be(RoofIdentityText.LinuxNamesReadOnly);
    }

    [TestMethod]
    [DataRow(1920, 1200, 9.0)]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task ThePeopleStep_ShowsAReadOnlyCardPerPerson_ThatOnlyChooses(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await NameOlgaAsync(harness);
        await harness.StartLiveAsync();
        // Olga has signed in once, so her card has a last sign-in; Ada has not.
        await harness.UnlockAsync(KioskHarness.Operator);
        await harness.Console.LockAsync();
        await harness.WaitForAsync(view => !view.IsUnlocked, "the kiosk locked again");
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);

        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Unlock));
        await UntilAsync(() => screen.Shell.PinPad.Users.Count == 2 && screen.Find($"pin-user-{KioskHarness.Operator}") is not null, "the people with a PIN");
        await screen.RenderAsync("person-people", TestContext);
        await OnUiAsync(() =>
        {
            screen.Text($"pin-user-{KioskHarness.Operator}").Should().Be($"Olga Example ({KioskHarness.Operator})");
            Texts(screen, $"pin-user-{KioskHarness.Operator}").Should().Equal(
                $"Olga Example ({KioskHarness.Operator})", "Operator", "Local account", "Short name: Olga",
                $"Last sign-in: {RoofStatusText.Time(KioskHarness.Start)}");
            Texts(screen, $"pin-user-{KioskHarness.Admin}").Should().Equal(
                KioskHarness.Admin, "Admin", "Local account", $"Last sign-in: {RoofIdentityText.NoSignInRecorded}");
            screen.OffScreen(name => name.StartsWith("pin-user-", StringComparison.Ordinal)).Should().BeEmpty("both people fit");
            ReadOnly(screen, "pin-people-cancel", $"pin-user-{KioskHarness.Operator}", $"pin-user-{KioskHarness.Admin}");
            var olga = screen.Find($"pin-user-{KioskHarness.Operator}")!;
            var ada = screen.Find($"pin-user-{KioskHarness.Admin}")!;
            olga.Bounds.Height.Should().Be(ada.Bounds.Height, "the cards in a row are one height");
        });

        // Choosing a card only chooses: the keypad for that person.
        await screen.TouchAsync(screen.Find($"pin-user-{KioskHarness.Operator}")!);
        await OnUiAsync(() =>
        {
            screen.Shell.PinPad.Selected.Should().Be(KioskHarness.Operator);
            screen.Text("pin-prompt").Should().Be($"PIN for Olga Example ({KioskHarness.Operator}): four digits.");
        });
    }

    [TestMethod]
    [DataRow(1920, 1200, 9.0)]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task History_IsOfferedOnlyWhileUnlocked_AndShowsThePersonAndTheirOwnRecentCommands(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        await NameOlgaAsync(harness);
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await OnUiAsync(() =>
        {
            screen.Visible("nav-history").Should().BeFalse("nobody has unlocked the kiosk");
            screen.Shell.ShowPage(KioskPage.History);
            screen.Shell.Page.Should().Be(KioskPage.Roof, "History needs someone to have unlocked the kiosk");
        });
        harness.Sent(HttpMethod.Get, RoofApiRoutes.Me).Should().Be(0);

        // Someone else's command is not the person's.
        using (var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin)))
        {
            await admin.StopAsync();
        }

        await harness.UnlockAsync(KioskHarness.Operator);
        await SendOlgasCommandsAsync(harness);
        await UntilAsync(() => screen.Visible("nav-history"), "History offered once unlocked");
        await screen.TouchAsync(screen.Find("nav-history")!);
        await UntilAsync(() => screen.Shell.History is { Reading: false, Page: not null }, "the person's commands");
        await screen.RenderAsync("person-history", TestContext);
        await OnUiAsync(() =>
        {
            screen.Shell.Page.Should().Be(KioskPage.History);
            screen.Shell.History.Page!.Entries.Select(entry => (entry.Command, entry.Accepted)).Should().Equal(
                [(RoofHistoryCommand.Stop, true), (RoofHistoryCommand.Open, true), (RoofHistoryCommand.Open, false)],
                "the person's own commands, newest first; the admin key's Stop is not theirs");
            Texts(screen, "history-person").Should().Equal(
                $"Olga Example ({KioskHarness.Operator})", "Operator", "Local account", "Signed in with a PIN", "Short name: Olga");
            // The person and their newest commands are on the first page.
            Texts(screen, "history-entry-0").Should().Equal(
                RoofStatusText.Time(KioskHarness.Start.AddMinutes(3)), "Stop", "Accepted", "Roof after: Stopped", "From kiosk");
            Texts(screen, "history-entry-1").Should().Equal(
                RoofStatusText.Time(KioskHarness.Start.AddMinutes(2)), "Open", "Accepted (conditions unknown)", "Roof after: Opening", "From kiosk");
            ReadOnly(screen, "history-reload");
        });
        var shown = await ReadEveryPageAsync(screen, "history-pages");
        shown["history-result-2"].Should().Be("Refused [InterlockActive]", "a refusal says the controller's code");
        shown["history-range"].Should().StartWith("Newest first: 1–3 of 3 commands.");
        shown["history-names"].Should().Be(RoofIdentityText.OwnNamesWhere, "names are changed in the web UI or the terminal, never here");

        // Read again reads afresh.
        var reads = harness.Sent(HttpMethod.Get, RoofApiRoutes.Me);
        await screen.TouchAsync(screen.Find("history-reload")!);
        await UntilAsync(() => harness.Sent(HttpMethod.Get, RoofApiRoutes.Me) > reads && !screen.Shell.History.Reading, "History read again");

        // Locked, History goes, and nothing read stays for the next person.
        await harness.Console.LockAsync();
        await UntilAsync(() => !screen.Visible("nav-history"), "History gone once locked");
        await OnUiAsync(() =>
        {
            screen.Shell.Page.Should().Be(KioskPage.Roof);
            screen.Shell.History.Person.Should().BeNull();
            screen.Shell.History.Page.Should().BeNull();
        });
        harness.Calls("Close").Should().Be(0);
    }

    [TestMethod]
    public async Task History_SaysWhyWhenTheCommandsCannotBeRead_AndReadsAgain()
    {
        await using var harness = await KioskHarness.CreateAsync();
        await harness.StartLiveAsync();
        await harness.UnlockAsync(KioskHarness.Operator);
        await using var screen = await KioskScreen.ShowAsync(harness, 800, 480, 5.2);
        harness.CutAnswer = request => request.RequestUri!.AbsolutePath.EndsWith("/" + RoofCommandHistoryContract.Route, StringComparison.Ordinal);

        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.History));
        await UntilAsync(() => screen.Shell.History is { Reading: false, Problem: not null }, "the failed read");
        await screen.RenderAsync("person-history-unreadable", TestContext);
        await OnUiAsync(() =>
        {
            screen.Text("history-status").Should().StartWith("Your roof commands could not be read.");
            screen.Shell.History.Person.Should().NotBeNull("the person's details were read; only the commands were not");
            screen.Visible("history-person").Should().BeTrue();
        });

        harness.CutAnswer = null;
        await screen.TouchAsync(screen.Find("history-reload")!);
        await UntilAsync(() => screen.Shell.History is { Reading: false, Page: not null }, "the commands read again");
        await OnUiAsync(() =>
        {
            screen.Visible("history-status").Should().BeFalse();
            screen.Text("history-empty").Should().Be(RoofHistoryText.Empty, "nothing has been sent from this session yet");
        });
    }

    [TestMethod]
    [DataRow(1280, 800)]
    [DataRow(960, 600)]
    public async Task TheMacApp_OffersHistory_ToWhoeverSignedIn_WithTheirOwnCommandsOnly(int width, int height)
    {
        await using var harness = await KioskHarness.CreateAsync(
            settings: new Dictionary<string, string?> { ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "false" },
            options: new KioskConsoleOptions { Wording = KioskWording.Desktop, Blanking = false });
        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, KioskMetrics.DeskPixelsPerMillimetre);
        await OnUiAsync(() => screen.Visible("nav-history").Should().BeFalse("nobody has signed in"));
        using (var key = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin)))
        {
            await key.StopAsync();
        }

        await harness.PasswordAsync(KioskHarness.Admin);
        await harness.Console.StopAsync();
        await UntilAsync(() => screen.Visible("nav-history"), "History offered once signed in");
        await screen.ClickAsync(screen.Find("nav-history")!);
        await UntilAsync(() => screen.Shell.History is { Reading: false, Page: not null }, "the person's commands");
        await screen.RenderAsync("mac-history", TestContext);
        await OnUiAsync(() =>
        {
            screen.Shell.History.Page!.Entries.Should().ContainSingle("an admin's app reads their own commands, not everyone's")
                .Which.User.Should().Be(KioskHarness.Admin);
            Texts(screen, "history-person").Should().Contain(["Admin", "Signed in with a password"]);
            Texts(screen, "history-entry-0").Should().Contain("From Mac app");
        });
        (await ReadEveryPageAsync(screen, "history-pages")).Should().ContainKey("history-command-0");
    }

    [TestMethod]
    public async Task TheMacAppsHistory_IsTheAdminsOwn_NotAKeyNamedLikeThem()
    {
        await using var harness = await KioskHarness.CreateAsync(
            settings: new Dictionary<string, string?> { ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "false" },
            options: new KioskConsoleOptions { Wording = KioskWording.Desktop, Blanking = false });
        await harness.StartLiveAsync();
        using (var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin)))
        {
            var key = await admin.Identity.AddApiKeyAsync(new RoofApiKeyCreateRequest { Name = KioskHarness.Admin, Role = RoofControllerApiContract.OperatorRole });
            using var namesake = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(key.Secret));
            await namesake.StopAsync();
        }

        await harness.PasswordAsync(KioskHarness.Admin);
        await harness.Console.StopAsync();

        var page = await harness.Console.ReadRecentHistoryAsync(KioskHistoryPanel.Take);
        page.Should().NotBeNull();
        page!.AllUsers.Should().BeFalse("History is the person's own, an admin's too");
        page.Entries.Should().ContainSingle("a key named like the admin is not the admin").Which.Should().Match<RoofCommandHistoryEntry>(entry =>
            entry.User == KioskHarness.Admin && entry.PrincipalKind == RoofHistoryPrincipalKind.Person);

        // A controller from before "mine" ignores it and gives an admin everyone's: History says so and shows none of them.
        harness.AlterRequest = request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/" + RoofCommandHistoryContract.Route, StringComparison.Ordinal))
                request.RequestUri = new Uri(request.RequestUri.GetLeftPart(UriPartial.Path) + request.RequestUri.Query.Replace("mine=true&", string.Empty, StringComparison.Ordinal));
        };
        await using var screen = await KioskScreen.ShowAsync(harness, 960, 600, KioskMetrics.DeskPixelsPerMillimetre);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.History));
        await UntilAsync(() => screen.Shell.History is { Reading: false, Problem: not null }, "the refusal to show everyone's as the admin's");
        await OnUiAsync(() =>
        {
            screen.Shell.History.Page.Should().BeNull();
            screen.Text("history-status").Should().EndWith(KioskConsole.OwnHistoryUnsupported);
            screen.Find("history-entry-0").Should().BeNull("no one's commands are shown as the admin's own");
        });
    }

    [TestMethod]
    [DataRow(1920, 1200, 9.0)]
    [DataRow(1280, 720, 8.2)]
    [DataRow(800, 480, 5.2)]
    public async Task TheLongestNames_StayClearOfThePageControls_OnThePeopleStepAndHistory(int width, int height, double density)
    {
        await using var harness = await KioskHarness.CreateAsync();
        var longest = new[] { new string('W', RoofIdentityContract.MaximumNameLength), new string('X', RoofIdentityContract.MaximumNameLength) };
        using (var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin)))
        {
            foreach (var name in longest)
            {
                await RoofClientApiTests.AddUserAsync(harness.Host, name, RoofControllerApiContract.OperatorRole, TestSecrets.Pin);
                await admin.Identity.UpdateUserAsync(name, new RoofUserUpdateRequest
                {
                    Role = RoofControllerApiContract.OperatorRole,
                    DisplayName = new string('W', RoofIdentityContract.MaximumDisplayNameLength),
                    ShortName = new string('W', RoofIdentityContract.MaximumShortNameLength)
                });
            }
        }

        await harness.StartLiveAsync();
        await using var screen = await KioskScreen.ShowAsync(harness, width, height, density);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Unlock));
        await UntilAsync(() => screen.Shell.PinPad.Users.Count == 4 && screen.Find($"pin-user-{KioskHarness.Admin}") is not null, "the people with a PIN");
        var people = await ReadEveryPageAsync(screen, "pin-people", "person-people-longest");
        foreach (var name in longest.Append(KioskHarness.Operator).Append(KioskHarness.Admin))
        {
            people.Should().ContainKey($"pin-user-{name}", "every person is on a page of their own or beside another");
        }

        await harness.UnlockAsync(longest[0]);
        await UntilAsync(() => screen.Visible("nav-history"), "History offered once unlocked");
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.History));
        await UntilAsync(() => screen.Shell.History is { Reading: false, Person: not null }, "the person");
        (await ReadEveryPageAsync(screen, "history-pages", "person-history-longest")).Should().ContainKey("history-names");
    }

    // Olga has a display name and a short name (#209), set by an admin; Ada has neither.
    private static async Task NameOlgaAsync(KioskHarness harness)
    {
        using var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin));
        await admin.Identity.UpdateUserAsync(KioskHarness.Operator, new RoofUserUpdateRequest
        {
            Role = RoofControllerApiContract.OperatorRole,
            DisplayName = "Olga Example",
            ShortName = "Olga"
        });
    }

    // From the unlocked kiosk, a minute apart on the controller's clock: an Open the controller refuses, an Open it
    // carries out, and a Stop.
    private static async Task SendOlgasCommandsAsync(KioskHarness harness)
    {
        harness.Roof.Mock.Setup(service => service.Open()).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.InterlockActive, "The drive reports running; the roof was not started.", harness.Current)));
        harness.ServerClock.Advance(TimeSpan.FromMinutes(1));
        await harness.Console.OpenAsync();
        await harness.WaitForAsync(view => view.Busy is null, "the refused Open");
        harness.OpensTheRoof();
        harness.ServerClock.Advance(TimeSpan.FromMinutes(1));
        await harness.Console.OpenAsync();
        await harness.WaitForAsync(view => view.Busy is null && view.Status?.Status == RoofControllerStatus.Opening, "the opening roof");
        harness.ServerClock.Advance(TimeSpan.FromMinutes(1));
        await harness.Console.StopAsync();
        await harness.WaitForAsync(view => view.StopOutcome == RoofStopOutcome.Acknowledged, "the Stop");
    }

    // Every page of the bounded document called pager in turn, each laid out and checked as a render is (nothing cut off,
    // overlapping or off the screen): each named control on it, with what it says when it is a text. It ends back on the
    // first page. With a render name, each page is drawn and saved too (render-p1, render-p2, …), Stop checked on it.
    private async Task<IReadOnlyDictionary<string, string?>> ReadEveryPageAsync(KioskScreen screen, string pager, string? render = null)
    {
        var shown = new Dictionary<string, string?>();
        var pages = await OnUiAsync(() => ((KioskPager)screen.Find(pager)!).PageCount);
        for (var page = 0; page < pages; page++)
        {
            if (render is null) await screen.CheckAsync($"{pager} page {page + 1}");
            else await screen.RenderAsync($"{render}-p{page + 1}", TestContext);
            await OnUiAsync(() =>
            {
                screen.OffScreen(name => name.StartsWith(pager.Split('-')[0] + "-", StringComparison.Ordinal) && name != pager).Should().BeEmpty();
                foreach (var control in screen.Find(pager)!.GetVisualDescendants().OfType<Control>().Where(control => control.Name is not null))
                {
                    shown[control.Name!] = (control as TextBlock)?.Text;
                }

                ((KioskPager)screen.Find(pager)!).Move(1);
            });
        }

        await OnUiAsync(() => ((KioskPager)screen.Find(pager)!).Move(-pages));
        return shown;
    }

    // The texts drawn inside the control called name, in order.
    private static IReadOnlyList<string?> Texts(KioskScreen screen, string name)
        => [.. screen.Find(name)!.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text)];

    // The page edits nothing: no text box, and no button but those named, and its pages' Previous and Next.
    private static void ReadOnly(KioskScreen screen, params string[] buttons)
    {
        var page = screen.Find("page")!;
        page.GetVisualDescendants().OfType<TextBox>().Should().BeEmpty("nothing is typed here: names change in the web UI and the terminal");
        page.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).Select(button => button.Name)
            .Where(name => name is null || !(name.EndsWith("-previous", StringComparison.Ordinal) || name.EndsWith("-next", StringComparison.Ordinal)))
            .Should().BeEquivalentTo(buttons, "the page only chooses or reads again");
    }
}
