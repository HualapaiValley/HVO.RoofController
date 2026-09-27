using System;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Stop policy: any authenticated roof role may stop the roof; anonymous callers only when
/// <see cref="RoofControllerSecurityOptions.AllowAnonymousStop"/> is true.
/// </summary>
public sealed class RoofStopRequirement : IAuthorizationRequirement
{
}

/// <summary>Evaluates <see cref="RoofStopRequirement"/>.</summary>
public sealed class RoofStopAuthorizationHandler : AuthorizationHandler<RoofStopRequirement>
{
    private readonly IOptionsMonitor<RoofControllerSecurityOptions> _options;

    public RoofStopAuthorizationHandler(IOptionsMonitor<RoofControllerSecurityOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoofStopRequirement requirement)
    {
        if (context.User.IsInRole(RoofControllerApiContract.ViewerRole) || _options.CurrentValue.AllowAnonymousStop)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
