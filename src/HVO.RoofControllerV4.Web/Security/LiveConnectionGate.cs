using HVO.RoofControllerV4.Web.Sessions;

namespace HVO.RoofControllerV4.Web.Security;

/// <summary>
/// Only a signed-in person may open a live connection (<c>/_blazor</c>): page authorization protects the first render,
/// and this closes the connection endpoint itself, so nobody can open live pages without signing in. The public list of
/// the connection's start-up scripts (<c>/_blazor/initializers</c>) is exempt. So pages that must work before sign-in
/// are static. Runs after authentication.
/// </summary>
public sealed class LiveConnectionGate(RequestDelegate next)
{
    private static readonly PathString BlazorPath = new("/_blazor");
    private static readonly PathString InitializersPath = new("/_blazor/initializers");

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments(BlazorPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(InitializersPath, StringComparison.OrdinalIgnoreCase)
            || context.User.IsInRole(WebRoles.Viewer))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
