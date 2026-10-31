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
public sealed partial class RoofIdentityStore
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
    private readonly IOptions<HatEmulatorOptions>? _emulator;
    private readonly IOptionsMonitor<RoofControllerSecurityOptions> _securityOptions;

    // Session id -> UTC ticks of its last request. PIN sessions end after the idle timeout; after a restart every PIN
    // session starts a fresh idle period.
    private readonly ConcurrentDictionary<string, long> _lastActivity = new(StringComparer.Ordinal);

    private RoofIdentityDocument _document;
    private volatile Snapshot _snapshot;
    private bool _reportedUnflushedSave;

    public RoofIdentityStore(
        IOptionsMonitor<RoofIdentityOptions> options,
        IOptionsMonitor<RoofControllerSecurityOptions> securityOptions,
        TimeProvider time,
        ILogger<RoofIdentityStore> logger,
        IOptions<HatEmulatorOptions>? emulator = null)
    {
        _emulator = emulator;
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
            try
            {
                _file = new RoofIdentityFile(path);
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
                        _file!.Path);
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

        if (UnavailableReason is null && options.CurrentValue.LinuxAuth.Enabled)
        {
            try { MigrateProviderIdentity(); }
            catch (RoofIdentityStoreException ex) { UnavailableReason = ex.Message; }
        }

        var now = _time.GetUtcNow();
        foreach (var session in _document.Sessions.Where(session => session.Kind == RoofCredentialKind.Pin))
        {
            _lastActivity[session.Id] = now.UtcTicks;
        }

        try
        {
            _snapshot = Build(_document);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Load checks for everything Build relies on; this is the last line that keeps a bad file from stopping startup.
            UnavailableReason = $"The identity store '{_file?.Path}' is inconsistent: {ex.Message}";
            _logger.LogError(
                "Identity store unavailable: {Reason} Sign-in and identity management are refused; configured API keys still work.",
                UnavailableReason);
            _document = new RoofIdentityDocument();
            _lastActivity.Clear();
            _snapshot = Build(_document);
        }
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
    /// <paramref name="expectedStamp"/> was read (the credential that was checked is then out of date). Records the
    /// sign-in as the person's last (#209), in the same save.
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
            var index = document.Users.FindIndex(candidate => Same(candidate.Name, userName));
            var user = index < 0 ? null : document.Users[index];
            if (user is null || !string.Equals(user.Stamp, expectedStamp, StringComparison.Ordinal))
            {
                return RoofIdentityResult<RoofIssuedSession>.Failure(
                    RoofControllerErrorCode.SignInFailed,
                    "The name, password or PIN is not correct.");
            }

            if (kind == RoofCredentialKind.Pin && user.TemporaryPin && !CanUseTemporaryPin(user))
                return RoofIdentityResult<RoofIssuedSession>.Failure(RoofControllerErrorCode.SignInFailed, "Temporary kiosk commissioning access is unavailable.");
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
                now + lifetime)
            {
                TemporaryPin = kind == RoofCredentialKind.Pin && CanUseTemporaryPin(user),
                PrincipalId = ProviderEnabled && kind == RoofCredentialKind.Session ? user.PrincipalId : null,
                PrincipalStamp = ProviderEnabled && kind == RoofCredentialKind.Session ? user.AuthenticationEpoch ?? user.Stamp : null,
                Provider = ProviderEnabled && kind == RoofCredentialKind.Session ? "virtual" : null
            };
            document.Sessions.Add(session);
            document.Users[index] = user with { LastLoginUtc = now };
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

    public bool TemporaryPinEnabled => _options.CurrentValue.AllowTemporaryKioskPin && _emulator?.Value.Enabled == true;

    public bool CanUseTemporaryPin(StoredUser user)
        => TemporaryPinEnabled && user.TemporaryPin && user.PinHash is null && user.Role != RoofControllerApiContract.ViewerRole;

    /// <summary>
    /// Adds a person. <paramref name="displayName"/> and <paramref name="shortName"/> are optional and already
    /// normalized (<see cref="RoofIdentityContract.NormalizeLabel"/>) and checked.
    /// </summary>
    public RoofIdentityResult<StoredUser> AddUser(
        string name,
        string role,
        string? passwordHash,
        string? pinHash,
        bool temporaryPin = false,
        string? displayName = null,
        string? shortName = null)
        => Mutate<StoredUser>((document, now) =>
        {
            if (document.Users.Exists(user => Same(user.Name, name)))
            {
                return RoofIdentityResult<StoredUser>.Failure(RoofControllerErrorCode.IdentityNameConflict, $"A person named '{name}' already exists.");
            }

            if (temporaryPin && (!TemporaryPinEnabled || pinHash is not null || passwordHash is null || role == RoofControllerApiContract.ViewerRole))
                return RoofIdentityResult<StoredUser>.Failure(RoofControllerErrorCode.InvalidRequest, "Temporary kiosk access requires explicit emulator commissioning, an operator or admin password account, and no personal PIN.");
            var stamp = NewStamp();
            var user = new StoredUser(name, role, passwordHash, pinHash, stamp, now, now)
            {
                TemporaryPin = temporaryPin,
                DisplayName = displayName,
                ShortName = shortName,
                PrincipalId = document.SchemaVersion >= 2 ? Guid.NewGuid().ToString("N") : null,
                AuthenticationEpoch = document.SchemaVersion >= 2 ? stamp : null
            };
            document.Users.Add(user);
            return RoofIdentityResult<StoredUser>.Success(user);
        });

    /// <summary>
    /// Changes a person. A null hash leaves that secret as it is. A new role or password ends all their sessions; a new
    /// or removed PIN ends their PIN sessions. A null <paramref name="displayName"/> or <paramref name="shortName"/>
    /// leaves it as it is, an empty one removes it, and any other (normalized and checked) replaces it; a name changes
    /// no stamp and ends no session.
    /// </summary>
    public RoofIdentityResult<RoofUserChange> UpdateUser(
        string name,
        string role,
        string? newPasswordHash,
        string? newPinHash,
        bool removePassword,
        bool removePin,
        string? displayName = null,
        string? shortName = null)
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
                TemporaryPin = pinChanged ? false : before.TemporaryPin,
                DisplayName = displayName is null ? before.DisplayName : displayName.Length == 0 ? null : displayName,
                ShortName = shortName is null ? before.ShortName : shortName.Length == 0 ? null : shortName,
                Stamp = roleChanged || passwordChanged || pinChanged ? NewStamp() : before.Stamp,
                AuthenticationEpoch = document.SchemaVersion >= 2 && (roleChanged || passwordChanged) ? NewStamp() : before.AuthenticationEpoch,
                UpdatedUtc = now
            };
            document.Users[index] = after;

            var ended = roleChanged || passwordChanged
                ? document.Sessions.RemoveAll(session => session.Provider != "linux" && Same(session.UserName, before.Name))
                : pinChanged
                    ? document.Sessions.RemoveAll(session => session.Provider != "linux" && Same(session.UserName, before.Name) && session.Kind == RoofCredentialKind.Pin)
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

            var after = document.Users[index] with { PasswordHash = newPasswordHash, Stamp = NewStamp(), AuthenticationEpoch = document.SchemaVersion >= 2 ? NewStamp() : null, UpdatedUtc = now };
            document.Users[index] = after;
            var retainedIndex = document.Sessions.FindIndex(session => session.Id == keepSessionId && session.Provider == "virtual" && session.PrincipalId == after.PrincipalId);
            if (retainedIndex >= 0) document.Sessions[retainedIndex] = document.Sessions[retainedIndex] with { PrincipalStamp = after.AuthenticationEpoch ?? after.Stamp };
            var ended = document.Sessions.RemoveAll(session =>
                session.Provider != "linux" && Same(session.UserName, name) && !string.Equals(session.Id, keepSessionId, StringComparison.Ordinal));
            return RoofIdentityResult<RoofUserChange>.Success(new RoofUserChange(after, ended));
        });

    /// <summary>
    /// A signed-in person changes their own PIN (#205): the new one replaces their PIN, or the emulator's temporary one.
    /// Their other PIN sessions end; <paramref name="keepSessionId"/> stays open, and so do their password sessions.
    /// Refused when the PIN, password or role changed since <paramref name="expectedStamp"/> was read.
    /// </summary>
    public RoofIdentityResult<RoofUserChange> ChangeOwnPin(string name, string expectedStamp, string newPinHash, string keepSessionId)
        => Mutate<RoofUserChange>((document, now) =>
        {
            var index = document.Users.FindIndex(user => Same(user.Name, name));
            if (index < 0 || !string.Equals(document.Users[index].Stamp, expectedStamp, StringComparison.Ordinal))
            {
                return RoofIdentityResult<RoofUserChange>.Failure(RoofControllerErrorCode.SignInFailed, "The current PIN is not correct.");
            }

            var before = document.Users[index];
            var failure = CheckUserSecrets(before.Role, before.PasswordHash, newPinHash);
            if (failure is not null)
            {
                return RoofIdentityResult<RoofUserChange>.Failure(RoofControllerErrorCode.InvalidRequest, failure);
            }

            var after = before with { PinHash = newPinHash, TemporaryPin = false, Stamp = NewStamp(), UpdatedUtc = now };
            document.Users[index] = after;
            var ended = document.Sessions.RemoveAll(session =>
                session.Provider != "linux" && Same(session.UserName, before.Name) && session.Kind == RoofCredentialKind.Pin
                && !string.Equals(session.Id, keepSessionId, StringComparison.Ordinal));
            return RoofIdentityResult<RoofUserChange>.Success(new RoofUserChange(after, ended));
        });

    /// <summary>
    /// An admin sets (<paramref name="newPinHash"/>) or removes (null) a person's PIN (#205), whatever their role is now.
    /// Their PIN sessions end; their password sessions stay. Removing a PIN the person does not have changes nothing.
    /// </summary>
    public RoofIdentityResult<RoofUserChange> SetPin(string name, string? newPinHash)
        => Mutate<RoofUserChange>((document, now) =>
        {
            var index = document.Users.FindIndex(user => Same(user.Name, name));
            if (index < 0)
            {
                return RoofIdentityResult<RoofUserChange>.Failure(RoofControllerErrorCode.IdentityNotFound, $"No person is named '{name}'.");
            }

            var before = document.Users[index];
            if (newPinHash is null && before.PinHash is null && !before.TemporaryPin)
            {
                return RoofIdentityResult<RoofUserChange>.Success(new RoofUserChange(before, 0));
            }

            var failure = CheckUserSecrets(before.Role, before.PasswordHash, newPinHash);
            if (failure is not null)
            {
                return RoofIdentityResult<RoofUserChange>.Failure(RoofControllerErrorCode.InvalidRequest, failure);
            }

            var after = before with { PinHash = newPinHash, TemporaryPin = false, Stamp = NewStamp(), UpdatedUtc = now };
            document.Users[index] = after;
            var ended = document.Sessions.RemoveAll(session =>
                session.Provider != "linux" && Same(session.UserName, before.Name) && session.Kind == RoofCredentialKind.Pin);
            return RoofIdentityResult<RoofUserChange>.Success(new RoofUserChange(after, ended));
        });

    /// <summary>
    /// A signed-in person changes their own display and short name (#209). A null name is left as it is, an empty one is
    /// removed, and any other (normalized and checked) replaces it. Changes no stamp and ends no session.
    /// </summary>
    public RoofIdentityResult<StoredUser> ChangeOwnNames(string name, string? displayName, string? shortName)
        => Mutate<StoredUser>((document, now) =>
        {
            var index = document.Users.FindIndex(user => Same(user.Name, name));
            if (index < 0)
            {
                return RoofIdentityResult<StoredUser>.Failure(RoofControllerErrorCode.IdentityNotFound, "No person has that name.");
            }

            var before = document.Users[index];
            var after = before with
            {
                DisplayName = displayName is null ? before.DisplayName : displayName.Length == 0 ? null : displayName,
                ShortName = shortName is null ? before.ShortName : shortName.Length == 0 ? null : shortName,
                UpdatedUtc = now
            };
            document.Users[index] = after;
            return RoofIdentityResult<StoredUser>.Success(after);
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
            var ended = document.Sessions.RemoveAll(session => session.Provider != "linux" && Same(session.UserName, removed.Name));
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

            ReconcileKioskCredential(working, now);
            PruneSessions(working, now);
            working.Revision++;
            if (_document.KioskCredential is { State: "pending" } && working.KioskCredential is { State: not "pending" })
                _file?.RetireKioskDefault();
            var persisted = working.Clone();
            persisted.Sessions.RemoveAll(session => session.TemporaryPin);
            var durable = _file?.Save(persisted) != false;
            if (!durable && OperatingSystem.IsLinux() && !_reportedUnflushedSave)
            {
                _reportedUnflushedSave = true;
                _logger.LogWarning(
                    "The identity store {Path} was saved, but its directory could not be flushed to disk, so a power cut in " +
                    "the next few seconds could bring back the previous version. Reported once.",
                    _file!.Path);
            }

            _document = working;
            _snapshot = Build(working);
            if (!durable && working.KioskCredential is not null)
                throw new RoofIdentityStoreException("Kiosk credential change was not durably confirmed; access must be checked again.");
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

        if (found.PrincipalId is not null
            ? !ProviderSessionIsCurrent(found)
            : !_snapshot.UsersByName.TryGetValue(found.UserName, out var user) || user.Role != found.Role)
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
        if (session.TemporaryPin && !TemporaryPinEnabled)
        {
            failure = "temporary kiosk commissioning access ended";
            return false;
        }
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
        foreach (var crowded in document.Sessions.GroupBy(session => (session.Scope is null ? "remote:" : "local:") + (session.PrincipalId ?? "legacy:" + session.UserName.ToLowerInvariant()), StringComparer.Ordinal)
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
