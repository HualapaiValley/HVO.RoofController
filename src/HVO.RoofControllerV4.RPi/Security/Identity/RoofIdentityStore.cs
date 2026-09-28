using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security.Identity;

/// <summary>The outcome of a change to the identity store: success, or the error code and a message without secrets.</summary>
public sealed record RoofIdentityResult<T>(T? Value, RoofControllerErrorCode? Error, string? Detail)
{
    public bool Succeeded => Error is null;

    public static RoofIdentityResult<T> Success(T value) => new(value, null, null);

    public static RoofIdentityResult<T> Failure(RoofControllerErrorCode error, string detail) => new(default, error, detail);
}

/// <summary>A session that was just opened: the token (shown once) and what the store keeps.</summary>
public sealed record RoofIssuedSession(string Token, StoredSession Session);

/// <summary>What changing a person did.</summary>
public sealed record RoofUserChange(StoredUser User, int SessionsEnded);

/// <summary>
/// People, managed API keys and open sessions, kept in the identity store file (<see cref="RoofIdentityOptions.StorePath"/>)
/// or, without one, in memory. Reads are lock-free from an immutable snapshot. Every change is made on a copy, saved to
/// the file, and only then published, so memory never holds a change the file does not.
/// </summary>
/// <remarks>
/// When the file cannot be read, the store is unavailable: sign-in and management answer 503, managed keys and sessions
/// do not authenticate, and configured API keys (and so Stop and the roof itself) keep working.
/// </remarks>
public sealed class RoofIdentityStore
{
    /// <summary>A person's oldest sessions are ended beyond this many.</summary>
    internal const int MaximumSessionsPerUser = 20;

    /// <summary>The oldest sessions are ended beyond this many in all.</summary>
    internal const int MaximumSessions = 500;

    /// <summary>Every session token starts with this, so a leaked one is easy to recognise.</summary>
    internal const string TokenPrefix = "hvo_s_";

    /// <summary>Upper bound on a presented token, so a caller cannot make us hash arbitrarily large inputs.</summary>
    internal const int MaximumTokenLength = 128;

    /// <summary>Every generated API key starts with this.</summary>
    internal const string ApiKeyPrefix = "hvo_k_";

    private const string KioskRoleRequired = "A kiosk key must have the RoofViewer role: a PIN unlocks more.";

    private readonly object _gate = new();
    private readonly RoofIdentityFile? _file;
    private readonly TimeProvider _time;
    private readonly ILogger<RoofIdentityStore> _logger;
    private readonly IOptionsMonitor<RoofIdentityOptions> _options;
    private readonly IOptionsMonitor<RoofControllerSecurityOptions> _securityOptions;

    // Session id -> UTC ticks of its last request. PIN sessions end after the idle timeout; after a restart every PIN
    // session starts a fresh idle period.
    private readonly ConcurrentDictionary<string, long> _lastActivity = new(StringComparer.Ordinal);

    private RoofIdentityDocument _document;
    private volatile Snapshot _snapshot;

    public RoofIdentityStore(
        IOptionsMonitor<RoofIdentityOptions> options,
        IOptionsMonitor<RoofControllerSecurityOptions> securityOptions,
        TimeProvider time,
        ILogger<RoofIdentityStore> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _securityOptions = securityOptions ?? throw new ArgumentNullException(nameof(securityOptions));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var path = options.CurrentValue.StorePath;
        _document = new RoofIdentityDocument();
        if (string.IsNullOrWhiteSpace(path))
        {
            _logger.LogWarning(
                "No identity store is configured ({Section}:StorePath): people, sessions and managed API keys are kept in memory " +
                "and lost when the controller restarts.",
                RoofIdentityOptions.SectionName);
        }
        else
        {
            _file = new RoofIdentityFile(path);
            try
            {
                _document = _file.Load();
                _logger.LogInformation(
                    "Identity store {Path} loaded: {Users} people, {Keys} managed API keys, {Sessions} sessions.",
                    _file.Path,
                    _document.Users.Count,
                    _document.ApiKeys.Count,
                    _document.Sessions.Count);
                if (_file.IsReadableByOthers())
                {
                    _logger.LogWarning(
                        "The identity store {Path} can be read or written by other users; the next change saves it readable by the controller only.",
                        _file.Path);
                }
            }
            catch (RoofIdentityStoreException ex)
            {
                UnavailableReason = ex.Message;
                _logger.LogError(
                    "Identity store unavailable: {Reason} Sign-in and identity management are refused; configured API keys still work.",
                    ex.Message);
            }
        }

        var now = _time.GetUtcNow();
        foreach (var session in _document.Sessions.Where(session => session.Kind == RoofCredentialKind.Pin))
        {
            _lastActivity[session.Id] = now.UtcTicks;
        }

        _snapshot = Build(_document);
    }

