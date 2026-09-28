using System;
using System.Globalization;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// RFC 7807 answers for the sign-in and identity endpoints, shaped like the roof's: the <c>type</c> URI and the
/// <c>code</c> extension name the <see cref="RoofControllerErrorCode"/>, and 429 and 503 carry <c>Retry-After</c>.
/// </summary>
internal static class RoofProblemResults
{
    public static ObjectResult Create(ControllerBase controller, RoofControllerErrorCode code, string detail, TimeSpan? retryAfter = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var status = RoofControllerApiContract.HttpStatusFor(code);
        var result = controller.Problem(
            statusCode: status,
            title: TitleFor(code),
            type: RoofControllerApiContract.ProblemType(code),
            detail: detail);

        if (result.Value is ProblemDetails details)
        {
            details.Extensions[RoofControllerApiContract.ProblemCodeExtension] = code.ToString();
        }

        if (retryAfter is { } wait)
        {
            var seconds = Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds));
            controller.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }
        else if (status is StatusCodes.Status429TooManyRequests or StatusCodes.Status503ServiceUnavailable)
        {
            controller.Response.Headers.RetryAfter = "2";
        }

        return result;
    }

    private static string TitleFor(RoofControllerErrorCode code) => code switch
    {
        RoofControllerErrorCode.SignInFailed => "Sign-in failed",
        RoofControllerErrorCode.SignInLockedOut => "Sign-in locked",
        RoofControllerErrorCode.SignInBusy => "Sign-in busy",
        RoofControllerErrorCode.KioskKeyRequired => "Kiosk key required",
        RoofControllerErrorCode.IdentityNotFound => "Not found",
        RoofControllerErrorCode.IdentityNameConflict => "Name already in use",
        RoofControllerErrorCode.IdentityReadOnly => "Defined in configuration",
        RoofControllerErrorCode.LastAdministrator => "Last admin credential",
        RoofControllerErrorCode.IdentityStoreUnavailable => "Identity store unavailable",
        RoofControllerErrorCode.InvalidRequest => "Invalid request",
        _ => "Roof controller error"
    };
}
