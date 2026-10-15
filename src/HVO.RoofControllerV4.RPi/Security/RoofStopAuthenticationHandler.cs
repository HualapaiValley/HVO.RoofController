using System;
using System.Text.Encodings.Web;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>
/// Authenticates Stop (<see cref="RoofControllerSecurityDefaults.StopScheme"/>). A request with a valid session token
/// is the person's; when the token is refused (the session ended, idled out or expired) and an <c>X-Api-Key</c> came
/// with it, the key is tried, so a kiosk or UI that sends both can always stop with its own key. Everywhere else a
/// refused token is never replaced by a key (<see cref="RoofControllerSecurityDefaults.ApiScheme"/>). Challenges come
/// from the scheme whose answer was used.
/// </summary>
public sealed class RoofStopAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    // The scheme whose answer was used (one handler serves one request).
    private string? _answeredBy;
    private readonly RoofApiKeyStore? _keys;
    private readonly Identity.RoofIdentityStore? _identity;

    public RoofStopAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        RoofApiKeyStore? keys = null, Identity.RoofIdentityStore? identity = null)
        : base(options, logger, encoder)
    {
        _keys = keys;
        _identity = identity;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (RoofSessionAuthenticationHandler.HasBearerToken(Request))
        {
            // A separate live kiosk/configured credential can stop immediately. Do not wait for a native
            // worker to revalidate a Linux bearer before using that independent authority.
            // Audit remains the key's identity; no Linux person is asserted without the native check.
            var bearer = Request.Headers.Authorization;
            var keyHeader = Request.Headers[RoofControllerApiContract.ApiKeyHeaderName];
            if (bearer.Count == 1 && keyHeader.Count == 1
                && _keys?.TryValidate(keyHeader[0], out _) == true
                && _identity?.TryValidateToken(bearer[0]![(RoofIdentityContract.BearerScheme.Length + 1)..].Trim(), false, out var candidate, out _) == true
                && candidate!.Provider == "linux")
            {
                _answeredBy = RoofControllerSecurityDefaults.ApiKeyScheme;
                return await Context.AuthenticateAsync(_answeredBy).ConfigureAwait(false);
            }

            var session = await Context.AuthenticateAsync(RoofControllerSecurityDefaults.SessionScheme).ConfigureAwait(false);
            if (session.Succeeded || !HasApiKey)
            {
                _answeredBy = RoofControllerSecurityDefaults.SessionScheme;
                return session;
            }
        }

        _answeredBy = RoofControllerSecurityDefaults.ApiKeyScheme;
        return await Context.AuthenticateAsync(RoofControllerSecurityDefaults.ApiKeyScheme).ConfigureAwait(false);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        => Context.ChallengeAsync(AnsweringScheme, properties);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        => Context.ForbidAsync(AnsweringScheme, properties);

    private bool HasApiKey => Request.Headers.ContainsKey(RoofControllerApiContract.ApiKeyHeaderName);

    private string AnsweringScheme => _answeredBy ?? (RoofSessionAuthenticationHandler.HasBearerToken(Request) && !HasApiKey
        ? RoofControllerSecurityDefaults.SessionScheme
        : RoofControllerSecurityDefaults.ApiKeyScheme);
}
