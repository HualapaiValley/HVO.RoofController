using System;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Re-checks the signed-in console user of a live Blazor circuit against the API key store every minute, so removing
/// or rotating a key also ends interactive sessions that were opened with it (the cookie itself is revalidated on
/// every HTTP request by the cookie handler).
/// </summary>
public sealed class RoofConsoleAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly RoofApiKeyStore _keyStore;

    public RoofConsoleAuthenticationStateProvider(ILoggerFactory loggerFactory, RoofApiKeyStore keyStore)
        : base(loggerFactory)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
        => Task.FromResult(IsStillValid(_keyStore, authenticationState.User));

    /// <summary>True when the key that signed <paramref name="user"/> in still exists with the same name and role.</summary>
    public static bool IsStillValid(RoofApiKeyStore keyStore, ClaimsPrincipal? user)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        if (user?.Identity?.IsAuthenticated != true)
        {
            // Anonymous state has nothing to revoke.
            return true;
        }

        return keyStore.TryFindByKeyId(user.FindFirst(RoofPrincipalFactory.KeyIdClaimType)?.Value, out var identity)
            && string.Equals(identity.Name, user.Identity.Name, StringComparison.Ordinal)
            && string.Equals(identity.Role, RoofPrincipalFactory.GetHighestRole(user), StringComparison.Ordinal);
    }
}
