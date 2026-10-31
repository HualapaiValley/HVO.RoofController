namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// Keeps the address of each signed-in person's browser on their session: every request to the web UI (a page, the live
/// connection, Stop) sets it from the address that connected, so the commands sent for the person name it to the
/// controller (<see cref="RoofWebCredential.ForwardFor"/>). Runs after authentication.
/// </summary>
public sealed class WebForwardedFor(RequestDelegate next, WebSessionStore store)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (store.TryGet(WebAuthentication.GetSessionId(context.User), out var session))
        {
            session.ForwardFor(context.Connection.RemoteIpAddress);
        }

        return next(context);
    }
}
