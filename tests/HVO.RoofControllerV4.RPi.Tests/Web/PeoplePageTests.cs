using AngleSharp.Dom;
using Bunit;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Web.Components.Pages;
using HVO.RoofControllerV4.Web.Sessions;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The People page: an admin adds and changes people, API keys and sessions through the controller's identity API, with
/// their own session. A change the controller refuses is said in its panel, which stays open; a new or rotated key's
/// secret is shown once.
/// </summary>
[TestClass]
public sealed class PeoplePageTests
{
    private const string NewPassword = "test-new-password-not-real";
    private const string NewPin = "864209";

    [TestMethod]
    public async Task TheLists_AreRead_WithTheAdminsSession()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await harness.SignInAsync("vic", RoofControllerApiContract.ViewerRole);
        await using var context = harness.Context(session);

        var cut = Loaded(context);

        cut.FindAll("[data-testid=user]").Select(Row).ToList().Should().Equal(
            "ada | Admin | yes | no | 1 | 2026-09-29 12:00:00Z",
            "vic | Viewer | yes | no | 1 | 2026-09-29 12:00:00Z");

        cut.Click("[data-testid=people-tab][data-tab=keys]");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=key]").Select(Row).ToList().Should().Contain(
            "test-admin | Admin | no | configuration (read-only) | "));
        cut.FindAll("[data-testid=key-change]").Should().BeEmpty("a key from the configuration is read-only here");

        cut.Click("[data-testid=people-tab][data-tab=sessions]");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=session]").Select(row => row.QuerySelector("th")!.TextContent).ToList()
            .Should().Equal("ada (this page)", "vic"));
        cut.Find($"[data-testid=session][data-id='{session.Id}']").TextContent.Should().Contain("password");
    }

    [TestMethod]
    public async Task AnAdmin_AddsAPerson_WithAPasswordAndAPin()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        cut.Click("[data-testid=user-add]");
        cut.WaitForElement("[data-testid=user-name]");
        cut.Input("[data-testid=user-name]", "otto");
        cut.Change("[data-testid=user-role]", RoofControllerApiContract.OperatorRole);
        cut.Input("[data-testid=user-password]", NewPassword);
        cut.Input("[data-testid=user-password-again]", NewPassword);
        cut.Input("[data-testid=user-pin]", NewPin);
        cut.Input("[data-testid=user-pin-again]", NewPin);
        cut.Click("[data-testid=user-add-send]");

        cut.WaitForAssertion(() => Message(cut).Should().Be("Added otto (Operator)."));
        cut.FindAll("[data-testid=user-add-form]").Should().BeEmpty();
        Row(cut.Find("[data-testid=user][data-name=otto]")).Should().Be("otto | Operator | yes | yes | 0 | 2026-09-29 12:00:00Z");
        using var anonymous = ClientTestSupport.CreateClient(harness.Host);
        (await anonymous.Auth.SignInAsync("otto", NewPassword)).Role.Should().Be(RoofControllerApiContract.OperatorRole);
    }

    [TestMethod]
    [DataRow("bad name", NewPassword, NewPassword, "", "", "'bad name' is not a valid name: ")]
    [DataRow("otto", "", "", "", "", "Give a password, a PIN, or both.")]
    [DataRow("otto", NewPassword, NewPassword + "!", "", "", "The two passwords differ.")]
    [DataRow("otto", "short", "short", "", "", "A password must be ")]
    [DataRow("otto", "", "", "12", "12", "A PIN must be ")]
    public async Task APersonWhoCannotBeRight_IsNotSent(string name, string password, string again, string pin, string pinAgain, string said)
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        cut.Click("[data-testid=user-add]");
        cut.WaitForElement("[data-testid=user-name]");
        cut.Input("[data-testid=user-name]", name);
        cut.Input("[data-testid=user-password]", password);
        cut.Input("[data-testid=user-password-again]", again);
        cut.Input("[data-testid=user-pin]", pin);
        cut.Input("[data-testid=user-pin-again]", pinAgain);
        cut.Click("[data-testid=user-add-send]");

        cut.WaitForAssertion(() => cut.Find("[data-testid=panel-error]").TextContent.Should().StartWith(said));
        cut.FindAll("[data-testid=user-add-form]").Should().ContainSingle("the form stays open to correct it");
        using var admin = harness.Admin();
        (await admin.Identity.GetUsersAsync()).Select(user => user.Name).Should().Equal("ada");
    }

    [TestMethod]
    public async Task ARefusal_IsSaidInThePanel_WhichStaysOpen()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        cut.Click("[data-testid=user-add]");
        cut.WaitForElement("[data-testid=user-name]");
        cut.Input("[data-testid=user-name]", "otto");
        cut.Change("[data-testid=user-role]", RoofControllerApiContract.ViewerRole);
        cut.Input("[data-testid=user-pin]", NewPin);
        cut.Input("[data-testid=user-pin-again]", NewPin);
        cut.Click("[data-testid=user-add-send]");

        cut.WaitForAssertion(() => cut.Find("[data-testid=panel-error]").TextContent.Should().StartWith("otto could not be added. "));
        cut.FindAll("[data-testid=page-message]").Should().BeEmpty("the refusal is said where it can be corrected");
        cut.FindAll("[data-testid=user-add-form]").Should().ContainSingle();
    }

    [TestMethod]
    public async Task ChangingARole_EndsThePersonsSessions()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await harness.SignInAsync("vic", RoofControllerApiContract.ViewerRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        UserAction(cut, "vic", "user-role-change");
        cut.WaitForElement("[data-testid=user-panel]").TextContent.Should().Contain(RoofIdentityText.RoleChangeEndsSessions);
        cut.Find("[data-testid=user-new-role] option[selected]").GetAttribute("value").Should().Be(RoofControllerApiContract.ViewerRole);
        cut.Change("[data-testid=user-new-role]", RoofControllerApiContract.OperatorRole);
        cut.Click("[data-testid=user-panel-send]");

        cut.WaitForAssertion(() => Message(cut).Should().Be("vic is now Operator."));
        Row(cut.Find("[data-testid=user][data-name=vic]")).Should().StartWith("vic | Operator | yes | no | 0 | ");
        using var admin = harness.Admin();
        (await admin.Identity.GetSessionsAsync()).Select(open => open.Name).Should().NotContain("vic");
    }

    [TestMethod]
    public async Task SettingAPasswordAndAPin_ThenRemovingThePin()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await RoofClientApiTests.AddUserAsync(harness.Host, "olga", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        UserAction(cut, "olga", "user-password-set");
        cut.WaitForElement("[data-testid=user-panel-send]");
        cut.Click("[data-testid=user-panel-send]");
        cut.WaitForAssertion(() => cut.Find("[data-testid=panel-error]").TextContent.Should().Be(RoofIdentityText.PasswordRule));
        cut.Input("[data-testid=user-new-password]", NewPassword);
        cut.Input("[data-testid=user-new-password-again]", NewPassword);
        cut.Click("[data-testid=user-panel-send]");
        cut.WaitForAssertion(() => Message(cut).Should().Be("Set the password of olga."));

        UserAction(cut, "olga", "user-pin-set");
        cut.WaitForElement("[data-testid=user-new-pin]");
        cut.Input("[data-testid=user-new-pin]", NewPin);
        cut.Input("[data-testid=user-new-pin-again]", NewPin);
        cut.Click("[data-testid=user-panel-send]");
        cut.WaitForAssertion(() => Message(cut).Should().Be("Set the PIN of olga."));
        Row(cut.Find("[data-testid=user][data-name=olga]")).Should().StartWith("olga | Operator | yes | yes | ");

        UserAction(cut, "olga", "user-pin-remove");
        cut.WaitForElement("[data-testid=user-panel]").TextContent.Should().Contain("Remove the PIN of olga? Their PIN sessions end.");
        cut.Click("[data-testid=user-panel-send]");
        cut.WaitForAssertion(() => Message(cut).Should().Be("Removed the PIN of olga."));
        Row(cut.Find("[data-testid=user][data-name=olga]")).Should().StartWith("olga | Operator | yes | no | ");

        using var anonymous = ClientTestSupport.CreateClient(harness.Host);
        (await anonymous.Auth.SignInAsync("olga", NewPassword)).Role.Should().Be(RoofControllerApiContract.OperatorRole, "the role was kept");
    }

    [TestMethod]
    public async Task AChange_KeepsTheRoleTheControllerHasNow_NotTheOneOnThePage()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await RoofClientApiTests.AddUserAsync(harness.Host, "olga", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        using var admin = harness.Admin();
        await admin.Identity.UpdateUserAsync("olga", new RoofUserUpdateRequest { Role = RoofControllerApiContract.ViewerRole });

        UserAction(cut, "olga", "user-password-set");
        cut.WaitForElement("[data-testid=user-new-password]");
        cut.Input("[data-testid=user-new-password]", NewPassword);
        cut.Input("[data-testid=user-new-password-again]", NewPassword);
        cut.Click("[data-testid=user-panel-send]");

        cut.WaitForAssertion(() => Message(cut).Should().Be("Set the password of olga."));
        (await admin.Identity.GetUserAsync("olga")).Role.Should().Be(RoofControllerApiContract.ViewerRole, "another admin's change is not undone");
    }

    [TestMethod]
    public async Task RemovingAPerson_IsAskedFirst()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await RoofClientApiTests.AddUserAsync(harness.Host, "olga", RoofControllerApiContract.OperatorRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);

        UserAction(cut, "olga", "user-remove");
        cut.WaitForElement("[data-testid=user-panel]").TextContent.Should().Contain("Remove olga? Their sessions end at once.");
        cut.Click("[data-testid=panel-cancel]");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=user-panel]").Should().BeEmpty());

        UserAction(cut, "olga", "user-remove");
        cut.WaitForElement("[data-testid=user-panel-send]");
        cut.Click("[data-testid=user-panel-send]");

        cut.WaitForAssertion(() => Message(cut).Should().Be("Removed olga."));
        cut.FindAll("[data-testid=user]").Select(user => user.GetAttribute("data-name")).Should().Equal("ada");
    }

    [TestMethod]
    public async Task AKey_IsAdded_WithItsSecretShownOnce_AndRotated()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        cut.Click("[data-testid=people-tab][data-tab=keys]");

        cut.WaitForElement("[data-testid=key-add]");

        cut.Click("[data-testid=key-add]");
        cut.WaitForElement("[data-testid=key-add-form]").TextContent.Should().Contain("A kiosk's key must have the viewer role");
        cut.Input("[data-testid=key-name]", "pier-kiosk");
        cut.Change("[data-testid=key-kiosk]", true);
        cut.Click("[data-testid=key-add-send]");

        cut.WaitForAssertion(() => Message(cut).Should().Be("The key pier-kiosk is ready (Viewer, kiosk)."));
        cut.Find("[data-testid=key-secret] h2").TextContent.Should().Be("The secret of pier-kiosk");
        cut.Find("[data-testid=key-secret]").TextContent.Should().Contain(RoofIdentityText.SecretShownOnce);
        var secret = cut.Find("[data-testid=key-secret-value]").GetAttribute("value")!;
        Row(cut.Find("[data-testid=key][data-name=pier-kiosk]")).Should().Be("pier-kiosk | Viewer | yes | managed | 2026-09-29 12:00:00Z");
        using (var kiosk = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(secret)))
        {
            (await kiosk.Auth.GetCallerAsync()).Name.Should().Be("pier-kiosk", "the secret shown is the key's");
        }

        cut.Click("[data-testid=key-secret-done]");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=key-secret]").Should().BeEmpty());
        cut.Markup.Should().NotContain(secret, "the secret is shown once");

        KeyAction(cut, "pier-kiosk", "key-rotate");
        cut.WaitForElement("[data-testid=key-panel]").TextContent.Should().Contain("Rotate the key pier-kiosk? The old secret stops working at once.");
        cut.Click("[data-testid=key-panel-send]");

        cut.WaitForAssertion(() => cut.Find("[data-testid=key-secret-value]").GetAttribute("value").Should().NotBe(secret));
        using var old = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(secret));
        (await RoofClientApiTests.RefusedAsync(() => old.Auth.GetCallerAsync())).StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [TestMethod]
    public async Task AKioskKeyWithMoreThanTheViewerRole_IsRefused_InThePanel()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        cut.Click("[data-testid=people-tab][data-tab=keys]");

        cut.WaitForElement("[data-testid=key-add]");

        cut.Click("[data-testid=key-add]");
        cut.WaitForElement("[data-testid=key-name]");
        cut.Input("[data-testid=key-name]", "pier-kiosk");
        cut.Change("[data-testid=key-role]", RoofControllerApiContract.OperatorRole);
        cut.Change("[data-testid=key-kiosk]", true);
        cut.Click("[data-testid=key-add-send]");

        cut.WaitForAssertion(() => cut.Find("[data-testid=panel-error]").TextContent.Should().StartWith("The key pier-kiosk could not be added. "));
        cut.FindAll("[data-testid=key-secret]").Should().BeEmpty();
        cut.FindAll("[data-testid=key][data-name=pier-kiosk]").Should().BeEmpty();
    }

    [TestMethod]
    public async Task AKey_IsChanged_AndRemoved()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        using var admin = harness.Admin();
        await admin.Identity.AddApiKeyAsync(new RoofApiKeyCreateRequest { Name = "pier", Role = RoofControllerApiContract.ViewerRole });
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        cut.Click("[data-testid=people-tab][data-tab=keys]");

        KeyAction(cut, "pier", "key-change");
        cut.WaitForElement("[data-testid=key-new-role]");
        cut.Change("[data-testid=key-new-role]", RoofControllerApiContract.OperatorRole);
        cut.Click("[data-testid=key-panel-send]");
        cut.WaitForAssertion(() => Message(cut).Should().Be("The key pier is now Operator."));

        KeyAction(cut, "pier", "key-remove");
        cut.WaitForElement("[data-testid=key-panel]").TextContent.Should().Contain("Remove the key pier? It stops working at once.");
        cut.Click("[data-testid=key-panel-send]");
        cut.WaitForAssertion(() => Message(cut).Should().Be("Removed the key pier."));
        cut.FindAll("[data-testid=key][data-name=pier]").Should().BeEmpty();
    }

    [TestMethod]
    public async Task EndingAnotherSession_AndThePagesOwn()
    {
        using var harness = new WebAdminHarness();
        var session = await harness.SignInAsync("ada", RoofControllerApiContract.AdminRole);
        var vic = await harness.SignInAsync("vic", RoofControllerApiContract.ViewerRole);
        await using var context = harness.Context(session);
        var cut = Loaded(context);
        cut.Click("[data-testid=people-tab][data-tab=sessions]");

        cut.WaitForElement($"[data-testid=session][data-id='{vic.Id}'] [data-testid=session-end]");

        cut.Click($"[data-testid=session][data-id='{vic.Id}'] [data-testid=session-end]");
        cut.WaitForElement("[data-testid=session-panel]").TextContent.Should().Contain("End the session of vic?").And.NotContain("own session");
        cut.Click("[data-testid=session-end-send]");

        cut.WaitForAssertion(() => Message(cut).Should().Be("Ended the session of vic."));
        cut.FindAll("[data-testid=session]").Select(row => row.GetAttribute("data-id")).Should().Equal(session.Id);

        cut.Click($"[data-testid=session][data-id='{session.Id}'] [data-testid=session-end]");
        cut.WaitForElement("[data-testid=session-panel]").TextContent.Should().Contain("It is this page's own session: you will need to sign in again.");
        cut.Click("[data-testid=session-end-send]");

        cut.WaitForAssertion(() => Message(cut).Should().Be("Ended this page's own session: sign in again to go on."));
        using var admin = harness.Admin();
        (await admin.Identity.GetSessionsAsync()).Should().BeEmpty();
    }

    [TestMethod]
    public async Task SignedOut_ThePageSaysSo()
    {
        using var harness = new WebAdminHarness();
        await using var context = harness.Context(session: null);

        var cut = context.Render<People>();

        cut.WaitForAssertion(() => Message(cut).Should().Be(HVO.RoofControllerV4.Web.Components.WebClientPage.SignedOut));
        cut.Find("[data-testid=people-loading]").TextContent.Should().Be("People, API keys and sessions are not read.");
    }

    private static IRenderedComponent<People> Loaded(BunitContext context)
    {
        var cut = context.Render<People>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=users]").Should().ContainSingle(
            cut.FindAll("[data-testid=page-message]").FirstOrDefault()?.TextContent ?? "the lists to be read"));
        return cut;
    }

    private static string Row(IElement row)
        => string.Join(" | ", row.Children.Select(cell => cell.TextContent));

    private static string Message(IRenderedComponent<People> cut)
        => string.Join(' ', cut.Find("[data-testid=page-message]").QuerySelectorAll("p").Select(line => line.TextContent));

    /// <summary>Presses a person's action, once it is shown: it is in the row after theirs.</summary>
    private static void UserAction(IRenderedComponent<People> cut, string name, string action)
    {
        var selector = $"[data-testid=user][data-name='{name}'] + tr [data-testid={action}]";
        cut.WaitForElement(selector);
        cut.Click(selector);
    }

    /// <summary>Presses a key's action, once it is shown: it is in the row after the key's.</summary>
    private static void KeyAction(IRenderedComponent<People> cut, string name, string action)
    {
        var selector = $"[data-testid=key][data-name='{name}'] + tr [data-testid={action}]";
        cut.WaitForElement(selector);
        cut.Click(selector);
    }
}
