using System;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Re-checks the signed-in console user of a live Blazor circuit every minute, so removing or rotating a key (or ending
/// a session) also ends interactive sessions that were opened with it (the cookie itself is revalidated on every HTTP
/// request by the cookie handler).
/// </summary>
public sealed class RoofConsoleAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly RoofCredentialValidator _validator;

    public RoofConsoleAuthenticationStateProvider(ILoggerFactory loggerFactory, RoofCredentialValidator validator)
        : base(loggerFactory)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
        => Task.FromResult(_validator.IsStillValid(authenticationState.User));
}