    /// <summary>Raised after every saved change (outside the store's lock).</summary>
    public event Action? Changed;

    /// <summary>True when changes are saved to a file.</summary>
    public bool IsPersistent => _file is not null;

    /// <summary>The store file's full path, or null when the store is in memory.</summary>
    public string? StorePath => _file?.Path;

    /// <summary>Why the store is unavailable (it could not be read), or null when it is available.</summary>
    public string? UnavailableReason { get; }

    public bool IsAvailable => UnavailableReason is null;

    public IReadOnlyList<StoredUser> Users => _snapshot.Users;

    public IReadOnlyList<StoredApiKey> ManagedKeys => _snapshot.Keys;

    /// <summary>Sessions that have not ended.</summary>
    public IReadOnlyList<StoredSession> ActiveSessions
    {
        get
        {
            var now = _time.GetUtcNow();
            return _snapshot.Sessions.Where(session => IsLive(session, now, out _)).ToList();
        }
    }

    public bool TryGetUser(string? name, [NotNullWhen(true)] out StoredUser? user)
    {
        user = null;
        return name is not null && _snapshot.UsersByName.TryGetValue(name, out user);
    }

    /// <summary>
    /// Finds the live session for <paramref name="token"/>. With <paramref name="touch"/>, the request counts as activity
    /// (a PIN session's idle period starts again). <paramref name="failure"/> says why a token was refused, without the token.
    /// </summary>
    public bool TryValidateToken(string? token, bool touch, [NotNullWhen(true)] out StoredSession? session, out string? failure)
    {
        session = null;
        if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenLength)
        {
            failure = "malformed token";
            return false;
        }

        if (!_snapshot.SessionsByTokenHash.TryGetValue(HashHex(token), out var found))
        {
            failure = "unknown or ended session";
            return false;
        }

