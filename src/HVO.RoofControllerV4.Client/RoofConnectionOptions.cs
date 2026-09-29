using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.Client;

/// <summary>Opens a WebSocket for the status hub with the given headers. Tests use it to reach an in-memory server.</summary>
public delegate ValueTask<WebSocket> RoofWebSocketFactory(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken);

/// <summary>Where the controller is and how to reach it.</summary>
public sealed class RoofConnectionOptions
{
    /// <summary>The controller's base address, for example <c>https://roof.local:5001/</c>.</summary>
    public required Uri BaseAddress { get; init; }

    /// <summary>The initial credential. <see cref="RoofControllerClient.Credential"/> can change it later.</summary>
    public RoofCredential? Credential { get; init; }

    /// <summary>How long an ordinary request may take. Default 30 s.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long Stop waits for the controller's answer before reporting that it could not be confirmed. Default 10 s.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The SHA-256 of the controller's certificate, as hex (colons and spaces are ignored). When set, a certificate with
    /// this hash is accepted even if it is self-signed or names another host; any other certificate must still be
    /// trusted normally.
    /// </summary>
    public string? ServerCertificateSha256 { get; init; }

    /// <summary>
    /// Creates the innermost HTTP handler. Null uses a <see cref="SocketsHttpHandler"/> that honours
    /// <see cref="ServerCertificateSha256"/>. Called once for requests, once for Stop, and once per status hub connection.
    /// </summary>
    public Func<HttpMessageHandler>? CreateHandler { get; init; }

    /// <summary>Opens the status hub's WebSocket. Null uses the platform's WebSocket client.</summary>
    public RoofWebSocketFactory? WebSocketFactory { get; init; }

    /// <summary>The status hub's reconnection and staleness settings.</summary>
    public RoofStatusFeedOptions StatusFeed { get; init; } = new();

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public ILoggerFactory? LoggerFactory { get; init; }

    internal void Validate()
    {
        if (!BaseAddress.IsAbsoluteUri || BaseAddress.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The base address must be an absolute http or https URL.", nameof(BaseAddress));
        }

        if (RequestTimeout <= TimeSpan.Zero || StopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Timeouts must be positive.");
        }

        if (ServerCertificateSha256 is not null)
        {
            _ = RoofCertificatePin.Parse(ServerCertificateSha256);
        }

        StatusFeed.Validate();
    }

    /// <summary>The base address with a trailing slash, so relative routes resolve beneath it.</summary>
    internal Uri NormalizedBaseAddress => BaseAddress.AbsoluteUri.EndsWith('/') ? BaseAddress : new Uri(BaseAddress.AbsoluteUri + "/");

    internal HttpMessageHandler CreatePrimaryHandler()
    {
        if (CreateHandler is { } factory)
        {
            return factory() ?? throw new InvalidOperationException("CreateHandler returned null.");
        }

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false
        };

        if (ServerCertificateSha256 is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = RoofCertificatePin.CreateValidator(ServerCertificateSha256);
        }

        return handler;
    }
}

/// <summary>Reconnection and staleness settings for <see cref="RoofStatusFeed"/>.</summary>
public sealed class RoofStatusFeedOptions
{
    /// <summary>The first reconnect delay; each failed attempt doubles it. Default 1 s.</summary>
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest reconnect delay. Default 30 s.</summary>
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Random spread applied to each delay, as a fraction (0.2 is ±20%), so clients do not reconnect in step.</summary>
    public double ReconnectJitter { get; init; } = 0.2;

    /// <summary>
    /// Silence after which the view is stale. The controller sends at least one message every heartbeat interval (1 s);
    /// the default is three intervals.
    /// </summary>
    public TimeSpan StaleAfter { get; init; } = 3 * Common.Models.RoofStatusHubContract.HeartbeatInterval;

    /// <summary>How long one connection attempt may take. Default 15 s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    internal void Validate()
    {
        if (InitialReconnectDelay <= TimeSpan.Zero || MaxReconnectDelay < InitialReconnectDelay)
        {
            throw new ArgumentException("Reconnect delays must be positive, and the maximum at least the initial delay.");
        }

        if (ReconnectJitter is < 0 or >= 1 || double.IsNaN(ReconnectJitter))
        {
            throw new ArgumentException("Reconnect jitter must be at least 0 and less than 1.");
        }

        if (StaleAfter <= TimeSpan.Zero || ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("StaleAfter and ConnectTimeout must be positive.");
        }
    }
}

/// <summary>Accepts a controller's certificate by its SHA-256 hash, for controllers with a self-signed certificate.</summary>
public static class RoofCertificatePin
{
    /// <summary>The certificate's SHA-256 as uppercase hex, the form <see cref="RoofConnectionOptions.ServerCertificateSha256"/> takes.</summary>
    public static string GetSha256(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    }

    internal static byte[] Parse(string pin)
    {
        var hex = new string(pin.Where(c => c is not (':' or ' ' or '-')).ToArray());
        if (hex.Length != 64)
        {
            throw new ArgumentException("The certificate pin must be a SHA-256 hash: 64 hex digits.", nameof(pin));
        }

        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The certificate pin must be a SHA-256 hash: 64 hex digits.", nameof(pin), ex);
        }
    }

    internal static RemoteCertificateValidationCallback CreateValidator(string pin)
    {
        var expected = Parse(pin);
        return (_, certificate, _, errors) =>
            errors == SslPolicyErrors.None
            || (certificate is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), expected));
    }
}
