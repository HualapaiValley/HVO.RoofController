using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// What every <c>hvo-roof</c> command shares (#45): usage errors, where the controller address and the credential come
/// from (<c>--controller</c>, the environment, the credentials file) and when that file is refused, errors as JSON,
/// Ctrl+C, and the exit codes scripts rely on.
/// </summary>
[TestClass]
public sealed class RoofCliGlobalTests
{
    private static readonly Uri Elsewhere = new("http://roof.invalid:5001/");

    private static void Save(CliRig rig, Uri controller, string apiKey)
        => RoofCredentialStore.Save(rig.CredentialsPath, new RoofStoredCredentials { Controller = controller, ApiKey = apiKey });

    /// <summary>Runs a command line as is: without the rig's <c>--credentials-file</c>.</summary>
    private static async Task<CliResult> RunAsIsAsync(CliRig rig, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await RoofCli.RunAsync(args, rig.CreateHost(output, error));
        return new CliResult(code, output.ToString(), error.ToString());
    }

    // ---- Usage -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task UnknownCommand_IsAUsageError_WithTheRootHint()
    {
        using var rig = new CliRig();

        var result = await rig.RunAsync("fly");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("'fly'").And.EndWith("Run 'hvo-roof --help' for usage." + Environment.NewLine);
        result.Out.Should().BeEmpty();
    }

    [TestMethod]
    public async Task UnknownOption_IsAUsageError_WithTheCommandsHint()
    {
        using var rig = new CliRig();

        var result = await rig.RunAsync("status", "--verbose");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("'--verbose'").And.EndWith("Run 'hvo-roof status --help' for usage." + Environment.NewLine);
        result.Out.Should().BeEmpty();
    }

    [TestMethod]
    public void RoleOption_CompletesToTheThreeRoles()
    {
        using var rig = new CliRig();
        var root = RoofCli.CreateRootCommand(rig.CreateHost(TextWriter.Null, TextWriter.Null));

        var completions = root.Parse("users add ada --role ").GetCompletions().Select(completion => completion.Label);

        completions.Should().Equal("admin", "operator", "viewer");
    }

    [TestMethod]
    public async Task CredentialsFile_ThatIsNotAPath_IsAUsageError()
    {
        using var rig = new CliRig();

        var result = await RunAsIsAsync(rig, "status", "--credentials-file", "bad\0path");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().StartWith("The credentials file path is not valid: ");
    }

    // ---- The controller address ------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Controller_OverridesTheFileAndTheEnvironment()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        Save(rig, Elsewhere, TestApiKeys.Operator);
        rig.Environment["HVO_ROOF_URL"] = Elsewhere.ToString();

