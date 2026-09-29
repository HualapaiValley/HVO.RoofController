using System;
using Asp.Versioning;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// Restarts the controller (#42) so that settings read only at startup take effect. The roof is stopped and the stop
/// verified first; the controller then shuts down as on SIGTERM and exits with
/// <see cref="RoofSettingsContract.RestartExitCode"/>, and whatever supervises it (Docker's restart policy, systemd)
/// starts it again.
/// </summary>
[ApiController, ApiVersion("4.0")]
[Route("api/v{version:apiVersion}/System")]
[Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.AdminPolicy)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
[Tags("System Administration")]
public sealed class SystemRestartController : ControllerBase
{
    private readonly IRoofControllerServiceV4 _roof;
    private readonly RoofSettingsStore _settings;
    private readonly RoofRestartSignal _signal;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SystemRestartController> _logger;

    public SystemRestartController(
        IRoofControllerServiceV4 roof,
        RoofSettingsStore settings,
        RoofRestartSignal signal,
        IHostApplicationLifetime lifetime,
        ILogger<SystemRestartController> logger)
    {
        _roof = roof ?? throw new ArgumentNullException(nameof(roof));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Stops the roof, verifies the stop, and restarts the controller (Admin). The body is optional; it must confirm a
    /// pending hand edit with a safety-critical change, which the restart would load.
    /// </summary>
    /// <response code="202">The roof is stopped and verified; the controller is restarting.</response>
    /// <response code="409">The stop could not be verified, or a pending hand edit cannot be loaded as it is.</response>
    [HttpPost("Restart", Name = nameof(RestartController))]
    [ProducesResponseType(typeof(RoofRestartResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public ActionResult<RoofRestartResponse> RestartController([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] RoofRestartRequest? request)
    {
        var caller = RoofPrincipalFactory.DescribeCaller(User);
        if (_settings.CheckRestart(request ?? new RoofRestartRequest(), User) is { } refusal)
        {
            _logger.LogWarning("Restart by {Caller} refused: {Reason}", caller, refusal);
            return RoofProblemResults.Create(this, RoofControllerErrorCode.RestartRefused, refusal);
        }

        var stop = _roof.Stop(RoofControllerStopReason.HostShutdown);
        if (!stop.IsSuccessful)
        {
            _logger.LogWarning("Restart by {Caller} refused: the roof stop could not be verified ({Error})", caller, stop.Error?.Message);
            return RoofProblemResults.Create(
                this,
                RoofControllerErrorCode.RestartRefused,
                "The roof stop could not be verified, so the controller was not restarted: " +
                (stop.Error?.Message ?? "unknown error") + " Check the controller and the relays before restarting it.");
        }

        _logger.LogWarning(
            "AUDIT controller restart requested by {Caller}: roof stopped and verified; exiting with code {ExitCode}",
            caller,
            RoofSettingsContract.RestartExitCode);

        // After the answer is sent: the host then stops the roof again (ShutdownAsync) and refuses new commands.
        Response.OnCompleted(() =>
        {
            _signal.Request();
            _lifetime.StopApplication();
            return System.Threading.Tasks.Task.CompletedTask;
        });

        return StatusCode(
            StatusCodes.Status202Accepted,
            new RoofRestartResponse(
                "The roof is stopped. The controller is restarting; it answers again once it has started.",
                RoofSettingsContract.RestartExitCode));
    }
}
