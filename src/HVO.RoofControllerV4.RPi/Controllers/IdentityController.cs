using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Asp.Versioning;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// Admin management of people, managed API keys and open sessions (#41). Secrets are never returned: a person shows
/// only whether a password and PIN are set, and a managed key's value is shown once, when it is created or rotated.
/// API keys from the controller's configuration are listed but read-only. Every change is logged as an <c>AUDIT</c>
/// line naming the admin; no change may leave the controller without an admin credential. Needs an admin API key or an
/// admin's password session: a PIN session is refused (<c>CredentialNotAllowed</c>).
/// </summary>
[ApiController, ApiVersion("4.0")]
[Route(RoofIdentityContract.IdentityRoute)]
[Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.AdminPolicy)]
[RoofRefusePinSession]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Tags("People and API keys")]
public sealed class IdentityController : ControllerBase
{
    private readonly RoofIdentityStore _identity;
    private readonly RoofApiKeyStore _keys;
    private readonly RoofSecretHasher _hasher;
    private readonly ILogger<IdentityController> _logger;

    public IdentityController(
        RoofIdentityStore identity,
        RoofApiKeyStore keys,
        RoofSecretHasher hasher,
        ILogger<IdentityController> logger)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _hasher = hasher ?? throw new ArgumentNullException(nameof(hasher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string Caller => RoofPrincipalFactory.DescribeCaller(User);

    /// <summary>Lists the people who can sign in.</summary>
    [HttpGet("Users", Name = nameof(GetUsers))]
    [ProducesResponseType(typeof(RoofUserResponse[]), StatusCodes.Status200OK)]
    public ActionResult<RoofUserResponse[]> GetUsers()
    {
        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        var sessions = _identity.ActiveSessions;
        return _identity.Users
            .OrderBy(user => user.Name, StringComparer.OrdinalIgnoreCase)
            .Select(user => ToResponse(user, sessions))
            .ToArray();
    }

    /// <summary>Gets one person.</summary>
    [HttpGet("Users/{name}", Name = nameof(GetUser))]
    [ProducesResponseType(typeof(RoofUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<RoofUserResponse> GetUser(string name)
    {
        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        return _identity.TryGetUser(name, out var user)
            ? ToResponse(user, _identity.ActiveSessions)
            : NoSuchUser();
    }

    /// <summary>
    /// Adds a person with a password (web UI, CLI), a PIN (kiosk; operators and admins only) or both.
    /// </summary>
    /// <response code="201">The person, without their secrets.</response>
    /// <response code="400">An invalid name, role, password or PIN.</response>
    /// <response code="409">A person with that name exists (<c>IdentityNameConflict</c>).</response>
    /// <response code="429">Too many passwords are being hashed at once; retry shortly.</response>
    [HttpPost("Users", Name = nameof(AddUser))]
    [ProducesResponseType(typeof(RoofUserResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<RoofUserResponse>> AddUser([FromBody] RoofUserCreateRequest request, CancellationToken cancellationToken)
    {
        if (!RoofIdentityContract.IsValidName(request.Name))
        {
            return Invalid(NameRule);
        }

        var role = RoofPrincipalFactory.NormalizeRole(request.Role);
        if (role is null)
        {
            return Invalid(RoleRule);
        }

        var invalid = SecretFormatProblem(request.Password, request.Pin)
            ?? RoofIdentityStore.CheckUserSecrets(role, request.Password, request.Pin);
        if (invalid is not null)
        {
            return Invalid(invalid);
        }

        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        if (_identity.TryGetUser(request.Name, out _))
        {
            // Refused before hashing; the store checks again when it adds the person.
            return RoofProblemResults.Create(this, RoofControllerErrorCode.IdentityNameConflict, $"A person named '{request.Name}' already exists.");
        }

        var passwordHash = await HashAsync(request.Password, cancellationToken).ConfigureAwait(false);
        var pinHash = await HashAsync(request.Pin, cancellationToken).ConfigureAwait(false);
        if ((request.Password is not null && passwordHash is null) || (request.Pin is not null && pinHash is null))
        {
            return HasherBusy();
        }

        return Change(
            () => _identity.AddUser(request.Name!, role, passwordHash, pinHash),
            user =>
            {
                _logger.LogWarning(
                    "AUDIT {Caller} added {Name} ({Role}; password: {HasPassword}, PIN: {HasPin}) from {Remote}.",
                    Caller, user.Name, user.Role, user.PasswordHash is not null, user.PinHash is not null, Remote);
                return CreatedAtRoute(nameof(GetUser), new { name = user.Name }, ToResponse(user, Array.Empty<StoredSession>()));
            });
    }

    /// <summary>
    /// Changes a person's role, password or PIN. A new role or password ends all their sessions; a new or removed PIN
    /// ends their PIN sessions.
    /// </summary>
    /// <response code="200">The person, without their secrets.</response>
    /// <response code="400">An invalid role, password or PIN, or the person would have neither.</response>
    /// <response code="404">No person has that name.</response>
    /// <response code="409">The change would leave no admin credential (<c>LastAdministrator</c>).</response>
    /// <response code="429">Too many passwords are being hashed at once; retry shortly.</response>
    [HttpPut("Users/{name}", Name = nameof(UpdateUser))]
    [ProducesResponseType(typeof(RoofUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<RoofUserResponse>> UpdateUser(string name, [FromBody] RoofUserUpdateRequest request, CancellationToken cancellationToken)
    {
        var role = RoofPrincipalFactory.NormalizeRole(request.Role);
        if (role is null)
        {
            return Invalid(RoleRule);
        }

        if ((request.RemovePassword && request.Password is not null) || (request.RemovePin && request.Pin is not null))
        {
            return Invalid("Send a new secret or remove it, not both.");
        }

        // A new PIN for a viewer is refused before hashing; the store checks the secrets the person ends up with.
        var invalid = SecretFormatProblem(request.Password, request.Pin)
            ?? (request.Pin is null ? null : RoofIdentityStore.CheckUserSecrets(role, request.Password ?? "unchanged", request.Pin));
        if (invalid is not null)
        {
            return Invalid(invalid);
        }

        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        if (!_identity.TryGetUser(name, out _))
        {
            return NoSuchUser();
        }

        var passwordHash = await HashAsync(request.Password, cancellationToken).ConfigureAwait(false);
        var pinHash = await HashAsync(request.Pin, cancellationToken).ConfigureAwait(false);
        if ((request.Password is not null && passwordHash is null) || (request.Pin is not null && pinHash is null))
        {
            return HasherBusy();
        }

        return Change(
            () => _identity.UpdateUser(name, role, passwordHash, pinHash, request.RemovePassword, request.RemovePin),
            change =>
            {
                _logger.LogWarning(
                    "AUDIT {Caller} changed {Name} (role {Role}; new password: {NewPassword}, new PIN: {NewPin}, password removed: {RemovedPassword}, PIN removed: {RemovedPin}) from {Remote}; {Ended} session(s) ended.",
                    Caller,
                    change.User.Name,
                    change.User.Role,
                    passwordHash is not null,
                    pinHash is not null,
                    request.RemovePassword,
                    request.RemovePin,
                    Remote,
                    change.SessionsEnded);
                return Ok(ToResponse(change.User, _identity.ActiveSessions));
            });
    }

    /// <summary>Removes a person and ends their sessions.</summary>
    /// <response code="204">Removed.</response>
    /// <response code="404">No person has that name.</response>
    /// <response code="409">It would leave no admin credential (<c>LastAdministrator</c>).</response>
    [HttpDelete("Users/{name}", Name = nameof(RemoveUser))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public IActionResult RemoveUser(string name)
    {
        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        if (!_identity.TryGetUser(name, out _))
        {
            return NoSuchUser();
        }

        return Change(
            () => _identity.RemoveUser(name),
            change =>
            {
                _logger.LogWarning(
                    "AUDIT {Caller} removed {Name} ({Role}) from {Remote}; {Ended} session(s) ended.",
                    Caller, change.User.Name, change.User.Role, Remote, change.SessionsEnded);
                return NoContent();
            });
    }

    /// <summary>Lists every API key: those in the controller's configuration (read-only) and the managed ones.</summary>
    [HttpGet("ApiKeys", Name = nameof(GetApiKeys))]
    [ProducesResponseType(typeof(RoofApiKeyResponse[]), StatusCodes.Status200OK)]
    public ActionResult<RoofApiKeyResponse[]> GetApiKeys()
    {
        if (!_identity.IsAvailable)
        {
            // The managed keys cannot be read, so the list would be incomplete.
            return StoreUnavailable();
        }

        var configured = _keys.Keys
            .Where(key => key.Source == RoofApiKeySource.Configuration)
            .Select(key => new RoofApiKeyResponse(key.Name, key.Role, key.Kiosk, RoofApiKeySource.Configuration, null, null));
        var managed = _identity.ManagedKeys.Select(ToResponse);
        return configured.Concat(managed)
            .OrderBy(key => key.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Adds a managed API key. The controller generates the key and returns it once; store it now, it cannot be read
    /// again. A kiosk key must have the <c>RoofViewer</c> role.
    /// </summary>
    /// <response code="201">The key and its value.</response>
    /// <response code="400">An invalid name or role, or a kiosk key that is not a viewer.</response>
    /// <response code="409">A key with that name exists (<c>IdentityNameConflict</c>).</response>
    [HttpPost("ApiKeys", Name = nameof(AddApiKey))]
    [ProducesResponseType(typeof(RoofApiKeySecretResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public IActionResult AddApiKey([FromBody] RoofApiKeyCreateRequest request)
    {
        if (!RoofIdentityContract.IsValidName(request.Name))
        {
            return Invalid(NameRule);
        }

        var role = RoofPrincipalFactory.NormalizeRole(request.Role);
        if (role is null)
        {
            return Invalid(RoleRule);
        }

        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        var secret = RoofIdentityStore.NewApiKey();
        return Change(
            () => _identity.AddManagedKey(request.Name!, role, request.Kiosk, RoofIdentityStore.HashHex(secret)),
            key =>
            {
                _logger.LogWarning(
                    "AUDIT {Caller} added the API key {Name} ({Role}, kiosk: {Kiosk}) from {Remote}.",
                    Caller, key.Name, key.Role, key.Kiosk, Remote);
                NoStore();
                return StatusCode(StatusCodes.Status201Created, new RoofApiKeySecretResponse(ToResponse(key), secret));
            });
    }

    /// <summary>
    /// Changes a managed key's role or kiosk flag. Turning the kiosk flag off ends the PIN sessions opened at it.
    /// </summary>
    /// <response code="200">The key.</response>
    /// <response code="400">An invalid role, or a kiosk key that is not a viewer.</response>
    /// <response code="404">No managed key has that name.</response>
    /// <response code="409">The key is in the configuration (<c>IdentityReadOnly</c>), or no admin credential would remain.</response>
    [HttpPut("ApiKeys/{name}", Name = nameof(UpdateApiKey))]
    [ProducesResponseType(typeof(RoofApiKeyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public IActionResult UpdateApiKey(string name, [FromBody] RoofApiKeyUpdateRequest request)
    {
        var role = RoofPrincipalFactory.NormalizeRole(request.Role);
        if (role is null)
        {
            return Invalid(RoleRule);
        }

        if (!RoofIdentityContract.IsValidName(name))
        {
            return NoSuchKey();
        }

        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        return Change(
            () => _identity.UpdateManagedKey(name, role, request.Kiosk!.Value),
            key =>
            {
                _logger.LogWarning(
                    "AUDIT {Caller} changed the API key {Name} ({Role}, kiosk: {Kiosk}) from {Remote}.",
                    Caller, key.Name, key.Role, key.Kiosk, Remote);
                return Ok(ToResponse(key));
            });
    }

    /// <summary>
    /// Gives a managed key a new value, returned once. The old value stops working at once, and PIN sessions opened at
    /// the key end.
    /// </summary>
    /// <response code="200">The key and its new value.</response>
    /// <response code="404">No managed key has that name.</response>
    /// <response code="409">The key is in the configuration (<c>IdentityReadOnly</c>).</response>
    [HttpPost("ApiKeys/{name}/Rotate", Name = nameof(RotateApiKey))]
    [ProducesResponseType(typeof(RoofApiKeySecretResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public IActionResult RotateApiKey(string name)
    {
        if (!RoofIdentityContract.IsValidName(name))
        {
            return NoSuchKey();
        }

        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        var secret = RoofIdentityStore.NewApiKey();
        return Change(
            () => _identity.RotateManagedKey(name, RoofIdentityStore.HashHex(secret)),
            key =>
            {
                _logger.LogWarning("AUDIT {Caller} rotated the API key {Name} from {Remote}.", Caller, key.Name, Remote);
                NoStore();
                return Ok(new RoofApiKeySecretResponse(ToResponse(key), secret));
            });
    }

    /// <summary>Removes a managed key; it stops working at once and PIN sessions opened at it end.</summary>
    /// <response code="204">Removed.</response>
    /// <response code="404">No managed key has that name.</response>
    /// <response code="409">The key is in the configuration, or no admin credential would remain.</response>
    [HttpDelete("ApiKeys/{name}", Name = nameof(RemoveApiKey))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public IActionResult RemoveApiKey(string name)
    {
        if (!RoofIdentityContract.IsValidName(name))
        {
            return NoSuchKey();
        }

        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        return Change(
            () => _identity.RemoveManagedKey(name),
            key =>
            {
                _logger.LogWarning("AUDIT {Caller} removed the API key {Name} ({Role}) from {Remote}.", Caller, key.Name, key.Role, Remote);
                return NoContent();
            });
    }

    /// <summary>Lists the open sessions (never their tokens).</summary>
    [HttpGet("Sessions", Name = nameof(GetSessions))]
    [ProducesResponseType(typeof(RoofSessionInfoResponse[]), StatusCodes.Status200OK)]
    public ActionResult<RoofSessionInfoResponse[]> GetSessions()
    {
        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        return _identity.ActiveSessions
            .OrderBy(session => session.CreatedUtc)
            .Select(session => new RoofSessionInfoResponse(
                session.Id,
                session.UserName,
                session.Role,
                session.Kind,
                session.Device,
                session.CreatedUtc,
                session.ExpiresUtc))
            .ToArray();
    }

    /// <summary>Ends a session; its token stops working at once.</summary>
    /// <response code="204">Ended.</response>
    /// <response code="404">No open session has that identifier.</response>
    [HttpDelete("Sessions/{id}", Name = nameof(EndUserSession))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult EndUserSession(string id)
    {
        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        if (!_identity.TryGetLiveSession(id, out _))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.IdentityNotFound, "No open session has that identifier.");
        }

        return Change(
            () => _identity.EndSession(id),
            session =>
            {
                _logger.LogWarning(
                    "AUDIT {Caller} ended {Name}'s session {SessionId} from {Remote}.",
                    Caller, session.UserName, session.Id, Remote);
                return NoContent();
            });
    }

    internal const string NameRule =
        "A name has 1 to 64 characters: a letter or digit, then letters, digits, '.', '_', '@' or '-'.";

    internal const string RoleRule = "The role must be RoofViewer, RoofOperator or RoofAdmin.";

    private string Remote => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Why a password or PIN that was sent has the wrong form, or null.</summary>
    private static string? SecretFormatProblem(string? password, string? pin)
    {
        if (password is not null && !RoofIdentityContract.IsValidPassword(password))
        {
            return AuthController.PasswordRule;
        }

        if (pin is not null && !RoofIdentityContract.IsValidPin(pin))
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"A PIN has {RoofIdentityContract.MinimumPinLength} to {RoofIdentityContract.MaximumPinLength} digits.");
        }

        return null;
    }

    private async Task<string?> HashAsync(string? secret, CancellationToken cancellationToken)
        => secret is null ? null : await _hasher.HashAsync(secret, cancellationToken).ConfigureAwait(false);

    /// <summary>Applies a change to the store and maps its refusal, or a store that cannot be written, to ProblemDetails.</summary>
    private ActionResult Change<T>(Func<RoofIdentityResult<T>> change, Func<T, ActionResult> succeeded)
    {
        RoofIdentityResult<T> result;
        try
        {
            result = change();
        }
        catch (RoofIdentityStoreException ex)
        {
            _logger.LogError("Identity change by {Caller} could not be saved: {Reason}", Caller, ex.Message);
            return StoreUnavailable();
        }

        return result.Succeeded
            ? succeeded(result.Value!)
            : RoofProblemResults.Create(this, result.Error!.Value, result.Detail!);
    }

    private void NoStore()
    {
        // A key value must not be kept by a cache or proxy.
        Response.Headers.CacheControl = "no-store";
        Response.Headers[HeaderNames.Pragma] = "no-cache";
    }

    private static RoofUserResponse ToResponse(StoredUser user, IReadOnlyList<StoredSession> sessions)
        => new(
            user.Name,
            user.Role,
            user.PasswordHash is not null,
            user.PinHash is not null,
            user.CreatedUtc,
            user.UpdatedUtc,
            sessions.Count(session => string.Equals(session.UserName, user.Name, StringComparison.OrdinalIgnoreCase)));

    private static RoofApiKeyResponse ToResponse(StoredApiKey key)
        => new(key.Name, key.Role, key.Kiosk, RoofApiKeySource.Managed, key.CreatedUtc, key.UpdatedUtc);

    private ObjectResult Invalid(string detail) => RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest, detail);

    private ObjectResult NoSuchUser() => RoofProblemResults.Create(this, RoofControllerErrorCode.IdentityNotFound, "No person has that name.");

    private ObjectResult NoSuchKey() => RoofProblemResults.Create(this, RoofControllerErrorCode.IdentityNotFound, "No managed API key has that name.");

    private ObjectResult HasherBusy()
        => RoofProblemResults.Create(
            this,
            RoofControllerErrorCode.SignInBusy,
            "Too many passwords or PINs are being hashed at once. Retry in a few seconds.",
            TimeSpan.FromSeconds(2));

    private ObjectResult StoreUnavailable()
        => RoofProblemResults.Create(
            this,
            RoofControllerErrorCode.IdentityStoreUnavailable,
            "The identity store is unavailable; see the controller log. API keys in the configuration still work.",
            TimeSpan.FromSeconds(30));
}
