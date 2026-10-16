using System.Threading;
using System.Threading.Tasks;
using HVO.RoofControllerV4.Common.Models;
namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>Serial, durably counted PIN checks with independent native account revalidation.</summary>
public sealed class RoofKioskSignInService(RoofIdentityStore identity, RoofSecretHasher hasher, RoofProviderAuthService providers)
{
    private readonly SemaphoreSlim _attempt = new(1, 1);
    public async Task<RoofIdentityResult<RoofKioskPinResponse>> SignInAsync(RoofApiKeyIdentity kiosk,
        RoofKioskPinRequest request, CancellationToken cancellationToken)
    {
        if (!await _attempt.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return RoofIdentityResult<RoofKioskPinResponse>.Failure(RoofControllerErrorCode.SignInBusy, "Another PIN is being checked.");
        try
        {
            var reserved = identity.BeginKioskAttempt(request.PrincipalId);
            if (!reserved.Succeeded) return RoofIdentityResult<RoofKioskPinResponse>.Failure(reserved.Error!.Value, reserved.Detail!);
            var c = reserved.Value!;
            var hash = c.State == "pending" ? await hasher.HashAsync("0000", cancellationToken).ConfigureAwait(false) : c.PinHash;
            if (hash is null) return RoofIdentityResult<RoofKioskPinResponse>.Failure(RoofControllerErrorCode.SignInBusy, "PIN verification is busy.");
            var check = await hasher.VerifyAsync(hash, request.Pin, cancellationToken).ConfigureAwait(false);
            if (check is not (RoofSecretCheck.Succeeded or RoofSecretCheck.SucceededRehashNeeded) || !providers.KioskPrincipalIsCurrent(c.PrincipalId))
                return RoofIdentityResult<RoofKioskPinResponse>.Failure(RoofControllerErrorCode.SignInFailed, "PIN sign-in failed.");
            return identity.FinishKioskAttempt(c, kiosk.Name, kiosk.KeyId);
        }
        finally { _attempt.Release(); }
    }
}
