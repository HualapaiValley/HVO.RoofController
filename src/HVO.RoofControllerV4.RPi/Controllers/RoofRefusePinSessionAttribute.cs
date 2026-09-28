using System;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// Refuses PIN sessions with 403 <c>CredentialNotAllowed</c>. A PIN is short and typed where others can see it, so it
/// must not be able to create credentials that work from anywhere and outlive the session (API keys, passwords, PINs).
/// Runs after authorization, so a caller without the role still gets a plain 403 first, and before the request body is
/// validated, so a PIN session learns nothing from a 400.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RoofRefusePinSessionAttribute : Attribute, IActionFilter, IOrderedFilter
{
    /// <summary>Before <c>[ApiController]</c>'s model-state filter (order -2000).</summary>
    public int Order => -3000;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (RoofPrincipalFactory.GetCredentialKind(context.HttpContext.User) == RoofCredentialKind.Pin
            && context.Controller is ControllerBase controller)
        {
            context.Result = RoofProblemResults.Create(
                controller,
                RoofControllerErrorCode.CredentialNotAllowed,
                "A PIN session cannot manage people, API keys or sessions. Use an admin API key, or sign in with a password.");
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}
