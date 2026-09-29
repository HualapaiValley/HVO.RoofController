using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebProgram = HVO.RoofControllerV4.Web.Program;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// A stand-in controller for the web UI's sign-in: people with passwords, sessions, and a log of what was asked of it
/// (including the credential headers). Every other request is answered ready.
/// </summary>
internal sealed class FakeController
{
    public const string AdaPassword = "correct horse battery";

    private readonly Dictionary<string, (string Password, string Role)> _people = new(StringComparer.Ordinal)
    {
        ["ada"] = (AdaPassword, RoofControllerApiContract.AdminRole),
        ["olga"] = (AdaPassword, RoofControllerApiContract.OperatorRole),
        ["vic"] = (AdaPassword, RoofControllerApiContract.ViewerRole),
    };

    private readonly HashSet<string> _endedTokens = new(StringComparer.Ordinal);
    private int _sessions;

    public FakeController(TimeProvider? time = null)
    {
        Time = time ?? TimeProvider.System;
    }

    public TimeProvider Time { get; }

    public List<FakeControllerRequest> Requests { get; } = [];

    /// <summary>When set, answers every sign-in instead.</summary>
    public Func<HttpResponseMessage>? SignInAnswer { get; set; }

    /// <summary>When set, answers every password change instead.</summary>
    public Func<HttpResponseMessage>? PasswordAnswer { get; set; }

    /// <summary>When set, answers any other request it does not return null for, before the default.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? OtherAnswer { get; set; }

    /// <summary>When set, the controller's answer to the anonymous mode read; otherwise it answers as for anything else.</summary>
    public RoofModeResponse? Mode { get; set; }

    /// <summary>
    /// When set, each request waits for the task it returns before it is answered, without holding a thread (as a real
    /// controller that is slow to answer does).
    /// </summary>
    public Func<HttpRequestMessage, Task>? Hold { get; set; }

    /// <summary>The role the next sessions get, whatever the person's own.</summary>
    public string? RoleOverride { get; set; }

    public int SignIns => Logged(HttpMethod.Post, RoofApiRoutesTest.Session).Count;

    public IReadOnlyList<FakeControllerRequest> Logged(HttpMethod method, string path)
    {
        lock (Requests)
        {
            return Requests.Where(request => request.Method == method && request.Path == "/" + path).ToList();
        }
    }

    /// <summary>Ends a session at the controller (as an admin or a password change would).</summary>
    public void EndSession(string token)
    {
        lock (_endedTokens)
        {
            _endedTokens.Add(token);
        }
    }

    public HttpMessageHandler CreateHandler() => new HoldingHandler(this) { InnerHandler = new WebTestSupport.StubHandler(Answer) };

    private sealed class HoldingHandler(FakeController controller) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (controller.Hold is { } hold)
            {
                await hold(request).WaitAsync(cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    public HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var authorization = request.Headers.Authorization?.ToString();
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        lock (Requests)
        {
            var onBehalfOf = request.Headers.TryGetValues(RoofIdentityContract.OnBehalfOfHeaderName, out var names) ? string.Join(",", names) : null;
            Requests.Add(new FakeControllerRequest(request.Method, path, authorization, request.Headers.Contains("X-Api-Key"), body, onBehalfOf));
        }

        if (path == "/" + RoofApiRoutesTest.Session && request.Method == HttpMethod.Post)
        {
            return SignInAnswer?.Invoke() ?? SignIn(body);
        }

        var token = authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? authorization["Bearer ".Length..] : null;
        if (token is not null && IsEnded(token))
        {
            return Problem(HttpStatusCode.Unauthorized, null);
        }

        if (path == "/" + RoofApiRoutesTest.Session && request.Method == HttpMethod.Delete)
        {
            EndSession(token!);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (path == "/" + RoofApiRoutesTest.Mode && request.Method == HttpMethod.Get && Mode is { } mode)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(mode, options: RoofClientJson.Options) };
        }

