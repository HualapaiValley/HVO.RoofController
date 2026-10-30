using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>A separate kiosk credential bound to a human; never a Linux password.</summary>
public sealed record StoredKioskCredential(string PrincipalId, string OwnerStamp, string Stamp, string State,
    string? PinHash, DateTimeOffset CreatedUtc, DateTimeOffset ExpiresUtc, int Failures = 0, DateTimeOffset? LockedUntilUtc = null);

public sealed partial class RoofIdentityStore
{
    private readonly Dictionary<string, SetupGrant> _setupGrants = new(StringComparer.Ordinal);
    private sealed record SetupGrant(string PrincipalId, string Stamp, string DeviceId, DateTimeOffset ExpiresUtc);

    private void ReconcileKioskCredential(RoofIdentityDocument document, DateTimeOffset now)
    {
        if (document.KioskCredential is not { State: not "retired" } credential) return;
        var owner = Principal(document, credential.PrincipalId);
        if (owner is null || owner.Value.Stamp != credential.OwnerStamp || owner.Value.Role == RoofControllerApiContract.ViewerRole
            || (credential.State == "pending" && (_file?.KioskRetired == true || now >= credential.ExpiresUtc || document.Users.Count + document.LinuxPrincipals.Count != 1)))
        {
            document.KioskCredential = credential with { State = "retired", PinHash = null, Stamp = NewStamp() };
            document.Sessions.RemoveAll(s => s.Kind == RoofCredentialKind.Pin && s.PrincipalId == credential.PrincipalId);
        }
    }

    /// <summary>Reconciles retirement before exposing access; never recreates a missing credential.</summary>
    public StoredKioskCredential? GetKioskCredential()
    {
        lock (_gate)
        {
            if (!IsAvailable) return null;
            var copy = _document.Clone();
            ReconcileKioskCredential(copy, _time.GetUtcNow());
            if (copy.KioskCredential != _document.KioskCredential)
                Mutate<bool>((d, _) => { d.KioskCredential = copy.KioskCredential; return RoofIdentityResult<bool>.Success(true); });
            return _document.KioskCredential is { State: not "retired" } c ? c : null;
        }
    }

    /// <summary>Retire a positively revoked owner; never transfers access to a later namesake.</summary>
    public void RetireKioskCredential(string id)
        => Mutate<bool>((d, _) =>
        {
            if (d.KioskCredential is { } c && c.PrincipalId == id && c.State != "retired")
            {
                d.KioskCredential = c with { State = "retired", PinHash = null, Stamp = NewStamp() };
                d.Sessions.RemoveAll(s => s.Kind == RoofCredentialKind.Pin && s.PrincipalId == id);
            }
            return RoofIdentityResult<bool>.Success(true);
        });

    /// <summary>Reserve a guess durably before checking it, so crash/restart cannot erase failures.</summary>
    public RoofIdentityResult<StoredKioskCredential> BeginKioskAttempt(string principalId)
    {
        var expected = GetKioskCredential();
        if (expected is null || expected.PrincipalId != principalId)
            return RoofIdentityResult<StoredKioskCredential>.Failure(RoofControllerErrorCode.SignInFailed, "PIN sign-in failed.");
        return Mutate<StoredKioskCredential>((d, now) =>
        {
            var c = d.KioskCredential!;
            if (c.Stamp != expected.Stamp || c.State == "retired")
                return RoofIdentityResult<StoredKioskCredential>.Failure(RoofControllerErrorCode.SignInFailed, "PIN sign-in failed.");
            if (c.LockedUntilUtc > now)
                return RoofIdentityResult<StoredKioskCredential>.Failure(RoofControllerErrorCode.SignInLockedOut, "PIN sign-in is temporarily locked.");
            var failures = c.LockedUntilUtc is not null ? 1 : c.Failures + 1;
            d.KioskCredential = c with { Failures = failures, LockedUntilUtc = failures >= 5 ? now.AddMinutes(5) : null };
            return RoofIdentityResult<StoredKioskCredential>.Success(d.KioskCredential);
        });
    }

