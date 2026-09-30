using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Screens;
using static HVO.RoofControllerV4.RPi.Tests.Kiosk.KioskAvalonia;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The Mac app's screens, drawn headless at the desk's sizes and used with a mouse and keyboard as on a Mac: the sign-in
/// page, whose password box shows only dots and is emptied as the password is sent, and settings typed in a text box.
/// The renders are saved as the kiosk's are (<see cref="KioskScreen.RendersVariable"/>), with <c>mac-</c> names.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class MacScreenTests
{
    private const string KioskTimeout = "RoofControllerUi:KioskScreenTimeout";
    private const string DefaultCamera = "RoofControllerUi:DefaultCamera";

    public TestContext TestContext { get; set; } = null!;

    /// <summary>The app's console on a plain viewer key, as <see cref="MacConsoleTests"/> has it.</summary>
    private static Task<KioskHarness> CreateAsync() => KioskHarness.CreateAsync(
        settings: new Dictionary<string, string?> { ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "false" },
        options: new KioskConsoleOptions { Wording = KioskWording.Desktop, Blanking = false });

    private static Task<KioskScreen> ShowAsync(KioskHarness harness, int width = 1280, int height = 800)
        => KioskScreen.ShowAsync(harness, width, height, KioskMetrics.DeskPixelsPerMillimetre);

    [TestMethod]
    [DataRow(1280, 800)]
    [DataRow(960, 600)]
    public async Task SignedOut_TheAppOffersSignIn_AndStop_InItsWords(int width, int height)
    {
        await using var harness = await CreateAsync();
        await harness.StartLiveAsync();
        await using var screen = await ShowAsync(harness, width, height);

        await screen.RenderAsync("mac-signed-out", TestContext);

        await OnUiAsync(() =>
        {
            screen.Text("nav-lock").Should().Be("Sign in");
            screen.Text("locked").Should().Be("Signed out");
            screen.Text("hint").Should().StartWith(KioskWording.Desktop.SignedOutHint);
            screen.Shell.RoofPage.Open.IsEffectivelyVisible.Should().BeFalse("Open needs someone signed in");
            screen.Visible("nav-settings").Should().BeFalse();
        });
    }

    [TestMethod]
    [DataRow(1280, 800)]
    [DataRow(960, 600)]
    public async Task TheSignInPage_ShowsADotPerCharacter_AndReturnSignsIn(int width, int height)
    {
        await using var harness = await CreateAsync();
        await harness.StartLiveAsync();
        await using var screen = await ShowAsync(harness, width, height);
        var page = screen.Shell.SignInPage;

        await screen.ClickAsync(screen.Find("nav-lock")!);
        await OnUiAsync(() =>
        {
            screen.Visible("signin-name").Should().BeTrue();
            screen.Visible("pin-masked").Should().BeFalse("the app has no PIN pad");
            page.NameBox.IsFocused.Should().BeTrue("the page opens with the keyboard in the name box");
            screen.Find("signin-submit")!.IsEnabled.Should().BeFalse("there is no name or password yet");
        });
        await screen.TypeAsync(KioskHarness.Operator);
        await screen.PressAsync(PhysicalKey.Tab);
        await screen.TypeAsync(TestSecrets.Password);

        await screen.RenderAsync("mac-sign-in", TestContext);

        await OnUiAsync(() =>
        {
            page.PasswordBox.IsFocused.Should().BeTrue("Tab moves from the name to the password");
            screen.Shell.PasswordForm.Name.Should().Be(KioskHarness.Operator);
            screen.Shell.PasswordForm.HasPassword.Should().BeTrue();
            page.PasswordBox.PasswordChar.Should().Be('●');
            page.PasswordBox.RevealPassword.Should().BeFalse();
            page.PasswordBox.GetVisualDescendants().OfType<TextPresenter>().Should().ContainSingle()
                .Which.PasswordChar.Should().Be('●', "what is drawn is a dot for each character");
            screen.Find("signin-submit")!.IsEnabled.Should().BeTrue();
            screen.OffScreen(name => name.StartsWith("signin-", StringComparison.Ordinal)).Should().BeEmpty("the whole form is on the screen");
        });
        harness.Sent(HttpMethod.Get, RoofApiRoutes.PinUsers).Should().Be(0, "the app asks for a name, not a choice of the people with a PIN");

        await screen.PressAsync(PhysicalKey.Enter);

        await UntilAsync(() => harness.Console.View.IsUnlocked, "the sign-in");

        // The shell shows it with the console's next update, which it posts to its thread.
        await UntilAsync(() => screen.Text("nav-lock") == "Sign out", "the shell to show the sign-in");
        await OnUiAsync(() =>
        {
            page.PasswordBox.Text.Should().BeNullOrEmpty("the password is forgotten as it is sent");
            screen.Shell.PasswordForm.HasPassword.Should().BeFalse();
            screen.Visible("signin-name").Should().BeFalse("signed in, the app shows the roof");
            screen.Visible("nav-settings").Should().BeTrue();
            screen.Shell.RoofPage.Open.IsEffectivelyVisible.Should().BeTrue();
        });
        harness.Console.View.UnlockedBy.Should().Be(KioskHarness.Operator);

        await screen.RenderAsync("mac-roof", TestContext);
    }

    [TestMethod]
    public async Task AWrongPassword_IsSaidSo_AndThePasswordBoxIsEmptied_WithTheKeyboard()
    {
        await using var harness = await CreateAsync();
        await harness.StartLiveAsync();
        await using var screen = await ShowAsync(harness);
        var page = screen.Shell.SignInPage;
        await screen.ClickAsync(screen.Find("nav-lock")!);
        await screen.TypeAsync(KioskHarness.Operator);
        await screen.ClickAsync(page.PasswordBox);
        await screen.TypeAsync(TestSecrets.OtherPassword);

        await screen.ClickAsync(screen.Find("signin-submit")!);

        await UntilAsync(() => screen.Shell.PasswordForm is { SigningIn: false, Error: not null }, "the refusal");
        await OnUiAsync(() =>
        {
            screen.Visible("signin-error").Should().BeTrue();
            screen.Text("signin-error").Should().Be(screen.Shell.PasswordForm.Error).And.NotContain(TestSecrets.OtherPassword);
            page.PasswordBox.Text.Should().BeNullOrEmpty();
            page.PasswordBox.IsFocused.Should().BeTrue("the password box has the keyboard again, for the next try");
            page.NameBox.Text.Should().Be(KioskHarness.Operator, "the name is kept");
        });
        await screen.DrawAsync();
        await OnUiAsync(() => screen.Visible("signin-error").Should().BeTrue("the refusal stays until the person types"));
        harness.Console.View.IsUnlocked.Should().BeFalse();

        await screen.TypeAsync("x");

        await OnUiAsync(() =>
        {
            screen.Shell.PasswordForm.Error.Should().BeNull("typing again clears the last answer");
            screen.Visible("signin-error").Should().BeFalse();
        });
    }

    [TestMethod]
    public async Task SignIn_IsOfferedForANameAndAPassword_AndEscapeLeavesWithoutThePassword()
    {
        await using var harness = await CreateAsync();
        await harness.StartLiveAsync();
        await using var screen = await ShowAsync(harness);
        var page = screen.Shell.SignInPage;
        await screen.ClickAsync(screen.Find("nav-lock")!);

        await screen.TypeAsync("not a name!");
        await screen.PressAsync(PhysicalKey.Tab);
        await screen.TypeAsync(TestSecrets.Password);
        await OnUiAsync(() => screen.Find("signin-submit")!.IsEnabled.Should().BeFalse("that could not be anyone's name"));
        await screen.PressAsync(PhysicalKey.Enter);
        harness.Sent(HttpMethod.Post, RoofApiRoutes.Session).Should().Be(0, "Return sends only what Sign in would");

        await screen.PressAsync(PhysicalKey.Escape);

        await OnUiAsync(() =>
        {
            screen.Visible("signin-name").Should().BeFalse("Escape leaves the page");
            screen.Shell.RoofPage.IsEffectivelyVisible.Should().BeTrue();
        });
        await screen.ClickAsync(screen.Find("nav-lock")!);
        await OnUiAsync(() =>
        {
            screen.Shell.PasswordForm.HasPassword.Should().BeFalse("the page forgets the password when it opens again");
            page.PasswordBox.Text.Should().BeNullOrEmpty();
            page.NameBox.Text.Should().Be("not a name!", "the name is kept, to be corrected");
            page.PasswordBox.IsFocused.Should().BeTrue("with a name, the keyboard is in the password box");
        });
    }

    [TestMethod]
    [DataRow(1280, 800)]
    [DataRow(960, 600)]
    public async Task ASetting_IsTypedInATextBox_AndReturnSavesIt(int width, int height)
    {
        await using var harness = await CreateAsync();
        await harness.StartLiveAsync();
        await harness.Console.SignInAsync(KioskHarness.Admin, TestSecrets.Password);
        await using var screen = await ShowAsync(harness, width, height);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Settings));
        await UntilAsync(() => screen.Shell.Settings is { Form: not null, Busy: null }, "the settings");
        var version = await OnUiAsync(() => screen.Shell.Settings.Form!.Version);

        await OnUiAsync(() =>
        {
            screen.Shell.Settings.SelectField(DefaultCamera);
            screen.Shell.Settings.BeginEdit();
        });
        var box = await OnUiAsync(() => (TextBox)screen.Find("editor-text")!);
        await OnUiAsync(() =>
        {
            box.IsFocused.Should().BeTrue("the editor opens with the keyboard in its box");
            screen.Find("editor-key-shift").Should().BeNull("the app has a keyboard: there is no keyboard on the screen");
            screen.Find("editor-value").Should().BeNull();
            box.SelectAll();
        });
        await screen.TypeAsync("Pier");

        await screen.RenderAsync("mac-settings-edit", TestContext);

        await OnUiAsync(() =>
        {
            screen.Shell.Settings.EditText.Should().Be("Pier");
            screen.OffScreen(name => name.StartsWith("editor-", StringComparison.Ordinal)).Should().BeEmpty("the box, Save and Cancel are on the screen");
        });

        await screen.PressAsync(PhysicalKey.Enter);

        await UntilAsync(() => screen.Shell.Settings is { Editing: null, Busy: null, Message: not null }, "the save");
        await OnUiAsync(() =>
        {
            screen.Shell.Settings.Message!.Text.Should().Be($"Saved (settings version {version + 1}).");
            screen.Shell.Settings.Form!.FindField(DefaultCamera)!.DisplayValue.Should().Be("Pier");
        });
    }

    [TestMethod]
    public async Task ARefusedValue_IsSaid_UntilThePersonTypes_AndEscapeCancels()
    {
        await using var harness = await CreateAsync();
        await harness.StartLiveAsync();
        await harness.Console.SignInAsync(KioskHarness.Operator, TestSecrets.Password);
        await using var screen = await ShowAsync(harness);
        await OnUiAsync(() => screen.Shell.ShowPage(KioskPage.Settings));
        await UntilAsync(() => screen.Shell.Settings is { Form: not null, Busy: null }, "the settings");
        var version = await OnUiAsync(() => screen.Shell.Settings.Form!.Version);
        await OnUiAsync(() =>
        {
            screen.Shell.Settings.SelectField(KioskTimeout);
            screen.Shell.Settings.BeginEdit();
            ((TextBox)screen.Find("editor-text")!).Text.Should().Be("300", "the box starts with the setting's value");
        });
        await screen.TypeAsync("0000");

        await screen.PressAsync(PhysicalKey.Enter);

        await UntilAsync(() => screen.Shell.Settings is { EditError: not null, Busy: null }, "the refusal");
        await screen.DrawAsync();
        await OnUiAsync(() =>
        {
            screen.Visible("editor-error").Should().BeTrue("the refusal stays on the rebuilt editor until the person types");
            var box = (TextBox)screen.Find("editor-text")!;
            box.Text.Should().Be("3000000");
            box.IsFocused.Should().BeTrue();
            box.CaretIndex.Should().Be(box.Text!.Length, "the caret is at the end, where the typing was");
        });

        await screen.PressAsync(PhysicalKey.Backspace);

        await OnUiAsync(() =>
        {
            screen.Visible("editor-error").Should().BeFalse("the refusal was of the value before this change");
            screen.Shell.Settings.EditText.Should().Be("300000");
            screen.Shell.Settings.EditError.Should().BeNull();
        });

        await screen.PressAsync(PhysicalKey.Escape);

        await OnUiAsync(() =>
        {
            screen.Shell.Settings.Editing.Should().BeNull("Escape cancels the change");
            screen.Find("editor-text").Should().BeNull();
        });
        (await harness.Client.Settings.GetAsync()).Version.Should().Be(version, "nothing was sent");
    }
}
