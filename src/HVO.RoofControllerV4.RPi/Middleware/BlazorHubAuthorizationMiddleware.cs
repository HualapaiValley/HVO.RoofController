using System;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Http;

namespace HVO.RoofControllerV4.RPi.Middleware;

/// <summary>
/// Requires a signed-in console user (console cookie, any roof role) before a Blazor Server circuit can be negotiated
/// or connected on <c>/_blazor</c>. Page-level <c>[Authorize]</c> protects prerendering; this closes the hub itself so
/// an anonymous client cannot open interactive circuits. Only the public <c>/_blazor/initializers</c> list is exempt.
/// Consequence: pages that must work before login (such as <c>/login</c>) must be static server-rendered.
/// Must run after <c>UseAuthentication</c>.
/// </summary>
public sealed class BlazorHubAuthorizationMiddleware
{
    private static readonly PathString BlazorPath = new("/_blazor");
    private static readonly PathString InitializersPath = new("/_blazor/initializers");

    private readonly RequestDelegate _next;

    public BlazorHubAuthorizationMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments(BlazorPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(InitializersPath, StringComparison.OrdinalIgnoreCase)
            || context.User.IsInRole(RoofControllerApiContract.ViewerRole))
        {
            return _next(context);
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
