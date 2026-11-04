using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>Exercises the actual local CLI status command, with an inert native exchange and HTTP handler.</summary>
[TestClass]
public sealed class RoofCliLocalAlpacaStatusTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LocalAlpacaStatus_ReadsCommandAndServerState_WithFreshReadSessions(bool json)
    {
        using var files = new CliRig();
        files.UseApiKey("test-unused-saved-key");
        var before = File.ReadAllBytes(files.CredentialsPath);
        var scopes = new List<string>();
        var paths = new List<string>();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new RoofCliHost
        {
            Out = output, Error = error, IsInteractive = false,
            GetEnvironmentVariable = _ => null,
            ReadLine = (_, _) => throw new AssertFailedException("No credential prompt."),
            ObtainLocalAssertion = (scope, _) =>
            {
                scopes.Add(scope);
                return Task.FromResult("test-inert-assertion");
            },
            CreateHandler = () => new RoofCliProviderTests.Handler((request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                paths.Add(path);
                request.Headers.Contains("X-Api-Key").Should().BeFalse();
                if (path == "/api/v4.0/Auth/LocalSession")
                {
                    request.Method.Should().Be(HttpMethod.Post);
                    request.Headers.Authorization.Should().BeNull();
                    return Task.FromResult(RoofCliProviderTests.Session());
                }

                request.Method.Should().Be(HttpMethod.Get);
                request.Headers.Authorization!.Parameter.Should().Be("test-local-session");
                return Task.FromResult(path switch
                {
                    "/api/v4.0/RoofControl/Status" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(RoofServiceMock.Snapshot() with
                        {
                            Alpaca = new RoofAlpacaStatus { Mode = RoofAlpacaCommandMode.CloseOnly }
                        }, options: RoofClientJson.Options)
                    },
                    "/api/v4.0/System/Summary" => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new SystemSummaryResponse("test-version", 1, DateTimeOffset.UnixEpoch)
                        {
                            AlpacaServer = new AlpacaServerSummary { Enabled = false }
                        }, options: RoofClientJson.Options)
                    },
                    _ => throw new AssertFailedException("Unexpected route: " + path)
                });
            })
        };

        var arguments = new List<string> { "--local", "alpaca", "status", "--credentials-file", files.CredentialsPath };
        if (json) arguments.Add("--json");
        var result = await RoofCli.RunAsync(arguments.ToArray(), host);

        result.Should().Be((int)RoofExitCode.Success, error.ToString() + output);
        error.ToString().Should().BeEmpty();
        scopes.Should().Equal("read", "read");
        paths.Should().Equal("/api/v4.0/Auth/LocalSession", "/api/v4.0/RoofControl/Status",
            "/api/v4.0/Auth/LocalSession", "/api/v4.0/System/Summary");
        File.ReadAllBytes(files.CredentialsPath).Should().Equal(before);
        if (json)
        {
            using var document = JsonDocument.Parse(output.ToString());
            document.RootElement.GetProperty("mode").GetString().Should().Be("CloseOnly");
            document.RootElement.GetProperty("server").GetProperty("enabled").GetBoolean().Should().BeFalse();
        }
        else
        {
            output.ToString().Should().MatchRegex(@"Alpaca server: +Off").And.Contain(RoofAlpacaText.DescribeMode(RoofAlpacaCommandMode.CloseOnly));
        }
    }
}