    private bool KioskDeviceIsCurrent(RoofIdentityDocument document, string deviceId)
        => RoofApiKeyStore.Inspect(_securityOptions.CurrentValue).Keys.Any(key => key.KeyId == deviceId && key.Kiosk)
            || document.ApiKeys.Any(key => key.Kiosk && key.Role == RoofControllerApiContract.ViewerRole
                && RoofApiKeyStore.ComputeKeyId(Convert.FromHexString(key.KeySha256)) == deviceId);

    /// <summary>Issue isolated setup access or a normal PIN session after verification.</summary>
    public RoofIdentityResult<RoofKioskPinResponse> FinishKioskAttempt(StoredKioskCredential expected, string device, string deviceId)
    {
        lock (_gate)
        {
            GetKioskCredential();
            var token = "hvo_setup_" + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var result = Mutate<RoofKioskPinResponse>((d, now) =>
            {
                var c = d.KioskCredential; var owner = Principal(d, expected.PrincipalId);
                if (c is null || c.Stamp != expected.Stamp || c.State != expected.State || owner is null || !KioskDeviceIsCurrent(d, deviceId))
                    return RoofIdentityResult<RoofKioskPinResponse>.Failure(RoofControllerErrorCode.SignInFailed, "PIN sign-in failed.");
                d.KioskCredential = c with { Failures = 0, LockedUntilUtc = null };
                if (c.State == "pending")
                    return RoofIdentityResult<RoofKioskPinResponse>.Success(new(null, token, now.AddMinutes(5)));
                var sessionToken = TokenPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                var session = new StoredSession(Guid.NewGuid().ToString("N"), HashHex(sessionToken), owner.Value.Name,
                    owner.Value.Role, RoofCredentialKind.Pin, device, deviceId, now, now + _options.CurrentValue.PinSessionLifetime)
                    { PrincipalId = owner.Value.Id, PrincipalStamp = owner.Value.Stamp, Provider = owner.Value.Provider };
                d.Sessions.Add(session); _lastActivity[session.Id] = now.UtcTicks;
                RecordSignIn(d, owner.Value.Id, now);
                return RoofIdentityResult<RoofKioskPinResponse>.Success(new(new RoofSessionResponse(sessionToken, session.Id,
                    session.UserName, RoofControllerApiContract.OperatorRole, RoofCredentialKind.Pin, session.ExpiresUtc,
                    _options.CurrentValue.PinSessionIdleTimeout.TotalSeconds), null, null));
            });
            if (result.Succeeded && result.Value!.SetupToken is not null)
            {
                foreach (var key in _setupGrants.Where(p => p.Value.ExpiresUtc <= _time.GetUtcNow()).Select(p => p.Key).ToArray()) _setupGrants.Remove(key);
                if (_setupGrants.Count >= 16) _setupGrants.Clear();
                _setupGrants[HashHex(token)] = new(expected.PrincipalId, expected.Stamp, deviceId, result.Value.SetupExpiresUtc!.Value);
            }
            return result;
        }
    }

    /// <summary>Atomically replace pending access, retire the default and require fresh personal-PIN login.</summary>
    public RoofIdentityResult<bool> CompleteKioskSetup(string token, string deviceId, string newHash)
    {
        lock (_gate)
        {
            GetKioskCredential();
            if (token.Length > MaximumTokenLength || !_setupGrants.TryGetValue(HashHex(token), out var grant)
                || grant.DeviceId != deviceId || grant.ExpiresUtc <= _time.GetUtcNow())
                return RoofIdentityResult<bool>.Failure(RoofControllerErrorCode.SignInFailed, "Setup access expired or ended.");
            var result = Mutate<bool>((d, _) =>
            {
                var c = d.KioskCredential;
                if (c is not { State: "pending" } || c.Stamp != grant.Stamp || c.PrincipalId != grant.PrincipalId || !KioskDeviceIsCurrent(d, deviceId))
                    return RoofIdentityResult<bool>.Failure(RoofControllerErrorCode.SignInFailed, "Setup access expired or ended.");
                d.KioskCredential = c with { State = "complete", PinHash = newHash, Stamp = NewStamp(), Failures = 0, LockedUntilUtc = null };
                d.Sessions.RemoveAll(s => s.Kind == RoofCredentialKind.Pin && s.PrincipalId == grant.PrincipalId);
                return RoofIdentityResult<bool>.Success(true);
            });
            if (result.Succeeded) _setupGrants.Clear();
            return result;
        }
    }
}
