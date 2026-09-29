using System.Security.Claims;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;

namespace HVO.RoofControllerV4.RPi.Settings;

/// <summary>
/// Whether a caller holds a local credential, which local-only settings need: a configured API key marked
/// <see cref="RoofApiKeyOptions.Local"/>, or a PIN session at a kiosk whose configured key is marked Local. A password
/// session and a key added through the API are never local. The check is on the credential, never on the caller's
/// address, and is made on every request, so a key that loses its Local mark stops counting at once.
/// </summary>
internal static class RoofLocalCredential
{
    public static bool IsLocal(ClaimsPrincipal? user, RoofApiKeyStore keys)
    {
        switch (RoofPrincipalFactory.GetCredentialKind(user))
        {
            case RoofCredentialKind.ApiKey:
                return keys.TryFindByKeyId(user!.FindFirst(RoofPrincipalFactory.KeyIdClaimType)?.Value, out var key)
                    && key is { Source: RoofApiKeySource.Configuration, Local: true };
            case RoofCredentialKind.Pin:
                return keys.TryFindByKeyId(user!.FindFirst(RoofPrincipalFactory.DeviceKeyIdClaimType)?.Value, out var device)
                    && device is { Source: RoofApiKeySource.Configuration, Kiosk: true, Local: true };
            default:
                return false;
        }
    }
}
