using System;
using HVO.Core.Results;
using System.Collections.Generic;
using System.Linq;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Controllers
{
    /// <summary>
    /// Roof Controller API v4.0 - Controls the observatory roof operations
    /// </summary>
    [ApiController, ApiVersion("4.0"), Produces("application/json")]
    [Route("api/v{version:apiVersion}/RoofControl")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [Tags("Roof Control")]
    public class RoofController : ControllerBase
    {
        private readonly ILogger<RoofController> _logger;
        private readonly IRoofControllerServiceV4 _roofController;
        private readonly IOptionsMonitor<RoofControllerHostOptionsV4> _hostOptions;
        private readonly IEnumerable<IValidateOptions<RoofControllerOptionsV4>> _configurationValidators;

        public RoofController(
            ILogger<RoofController> logger,
            IRoofControllerServiceV4 roofController,
            IOptionsMonitor<RoofControllerHostOptionsV4> hostOptions,
            IEnumerable<IValidateOptions<RoofControllerOptionsV4>> configurationValidators)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _roofController = roofController ?? throw new ArgumentNullException(nameof(roofController));
            _hostOptions = hostOptions ?? throw new ArgumentNullException(nameof(hostOptions));
            _configurationValidators = configurationValidators ?? throw new ArgumentNullException(nameof(configurationValidators));
        }

        /// <summary>
        /// Gets the current status of the roof controller
        /// </summary>
        /// <returns>Current roof controller status</returns>
        /// <response code="200">Returns the current roof status</response>
        /// <response code="500">Internal server error occurred</response>
        [HttpGet, Route("Status", Name = nameof(GetRoofStatus))]
        [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public ActionResult<RoofStatusResponse> GetRoofStatus()
        {
            _roofController.RefreshStatus(forceHardwareRead: true);
            return Ok(CreateStatus());
        }

        /// <summary>
        /// Retrieves the current configuration applied to the roof controller service.
        /// </summary>
        /// <returns>Current controller configuration values.</returns>
        /// <response code="200">Returns the configuration snapshot.</response>
        [HttpGet, Route("Configuration", Name = nameof(GetRoofConfiguration))]
        [ProducesResponseType(typeof(RoofConfigurationResponse), StatusCodes.Status200OK)]
        public ActionResult<RoofConfigurationResponse> GetRoofConfiguration()
        {
            var snapshot = _roofController.GetConfigurationSnapshot();
            return Ok(CreateConfigurationResponse(snapshot));
        }

        /// <summary>
        /// Applies a configuration update to the roof controller service.
        /// </summary>
        /// <param name="request">The desired configuration values.</param>
        /// <returns>The effective configuration after applying the update.</returns>
        /// <response code="200">Configuration updated successfully.</response>
        /// <response code="400">Validation failed for one or more configuration values.</response>
        /// <response code="500">Internal server error or service state issue occurred.</response>
        [HttpPost, Route("Configuration", Name = nameof(UpdateRoofConfiguration))]
        [ProducesResponseType(typeof(RoofConfigurationResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public ActionResult<RoofConfigurationResponse> UpdateRoofConfiguration([FromBody] RoofConfigurationRequest? request)
        {
            if (request is null)
            {
                ModelState.AddModelError(string.Empty, "Request body is required.");
                return ValidationProblem(ModelState);
            }

            if (!ModelState.IsValid)
            {
                return ValidationProblem(ModelState);
            }

            var updatedOptions = new RoofControllerOptionsV4
            {
                SafetyWatchdogTimeout = TimeSpan.FromSeconds(request.SafetyWatchdogTimeoutSeconds),
                OpenRelayId = request.OpenRelayId,
                CloseRelayId = request.CloseRelayId,
                ClearFaultRelayId = request.ClearFaultRelayId,
                StopRelayId = request.StopRelayId,
                EnableDigitalInputPolling = request.EnableDigitalInputPolling,
                DigitalInputPollInterval = TimeSpan.FromMilliseconds(request.DigitalInputPollIntervalMilliseconds),
                EnablePeriodicVerificationWhileMoving = request.EnablePeriodicVerificationWhileMoving,
                PeriodicVerificationInterval = TimeSpan.FromSeconds(request.PeriodicVerificationIntervalSeconds),
                UseNormallyClosedLimitSwitches = request.UseNormallyClosedLimitSwitches,
                LimitSwitchDebounce = TimeSpan.FromMilliseconds(request.LimitSwitchDebounceMilliseconds),
                IgnorePhysicalLimitSwitches = request.IgnorePhysicalLimitSwitches
            };

            var validationFailures = _configurationValidators
                .Select(validator => validator.Validate(Options.DefaultName, updatedOptions))
                .Where(result => result is { Failed: true })
                .SelectMany(result => result.Failures ?? Array.Empty<string>())
                .Distinct()
                .ToArray();

            if (validationFailures.Length > 0)
            {
                foreach (var failure in validationFailures)
                {
                    ModelState.AddModelError(nameof(RoofConfigurationRequest), failure);
                }

                return ValidationProblem(ModelState);
            }

            var result = _roofController.UpdateConfiguration(updatedOptions);

            return result.Match(
                success: options => Ok(CreateConfigurationResponse(options)),
                failure: error => error switch
                {
                    InvalidOperationException => Problem(
                        title: "Service Error",
                        detail: error.Message,
                        statusCode: StatusCodes.Status500InternalServerError),
                    _ => Problem(
                        title: "Internal Server Error",
                        detail: "An error occurred while updating configuration",
                        statusCode: StatusCodes.Status500InternalServerError)
                });
        }

        /// <summary>
        /// Opens the observatory roof
        /// </summary>
        /// <returns>Updated roof controller status after opening operation</returns>
        /// <response code="200">Roof opening operation completed successfully</response>
        /// <response code="500">Internal server error or service state issue occurred</response>
        [HttpGet, Route("Open", Name = nameof(DoRoofOpen))]
        [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public ActionResult<RoofStatusResponse> DoRoofOpen()
        {
            var result = this._roofController.Open();
            
            return result.Match(
                success: status => Ok(CreateStatus(status)),
                failure: error => error switch
                {
                    // Service state issues should return 500
                    InvalidOperationException => Problem(
                        title: "Service Error",
                        detail: error.Message,
                        statusCode: StatusCodes.Status500InternalServerError
                    ),
                    _ => Problem(
                        title: "Internal Server Error", 
                        detail: "An error occurred while opening the roof",
                        statusCode: StatusCodes.Status500InternalServerError
                    )
                }
            );
        }

        /// <summary>
        /// Closes the observatory roof
        /// </summary>
        /// <returns>Updated roof controller status after closing operation</returns>
        /// <response code="200">Roof closing operation completed successfully</response>
        /// <response code="500">Internal server error or service state issue occurred</response>
        [HttpGet, Route("Close", Name = nameof(DoRoofClose))]
        [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public ActionResult<RoofStatusResponse> DoRoofClose()
        {
            var result = this._roofController.Close();
            
            return result.Match(
                success: status => Ok(CreateStatus(status)),
                failure: error => error switch
                {
                    // Service state issues should return 500
                    InvalidOperationException => Problem(
                        title: "Service Error",
                        detail: error.Message,
                        statusCode: StatusCodes.Status500InternalServerError
                    ),
                    _ => Problem(
                        title: "Internal Server Error", 
                        detail: "An error occurred while closing the roof",
                        statusCode: StatusCodes.Status500InternalServerError
                    )
                }
            );
        }

        /// <summary>
        /// Stops the current roof operation
        /// </summary>
        /// <returns>Updated roof controller status after stop operation</returns>
        /// <response code="200">Roof stop operation completed successfully</response>
        /// <response code="500">Internal server error or service state issue occurred</response>
        [HttpGet, Route("Stop", Name = nameof(DoRoofStop))]
        [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public ActionResult<RoofStatusResponse> DoRoofStop()
        {
            var result = this._roofController.Stop();
            
            return result.Match(
                success: status => Ok(CreateStatus(status)),
                failure: error => error switch
                {
                    // Service state issues should return 500
                    InvalidOperationException => Problem(
                        title: "Service Error",
                        detail: error.Message,
                        statusCode: StatusCodes.Status500InternalServerError
                    ),
                    _ => Problem(
                        title: "Internal Server Error", 
                        detail: "An error occurred while stopping the roof",
                        statusCode: StatusCodes.Status500InternalServerError
                    )
                }
            );
        }

        /// <summary>
        /// Clears controller/motor fault by pulsing the clear-fault relay.
        /// </summary>
        /// <param name="pulseMs">Pulse duration in milliseconds</param>
        /// <returns>True when the pulse completed</returns>
        /// <response code="200">Fault clear pulse issued successfully</response>
        /// <response code="500">Internal server error or service state issue occurred</response>
        [HttpPost, Route("ClearFault", Name = nameof(DoClearFault))]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<bool>> DoClearFault([FromQuery] int pulseMs = 250, CancellationToken cancellationToken = default)
        {
            var result = await this._roofController.ClearFault(pulseMs, cancellationToken).ConfigureAwait(false); // ClearFaultRelayId used internally
            return result.Match(
                success: ok => Ok(ok),
                failure: error => error switch
                {
                    InvalidOperationException => Problem(
                        title: "Service Error",
                        detail: error.Message,
                        statusCode: StatusCodes.Status500InternalServerError
                    ),
                    _ => Problem(
                        title: "Internal Server Error",
                        detail: "An error occurred while clearing fault",
                        statusCode: StatusCodes.Status500InternalServerError
                    )
                }
            );
        }
        private RoofStatusResponse CreateStatus(RoofControllerStatus? overrideStatus = null)
        {
            // Force a refresh so AtSpeed/Run input is current
            this._roofController.RefreshStatus(forceHardwareRead: false);
            return new RoofStatusResponse(
                overrideStatus ?? this._roofController.Status,
                this._roofController.IsMoving,
                this._roofController.LastStopReason,
                this._roofController.LastTransitionUtc,
                this._roofController.IsWatchdogActive,
                this._roofController.WatchdogSecondsRemaining,
                this._roofController.IsAtSpeed,
                this._roofController.IsUsingPhysicalHardware,
                this._roofController.IsIgnoringPhysicalLimitSwitches);
        }

        private RoofConfigurationResponse CreateConfigurationResponse(RoofControllerOptionsV4 options)
        {
            var host = _hostOptions.CurrentValue;

            return new RoofConfigurationResponse
            {
                SafetyWatchdogTimeoutSeconds = options.SafetyWatchdogTimeout.TotalSeconds,
                OpenRelayId = options.OpenRelayId,
                CloseRelayId = options.CloseRelayId,
                ClearFaultRelayId = options.ClearFaultRelayId,
                StopRelayId = options.StopRelayId,
                EnableDigitalInputPolling = options.EnableDigitalInputPolling,
                DigitalInputPollIntervalMilliseconds = options.DigitalInputPollInterval.TotalMilliseconds,
                EnablePeriodicVerificationWhileMoving = options.EnablePeriodicVerificationWhileMoving,
                PeriodicVerificationIntervalSeconds = options.PeriodicVerificationInterval.TotalSeconds,
                UseNormallyClosedLimitSwitches = options.UseNormallyClosedLimitSwitches,
                LimitSwitchDebounceMilliseconds = options.LimitSwitchDebounce.TotalMilliseconds,
                IgnorePhysicalLimitSwitches = options.IgnorePhysicalLimitSwitches,
                RestartOnFailureWaitTimeSeconds = host.RestartOnFailureWaitTime
            };
        }

        
    }
}
