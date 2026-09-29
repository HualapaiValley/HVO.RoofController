using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace HVO.RoofControllerV4.Web.Sessions;

/// <summary>
/// The signed-in person as a live page sees them. The page is signed out as soon as their session ends (they signed out
/// on another page, or the controller refused the session: an admin ended it, their password changed, or it expired),
/// and the session is checked again every 30 seconds in case it expired while nothing was sent.
/// </summary>
public sealed class WebAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private static readonly AuthenticationState SignedOut = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly WebSessionStore _store;
    private readonly Lock _gate = new();
    private WebSession? _watched;

    public WebAuthenticationStateProvider(ILoggerFactory loggerFactory, WebSessionStore store)
        : base(loggerFactory)
    {
        _store = store;
        AuthenticationStateChanged += OnAuthenticationStateChanged;
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(30);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
        => Task.FromResult(_store.TryGet(WebAuthentication.GetSessionId(authenticationState.User), out _));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            AuthenticationStateChanged -= OnAuthenticationStateChanged;
            Watch(null);
        }

        base.Dispose(disposing);
    }

    private async void OnAuthenticationStateChanged(Task<AuthenticationState> task)
    {
        try
        {
            var state = await task;
            if (_store.TryGet(WebAuthentication.GetSessionId(state.User), out var session))
            {
                Watch(session);
            }
            else
            {
                Watch(null);
                if (state.User.Identity?.IsAuthenticated == true)
                {
                    SetAuthenticationState(Task.FromResult(SignedOut));
                }
            }
        }
        catch (Exception)
        {
            // A state that could not be had is a signed-out page; nothing to watch.
            Watch(null);
        }
    }

    private void Watch(WebSession? session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(session, _watched))
            {
                return;
            }

            if (_watched is not null)
            {
                _watched.Ended -= OnSessionEnded;
            }

            _watched = session;
            if (session is not null)
            {
                session.Ended += OnSessionEnded;
            }
        }

        // It may have ended before it was watched.
        if (session?.IsEnded == true)
        {
            OnSessionEnded(session, EventArgs.Empty);
        }
    }

    private void OnSessionEnded(object? sender, EventArgs e) => SetAuthenticationState(Task.FromResult(SignedOut));
}
