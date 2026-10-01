using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Installer;
using HVO.RoofControllerV4.Installer.Answers;
using HVO.RoofControllerV4.Installer.Machine;
using HVO.RoofControllerV4.Installer.Plan;
using HVO.RoofControllerV4.Installer.Record;
using HVO.RoofControllerV4.Installer.Roles;
using HVO.RoofControllerV4.Installer.Wizard;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.TerminalUi;
using Terminal.Gui.Input;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.RPi.Tests.Installer;

/// <summary>
/// The wizard's page for hvo-roof and the Mac app (#71): the controller's address, its CA fetched there and trusted only
/// once the person says its fingerprint is the one the controller shows (or a pin, or no pin), the admin who makes the
/// Mac's device key and the login keychain on a Mac; then the install, and one that stops part way.
/// </summary>
[TestClass]
[UnsupportedOSPlatform("windows")]
public sealed class InstallerWizardClientTests
{
    private const string Credentials = "/home/roy/.config/hvo-roof/credentials.json";
    private const string Admin = "ada";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task HvoRoof_TrustsTheControllersCa_OnlyOnceThePersonSaysItsFingerprintIsTheOneShown()
    {
        using var laptop = Laptop();
        using var wizard = await StartAsync(laptop, InstallRole.Cli);
        var page = (ClientPage)wizard.Page;

        wizard.Wizard.Header.Should().Be("Step 4 of 7: Connecting to the controller");
        page.Address!.Text.Should().BeEmpty();
        page.Matches!.Enabled.Should().BeFalse("there is no CA to check yet");
        page.Keychain.Should().BeNull("only a Mac's login keychain is offered");
        page.Admin.Should().BeNull("only the Mac app has a device key");
        wizard.Screen.Should().Contain("The controller hvo-roof connects to").And.Contain("How hvo-roof trusts it")
            .And.Contain("Fetch its CA to see its fingerprint, and compare it with the one the controller shows.");

        wizard.Press(Key.Enter);
        wizard.Page.Should().BeSameAs(page);
        wizard.Wizard.Message.Should().Be("The controller's address must be an absolute http or https address (https://roof.local:8443).");

        page.Address.Text = FakeMachine.ControllerUrl;
        wizard.Press(Key.Enter);
        wizard.Page.Should().BeSameAs(page);
        wizard.Wizard.Message.Should().StartWith("Fetch the controller's CA, check its fingerprint is the one the controller shows, and tick that it is");

        Fetch(wizard, page);

        page.Authority!.Text.Should().Be($"It serves {FakeMachine.ControllerCaName}, whose SHA-256 fingerprint is\n"
            + "  FB:26:8B:E2:34:20:52:AF:FB:D8:05:14:87:EB:B5:D6:\n"
            + "  4A:D8:6D:4E:65:BC:35:26:C7:D2:31:11:2A:D1:C0:76\n"
            + "Does it match the controller's installer's Done page, or hvo-roof-install cert show on it?");
        page.Matches.Enabled.Should().BeTrue();
        page.Matches.Value.Should().Be(CheckState.UnChecked, "the person says whether it matches");
        laptop.CaFetches.Should().Equal(new Uri(FakeMachine.ControllerUrl));
        page.Address.SetFocus();
        wizard.Press(Key.Enter);
        wizard.Page.Should().BeSameAs(page, "the fingerprint is not ticked as the one the controller shows");

        page.Matches.Value = CheckState.Checked;
        wizard.Render(TestContext, "4-client");
        wizard.NextTo(5);

        wizard.Session.Answers.Client.Should().Be(new ClientSettings { Controller = FakeMachine.ControllerUrl, CaSha256 = FakeMachine.ControllerCaSha256 });
        wizard.Page.Should().BeOfType<ReviewPage>();
        wizard.Wizard.NextButton.Text.Should().Be("Install", "hvo-roof needs no password");
        wizard.Page.Describe().Should().Contain(line => line.Contains(Credentials, StringComparison.Ordinal) && line.Contains("trusting its CA (FB:26:8B:E2…)", StringComparison.Ordinal));

        wizard.Press(Key.Enter);
        wizard.WaitIdle("the install", () => wizard.Page is DonePage);

        wizard.Wizard.Result.Should().Be(InstallerExitCode.Success, string.Join("\n", wizard.Page.Describe()));
        wizard.Page.Describe().Should().Contain($"hvo-roof: /home/roy/.local/bin/hvo-roof, connected to {FakeMachine.ControllerUrl} ({Credentials}).")
            .And.Contain("Next, sign in as yourself: hvo-roof login NAME");
        laptop.Read(Credentials).Should().Contain("-----BEGIN CERTIFICATE-----");
    }

