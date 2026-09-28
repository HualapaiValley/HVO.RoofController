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
    private readonly RoofIdentityStore _identity;
    private readonly RoofApiKeyStore _keys;
    private readonly IOptionsMonitor<RoofIdentityOptions> _options;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        RoofSignInService signIn,
        RoofIdentityStore identity,
        RoofApiKeyStore keys,
        IOptionsMonitor<RoofIdentityOptions> options,
        ILogger<AuthController> logger)
    {
        _signIn = signIn ?? throw new ArgumentNullException(nameof(signIn));
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

        if (!RoofIdentityContract.IsValidName(request.Name) || request.Pin is not { Length: > 0 and <= RoofIdentityContract.MaximumPinLength })
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.SignInFailed, RoofSignInService.WrongCredentials);
        }

        var result = await _signIn.SignInWithPinAsync(kiosk, request.Name!, request.Pin, Remote, cancellationToken).ConfigureAwait(false);
        return SessionOrProblem(result);
    }

    /// <summary>The people who can sign in with a PIN, for a kiosk's picker (the kiosk's key in <c>X-Api-Key</c>).</summary>
    /// <response code="200">Names and roles, sorted by name. Nothing secret.</response>
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

        return _identity.Users
            .Where(user => user.PinHash is not null && user.Role != RoofControllerApiContract.ViewerRole)
            .OrderBy(user => user.Name, StringComparer.OrdinalIgnoreCase)
            .Select(user => new RoofPinUserResponse(user.Name, user.Role))
            .ToArray();
    }

    /// <summary>Who the caller is: the person or API key, its role and how it signed in (any credential).</summary>
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

        return new RoofCallerResponse(
            User.Identity!.Name!,
            RoofPrincipalFactory.GetHighestRole(User)!,
            kind,
            User.FindFirst(RoofPrincipalFactory.DeviceClaimType)?.Value,
            sessionId,
            session?.ExpiresUtc,
            User.HasClaim(RoofPrincipalFactory.KioskClaimType, "true"));
    }

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
            session.Role,
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
