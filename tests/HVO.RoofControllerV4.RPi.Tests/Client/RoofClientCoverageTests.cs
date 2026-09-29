using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Asp.Versioning;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// Every endpoint the controller maps has a call in the client library (#44), so an endpoint cannot be added without
/// one. Each call goes to a handler that records it and answers 503, so nothing reaches the roof.
/// </summary>
[TestClass]
public sealed class RoofClientCoverageTests
{
    /// <summary>Endpoints the library does not call, each with the reason.</summary>
    private static readonly Dictionary<string, string> NotCalled = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GET /openapi/{documentName}.json"] = ApiDocs,
        ["GET /scalar/{documentName?}"] = ApiDocs,
        ["GET /scalar/favicon.svg"] = ApiDocs,
        ["GET /scalar/scalar.aspnetcore.js"] = ApiDocs,
        ["GET /scalar/scalar.js"] = ApiDocs
    };

    private const string ApiDocs = "the API description and its viewer, read by people and tools, not by clients";

    /// <summary>A SignalR negotiate answer offering WebSockets, so the status feed goes on to open one.</summary>
    private const string Negotiated = """
        {"negotiateVersion":1,"connectionId":"c1","connectionToken":"t1","availableTransports":[{"transport":"WebSockets","transferFormats":["Text","Binary"]}]}
        """;

    private static readonly (string Name, Func<RoofControllerClient, Task> Call)[] Calls =
    [
        ("Roof.GetStatus", client => client.Roof.GetStatusAsync()),
        ("Roof.Open", client => client.Roof.OpenAsync()),
        ("Roof.Close", client => client.Roof.CloseAsync()),
        ("Roof.RenewLease", client => client.Roof.RenewLeaseAsync()),
        ("Roof.ClearFault", client => client.Roof.ClearFaultAsync()),
        ("Roof.GetConfiguration", client => client.Roof.GetConfigurationAsync()),
        ("Roof.UpdateConfiguration", client => client.Roof.UpdateConfigurationAsync(new RoofConfigurationRequest())),
        ("Stop", client => client.StopAsync()),
        ("Auth.SignIn", client => client.Auth.SignInAsync("alice", "not-a-password")),
        ("Auth.SignInWithPin", client => client.Auth.SignInWithPinAsync("alice", "0000")),
        ("Auth.GetPinUsers", client => client.Auth.GetPinUsersAsync()),
        ("Auth.GetCaller", client => client.Auth.GetCallerAsync()),
        ("Auth.SignOut", client => client.Auth.SignOutAsync()),
        ("Auth.ChangePassword", client => client.Auth.ChangePasswordAsync("not-a-password", "not-a-password-either")),
        ("Identity.GetUsers", client => client.Identity.GetUsersAsync()),
        ("Identity.GetUser", client => client.Identity.GetUserAsync("alice")),
        ("Identity.AddUser", client => client.Identity.AddUserAsync(
            new RoofUserCreateRequest { Name = "alice", Role = RoofControllerApiContract.ViewerRole, Password = "not-a-password" })),
        ("Identity.UpdateUser", client => client.Identity.UpdateUserAsync(
            "alice", new RoofUserUpdateRequest { Role = RoofControllerApiContract.OperatorRole })),
        ("Identity.RemoveUser", client => client.Identity.RemoveUserAsync("alice")),
        ("Identity.GetApiKeys", client => client.Identity.GetApiKeysAsync()),
        ("Identity.AddApiKey", client => client.Identity.AddApiKeyAsync(
            new RoofApiKeyCreateRequest { Name = "ci", Role = RoofControllerApiContract.OperatorRole })),
        ("Identity.UpdateApiKey", client => client.Identity.UpdateApiKeyAsync(
            "ci", new RoofApiKeyUpdateRequest { Role = RoofControllerApiContract.ViewerRole })),
        ("Identity.RotateApiKey", client => client.Identity.RotateApiKeyAsync("ci")),
        ("Identity.RemoveApiKey", client => client.Identity.RemoveApiKeyAsync("ci")),
        ("Identity.GetSessions", client => client.Identity.GetSessionsAsync()),
        ("Identity.EndSession", client => client.Identity.EndSessionAsync("session-1")),
        ("Settings.GetCatalogue", client => client.Settings.GetCatalogueAsync()),
        ("Settings.Get", client => client.Settings.GetAsync()),
        ("Settings.Update", client => client.Settings.UpdateAsync(
            RoofSettingsContract.UiGroup, new RoofSettingsUpdateRequest { ExpectedVersion = 1, Values = new Dictionary<string, JsonElement>() })),
        ("Settings.ApplyHandEdit", client => client.Settings.ApplyHandEditAsync(new RoofSettingsHandEditRequest { Token = "edit-1" })),
        ("Settings.DiscardHandEdit", client => client.Settings.DiscardHandEditAsync(new RoofSettingsHandEditRequest { Token = "edit-1" })),
        ("System.Restart", client => client.System.RestartAsync()),
        ("System.GetInformation", client => client.System.GetInformationAsync()),
        ("System.GetMetrics", client => client.System.GetMetricsAsync()),
        ("Health.GetReport", client => client.Health.GetReportAsync()),
        ("Health.GetReadiness", client => client.Health.GetReadinessAsync()),
        ("Health.GetLiveness", client => client.Health.GetLivenessAsync()),
        ("Camera.Open", async client => { await using var stream = await client.Camera.OpenAsync(1); })
    ];

    [TestMethod]
    public async Task EveryControllerEndpoint_HasAClientCall()
    {
        using var host = RoofClientApiTests.CreateHost();
        var endpoints = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(endpoint => endpoint as RouteEndpoint
                ?? throw new AssertFailedException($"Endpoint '{endpoint.DisplayName}' is not routed; teach this test to match it."))
            .Select(route => new MappedEndpoint(route))
            .ToList();
        var sent = new ConcurrentQueue<(string Call, string Method, string Path)>();
        var current = "";
        using var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = ClientTestSupport.BaseAddress,
            // A kiosk credential, so the PIN sign-in is sent too; nothing here reaches a controller.
            Credential = new RoofKioskCredential(RoofClientApiTests.KioskKey),
            CreateHandler = () => new FailingHandler((request, _) =>
            {
                sent.Enqueue((current, request.Method.Method, request.RequestUri!.AbsolutePath));
                return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/negotiate", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(Negotiated) }
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { RequestMessage = request });
            }),
            WebSocketFactory = (uri, _, _) =>
            {
                sent.Enqueue(("StatusFeed", "GET", uri.AbsolutePath));
                throw new WebSocketException("Not connected (test).");
            },
            StatusFeed = ClientTestSupport.FastFeed
        });

        foreach (var (name, call) in Calls)
        {
            current = name;
            try
            {
                await call(client);
            }
            catch (Exception ex) when (ex is RoofApiException or RoofProtocolException or InvalidOperationException)
            {
                // Every call is refused, or its answer cannot be read; only the request it sent matters here.
            }

            sent.Should().Contain(request => request.Call == name, "{0} sends a request", name);
        }

        current = "StatusFeed";
        await using (var feed = client.CreateStatusFeed())
        {
            feed.Start();
            await ClientTestSupport.WaitUntilAsync(
                () => sent.Any(request => request.Call == "StatusFeed" && !request.Path.EndsWith("/negotiate", StringComparison.Ordinal)),
                "the status feed to open its WebSocket");
        }

        foreach (var request in sent)
        {
            endpoints.Should().Contain(endpoint => endpoint.Matches(request.Method, request.Path),
                "{0} sent {1} {2}, which the controller should map", request.Call, request.Method, request.Path);
        }

        var uncalled = endpoints
            .Where(endpoint => !sent.Any(request => endpoint.Matches(request.Method, request.Path)))
            .Select(endpoint => endpoint.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        uncalled.Should().BeEquivalentTo(NotCalled.Keys, "every other endpoint has a call in the client library");
    }

    /// <summary>A mapped endpoint as a method and a path pattern, with the API version filled in.</summary>
    private sealed class MappedEndpoint
    {
        private readonly Regex _path;
        private readonly IReadOnlyList<string>? _methods;

        public MappedEndpoint(RouteEndpoint route)
        {
            _methods = route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? methods : null;
            var raw = "/" + route.RoutePattern.RawText?.TrimStart('/');
            Key = $"{(_methods is null ? "*" : string.Join(",", _methods))} {raw}";
            var segments = route.RoutePattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart literal => Regex.Escape(literal.Content),
                RoutePatternSeparatorPart separator => Regex.Escape(separator.Content),
                RoutePatternParameterPart { Name: "version" } => Regex.Escape(ApiVersion(route)),
                RoutePatternParameterPart { IsCatchAll: true } => ".*",
                RoutePatternParameterPart => "[^/]+",
                _ => throw new AssertFailedException($"Unexpected route part in '{raw}'.")
            })));
            _path = new Regex($"^/{string.Join("/", segments)}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public string Key { get; }

        public bool Matches(string method, string path)
            => (_methods is null || _methods.Contains(method, StringComparer.OrdinalIgnoreCase)) && _path.IsMatch(path);

        private static string ApiVersion(RouteEndpoint route)
            => route.Metadata.GetMetadata<ApiVersionMetadata>()?.Map(ApiVersionMapping.Explicit | ApiVersionMapping.Implicit)
                .DeclaredApiVersions.Single().ToString()
                ?? throw new AssertFailedException($"'{route.RoutePattern.RawText}' has no API version.");
    }
}
