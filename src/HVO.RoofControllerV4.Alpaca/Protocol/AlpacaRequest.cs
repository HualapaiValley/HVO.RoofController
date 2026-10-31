using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.RoofControllerV4.Alpaca.Protocol;

/// <summary>
/// One Alpaca request's parameters, as the Alpaca API defines them: a GET's in the query string, whose names are matched
/// whatever their case; a PUT's in a form-encoded body, whose names must be cased exactly (a wrongly cased name is
/// ignored, as if it were not sent). A missing <c>ClientID</c> or <c>ClientTransactionID</c> counts as 0; one sent
/// with a value that is not a number from 0 to 4294967295 makes the request malformed.
/// </summary>
public sealed class AlpacaRequest
{
    /// <summary>The largest PUT body read; a larger one is refused.</summary>
    public const int MaximumBodyBytes = 16 * 1024;

    public const string ClientIdName = "ClientID";
    public const string ClientTransactionIdName = "ClientTransactionID";

    private readonly Dictionary<string, string> _parameters;

    private AlpacaRequest(bool isPut, Dictionary<string, string> parameters, IPAddress remoteAddress)
    {
        IsPut = isPut;
        _parameters = parameters;
        RemoteAddress = remoteAddress;
        ClientId = ParseId(ClientIdName, Find(ClientIdName));
        ClientTransactionId = ParseId(ClientTransactionIdName, Find(ClientTransactionIdName));
    }

    public bool IsPut { get; }

    /// <summary>The client's address (IPv4 when it came over a dual-stack socket).</summary>
    public IPAddress RemoteAddress { get; }

    /// <summary>The client's <c>ClientID</c>, or 0. Untrusted: it only tells one client from another at an address.</summary>
    public uint ClientId { get; }

    /// <summary>The client's <c>ClientTransactionID</c>, or 0; echoed in the answer.</summary>
    public uint ClientTransactionId { get; }

    /// <summary>Reads the parameters of <paramref name="context"/>'s request.</summary>
    /// <exception cref="AlpacaBadRequestException">
    /// A PUT whose body is not form-encoded or is too large, or a ClientID or ClientTransactionID that is not a number.
    /// </exception>
    public static async Task<AlpacaRequest> ReadAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        var remote = Security.AlpacaNetworks.Normalize(context.Connection.RemoteIpAddress ?? IPAddress.None);
        if (HttpMethods.IsGet(request.Method))
        {
            return new AlpacaRequest(false, Parse(request.QueryString.Value, StringComparer.OrdinalIgnoreCase), remote);
        }

        var body = await ReadBodyAsync(context).ConfigureAwait(false);
        if (body.Length > 0 && !IsFormEncoded(request.ContentType))
        {
            throw new AlpacaBadRequestException("PUT parameters must be form-encoded (application/x-www-form-urlencoded).");
        }

        return new AlpacaRequest(true, Parse(body, StringComparer.Ordinal), remote);
    }

    /// <summary>The value of the parameter <paramref name="name"/>, or null when it was not sent (or, in a PUT, was cased otherwise).</summary>
    public string? Find(string name) => _parameters.TryGetValue(name, out var value) ? value : null;

    /// <summary>The value of a required parameter.</summary>
    /// <exception cref="AlpacaBadRequestException">It was not sent.</exception>
    public string Require(string name)
        => Find(name) ?? throw new AlpacaBadRequestException($"The {name} parameter is missing{(IsPut ? " (PUT parameter names are case-sensitive)" : string.Empty)}.");

    /// <summary>A required true or false parameter.</summary>
    /// <exception cref="AlpacaBadRequestException">It was not sent, or is not true or false.</exception>
    public bool RequireBoolean(string name)
    {
        var text = Require(name).Trim();
        return bool.TryParse(text, out var value)
            ? value
            : throw new AlpacaBadRequestException($"The {name} parameter must be true or false, got '{Shorten(text)}'.");
    }

    /// <summary>A required number.</summary>
    /// <exception cref="AlpacaBadRequestException">It was not sent, or is not a finite number.</exception>
    public double RequireDouble(string name)
    {
        var text = Require(name).Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw new AlpacaBadRequestException($"The {name} parameter must be a number, got '{Shorten(text)}'.");
    }

    private static uint ParseId(string name, string? text)
        => text is null ? 0
            : uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id
            : throw new AlpacaBadRequestException($"{name} must be a number from 0 to {uint.MaxValue}, got '{Shorten(text)}'.");

    private static Dictionary<string, string> Parse(string? text, StringComparer comparer)
    {
        var parameters = new Dictionary<string, string>(comparer);
        foreach (var pair in new QueryStringEnumerable(text))
        {
            var name = pair.DecodeName().ToString();
            if (name.Length > 0)
            {
                // The first of a repeated name counts.
                parameters.TryAdd(name, pair.DecodeValue().ToString());
            }
        }

        return parameters;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.ContentLength > MaximumBodyBytes)
        {
            throw new AlpacaBadRequestException($"The request body is larger than {MaximumBodyBytes} bytes.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumBodyBytes)
            {
                throw new AlpacaBadRequestException($"The request body is larger than {MaximumBodyBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (DecoderFallbackException)
        {
            throw new AlpacaBadRequestException("The request body is not UTF-8.");
        }
    }

    private static bool IsFormEncoded(string? contentType)
        => contentType is not null
            && contentType.Split(';')[0].Trim().Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text) => text.Length <= 32 ? text : text[..32] + "…";
}