        if (path == "/" + RoofApiRoutesTest.Password && request.Method == HttpMethod.Post)
        {
            var change = JsonSerializer.Deserialize<RoofPasswordChangeRequest>(body!, JsonSerializerOptions.Web)!;
            return PasswordAnswer?.Invoke() ?? (change.CurrentPassword == AdaPassword
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : Problem(HttpStatusCode.Unauthorized, RoofControllerErrorCode.SignInFailed));
        }

        return OtherAnswer?.Invoke(request) ?? WebTestSupport.Text(HttpStatusCode.OK, "Healthy");
    }

    public static HttpResponseMessage Problem(HttpStatusCode status, RoofControllerErrorCode? code, TimeSpan? retryAfter = null)
    {
        var problem = new Dictionary<string, object?> { ["status"] = (int)status, ["title"] = "Refused" };
        if (code is not null)
        {
            problem[RoofControllerApiContract.ProblemCodeExtension] = code.ToString();
        }

        var response = new HttpResponseMessage(status) { Content = JsonContent.Create(problem) };
        if (retryAfter is not null)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter.Value);
        }

        return response;
    }

    private bool IsEnded(string token)
    {
        lock (_endedTokens)
        {
            return _endedTokens.Contains(token);
        }
    }

    private HttpResponseMessage SignIn(string? body)
    {
        var request = JsonSerializer.Deserialize<RoofSignInRequest>(body!, JsonSerializerOptions.Web)!;
        if (!_people.TryGetValue(request.Name!, out var person) || person.Password != request.Password)
        {
            return Problem(HttpStatusCode.Unauthorized, RoofControllerErrorCode.SignInFailed);
        }

        var number = Interlocked.Increment(ref _sessions);
        var session = new RoofSessionResponse(
            $"token-{number}-{request.Name}",
            $"session-{number}",
            request.Name!,
            RoleOverride ?? person.Role,
            RoofCredentialKind.Session,
            Time.GetUtcNow().AddHours(12),
            null);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(session) };
    }
}

internal sealed record FakeControllerRequest(HttpMethod Method, string Path, string? Authorization, bool HasApiKey, string? Body, string? OnBehalfOf = null);

/// <summary>The controller's routes, as the client library sends them.</summary>
internal static class RoofApiRoutesTest
{
    public const string Session = "api/v4.0/Auth/Session";

    public const string Password = "api/v4.0/Auth/Password";

    public const string Stop = "api/v4.0/RoofControl/Stop";

    public const string Mode = "api/v4.0/RoofControl/Mode";

    public static string Camera(int cameraId) => $"api/v1.0/Camera/{cameraId}/mjpeg";
}

/// <summary>Reaches <see cref="FakeController"/> instead of a controller.</summary>
internal sealed class FakeControllerConnector(FakeController controller, IOptions<RoofWebOptions> options, ILoggerFactory loggerFactory, TimeProvider time)
    : RoofControllerConnector(options, loggerFactory, time)
{
    public List<RoofControllerClient> Created { get; } = [];

    public override RoofControllerClient Create(RoofCredential? credential, TimeSpan requestTimeout)
    {
        var client = new RoofControllerClient(new RoofConnectionOptions
        {
            BaseAddress = new Uri("http://localhost:8080"),
            Credential = credential,
            RequestTimeout = requestTimeout,
            CreateHandler = controller.CreateHandler,
            TimeProvider = controller.Time,
        });
        lock (Created)
        {
            Created.Add(client);
        }

        return client;
    }
}

/// <summary>The web UI on a test server, with <see cref="FakeController"/> as its controller.</summary>
internal sealed class WebHost : IAsyncDisposable
{
    private WebHost(WebApplication app, FakeController controller)
    {
        App = app;
        Controller = controller;
    }

    public WebApplication App { get; }

    public FakeController Controller { get; }

    public WebSessionStore Sessions => App.Services.GetRequiredService<WebSessionStore>();

