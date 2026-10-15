using System.Security.Claims;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;

namespace HVO.RoofControllerV4.RPi.Settings;

/// <summary>
/// Whether a caller holds a local credential, which local-only settings need: a configured API key marked
/// <see cref="RoofApiKeyOptions.Local"/>, or a verified native local configure assertion. A PIN or ordinary password
/// session and a key added through the API are never local. The check is on the credential, never on the caller's
/// address, and is made on every request, so a key that loses its Local mark stops counting at once.
/// </summary>
internal static class RoofLocalCredential
{
    public static bool IsLocal(ClaimsPrincipal? user, RoofApiKeyStore keys)
    {
        if (user?.Identity?.IsAuthenticated == true && user.HasClaim(RoofPrincipalFactory.LocalClaimType, "true")
            && user.HasClaim(RoofPrincipalFactory.ScopeClaimType, "configure")) return true;
        switch (RoofPrincipalFactory.GetCredentialKind(user))
        {
            case RoofCredentialKind.ApiKey:
                return keys.TryFindByKeyId(user!.FindFirst(RoofPrincipalFactory.KeyIdClaimType)?.Value, out var key)
                    && key is { Source: RoofApiKeySource.Configuration, Local: true };
            default:
                return false;
        }
    }
}
