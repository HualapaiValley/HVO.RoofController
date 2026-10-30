using Asp.Versioning;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// The observatory's safety conditions (telescope park, weather) as the safety service judges them
/// (docs/safety-service.md). Every status snapshot carries the same thing in <c>Safety</c>; this reads it alone.
/// </summary>
[ApiController, ApiVersion("4.0")]
[Route("api/v{version:apiVersion}/Safety")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[Tags("Safety")]
public sealed class SafetyController : ControllerBase
{
    private readonly IRoofSafetyService _safety;

    public SafetyController(IRoofSafetyService safety)
    {
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
    }

    /// <summary>
    /// The safety conditions now: Safe, Unsafe or Unknown, the reasons, each source and its staleness, and the Open
    /// interlock's mode (Viewer).
    /// </summary>
    /// <response code="200">The safety conditions.</response>
    [HttpGet(Name = nameof(GetSafety))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.ViewerPolicy)]
    [ProducesResponseType(typeof(RoofSafetyStatus), StatusCodes.Status200OK)]
    public ActionResult<RoofSafetyStatus> GetSafety() => Ok(_safety.GetCurrent());
}
