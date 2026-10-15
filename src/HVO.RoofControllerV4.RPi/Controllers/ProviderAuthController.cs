using System;
using System.Linq;
using Asp.Versioning;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>Opt-in provider authentication and personal scoped automation keys.</summary>
[ApiController, ApiVersion("4.0"), Route(RoofIdentityContract.AuthRoute)]
public sealed class ProviderAuthController(RoofProviderAuthService providers, RoofIdentityStore identity, ILogger<ProviderAuthController> logger) : ControllerBase
{
    [HttpGet("Providers"), AllowAnonymous]
    public RoofAuthProvidersResponse Providers() => new(providers.Enabled, providers.Enabled ? ["virtual", "linux"] : ["virtual"]);

    [HttpPost("ProviderSession"), AllowAnonymous, EnableRateLimiting(RoofSignInRateLimiting.PolicyName)]
    public async Task<IActionResult> ProviderSession(RoofProviderSignInRequest request, CancellationToken cancellationToken)
    {
        if (!providers.Enabled) return NotFound();
        try { return Session(await providers.PasswordAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", cancellationToken).ConfigureAwait(false)); }
        catch (RoofIdentityStoreException) { return StatusCode(503); }
    }

    [HttpPost("LocalSession"), AllowAnonymous, EnableRateLimiting(RoofSignInRateLimiting.PolicyName)]
    public IActionResult LocalSession(RoofLocalSignInRequest request)
    {
        if (!providers.Enabled) return NotFound();
        try { return Session(providers.Exchange(request.Assertion, "local-admin")); }
        catch (RoofIdentityStoreException) { return StatusCode(503); }
    }

    [HttpGet("Keys"), Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.SessionScheme)]
    public IActionResult Keys()
    {
        if (!providers.Enabled) return NotFound();
        var owner = OwnerSession();
        return owner is null ? Forbid() : Ok(identity.OwnedKeysFor(owner.PrincipalId!).Select(KeyResponse).ToArray());
    }

    [HttpPost("Keys"), Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.SessionScheme)]
    public IActionResult CreateKey(RoofOwnedKeyCreateRequest request)
    {
        if (!providers.Enabled) return NotFound();
        var owner = OwnerSession();
        if (owner is null) return Forbid();
        try
        {
            var secret = RoofIdentityStore.NewApiKey();
            var result = identity.CreateOwnedKey(owner.PrincipalId!, owner.PrincipalStamp!, request, RoofIdentityStore.HashHex(secret));
            if (!result.Succeeded) return RoofProblemResults.Create(this, result.Error!.Value, result.Detail!);
            logger.LogWarning("AUDIT Principal {PrincipalId} created personal key {KeyId} with scopes {Scopes}.", owner.PrincipalId, result.Value!.Id, string.Join(",", result.Value.Scopes));
            Response.Headers.CacheControl = "no-store";
            return StatusCode(201, new RoofOwnedKeySecretResponse(KeyResponse(result.Value!), secret));
        }
        catch (RoofIdentityStoreException) { return StatusCode(503); }
    }

    [HttpDelete("Keys/{id}"), Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.SessionScheme)]
    public IActionResult RevokeKey(string id)
    {
        if (!providers.Enabled) return NotFound();
        var owner = OwnerSession();
        if (owner is null) return Forbid();
        try
        {
            var result = identity.RevokeOwnedKey(owner.PrincipalId!, id);
            if (result.Succeeded) logger.LogWarning("AUDIT Principal {PrincipalId} revoked personal key {KeyId}.", owner.PrincipalId, id);
            return result.Succeeded ? NoContent() : RoofProblemResults.Create(this, result.Error!.Value, result.Detail!);
        }
        catch (RoofIdentityStoreException) { return StatusCode(503); }
    }

    private StoredSession? OwnerSession()
        => identity.TryGetLiveSession(User.FindFirst(RoofPrincipalFactory.SessionIdClaimType)?.Value, out var session)
            && session.Kind == RoofCredentialKind.Session && session.PrincipalId is not null && session.Scope is null ? session : null;

    private IActionResult Session(RoofIdentityResult<RoofIssuedSession> result)
    {
        if (!result.Succeeded)
            return result.Error == RoofControllerErrorCode.IdentityReadOnly
                ? StatusCode(403)
                : RoofProblemResults.Create(this, result.Error!.Value, result.Detail!);
        var value = result.Value!;
        var session = value.Session;
        logger.LogInformation("AUDIT Provider {Provider} principal {PrincipalId} opened session {SessionId} for scope {Scope}.",
            session.Provider, session.PrincipalId, session.Id, session.Scope ?? "interactive");
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return Ok(new RoofSessionResponse(value.Token, session.Id, session.UserName, session.Role, session.Kind, session.ExpiresUtc, null));
    }

    private static RoofOwnedKeyResponse KeyResponse(StoredOwnedKey key)
        => new(key.Id, key.Name, key.OwnerId, key.Scopes, key.ExpiresUtc, key.Revoked);
}

/// <summary>Explicit controller-side Linux enrollment, separate from native administrator permission.</summary>
[ApiController, ApiVersion("4.0"), Route(RoofIdentityContract.IdentityRoute)]
[Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.AdminPolicy)]
[RoofRefusePinSession]
public sealed class ProviderIdentityController(RoofIdentityStore identity, ILogger<ProviderIdentityController> logger) : ControllerBase
{
    [HttpGet("Principals")]
    public IActionResult Principals() => identity.ProviderEnabled ? Ok(identity.ProviderPrincipals) : NotFound();

    [HttpPost("Principals")]
    public IActionResult Enroll(RoofPrincipalEnrollRequest request) => Change(() => identity.EnrollLinux(request));

    [HttpPut("Principals/{id}")]
    public IActionResult Update(string id, RoofPrincipalUpdateRequest request) => Change(() => identity.UpdateLinux(id, request));

    private IActionResult Change(Func<RoofIdentityResult<RoofPrincipalResponse>> action)
    {
        if (!identity.ProviderEnabled) return NotFound();
        try
        {
            var result = action();
            if (result.Succeeded) logger.LogWarning("AUDIT {Caller} saved Linux enrollment {PrincipalId} role {Role}, enabled {Enabled}.",
                RoofPrincipalFactory.DescribeCaller(User), result.Value!.Id, result.Value.Role, result.Value.Enabled);
            return result.Succeeded ? Ok(result.Value) : RoofProblemResults.Create(this, result.Error!.Value, result.Detail!);
        }
        catch (RoofIdentityStoreException) { return StatusCode(503); }
    }
}
