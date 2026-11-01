using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.AlpacaClientMatrix;

/// <summary>One request the client sent, as the proxy saw it. Never the credential itself.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The path, without the query.</param>
/// <param name="Authorization">Whether the request carried an <c>Authorization</c> header.</param>
/// <param name="BasicWithKey">Whether that header was HTTP Basic with the External key as both the user name and the password.</param>
/// <param name="Status">The Alpaca server's HTTP status.</param>
/// <param name="Challenged">Whether the server answered 401 or sent <c>WWW-Authenticate</c>.</param>
internal sealed record ProxiedRequest(string Method, string Path, bool Authorization, bool BasicWithKey, int Status, bool Challenged);

/// <summary>
/// A forwarding HTTP proxy on loopback between one Alpaca client and the Alpaca server, which records what the client
/// sent: whether each request carried HTTP Basic with the External key (compared, never kept or printed), and whether the
/// server ever challenged it. So the matrix sees on the wire that a client sends its credential pre-emptively.
/// </summary>
internal sealed class CapturingProxy : IAsyncDisposable
{
    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length", "Expect", "Proxy-Connection", "TE", "Upgrade"
    };

    private readonly WebApplication _app;
    private readonly HttpClient _upstream = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly byte[] _expected;
    private readonly List<ProxiedRequest> _requests = [];

    private CapturingProxy(WebApplication app, Uri upstream, string externalKey)
    {
        _app = app;
        _expected = Encoding.UTF8.GetBytes($"{externalKey}:{externalKey}");
        _app.Run(context => ForwardAsync(context, upstream));
    }

    public int Port { get; private init; }

    /// <summary>What the client has sent so far, in order.</summary>
    public IReadOnlyList<ProxiedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Starts a proxy listening on 127.0.0.1:<paramref name="port"/> that forwards to <paramref name="upstream"/>.</summary>
    public static async Task<CapturingProxy> StartAsync(int port, Uri upstream, string externalKey)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        var proxy = new CapturingProxy(builder.Build(), upstream, externalKey) { Port = port };
        await proxy._app.StartAsync();
        return proxy;
    }

    private async Task ForwardAsync(HttpContext context, Uri upstream)
    {
        var target = new Uri(upstream, context.Request.Path + context.Request.QueryString);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            request.Content = new ByteArrayContent(body.ToArray());
        }

        foreach (var (name, values) in context.Request.Headers)
        {
            if (NotForwarded.Contains(name))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, values.ToArray()))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        using var response = await _upstream.SendAsync(request, context.RequestAborted);
        var challenged = response.StatusCode == HttpStatusCode.Unauthorized || response.Headers.WwwAuthenticate.Count > 0;
        lock (_requests)
        {
            _requests.Add(new ProxiedRequest(context.Request.Method, context.Request.Path, authorization.Length > 0,
                IsBasicWithKey(authorization), (int)response.StatusCode, challenged));
        }

        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } type)
        {
            context.Response.ContentType = type.ToString();
        }

        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private bool IsBasicWithKey(string authorization)
    {
        const string Scheme = "Basic ";
        if (!authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(authorization[Scheme.Length..].Trim()), _expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _upstream.Dispose();
    }
}
