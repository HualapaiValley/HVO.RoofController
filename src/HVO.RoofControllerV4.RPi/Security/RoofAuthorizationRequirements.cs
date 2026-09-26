using System;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
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

/// <summary>
/// Camera stream policy: a Viewer principal (API key or console cookie), or a valid stream ticket for the camera in the
/// route (<c>?ticket=</c>, see <see cref="CameraStreamTicketService"/>).
/// </summary>
public sealed class CameraStreamRequirement : IAuthorizationRequirement
{
    /// <summary>Policy name (added by this change; not part of <see cref="RoofControllerSecurityDefaults"/>).</summary>
    public const string PolicyName = "RoofCameraStreamPolicy";

    /// <summary>Route value holding the camera id.</summary>
    public const string CameraIdRouteValue = "cameraId";
}

/// <summary>Evaluates <see cref="CameraStreamRequirement"/>.</summary>
public sealed class CameraStreamAuthorizationHandler : AuthorizationHandler<CameraStreamRequirement>
{
    private readonly CameraStreamTicketService _tickets;

    public CameraStreamAuthorizationHandler(CameraStreamTicketService tickets)
    {
        _tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CameraStreamRequirement requirement)
    {
        if (context.User.IsInRole(RoofControllerApiContract.ViewerRole))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.Resource is HttpContext http
            && http.Request.Query.TryGetValue(RoofControllerApiContract.CameraTicketQueryParameter, out var ticket)
            && ticket.Count == 1
            && http.GetRouteValue(CameraStreamRequirement.CameraIdRouteValue) is { } routeValue
            && int.TryParse(Convert.ToString(routeValue, System.Globalization.CultureInfo.InvariantCulture), out var cameraId)
            && _tickets.Validate(ticket[0], cameraId) == CameraStreamTicketValidation.Valid)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
