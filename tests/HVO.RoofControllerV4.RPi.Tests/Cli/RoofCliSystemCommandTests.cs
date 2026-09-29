using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Settings;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// <c>hvo-roof info</c>, <c>restart</c>, <c>setup</c> and the refusals of <c>ui</c> against the real API, in process (#45):
/// what each writes as text and JSON, the confirmation a restart needs (and the safety-critical hand edit it would
/// load), and how setup saves the credentials file (owner only), checks the connection and adds the first admin.
/// </summary>
[TestClass]
public sealed class RoofCliSystemCommandTests
{
    private const string NotConfirmed = "Not confirmed: run the command again with --force.";
    private const string RestartQuestion = "Restart the controller? It stops the roof first, and does not answer until it has started again.";
    private const string Restarting = "The roof is stopped. The controller is restarting; it answers again once it has started.";
    private const string CertificateHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // ---- info ---------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Info_WithAnAdminKey_DescribesTheControllerAndItsResourceUse()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("info");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().MatchRegex(@"(?m)^Application:\s+\S.* \(Development\)\s*$")
            .And.MatchRegex($@"(?m)^Host:\s+{Regex.Escape(Environment.MachineName)}\s*$")
            .And.MatchRegex(@"(?m)^System:\s+\S")
            .And.MatchRegex(@"(?m)^Runtime:\s+\S")
            .And.MatchRegex(@"(?m)^Started:\s+\S.*\(up .+\)\s*$")
            .And.MatchRegex(@"(?m)^Memory:\s+\S.* working set, \S.* managed\s*$")
            .And.MatchRegex(@"(?m)^CPU:\s+\d+\.\d %, \d+ threads\s*$");
    }

    [TestMethod]
    public async Task Info_Json_HoldsTheInformationAndTheMetrics()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("info", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        var information = result.Json.GetProperty("information");
        information.GetProperty("environmentName").GetString().Should().Be("Development");
        information.GetProperty("machineName").GetString().Should().Be(Environment.MachineName);
        information.GetProperty("applicationName").GetString().Should().NotBeNullOrWhiteSpace();
        result.Json.GetProperty("metrics").GetProperty("threadCount").GetInt32().Should().BePositive();
    }

    [TestMethod]
    [DataRow(TestApiKeys.Operator)]
    [DataRow(TestApiKeys.Viewer)]
    public async Task Info_WithoutAnAdminCredential_IsForbidden(string apiKey)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(apiKey);

        var result = await rig.RunAsync("info");

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        result.Out.Should().BeEmpty();
        result.Error.Should().Contain("Your role does not permit this.");
    }

    [TestMethod]
    public async Task Info_WithAWrongKey_IsSignedOut_AsJson()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey("wrong-key-not-a-real-secret-000000");

        var result = await rig.RunAsync("info", "--json");

        result.Code.Should().Be(RoofExitCode.SignedOut, result.ToString());
        var error = result.Json.GetProperty("error");
        error.GetProperty("exitCode").GetInt32().Should().Be((int)RoofExitCode.SignedOut);
        error.GetProperty("status").GetInt32().Should().Be(401);
    }

    [TestMethod]
    public async Task Info_WithoutAController_IsNotConfigured()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);

        var result = await rig.RunAsync("info");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().Contain("No controller address. Run 'hvo-roof setup', set HVO_ROOF_URL, or pass --controller.");
    }

    // ---- restart ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Restart_WithoutForce_AndNoTerminal_SendsNothing()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var text = await rig.RunAsync("restart");
        rig.Interactive = true;
        var json = await rig.RunAsync("restart", "--json");

        text.Code.Should().Be(RoofExitCode.ConfirmationRequired, text.ToString());
        text.Out.Should().BeEmpty();
        text.Error.Should().Contain($"{RestartQuestion} {NotConfirmed}");
        json.Code.Should().Be(RoofExitCode.ConfirmationRequired, json.ToString());
        json.Error.Should().Contain(NotConfirmed, "--json never prompts");
        rig.Prompts.Should().BeEmpty();
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
        host.Services.GetRequiredService<RoofRestartSignal>().Requested.Should().BeFalse();
    }

    [TestMethod]
    public async Task Restart_InATerminal_No_SendsNothing()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue("no");

        var result = await rig.RunAsync("restart");

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        result.Error.Should().Contain("Not confirmed; nothing was sent.");
        rig.Prompts.Should().Equal(($"{RestartQuestion} [y/N] ", false));
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    public async Task Restart_InATerminal_Yes_StopsTheRoofAndRestarts()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue("y");

        var result = await rig.RunAsync("restart");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be(Restarting + Environment.NewLine + "'hvo-roof health --probe ready' says when it is back." + Environment.NewLine);
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.HostShutdown), Times.Once());
        var signal = host.Services.GetRequiredService<RoofRestartSignal>();
        await ClientTestSupport.WaitUntilAsync(() => signal.Requested, "the restart to be requested");
    }

    [TestMethod]
    public async Task Restart_WithForce_Json_HoldsTheMessageAndTheExitCode()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("restart", "--force", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("message").GetString().Should().Be(Restarting);
        result.Json.GetProperty("exitCode").GetInt32().Should().Be(RoofSettingsContract.RestartExitCode);
        rig.Prompts.Should().BeEmpty();
        host.RoofService.Verify(service => service.Stop(RoofControllerStopReason.HostShutdown), Times.Once());
    }

    [TestMethod]
    public async Task Restart_WhenTheStopCannotBeVerified_IsRefusedByTheController()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "The relays could not be read back.")));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);

        var result = await rig.RunAsync("restart", "--force");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Out.Should().BeEmpty();
        result.Error.Should().Contain("The controller refused to restart. [RestartRefused]")
            .And.Contain("The roof stop could not be verified");
        host.Services.GetRequiredService<RoofRestartSignal>().Requested.Should().BeFalse();
    }

    [TestMethod]
    public async Task Restart_WithAnOperatorKey_IsForbidden()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("restart", "--force");

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    public async Task Restart_WithASafetyCriticalHandEditPending_ShowsIt_AndNeedsConfirming()
    {
        using var files = new SettingsFiles();
        var roof = new SettingsApiTests.RoofDouble();
        using var host = files.StartHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        files.WriteHandEdit("{ \"RoofControllerOptionsV4\": { \"OpenRelayId\": 2, \"CloseRelayId\": 1 } }");

        var result = await rig.RunAsync("restart", "--force");

        result.Code.Should().Be(RoofExitCode.ConfirmationRequired, result.ToString());
        result.Out.Should().Contain("The settings file was edited by hand.")
            .And.MatchRegex(@"(?m)^  RoofControllerOptionsV4:OpenRelayId: .+ -> .+\[SAFETY-CRITICAL\]\s*$");
        result.Error.Should().Contain("--confirm-safety-critical");
        roof.Mock.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    public async Task Restart_WithASafetyCriticalHandEdit_AndConfirmation_Restarts()
    {
        using var files = new SettingsFiles();
        var roof = new SettingsApiTests.RoofDouble();
        using var host = files.StartHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        files.WriteHandEdit("{ \"RoofControllerOptionsV4\": { \"OpenRelayId\": 2, \"CloseRelayId\": 1 } }");

        var result = await rig.RunAsync("restart", "--force", "--confirm-safety-critical");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().StartWith(Restarting);
        roof.Mock.Verify(service => service.Stop(RoofControllerStopReason.HostShutdown), Times.Once());
        var signal = host.Services.GetRequiredService<RoofRestartSignal>();
        await ClientTestSupport.WaitUntilAsync(() => signal.Requested, "the restart to be requested");
    }

    // ---- setup: saving ------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Setup_WithAControllerAndAnApiKey_SavesThem_AndChecksTheConnection()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.Input.Enqueue(TestApiKeys.Operator);

        var result = await rig.RunAsync("setup", "--controller", "http://localhost/", "--api-key");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal(("API key: ", true));
        result.Out.Should().Contain($"Saved in {rig.CredentialsPath}: http://localhost/, API key.")
            .And.MatchRegex(@"(?m)^Reachable: the controller says it is \S+\.\s*$")
            .And.Contain("Signed in as test-operator (operator, API key).")
            .And.NotContain("Nobody can sign in yet", "only an admin key can tell")
            .And.NotContain(TestApiKeys.Operator);
        result.Error.Should().BeEmpty();
        var stored = rig.Stored!;
        stored.Controller.Should().Be(new Uri("http://localhost/"));
        stored.ApiKey.Should().Be(TestApiKeys.Operator);
        stored.CertificateSha256.Should().BeNull();
    }

    [TestMethod]
    public async Task Setup_SavesTheFileForItsOwnerOnly_InADirectoryOnlyTheOwnerMayUse()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        var directory = Path.Combine(rig.Directory, "hvo-roof");
        var path = Path.Combine(directory, "credentials.json");
        rig.Input.Enqueue(TestApiKeys.Viewer);

        var result = await RunWithCredentialsFileAsync(rig, path, "setup", "--controller", "http://localhost/", "--api-key");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain($"Saved in {path}: http://localhost/, API key.");
        RoofCredentialStore.Load(path)!.ApiKey.Should().Be(TestApiKeys.Viewer);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.GetUnixFileMode(directory).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [TestMethod]
    public async Task Setup_WithAFileOthersCanRead_IsRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        File.SetUnixFileMode(rig.CredentialsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        var result = await rig.RunAsync("setup", "--controller", "http://localhost/");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().Contain($"{rig.CredentialsPath} can be read or changed by other users. Run 'chmod 600 {rig.CredentialsPath}', then try again.");
    }

    [TestMethod]
    public async Task Setup_WithoutAControllerAndNoTerminal_IsNotConfigured()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);

        var result = await rig.RunAsync("setup");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().Contain("No controller address. Give it with --controller, for example 'hvo-roof setup --controller https://roof.local:5001/'.");
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("ftp://roof.local/")]
    [DataRow("roof.local")]
    public async Task Setup_WithAnInvalidControllerAddress_IsAUsageError(string address)
    {
        using var rig = new CliRig((RoofApiTestHost?)null);

        var result = await rig.RunAsync("setup", "--controller", address);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain($"'{address}' is not a controller address. Give an http or https address, for example https://roof.local:5001/.");
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    public async Task Setup_WithAnInvalidTypedAddress_IsAUsageError()
    {
        using var rig = new CliRig((RoofApiTestHost?)null) { Interactive = true };
        rig.Input.Enqueue("not an address");

        var result = await rig.RunAsync("setup");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        rig.Prompts.Should().Equal(("Controller address (for example https://roof.local:5001/): ", false));
        result.Error.Should().Contain("'not an address' is not a controller address.");
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    public async Task Setup_CertificateSha256_IsSavedAsUppercaseHex_AndNoneRemovesIt()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);
        var withColons = string.Join(':', Enumerable.Range(0, 32).Select(i => CertificateHex.Substring(i * 2, 2)));

        var pinned = await rig.RunAsync("setup", "--controller", "http://localhost/", "--certificate-sha256", withColons);
        var pin = rig.Stored!.CertificateSha256;
        var removed = await rig.RunAsync("setup", "--certificate-sha256", "none");

        pinned.Code.Should().Be(RoofExitCode.Success, pinned.ToString());
        pinned.Out.Should().Contain("http://localhost/, certificate pinned, API key.");
        pin.Should().Be(CertificateHex.ToUpperInvariant());
        removed.Code.Should().Be(RoofExitCode.Success, removed.ToString());
        removed.Out.Should().Contain("http://localhost/, API key.").And.NotContain("certificate pinned");
        rig.Stored!.CertificateSha256.Should().BeNull();
        rig.Stored!.ApiKey.Should().Be(TestApiKeys.Viewer, "a setup without --api-key keeps the saved key");
    }

    [TestMethod]
    [DataRow("0123")]
    [DataRow("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task Setup_CertificateSha256_ThatIsNotAHash_IsAUsageError(string value)
    {
        using var rig = new CliRig((RoofApiTestHost?)null);

        var result = await rig.RunAsync("setup", "--controller", "http://localhost/", "--certificate-sha256", value);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("The certificate SHA-256 must be 64 hex digits (colons allowed), or 'none'.");
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    [DataRow(null, "No value was given for: API key.")]
    [DataRow("bad\u0007key-not-a-real-secret", RoofCredential.InvalidHeaderValue)]
    public async Task Setup_ApiKey_ThatIsMissingOrCannotBeSent_IsAUsageError(string? answer, string expected)
    {
        using var rig = new CliRig((RoofApiTestHost?)null);
        rig.Input.Enqueue(answer);

        var result = await rig.RunAsync("setup", "--controller", "http://localhost/", "--api-key");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain(expected);
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    // ---- setup: the check ---------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Setup_WithoutACredential_SaysHowToSignIn()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);

        var result = await rig.RunAsync("setup", "--controller", "http://localhost/");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain($"Saved in {rig.CredentialsPath}: http://localhost/.")
            .And.Contain("No credential is saved: sign in with 'hvo-roof login NAME', or save an API key with 'hvo-roof setup --api-key'.");
    }

    [TestMethod]
    public async Task Setup_WithAWrongKey_SavesIt_ButReportsItWasNotAccepted()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.Input.Enqueue("wrong-key-not-a-real-secret-000000");

        var result = await rig.RunAsync("setup", "--controller", "http://localhost/", "--api-key");

        result.Code.Should().Be(RoofExitCode.SignedOut, result.ToString());
        result.Out.Should().Contain("The credential was not accepted: Not signed in, or the credential is no longer valid. Sign in again.");
        rig.Stored!.ApiKey.Should().Be("wrong-key-not-a-real-secret-000000");
    }

    [TestMethod]
    public async Task Setup_WithAnUnreachableController_SavesIt_ButReportsIt()
    {
        using var rig = new CliRig(() => throw new ObjectDisposedException("controller"));
        rig.Input.Enqueue(TestApiKeys.Admin);

        var text = await rig.RunAsync("setup", "--controller", "http://localhost/", "--api-key");
        var json = await rig.RunAsync("setup", "--json");

        text.Code.Should().Be(RoofExitCode.Unreachable, text.ToString());
        text.Out.Should().Contain($"Saved in {rig.CredentialsPath}: http://localhost/, API key.")
            .And.MatchRegex(@"(?m)^Not reachable: \S");
        rig.Stored!.ApiKey.Should().Be(TestApiKeys.Admin);
        json.Code.Should().Be(RoofExitCode.Unreachable, json.ToString());
        json.Json.GetProperty("reachable").GetBoolean().Should().BeFalse();
        json.Json.GetProperty("exitCode").GetInt32().Should().Be((int)RoofExitCode.Unreachable);
        json.Json.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task Setup_Json_DescribesTheSavedFileAndTheCheck_WithoutTheKey()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.Input.Enqueue(TestApiKeys.Admin);

        var result = await rig.RunAsync("setup", "--controller", "http://localhost/", "--api-key", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().NotContain(TestApiKeys.Admin);
        var json = result.Json;
        json.GetProperty("controller").GetString().Should().Be("http://localhost/");
        json.GetProperty("credentialsFile").GetString().Should().Be(rig.CredentialsPath);
        json.GetProperty("certificatePinned").GetBoolean().Should().BeFalse();
        json.GetProperty("credential").GetString().Should().Be("API key");
        json.GetProperty("reachable").GetBoolean().Should().BeTrue();
        json.GetProperty("live").GetString().Should().NotBeNullOrWhiteSpace();
        json.GetProperty("caller").GetProperty("name").GetString().Should().Be("test-admin");
        json.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("exitCode").GetInt32().Should().Be(0);
        json.GetProperty("createdAdmin").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestMethod]
    public async Task Setup_WithAnAdminKeyAndNobody_SaysTheFirstAdminIsMissing()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.Input.Enqueue(TestApiKeys.Admin);

        var result = await rig.RunAsync("setup", "--controller", "http://localhost/", "--api-key");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("Signed in as test-admin (admin, API key).")
            .And.Contain("Nobody can sign in yet. Add the first admin person with 'hvo-roof setup --create-admin NAME'.");
        rig.Prompts.Should().Equal(("API key: ", true));
    }

    [TestMethod]
    public async Task Setup_WithASavedSession_KeepsIt_AndSaysItIsUsedBeforeTheKey()
    {
        using var host = RoofClientApiTests.CreateHost();
        await RoofClientApiTests.AddUserAsync(host, "alice", RoofControllerApiContract.OperatorRole);
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        rig.Input.Enqueue(TestSecrets.Password);
        (await rig.RunAsync("login", "alice")).Code.Should().Be(RoofExitCode.Success);
        rig.Input.Enqueue(TestApiKeys.Admin);

        var result = await rig.RunAsync("setup", "--api-key");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("The saved session for alice is used before the key; sign out to use the key.")
            .And.Contain("Signed in as alice (operator, session).");
        rig.Stored!.Session!.Name.Should().Be("alice");
        rig.Stored!.ApiKey.Should().Be(TestApiKeys.Admin);
    }

    [TestMethod]
    public async Task Setup_WithACredentialInTheEnvironment_SaysItWins()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.Environment[RoofCredentialStore.ApiKeyVariable] = TestApiKeys.Viewer;
        rig.Input.Enqueue(TestApiKeys.Operator);

        var result = await rig.RunAsync("setup", "--controller", "http://localhost/", "--api-key");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("Signed in as test-operator (operator, API key).", "setup checks what it saved");
        result.Error.Should().Contain(
            "Note: HVO_ROOF_API_KEY or HVO_ROOF_SESSION is set, and commands use it instead of the saved credential.");
    }

    [TestMethod]
    [DataRow(false, DisplayName = "text")]
    [DataRow(true, DisplayName = "--json")]
    public async Task Setup_WithABrokenVariableInTheEnvironment_IsNotConfigured_AndAsksAndSavesNothing(bool json)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.Environment[RoofCredentialStore.ControllerVariable] = "ftp://roof.invalid/";
        rig.Input.Enqueue(TestApiKeys.Operator);
        string[] args = ["setup", "--controller", ClientTestSupport.BaseAddress.ToString(), "--api-key"];

        var result = json ? await rig.RunAsync([.. args, "--json"]) : await rig.RunAsync(args);

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        if (json)
        {
            // One document: the error, and nothing written before it.
            result.Json.GetProperty("error").GetProperty("message").GetString().Should().Be("HVO_ROOF_URL is not an absolute http or https URL.");
            result.Error.Should().BeEmpty();
        }
        else
        {
            result.Error.Should().Be("HVO_ROOF_URL is not an absolute http or https URL." + Environment.NewLine);
            result.Out.Should().BeEmpty();
        }

        rig.Prompts.Should().BeEmpty("the key is not asked for when it could not be used");
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    // ---- setup: in a terminal -----------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Setup_InATerminal_AsksForTheAddressAndTheKey()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        rig.Input.Enqueue("http://localhost");
        rig.Input.Enqueue(TestApiKeys.Operator);

        var result = await rig.RunAsync("setup");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal(
            ("Controller address (for example https://roof.local:5001/): ", false),
            ("API key (Enter to skip, and sign in later with 'hvo-roof login NAME'): ", true));
        result.Out.Should().Contain("Signed in as test-operator (operator, API key).");
        rig.Stored!.Controller.Should().Be(new Uri("http://localhost/"), "the address gets its trailing slash");
    }

    [TestMethod]
    public async Task Setup_InATerminal_EnterKeepsWhatIsSaved()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(string.Empty);
        rig.Input.Enqueue(string.Empty);
        rig.Input.Enqueue(string.Empty);

        var result = await rig.RunAsync("setup");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal(
            ("Controller address [http://localhost/]: ", false),
            ("API key (Enter keeps the saved credential): ", true),
            ("Add the first admin person now? [y/N] ", false));
        rig.Stored!.ApiKey.Should().Be(TestApiKeys.Admin);
        (await UsersAsync(host)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task Setup_InATerminal_ForHttps_AsksForTheCertificate()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        rig.Input.Enqueue(CertificateHex);
        rig.Input.Enqueue(string.Empty);

        var result = await rig.RunAsync("setup", "--controller", "https://localhost/");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal(
            ("Certificate SHA-256, for a self-signed certificate (Enter keeps none; 'none' removes it): ", false),
            ("API key (Enter to skip, and sign in later with 'hvo-roof login NAME'): ", true));
        result.Out.Should().Contain("https://localhost/, certificate pinned.");
        rig.Stored!.CertificateSha256.Should().Be(CertificateHex.ToUpperInvariant());
        rig.Stored!.ApiKey.Should().BeNull();
    }

    [TestMethod]
    public async Task Setup_InATerminal_AddsTheFirstAdmin_WhenAsked()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        foreach (var answer in new[] { "http://localhost/", TestApiKeys.Admin, "y", "root", TestSecrets.Password, TestSecrets.Password })
        {
            rig.Input.Enqueue(answer);
        }

        var result = await rig.RunAsync("setup");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal(
            ("Controller address (for example https://roof.local:5001/): ", false),
            ("API key (Enter to skip, and sign in later with 'hvo-roof login NAME'): ", true),
            ("Add the first admin person now? [y/N] ", false),
            ("Name: ", false),
            ("Password for root: ", true),
            ("Again: ", true));
        result.Out.Should().EndWith("Added root (admin). Sign in with 'hvo-roof login root'." + Environment.NewLine)
            .And.NotContain(TestSecrets.Password);
        (await UsersAsync(host)).Should().ContainSingle(user => user.Name == "root" && user.Role == RoofControllerApiContract.AdminRole);
    }

    [TestMethod]
    public async Task Setup_InATerminal_AnInvalidFirstAdminName_IsAUsageError()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Admin);
        foreach (var answer in new[] { string.Empty, string.Empty, "yes", "-root" })
        {
            rig.Input.Enqueue(answer);
        }

        var result = await rig.RunAsync("setup");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("'-root' is not a valid name: use 1 to 64 letters");
        (await UsersAsync(host)).Should().BeEmpty();
    }

    // ---- setup --create-admin -----------------------------------------------------------------------------------------

    [TestMethod]
    public async Task SetupCreateAdmin_WithAnAdminKey_AddsTheFirstAdmin_WhoCanSignIn()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.Input.Enqueue(TestApiKeys.Admin);
        rig.Input.Enqueue(TestSecrets.Password);

        var result = await rig.RunAsync("setup", "--controller", "http://localhost/", "--api-key", "--create-admin", "root");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal(("API key: ", true), ("Password for root: ", true));
        result.Out.Should().EndWith("Added root (admin). Sign in with 'hvo-roof login root'." + Environment.NewLine);
        rig.Input.Enqueue(TestSecrets.Password);
        var login = await rig.RunAsync("login", "root");
        login.Code.Should().Be(RoofExitCode.Success, login.ToString());
        login.Out.Should().StartWith("Signed in as root (");
    }

    [TestMethod]
    public async Task SetupCreateAdmin_Json_NamesWhoWasAdded()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue(TestSecrets.Password);

        var result = await rig.RunAsync("setup", "--create-admin", "root", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("createdAdmin").GetString().Should().Be("root");
        result.Out.Should().NotContain(TestSecrets.Password);
    }

    [TestMethod]
    public async Task SetupCreateAdmin_WithAnOperatorKey_IsAUsageError()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("setup", "--create-admin", "root");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("Adding a person needs an admin API key, and the saved credential is not one. Save one with --api-key.");
        rig.Prompts.Should().BeEmpty("no password is asked for");
        (await UsersAsync(host)).Should().BeEmpty();
    }

    [TestMethod]
    public async Task SetupCreateAdmin_WithAnInvalidName_SavesNothing()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);

        var result = await rig.RunAsync("setup", "--controller", "http://localhost/", "--create-admin", "root admin");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("'root admin' is not a valid name:");
        File.Exists(rig.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    public async Task SetupCreateAdmin_WithAShortPassword_AddsNobody()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Admin);
        rig.Input.Enqueue("too-short");

        var result = await rig.RunAsync("setup", "--create-admin", "root");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("A password must be 12 to 256 characters.");
        (await UsersAsync(host)).Should().BeEmpty();
    }

    // ---- ui -----------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Ui_WithJson_IsAUsageError_EvenInATerminal()
    {
        using var rig = new CliRig((RoofApiTestHost?)null) { Interactive = true };

        var result = await rig.RunAsync("ui", "--json");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        var error = result.Json.GetProperty("error");
        error.GetProperty("exitCode").GetInt32().Should().Be((int)RoofExitCode.Usage);
        error.GetProperty("message").GetString().Should().Be("The terminal interface has no JSON output; leave out --json.");
    }

    [TestMethod]
    public async Task Ui_WithRedirectedInput_IsAUsageError()
    {
        using var rig = new CliRig((RoofApiTestHost?)null);

        var result = await rig.RunAsync("ui");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Out.Should().BeEmpty();
        result.Error.Should().Be("The terminal interface needs a terminal, and the input is redirected." + Environment.NewLine);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one command line with <c>--credentials-file <paramref name="path"/></c>: <see cref="CliRig.RunAsync"/> always
    /// passes the rig's own file, and setup's creation of a missing directory needs another.
    /// </summary>
    private static async Task<CliResult> RunWithCredentialsFileAsync(CliRig rig, string path, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await RoofCli.RunAsync([.. args, "--credentials-file", path], rig.CreateHost(output, error));
        return new CliResult(code, output.ToString(), error.ToString());
    }

    private static async Task<RoofUserResponse[]> UsersAsync(RoofApiTestHost host)
    {
        using var admin = ClientTestSupport.CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin));
        return await admin.Identity.GetUsersAsync();
    }

    /// <summary>A settings file and a managed secrets file on disk, so a hand edit can be made, as in <see cref="SettingsApiTests"/>.</summary>
    private sealed class SettingsFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "hvo-roof-cli-settings-" + Guid.NewGuid().ToString("N"));

        public SettingsFiles() => Directory.CreateDirectory(Path.Combine(_directory, "config"));

        private string SettingsPath => Path.Combine(_directory, "config", "appsettings.Local.json");

        public RoofApiTestHost StartHost(SettingsApiTests.RoofDouble roof)
        {
            var host = new RoofApiTestHost(
                roof.Mock,
                configureServices: services => services.Configure<PasswordHasherOptions>(options => options.IterationCount = 1_000),
                settingsFilePath: SettingsPath,
                secretsFilePath: Path.Combine(_directory, "secrets", "managed-secrets.json"));
            roof.StartFrom(host);
            return host;
        }

        public void WriteHandEdit(string json) => File.WriteAllText(SettingsPath, json);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
