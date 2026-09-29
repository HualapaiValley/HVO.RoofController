using System;
using Asp.Versioning;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// Remote configuration (#42): every setting in the catalogue, read by role and changed one group at a time. Changes are
/// saved to the settings file (<c>appsettings.Local.json</c>; secrets to the managed secrets file) and take effect at
/// once unless the catalogue says a restart is needed. Every change is logged as an <c>AUDIT</c> line naming the caller.
/// A PIN session is accepted: the kiosk changes local-only settings with an admin PIN.
/// </summary>
[ApiController, ApiVersion("4.0")]
[Route(RoofSettingsContract.SettingsRoute)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Tags("Settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly RoofSettingsStore _store;

    public SettingsController(RoofSettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>Describes the settings the caller may read: type, limits, roles, safety and default (Viewer).</summary>
    [HttpGet("Catalogue", Name = nameof(GetSettingsCatalogue))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.ViewerPolicy)]
    [ProducesResponseType(typeof(RoofSettingsCatalogueResponse), StatusCodes.Status200OK)]
    public ActionResult<RoofSettingsCatalogueResponse> GetSettingsCatalogue() => _store.GetCatalogue(User);

    /// <summary>
    /// The settings the caller may read, with the version to send back, where each value comes from, whether the caller
    /// may change it, and (for admins) any edit made to the file outside the API (Viewer).
    /// </summary>
    [HttpGet(Name = nameof(GetSettings))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.ViewerPolicy)]
    [ProducesResponseType(typeof(RoofSettingsResponse), StatusCodes.Status200OK)]
    public ActionResult<RoofSettingsResponse> GetSettings() => _store.Get(User);

    /// <summary>
    /// Replaces one group's settings (Operator for <c>ui</c>, Admin for the rest). Send every setting of the group you
    /// may change, and the version you read; a safety-critical change also needs ConfirmSafetyCriticalChange=true.
    /// </summary>
    /// <response code="200">Saved and applied; the settings after the change.</response>
    /// <response code="400">Missing, unknown or invalid values.</response>
    /// <response code="403">A setting the caller may not change (role, local credential, or set by a higher layer).</response>
    /// <response code="404">No such group.</response>
    /// <response code="409">Version conflict, pending hand edit, unconfirmed safety-critical change, or the roof is moving.</response>
    /// <response code="503">The settings could not be saved; nothing was changed.</response>
    [HttpPost("{group}", Name = nameof(UpdateSettings))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.OperatorPolicy)]
    [ProducesResponseType(typeof(RoofSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<RoofSettingsResponse> UpdateSettings(string group, [FromBody] RoofSettingsUpdateRequest? request)
    {
        if (request is null)
        {
            ModelState.AddModelError(string.Empty, "Request body is required.");
            return ValidationProblem(ModelState);
        }

        return ToResult(_store.Update(group, request, User));
    }

    /// <summary>
    /// Applies the pending edit made to the settings file outside the API, checked like a change through the API (Admin).
    /// Send the edit's token; a safety-critical edit also needs ConfirmSafetyCriticalChange=true, and an edit to a
    /// local-only setting needs a local credential.
    /// </summary>
    [HttpPost("Reload", Name = nameof(ReloadSettings))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.AdminPolicy)]
    [ProducesResponseType(typeof(RoofSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<RoofSettingsResponse> ReloadSettings([FromBody] RoofSettingsHandEditRequest? request)
    {
        if (request is null)
        {
            ModelState.AddModelError(string.Empty, "Request body is required.");
            return ValidationProblem(ModelState);
        }

        return ToResult(_store.Reload(request, User));
    }

    /// <summary>Overwrites the pending hand edit with the settings in effect (Admin). Send the edit's token.</summary>
    [HttpPost("Discard", Name = nameof(DiscardSettingsEdit))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.AdminPolicy)]
    [ProducesResponseType(typeof(RoofSettingsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<RoofSettingsResponse> DiscardSettingsEdit([FromBody] RoofSettingsHandEditRequest? request)
    {
        if (request is null)
        {
            ModelState.AddModelError(string.Empty, "Request body is required.");
            return ValidationProblem(ModelState);
        }

        return ToResult(_store.Discard(request, User));
    }

    private ActionResult<RoofSettingsResponse> ToResult(RoofSettingsOutcome outcome)
    {
        if (outcome.ValidationErrors is { } errors)
        {
            foreach (var error in errors)
            {
                ModelState.AddModelError(string.Empty, error);
            }

            return ValidationProblem(ModelState);
        }

        if (outcome.Error is { } code)
        {
            return RoofProblemResults.Create(this, code, outcome.Detail ?? "The settings request was refused.");
        }

        return outcome.Response!;
    }
}
