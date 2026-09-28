using System;
using System.Threading;
using System.Threading.Tasks;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>The outcome of a sign-in or password change: the new session (if any), or an error code and when to retry.</summary>
public sealed record RoofSignInResult(
    RoofIssuedSession? Issued,
    RoofControllerErrorCode? Error,
    string? Detail,
    TimeSpan? RetryAfter)
{
    public bool Succeeded => Error is null;

    public static RoofSignInResult Success(RoofIssuedSession? issued) => new(issued, null, null, null);

    public static RoofSignInResult Failure(RoofControllerErrorCode error, string detail, TimeSpan? retryAfter = null)
        => new(null, error, detail, retryAfter);
}

/// <summary>
/// Signs people in by name and password or, at a kiosk, by name and PIN, and lets a signed-in person change their own
/// password. Every answer to a wrong name, password or PIN is the same, and an unknown name takes as long to refuse as
/// a wrong password. Repeated failures lock the name (or, for PINs, the kiosk) out (<see cref="RoofSignInLockout"/>).
/// </summary>
public sealed class RoofSignInService
{
    internal const string WrongCredentials = "The name, password or PIN is not correct.";

    private readonly RoofIdentityStore _identity;
    private readonly RoofSecretHasher _hasher;
    private readonly RoofSignInLockout _lockout;
    private readonly ILogger<RoofSignInService> _logger;

    public RoofSignInService(
        RoofIdentityStore identity,
        RoofSecretHasher hasher,
        RoofSignInLockout lockout,
        ILogger<RoofSignInService> logger)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _lockout = lockout ?? throw new ArgumentNullException(nameof(lockout));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Signs in with a name and password (web UI, CLI).</summary>
    public Task<RoofSignInResult> SignInWithPasswordAsync(string name, string password, string remote, CancellationToken cancellationToken)
        => SignInAsync(
            name,
            password,
            RoofSignInLockout.ForName(name),
            user => user.PasswordHash,
            RoofCredentialKind.Session,
            kiosk: null,
            remote,
            cancellationToken);

