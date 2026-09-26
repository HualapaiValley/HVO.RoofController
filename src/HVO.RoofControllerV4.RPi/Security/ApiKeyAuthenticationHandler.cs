using System;
using System.Text.Encodings.Web;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HVO.RoofControllerV4.RPi.Security;

/// <summary>Options for <see cref="ApiKeyAuthenticationHandler"/> (no settings; keys come from <see cref="RoofApiKeyStore"/>).</summary>
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Authenticates requests carrying <c>X-Api-Key</c>. A missing header yields no result (anonymous); a wrong key fails
/// and is logged at Warning with the remote address (never the key). Challenges return 401 with
/// <c>WWW-Authenticate: ApiKey</c>; forbidden returns 403. Never redirects.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly RoofApiKeyStore _keyStore;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        RoofApiKeyStore keyStore)
        : base(options, logger, encoder)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RoofControllerApiContract.ApiKeyHeaderName, out var values))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            LogRejected("malformed header");
            return Task.FromResult(AuthenticateResult.Fail("Malformed API key header."));
        }

        if (!_keyStore.TryValidate(values[0], out var identity))
        {
            LogRejected(_keyStore.HasKeys ? "unknown key" : "no API keys are configured");
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var principal = RoofPrincipalFactory.Create(identity, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(HeaderNames.WWWAuthenticate, RoofControllerApiContract.ApiKeyScheme);
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private void LogRejected(string reason)
    {
        Logger.LogWarning(
            "Rejected API key ({Reason}) from {RemoteIp} for {Method} {Path}",
            reason,
            Context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Request.Method,
            Request.Path.Value);
    }
}
