using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

public sealed partial class RoofIdentityStore
{
    public bool ProviderEnabled => _options.CurrentValue.LinuxAuth.Enabled && IsAvailable && _document.SchemaVersion == 2;

    // Startup only, before snapshots become visible. No migration is performed while disabled.
    private void MigrateProviderIdentity()
    {
        if (_document.SchemaVersion == 2) return;
        if (_file is not null && File.Exists(_file.Path))
        {
            var backup = _file.Path + ".schema1.bak";
            try
            {
                // Never replace the rollback baseline.
                using var input = File.OpenRead(_file.Path);
                var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var output = new FileStream(backup, fileOptions);
                input.CopyTo(output);
                output.Flush(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new RoofIdentityStoreException("Provider migration could not preserve the schema 1 rollback copy.", ex); }
        }
        var migrated = _document.Clone();
        migrated.SchemaVersion = 2;
        migrated.Users = migrated.Users.Select(user => user with { PrincipalId = Guid.NewGuid().ToString("N"), AuthenticationEpoch = user.Stamp }).ToList();
        migrated.Revision++;
        _file?.Save(migrated);
        _document = migrated;
    }

    public IReadOnlyList<RoofPrincipalResponse> ProviderPrincipals
    {
        get
        {
            lock (_gate)
                return _document.Users.Select(user => new RoofPrincipalResponse(user.PrincipalId ?? "", "virtual", user.PrincipalId ?? "", user.Name, user.Role, true))
                    .Concat(_document.LinuxPrincipals.Select(LinuxResponse)).ToArray();
        }
    }

    private static RoofPrincipalResponse LinuxResponse(StoredLinuxPrincipal principal)
        => new(principal.Id, "linux", principal.Subject, principal.Name, principal.Role, principal.Enabled);

    public RoofIdentityResult<RoofPrincipalResponse> EnrollLinux(RoofPrincipalEnrollRequest request)
        => Mutate<RoofPrincipalResponse>((document, now) =>
        {
            if (!ProviderEnabled || request.Provider != "linux" || !RoofIdentityContract.IsValidName(request.Name)
                || !Guid.TryParse(request.Subject, out _) || !Guid.TryParse(request.EnrollmentStamp, out _)
                || RoofPrincipalFactory.NormalizeRole(request.Role) != request.Role)
                return RoofIdentityResult<RoofPrincipalResponse>.Failure(RoofControllerErrorCode.InvalidRequest, "Invalid or disabled provider enrollment.");
            var existing = document.LinuxPrincipals.Find(candidate => candidate.Subject == request.Subject || candidate.Name == request.Name);
            if (existing is not null)
                return existing.Enabled && existing.Subject == request.Subject && existing.Name == request.Name && existing.EnrollmentStamp == request.EnrollmentStamp && existing.Role == request.Role
                    ? RoofIdentityResult<RoofPrincipalResponse>.Success(LinuxResponse(existing))
                    : RoofIdentityResult<RoofPrincipalResponse>.Failure(RoofControllerErrorCode.IdentityNameConflict, "Enrollment already exists; change it explicitly.");
            var principal = new StoredLinuxPrincipal(Guid.NewGuid().ToString("N"), request.Subject, request.Name, request.Role, request.EnrollmentStamp, NewStamp(), true);
            document.LinuxPrincipals.Add(principal);
            return RoofIdentityResult<RoofPrincipalResponse>.Success(LinuxResponse(principal));
        });

    public RoofIdentityResult<RoofPrincipalResponse> UpdateLinux(string id, RoofPrincipalUpdateRequest request)
        => Mutate<RoofPrincipalResponse>((document, now) =>
        {
            var index = document.LinuxPrincipals.FindIndex(candidate => candidate.Id == id);
            if (!ProviderEnabled || index < 0)
                return RoofIdentityResult<RoofPrincipalResponse>.Failure(RoofControllerErrorCode.IdentityNotFound, "Unknown principal.");
            if (RoofPrincipalFactory.NormalizeRole(request.Role) != request.Role)
                return RoofIdentityResult<RoofPrincipalResponse>.Failure(RoofControllerErrorCode.InvalidRequest, "Invalid role.");
            var before = document.LinuxPrincipals[index];
            var after = before with { Role = request.Role, Enabled = request.Enabled, Stamp = before.Role != request.Role || before.Enabled != request.Enabled ? NewStamp() : before.Stamp };
            document.LinuxPrincipals[index] = after;
            if (after.Stamp != before.Stamp) document.Sessions.RemoveAll(session => session.PrincipalId == id);
            return RoofIdentityResult<RoofPrincipalResponse>.Success(LinuxResponse(after));
        });

    public StoredLinuxPrincipal? FindLinux(string subject)
    {
        lock (_gate) return ProviderEnabled ? _document.LinuxPrincipals.Find(principal => principal.Subject == subject && principal.Enabled) : null;
    }

    public StoredLinuxPrincipal? LinuxForPrincipal(string id)
    {
        lock (_gate) return ProviderEnabled ? _document.LinuxPrincipals.Find(principal => principal.Id == id && principal.Enabled) : null;
    }

    private (string Id, string Name, string Role, string Stamp, string Provider)? Principal(RoofIdentityDocument document, string id)
    {
        var user = document.Users.Find(candidate => candidate.PrincipalId == id);
        if (user is not null) return (id, user.Name, user.Role, user.AuthenticationEpoch ?? user.Stamp, "virtual");
        var linux = document.LinuxPrincipals.Find(candidate => candidate.Id == id && candidate.Enabled);
        return linux is null ? null : (id, linux.Name, linux.Role, linux.Stamp, "linux");
    }

    private bool ProviderSessionIsCurrent(StoredSession session)
    {
        if (!ProviderEnabled) return false;
        lock (_gate)
        {
            var principal = Principal(_document, session.PrincipalId!);
            return principal is not null && principal.Value.Stamp == session.PrincipalStamp
                && principal.Value.Role == session.Role && principal.Value.Provider == session.Provider;
        }
    }

    public RoofIdentityResult<RoofIssuedSession> CreateProviderSession(
        string principalId, string expectedStamp, string? scope = null, StoredAssertionNonce? assertion = null)
        => Mutate<RoofIssuedSession>((document, now) =>
        {
            var principal = Principal(document, principalId);
            if (!ProviderEnabled || principal is null || principal.Value.Stamp != expectedStamp
                || (scope is not null && !RoofIdentityScopes.IsValid(scope)))
                return RoofIdentityResult<RoofIssuedSession>.Failure(RoofControllerErrorCode.SignInFailed, "Provider authentication failed.");
            if (assertion is not null)
            {
                document.AssertionNonces.RemoveAll(nonce => nonce.ExpiresUtc <= now);
                if (assertion.ExpiresUtc <= now || document.AssertionNonces.Exists(nonce => nonce.Nonce == assertion.Nonce))
                    return RoofIdentityResult<RoofIssuedSession>.Failure(RoofControllerErrorCode.SignInFailed, "Provider authentication failed.");
                document.AssertionNonces.Add(assertion);
            }
            var token = TokenPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var expires = scope is null ? now + _options.CurrentValue.SessionLifetime : assertion!.ExpiresUtc;
            var session = new StoredSession(Guid.NewGuid().ToString("N"), HashHex(token), principal.Value.Name, principal.Value.Role,
                RoofCredentialKind.Session, null, null, now, expires)
            { PrincipalId = principalId, PrincipalStamp = expectedStamp, Provider = principal.Value.Provider, Scope = scope };
            document.Sessions.Add(session);
            return RoofIdentityResult<RoofIssuedSession>.Success(new RoofIssuedSession(token, session));
        });

    public IReadOnlyList<StoredOwnedKey> OwnedKeysFor(string ownerId)
    {
        lock (_gate) return _document.OwnedKeys.Where(key => key.OwnerId == ownerId).Select(key => key with { Scopes = key.Scopes.ToArray() }).ToArray();
    }

    public RoofIdentityResult<StoredOwnedKey> CreateOwnedKey(string ownerId, string expectedStamp, RoofOwnedKeyCreateRequest request, string hash)
        => Mutate<StoredOwnedKey>((document, now) =>
        {
            var principal = Principal(document, ownerId);
            if (!ProviderEnabled || principal is null || principal.Value.Stamp != expectedStamp)
                return RoofIdentityResult<StoredOwnedKey>.Failure(RoofControllerErrorCode.SignInFailed, "Owner changed.");
            if (!RoofIdentityContract.IsValidName(request.Name) || request.Scopes is not { Length: > 0 and <= 4 }
                || request.Scopes.Distinct(StringComparer.Ordinal).Count() != request.Scopes.Length
                || request.Scopes.Any(scope => scope == RoofIdentityScopes.Identity || !ScopeAllowed(principal.Value.Role, scope))
                || request.ExpiresUtc <= now || request.ExpiresUtc > now.AddDays(366))
                return RoofIdentityResult<StoredOwnedKey>.Failure(RoofControllerErrorCode.InvalidRequest, "Invalid name, owner scope or expiry (maximum 366 days).");
            var key = new StoredOwnedKey(Guid.NewGuid().ToString("N"), request.Name, ownerId, expectedStamp, request.Scopes.ToArray(), hash, request.ExpiresUtc, false);
            document.OwnedKeys.Add(key);
            return RoofIdentityResult<StoredOwnedKey>.Success(key);
        });

    public RoofIdentityResult<StoredOwnedKey> RevokeOwnedKey(string ownerId, string id)
        => Mutate<StoredOwnedKey>((document, _) =>
        {
            var index = document.OwnedKeys.FindIndex(key => key.Id == id && key.OwnerId == ownerId);
            if (!ProviderEnabled || index < 0) return RoofIdentityResult<StoredOwnedKey>.Failure(RoofControllerErrorCode.IdentityNotFound, "Unknown key.");
            document.OwnedKeys[index] = document.OwnedKeys[index] with { Revoked = true };
            return RoofIdentityResult<StoredOwnedKey>.Success(document.OwnedKeys[index]);
        });

    public bool TryOwnedKey(string? secret, out StoredOwnedKey? key, out string? name, out string? role)
    {
        key = null; name = null; role = null;
        if (!ProviderEnabled || secret is not { Length: > 0 and <= MaximumTokenLength }) return false;
        var hash = HashHex(secret);
        lock (_gate)
        {
            key = _document.OwnedKeys.Find(candidate => candidate.KeySha256 == hash);
            return CheckOwnedKey(key, out name, out role);
        }
    }

    public bool OwnedKeyIsCurrent(string id)
    {
        lock (_gate) return ProviderEnabled && CheckOwnedKey(_document.OwnedKeys.Find(key => key.Id == id), out _, out _);
    }

    private bool CheckOwnedKey(StoredOwnedKey? key, out string? name, out string? role)
    {
        name = null; role = null;
        if (key is null || key.Revoked || key.ExpiresUtc <= _time.GetUtcNow()) return false;
        var principal = Principal(_document, key.OwnerId);
        if (principal is null || principal.Value.Stamp != key.OwnerStamp || key.Scopes.Any(scope => !ScopeAllowed(principal.Value.Role, scope))) return false;
        name = principal.Value.Name; role = principal.Value.Role; return true;
    }

    public static bool ScopeAllowed(string role, string scope) => scope switch
    {
        RoofIdentityScopes.Read => RoofPrincipalFactory.NormalizeRole(role) is not null,
        RoofIdentityScopes.Operate => role is RoofControllerApiContract.OperatorRole or RoofControllerApiContract.AdminRole,
        RoofIdentityScopes.Configure or RoofIdentityScopes.Identity => role == RoofControllerApiContract.AdminRole,
        _ => false
    };
}
