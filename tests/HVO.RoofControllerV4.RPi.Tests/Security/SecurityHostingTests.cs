using System.Net;
using Asp.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// Production security posture: HTTPS requirement, OpenAPI protection, fail-closed behaviour without keys, anonymous access
/// to every mapped endpoint, and host filtering.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class SecurityHostingTests
{
    private const string StatusPath = "/api/v4.0/RoofControl/Status";
    private static readonly IPAddress LanClient = IPAddress.Parse("192.168.1.50");

    /// <summary>
    /// The only routes an anonymous caller may use. Every other endpoint the app maps must refuse one, so a new endpoint
    /// that forgets its [Authorize] fails <see cref="EveryEndpoint_Anonymous_IsRefusedUnlessAllowListed"/> until it is
    /// secured or deliberately added here.
    /// </summary>
    private static readonly string[] AnonymousRoutes =
    [
        "GET /health/live",
        "GET /health/ready",
        // Signing in with a name and password is how a person without a key gets a session token.
        "POST /api/v4.0/Auth/Session"
    ];

    /// <summary>Development only: the OpenAPI document and the Scalar reference are open for local work.</summary>
    private static readonly string[] DevelopmentAnonymousRoutes =
    [
        "GET /openapi/v4.json",
        "GET /scalar/scalar.js",
        "GET /scalar/scalar.aspnetcore.js",
        "GET /scalar/favicon.svg",
        "GET /scalar/v4"
    ];

    /// <summary>AllowAnonymousStop opens Stop, and only Stop, to anyone on the network.</summary>
    private static readonly string[] AnonymousStopRoutes =
    [
        "POST /api/v4.0/RoofControl/Stop"
    ];

    [TestMethod]
    public async Task OpenApi_InProduction_RequiresAdminKey()
    {
        using var host = new RoofApiTestHost(environment: "Production");
        using var anonymous = host.CreateApiClient(https: true);
        using var viewer = host.CreateApiClient(TestApiKeys.Viewer, https: true);
        using var admin = host.CreateApiClient(TestApiKeys.Admin, https: true);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/openapi/v4.json")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await viewer.GetAsync("/openapi/v4.json")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await admin.GetAsync("/openapi/v4.json")).StatusCode);
    }

    [TestMethod]
    public async Task OpenApi_InDevelopment_IsOpen()
    {
        using var host = new RoofApiTestHost();
        using var client = host.CreateApiClient();

        var response = await client.GetAsync("/openapi/v4.json");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var document = await ApiJson.ReadElementAsync(response);
        document.GetProperty("openapi").GetString().Should().StartWith("3.");
        document.GetProperty("paths").EnumerateObject().Should().Contain(p => p.Name.Contains("RoofControl/Status", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RequireHttps_ProductionDefault_RefusesPlainHttpFromTheLan()
    {
        using var host = new RoofApiTestHost(environment: "Production", remoteIp: LanClient);
        using var plain = host.CreateApiClient(TestApiKeys.Viewer);
        using var secure = host.CreateApiClient(TestApiKeys.Viewer, https: true);

        var refused = await plain.GetAsync(StatusPath);
        var live = await plain.GetAsync("/health/live");
        var accepted = await secure.GetAsync(StatusPath);

        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = await ApiJson.ReadElementAsync(refused);
        Assert.AreEqual("https_required", problem.GetProperty("code").GetString());
        Assert.AreEqual(HttpStatusCode.OK, live.StatusCode, "liveness stays reachable over plain HTTP");
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        Assert.IsNull(refused.Headers.Location, "never redirect a request that carries a key");
    }

    [TestMethod]
    public async Task RequireHttps_ProductionDefault_AllowsLoopbackPlainHttp()
    {
        // Docker healthcheck and the deploy script's docker-exec calls use http://localhost inside the container.
        using var host = new RoofApiTestHost(environment: "Production", remoteIp: IPAddress.Loopback);
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync(StatusPath);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task RequireHttps_ExplicitlyDisabled_AllowsPlainHttpFromTheLan()
    {
        using var host = new RoofApiTestHost(
            settings: new Dictionary<string, string?> { ["RoofControllerSecurity:RequireHttps"] = "false" },
            environment: "Production",
            remoteIp: LanClient);
        using var client = host.CreateApiClient(TestApiKeys.Viewer);

        var response = await client.GetAsync(StatusPath);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task NoKeysConfigured_ProtectedEndpointsFailClosed()
    {
        using var host = new RoofApiTestHost(includeDefaultKeys: false);
        using var client = host.CreateApiClient(TestApiKeys.Admin);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync(StatusPath)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v4.0/RoofControl/Stop", content: null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/v4.0/RoofControl/Open", content: null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/health")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        host.RoofService.Verify(s => s.Open(), Moq.Times.Never);
        host.RoofService.Verify(s => s.Stop(Moq.It.IsAny<Common.Models.RoofControllerStopReason>()), Moq.Times.Never);
    }

    [TestMethod]
    public async Task ShortConfiguredKey_IsIgnored()
    {
        using var host = new RoofApiTestHost(settings: new Dictionary<string, string?>
        {
            ["RoofControllerSecurity:ApiKeys:4:Name"] = "too-short",
            ["RoofControllerSecurity:ApiKeys:4:Role"] = "RoofAdmin",
            ["RoofControllerSecurity:ApiKeys:4:Key"] = "short-test-key"
        });
        using var client = host.CreateApiClient("short-test-key");

        var response = await client.GetAsync(StatusPath);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DataRow("Production", false)]
    [DataRow("Production", true)]
    [DataRow("Development", false)]
    public async Task EveryEndpoint_Anonymous_IsRefusedUnlessAllowListed(string environment, bool allowAnonymousStop)
    {
        using var host = new RoofApiTestHost(
            settings: new Dictionary<string, string?> { ["RoofControllerSecurity:AllowAnonymousStop"] = allowAnonymousStop.ToString() },
            environment: environment);
        using var anonymous = host.CreateApiClient(https: true);
        var allowed = AnonymousRoutes
            .Concat(environment == "Development" ? DevelopmentAnonymousRoutes : [])
            .Concat(allowAnonymousStop ? AnonymousStopRoutes : [])
            .ToHashSet(StringComparer.Ordinal);
        var probes = EnumerateProbes(host);
        var failures = new List<string>();

        foreach (var probe in probes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(probe.Method), probe.Path);
            var response = await anonymous.SendAsync(request);

            var refused = response.StatusCode == HttpStatusCode.Unauthorized;
            if (allowed.Contains(probe.Key) == refused)
            {
                failures.Add($"{probe.Key} [{probe.Route}] answered {(int)response.StatusCode} {response.Headers.Location} but is "
                    + (refused ? "allow-listed" : "not allow-listed"));
            }
        }

        string.Join(Environment.NewLine, failures).Should().BeEmpty(
            "an anonymous caller gets 401 everywhere but the allow-list");
        var probed = probes.Select(p => p.Key).ToList();
        probed.Should().Contain(allowed, "the allow-list must not name a route the app no longer maps");
        probed.Should().Contain(
        [
            "GET /api/v4.0/RoofControl/Configuration",
            "POST /api/v4.0/RoofControl/Configuration",
            "GET /api/v1.0/System/info",
            "GET /api/v1.0/System/metrics",
            "POST /api/v4.0/Auth/Pin",
            "GET /api/v4.0/Identity/Users",
            "DELETE /api/v4.0/Identity/Sessions/sample-id"
        ]);
        host.RoofService.Verify(s => s.Open(), Times.Never);
        host.RoofService.Verify(s => s.Close(), Times.Never);
        host.RoofService.Verify(s => s.RenewLease(), Times.Never);
        host.RoofService.Verify(s => s.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        host.RoofService.Verify(s => s.UpdateConfiguration(It.IsAny<RoofControllerOptionsV4>(), It.IsAny<long>()), Times.Never);
        host.RoofService.Verify(
            s => s.Stop(It.IsAny<RoofControllerStopReason>()),
            allowAnonymousStop ? Times.Once() : Times.Never());
    }

    [TestMethod]
    public async Task AllowedHosts_Restricted_RefusesAnotherHostWith400()
    {
        using var host = new RoofApiTestHost(
            settings: new Dictionary<string, string?> { ["AllowedHosts"] = "localhost;roof-pi.local" },
            environment: "Production");
        using var client = host.CreateApiClient(TestApiKeys.Viewer, https: true);

        var foreign = await client.GetAsync($"https://evil.example{StatusPath}");
        var local = await client.GetAsync(StatusPath);

        Assert.AreEqual(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, local.StatusCode);
        host.RoofService.Verify(s => s.GetCurrentStatusSnapshot(), Times.Once);
    }

    private sealed record EndpointProbe(string Method, string Path, string Route)
    {
        public string Key => $"{Method} {Path}";
    }

    /// <summary>
    /// One probe per mapped endpoint and HTTP method, with sample route values. An endpoint that accepts any method is
    /// probed with GET: its authorization does not depend on the verb.
    /// </summary>
    private static List<EndpointProbe> EnumerateProbes(RoofApiTestHost host)
    {
        var probes = new List<EndpointProbe>();
        foreach (var endpoint in host.Services.GetRequiredService<EndpointDataSource>().Endpoints)
        {
            if (endpoint is not RouteEndpoint route)
            {
                throw new AssertFailedException($"Endpoint '{endpoint.DisplayName}' is not routed; teach this test to reach it.");
            }

            var path = SamplePath(route);
            IReadOnlyList<string> methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } declared
                ? declared
                : ["GET"];
            probes.AddRange(methods.Select(method => new EndpointProbe(method, path, route.RoutePattern.RawText ?? path)));
        }

        return probes;
    }

    private static string SamplePath(RouteEndpoint route)
        => "/" + string.Join("/", route.RoutePattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter => SampleValue(route, parameter.Name),
            _ => throw new AssertFailedException($"Unexpected route part in '{route.RoutePattern.RawText}'.")
        }))));

    private static string SampleValue(RouteEndpoint route, string parameter) => parameter switch
    {
        "version" => route.Metadata.GetMetadata<ApiVersionMetadata>()?.Map(ApiVersionMapping.Explicit | ApiVersionMapping.Implicit)
            .DeclaredApiVersions.Single().ToString()
            ?? throw new AssertFailedException($"'{route.RoutePattern.RawText}' has no API version."),
        "cameraId" => "1",
        "documentName" => "v4",
        "name" => "sample-name",
        "id" => "sample-id",
        "group" => "roof",
        _ => throw new AssertFailedException($"No sample value for '{{{parameter}}}' in '{route.RoutePattern.RawText}'; add one.")
    };
}
