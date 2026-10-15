using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>Real CLI and shared Client routing; the native helper and HTTP responses are explicit test doubles.</summary>
[TestClass]
public sealed class RoofCliProviderTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalDenial_NeverUsesSavedOrEnvironmentCredentials_AndNeverPrompts(bool installedDefault)
    {
        using var files = new CliRig();
        RoofCredentialStore.Save(files.CredentialsPath, new RoofStoredCredentials
        {
            Controller = new Uri("http://localhost/"), AuthMode = installedDefault ? "local" : null,
            ApiKey = "test-saved-key", Session = new("test-saved-session", "ada", "RoofAdmin", "saved", DateTimeOffset.UtcNow.AddHours(1))
        });
        var before = File.ReadAllBytes(files.CredentialsPath);
        var requests = 0;
        var attempts = 0;
        var prompts = 0;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new RoofCliHost
        {
            Out = output, Error = error, IsInteractive = false,
            GetEnvironmentVariable = name => name switch
            {
                RoofCredentialStore.SessionVariable => "test-environment-session",
                RoofCredentialStore.ApiKeyVariable => "test-environment-key",
                _ => null
            },
            ReadLine = (_, _) => { prompts++; return "unused"; },
            ObtainLocalAssertion = (_, _) => { attempts++; throw new RoofCliRefusedException("Native gate refused (test).", RoofExitCode.Forbidden); },
            CreateHandler = () => new Handler((_, _) => { requests++; throw new AssertFailedException("No network request may follow a refused native gate."); })
        };
        var arguments = new List<string> { "whoami", "--credentials-file", files.CredentialsPath };
        if (!installedDefault) arguments.Add("--local");

        var code = await RoofCli.RunAsync(arguments.ToArray(), host);

        code.Should().Be((int)RoofExitCode.Forbidden);
        attempts.Should().Be(1);
        requests.Should().Be(0);
        prompts.Should().Be(0);
        File.ReadAllBytes(files.CredentialsPath).Should().Equal(before);
        (output.ToString() + error).Should().NotContain("test-saved-session").And.NotContain("test-environment-session");
    }

    [TestMethod]
    public async Task InstalledLocalMode_RequiresExplicitRemoteChoice()
    {
        using var files = new CliRig();
        RoofCredentialStore.Save(files.CredentialsPath, new RoofStoredCredentials
        {
            Controller = new Uri("http://localhost/"), AuthMode = "local",
            Session = new("test-remote-session", "ada", "RoofAdmin", "saved", DateTimeOffset.UtcNow.AddHours(1))
        });
        var requests = 0;
        var gates = 0;
        var host = new RoofCliHost
        {
            Out = TextWriter.Null, Error = TextWriter.Null, IsInteractive = false,
            GetEnvironmentVariable = _ => null, ReadLine = (_, _) => throw new AssertFailedException("No prompt."),
            ObtainLocalAssertion = (_, _) => { gates++; throw new AssertFailedException("Remote mode must not use the native helper."); },
            CreateHandler = () => new Handler((request, _) =>
            {
                requests++;
                request.Headers.Authorization!.Parameter.Should().Be("test-remote-session");
                return Task.FromResult(Caller());
            })
        };

        (await RoofCli.RunAsync(["whoami", "--remote", "--credentials-file", files.CredentialsPath], host)).Should().Be(0);
        requests.Should().Be(1);
        gates.Should().Be(0);
    }

    [TestMethod]
    [DataRow("login", "ada", "--provider", "virtual")]
    [DataRow("login", "ada", "--provider", "linux")]
    [DataRow("my-keys", "create", "job", "--scopes", "read")]
    public async Task LocalMode_CannotTurnIntoASavedPasswordOrKey(params string[] command)
    {
        using var files = new CliRig();
        var prompts = 0;
        var requests = 0;
        var host = new RoofCliHost
        {
            Out = TextWriter.Null, Error = TextWriter.Null, IsInteractive = false, GetEnvironmentVariable = _ => null,
            ReadLine = (_, _) => { prompts++; return "unused"; },
            ObtainLocalAssertion = (_, _) => throw new AssertFailedException("No native request."),
            CreateHandler = () => new Handler((_, _) => { requests++; throw new AssertFailedException("No network request."); })
        };
        (await RoofCli.RunAsync([.. command, "--local", "--controller", "http://localhost/", "--credentials-file", files.CredentialsPath], host))
            .Should().Be((int)RoofExitCode.Usage);
        prompts.Should().Be(0);
        requests.Should().Be(0);
        File.Exists(files.CredentialsPath).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("virtual", true)]
    [DataRow("linux", true)]
    [DataRow("virtual", false)]
    [DataRow("linux", false)]
    public async Task RemoteLogin_SendsExplicitProvider_WithoutExistingCredentials(string provider, bool explicitName)
    {
        using var files = new CliRig();
        files.UseApiKey("test-existing-key");
        var sent = 0;
        var host = new RoofCliHost
        {
            Out = TextWriter.Null, Error = TextWriter.Null, IsInteractive = false, GetEnvironmentVariable = _ => null,
            GetUserName = () => "os-user",
            ReadLine = (_, _) => "test-inert-password",
            CreateHandler = () => new Handler(async (request, cancellationToken) =>
            {
                sent++;
                request.RequestUri!.AbsolutePath.Should().Be("/api/v4.0/Auth/ProviderSession");
                request.Headers.Authorization.Should().BeNull();
                request.Headers.Contains("X-Api-Key").Should().BeFalse();
                var body = await request.Content!.ReadFromJsonAsync<RoofProviderSignInRequest>(cancellationToken);
                body!.Provider.Should().Be(provider);
                body.Name.Should().Be(explicitName ? "ada" : "os-user");
                return Session();
            })
        };
        (await RoofCli.RunAsync(["login", .. explicitName ? new[] { "ada" } : Array.Empty<string>(), "--provider", provider, "--credentials-file", files.CredentialsPath], host)).Should().Be(0);
        sent.Should().Be(1);
        files.Stored!.Session!.Token.Should().Be("test-local-session");
    }

    [TestMethod]
    public async Task LocalRequest_ExchangesAssertionAnonymously_AndNeverPersistsReturnedSession()
    {
        using var files = new CliRig();
        files.UseApiKey("test-existing-key");
        var before = File.ReadAllBytes(files.CredentialsPath);
        var paths = new List<string>();
        var host = new RoofCliHost
        {
            Out = TextWriter.Null, Error = TextWriter.Null, IsInteractive = false, GetEnvironmentVariable = _ => null,
            ReadLine = (_, _) => throw new AssertFailedException("No prompt."),
            ObtainLocalAssertion = (operation, _) => { operation.Should().Be("read"); return Task.FromResult("test-signed-assertion"); },
            CreateHandler = () => new Handler(async (request, cancellationToken) =>
            {
                paths.Add(request.RequestUri!.AbsolutePath);
                request.Headers.Contains("X-Api-Key").Should().BeFalse();
                if (request.RequestUri.AbsolutePath.EndsWith("/LocalSession", StringComparison.Ordinal))
                {
                    request.Headers.Authorization.Should().BeNull();
                    var body = await request.Content!.ReadFromJsonAsync<RoofLocalSignInRequest>(cancellationToken);
                    body!.Assertion.Should().Be("test-signed-assertion");
                    return Session();
                }

                request.Headers.Authorization!.Parameter.Should().Be("test-local-session");
                return Caller();
            })
        };
        (await RoofCli.RunAsync(["whoami", "--local", "--credentials-file", files.CredentialsPath], host)).Should().Be(0);
        paths.Should().Equal("/api/v4.0/Auth/LocalSession", "/api/v4.0/Auth/Me");
        File.ReadAllBytes(files.CredentialsPath).Should().Equal(before);
    }

    [TestMethod]
    public async Task LocalPrincipalChange_UsesIdentityScopeAndImmutableId_WithoutSavingCredentials()
    {
        using var files = new CliRig();
        files.UseApiKey("test-existing-key");
        var before = File.ReadAllBytes(files.CredentialsPath);
        var changed = false;
        var host = new RoofCliHost
        {
            Out = TextWriter.Null, Error = TextWriter.Null, IsInteractive = false, GetEnvironmentVariable = _ => null,
            ReadLine = (_, _) => throw new AssertFailedException("No password prompt."),
            ObtainLocalAssertion = (operation, _) => { operation.Should().Be("identity"); return Task.FromResult("test-assertion"); },
            CreateHandler = () => new Handler(async (request, cancellationToken) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/LocalSession", StringComparison.Ordinal)) return Session();
                request.RequestUri.AbsolutePath.Should().Be("/api/v4.0/Identity/Principals/fixture-id");
                request.Method.Should().Be(HttpMethod.Put);
                request.Headers.Authorization!.Parameter.Should().Be("test-local-session");
                var body = await request.Content!.ReadFromJsonAsync<RoofPrincipalUpdateRequest>(cancellationToken);
                body!.Role.Should().Be("RoofOperator");
                body.Enabled.Should().BeFalse();
                changed = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new RoofPrincipalResponse("fixture-id", "linux", "fixture-subject", "fixture-linux", body.Role, body.Enabled)) };
            })
        };
        (await RoofCli.RunAsync(["principals", "set", "fixture-id", "--role", "operator", "--enabled", "false", "--local", "--credentials-file", files.CredentialsPath], host)).Should().Be(0);
        changed.Should().BeTrue();
        File.ReadAllBytes(files.CredentialsPath).Should().Equal(before);
    }

    internal static HttpResponseMessage Session() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new RoofSessionResponse("test-local-session", "fixture", "ada", "RoofAdmin",
            RoofCredentialKind.Session, DateTimeOffset.UtcNow.AddSeconds(30), null), options: RoofClientJson.Options)
    };

    internal static HttpResponseMessage Caller() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new RoofCallerResponse("ada", "RoofAdmin", RoofCredentialKind.Session, null, "fixture",
            DateTimeOffset.UtcNow.AddSeconds(30), false), options: RoofClientJson.Options)
    };

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => answer(request, cancellationToken);
    }
}