        var result = await rig.RunAsync("whoami", "--controller", "http://localhost");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("Controller: http://localhost/").And.Contain("Name:       test-operator");
    }

    [TestMethod]
    [DataRow("ftp://roof.local/", "'ftp://roof.local/' is not a controller address. Give an http or https address, for example https://roof.local:5001/.")]
    [DataRow("roof.local", "'roof.local' is not a controller address. Give an http or https address, for example https://roof.local:5001/.")]
    [DataRow("https://user@roof.local/", "The controller address may not include a user name, a query or a fragment.")]
    public async Task Controller_ThatIsNotAnHttpAddress_IsAUsageError(string controller, string message)
    {
        using var rig = new CliRig();
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("status", "--controller", controller);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain(message);
        result.Out.Should().BeEmpty();
    }

    [TestMethod]
    public async Task EnvironmentUrl_ThatIsNotAnHttpAddress_IsNotConfigured()
    {
        using var rig = new CliRig();
        rig.UseApiKey(TestApiKeys.Operator);
        rig.Environment["HVO_ROOF_URL"] = "ftp://roof.local/";

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().Be("HVO_ROOF_URL is not an absolute http or https URL." + Environment.NewLine);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "text")]
    [DataRow(true, DisplayName = "--json")]
    public async Task EnvironmentCertificatePin_ThatIsNotAPin_IsNotConfigured(bool json)
    {
        using var rig = new CliRig();
        rig.UseApiKey(TestApiKeys.Operator);
        rig.Environment[RoofCredentialStore.CertificateVariable] = "not-a-pin";

        var result = json ? await rig.RunAsync("status", "--json") : await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        const string message = "HVO_ROOF_CERT_SHA256 is not a SHA-256 pin: 64 hex digits.";
        if (json)
        {
            result.Json.GetProperty("error").GetProperty("message").GetString().Should().Be(message);
        }
        else
        {
            result.Error.Should().Be(message + Environment.NewLine);
        }
    }

    [TestMethod]
    public async Task CredentialsFile_WithACertificatePinThatIsNotAPin_IsNotConfigured()
    {
        using var rig = new CliRig();
        RoofCredentialStore.Save(
            rig.CredentialsPath,
            new RoofStoredCredentials { Controller = Elsewhere, ApiKey = TestApiKeys.Operator, CertificateSha256 = "AB:CD" });

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().Be(
            $"The certificate pin in {rig.CredentialsPath} is not a SHA-256 pin: 64 hex digits. Save it again with 'hvo-roof setup'."
            + Environment.NewLine);
    }

    [TestMethod]
    public void EnvironmentCertificatePin_OnItsOwn_OverridesTheFile()
    {
        var saved = new string('A', 64);
        var pinned = string.Join(':', Enumerable.Repeat("0B", 32));
        using var rig = new CliRig();
        RoofCredentialStore.Save(
            rig.CredentialsPath,
            new RoofStoredCredentials { Controller = Elsewhere, ApiKey = TestApiKeys.Operator, CertificateSha256 = saved });
        rig.Environment[RoofCredentialStore.CertificateVariable] = pinned;

        var connection = rig.CreateContext().ResolveConnection();

        connection.CertificateSha256.Should().Be(pinned, "each variable overrides the file, the pin among them");
        connection.Controller.Should().Be(Elsewhere, "the other values still come from the file");
        connection.Source.Should().Be(rig.CredentialsPath);
    }

    [TestMethod]
    public async Task Environment_UrlAndKey_WinOverTheFile()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        Save(rig, Elsewhere, TestApiKeys.Viewer);
        rig.Environment["HVO_ROOF_URL"] = "http://localhost/";
        rig.Environment["HVO_ROOF_API_KEY"] = TestApiKeys.Operator;

        var result = await rig.RunAsync("whoami");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("Controller: http://localhost/")
            .And.Contain("Credential: environment")
            .And.Contain("Name:       test-operator");
    }

    // ---- The credentials file --------------------------------------------------------------------------------------

    [TestMethod]
    public async Task NoCredentialsFile_IsNotConfigured_AndSaysHowToSetUp()
    {
        using var rig = new CliRig();

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().Be("No controller address. Run 'hvo-roof setup', set HVO_ROOF_URL, or pass --controller." + Environment.NewLine);
        result.Out.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CredentialsFile_ReadableByOthers_IsRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Unix file modes only.");
            return;
        }

        using var rig = new CliRig();
        rig.UseApiKey(TestApiKeys.Operator);
        File.SetUnixFileMode(
            rig.CredentialsPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().Be(
            $"{rig.CredentialsPath} can be read or changed by other users. Run 'chmod 600 {rig.CredentialsPath}', then try again."
            + Environment.NewLine);
    }

    [TestMethod]
    public async Task CredentialsFile_ThatIsNotJson_IsRefused()
    {
        using var rig = new CliRig();
        File.WriteAllText(rig.CredentialsPath, "{ not json");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(rig.CredentialsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        result.Error.Should().StartWith($"{rig.CredentialsPath} is not a valid credentials file.");
    }

    // ---- Errors as JSON --------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Json_WritesTheErrorToStandardOutput()
    {
        using var rig = new CliRig();

        var result = await rig.RunAsync("status", "--json");

        result.Code.Should().Be(RoofExitCode.NotConfigured, result.ToString());
        var error = result.Json.GetProperty("error");
        error.GetProperty("exitCode").GetInt32().Should().Be(3);
        error.GetProperty("kind").GetString().Should().Be("NotConfigured");
        error.GetProperty("message").GetString().Should().Be("No controller address. Run 'hvo-roof setup', set HVO_ROOF_URL, or pass --controller.");
        error.GetProperty("status").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        result.Error.Should().BeEmpty();
    }

    // ---- Ctrl+C ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task CtrlC_DuringARequest_ExitsInterrupted()
    {
        using var rig = new CliRig();
        rig.UseApiKey(TestApiKeys.Viewer);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var interrupt = new CancellationTokenSource();
        var host = new RoofCliHost
        {
            Out = output,
            Error = error,
            GetEnvironmentVariable = _ => null,
            ReadLine = (_, _) => null,
            CreateHandler = () => new FailingHandler(async (_, cancellationToken) =>
            {
                arrived.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }),
            StatusFeed = ClientTestSupport.FastFeed
        };

        var run = RoofCli.RunAsync(["status", "--credentials-file", rig.CredentialsPath], host, interrupt.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await interrupt.CancelAsync();
        var result = new CliResult(await run.WaitAsync(TimeSpan.FromSeconds(10)), output.ToString(), error.ToString());

        result.ExitCode.Should().Be(130, result.ToString());
        result.Code.Should().Be(RoofExitCode.Interrupted, result.ToString());
        result.Error.Should().Be("Interrupted." + Environment.NewLine);
        result.Out.Should().BeEmpty();
    }

    [TestMethod]
    public async Task CtrlC_DuringARequest_Json_WritesTheInterruptedError()
    {
        using var rig = new CliRig();
        rig.UseApiKey(TestApiKeys.Viewer);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var interrupt = new CancellationTokenSource();
        var host = new RoofCliHost
        {
            Out = output,
            Error = error,
            GetEnvironmentVariable = _ => null,
            ReadLine = (_, _) => null,
            CreateHandler = () => new FailingHandler(async (_, cancellationToken) =>
            {
                arrived.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }),
            StatusFeed = ClientTestSupport.FastFeed
        };

        var run = RoofCli.RunAsync(["status", "--json", "--credentials-file", rig.CredentialsPath], host, interrupt.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await interrupt.CancelAsync();
        var result = new CliResult(await run.WaitAsync(TimeSpan.FromSeconds(10)), output.ToString(), error.ToString());

        result.ExitCode.Should().Be(130, result.ToString());
        var json = result.Json.GetProperty("error");
        json.GetProperty("exitCode").GetInt32().Should().Be(130);
        json.GetProperty("kind").GetString().Should().Be("Interrupted");
        json.GetProperty("message").GetString().Should().Be("Interrupted.");
        result.Error.Should().BeEmpty();
    }

    // ---- Exit codes ------------------------------------------------------------------------------------------------

    [TestMethod]
    public void ExitCodes_AreTheDocumentedNumbers()
    {
        var expected = new Dictionary<RoofExitCode, int>
        {
            [RoofExitCode.Success] = 0,
            [RoofExitCode.Failed] = 1,
            [RoofExitCode.Usage] = 2,
            [RoofExitCode.NotConfigured] = 3,
            [RoofExitCode.Unreachable] = 4,
            [RoofExitCode.SignedOut] = 5,
            [RoofExitCode.Forbidden] = 6,
            [RoofExitCode.Refused] = 7,
            [RoofExitCode.Unhealthy] = 8,
            [RoofExitCode.StopNotVerified] = 9,
            [RoofExitCode.ConfirmationRequired] = 10,
            [RoofExitCode.Stale] = 11,
            [RoofExitCode.Interrupted] = 130
        };

        Enum.GetValues<RoofExitCode>().ToDictionary(code => code, code => (int)code)
            .Should().BeEquivalentTo(expected, "scripts depend on these numbers; a new code is added here deliberately");
    }
}
