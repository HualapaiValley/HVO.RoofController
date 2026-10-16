using System.Threading;
using System.Threading.Tasks;
using Asp.Versioning;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>Setup tokens are body-only credentials accepted here with an independently authenticated kiosk key.</summary>
[ApiController, ApiVersion("4.0")]
[Route(RoofIdentityContract.AuthRoute)]
[Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiKeyScheme)]
public sealed class KioskCommissioningController(RoofIdentityStore identity, RoofApiKeyStore keys,
    RoofKioskSignInService signIn, RoofProviderAuthService providers, RoofSecretHasher hasher) : ControllerBase
{
    private RoofApiKeyIdentity? Kiosk()
        => User.HasClaim(RoofPrincipalFactory.KioskClaimType, "true")
            && keys.TryFindByKeyId(User.FindFirst(RoofPrincipalFactory.KeyIdClaimType)?.Value, out var key) && key.Kiosk ? key : null;

    [HttpPost("KioskPin")]
    [EnableRateLimiting(RoofSignInRateLimiting.PolicyName)]
    public async Task<IActionResult> SignIn(RoofKioskPinRequest request, CancellationToken cancellationToken)
    {
        var kiosk = Kiosk();
        if (kiosk is null) return Forbid();
        if (request.PrincipalId is not { Length: > 0 and <= 64 } || !RoofIdentityContract.IsAcceptedPin(request.Pin)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        var result = await signIn.SignInAsync(kiosk, request, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? Ok(result.Value) : RoofProblemResults.Create(this, result.Error!.Value, result.Detail!);
    }

    [HttpPost("KioskPin/Complete")]
    [EnableRateLimiting(RoofSignInRateLimiting.PolicyName)]
    public async Task<IActionResult> Complete(RoofKioskSetupRequest request, CancellationToken cancellationToken)
    {
        var kiosk = Kiosk();
        if (kiosk is null) return Forbid();
        if (request.SetupToken is not { Length: > 0 and <= 128 } || !RoofIdentityContract.IsValidPin(request.NewPin)
            || request.NewPin == "0000" || request.NewPin != request.ConfirmPin) return BadRequest();
        var credential = identity.GetKioskCredential();
        if (credential is null || !providers.KioskPrincipalIsCurrent(credential.PrincipalId)) return Unauthorized();
        var hash = await hasher.HashAsync(request.NewPin, cancellationToken).ConfigureAwait(false);
        if (hash is null) return StatusCode(429);
        if (!providers.KioskPrincipalIsCurrent(credential.PrincipalId)) return Unauthorized();
        var result = identity.CompleteKioskSetup(request.SetupToken, kiosk.KeyId, hash);
        return result.Succeeded ? NoContent() : Unauthorized();
    }
}