    [TestMethod]
    public async Task AFingerprintChecked_IsForTheAddressItWasFetchedFrom()
    {
        using var laptop = Laptop();
        using var wizard = await StartAsync(laptop, InstallRole.Cli);
        var page = (ClientPage)wizard.Page;
        page.Address!.Text = FakeMachine.ControllerUrl;
        Fetch(wizard, page);
        page.Matches!.Value = CheckState.Checked;

        page.Address.Text = "https://roof.example.org:8443";

        page.Matches.Value.Should().Be(CheckState.UnChecked);
        page.Matches.Enabled.Should().BeFalse("the CA checked is another address's");
        page.Authority!.Text.Should().Be("Fetch its CA to see its fingerprint, and compare it with the one the controller shows.");

        page.Address.Text = FakeMachine.ControllerUrl;
        page.Matches.Enabled.Should().BeTrue("the CA fetched is this address's again");
        page.Matches.Value.Should().Be(CheckState.UnChecked, "it is ticked again, not remembered");
    }

    [TestMethod]
    public async Task WhileItsCaIsBeingFetched_ThePageCannotBeLeft_AndTheCaFetchedIsComparedAfresh()
    {
        using var laptop = Laptop();
        using var wizard = await StartAsync(laptop, InstallRole.Cli);
        var page = (ClientPage)wizard.Page;
        page.Address!.Text = FakeMachine.ControllerUrl;
        Fetch(wizard, page);
        page.Matches!.Value = CheckState.Checked;
        wizard.NextTo(5);
        wizard.Press(Key.Esc);
        wizard.Page.Should().BeSameAs(page);
        page.Matches!.Value.Should().Be(CheckState.Checked, "the CA checked before is this address's");

        // Fetched again, slowly: the page is left neither way while it is on its way.
        var answer = new TaskCompletionSource<System.Security.Cryptography.X509Certificates.X509Certificate2>(TaskCreationOptions.RunContinuationsAsynchronously);
        laptop.FetchCa = (_, _) => answer.Task;
        page.FetchButton!.SetFocus();
        wizard.Press(Key.Enter);
        wizard.PumpUntil("the fetch", () => page.Authority!.Text.StartsWith("Fetching", StringComparison.Ordinal));
        wizard.Wizard.BackButton.Enabled.Should().BeFalse();
        wizard.Wizard.NextButton.Enabled.Should().BeFalse();
        wizard.Press(Key.Esc);
        wizard.Page.Should().BeSameAs(page, "a fetch still on its way would land on the page shown again");

        answer.SetResult(RoofCertificateAuthority.FromPem(FakeMachine.ControllerCaPem));
        wizard.WaitIdle("the CA", () => !page.Authority!.Text.StartsWith("Fetching", StringComparison.Ordinal));

        page.Matches!.Value.Should().Be(CheckState.UnChecked, "a CA just fetched is compared again, whatever was ticked before");
        wizard.Wizard.BackButton.Enabled.Should().BeTrue();
        wizard.Press(Key.Esc);
        wizard.Page.Should().NotBeSameAs(page);
    }

    [TestMethod]
    public async Task ACaThatCannotBeFetched_SaysWhy()
    {
        using var laptop = Laptop();
        laptop.FetchCa = (_, _) => throw new HttpRequestException("Connection refused (roofpi.local:8443)");
        using var wizard = await StartAsync(laptop, InstallRole.Cli);
        var page = (ClientPage)wizard.Page;
        page.Address!.Text = FakeMachine.ControllerUrl;

        Fetch(wizard, page);

        page.Authority!.Text.Should().Be("The installer could not fetch the controller's CA from https://roofpi.local:8443/: Connection refused (roofpi.local:8443).");
        InstallerWizardTests.ShouldHaveColours(wizard.ColoursOf("The installer could not fetch"), RoofUiPalette.DangerText, RoofUiPalette.DangerBackground);
        page.Matches!.Enabled.Should().BeFalse();

        page.Address.Text = "https://roof.example.org:8443";
        page.Authority.Text.Should().StartWith("Fetch its CA", "what failed was another address");
    }

