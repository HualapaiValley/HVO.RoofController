using System.Collections.Concurrent;
using System.Net;
using IPNetwork = System.Net.IPNetwork;

namespace HVO.RoofControllerV4.Alpaca.Security;

/// <summary>
/// The first thing every request meets. It refuses, before anything reads the request:
/// <list type="bullet">
/// <item>a client outside <see cref="AlpacaOptions.AllowedNetworks"/> (403);</item>
/// <item>a <c>Host</c> header that is not an IP address, <c>localhost</c>, this computer's name or one of
/// <see cref="AlpacaOptions.AllowedHosts"/> (400): a page served from another name cannot reach the server through DNS
/// rebinding;</item>
/// <item>a request from a web page of another origin (an <c>Origin</c> header naming another host, 403). Alpaca clients
/// send no <c>Origin</c>, and the server sends no CORS headers, so a browser never lets another site read an
/// answer.</item>
/// </list>
/// The address allowlist is defence in depth: behind Docker's NAT every client can look like the gateway. The real
/// controls are the address the server listens on (or the port is published on) and the host's firewall.
/// </summary>
internal sealed class AlpacaAccessFilter
{
    private static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(1);

    private readonly RequestDelegate _next;
    private readonly IReadOnlyList<IPNetwork> _networks;
    private readonly HashSet<string> _hosts;
    private readonly ILogger<AlpacaAccessFilter> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(IPAddress?, string), DateTimeOffset> _logged = new();

    public AlpacaAccessFilter(RequestDelegate next, AlpacaOptions options, TimeProvider time, ILogger<AlpacaAccessFilter> logger)
    {
        _next = next;
        _networks = options.EffectiveAllowedNetworks;
        _hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };
        var machine = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(machine))
        {
            _hosts.Add(machine);
            _hosts.Add(machine + ".local");
        }

        foreach (var host in options.AllowedHosts)
        {
            _hosts.Add(host.Trim().TrimEnd('.'));
        }

        _time = time;
        _logger = logger;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !AlpacaNetworks.Contains(_networks, remote))
        {
            Refused(remote, "address", "Refused a request from {RemoteAddress}, which is outside Alpaca:AllowedNetworks", remote);
            return Text(context, StatusCodes.Status403Forbidden, "This Alpaca server does not answer requests from this address.");
        }

        if (!IsAllowedHost(context.Request.Host))
        {
            Refused(remote, "host", "Refused a request from {RemoteAddress} for a host this server does not answer for (see Alpaca:AllowedHosts)", remote);
            return Text(context, StatusCodes.Status400BadRequest, "This Alpaca server does not answer for that host name.");
        }

        if (context.Request.Headers.Origin is { Count: > 0 } origin && !IsSameOrigin(origin.ToString(), context.Request.Host))
        {
            Refused(remote, "origin", "Refused a request from {RemoteAddress} sent by a web page of another origin", remote);
            return Text(context, StatusCodes.Status403Forbidden, "This Alpaca server does not answer requests from other web pages.");
        }

        return _next(context);
    }

    /// <summary>True when <paramref name="host"/> may be served: an IP address, or a name the server answers for.</summary>
    internal bool IsAllowedHost(HostString host)
    {
        if (!host.HasValue)
        {
            return false;
        }

        var name = host.Host;
        if (name.StartsWith('[') && name.EndsWith(']'))
        {
            name = name[1..^1];
        }

        return IPAddress.TryParse(name, out _) || _hosts.Contains(name.TrimEnd('.'));
    }

    private static bool IsSameOrigin(string origin, HostString host)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && string.Equals(uri.Authority, host.Value, StringComparison.OrdinalIgnoreCase);

    internal static Task Text(HttpContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(text);
    }

    private void Refused(IPAddress? remote, string reason, string message, IPAddress? address)
    {
        var now = _time.GetUtcNow();
        var key = (remote, reason);
        if (_logged.TryGetValue(key, out var last) && now - last < LogEvery)
        {
            return;
        }

        if (_logged.Count > 1024)
        {
            _logged.Clear();
        }

        _logged[key] = now;
#pragma warning disable CA2254 // The templates are the constant strings above.
        _logger.LogWarning(message, address?.ToString() ?? "(unknown)");
#pragma warning restore CA2254
    }
}
