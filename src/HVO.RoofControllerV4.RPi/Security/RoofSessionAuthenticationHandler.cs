using System;
using System.Text.Encodings.Web;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>Options for <see cref="RoofSessionAuthenticationHandler"/> (no settings; sessions come from the identity store).</summary>
public sealed class RoofSessionAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Authenticates requests carrying <c>Authorization: Bearer &lt;session token&gt;</c>. No such header yields no result; an
/// unknown, ended or expired token fails and is logged at Warning with the remote address (never the token). Each
/// accepted request counts as activity for a PIN session's idle timeout. Challenges return 401 with
/// <c>WWW-Authenticate: Bearer</c> (and <c>error="invalid_token"</c> when a token was refused); forbidden returns 403.
/// </summary>
public sealed class RoofSessionAuthenticationHandler : AuthenticationHandler<RoofSessionAuthenticationOptions>
{
    private readonly RoofCredentialValidator _validator;

    public RoofSessionAuthenticationHandler(
        IOptionsMonitor<RoofSessionAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        RoofCredentialValidator validator)
        : base(options, logger, encoder)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>True when <paramref name="request"/> carries <c>Authorization: Bearer ...</c>.</summary>
    public static bool HasBearerToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.Headers.Authorization;
        return header.Count > 0
            && header[0] is { } value
            && value.StartsWith(RoofIdentityContract.BearerScheme + " ", StringComparison.OrdinalIgnoreCase);
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!HasBearerToken(Request))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var header = Request.Headers.Authorization;
        if (header.Count != 1)
        {
            LogRejected("more than one Authorization header");
            return Task.FromResult(AuthenticateResult.Fail("Malformed Authorization header."));
        }

        var token = header[0]![(RoofIdentityContract.BearerScheme.Length + 1)..].Trim();
        if (!_validator.TryAuthenticateSession(token, touch: true, out var session, out var failure))
        {
            LogRejected(failure ?? "invalid session");
            return Task.FromResult(AuthenticateResult.Fail("Invalid or ended session."));
        }

        var principal = RoofPrincipalFactory.CreateForSession(session, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync().ConfigureAwait(false);
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(
            HeaderNames.WWWAuthenticate,
            result.Failure is null ? RoofIdentityContract.BearerScheme : RoofIdentityContract.BearerScheme + " error=\"invalid_token\"");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private void LogRejected(string reason)
    {
        Logger.LogWarning(
            "Rejected session token ({Reason}) from {RemoteIp} for {Method} {Path}",
            reason,
            Context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Request.Method,
            Request.Path.Value);
    }
}
