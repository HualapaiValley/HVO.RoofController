using System;
using System.Threading;
using System.Threading.Tasks;
using Asp.Versioning;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// The controller's version and the basic resource use of its machine, for every UI and every role (#209): a viewer, an
/// operator, an admin, a kiosk key or a PIN session. The detailed host information (<c>api/v1.0/System/info</c> and
/// <c>metrics</c>) and the restart stay admin-only.
/// </summary>
[ApiController, ApiVersion("4.0"), Produces("application/json")]
[Route("api/v{version:apiVersion}/System")]
[Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.ViewerPolicy)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
[Tags("System")]
public sealed class SystemSummaryController : ControllerBase
{
    private readonly RoofHostMetrics _metrics;

    public SystemSummaryController(RoofHostMetrics metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <summary>
    /// The controller's version and uptime, and its machine's CPU use, load and temperature, memory, data disk, addresses
    /// and uptime (Viewer). A value the controller could not read is null.
    /// </summary>
    /// <response code="200">The summary.</response>
    [HttpGet("Summary", Name = nameof(GetSystemSummary))]
    [ProducesResponseType(typeof(SystemSummaryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SystemSummaryResponse>> GetSystemSummary(CancellationToken cancellationToken)
        => await _metrics.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
}
