using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Asp.Versioning;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Controllers
{
    /// <summary>
    /// Roof Controller API v4.0 - controls the observatory roof. Accepts the <c>X-Api-Key</c> header or a session's
    /// <c>Authorization: Bearer</c> token (never a cookie), so these routes cannot be driven cross-site. Commands are POST and return the controller's
    /// coherent status snapshot; refusals are RFC 7807 ProblemDetails with <c>code</c> and <c>roofStatus</c> extensions.
    /// </summary>
    // No class-level [Produces]: it would override the application/problem+json content type of refusals.
    [ApiController, ApiVersion("4.0")]
    [Route("api/v{version:apiVersion}/RoofControl")]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [Tags("Roof Control")]
    public class RoofController : ControllerBase
    {
        private readonly ILogger<RoofController> _logger;
        private readonly IRoofControllerServiceV4 _roofController;
        private readonly IOptionsMonitor<RoofControllerHostOptionsV4> _hostOptions;
        private readonly IEnumerable<IValidateOptions<RoofControllerOptionsV4>> _configurationValidators;
        private readonly RoofSettingsStore _settings;

        public RoofController(
            ILogger<RoofController> logger,
            IRoofControllerServiceV4 roofController,
            IOptionsMonitor<RoofControllerHostOptionsV4> hostOptions,
            IEnumerable<IValidateOptions<RoofControllerOptionsV4>> configurationValidators,
            RoofSettingsStore settings)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _roofController = roofController ?? throw new ArgumentNullException(nameof(roofController));
            _hostOptions = hostOptions ?? throw new ArgumentNullException(nameof(hostOptions));
            _configurationValidators = configurationValidators ?? throw new ArgumentNullException(nameof(configurationValidators));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Gets the current status of the roof controller (Viewer).
        /// </summary>
        /// <response code="200">Coherent status snapshot.</response>
        [HttpGet("Status", Name = nameof(GetRoofStatus))]
        [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.ViewerPolicy)]
        [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
        public ActionResult<RoofStatusResponse> GetRoofStatus()
        {
            // Re-read the inputs so the snapshot reflects current hardware when input events are not flowing.
            _roofController.RefreshStatus(forceHardwareRead: true);
            return Ok(_roofController.GetCurrentStatusSnapshot());
        }

        /// <summary>
        /// Starts opening the roof (Operator).
        /// </summary>
        /// <response code="200">Motion started; status snapshot after the command.</response>
        /// <response code="409">Refused by an interlock (fault latched, limit, operation in progress).</response>
        /// <response code="503">Controller not ready (not initialized, shutting down, hardware or relay state unverified).</response>
        [HttpPost("Open", Name = nameof(DoRoofOpen))]
        [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.OperatorPolicy)]
        [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
        public ActionResult<RoofStatusResponse> DoRoofOpen()
            => RunCommand("open", () => _roofController.Open());

        /// <summary>
        /// Starts closing the roof (Operator).
        /// </summary>
        /// <response code="200">Motion started; status snapshot after the command.</response>
        /// <response code="409">Refused by an interlock (fault latched, limit, operation in progress).</response>
        /// <response code="503">Controller not ready (not initialized, shutting down, hardware or relay state unverified).</response>
        [HttpPost("Close", Name = nameof(DoRoofClose))]
        [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.OperatorPolicy)]
        [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
        public ActionResult<RoofStatusResponse> DoRoofClose()
            => RunCommand("close", () => _roofController.Close());

        /// <summary>
        /// Stops the roof (any authenticated key; anonymous only when RoofControllerSecurity:AllowAnonymousStop is true).
        /// </summary>
        /// <response code="200">Stop verified; status snapshot after the command.</response>
        /// <response code="503">The stop could not be verified (relay register unverified) or hardware is unavailable.</response>
        [HttpPost("Stop", Name = nameof(DoRoofStop))]
        [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.StopScheme, Policy = RoofControllerSecurityDefaults.StopPolicy)]
        [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
        public ActionResult<RoofStatusResponse> DoRoofStop()
            => RunCommand("stop", () => _roofController.Stop());

        /// <summary>
        /// Renews the operator lease while the roof is moving (Operator). Never starts motion.
        /// </summary>
        /// <response code="200">Lease renewed; status snapshot after the command.</response>
        /// <response code="409">No lease is active (roof not moving or lease disabled).</response>
        [HttpPost("Lease", Name = nameof(RenewRoofLease))]
        [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.OperatorPolicy)]
        [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
        public ActionResult<RoofStatusResponse> RenewRoofLease()
            => RunCommand("lease", () => _roofController.RenewLease());

        /// <summary>
        /// Pulses the clear-fault relay to reset the drive and, when inputs are healthy, clear a latched fault (Operator).
        /// </summary>
        /// <param name="pulseMs">Pulse length in milliseconds (50-2000, default 250).</param>
        /// <param name="cancellationToken">Request cancellation.</param>
        /// <response code="200">Pulse completed; status snapshot afterwards (check IsFaultLatched).</response>
        /// <response code="400">pulseMs out of range.</response>
        /// <response code="409">Refused (roof moving, another clear in progress).</response>
        [HttpPost("ClearFault", Name = nameof(DoClearFault))]
        [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.OperatorPolicy)]
        [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<RoofStatusResponse>> DoClearFault(
            [FromQuery, Range(RoofControllerLimits.MinClearFaultPulseMilliseconds, RoofControllerLimits.MaxClearFaultPulseMilliseconds)]
            int pulseMs = RoofControllerLimits.DefaultClearFaultPulseMilliseconds,
            CancellationToken cancellationToken = default)
        {
            LogCommand("clear_fault");
            var startTimestamp = Stopwatch.GetTimestamp();
            using var activity = RoofControllerTelemetry.StartCommand("clear_fault");
            var result = await _roofController.ClearFault(pulseMs, cancellationToken).ConfigureAwait(false);
            RoofControllerTelemetry.CompleteCommand(activity, "clear_fault", result.IsSuccessful, startTimestamp);

            return result.IsSuccessful
                ? Ok(_roofController.GetCurrentStatusSnapshot())
                : RoofProblem(result.Error, "clear_fault");
        }

        /// <summary>
        /// Retrieves the configuration applied to the roof controller service (Admin). The Version is the settings version
        /// (<c>GET Settings</c>): any settings change moves it.
        /// </summary>
        /// <response code="200">Configuration snapshot including its Version.</response>
        [HttpGet("Configuration", Name = nameof(GetRoofConfiguration))]
        [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.AdminPolicy)]
        [ProducesResponseType(typeof(RoofConfigurationResponse), StatusCodes.Status200OK)]
        public ActionResult<RoofConfigurationResponse> GetRoofConfiguration()
        {
            var version = _settings.Version;
            return Ok(CreateConfigurationResponse(_roofController.GetConfigurationSnapshot(), version));
        }

        /// <summary>
        /// Replaces the remotely editable configuration (Admin). Every field but ConfirmSafetyCriticalChange must be sent,
        /// ExpectedVersion must match the current version, and changes to relay mapping, limit/fault polarity or
        /// IgnorePhysicalLimitSwitches, or turning off the operator lease or the IN4 interlock, also need
        /// ConfirmSafetyCriticalChange=true. Refused while the roof is moving. The same change as <c>POST Settings/roof</c>:
        /// it is saved to the settings file, and the local-only roof settings are left as they are.
        /// </summary>
        /// <response code="200">Configuration applied; the new configuration and version.</response>
        /// <response code="400">Missing or invalid values.</response>
        /// <response code="409">Version conflict, unconfirmed safety-critical change, or refused by the controller.</response>
        [HttpPost("Configuration", Name = nameof(UpdateRoofConfiguration))]
        [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.AdminPolicy)]
        [ProducesResponseType(typeof(RoofConfigurationResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
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

            var caller = RoofPrincipalFactory.DescribeCaller(User);
            var currentVersion = _settings.Version;
            var currentOptions = _roofController.GetConfigurationSnapshot();
            if (request.ExpectedVersion != currentVersion)
            {
                return RoofProblem(
                    new RoofControllerException(
                        RoofControllerErrorCode.ConfigurationVersionConflict,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"The configuration changed since it was read (expected version {request.ExpectedVersion}, current version {currentVersion}). Reload and retry.")),
                    "update_configuration");
            }

            var updatedOptions = request.ToOptions(currentOptions);
            var changes = DescribeChanges(currentOptions, updatedOptions);
            var safetyCritical = request.ChangesSafetyCriticalSettings(currentOptions);
            if (safetyCritical && !request.ConfirmSafetyCriticalChange)
            {
                _logger.LogWarning(
                    "Configuration update by {Caller} rejected: safety-critical change not confirmed ({Changes})",
                    caller,
                    changes);
                return RoofProblem(
                    new RoofControllerException(
                        RoofControllerErrorCode.ConfigurationRejected,
                        "This change affects relay mapping, limit-switch or fault polarity, or ignoring limit switches, " +
                        "or turns off the operator lease or the IN4 interlock. " +
                        "Check the wiring, or that the lease or the IN4 interlock should be off, " +
                        "then resend with ConfirmSafetyCriticalChange=true."),
                    "update_configuration");
            }

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

            var result = _settings.UpdateRoofFromAlias(updatedOptions, request.ExpectedVersion!.Value, User);
            if (result.ValidationErrors is { } errors)
            {
                foreach (var failure in errors)
                {
                    ModelState.AddModelError(nameof(RoofConfigurationRequest), failure);
                }

                return ValidationProblem(ModelState);
            }

            if (result.Error is { } code)
            {
                _logger.LogWarning(
                    "Configuration update by {Caller} refused: {Error}",
                    caller,
                    result.Detail);
                return RoofProblem(new RoofControllerException(code, result.Detail ?? "The configuration change was refused."), "update_configuration");
            }

            _logger.Log(
                safetyCritical ? LogLevel.Warning : LogLevel.Information,
                "AUDIT configuration updated by {Caller} (version {OldVersion} -> {NewVersion}, safety-critical: {SafetyCritical}): {Changes}",
                caller,
                currentVersion,
                result.Version,
                safetyCritical,
                changes);

            return Ok(CreateConfigurationResponse(_roofController.GetConfigurationSnapshot(), result.Version));
        }

        private ActionResult<RoofStatusResponse> RunCommand<T>(string command, Func<Result<T>> execute)
        {
            LogCommand(command);
            var startTimestamp = Stopwatch.GetTimestamp();
            using var activity = RoofControllerTelemetry.StartCommand(command);
            var result = execute();
            RoofControllerTelemetry.CompleteCommand(activity, command, result.IsSuccessful, startTimestamp);

            return result.IsSuccessful
                ? Ok(_roofController.GetCurrentStatusSnapshot())
                : RoofProblem(result.Error, command);
        }

        private void LogCommand(string command)
        {
            _logger.LogInformation(
                "Roof command {Command} requested by {Caller} from {RemoteIp}",
                command,
                RoofPrincipalFactory.DescribeCaller(User),
                HttpContext.Connection.RemoteIpAddress);
        }

        /// <summary>
        /// Maps a failure to ProblemDetails: <see cref="RoofControllerException"/> codes use
        /// <see cref="RoofControllerApiContract.HttpStatusFor"/>; anything else is 500 <c>Unknown</c> with a generic detail.
        /// </summary>
        private ObjectResult RoofProblem(Exception? error, string operation)
        {
            RoofControllerErrorCode code;
            string detail;
            RoofStatusResponse? snapshot;

            if (error is RoofControllerException roofError)
            {
                code = roofError.Code;
                detail = roofError.Message;
                snapshot = roofError.Snapshot ?? TryGetSnapshot();
                _logger.LogInformation("Roof {Operation} refused: {Code} {Detail}", operation, code, detail);
            }
            else
            {
                code = RoofControllerErrorCode.Unknown;
                detail = $"An unexpected error occurred during '{operation}'. See the controller log.";
                snapshot = TryGetSnapshot();
                _logger.LogError(error, "Roof {Operation} failed unexpectedly", operation);
            }

            var status = RoofControllerApiContract.HttpStatusFor(code);
            var problem = Problem(
                statusCode: status,
                title: TitleFor(code),
                type: RoofControllerApiContract.ProblemType(code),
                detail: detail);

            if (problem.Value is ProblemDetails details)
            {
                details.Extensions[RoofControllerApiContract.ProblemCodeExtension] = code.ToString();
                if (snapshot is not null)
                {
                    details.Extensions[RoofControllerApiContract.ProblemStatusExtension] = snapshot;
                }
            }

            if (status == StatusCodes.Status503ServiceUnavailable)
            {
                Response.Headers.RetryAfter = "2";
            }

            return problem;
        }

        private RoofStatusResponse? TryGetSnapshot()
        {
            try
            {
                return _roofController.GetCurrentStatusSnapshot();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not capture a status snapshot for a problem response");
                return null;
            }
        }

        private static string TitleFor(RoofControllerErrorCode code) => code switch
        {
            RoofControllerErrorCode.NotInitialized => "Roof controller not initialized",
            RoofControllerErrorCode.ShuttingDown => "Roof controller shutting down",
            RoofControllerErrorCode.HardwareUnavailable => "Roof hardware unavailable",
            RoofControllerErrorCode.RelayStateUnverified => "Relay state unverified",
            RoofControllerErrorCode.FaultLatched => "Fault latched",
            RoofControllerErrorCode.InterlockActive => "Interlock active",
            RoofControllerErrorCode.OperationInProgress => "Operation in progress",
            RoofControllerErrorCode.LeaseNotActive => "No active lease",
            RoofControllerErrorCode.ConfigurationVersionConflict => "Configuration version conflict",
            RoofControllerErrorCode.ConfigurationRejected => "Configuration rejected",
            RoofControllerErrorCode.InvalidRequest => "Invalid request",
            _ => "Roof controller error"
        };

        private RoofConfigurationResponse CreateConfigurationResponse(RoofControllerOptionsV4 options, long version)
        {
            var host = _hostOptions.CurrentValue;

            return new RoofConfigurationResponse
            {
                Version = version,
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
                FaultInputActiveHigh = options.FaultInputActiveHigh,
                MaxConsecutiveInputReadFailures = options.MaxConsecutiveInputReadFailures,
                OperatorLeaseTimeoutSeconds = options.OperatorLeaseTimeout?.TotalSeconds,
                AtSpeedConfirmationTimeoutSeconds = options.AtSpeedConfirmationTimeout?.TotalSeconds,
                DriveStopConfirmationTimeoutSeconds = options.DriveStopConfirmationTimeout?.TotalSeconds,
                DepartureReleaseTimeoutSeconds = options.DepartureReleaseTimeout?.TotalSeconds,
                AllowIgnoringLimitSwitchesOnPhysicalHardware = options.AllowIgnoringLimitSwitchesOnPhysicalHardware,
                RestartOnFailureWaitTimeSeconds = host.RestartOnFailureWaitTime
            };
        }

        /// <summary>Human-readable list of changed API-editable settings for the audit log ("none" when unchanged).</summary>
        internal static string DescribeChanges(RoofControllerOptionsV4 before, RoofControllerOptionsV4 after)
        {
            var changes = new List<string>();
            Compare(nameof(RoofControllerOptionsV4.SafetyWatchdogTimeout), before.SafetyWatchdogTimeout, after.SafetyWatchdogTimeout);
            Compare(nameof(RoofControllerOptionsV4.OpenRelayId), before.OpenRelayId, after.OpenRelayId);
            Compare(nameof(RoofControllerOptionsV4.CloseRelayId), before.CloseRelayId, after.CloseRelayId);
            Compare(nameof(RoofControllerOptionsV4.ClearFaultRelayId), before.ClearFaultRelayId, after.ClearFaultRelayId);
            Compare(nameof(RoofControllerOptionsV4.StopRelayId), before.StopRelayId, after.StopRelayId);
            Compare(nameof(RoofControllerOptionsV4.EnableDigitalInputPolling), before.EnableDigitalInputPolling, after.EnableDigitalInputPolling);
            Compare(nameof(RoofControllerOptionsV4.DigitalInputPollInterval), before.DigitalInputPollInterval, after.DigitalInputPollInterval);
            Compare(nameof(RoofControllerOptionsV4.EnablePeriodicVerificationWhileMoving), before.EnablePeriodicVerificationWhileMoving, after.EnablePeriodicVerificationWhileMoving);
            Compare(nameof(RoofControllerOptionsV4.PeriodicVerificationInterval), before.PeriodicVerificationInterval, after.PeriodicVerificationInterval);
            Compare(nameof(RoofControllerOptionsV4.UseNormallyClosedLimitSwitches), before.UseNormallyClosedLimitSwitches, after.UseNormallyClosedLimitSwitches);
            Compare(nameof(RoofControllerOptionsV4.LimitSwitchDebounce), before.LimitSwitchDebounce, after.LimitSwitchDebounce);
            Compare(nameof(RoofControllerOptionsV4.IgnorePhysicalLimitSwitches), before.IgnorePhysicalLimitSwitches, after.IgnorePhysicalLimitSwitches);
            Compare(nameof(RoofControllerOptionsV4.FaultInputActiveHigh), before.FaultInputActiveHigh, after.FaultInputActiveHigh);
            Compare(nameof(RoofControllerOptionsV4.MaxConsecutiveInputReadFailures), before.MaxConsecutiveInputReadFailures, after.MaxConsecutiveInputReadFailures);
            Compare(nameof(RoofControllerOptionsV4.OperatorLeaseTimeout), before.OperatorLeaseTimeout, after.OperatorLeaseTimeout);
            Compare(nameof(RoofControllerOptionsV4.AtSpeedConfirmationTimeout), before.AtSpeedConfirmationTimeout, after.AtSpeedConfirmationTimeout);
            return changes.Count == 0 ? "none" : string.Join("; ", changes);

            void Compare<T>(string name, T oldValue, T newValue)
            {
                if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
                {
                    changes.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {oldValue?.ToString() ?? "null"} -> {newValue?.ToString() ?? "null"}"));
                }
            }
        }
    }
}