        return Validate(found, touch, out session, out failure);
    }

    /// <summary>True when the session <paramref name="sessionId"/> is still live. Does not count as activity.</summary>
    public bool TryGetLiveSession(string? sessionId, [NotNullWhen(true)] out StoredSession? session)
    {
        session = null;
        return sessionId is not null
            && _snapshot.SessionsById.TryGetValue(sessionId, out var found)
            && Validate(found, touch: false, out session, out _);
    }

    /// <summary>
    /// Opens a session for <paramref name="userName"/>, unless the person's password or role changed since
    /// <paramref name="expectedStamp"/> was read (the credential that was checked is then out of date).
    /// </summary>
    public RoofIdentityResult<RoofIssuedSession> CreateSession(
        string userName,
        string expectedStamp,
        RoofCredentialKind kind,
        string? device = null,
        string? deviceKeyId = null)
    {
        if (kind is not (RoofCredentialKind.Session or RoofCredentialKind.Pin))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var token = TokenPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return Mutate<RoofIssuedSession>((document, now) =>
        {
            var user = document.Users.Find(candidate => Same(candidate.Name, userName));
            if (user is null || !string.Equals(user.Stamp, expectedStamp, StringComparison.Ordinal))
            {
                return RoofIdentityResult<RoofIssuedSession>.Failure(
                    RoofControllerErrorCode.SignInFailed,
                    "The name, password or PIN is not correct.");
            }

            var options = _options.CurrentValue;
            var lifetime = kind == RoofCredentialKind.Pin ? options.PinSessionLifetime : options.SessionLifetime;
            var session = new StoredSession(
                Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                HashHex(token),
                user.Name,
                user.Role,
                kind,
                device,
                deviceKeyId,
                now,
                now + lifetime);
            document.Sessions.Add(session);
            _lastActivity[session.Id] = now.UtcTicks;
            return RoofIdentityResult<RoofIssuedSession>.Success(new RoofIssuedSession(token, session));
        });
    }

    /// <summary>Ends one session (sign-out, or an admin ending it).</summary>
    public RoofIdentityResult<StoredSession> EndSession(string sessionId)
        => Mutate<StoredSession>((document, _) =>
        {
            var index = document.Sessions.FindIndex(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));
            if (index < 0)
            {
                return RoofIdentityResult<StoredSession>.Failure(RoofControllerErrorCode.IdentityNotFound, "No open session has that identifier.");
            }

            var ended = document.Sessions[index];
            document.Sessions.RemoveAt(index);
            return RoofIdentityResult<StoredSession>.Success(ended);
        });

    public RoofIdentityResult<StoredUser> AddUser(string name, string role, string? passwordHash, string? pinHash)
        => Mutate<StoredUser>((document, now) =>
        {
            if (document.Users.Exists(user => Same(user.Name, name)))
            {
                return RoofIdentityResult<StoredUser>.Failure(RoofControllerErrorCode.IdentityNameConflict, $"A person named '{name}' already exists.");
            }

            var user = new StoredUser(name, role, passwordHash, pinHash, NewStamp(), now, now);
            document.Users.Add(user);
            return RoofIdentityResult<StoredUser>.Success(user);
        });

    /// <summary>
    /// Changes a person. A null hash leaves that secret as it is. A new role or password ends all their sessions; a new
    /// or removed PIN ends their PIN sessions.
    /// </summary>
    public RoofIdentityResult<RoofUserChange> UpdateUser(
        string name,
        string role,
        string? newPasswordHash,
        string? newPinHash,
        bool removePassword,
        bool removePin)
        => Mutate<RoofUserChange>((document, now) =>
        {
            var index = document.Users.FindIndex(user => Same(user.Name, name));
            if (index < 0)
            {
                return RoofIdentityResult<RoofUserChange>.Failure(RoofControllerErrorCode.IdentityNotFound, $"No person is named '{name}'.");
            }

            var before = document.Users[index];
            var passwordHash = removePassword ? null : newPasswordHash ?? before.PasswordHash;
            var pinHash = removePin ? null : newPinHash ?? before.PinHash;
            var failure = CheckUserSecrets(role, passwordHash, pinHash);
            if (failure is not null)
            {
                return RoofIdentityResult<RoofUserChange>.Failure(RoofControllerErrorCode.InvalidRequest, failure);
            }

            var roleChanged = !string.Equals(before.Role, role, StringComparison.Ordinal);
            var passwordChanged = removePassword || newPasswordHash is not null;
            var pinChanged = removePin || newPinHash is not null;
            var after = before with
            {
                Role = role,
                PasswordHash = passwordHash,
                PinHash = pinHash,
                Stamp = roleChanged || passwordChanged || pinChanged ? NewStamp() : before.Stamp,
                UpdatedUtc = now
            };
            document.Users[index] = after;

            var ended = roleChanged || passwordChanged
                ? document.Sessions.RemoveAll(session => Same(session.UserName, before.Name))
                : pinChanged
                    ? document.Sessions.RemoveAll(session => Same(session.UserName, before.Name) && session.Kind == RoofCredentialKind.Pin)
                    : 0;
            return RoofIdentityResult<RoofUserChange>.Success(new RoofUserChange(after, ended));
        });

    /// <summary>
    /// A signed-in person changes their own password: every other session of theirs ends, <paramref name="keepSessionId"/>
    /// stays open. Refused when the password or role changed since <paramref name="expectedStamp"/> was read.
    /// </summary>
    public RoofIdentityResult<RoofUserChange> ChangeOwnPassword(string name, string expectedStamp, string newPasswordHash, string keepSessionId)
        => Mutate<RoofUserChange>((document, now) =>
        {
            var index = document.Users.FindIndex(user => Same(user.Name, name));
            if (index < 0 || !string.Equals(document.Users[index].Stamp, expectedStamp, StringComparison.Ordinal))
            {
                return RoofIdentityResult<RoofUserChange>.Failure(RoofControllerErrorCode.SignInFailed, "The current password is not correct.");
            }

            var after = document.Users[index] with { PasswordHash = newPasswordHash, Stamp = NewStamp(), UpdatedUtc = now };
            document.Users[index] = after;
            var ended = document.Sessions.RemoveAll(session =>
                Same(session.UserName, name) && !string.Equals(session.Id, keepSessionId, StringComparison.Ordinal));
            return RoofIdentityResult<RoofUserChange>.Success(new RoofUserChange(after, ended));
        });

    /// <summary>
    /// Replaces a password or PIN hash with a stronger one of the same secret (after a sign-in, when the hash settings
    /// changed). Nothing happens if the hash changed meanwhile.
    /// </summary>
    public void UpgradeHash(string name, string oldHash, string newHash, bool pin)
    {
        try
        {
            Mutate<bool>((document, _) =>
            {
                var index = document.Users.FindIndex(user => Same(user.Name, name));
                if (index < 0)
                {
                    return RoofIdentityResult<bool>.Failure(RoofControllerErrorCode.IdentityNotFound, "gone");
                }

                var user = document.Users[index];
                var current = pin ? user.PinHash : user.PasswordHash;
                if (!string.Equals(current, oldHash, StringComparison.Ordinal))
                {
                    return RoofIdentityResult<bool>.Failure(RoofControllerErrorCode.IdentityNameConflict, "changed");
                }

                document.Users[index] = pin ? user with { PinHash = newHash } : user with { PasswordHash = newHash };
                return RoofIdentityResult<bool>.Success(true);
            });
        }
        catch (RoofIdentityStoreException ex)
        {
            // The old hash still works; the upgrade is tried again at the next sign-in.
            _logger.LogWarning("Could not save an upgraded hash for {Name}: {Reason}", name, ex.Message);
        }
    }

    public RoofIdentityResult<RoofUserChange> RemoveUser(string name)
        => Mutate<RoofUserChange>((document, _) =>
        {
            var index = document.Users.FindIndex(user => Same(user.Name, name));
            if (index < 0)
            {
                return RoofIdentityResult<RoofUserChange>.Failure(RoofControllerErrorCode.IdentityNotFound, $"No person is named '{name}'.");
            }

            var removed = document.Users[index];
            document.Users.RemoveAt(index);
            var ended = document.Sessions.RemoveAll(session => Same(session.UserName, removed.Name));
            return RoofIdentityResult<RoofUserChange>.Success(new RoofUserChange(removed, ended));
        });

    public RoofIdentityResult<StoredApiKey> AddManagedKey(string name, string role, bool kiosk, string keySha256)
        => Mutate<StoredApiKey>((document, now) =>
        {
            if (kiosk && role != RoofControllerApiContract.ViewerRole)
            {
                return RoofIdentityResult<StoredApiKey>.Failure(RoofControllerErrorCode.InvalidRequest, KioskRoleRequired);
            }

            if (document.ApiKeys.Exists(key => Same(key.Name, name)) || ConfiguredKeyNames().Contains(name))
            {
                return RoofIdentityResult<StoredApiKey>.Failure(RoofControllerErrorCode.IdentityNameConflict, $"An API key named '{name}' already exists.");
            }

            var key = new StoredApiKey(name, role, kiosk, keySha256, now, now);
            document.ApiKeys.Add(key);
            return RoofIdentityResult<StoredApiKey>.Success(key);
        });

    /// <summary>Changes a managed key's role or kiosk flag. Turning the kiosk flag off ends the PIN sessions opened at it.</summary>
    public RoofIdentityResult<StoredApiKey> UpdateManagedKey(string name, string role, bool kiosk)
        => MutateManagedKey(name, (document, key, now) =>
        {
            var after = key with { Role = role, Kiosk = kiosk, UpdatedUtc = now };
            if (key.Kiosk && !kiosk)
            {
                EndDeviceSessions(document, key);
            }

            return after;
        });

    /// <summary>Gives a managed key a new value; the old value stops working and PIN sessions opened at it end.</summary>
    public RoofIdentityResult<StoredApiKey> RotateManagedKey(string name, string newKeySha256)
        => MutateManagedKey(name, (document, key, now) =>
        {
            EndDeviceSessions(document, key);
            return key with { KeySha256 = newKeySha256, UpdatedUtc = now };
        });

    public RoofIdentityResult<StoredApiKey> RemoveManagedKey(string name)
        => MutateManagedKey(name, (document, key, _) =>
        {
            EndDeviceSessions(document, key);
            return null;
        });

    /// <summary>Lower-case hex SHA-256 of <paramref name="value"/>'s UTF-8 bytes.</summary>
    internal static string HashHex(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>A new random API key: <see cref="ApiKeyPrefix"/> and 256 random bits.</summary>
    internal static string NewApiKey() => ApiKeyPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Why a person with these secrets is not allowed, or null when they are.</summary>
    internal static string? CheckUserSecrets(string role, string? passwordHash, string? pinHash)
    {
        if (passwordHash is null && pinHash is null)
        {
            return "A person needs a password, a PIN or both, or they could not sign in.";
        }

        if (pinHash is not null && role == RoofControllerApiContract.ViewerRole)
        {
            return "Only operators and admins can have a PIN: a kiosk already shows status and stops the roof without one.";
        }

        return null;
    }

    private RoofIdentityResult<StoredApiKey> MutateManagedKey(
        string name,
        Func<RoofIdentityDocument, StoredApiKey, DateTimeOffset, StoredApiKey?> change)
        => Mutate<StoredApiKey>((document, now) =>
        {
            var index = document.ApiKeys.FindIndex(key => Same(key.Name, name));
            if (index < 0)
            {
                return ConfiguredKeyNames().Contains(name)
                    ? RoofIdentityResult<StoredApiKey>.Failure(
                        RoofControllerErrorCode.IdentityReadOnly,
                        $"The API key '{name}' comes from the controller's configuration; change it there.")
                    : RoofIdentityResult<StoredApiKey>.Failure(RoofControllerErrorCode.IdentityNotFound, $"No API key is named '{name}'.");
            }

            var before = document.ApiKeys[index];
            var after = change(document, before, now);
            if (after is null)
            {
                document.ApiKeys.RemoveAt(index);
                return RoofIdentityResult<StoredApiKey>.Success(before);
            }

            if (after.Kiosk && after.Role != RoofControllerApiContract.ViewerRole)
            {
                return RoofIdentityResult<StoredApiKey>.Failure(RoofControllerErrorCode.InvalidRequest, KioskRoleRequired);
            }

            document.ApiKeys[index] = after;
            return RoofIdentityResult<StoredApiKey>.Success(after);
        });

    private static void EndDeviceSessions(RoofIdentityDocument document, StoredApiKey key)
    {
        var keyId = RoofApiKeyStore.ComputeKeyId(Convert.FromHexString(key.KeySha256));
        document.Sessions.RemoveAll(session => string.Equals(session.DeviceKeyId, keyId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Applies <paramref name="change"/> to a copy of the document under the lock. A failed change leaves everything as it
    /// was; a successful one is checked (it must not remove the last admin credential), saved, and only then published.
    /// </summary>
    private RoofIdentityResult<T> Mutate<T>(Func<RoofIdentityDocument, DateTimeOffset, RoofIdentityResult<T>> change)
    {
        RoofIdentityResult<T> result;
        lock (_gate)
        {
            if (UnavailableReason is not null)
            {
                throw new RoofIdentityStoreException(UnavailableReason);
            }

            var now = _time.GetUtcNow();
            var working = _document.Clone();
            var hadAdmin = HasAdminCredential(working);
            result = change(working, now);
            if (!result.Succeeded)
            {
                return result;
            }

            if (hadAdmin && !HasAdminCredential(working))
            {
                return RoofIdentityResult<T>.Failure(
                    RoofControllerErrorCode.LastAdministrator,
                    "This would leave no admin credential (an admin API key, or an admin with a password). Add another admin first.");
            }

            PruneSessions(working, now);
            working.Revision++;
            _file?.Save(working);
            _document = working;
            _snapshot = Build(working);
            foreach (var id in _lastActivity.Keys)
            {
                if (!_snapshot.SessionsById.ContainsKey(id))
                {
                    _lastActivity.TryRemove(id, out _);
                }
            }
        }

        Changed?.Invoke();
        return result;
    }

    private bool Validate(StoredSession found, bool touch, [NotNullWhen(true)] out StoredSession? session, out string? failure)
    {
        session = null;
        var now = _time.GetUtcNow();
        if (!IsLive(found, now, out failure))
        {
            return false;
        }

        if (!_snapshot.UsersByName.TryGetValue(found.UserName, out var user) || user.Role != found.Role)
        {
            // Changes end sessions as they are saved; this also covers a store edited by hand.
            failure = "the person was removed or changed";
            return false;
        }

        if (touch && found.Kind == RoofCredentialKind.Pin)
        {
            _lastActivity[found.Id] = now.UtcTicks;
        }

        session = found;
        return true;
    }

    private bool IsLive(StoredSession session, DateTimeOffset now, out string? failure)
    {
        if (now >= session.ExpiresUtc)
        {
            failure = "session expired";
            return false;
        }

        if (session.Kind == RoofCredentialKind.Pin)
        {
            var lastActivity = _lastActivity.TryGetValue(session.Id, out var ticks) ? ticks : session.CreatedUtc.UtcTicks;
            if (now.UtcTicks - lastActivity >= _options.CurrentValue.PinSessionIdleTimeout.Ticks)
            {
                failure = "PIN session idle for too long";
                return false;
            }
        }

        failure = null;
        return true;
    }

    private void PruneSessions(RoofIdentityDocument document, DateTimeOffset now)
    {
        document.Sessions.RemoveAll(session => !IsLive(session, now, out _));
        foreach (var crowded in document.Sessions.GroupBy(session => session.UserName, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > MaximumSessionsPerUser)
                     .ToList())
        {
            foreach (var oldest in crowded.OrderBy(session => session.CreatedUtc).Take(crowded.Count() - MaximumSessionsPerUser))
            {
                document.Sessions.Remove(oldest);
            }
        }

        if (document.Sessions.Count > MaximumSessions)
        {
            document.Sessions = document.Sessions.OrderByDescending(session => session.CreatedUtc).Take(MaximumSessions).ToList();
        }
    }

    private bool HasAdminCredential(RoofIdentityDocument document)
        => document.Users.Exists(user => user.Role == RoofControllerApiContract.AdminRole && user.PasswordHash is not null)
            || document.ApiKeys.Exists(key => key.Role == RoofControllerApiContract.AdminRole)
            || RoofApiKeyStore.Inspect(_securityOptions.CurrentValue).Keys.Any(key => key.Role == RoofControllerApiContract.AdminRole);

    private HashSet<string> ConfiguredKeyNames()
        => RoofApiKeyStore.Inspect(_securityOptions.CurrentValue).Keys
            .Select(key => key.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string NewStamp() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Snapshot Build(RoofIdentityDocument document)
    {
        var users = document.Users.ToList();
        var sessions = document.Sessions.ToList();
        return new Snapshot(
            users,
            document.ApiKeys.ToList(),
            sessions,
            users.ToDictionary(user => user.Name, StringComparer.OrdinalIgnoreCase),
            sessions.ToDictionary(session => session.TokenSha256, StringComparer.Ordinal),
            sessions.ToDictionary(session => session.Id, StringComparer.Ordinal));
    }

    private sealed record Snapshot(
        IReadOnlyList<StoredUser> Users,
        IReadOnlyList<StoredApiKey> Keys,
        IReadOnlyList<StoredSession> Sessions,
        IReadOnlyDictionary<string, StoredUser> UsersByName,
        IReadOnlyDictionary<string, StoredSession> SessionsByTokenHash,
        IReadOnlyDictionary<string, StoredSession> SessionsById);
}
