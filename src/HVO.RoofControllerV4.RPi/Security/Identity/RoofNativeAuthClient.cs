using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>Signed native proof; not a controller role or a portable API credential.</summary>
public sealed record RoofNativeAssertion(int Version, string Purpose, string Provider, string Subject, string UserName,
    string EnrollmentStamp, string Audience, string Operation, long IssuedAt, long ExpiresAt, string Nonce);

/// <summary>Private Unix HTTP client. Native account checks fail closed when the host worker is unavailable.</summary>
public sealed class RoofNativeAuthClient : IDisposable
{
    private readonly IOptionsMonitor<RoofIdentityOptions> _options;
    private readonly TimeProvider _time;
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public RoofNativeAuthClient(IOptionsMonitor<RoofIdentityOptions> options, TimeProvider time)
    {
        _options = options; _time = time;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(_options.CurrentValue.LinuxAuth.SocketPath), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        _client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromSeconds(8) };
    }

    public async Task<string?> PasswordAsync(string userName, string password, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.LinuxAuth.Enabled) return null;
        try
        {
            using var result = await _client.PostAsJsonAsync("/v1/password", new { userName, password }, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccessStatusCode) return null;
            var body = await result.Content.ReadFromJsonAsync<AssertionResponse>(cancellationToken).ConfigureAwait(false);
            return body?.Assertion;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException) { return null; }
    }

    public bool IsCurrent(StoredLinuxPrincipal principal, string purpose = "linux-password")
        => CheckCurrent(principal, purpose) == true;

    /// <summary>Null means unavailability; false means the native worker positively rejected this identity.</summary>
    public bool? CheckCurrent(StoredLinuxPrincipal principal, string purpose = "linux-password")
    {
        if (!_options.CurrentValue.LinuxAuth.Enabled) return null;
        try
        {
            using var result = _client.PostAsJsonAsync("/v1/revalidate", new { subject = principal.Subject, enrollmentStamp = principal.EnrollmentStamp, purpose }).GetAwaiter().GetResult();
            if (!result.IsSuccessStatusCode) return null;
            var body = result.Content.ReadFromJsonAsync<RevalidationResponse>().GetAwaiter().GetResult();
            if (body?.Valid is null) return null;
            if (body.Valid == false) return false;
            if (!HVO.RoofControllerV4.Common.Models.RoofIdentityContract.IsValidName(body.UserName)) return null;
            return body.UserName == principal.Name;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException) { return null; }
    }

    /// <summary>
    /// The account's full name from the operating system (its GECOS field, #209), for display only; null when it has
    /// none, the account is not enrolled, or the worker cannot say (unavailable, or older than <c>/v1/profile</c>).
    /// Never a reason to refuse a sign-in.
    /// </summary>
    public string? GetFullName(StoredLinuxPrincipal principal)
    {
        if (!_options.CurrentValue.LinuxAuth.Enabled) return null;
        try
        {
            using var result = _client.PostAsJsonAsync("/v1/profile", new { subject = principal.Subject, enrollmentStamp = principal.EnrollmentStamp }).GetAwaiter().GetResult();
            if (!result.IsSuccessStatusCode) return null;
            return result.Content.ReadFromJsonAsync<ProfileResponse>(Json).GetAwaiter().GetResult()?.FullName;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or NotSupportedException) { return null; }
    }

    public RoofNativeAssertion? Verify(string? assertion, string purpose)
    {
        var options = _options.CurrentValue.LinuxAuth;
        if (!options.Enabled || assertion is not { Length: > 0 and <= 8192 }) return null;
        try
        {
            var parts = assertion.Split('.');
            if (parts.Length != 2) return null;
            var payload = Decode(parts[0]); var signature = Decode(parts[1]);
            var key = File.ReadAllBytes(options.AssertionKeyPath);
            if (key.Length != 32) return null;
            try
            {
                if (signature.Length != 32 || !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, payload), signature)) return null;
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || json.RootElement.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != json.RootElement.EnumerateObject().Count()) return null;
            var claims = JsonSerializer.Deserialize<RoofNativeAssertion>(payload, Json);
            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            if (claims is null || claims.Version != 1 || claims.Provider != "linux" || claims.Purpose != purpose
                || claims.Audience != options.Audience || claims.IssuedAt > now || claims.IssuedAt < now - 30
                || claims.ExpiresAt <= now || claims.ExpiresAt > now + 30
                || claims.ExpiresAt <= claims.IssuedAt || claims.ExpiresAt - claims.IssuedAt > 30
                || !Guid.TryParse(claims.Subject, out _) || !Guid.TryParse(claims.EnrollmentStamp, out _)
                || !HVO.RoofControllerV4.Common.Models.RoofIdentityContract.IsValidName(claims.UserName)
                || Decode(claims.Nonce).Length != 32
                || (purpose == "linux-password" ? claims.Operation != "login" : purpose != "local-admin" || !HVO.RoofControllerV4.Common.Models.RoofIdentityScopes.IsValid(claims.Operation)))
                return null;
            return claims;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException or ArgumentException) { return null; }
    }

    private static byte[] Decode(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))) throw new FormatException();
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    }
    public void Dispose() => _client.Dispose();
    private sealed record AssertionResponse(string Assertion);
    private sealed record RevalidationResponse(bool? Valid, string? UserName);
    private sealed record ProfileResponse(string? FullName);
}
