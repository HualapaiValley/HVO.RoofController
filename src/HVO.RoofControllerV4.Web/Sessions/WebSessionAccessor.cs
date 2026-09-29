using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>The signed-in person's session, for a page: its client of the controller is the one the page uses.</summary>
public sealed class WebSessionAccessor(AuthenticationStateProvider authentication, WebSessionStore store)
{
    /// <summary>The page's open session, or null when the page is signed out or the session has ended.</summary>
    public async Task<WebSession?> GetAsync()
    {
        var state = await authentication.GetAuthenticationStateAsync();
        return store.TryGet(WebAuthentication.GetSessionId(state.User), out var session) ? session : null;
    }
}
