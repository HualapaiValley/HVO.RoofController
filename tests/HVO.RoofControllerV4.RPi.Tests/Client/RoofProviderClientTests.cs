using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Cli;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

[TestClass]
public sealed class RoofProviderClientTests
{
    [TestMethod]
    public async Task EveryProtectedRequest_ReauthorizesForItsOperation_AndIgnoresStoredCredential()
    {
        var operations = new List<string>();
        var headers = new List<string>();
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost/"), Credential = new RoofApiKeyCredential("test-ignored-key"),
            LocalAuthorization = (operation, _) =>
            {
                operations.Add(operation);
                return Task.FromResult<RoofCredential>(new RoofSessionCredential("test-request-" + operations.Count));
            },
            CreateHandler = () => new RoofCliProviderTests.Handler((request, _) =>
            {
                request.Headers.Contains("X-Api-Key").Should().BeFalse();
                headers.Add(request.Headers.Authorization!.Parameter!);
                var content = request.RequestUri!.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal) ? "[]" : "{}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json") });
            })
        });

        await client.Auth.GetCallerAsync();
        await client.Identity.GetUsersAsync();
        await client.Auth.GetCallerAsync();

        operations.Should().Equal("read", "identity", "read");
        headers.Should().Equal("test-request-1", "test-request-2", "test-request-3");
        client.Credential.Should().BeOfType<RoofApiKeyCredential>("local sessions are never stored as the client's default credential");
    }

    [TestMethod]
    public async Task LocalAuthorizationFailure_NeverSendsARequest_OrFallsBack()
    {
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost/"), Credential = new RoofSessionCredential("test-ignored-session"),
            LocalAuthorization = (_, _) => throw new InvalidOperationException("Refused native gate."),
            CreateHandler = () => new RoofCliProviderTests.Handler((_, _) => throw new AssertFailedException("No network fallback."))
        });
        Func<Task> call = () => client.Auth.GetCallerAsync();
        await call.Should().ThrowAsync<RoofLocalAuthorizationException>();
    }

    [TestMethod]
    public async Task Stop_WithDeniedLocalAuthority_IsReportedFailedWithoutRemoteFallback()
    {
        var requests = 0;
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost/"), Credential = new RoofApiKeyCredential("test-ignored-key"),
            LocalAuthorization = (_, _) => throw new InvalidOperationException("Denied."),
            CreateHandler = () => new RoofCliProviderTests.Handler((_, _) =>
            {
                requests++;
                throw new AssertFailedException("A denied local gate cannot send Stop under another identity.");
            })
        });

        var result = await client.StopAsync();
        result.Outcome.Should().Be(RoofStopOutcome.Failed);
        requests.Should().Be(0);
    }

    [TestMethod]
    public async Task LocalMode_CannotMintPersonalKeys()
    {
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost/"), Credential = new RoofSessionCredential("test-ignored-session"),
            LocalAuthorization = (_, _) => throw new AssertFailedException("Personal credentials have no local operation."),
            CreateHandler = () => new RoofCliProviderTests.Handler((_, _) => throw new AssertFailedException("No network request."))
        });
        Func<Task> call = () => client.Auth.CreateOwnedKeyAsync(new RoofOwnedKeyCreateRequest
        {
            Name = "fixture", Scopes = ["read"], ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
        });
        await call.Should().ThrowAsync<InvalidOperationException>();
    }

    [TestMethod]
    [DataRow("read", "GET", "api/v4.0/RoofControl/Configuration")]
    [DataRow("read", "POST", "api/v4.0/RoofControl/Stop")]
    [DataRow("operate", "POST", "api/v4.0/RoofControl/Open")]
    [DataRow("configure", "POST", "api/v4.0/Settings/roof")]
    [DataRow("identity", "PUT", "api/v4.0/Identity/Principals/id")]
    public void LocalOperations_AreBounded(string expected, string method, string path)
        => RoofLocalAuthorization.Operation(new HttpMethod(method), path).Should().Be(expected);
}