    [TestMethod]
    public async Task OnlyAnHttpsAddress_HasACaToFetch()
    {
        using var laptop = Laptop();
        using var wizard = await StartAsync(laptop, InstallRole.Cli);
        var page = (ClientPage)wizard.Page;
        page.Address!.Text = "http://roofpi.local:8080";

        page.FetchButton!.SetFocus();
        wizard.Press(Key.Enter);

        wizard.Wizard.Message.Should().Be("Only an https address has a CA to fetch.");
        laptop.CaFetches.Should().BeEmpty();
        wizard.Page.Should().BeSameAs(page, "Enter on the button fetches; it is not Next");
    }

    [TestMethod]
    [DataRow(1, "https://roofpi.local:8443", "ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89", "AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89")]
    [DataRow(2, "https://roof.example.org", "", null)]
    [DataRow(2, "http://roofpi.local:8080", "", null)]
    public async Task APinOrNoPin_IsChosenInstead_AndNoCaIsFetched(int trust, string address, string pin, string? pinned)
    {
        using var laptop = Laptop();
        using var wizard = await StartAsync(laptop, InstallRole.Cli);
        var page = (ClientPage)wizard.Page;
        page.Address!.Text = address;

        page.Trust!.Value = trust;
        page.Pin!.Text = pin;

        page.Matches!.Visible.Should().BeFalse();
        page.FetchButton!.Enabled.Should().BeFalse();
        page.Pin.Visible.Should().Be(trust == 1);
        wizard.NextTo(5);
        wizard.Session.Answers.Client.Should().Be(new ClientSettings { Controller = address, CertificateSha256 = pinned });
        laptop.CaFetches.Should().BeEmpty();
    }

    [TestMethod]
    public async Task APinThatIsNotAFingerprint_IsRefused()
    {
        using var laptop = Laptop();
        using var wizard = await StartAsync(laptop, InstallRole.Cli);
        var page = (ClientPage)wizard.Page;
        page.Address!.Text = FakeMachine.ControllerUrl;
        page.Trust!.Value = 1;
        page.Pin!.Text = "AB:CD";

        wizard.Press(Key.Enter);

        wizard.Page.Should().BeSameAs(page);
        wizard.Wizard.Message.Should().Be("The controller's certificate's fingerprint must be 64 hex digits (colons between pairs allowed), not 'AB:CD'.");
    }

