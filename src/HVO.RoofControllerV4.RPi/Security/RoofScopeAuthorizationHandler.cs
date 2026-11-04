using System;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>Additional scope check; role checks still apply independently.</summary>
public sealed record RoofScopeRequirement(string Scope) : IAuthorizationRequirement;

/// <summary>Scopes restrict personal automation keys and ephemeral local grants. Legacy credentials are unchanged.</summary>
public sealed class RoofScopeAuthorizationHandler : AuthorizationHandler<RoofScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoofScopeRequirement requirement)
    {
        var claims = context.User.FindAll(RoofPrincipalFactory.ScopeClaimType).Select(claim => claim.Value).ToArray();
        // Only server-issued scoped credentials carry this marker. They may never omit a scope.
        var scoped = context.User.HasClaim(RoofPrincipalFactory.OwnedKeyClaimType, "true") || context.User.HasClaim(RoofPrincipalFactory.LocalClaimType, "true");
        if (!scoped) { context.Succeed(requirement); return Task.CompletedTask; }
        var scope = requirement.Scope;
        if (context.Resource is HttpContext http)
        {
            var path = http.Request.Path.Value ?? "";
            if (path.Contains("/Identity/", StringComparison.OrdinalIgnoreCase))
            {
                if (context.User.HasClaim(RoofPrincipalFactory.OwnedKeyClaimType, "true")) return Task.CompletedTask;
                scope = "identity";
            }
            else if (HttpMethods.IsGet(http.Request.Method)) scope = "read";
            else if (path.Contains("/Settings/", StringComparison.OrdinalIgnoreCase)) scope = "configure";
        }
        if (claims.Contains(scope, StringComparer.Ordinal)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
