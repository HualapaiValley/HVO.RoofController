using System;
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
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// Sign-in for people (#41): a name and password gives a session for the web UI and the CLI; a name and PIN, sent with
/// a kiosk key, gives a short session at that kiosk. A session token is sent as <c>Authorization: Bearer &lt;token&gt;</c>
/// on every API route and on the status hub. Every refusal is RFC 7807 ProblemDetails with a <c>code</c> extension; a
/// wrong name, password or PIN always gets the same answer.
/// </summary>
[ApiController, ApiVersion("4.0")]
[Route(RoofIdentityContract.AuthRoute)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Tags("Sign-in")]
public sealed class AuthController : ControllerBase
{
    private readonly RoofSignInService _signIn;
    private readonly RoofKioskSignInService _kioskSignIn;
    private readonly RoofIdentityStore _identity;
    private readonly RoofApiKeyStore _keys;
    private readonly IOptionsMonitor<RoofIdentityOptions> _options;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        RoofSignInService signIn,
        RoofKioskSignInService kioskSignIn,
        RoofIdentityStore identity,
        RoofApiKeyStore keys,
        IOptionsMonitor<RoofIdentityOptions> options,
        ILogger<AuthController> logger)
    {
        _signIn = signIn ?? throw new ArgumentNullException(nameof(signIn));
        _kioskSignIn = kioskSignIn ?? throw new ArgumentNullException(nameof(kioskSignIn));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string Remote => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Signs in with a name and password and returns a session token (anyone).</summary>
    /// <response code="200">The new session; the token is shown only here.</response>
    /// <response code="401">The name or password is not correct (<c>SignInFailed</c>).</response>
    /// <response code="429">Locked out after repeated failures, or too many sign-ins at once; see <c>Retry-After</c>.</response>
    /// <response code="503">The identity store is unavailable.</response>
    [HttpPost("Session", Name = nameof(CreateSession))]
    [EnableRateLimiting(RoofSignInRateLimiting.PolicyName)]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RoofSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<RoofSessionResponse>> CreateSession([FromBody] RoofSignInRequest request, CancellationToken cancellationToken)
    {
        // A malformed name or password can match nobody: the same answer as a wrong one, without hashing it.
        if (!RoofIdentityContract.IsValidName(request.Name) || request.Password is not { Length: > 0 and <= RoofIdentityContract.MaximumPasswordLength })
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.SignInFailed, RoofSignInService.WrongCredentials);
        }

        var result = await _signIn.SignInWithPasswordAsync(request.Name!, request.Password, Remote, cancellationToken).ConfigureAwait(false);
        return SessionOrProblem(result);
    }

    /// <summary>
    /// Signs in with a name and PIN at a kiosk (the kiosk's key in <c>X-Api-Key</c>). Only operators and admins have a
    /// PIN. A failure counts for both the kiosk and the name's PIN (at every kiosk); the name's password still works when
    /// its PIN is locked out, and Stop with the kiosk key keeps working.
    /// </summary>
    /// <response code="200">The new PIN session; it ends after a few idle minutes (<c>IdleTimeoutSeconds</c>).</response>
    /// <response code="401">No valid API key, or the name or PIN is not correct (<c>SignInFailed</c>).</response>
    /// <response code="403">The API key is not a kiosk key (<c>KioskKeyRequired</c>).</response>
    /// <response code="429">This kiosk, or this name's PIN, is locked out after repeated failures (<c>SignInLockedOut</c>), or too many sign-ins at once (<c>SignInBusy</c>); see <c>Retry-After</c>.</response>
    /// <response code="503">The identity store is unavailable.</response>
    [HttpPost("Pin", Name = nameof(SignInWithPin))]
    [EnableRateLimiting(RoofSignInRateLimiting.PolicyName)]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiKeyScheme)]
    [ProducesResponseType(typeof(RoofSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<RoofSessionResponse>> SignInWithPin([FromBody] RoofPinSignInRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetKiosk(out var kiosk, out var refusal))
        {
            return refusal;
        }

        if (!RoofIdentityContract.IsValidName(request.Name) || request.Pin is not { Length: > 0 and <= RoofIdentityContract.MaximumAcceptedPinLength })
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.SignInFailed, RoofSignInService.WrongCredentials);
        }

        var result = await _signIn.SignInWithPinAsync(kiosk, request.Name!, request.Pin, Remote, cancellationToken).ConfigureAwait(false);
        return SessionOrProblem(result);
    }

    /// <summary>The people who can sign in with a PIN, for a kiosk's picker (the kiosk's key in <c>X-Api-Key</c>).</summary>
    /// <response code="200">
    /// Names and roles, sorted by name, with each person's display and short name, login type and last sign-in (#209).
    /// Nothing secret.
    /// </response>
    /// <response code="403">The API key is not a kiosk key (<c>KioskKeyRequired</c>).</response>
    /// <response code="503">The identity store is unavailable.</response>
    [HttpGet("Pin/Users", Name = nameof(GetPinUsers))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiKeyScheme)]
    [ProducesResponseType(typeof(RoofPinUserResponse[]), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<RoofPinUserResponse[]> GetPinUsers()
    {
        if (!TryGetKiosk(out _, out var refusal))
        {
            return refusal;
        }

        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        var credential = _identity.GetKioskCredential();
        var owner = credential is null ? null : _identity.ProviderPrincipals.FirstOrDefault(p => p.Id == credential.PrincipalId && p.Enabled);
        var commissioned = owner is null ? Array.Empty<RoofPinUserResponse>() : new[] { new RoofPinUserResponse(owner.Name, RoofControllerApiContract.OperatorRole)
            {
                PrincipalId = owner.Id,
                Provider = owner.Provider,
                SetupPending = credential!.State == "pending",
                DisplayName = owner.DisplayName,
                ShortName = owner.ShortName,
                LastLoginUtc = owner.LastLoginUtc
            } };
        return _identity.Users
            .Where(user => (user.PinHash is not null || _identity.CanUseTemporaryPin(user)) && user.Role != RoofControllerApiContract.ViewerRole)
            .OrderBy(user => user.Name, StringComparer.OrdinalIgnoreCase)
            .Select(user => new RoofPinUserResponse(user.Name, user.Role)
            {
                DisplayName = user.DisplayName,
                ShortName = user.ShortName,
                LastLoginUtc = user.LastLoginUtc
            })
            .Concat(commissioned).ToArray();
    }

    /// <summary>
    /// Who the caller is: the person or API key, its role and how it signed in, and a person's display and short name
    /// (any credential).
    /// </summary>
    /// <response code="200">The caller.</response>
    [HttpGet("Me", Name = nameof(GetCaller))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme)]
    [ProducesResponseType(typeof(RoofCallerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<RoofCallerResponse> GetCaller()
    {
        var kind = RoofPrincipalFactory.GetCredentialKind(User) ?? RoofCredentialKind.ApiKey;
        var sessionId = User.FindFirst(RoofPrincipalFactory.SessionIdClaimType)?.Value;
        StoredSession? session = null;
        if (sessionId is not null)
        {
            _identity.TryGetLiveSession(sessionId, out session);
        }

        // A person: in the identity store, or an enrolled Linux account (its names are the operating system's). An API
        // key has no names, even one that shares a person's name.
        string? displayName = null, shortName = null, loginType = null;
        bool? hasPin = null;
        if (kind != RoofCredentialKind.ApiKey)
        {
            if (User.HasClaim(RoofPrincipalFactory.ProviderClaimType, "linux"))
            {
                var linux = _identity.LinuxForPrincipal(User.FindFirst(RoofPrincipalFactory.PrincipalIdClaimType)?.Value ?? string.Empty);
                (displayName, shortName, loginType) = (linux?.DisplayName, linux?.Name ?? User.Identity!.Name, RoofLoginTypes.Linux);
            }
            else if (_identity.TryGetUser(User.Identity!.Name, out var person))
            {
                (displayName, shortName, loginType) = (person.DisplayName, person.ShortName, RoofLoginTypes.Local);
            }

            // The PIN this person can change themselves (#205): the commissioned PIN a kiosk session was opened with, or
            // their own.
            hasPin = IsCommissionedPinSession() || (!IsLinuxOrLocal() && OwnPinUser() is not null);
        }

        return new RoofCallerResponse(
            User.Identity!.Name!,
            RoofPrincipalFactory.GetHighestRole(User)!,
            kind,
            User.FindFirst(RoofPrincipalFactory.DeviceClaimType)?.Value,
            sessionId,
            session?.ExpiresUtc,
            User.HasClaim(RoofPrincipalFactory.KioskClaimType, "true"))
        {
            DisplayName = displayName,
            ShortName = shortName,
            LoginType = loginType,
            HasPin = hasPin
        };
    }

    /// <summary>
    /// A person signed in with a password changes their own display and short name (#209). A null name is left as it
    /// is, an empty one removed. Names are edited where there is a keyboard (the web UI, the terminal): a PIN session at
    /// a kiosk is refused, and so is a Linux account, whose names come from the operating system.
    /// </summary>
    /// <response code="200">The caller, with the new names.</response>
    /// <response code="400">An invalid name, or the request used an API key.</response>
    /// <response code="403">A PIN session (<c>CredentialNotAllowed</c>).</response>
    /// <response code="409">A Linux account: its names are read-only here (<c>IdentityReadOnly</c>).</response>
    /// <response code="503">The identity store is unavailable.</response>
    [RoofRefusePinSession]
    [HttpPost("Names", Name = nameof(ChangeOwnNames))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme)]
    [ProducesResponseType(typeof(RoofCallerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<RoofCallerResponse> ChangeOwnNames([FromBody] RoofNamesChangeRequest request)
    {
        if (User.FindFirst(RoofPrincipalFactory.SessionIdClaimType)?.Value is null)
        {
            return RoofProblemResults.Create(
                this,
                RoofControllerErrorCode.InvalidRequest,
                "Only a signed-in person can change their names; this request used an API key.");
        }

        if (User.HasClaim(RoofPrincipalFactory.ProviderClaimType, "linux") || User.HasClaim(RoofPrincipalFactory.LocalClaimType, "true"))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.IdentityReadOnly, LinuxNamesAreReadOnly);
        }

        // Null leaves a name as it is; empty (or only spaces) removes it.
        var displayName = request.DisplayName is null ? null : RoofIdentityContract.NormalizeLabel(request.DisplayName) ?? string.Empty;
        var shortName = request.ShortName is null ? null : RoofIdentityContract.NormalizeLabel(request.ShortName) ?? string.Empty;
        if (displayName is { Length: > 0 } && !RoofIdentityContract.IsValidDisplayName(displayName))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest, IdentityController.DisplayNameRule);
        }

        if (shortName is { Length: > 0 } && !RoofIdentityContract.IsValidShortName(shortName))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest, IdentityController.ShortNameRule);
        }

        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        RoofIdentityResult<StoredUser> changed;
        try
        {
            changed = _identity.ChangeOwnNames(User.Identity!.Name!, displayName, shortName);
        }
        catch (RoofIdentityStoreException ex)
        {
            _logger.LogError("Name change for {Name} could not be saved: {Reason}", User.Identity!.Name, ex.Message);
            return StoreUnavailable();
        }

        if (!changed.Succeeded)
        {
            return RoofProblemResults.Create(this, changed.Error!.Value, changed.Detail!);
        }

        _logger.LogWarning("AUDIT {Name} changed their own names from {Remote}.", changed.Value!.Name, Remote);
        return GetCaller();
    }

    internal const string LinuxNamesAreReadOnly = "A Linux account's names come from the operating system: change the account's full name there.";

    /// <summary>Signs out: ends the session whose token was sent (sessions only).</summary>
    /// <response code="204">The session ended.</response>
    /// <response code="400">The request used an API key, which has no session to end.</response>
    [HttpDelete("Session", Name = nameof(EndSession))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult EndSession()
    {
        var sessionId = User.FindFirst(RoofPrincipalFactory.SessionIdClaimType)?.Value;
        if (sessionId is null)
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest, "Only a session can sign out; this request used an API key.");
        }

        try
        {
            // Already ended (by an admin, or a password change elsewhere) counts as signed out.
            _identity.EndSession(sessionId);
        }
        catch (RoofIdentityStoreException ex)
        {
            _logger.LogError("Sign-out of session {SessionId} could not be saved: {Reason}", sessionId, ex.Message);
            return StoreUnavailable();
        }

        _logger.LogInformation("AUDIT {Caller} signed out from {Remote}, session {SessionId}.", RoofPrincipalFactory.DescribeCaller(User), Remote, sessionId);
        return NoContent();
    }

    /// <summary>
    /// A signed-in person changes their own password (sessions only). Their other sessions end; this one stays open.
    /// </summary>
    /// <response code="204">The password changed.</response>
    /// <response code="400">The new password is too short or too long, or the request used an API key.</response>
    /// <response code="401">The current password is not correct (<c>SignInFailed</c>).</response>
    /// <response code="429">Locked out after repeated failures, or too many sign-ins at once.</response>
    /// <response code="503">The identity store is unavailable.</response>
    [RoofRefusePinSession]
    [HttpPost("Password", Name = nameof(ChangePassword))]
    [EnableRateLimiting(RoofSignInRateLimiting.PolicyName)]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ChangePassword([FromBody] RoofPasswordChangeRequest request, CancellationToken cancellationToken)
    {
        var sessionId = User.FindFirst(RoofPrincipalFactory.SessionIdClaimType)?.Value;
        if (sessionId is null)
        {
            return RoofProblemResults.Create(
                this,
                RoofControllerErrorCode.InvalidRequest,
                "Only a signed-in person can change their password; this request used an API key.");
        }

        if (User.HasClaim(RoofPrincipalFactory.ProviderClaimType, "linux") || User.HasClaim(RoofPrincipalFactory.LocalClaimType, "true"))
            return Forbid();

        if (!RoofIdentityContract.IsValidPassword(request.NewPassword))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest, PasswordRule);
        }

        if (request.CurrentPassword is not { Length: > 0 and <= RoofIdentityContract.MaximumPasswordLength })
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.SignInFailed, "The current password is not correct.");
        }

        var result = await _signIn.ChangePasswordAsync(
                User.Identity!.Name!,
                sessionId,
                request.CurrentPassword,
                request.NewPassword!,
                Remote,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded ? NoContent() : SignInProblem(result);
    }

    /// <summary>
    /// A signed-in person changes their own PIN (#205): with a PIN session at a kiosk, or with a password session (the
    /// web UI, the terminal, the CLI). The current PIN is always checked, and a wrong one counts towards the same lockouts
    /// as PIN sign-in (the name's PIN, and at a kiosk the kiosk). This session stays open, and so do the person's password
    /// sessions; their other PIN sessions, at every kiosk, end. A PIN session may change only its own PIN: a PIN works at
    /// a kiosk alone, so this creates no credential that works anywhere else. The first PIN is an admin's to give
    /// (<c>PUT Identity/Users/{name}/Pin</c>). A Linux account has no PIN of its own; the owner of the kiosk's
    /// commissioned PIN changes it at a kiosk, from a session opened with it.
    /// </summary>
    /// <response code="204">The PIN changed.</response>
    /// <response code="400">The new PIN is not four digits, is the reserved temporary PIN or the current one; the person has no PIN to change; or the request used an API key.</response>
    /// <response code="401">The current PIN is not correct (<c>SignInFailed</c>).</response>
    /// <response code="409">A Linux account signed in with its password: it has no PIN here (<c>IdentityReadOnly</c>).</response>
    /// <response code="429">Locked out after repeated failures, or too many sign-ins at once; see <c>Retry-After</c>.</response>
    /// <response code="503">The identity store is unavailable.</response>
    [HttpPost("Pin/Change", Name = nameof(ChangePin))]
    [EnableRateLimiting(RoofSignInRateLimiting.PolicyName)]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ChangePin([FromBody] RoofPinChangeRequest request, CancellationToken cancellationToken)
    {
        var sessionId = User.FindFirst(RoofPrincipalFactory.SessionIdClaimType)?.Value;
        if (sessionId is null)
        {
            return RoofProblemResults.Create(
                this,
                RoofControllerErrorCode.InvalidRequest,
                "Only a signed-in person can change their PIN; this request used an API key.");
        }

        if (!IsCommissionedPinSession() && IsLinuxOrLocal())
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.IdentityReadOnly, LinuxHasNoPin);
        }

        if (!RoofIdentityContract.IsValidPin(request.NewPin))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest, PinRule);
        }

        if (!RoofIdentityContract.IsAcceptedPin(request.CurrentPin))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.SignInFailed, RoofKioskSignInService.WrongCurrentPin);
        }

        if (string.Equals(request.CurrentPin, request.NewPin, StringComparison.Ordinal))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest, "Choose a new PIN different from your current one.");
        }

        if (!_identity.IsAvailable)
        {
            return StoreUnavailable();
        }

        var deviceKeyId = User.FindFirst(RoofPrincipalFactory.DeviceKeyIdClaimType)?.Value;
        var device = User.FindFirst(RoofPrincipalFactory.DeviceClaimType)?.Value;
        if (IsCommissionedPinSession())
        {
            RoofIdentityResult<int> changed;
            try
            {
                changed = await _kioskSignIn.ChangePinAsync(
                        User.FindFirst(RoofPrincipalFactory.PrincipalIdClaimType)!.Value,
                        deviceKeyId ?? string.Empty,
                        sessionId,
                        request.CurrentPin!,
                        request.NewPin!,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RoofIdentityStoreException ex)
            {
                _logger.LogError("Kiosk PIN change for {Name} could not be saved: {Reason}", User.Identity!.Name, ex.Message);
                return StoreUnavailable();
            }

            if (!changed.Succeeded)
            {
                _logger.LogWarning("SECURITY PIN change at {Device} for {Name} from {Remote} refused: {Reason}", device, User.Identity!.Name, Remote, changed.Detail);
                return RoofProblemResults.Create(this, changed.Error!.Value, changed.Detail!);
            }

            _logger.LogWarning(
                "AUDIT {Name} changed the kiosk's commissioned PIN (PIN session at {Device}) from {Remote}; {Ended} other PIN session(s) ended.",
                User.Identity!.Name, device, Remote, changed.Value);
            return NoContent();
        }

        if (OwnPinUser() is null)
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest, NoPinToChange);
        }

        var result = await _signIn.ChangePinAsync(
                User.Identity!.Name!,
                sessionId,
                RoofPrincipalFactory.GetCredentialKind(User) == RoofCredentialKind.Pin ? deviceKeyId : null,
                device,
                request.CurrentPin!,
                request.NewPin!,
                Remote,
                cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded ? NoContent() : SignInProblem(result);
    }

    internal const string LinuxHasNoPin =
        "A Linux account has no PIN of its own here. The kiosk's commissioned PIN is changed at a kiosk, after unlocking with it.";

    internal const string NoPinToChange = "You have no PIN to change. An admin gives you your first PIN.";

    internal static string PinRule => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"A PIN has exactly {RoofIdentityContract.MinimumPinLength} digits, and the temporary commissioning PIN is reserved.");

    /// <summary>A PIN session opened with the kiosk's commissioned PIN (<c>POST Auth/KioskPin</c>): it names a principal.</summary>
    private bool IsCommissionedPinSession()
        => RoofPrincipalFactory.GetCredentialKind(User) == RoofCredentialKind.Pin
            && User.FindFirst(RoofPrincipalFactory.PrincipalIdClaimType) is not null;

    private bool IsLinuxOrLocal()
        => User.HasClaim(RoofPrincipalFactory.ProviderClaimType, "linux") || User.HasClaim(RoofPrincipalFactory.LocalClaimType, "true");

    /// <summary>The signed-in person when they have a PIN of their own to change (or the emulator's temporary one), else null.</summary>
    private StoredUser? OwnPinUser()
        => _identity.TryGetUser(User.Identity?.Name, out var person)
            && person.Role != RoofControllerApiContract.ViewerRole
            && (person.PinHash is not null || _identity.CanUseTemporaryPin(person))
                ? person
                : null;

    internal static string PasswordRule => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"A password has {RoofIdentityContract.MinimumPasswordLength} to {RoofIdentityContract.MaximumPasswordLength} characters.");

    private bool TryGetKiosk(out RoofApiKeyIdentity kiosk, out ObjectResult refusal)
    {
        kiosk = null!;
        refusal = null!;
        if (User.HasClaim(RoofPrincipalFactory.KioskClaimType, "true")
            && _keys.TryFindByKeyId(User.FindFirst(RoofPrincipalFactory.KeyIdClaimType)?.Value, out var found)
            && found.Kiosk)
        {
            kiosk = found;
            return true;
        }

        refusal = RoofProblemResults.Create(
            this,
            RoofControllerErrorCode.KioskKeyRequired,
            "PIN sign-in needs a kiosk key in X-Api-Key; this key is not one.");
        return false;
    }

    private ActionResult SessionOrProblem(RoofSignInResult result)
    {
        if (!result.Succeeded)
        {
            return SignInProblem(result);
        }

        var session = result.Issued!.Session;
        // The token must not be kept by a cache or proxy.
        Response.Headers.CacheControl = "no-store";
        Response.Headers[HeaderNames.Pragma] = "no-cache";
        return Ok(new RoofSessionResponse(
            result.Issued.Token,
            session.Id,
            session.UserName,
            session.Kind == RoofCredentialKind.Pin && session.Role == RoofControllerApiContract.AdminRole ? RoofControllerApiContract.OperatorRole : session.Role,
            session.Kind,
            session.ExpiresUtc,
            session.Kind == RoofCredentialKind.Pin ? _options.CurrentValue.PinSessionIdleTimeout.TotalSeconds : null));
    }

    private ObjectResult SignInProblem(RoofSignInResult result)
        => RoofProblemResults.Create(this, result.Error!.Value, result.Detail!, result.RetryAfter);

    private ObjectResult StoreUnavailable()
        => RoofProblemResults.Create(
            this,
            RoofControllerErrorCode.IdentityStoreUnavailable,
            "The identity store is unavailable; see the controller log. API keys still work.",
            TimeSpan.FromSeconds(30));
}