    [TestMethod]
    public async Task TheMacApp_AsksForItsAdmin_AndOffersTheKeychain_ThenInstalls()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, Admin, RoofControllerApiContract.AdminRole);
        using var mac = Mac(host);
        using var wizard = await StartAsync(mac, InstallRole.MacApp);
        var page = (ClientPage)wizard.Page;

        wizard.Screen.Should().Contain("The controller the Mac app connects to").And.Contain("The Mac app's device key")
            .And.Contain("Trust it in my login keychain too, for Safari and Chrome (macOS asks for your password)");
        page.Keychain!.Value.Should().Be(CheckState.UnChecked, "the keychain is changed only when asked");
        page.Address!.Text = FakeMachine.ControllerUrl;
        Fetch(wizard, page);
        page.Matches!.Value = CheckState.Checked;
        page.Address.SetFocus();
        wizard.Press(Key.Enter);
        wizard.Page.Should().BeSameAs(page);
        wizard.Wizard.Message.Should().Be("Give the admin who makes the Mac's device key, by the name they sign in with.");

        page.Admin!.Text = Admin;
        page.Keychain.Value = CheckState.Checked;
        wizard.Render(TestContext, "4-client-mac");
        wizard.NextTo(5);

        wizard.Session.Answers.Client.Should().Be(new ClientSettings { Controller = FakeMachine.ControllerUrl, CaSha256 = FakeMachine.ControllerCaSha256, TrustInKeychain = true });
        wizard.Session.Answers.MacApp!.Admin.Should().Be(Admin);

        // Back to the folder: the admin given, and the CA checked, stay.
        wizard.Press(Key.Esc);
        wizard.Press(Key.Esc);
        ((SettingsPage)wizard.Page).MacAppFolder!.Value = 1;
        wizard.NextTo(4);
        ((ClientPage)wizard.Page).Admin!.Text.Should().Be(Admin);
        ((ClientPage)wizard.Page).Matches!.Value.Should().Be(CheckState.Checked, "the CA was checked for this address");
        wizard.NextTo(5);
        wizard.Session.Answers.MacApp.Should().Be(new MacAppSettings { Folder = MacAppSettings.HomeFolder, Admin = Admin });

        wizard.Press(Key.Enter);
        var passwords = (PasswordsPage)wizard.Page;
        passwords.Describe().Should().Equal("It is never shown, saved or logged.", "The Mac app's admin's password: typed once");
        passwords.Fields[0].Typed.SetFocus();
        wizard.Type(TestSecrets.Password);
        wizard.Press(Key.Enter);
        wizard.WaitIdle("the install", () => wizard.Page is DonePage);

        wizard.Wizard.Result.Should().Be(InstallerExitCode.Success, string.Join("\n", wizard.Page.Describe()));
        wizard.Page.Describe().Should().Contain($"The Mac app: /Users/roy/Applications/HVO Roof.app, connected to {FakeMachine.ControllerUrl}.")
            .And.Contain("Safari and Chrome trust the controller's CA: it is in your login keychain.");
        mac.KeychainTrusted.Should().ContainKey(FakeMachine.ControllerCaSha256.Replace(":", string.Empty, StringComparison.Ordinal));
        mac.MacChecks.Should().ContainSingle();
        mac.AllText().Should().NotContain(TestSecrets.Password);
    }

    [TestMethod]
    public async Task AnInstallThatStops_SaysWhere_AndThatRunningItAgainCarriesOn()
    {
        using var laptop = Laptop();
        var path = InstallPaths.Log(laptop.Machine);
        laptop.Folder(Path.GetDirectoryName(path)!);
        using var wizard = await StartAsync(laptop, InstallRole.Cli, InstallLog.Open(laptop.Machine, path, TimeProvider.System));
        var page = (ClientPage)wizard.Page;
        page.Address!.Text = FakeMachine.ControllerUrl;
        Fetch(wizard, page);
        page.Matches!.Value = CheckState.Checked;
        wizard.NextTo(5);

        // The controller goes away between the review and the install.
        laptop.FetchCa = (_, _) => throw new HttpRequestException("Connection refused (roofpi.local:8443)");
        wizard.Press(Key.Enter);
        wizard.WaitIdle("the install", () => wizard.Page is DonePage);

        wizard.Wizard.Result.Should().Be(InstallerExitCode.Failed);
        wizard.Wizard.Failure.Should().StartWith("The install stopped: ")
            .And.Contain(Credentials)
            .And.Contain("the installer could not fetch the controller's CA from https://roofpi.local:8443/: Connection refused (roofpi.local:8443).");
        wizard.Page.Describe().Skip(1).Should().Equal(
            "Nothing after that step was changed.",
            $"The log: {path}",
            "Run the installer again to carry on: it changes only what is left.");
        InstallerWizardTests.ShouldHaveColours(wizard.ColoursOf("The install stopped"), RoofUiPalette.DangerText, RoofUiPalette.DangerBackground);
        laptop.Exists("/home/roy/.local/bin/hvo-roof").Should().BeTrue("the step before it ran");
        laptop.Exists(Credentials).Should().BeFalse();
        laptop.Read(path).Should().Contain("The install stopped: ");
        wizard.Render(TestContext, "8-stopped");
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static FakeMachine Laptop()
    {
        var laptop = new FakeMachine(architecture: Architecture.X64, root: false, hostName: "laptop", userName: "roy");
        laptop.Machine.CreateDirectory("/home/roy/.local/bin", Modes.Folder);
        return laptop;
    }

    // A Mac with its Applications folder, whose API calls reach host.
    private static FakeMachine Mac(RoofApiTestHost host)
    {
        var mac = new FakeMachine(InstallerOs.MacOS, Architecture.Arm64, root: false, hostName: "studio", userName: "roy")
        {
            ApiHandler = () => ClientTestSupport.CreateHandler(() => host.Server)
        };
        mac.Machine.CreateDirectory("/Applications", Modes.Folder);
        return mac;
    }

    // The wizard on machine, with role chosen, on the page for the controller it connects to.
    private static async Task<WizardDriver> StartAsync(FakeMachine machine, InstallRole role, InstallLog? log = null)
    {
        var wizard = await WizardDriver.StartAsync(machine, log);
        wizard.NextTo(1);
        ((RolesPage)wizard.Page).Choices[role].Value = CheckState.Checked;
        wizard.NextTo(4);
        wizard.Page.Should().BeOfType<ClientPage>();
        return wizard;
    }

    // Presses Fetch its CA, as a person would (Tab to it, Enter), and waits for the answer.
    private static void Fetch(WizardDriver wizard, ClientPage page)
    {
        page.FetchButton!.SetFocus();
        wizard.Press(Key.Enter);
        wizard.WaitIdle("the CA", () => !page.Authority!.Text.StartsWith("Fetching", StringComparison.Ordinal));
    }
}
