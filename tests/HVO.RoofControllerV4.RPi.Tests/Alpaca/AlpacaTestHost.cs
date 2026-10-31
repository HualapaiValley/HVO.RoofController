using System.Net;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AlpacaProgram = HVO.RoofControllerV4.Alpaca.Program;
using ClientTestSupport = HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The Alpaca server in memory (#220), built by its own <c>Program.BuildApp</c> with its settings, in front of a
/// controller: an in-memory one (<see cref="RoofApiTestHost"/>) reached through the client library, as in production.
/// Requests come from <see cref="ClientAddress"/> unless a test says otherwise. Disposing it disposes the controller too.
/// </summary>
internal sealed class AlpacaTestHost : IAsyncDisposable
{
    /// <summary>A LAN client.</summary>
    public static readonly IPAddress ClientAddress = IPAddress.Parse("192.168.1.50");

    private readonly WebApplication _app;
    private readonly string _directory;

    private AlpacaTestHost(WebApplication app, string directory, HttpClient client, RoofApiTestHost? controller)
    {
        _app = app;
        _directory = directory;
        Http = client;
        Controller = controller;
    }

    /// <summary>The controller behind the server, or null for one that cannot be reached.</summary>
    public RoofApiTestHost? Controller { get; }

    public HttpClient Http { get; }

    public IServiceProvider Services => _app.Services;

    public string DataPath => Path.Combine(_directory, "data");

    /// <summary>
    /// Starts the server in front of <paramref name="controller"/> (null: a controller that cannot be reached), with the
    /// settings given on top of the test defaults, requests from <paramref name="remote"/>.
    /// </summary>
    public static Task<AlpacaTestHost> StartAsync(
        RoofApiTestHost? controller,
        IReadOnlyDictionary<string, string?>? settings = null,
        IPAddress? remote = null,
        string apiKey = TestApiKeys.Viewer,
        bool waitForStatus = true)
        => StartAsync(controller is null ? null : () => controller.Server, controller, settings, remote, apiKey, waitForStatus);

    /// <summary>
    /// Starts the server in front of a controller whose every REST request <paramref name="answer"/> answers, and whose
    /// status hub cannot be reached (for the answers a real controller would not give).
    /// </summary>
    public static Task<AlpacaTestHost> StartAsync(Func<HttpRequestMessage, HttpResponseMessage> answer)
        => StartAsync(null, null, null, null, TestApiKeys.Viewer, waitForStatus: false, answer);

    /// <summary>Starts the server in front of the controller <paramref name="server"/> serves (the emulated rig's).</summary>
    public static Task<AlpacaTestHost> StartAsync(Func<TestServer> server, IReadOnlyDictionary<string, string?>? settings = null)
        => StartAsync(server, null, settings, null, TestApiKeys.Viewer, waitForStatus: true);

    private static async Task<AlpacaTestHost> StartAsync(
        Func<TestServer>? server,
        RoofApiTestHost? controller,
        IReadOnlyDictionary<string, string?>? settings,
        IPAddress? remote,
        string apiKey,
        bool waitForStatus,
        Func<HttpRequestMessage, HttpResponseMessage>? answer = null)
    {
        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-tests-").FullName;
        var keyFile = Path.Combine(directory, "key");
        File.WriteAllText(keyFile, apiKey + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var values = new Dictionary<string, string?>
        {
            ["Alpaca:Enabled"] = "true",
            ["Alpaca:ListenAddresses:0"] = "127.0.0.1",
            ["Alpaca:ControllerUrl"] = "http://localhost/",
            ["Alpaca:ControllerApiKeyFile"] = keyFile,
            ["Alpaca:DataPath"] = Path.Combine(directory, "data"),
            ["Alpaca:Discovery:Enabled"] = "false",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            values[key] = value;
        }

        var args = values.Where(pair => pair.Value is not null).Select(pair => $"--{pair.Key}={pair.Value}").ToArray();
        var app = AlpacaProgram.BuildApp(args, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IStartupFilter>(new RemoteAddress(remote ?? ClientAddress));
            builder.Services.Replace(ServiceDescriptor.Singleton(_ => server is null
                ? new RoofControllerClient(new RoofConnectionOptions
                {
                    BaseAddress = new Uri("http://localhost/"),
                    Credential = new RoofApiKeyCredential(apiKey),
                    CreateHandler = () => answer is null ? new UnreachableHandler() : new HVO.RoofControllerV4.RPi.Tests.Web.WebTestSupport.StubHandler(answer),
                    WebSocketFactory = (_, _, _) => throw new HttpRequestException("The controller cannot be reached (test)."),
                    StatusFeed = ClientTestSupport.FastFeed,
                    RequestTimeout = TimeSpan.FromSeconds(5),
                    StopTimeout = TimeSpan.FromSeconds(5),
                })
                : ClientTestSupport.CreateClient(server, new RoofApiKeyCredential(apiKey), stopTimeout: TimeSpan.FromSeconds(5))));
        }) ?? throw new InvalidOperationException("The Alpaca server is off.");

        await app.StartAsync();
        var host = new AlpacaTestHost(app, directory, app.GetTestClient(), controller);
        if (waitForStatus && server is not null)
        {
            await host.ConnectAsync(clientId: 999);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while ((await host.GetAsync("shutterstatus", "ClientID=999")).GetProperty("ErrorNumber").GetInt32() != 0)
            {
                if (DateTime.UtcNow > deadline)
                {
                    await host.DisposeAsync();
                    throw new AssertFailedException("The Alpaca server did not get the controller's status within 10 s.");
                }

                await Task.Delay(20);
            }

            await host.PutAsync("connected", "Connected=false&ClientID=999");
        }

        return host;
    }

    /// <summary>GETs a Dome member and returns the envelope.</summary>
    public async Task<JsonElement> GetAsync(string member, string query = "ClientID=1&ClientTransactionID=1")
    {
        using var response = await Http.GetAsync($"/api/v1/dome/0/{member}?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>PUTs a Dome member, form-encoded, and returns the envelope.</summary>
    public async Task<JsonElement> PutAsync(string member, string form = "ClientID=1&ClientTransactionID=1")
    {
        using var response = await PutRawAsync(member, form);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>PUTs a Dome member, form-encoded, and returns the response as it is.</summary>
    public Task<HttpResponseMessage> PutRawAsync(string member, string form)
        => Http.PutAsync($"/api/v1/dome/0/{member}", new StringContent(form, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));

    /// <summary>Connects client <paramref name="clientId"/> at the test's address.</summary>
    public async Task ConnectAsync(uint clientId = 1)
        => (await PutAsync("connected", $"Connected=true&ClientID={clientId}&ClientTransactionID=1")).GetProperty("ErrorNumber").GetInt32().Should().Be(0);

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Controller?.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Gives every request the client address a test chooses, before the server's access filter sees it.</summary>
    private sealed class RemoteAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("The controller cannot be reached (test).");
    }
}