    /// <summary>Signs in with a name and PIN at the kiosk that holds <paramref name="kiosk"/>.</summary>
    public Task<RoofSignInResult> SignInWithPinAsync(
        RoofApiKeyIdentity kiosk,
        string name,
        string pin,
        string remote,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kiosk);
        return SignInAsync(
            name,
            pin,
            RoofSignInLockout.ForDevice(kiosk.KeyId),
            user => user.Role == RoofControllerApiContract.ViewerRole ? null : user.PinHash,
            RoofCredentialKind.Pin,
            kiosk,
            remote,
            cancellationToken);
    }

    /// <summary>
    /// A signed-in person changes their own password. Their other sessions end; <paramref name="keepSessionId"/> stays open.
    /// </summary>
    public async Task<RoofSignInResult> ChangePasswordAsync(
        string name,
        string keepSessionId,
        string currentPassword,
        string newPassword,
        string remote,
        CancellationToken cancellationToken)
    {
        if (!_identity.IsAvailable)
        {
            return Unavailable();
        }

        var lockKey = RoofSignInLockout.ForName(name);
        if (_lockout.IsLockedOut(lockKey, out var retryAfter))
        {
            return LockedOut(retryAfter);
        }

        _identity.TryGetUser(name, out var user);
        var oldHash = user?.PasswordHash;
        var stamp = user?.Stamp;
        var check = await _hasher.VerifyAsync(oldHash, currentPassword, cancellationToken).ConfigureAwait(false);
        switch (check)
        {
            case RoofSecretCheck.Busy:
                return Busy();
            case RoofSecretCheck.Failed:
                return Failed(lockKey, name, "password change", remote, "The current password is not correct.");
        }

        _lockout.RecordSuccess(lockKey);
        var newHash = await _hasher.HashAsync(newPassword, cancellationToken).ConfigureAwait(false);
        if (newHash is null)
        {
            return Busy();
        }

        try
        {
            var changed = _identity.ChangeOwnPassword(user!.Name, stamp!, newHash, keepSessionId);
            if (!changed.Succeeded)
            {
                return RoofSignInResult.Failure(changed.Error!.Value, changed.Detail!);
            }

            _logger.LogWarning(
                "AUDIT {Name} changed their own password from {Remote}; {Ended} other session(s) ended.",
                user.Name,
                remote,
                changed.Value!.SessionsEnded);
            return RoofSignInResult.Success(null);
        }
        catch (RoofIdentityStoreException ex)
        {
            _logger.LogError("Password change for {Name} could not be saved: {Reason}", user!.Name, ex.Message);
            return Unavailable();
        }
    }

    private async Task<RoofSignInResult> SignInAsync(
        string name,
        string secret,
        string lockKey,
        Func<StoredUser, string?> selectHash,
        RoofCredentialKind kind,
        RoofApiKeyIdentity? kiosk,
        string remote,
        CancellationToken cancellationToken)
    {
        if (!_identity.IsAvailable)
        {
            return Unavailable();
        }

        var what = kind == RoofCredentialKind.Pin ? $"PIN sign-in at {kiosk!.Name}" : "sign-in";
        if (_lockout.IsLockedOut(lockKey, out var retryAfter))
        {
            _logger.LogWarning(
                "SECURITY {What} for {Name} from {Remote} refused: locked out for another {RetryAfter}.",
                what,
                name,
                remote,
                retryAfter);
            return LockedOut(retryAfter);
        }

        // An unknown name, or a person without this kind of secret, is checked against a dummy hash (same time, same answer).
        _identity.TryGetUser(name, out var user);
        var hash = user is null ? null : selectHash(user);
        var stamp = user?.Stamp;
        var check = await _hasher.VerifyAsync(hash, secret, cancellationToken).ConfigureAwait(false);
        switch (check)
        {
            case RoofSecretCheck.Busy:
                _logger.LogWarning("{What} for {Name} from {Remote} refused: too many sign-ins are being checked at once.", what, name, remote);
                return Busy();
            case RoofSecretCheck.Failed:
                return Failed(lockKey, name, what, remote, WrongCredentials);
        }

        _lockout.RecordSuccess(lockKey);
        if (check == RoofSecretCheck.SucceededRehashNeeded)
        {
            var upgraded = await _hasher.HashAsync(secret, cancellationToken).ConfigureAwait(false);
            if (upgraded is not null)
            {
                _identity.UpgradeHash(user!.Name, hash!, upgraded, pin: kind == RoofCredentialKind.Pin);
            }
        }

        try
        {
            var created = _identity.CreateSession(user!.Name, stamp!, kind, kiosk?.Name, kiosk?.KeyId);
            if (!created.Succeeded)
            {
                // The password, PIN or role changed while the secret was being checked.
                return RoofSignInResult.Failure(RoofControllerErrorCode.SignInFailed, WrongCredentials);
            }

            _logger.LogInformation(
                "AUDIT {What}: {Name} ({Role}) from {Remote}, session {SessionId}.",
                what,
                user.Name,
                user.Role,
                remote,
                created.Value!.Session.Id);
            return RoofSignInResult.Success(created.Value);
        }
        catch (RoofIdentityStoreException ex)
        {
            _logger.LogError("{What} for {Name} could not be saved: {Reason}", what, user!.Name, ex.Message);
            return Unavailable();
        }
    }

    private RoofSignInResult Failed(string lockKey, string name, string what, string remote, string detail)
    {
        var lockedFor = _lockout.RecordFailure(lockKey);
        if (lockedFor is { } duration)
        {
            _logger.LogWarning(
                "SECURITY {What} for {Name} from {Remote} failed; too many failures, locked out for {Duration}.",
                what,
                name,
                remote,
                duration);
            return LockedOut(duration);
        }

        _logger.LogWarning("SECURITY {What} for {Name} from {Remote} failed.", what, name, remote);
        return RoofSignInResult.Failure(RoofControllerErrorCode.SignInFailed, detail);
    }

    private static RoofSignInResult LockedOut(TimeSpan retryAfter)
        => RoofSignInResult.Failure(
            RoofControllerErrorCode.SignInLockedOut,
            "Too many failed attempts: sign-in is locked for a while (see Retry-After). Stop still works.",
            retryAfter);

    private static RoofSignInResult Busy()
        => RoofSignInResult.Failure(
            RoofControllerErrorCode.SignInBusy,
            "Too many sign-ins are being checked at once. Retry in a few seconds.",
            TimeSpan.FromSeconds(2));

    private static RoofSignInResult Unavailable()
        => RoofSignInResult.Failure(
            RoofControllerErrorCode.IdentityStoreUnavailable,
            "The identity store is unavailable, so sign-in is refused; see the controller log. API keys still work.",
            TimeSpan.FromSeconds(30));
}
