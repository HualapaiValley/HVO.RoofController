using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.Screens;

namespace HVO.RoofControllerV4.RPi.Tests.Kiosk;

/// <summary>
/// The console as the Mac app runs it, against the controller's API: with a plain viewer key (not a kiosk key), the
/// person signs in with their name and password, which are never kept or shown; the app signs them out when it is not
/// used, or when the controller ends the session; the screen never blanks; and Stop is sent whoever is signed in.
/// </summary>
[TestClass]
public sealed class MacConsoleTests
{
    /// <summary>The Mac app's console: its wording, no blanking.</summary>
    private static KioskConsoleOptions Options(TimeSpan? idleLock = null) => new()
    {
        Wording = KioskWording.Desktop,
        Blanking = false,
        IdleLock = idleLock ?? TimeSpan.FromMinutes(15)
    };

    /// <summary>The app's key is a viewer key the controller does not mark as a kiosk's.</summary>
    private static Dictionary<string, string?> ViewerKey((string Key, string? Value)[]? more)
    {
        var settings = new Dictionary<string, string?> { ["RoofControllerSecurity:ApiKeys:4:Kiosk"] = "false" };
        foreach (var (key, value) in more ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }

    private static Task<KioskHarness> CreateAsync(TimeSpan? idleLock = null, (string Key, string? Value)[]? settings = null)
        => KioskHarness.CreateAsync(settings: ViewerKey(settings), options: Options(idleLock));

    [TestMethod]
    public async Task BeforeItStarts_AndSignedOut_OnlyStopIsOffered_InTheAppsWords()
    {
        await using var harness = await CreateAsync();
        harness.Console.View.OpenBlock.Should().Be("the app is starting");

        await harness.StartLiveAsync();

        var view = harness.Console.View;
        view.IsUnlocked.Should().BeFalse();
        view.OpenBlock.Should().Be(KioskWording.Desktop.SignedOutBlock).And.Be("no one is signed in: sign in with your name and password");
        view.CloseBlock.Should().Be(KioskWording.Desktop.SignedOutBlock);
        view.ClearFaultBlock.Should().Be(KioskWording.Desktop.SignedOutBlock);
        (await harness.Client.Auth.GetCallerAsync()).IsKiosk.Should().BeFalse("the app's key is a plain viewer key");

        await harness.Console.StopAsync();

        harness.Console.View.StopOutcome.Should().Be(RoofStopOutcome.Acknowledged, "Stop needs no one signed in");
        harness.Calls(nameof(IRoofControllerServiceV4.Stop)).Should().Be(1);
    }

    [TestMethod]
    public async Task AnOperator_SignsInWithTheirPassword_AndTheAppSendsTheirSession()
    {
        await using var harness = await CreateAsync();
        await harness.StartLiveAsync();

        (await harness.Console.SignInAsync(KioskHarness.Operator, TestSecrets.Password)).Should().BeNull();

        var view = harness.Console.View;
        view.UnlockedBy.Should().Be(KioskHarness.Operator);
        view.IsOperator.Should().BeTrue();
        view.OpenBlock.Should().BeNull();
        view.Notices.Should().ContainSingle().Which.Should().Be(
            new KioskNotice("Signed in as olga (Operator).", KioskNoticeLevel.Info, KioskHarness.Start));
        harness.Credential.PinSession!.Name.Should().Be(KioskHarness.Operator);
        (await harness.Client.Auth.GetCallerAsync()).Name.Should().Be(KioskHarness.Operator, "the app's requests are now the person's");
        harness.Logs.Entries.Should().NotContain(entry => entry.Message.Contains(TestSecrets.Password));
    }

    [TestMethod]
    public async Task Open_SaysTheAppRenewsTheLease()
    {
        await using var harness = await CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.Console.SignInAsync(KioskHarness.Operator, TestSecrets.Password);

        await harness.Console.OpenAsync();

        harness.Console.View.Notices[0].Text.Should().Be(
            "Open accepted. The app renews the operator lease while the roof moves; if it signs out or cannot reach the controller, the roof stops when the lease runs out. Stop roof stops it now.");
        await harness.Console.StopAsync();
    }

    [TestMethod]
    public async Task AWrongPassword_IsRefused_NeverShownOrLogged_AndTheAppStaysSignedOut()
    {
        await using var harness = await CreateAsync();
        await harness.StartLiveAsync();

        var refusal = await harness.Console.SignInAsync(KioskHarness.Operator, TestSecrets.OtherPassword);

        refusal.Should().NotBeNullOrWhiteSpace();
        refusal.Should().NotContain(TestSecrets.OtherPassword);
        harness.Console.View.IsUnlocked.Should().BeFalse();
        harness.Credential.PinSession.Should().BeNull();
        harness.Logs.Entries.Should().NotContain(entry => entry.Message.Contains(TestSecrets.OtherPassword));
    }

    [TestMethod]
    public async Task RepeatedWrongPasswords_LockTheNameOut_AndTheRightOneWaits()
    {
        await using var harness = await CreateAsync(settings: [("RoofControllerSecurity:Identity:LockoutThreshold", "2")]);
        await harness.StartLiveAsync();

        await harness.Console.SignInAsync(KioskHarness.Operator, TestSecrets.OtherPassword);
        await harness.Console.SignInAsync(KioskHarness.Operator, TestSecrets.OtherPassword);

        (await harness.Console.SignInAsync(KioskHarness.Operator, TestSecrets.Password))
            .Should().Be("Too many failed sign-ins. Wait, then try again. [SignInLockedOut]");
        harness.Console.View.IsUnlocked.Should().BeFalse();
    }

    [TestMethod]
    public async Task SigningInAgain_WhileSignedIn_IsRefused()
    {
        await using var harness = await CreateAsync();
        await harness.StartLiveAsync();
        await harness.Console.SignInAsync(KioskHarness.Operator, TestSecrets.Password);

        (await harness.Console.SignInAsync(KioskHarness.Admin, TestSecrets.Password)).Should().Be("Someone is already signed in.");

        harness.Console.View.UnlockedBy.Should().Be(KioskHarness.Operator);
    }

    [TestMethod]
    public async Task SignOut_EndsTheSessionAtTheController_AndTheAppIsBackToItsKey()
    {
        await using var harness = await CreateAsync();
        await harness.StartLiveAsync();
        await harness.Console.SignInAsync(KioskHarness.Admin, TestSecrets.Password);
        var session = harness.Credential.PinSession!;

        await harness.Console.LockAsync();

        var view = harness.Console.View;
        view.IsUnlocked.Should().BeFalse();
        view.OpenBlock.Should().Be(KioskWording.Desktop.SignedOutBlock);
        view.Notices[0].Should().Be(new KioskNotice("Signed out.", KioskNoticeLevel.Info, KioskHarness.Start));
        harness.Credential.PinSession.Should().BeNull();
        (await harness.Client.Auth.GetCallerAsync()).Name.Should().Be("test-kiosk", "the app is back to its device key");
        using var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin));
        (await admin.Identity.GetSessionsAsync()).Should().NotContain(candidate => candidate.Id == session.SessionId, "signing out ended the session");
    }

    [TestMethod]
    public async Task TheControllerEndingTheSession_SignsTheAppOut_AndSaysSo()
    {
        await using var harness = await CreateAsync();
        harness.OpensTheRoof();
        await harness.StartLiveAsync();
        await harness.Console.SignInAsync(KioskHarness.Operator, TestSecrets.Password);
        using var admin = ClientTestSupport.CreateClient(harness.Host, new RoofApiKeyCredential(TestApiKeys.Admin));
        await admin.Identity.EndSessionAsync(harness.Credential.PinSession!.SessionId);

        await harness.Console.OpenAsync();

        await harness.WaitForAsync(view => !view.IsUnlocked, "the app to sign out");
        harness.Console.View.Notices.Select(notice => notice.Text).Should().Contain("The controller ended the session, so the app signed out.");
        harness.Calls(nameof(IRoofControllerServiceV4.Open)).Should().Be(0, "the refused Open is reported, not repeated with the device key");
    }

    [TestMethod]
    public async Task WithoutUse_TheAppSignsOutAfterTheIdleLock()
    {
        await using var harness = await CreateAsync(idleLock: TimeSpan.FromMinutes(15));
        await harness.StartLiveAsync();
        await harness.Console.SignInAsync(KioskHarness.Operator, TestSecrets.Password);

        await harness.AdvanceAsync(TimeSpan.FromMinutes(10), step: TimeSpan.FromMinutes(1));
        harness.Console.Touch();
        await harness.AdvanceAsync(TimeSpan.FromMinutes(10), step: TimeSpan.FromMinutes(1));
        harness.Console.View.IsUnlocked.Should().BeTrue("the click 10 min ago keeps the person signed in");

        await harness.AdvanceAsync(TimeSpan.FromMinutes(5), step: TimeSpan.FromMinutes(1));

        await harness.WaitForAsync(view => !view.IsUnlocked, "the idle sign-out");
        harness.Console.View.Notices[0].Text.Should().Be("Signed out after 15 min without use.");
        await ClientTestSupport.WaitUntilAsync(() => harness.Credential.PinSession is null, "the session to end");
    }

    [TestMethod]
    public async Task TheScreenNeverBlanks_AndTheControllersScreenTimeoutIsNotRead()
    {
        await using var harness = await CreateAsync(settings: [("RoofControllerUi:KioskScreenTimeout", "00:01:00")]);
        await harness.StartLiveAsync();

        await harness.AdvanceAsync(TimeSpan.FromMinutes(10), step: TimeSpan.FromMinutes(1));

        harness.Console.View.IsBlank.Should().BeFalse();
        harness.Console.View.ScreenTimeout.Should().Be(TimeSpan.Zero);
        harness.Console.Touch().Should().BeFalse("a click always reaches the control under it");
        harness.Sent(HttpMethod.Get, RoofApiRoutes.Settings).Should().Be(0, "the app does not blank, so it has no use for the kiosk's screen timeout");
    }

    [TestMethod]
    public async Task ARefusedDeviceKey_IsSaidSo_InTheAppsWords()
    {
        await using var harness = await CreateAsync(settings: [("RoofControllerSecurity:ApiKeys:4:Key", "test-other-viewer-key-not-a-real-secret-08")]);

        harness.Console.Start();

        await harness.WaitForAsync(view => view.FeedRefused, "the refused key");
        harness.Console.View.FeedBanner.Should().Be(
            $"The controller refused this app's device key, so there is no live status. {KioskText.StopStillTried}");
    }
}
