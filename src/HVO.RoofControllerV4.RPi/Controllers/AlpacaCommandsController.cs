using System.Diagnostics;
using Asp.Versioning;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Controllers;

/// <summary>
/// The controller's Alpaca command surface (#220, phases 3 and 4), under <c>api/v4.0/alpaca</c>:
/// <list type="bullet">
/// <item>the External Alpaca account's ticket, Close and Open (its role, from a trusted Alpaca server address; every other
/// account 403);</item>
/// <item>the Alpaca server's own refusals (its own account only);</item>
/// <item>the person-only routes: override grants, clearing the human Stop hold, the operator lease, acknowledging an
/// alert (a person's session; every API key 403); and the reads of grants and alerts (Viewer).</item>
/// </list>
/// Refusals are ProblemDetails with <c>code</c>, <c>alpacaRefusal</c>, <c>reasonKeys</c> and <c>roofStatus</c>.
/// </summary>
[ApiController, ApiVersion("4.0")]
[Route("api/v{version:apiVersion}/alpaca")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[Tags("Alpaca commands")]
public sealed class AlpacaCommandsController : ControllerBase
{
    private readonly RoofAlpacaCommands _alpaca;
    private readonly IRoofControllerServiceV4 _controller;
    private readonly IRoofSafetyService _safety;
    private readonly RoofCommandHistory _history;
    private readonly IOptionsMonitor<RoofAlpacaCommandOptions> _options;
    private readonly IOptionsMonitor<RoofSafetyOptions> _safetyOptions;
    private readonly ILogger<AlpacaCommandsController> _logger;

    public AlpacaCommandsController(
        RoofAlpacaCommands alpaca,
        IRoofControllerServiceV4 controller,
        IRoofSafetyService safety,
        RoofCommandHistory history,
        IOptionsMonitor<RoofAlpacaCommandOptions> options,
        IOptionsMonitor<RoofSafetyOptions> safetyOptions,
        ILogger<AlpacaCommandsController> logger)
    {
        _alpaca = alpaca;
        _controller = controller;
        _safety = safety;
        _history = history;
        _options = options;
        _safetyOptions = safetyOptions;
        _logger = logger;
    }

    /// <summary>
    /// A single-use ticket for one Alpaca command (External Alpaca account): the controller's instance, its command
    /// generation, and the answer's deadline. The command must carry it within 5 s, and only a command of its kind may.
    /// </summary>
    /// <param name="kind">The command the ticket is for: <c>Close</c> or <c>Open</c>.</param>
    [HttpPost("shutter/ticket", Name = nameof(IssueAlpacaTicket))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.AlpacaCommanderPolicy)]
    [AlpacaCommanderAllowed, AlpacaCommandAuthReport]
    [ProducesResponseType(typeof(RoofAlpacaTicket), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<RoofAlpacaTicket> IssueAlpacaTicket([FromQuery] string? kind)
    {
        if (!Enum.TryParse<RoofAlpacaCommandKind>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(kind, out _))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest, "kind must be Close or Open.");
        }

        var outcome = _alpaca.IssueTicket(parsed, User, Client());
        if (outcome.Ticket is { } ticket)
        {
            Response.Headers.CacheControl = "no-store";
            return Ok(ticket);
        }

        return Problem(outcome.Refusal!, "alpaca_ticket", outcome.StatusCode);
    }

    /// <summary>
    /// An Alpaca client's Close (External Alpaca account), carrying its ticket: admitted under the controller's lock with
    /// the Alpaca rules (commands on, no person holding the operator lease, the Stop hold clear, every blocking telescope
    /// clear or an override grant covering every reason, the controller's own protections). Never queued: a refusal is
    /// final, and the client sends a new command.
    /// </summary>
    [HttpPost("shutter/close", Name = nameof(AlpacaClose))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.AlpacaCommanderPolicy)]
    [AlpacaCommanderAllowed, AlpacaCommandAuthReport(RoofAlpacaCommandKind.Close)]
    [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<RoofStatusResponse>> AlpacaClose([FromBody] RoofAlpacaCommandRequest? request)
        => CommandAsync(RoofAlpacaCommandKind.Close, request);

    /// <summary>
    /// An Alpaca client's Open (External Alpaca account; #220, phase 4), carrying its ticket: admitted under the
    /// controller's lock with the Alpaca rules (commands <c>OpenAndClose</c>, no person holding the operator lease, the
    /// Stop hold clear, the Open policy: every required weather source's latest read successful, fresh and safe, and every
    /// blocking telescope clear, whatever <c>InterlockMode</c> is; or an override grant covering every reason, never in
    /// Block; the controller's own protections). Never queued: a refusal is final, and the client sends a new command.
    /// </summary>
    [HttpPost("shutter/open", Name = nameof(AlpacaOpen))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.AlpacaCommanderPolicy)]
    [AlpacaCommanderAllowed, AlpacaCommandAuthReport(RoofAlpacaCommandKind.Open)]
    [ProducesResponseType(typeof(RoofStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<RoofStatusResponse>> AlpacaOpen([FromBody] RoofAlpacaCommandRequest? request)
        => CommandAsync(RoofAlpacaCommandKind.Open, request);

    private async Task<ActionResult<RoofStatusResponse>> CommandAsync(RoofAlpacaCommandKind kind, RoofAlpacaCommandRequest? request)
    {
        var client = Client();
        var started = Stopwatch.GetTimestamp();
        var operation = kind == RoofAlpacaCommandKind.Open ? "alpaca_open" : "alpaca_close";
        var command = kind == RoofAlpacaCommandKind.Open ? RoofHistoryCommand.Open : RoofHistoryCommand.Close;
        using var activity = RoofControllerTelemetry.StartCommand(operation);
        request ??= new RoofAlpacaCommandRequest(null, null, null);
        var outcome = kind == RoofAlpacaCommandKind.Open
            ? await _alpaca.OpenAsync(request, User, client).ConfigureAwait(false)
            : await _alpaca.CloseAsync(request, User, client).ConfigureAwait(false);
        RoofControllerTelemetry.CompleteCommand(activity, operation, outcome.Result.IsSuccessful, started);
        RoofCommandHistory.NoteAlpaca(HttpContext, new RoofHistoryAlpacaNote(
            "Alpaca", client.ClientId, client.ClientTransactionId, client.Address, client.AuthMethod, outcome.ReasonKeys, outcome.GrantId));

        if (outcome.Result.IsSuccessful)
        {
            var status = Decorate(_controller.GetCurrentStatusSnapshot());
            Record(command, status, null);
            return Ok(status);
        }

        var problem = Problem(outcome.Result.Error, operation);
        Record(command, null, problem.Value as ProblemDetails);
        return problem;
    }

    /// <summary>The Alpaca server's own refusal of a command (its own account): raises the UIs' alert. Never a credential.</summary>
    [HttpPost("events", Name = nameof(AlpacaEvent))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.AlpacaServerPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult AlpacaEvent([FromBody] RoofAlpacaEvent? report)
    {
        if (report is null || report.Kind is not ("Close" or "Open") || report.Reason is not "NotAuthorised"
            || (report.ClientAddress is { } address && (address.Length > 64 || !System.Net.IPAddress.TryParse(address, out _))))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest,
                "An event is a refused Close or Open (reason NotAuthorised) with the client's address, ClientID and ClientTransactionID.");
        }

        _alpaca.ReportServerEvent(report);
        return NoContent();
    }

    /// <summary>The override grants in force (Viewer).</summary>
    [HttpGet("grants", Name = nameof(GetOverrideGrants))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.ViewerPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<RoofOverrideGrant>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<RoofOverrideGrant>> GetOverrideGrants() => Ok(_alpaca.Grants());

    /// <summary>
    /// A person grants an override for one Alpaca command of a kind (a person's session with the override role; every API
    /// key 403). It lifts only the reasons named (the ones the person was shown), lasts the override window, and is used
    /// up by the next command of that kind. An Open grant is refused while the safety interlock is Block (403,
    /// <c>BlockMode</c>).
    /// </summary>
    [HttpPost("grants", Name = nameof(GrantOverride))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.OperatorPolicy)]
    [RoofPersonSessionOnly]
    [ProducesResponseType(typeof(RoofOverrideGrant), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public IActionResult GrantOverride([FromBody] RoofOverrideGrantRequest? request)
    {
        if (request is null)
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest, "A grant names its kind and the reasons it lifts.");
        }

        if (_safetyOptions.CurrentValue.OverrideRole == RoofSafetyOverrideRole.Admin && !User.IsInRole(RoofControllerApiContract.AdminRole))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.SettingNotPermitted, "Only an admin may grant an override on this controller (RoofSafety:OverrideRole).");
        }

        var outcome = _alpaca.Grant(User, request);
        RoofCommandHistory.NoteAlpaca(HttpContext, new RoofHistoryAlpacaNote(ReasonKeys: outcome.Grant?.ReasonKeys ?? request.ReasonKeys, GrantId: outcome.Grant?.Id));
        if (outcome.Grant is { } grant)
        {
            Record(RoofHistoryCommand.OverrideGrant, null, null);
            return StatusCode(StatusCodes.Status201Created, grant);
        }

        var problem = Problem(StatusCodeProblem(outcome.StatusCode, outcome.Detail!, outcome.Rule));
        Record(RoofHistoryCommand.OverrideGrant, null, problem.Value as ProblemDetails);
        return problem;
    }

    /// <summary>A person revokes the grant of a kind (a person's session; every API key 403).</summary>
    [HttpDelete("grants/{kind}", Name = nameof(RevokeOverride))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.OperatorPolicy)]
    [RoofPersonSessionOnly]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public IActionResult RevokeOverride(string kind)
    {
        if (!Enum.TryParse<RoofAlpacaCommandKind>(kind, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(kind, out _))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.InvalidRequest, "kind must be Close or Open.");
        }

        if (!_alpaca.Revoke(parsed, User))
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.IdentityNotFound, $"No {parsed} grant is in force.");
        }

        Record(RoofHistoryCommand.OverrideRevoke, null, null);
        return NoContent();
    }

    /// <summary>
    /// A person clears the human Stop hold, so Alpaca commands may run again (a person's session with the Operator role;
    /// every API key 403; audit-logged).
    /// </summary>
    [HttpDelete("hold", Name = nameof(ClearStopHold))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.OperatorPolicy)]
    [RoofPersonSessionOnly]
    [ProducesResponseType(typeof(RoofStopHoldState), StatusCodes.Status200OK)]
    public ActionResult<RoofStopHoldState> ClearStopHold()
    {
        var state = _alpaca.ClearHold(User);
        Record(RoofHistoryCommand.StopHoldClear, TryGetSnapshot(), null);
        return Ok(state);
    }

    /// <summary>
    /// A person's UI takes or keeps the operator lease (a heartbeat; a person's session with the Operator role; every API
    /// key 403). <paramref name="input"/> is true when the person did something since the last heartbeat. While anyone holds
    /// it, Alpaca commands are refused.
    /// </summary>
    [HttpPost("lease", Name = nameof(HoldOperatorLease))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.OperatorPolicy)]
    [RoofPersonSessionOnly]
    [ProducesResponseType(typeof(RoofOperatorLeaseState), StatusCodes.Status200OK)]
    public ActionResult<RoofOperatorLeaseState> HoldOperatorLease([FromQuery] bool input = false) => Ok(_alpaca.Heartbeat(User, input));

    /// <summary>A person's UI gives the operator lease up (a person's session; every API key 403).</summary>
    [HttpDelete("lease", Name = nameof(ReleaseOperatorLease))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.ViewerPolicy)]
    [RoofPersonSessionOnly]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult ReleaseOperatorLease()
    {
        _alpaca.ReleaseLease(User);
        return NoContent();
    }

    /// <summary>The Alpaca alerts, newest first (Viewer). UIs only: never sent anywhere else.</summary>
    [HttpGet("alerts", Name = nameof(GetAlpacaAlerts))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.ViewerPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<RoofAlpacaAlert>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<RoofAlpacaAlert>> GetAlpacaAlerts() => Ok(_alpaca.Alerts());

    /// <summary>A person acknowledges an alert (a person's session; every API key 403; audit-logged).</summary>
    [HttpPost("alerts/{id}/acknowledge", Name = nameof(AcknowledgeAlpacaAlert))]
    [Authorize(AuthenticationSchemes = RoofControllerSecurityDefaults.ApiScheme, Policy = RoofControllerSecurityDefaults.ViewerPolicy)]
    [RoofPersonSessionOnly]
    [ProducesResponseType(typeof(RoofAlpacaAlert), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public ActionResult<RoofAlpacaAlert> AcknowledgeAlpacaAlert(string id)
    {
        if (_alpaca.Acknowledge(id, User) is not { } alert)
        {
            return RoofProblemResults.Create(this, RoofControllerErrorCode.IdentityNotFound, "No alert has that id.");
        }

        Record(RoofHistoryCommand.AlertAcknowledge, null, null);
        return Ok(alert);
    }

    private RoofAlpacaClient Client() => RoofAlpacaClientMetadata.From(HttpContext, _options.CurrentValue);

    private RoofStatusResponse Decorate(RoofStatusResponse status) => _alpaca.WithAlpaca(_safety.WithSafety(status));

    private static RoofControllerException StatusCodeProblem(int statusCode, string detail, string? rule)
        => new(statusCode == StatusCodes.Status403Forbidden ? RoofControllerErrorCode.AlpacaCommandRefused : RoofControllerErrorCode.InvalidRequest, detail)
        {
            AlpacaRefusal = rule
        };

    private ObjectResult Problem(RoofControllerException refusal) => Problem(refusal, "alpaca_grant",
        refusal.Code == RoofControllerErrorCode.AlpacaCommandRefused ? StatusCodes.Status403Forbidden : null);

    /// <summary>A refusal as ProblemDetails: <c>code</c>, <c>alpacaRefusal</c>, <c>reasonKeys</c> and <c>roofStatus</c>.</summary>
    private ObjectResult Problem(Exception? error, string operation, int? statusCode = null)
    {
        var refusal = error as RoofControllerException;
        var code = refusal?.Code ?? RoofControllerErrorCode.Unknown;
        if (refusal is null)
        {
            _logger.LogError(error, "Alpaca {Operation} failed unexpectedly", operation);
        }

        var status = statusCode ?? RoofControllerApiContract.HttpStatusFor(code);
        var result = Problem(
            statusCode: status,
            title: code.ToString(),
            type: RoofControllerApiContract.ProblemType(code),
            detail: refusal?.Message ?? $"An unexpected error occurred during '{operation}'. See the controller log.");
        if (result.Value is ProblemDetails details)
        {
            details.Extensions[RoofControllerApiContract.ProblemCodeExtension] = code.ToString();
            if (refusal?.AlpacaRefusal is { } rule)
            {
                details.Extensions[RoofAlpacaContract.RefusalExtension] = rule;
            }

            if (refusal?.ReasonKeys is { Count: > 0 } keys)
            {
                details.Extensions[RoofAlpacaContract.ReasonKeysExtension] = keys;
            }

            if ((refusal?.Snapshot ?? TryGetSnapshot()) is { } snapshot)
            {
                details.Extensions[RoofControllerApiContract.ProblemStatusExtension] = Decorate(snapshot);
            }
        }

        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            Response.Headers.RetryAfter = "2";
        }

        return result;
    }

    private RoofStatusResponse? TryGetSnapshot()
    {
        try
        {
            return _controller.GetCurrentStatusSnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not capture a status snapshot for a problem response");
            return null;
        }
    }

    /// <summary>Never fails the request: the command has been answered.</summary>
    private void Record(RoofHistoryCommand command, RoofStatusResponse? status, ProblemDetails? refusal)
    {
        try
        {
            _history.Record(_history.Describe(HttpContext, command, status, refusal));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alpaca {Command} could not be recorded in the command history", command);
        }
    }
}