    public static async Task<WebHost> StartAsync(string[]? args = null, FakeController? controller = null, Action<WebApplicationBuilder>? customize = null)
    {
        controller ??= new FakeController();
        var app = WebProgram.BuildApp(args ?? [], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton(new RoofControllerClient(new RoofConnectionOptions
            {
                BaseAddress = new Uri("http://localhost:8080"),
                CreateHandler = controller.CreateHandler,
                RequestTimeout = TimeSpan.FromSeconds(5),
            }));
            builder.Services.AddSingleton(controller);
            builder.Services.AddSingleton<RoofControllerConnector, FakeControllerConnector>();
            customize?.Invoke(builder);
        });
        await app.StartAsync();
        return new WebHost(app, controller);
    }

    /// <summary>A browser: keeps cookies, does not follow redirects.</summary>
    public WebBrowserClient Browser() => new(App.GetTestServer());

    public async ValueTask DisposeAsync() => await App.DisposeAsync();
}

/// <summary>A browser for the test server: keeps its cookies and reads the forms' antiforgery tokens.</summary>
internal sealed partial class WebBrowserClient : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _client;

    public WebBrowserClient(TestServer server)
    {
        _client = new HttpClient(new CookieHandler(_cookies) { InnerHandler = server.CreateHandler() })
        {
            BaseAddress = new Uri("http://localhost/"),
        };
    }

    public Uri BaseAddress => _client.BaseAddress!;

    public string? Cookie(string name, string path = "/")
        => _cookies.GetCookies(new Uri(BaseAddress, path)).FirstOrDefault(cookie => cookie.Name == name)?.Value;

    /// <summary>Removes one cookie, as the browser does when the web UI deletes it.</summary>
    public void RemoveCookie(string name, string path = "/")
    {
        foreach (System.Net.Cookie cookie in _cookies.GetCookies(new Uri(BaseAddress, path)))
        {
            if (cookie.Name == name)
            {
                cookie.Expired = true;
            }
        }
    }

    public void ClearCookies()
    {
        foreach (System.Net.Cookie cookie in _cookies.GetCookies(BaseAddress))
        {
            cookie.Expired = true;
        }
    }

    public Task<HttpResponseMessage> GetAsync(string path) => _client.GetAsync(new Uri(path, UriKind.Relative));

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _client.SendAsync(request);

    /// <summary>Gets a page's stream: answers once its headers arrive, and the body is read as it comes.</summary>
    public Task<HttpResponseMessage> OpenStreamAsync(string path)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative)), HttpCompletionOption.ResponseHeadersRead);

    /// <summary>Opens the sign-in page and posts its form.</summary>
    public async Task<HttpResponseMessage> SignInAsync(string name, string password, string? returnUrl = null)
    {
        var token = await GetFormTokenAsync("/signin");
        return await PostFormAsync("/account/signin", new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["name"] = name,
            ["password"] = password,
            ["returnUrl"] = returnUrl ?? "/",
        });
    }

    public async Task<string> GetFormTokenAsync(string page)
    {
        using var response = await GetAsync(page);
        var html = await response.Content.ReadAsStringAsync();
        var input = TokenInputPattern().Match(html);
        var value = input.Success ? ValuePattern().Match(input.Value) : Match.Empty;
        return value.Success ? WebUtility.HtmlDecode(value.Groups[1].Value) : throw new InvalidOperationException($"{page} has no antiforgery token.");
    }

    /// <summary>
    /// Posts a form. With <see cref="HttpCompletionOption.ResponseHeadersRead"/>, answers as soon as the headers arrive,
    /// while the web UI may still be working on the post.
    /// </summary>
    public Task<HttpResponseMessage> PostFormAsync(
        string path,
        IReadOnlyDictionary<string, string> fields,
        string? origin = null,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = new FormUrlEncodedContent(fields) };
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return _client.SendAsync(request, completion);
    }

    public void Dispose() => _client.Dispose();

    [GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*>")]
    private static partial Regex TokenInputPattern();

    [GeneratedRegex("value=\"([^\"]+)\"")]
    private static partial Regex ValuePattern();

    private sealed class CookieHandler(CookieContainer cookies) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = new Uri(new Uri("http://localhost/"), request.RequestUri!);
            var header = cookies.GetCookieHeader(uri);
            if (header.Length > 0)
            {
                request.Headers.Add("Cookie", header);
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var setCookie in setCookies)
                {
                    cookies.SetCookies(uri, setCookie);
                }
            }

            return response;
        }
    }
}
