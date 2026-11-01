using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Alpaca.Security;

/// <summary>
/// The key an Alpaca client sent with OpenShutter or CloseShutter (#220): in <c>X-HVO-Api-Key</c>, or as HTTP Basic with
/// the key as both the user name and the password (they must match). The server keeps it for that one request and
/// passes it to the controller for the ticket and the command, never anywhere else; it never logs it, and it never
/// substitutes its own. Reads, the common members and AbortSlew need no credential.
/// </summary>
public sealed class AlpacaClientCredential
{
    /// <summary>The longest key accepted (the controller's own bound).</summary>
    public const int MaximumKeyLength = 512;

    private AlpacaClientCredential(string? key, string? method, string? problem)
    {
        Key = key;
        Method = method;
        Problem = problem;
    }

    /// <summary>The key, when one was sent well-formed; never logged.</summary>
    internal string? Key { get; }

    /// <summary>How it came: <c>header</c> or <c>basic</c>; null when none did.</summary>
    public string? Method { get; }

    /// <summary>Why the credential cannot be used (missing, malformed, the two parts differ), in words that quote none of it.</summary>
    public string? Problem { get; }

    /// <summary>True when a usable key came.</summary>
    public bool IsUsable => Key is not null && Problem is null;

    /// <summary>Reads the credential from <paramref name="headers"/>.</summary>
    public static AlpacaClientCredential From(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        string? headerKey = null;
        if (headers.TryGetValue(RoofAlpacaContract.ClientKeyHeader, out var values))
        {
            if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
            {
                return Refused("the X-HVO-Api-Key header is empty or sent more than once");
            }

            headerKey = values[0]!;
        }

        string? basicKey = null;
        if (headers.TryGetValue("Authorization", out var authorization))
        {
            if (authorization.Count != 1 || !AuthenticationHeaderValue.TryParse(authorization[0], out var parsed))
            {
                return Refused("the Authorization header is malformed");
            }

            if (!string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(parsed.Parameter))
            {
                return Refused("the Authorization header is not HTTP Basic");
            }

            string pair;
            try
            {
                pair = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(parsed.Parameter));
            }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            {
                return Refused("the HTTP Basic credential is not valid base64 UTF-8");
            }

            var colon = pair.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return Refused("the HTTP Basic credential has no user name and password");
            }

            var user = pair[..colon];
            var password = pair[(colon + 1)..];
            if (!FixedTimeEquals(user, password))
            {
                return Refused("the HTTP Basic user name and password differ (both must be the key)");
            }

            basicKey = user;
        }

        if (headerKey is null && basicKey is null)
        {
            return Refused("no credential was sent (X-HVO-Api-Key, or HTTP Basic with the key as both parts)");
        }

        if (headerKey is not null && basicKey is not null && !FixedTimeEquals(headerKey, basicKey))
        {
            return Refused("the X-HVO-Api-Key header and the HTTP Basic credential carry different keys");
        }

        var key = headerKey ?? basicKey!;
        if (key.Length > MaximumKeyLength || key.Any(c => c is < '!' or > '~'))
        {
            return Refused("the key is not one line of printable characters");
        }

        return new AlpacaClientCredential(key, headerKey is not null ? "header" : "basic", null);
    }

    private static AlpacaClientCredential Refused(string why) => new(null, null, why);

    /// <summary>Compares in time that depends on the lengths only, never on where the two first differ.</summary>
    private static bool FixedTimeEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    /// <summary>Never the key.</summary>
    public override string ToString() => Problem is null ? $"credential ({Method})" : $"no usable credential ({Problem})";
}
