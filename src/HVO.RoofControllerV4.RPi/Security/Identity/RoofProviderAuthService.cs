using System;
using System.Linq;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>Authenticates providers and independently checks application enrollment, role and epoch.</summary>
public sealed class RoofProviderAuthService(RoofIdentityStore identity, RoofSignInService legacy, RoofNativeAuthClient native, RoofSignInLockout lockout)
{
    public bool Enabled => identity.ProviderEnabled;

    public async Task<RoofIdentityResult<RoofIssuedSession>> PasswordAsync(RoofProviderSignInRequest request, string remote, CancellationToken cancellationToken)
    {
        if (!Enabled || !RoofIdentityContract.IsValidName(request.Name) || request.Password is not { Length: > 0 and <= RoofIdentityContract.MaximumPasswordLength }) return Failed();
        if (request.Provider == "virtual")
        {
            // Retain existing lockout, hash upgrades and password semantics, then exchange its fresh proof atomically.
            var checkedPassword = await legacy.SignInWithPasswordAsync(request.Name, request.Password, remote, cancellationToken).ConfigureAwait(false);
            if (!checkedPassword.Succeeded) return RoofIdentityResult<RoofIssuedSession>.Failure(checkedPassword.Error!.Value, checkedPassword.Detail!);
            return RoofIdentityResult<RoofIssuedSession>.Success(checkedPassword.Issued!);
        }
        if (request.Provider != "linux") return Failed();
        var key = "linux:" + request.Name;
        var known = identity.ProviderPrincipals.Any(principal => principal.Provider == "linux" && principal.Name == request.Name);
        if (lockout.TryBeginAttempt(key, known, out _) != RoofSignInAdmission.Admitted)
            return RoofIdentityResult<RoofIssuedSession>.Failure(RoofControllerErrorCode.SignInLockedOut, "Sign-in temporarily locked.");
        var outcome = RoofSignInOutcome.Failed;
        try
        {
            var assertion = await native.PasswordAsync(request.Name, request.Password, cancellationToken).ConfigureAwait(false);
            var result = Exchange(assertion, "linux-password");
            if (result.Succeeded) outcome = RoofSignInOutcome.Succeeded;
            return result;
        }
        finally { lockout.EndAttempt(key, outcome); }
    }

    public RoofIdentityResult<RoofIssuedSession> Exchange(string? assertion, string purpose)
    {
        var claims = native.Verify(assertion, purpose);
        if (!Enabled || claims is null) return Failed();
        var principal = identity.FindLinux(claims.Subject);
        if (principal is null || principal.Name != claims.UserName || principal.EnrollmentStamp != claims.EnrollmentStamp || !native.IsCurrent(principal, purpose)) return Failed();
        var scope = purpose == "local-admin" ? claims.Operation : null;
        if (scope is not null && !RoofIdentityStore.ScopeAllowed(principal.Role, scope))
            return RoofIdentityResult<RoofIssuedSession>.Failure(RoofControllerErrorCode.IdentityReadOnly, "Controller role does not allow the requested operation.");
        // The account's full name for display (#209), refreshed at each sign-in; an older or busy worker leaves the last one.
        return identity.CreateProviderSession(principal.Id, principal.Stamp, scope,
            new StoredAssertionNonce(claims.Nonce, DateTimeOffset.FromUnixTimeSeconds(claims.ExpiresAt)), native.GetFullName(principal));
    }

    public bool PrincipalIsCurrent(string? id, bool local = false)
    {
        if (id is null) return true;
        if (!Enabled) return false;
        var linux = identity.LinuxForPrincipal(id);
        return linux is null ? identity.ProviderPrincipals.Any(principal => principal.Id == id && principal.Provider == "virtual") : native.IsCurrent(linux, local ? "local-admin" : "linux-password");
    }

    /// <summary>Confirmed native revocation permanently retires kiosk access; transport failure only refuses it.</summary>
    public bool KioskPrincipalIsCurrent(string id)
    {
        if (!Enabled) return false;
        var linux = identity.LinuxForPrincipal(id);
        if (linux is null) return PrincipalIsCurrent(id);
        var current = native.CheckCurrent(linux);
        if (current == false) identity.RetireKioskCredential(id);
        return current == true;
    }

    private static RoofIdentityResult<RoofIssuedSession> Failed()
        => RoofIdentityResult<RoofIssuedSession>.Failure(RoofControllerErrorCode.SignInFailed, "Provider authentication failed.");
}
