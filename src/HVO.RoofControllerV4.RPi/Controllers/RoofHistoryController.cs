using System;
using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// The roof command history (#206): the Open, Close, Stop and Clear fault commands the controller was sent, accepted or
/// refused, newest first. An admin reads everyone's; anyone else reads only their own (a kiosk PIN session included,
/// which is never an admin's).
/// </summary>
[ApiController, ApiVersion("4.0")]
[Route("api/v{version:apiVersion}/RoofControl")]
[Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.ViewerPolicy)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Tags("Roof Control")]
public sealed class RoofHistoryController : ControllerBase
{
    private readonly RoofCommandHistory _history;

    public RoofHistoryController(RoofCommandHistory history)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    /// <summary>
    /// A page of the roof command history, newest first (Viewer). An admin reads everyone's commands, or one person's or
    /// key's with <paramref name="user"/>; anyone else reads their own, and naming someone else is refused (403).
    /// </summary>
    /// <param name="user">Only commands sent by, or on behalf of, this name (case-insensitive).</param>
    /// <param name="from">Only commands at or after this time (ISO 8601).</param>
    /// <param name="to">Only commands before this time (ISO 8601).</param>
    /// <param name="skip">Entries to skip, newest first.</param>
    /// <param name="take">Entries in the page (1-500, default 50).</param>
    /// <response code="200">The page, with how many entries match in all.</response>
    /// <response code="400">A value out of range, or <paramref name="to"/> before <paramref name="from"/>.</response>
    /// <response code="403">Not an admin, and <paramref name="user"/> names someone else.</response>
    [HttpGet(RoofCommandHistoryContract.Route, Name = nameof(GetRoofHistory))]
    [ProducesResponseType(typeof(RoofCommandHistoryPage), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<RoofCommandHistoryPage> GetRoofHistory(
        [FromQuery] string? user = null,
        [FromQuery] DateTimeOffset? from = null,
        [FromQuery] DateTimeOffset? to = null,
        [FromQuery, Range(0, int.MaxValue)] int skip = 0,
        [FromQuery, Range(1, RoofCommandHistoryContract.MaximumTake)] int take = RoofCommandHistoryContract.DefaultTake)
    {
        if (from is { } start && to is { } end && end < start)
        {
            ModelState.AddModelError(nameof(to), "'to' must not be before 'from'.");
            return ValidationProblem(ModelState);
        }

        user = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        RoofCommandHistoryOwner? owner = null;
        if (!User.IsInRole(RoofControllerApiContract.AdminRole))
        {
            owner = RoofCommandHistory.OwnerOf(User);
            if (owner is null)
            {
                return Forbid();
            }

            if (user is not null && !RoofCommandHistory.SameName(user, owner.Name))
            {
                return Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Not your history",
                    detail: "Only an admin can read another person's or key's roof commands. Leave the user out to read your own.");
            }

            // Their own commands are the filter; the name adds nothing.
            user = null;
        }

        return Ok(_history.Query(new RoofCommandHistoryFilter
        {
            Owner = owner,
            User = user,
            From = from,
            To = to,
            Skip = skip,
            Take = take
        }));
    }
}
