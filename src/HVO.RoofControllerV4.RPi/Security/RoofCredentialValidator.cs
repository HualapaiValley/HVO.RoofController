using System;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Checks that the credential behind a principal still stands: the API key still exists with the same name and role,
/// or the session is still open with the same person and role (and, for a PIN session, the kiosk key it was opened at
/// is still a kiosk key). Long-lived connections (the status hub, the console's Blazor circuit and cookie) use it to
/// end access when a key is removed or rotated, or a session ends.
/// </summary>
public sealed class RoofCredentialValidator
{
    private readonly RoofApiKeyStore _keys;
    private readonly RoofIdentityStore? _identity;

    internal RoofCredentialValidator(RoofApiKeyStore keys, RoofIdentityStore? identity)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _identity = identity;
    }

    /// <summary>A validator for API keys only (no people or sessions).</summary>
    public static RoofCredentialValidator ForKeysOnly(RoofApiKeyStore keys) => new(keys, identity: null);

    /// <summary>True when <paramref name="user"/>'s credential still stands. An anonymous principal has nothing to revoke.</summary>
    public bool IsStillValid(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return true;
        }

        var role = RoofPrincipalFactory.GetHighestRole(user);
        switch (RoofPrincipalFactory.GetCredentialKind(user))
        {
            case RoofCredentialKind.ApiKey:
                return _keys.TryFindByKeyId(user.FindFirst(RoofPrincipalFactory.KeyIdClaimType)?.Value, out var key)
                    && string.Equals(key.Name, user.Identity.Name, StringComparison.Ordinal)
                    && string.Equals(key.Role, role, StringComparison.Ordinal);

            case RoofCredentialKind.Session:
            case RoofCredentialKind.Pin:
                return _identity is not null
                    && _identity.TryGetLiveSession(user.FindFirst(RoofPrincipalFactory.SessionIdClaimType)?.Value, out var session)
                    && string.Equals(session.UserName, user.Identity.Name, StringComparison.Ordinal)
                    && string.Equals(session.Role, role, StringComparison.Ordinal)
                    && IsDeviceStillKiosk(session);

            default:
                return false;
        }
    }

    /// <summary>
    /// Finds the open session for a bearer token. With <paramref name="touch"/>, the request counts as activity for a
    /// PIN session's idle timeout. <paramref name="failure"/> says why a token was refused, never containing the token.
    /// </summary>
    internal bool TryAuthenticateSession(
        string? token,
        bool touch,
        [NotNullWhen(true)] out StoredSession? session,
        out string? failure)
    {
        session = null;
        if (_identity is null)
        {
            failure = "sessions are not enabled";
            return false;
        }

        if (!_identity.IsAvailable)
        {
            failure = "the identity store is unavailable";
            return false;
        }

        if (!_identity.TryValidateToken(token, touch, out var found, out failure))
        {
            return false;
        }

        if (!IsDeviceStillKiosk(found))
        {
            failure = "the kiosk key the PIN session was opened at was removed or is no longer a kiosk key";
            return false;
        }

        session = found;
        return true;
    }

    private bool IsDeviceStillKiosk(StoredSession session)
        => session.Kind != RoofCredentialKind.Pin
            || (_keys.TryFindByKeyId(session.DeviceKeyId, out var device) && device.Kiosk);
}
