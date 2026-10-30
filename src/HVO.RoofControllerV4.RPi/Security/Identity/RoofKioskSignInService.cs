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

    /// <summary>
    /// The owner of the commissioned kiosk PIN changes it from the PIN session <paramref name="keepSessionId"/> (#205).
    /// The current PIN is checked as a sign-in is, against the same durable count of failures; the new one is hashed
    /// first, so a busy hasher never leaves a verified attempt counted as a failure.
    /// </summary>
    public async Task<RoofIdentityResult<int>> ChangePinAsync(string principalId, string deviceId, string keepSessionId,
        string currentPin, string newPin, CancellationToken cancellationToken)
    {
        if (!await _attempt.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return RoofIdentityResult<int>.Failure(RoofControllerErrorCode.SignInBusy, "Another PIN is being checked.");
        try
        {
            var newHash = await hasher.HashAsync(newPin, cancellationToken).ConfigureAwait(false);
            if (newHash is null) return RoofIdentityResult<int>.Failure(RoofControllerErrorCode.SignInBusy, "PIN verification is busy.");
            var reserved = identity.BeginKioskAttempt(principalId);
            if (!reserved.Succeeded)
                return RoofIdentityResult<int>.Failure(reserved.Error!.Value,
                    reserved.Error == RoofControllerErrorCode.SignInFailed ? WrongCurrentPin : reserved.Detail!);
            var c = reserved.Value!;
            if (c.State != "complete") return RoofIdentityResult<int>.Failure(RoofControllerErrorCode.SignInFailed, WrongCurrentPin);
            var check = await hasher.VerifyAsync(c.PinHash, currentPin, cancellationToken).ConfigureAwait(false);
            if (check == RoofSecretCheck.Busy) return RoofIdentityResult<int>.Failure(RoofControllerErrorCode.SignInBusy, "PIN verification is busy.");
            if (check is not (RoofSecretCheck.Succeeded or RoofSecretCheck.SucceededRehashNeeded) || !providers.KioskPrincipalIsCurrent(c.PrincipalId))
                return RoofIdentityResult<int>.Failure(RoofControllerErrorCode.SignInFailed, WrongCurrentPin);
            return identity.ChangeKioskPin(c, deviceId, newHash, keepSessionId);
        }
        finally { _attempt.Release(); }
    }

    internal const string WrongCurrentPin = "The current PIN is not correct.";
}
