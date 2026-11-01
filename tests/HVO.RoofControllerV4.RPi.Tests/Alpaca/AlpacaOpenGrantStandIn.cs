using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Controllers;
using HVO.RoofControllerV4.RPi.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// Stands in for the controller's Open grants (#220, phase 4) in the UI tests, which run against the controller in
/// process: its grant route for an Open grant (201, or 403 with <c>alpacaRefusal</c> <c>BlockMode</c> while
/// <see cref="Block"/> says so), its revoke route for one, and the Alpaca state every status carries (REST and the status
/// hub): the Open grant in force, <see cref="RoofAlpacaStatus.OpenGrantUnavailable"/> and, when set, the commands setting.
/// The UIs' tests use it so that they test only what the UIs do; the controller's own Open rules have their own tests.
/// </summary>
/// <remarks>
/// <para>An Open grant rides on a Close grant the controller makes for the person, lifting only a telescope no test
/// names, so that the person steps away from the operator lease with it exactly as the controller does for any grant, and
/// their next input, taking the lease back, drops it. Every status shows that Close grant as the Open grant, with the
/// reasons the person granted. So a Close grant made while an Open grant is in force replaces it (the controller holds one
/// Close grant); a test that needs both shows another person's Open grant (<see cref="Show"/>).</para>
/// <para>Each request is checked as the controller checks it before its own rules: the Operator role and a person's
/// session (the route's own filters run first), and reasons an Open grant may name.</para>
/// </remarks>
internal sealed class AlpacaOpenGrantStandIn
{
    /// <summary>The reason the Close grant an Open grant rides on lifts: a telescope no test names.</summary>
    public const string RideKey = "scope-unknown:open-grant-stand-in";

    /// <summary>What the controller says in Block mode, as <see cref="RoofAlpacaStatus.OpenGrantUnavailable"/> and the 403's detail.</summary>
    public const string BlockReason =
        "The safety interlock is Block (RoofSafety:InterlockMode): no grant opens the roof, and Alpaca opens it only when it is Safe.";

    private readonly object _gate = new();
    private readonly List<RoofOverrideGrantRequest> _requests = [];
    private readonly List<RoofOverrideGrant> _shown = [];
    private IReadOnlyList<string> _keys = [];
    private string? _unavailable;
    private string? _refusal;

    /// <summary>The commands setting every status shows instead of the controller's (it starts only with Off or CloseOnly); null shows the controller's.</summary>
    public RoofAlpacaCommandMode? Mode { get; set; }

    /// <summary>The Open grants the UIs asked for, in order, whatever the answer.</summary>
    public IReadOnlyList<RoofOverrideGrantRequest> Requests
    {
        get { lock (_gate) { return [.. _requests]; } }
    }

    /// <summary>
    /// Block mode, or not: every status says why no Open grant can be made (<paramref name="reason"/>), and the grant route
    /// refuses one with 403 and <c>BlockMode</c>; null for neither.
    /// </summary>
    public void Block(string? reason = BlockReason)
    {
        lock (_gate)
        {
            _unavailable = reason;
            _refusal = reason;
        }
    }

    /// <summary>
    /// The grant route refuses an Open grant with 403 and <c>BlockMode</c> (the interlock turned to Block since the last
    /// status), while the status does not say so yet.
    /// </summary>
    public void RefuseOnly(string reason = BlockReason)
    {
        lock (_gate)
        {
            _refusal = reason;
        }
    }

    /// <summary>Every status shows <paramref name="grant"/> as well (another person's Open grant, say).</summary>
    public void Show(RoofOverrideGrant grant)
    {
        lock (_gate)
        {
            _shown.Add(grant);
        }
    }

    /// <summary>The Open grant in force, as the statuses show it; null when there is none.</summary>
    public RoofOverrideGrant? OpenGrant(IServiceProvider services)
        => Apply(services.GetRequiredService<RoofAlpacaCommands>().Grants()).FirstOrDefault(grant => grant.Kind == RoofAlpacaCommandKind.Open);

    /// <summary>Adds the stand-in to the controller's services: its routes' filter and the status hub's sender.</summary>
    public void AddTo(IServiceCollection services)
    {
        services.Configure<MvcOptions>(options => options.Filters.Add(new Filter(this)));
        var sender = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IRoofStatusSender));
        services.RemoveAll<IRoofStatusSender>();
        services.AddSingleton<IRoofStatusSender>(provider => new Sender(this, sender switch
        {
            { ImplementationFactory: { } factory } => (IRoofStatusSender)factory(provider),
            { ImplementationInstance: IRoofStatusSender instance } => instance,
            { ImplementationType: { } type } => (IRoofStatusSender)ActivatorUtilities.CreateInstance(provider, type),
            _ => throw new InvalidOperationException("The controller has no status hub sender to stand in front of.")
        }));
    }

    /// <summary>A status as the controller would send it with Open grants: the stand-in's Alpaca state over the controller's.</summary>
    public RoofStatusResponse Apply(RoofStatusResponse status)
        => status.Alpaca is { } alpaca ? status with { Alpaca = Apply(alpaca) } : status;

    private RoofAlpacaStatus Apply(RoofAlpacaStatus alpaca)
    {
        lock (_gate)
        {
            return alpaca with
            {
                Mode = Mode ?? alpaca.Mode,
                OpenGrantUnavailable = _unavailable ?? alpaca.OpenGrantUnavailable,
                Grants = Apply(alpaca.Grants)
            };
        }
    }

    private IReadOnlyList<RoofOverrideGrant> Apply(IReadOnlyList<RoofOverrideGrant> grants)
    {
        lock (_gate)
        {
            return
            [
                .. grants.Select(grant => IsRide(grant) ? grant with { Id = $"open-{grant.Id}", Kind = RoofAlpacaCommandKind.Open, ReasonKeys = _keys } : grant),
                .. _shown
            ];
        }
    }

    private static bool IsRide(RoofOverrideGrant grant)
        => grant is { Kind: RoofAlpacaCommandKind.Close, ReasonKeys: [RideKey] };

    private IActionResult? Grant(HttpContext http, RoofOverrideGrantRequest request)
    {
        IReadOnlyList<string> keys = [.. (request.ReasonKeys ?? []).Select(key => key?.Trim()).OfType<string>().Distinct(StringComparer.Ordinal)];
        string? refusal;
        lock (_gate)
        {
            _requests.Add(request with { ReasonKeys = [.. request.ReasonKeys ?? []] });
            refusal = _refusal;
        }

        if (refusal is not null)
        {
            return Problem(StatusCodes.Status403Forbidden, RoofControllerErrorCode.AlpacaCommandRefused, refusal, "BlockMode");
        }

        if (keys.Count is 0 or > 32 || keys.Any(key => !RoofAlpacaContract.IsGrantableReasonKey(RoofAlpacaCommandKind.Open, key)))
        {
            return Problem(StatusCodes.Status400BadRequest, RoofControllerErrorCode.InvalidRequest, "An Open grant names the reasons it lifts, as the alert gave them.", null);
        }

        // Set before the controller announces the Close grant, so the status that announces it shows the Open grant.
        lock (_gate)
        {
            _keys = keys;
        }

        var outcome = http.RequestServices.GetRequiredService<RoofAlpacaCommands>()
            .Grant(http.User, new RoofOverrideGrantRequest(RoofAlpacaCommandKind.Close, [RideKey]));
        return outcome.Grant is { } ride
            ? new ObjectResult(Apply([ride])[0]) { StatusCode = StatusCodes.Status201Created }
            : Problem(outcome.StatusCode, RoofControllerErrorCode.InvalidRequest, outcome.Detail ?? "Refused.", null);
    }

    // DELETE grants/Open revokes the Close grant it rides on; DELETE grants/Close never does (there is no Close grant).
    private IActionResult? Revoke(HttpContext http, string kind)
    {
        var commands = http.RequestServices.GetRequiredService<RoofAlpacaCommands>();
        var ride = commands.Grants().Any(IsRide);
        if (string.Equals(kind, nameof(RoofAlpacaCommandKind.Open), StringComparison.OrdinalIgnoreCase))
        {
            return ride && commands.Revoke(RoofAlpacaCommandKind.Close, http.User) ? new NoContentResult() : null;
        }

        return ride && string.Equals(kind, nameof(RoofAlpacaCommandKind.Close), StringComparison.OrdinalIgnoreCase)
            ? Problem(StatusCodes.Status404NotFound, RoofControllerErrorCode.IdentityNotFound, "No Close grant is in force.", null)
            : null;
    }

    private static ObjectResult Problem(int status, RoofControllerErrorCode code, string detail, string? rule)
    {
        var problem = new ProblemDetails { Status = status, Title = code.ToString(), Type = RoofControllerApiContract.ProblemType(code), Detail = detail };
        problem.Extensions[RoofControllerApiContract.ProblemCodeExtension] = code.ToString();
        if (rule is not null)
        {
            problem.Extensions[RoofAlpacaContract.RefusalExtension] = rule;
        }

        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    /// <summary>The grant routes for an Open grant, and the Alpaca state in every status and grant list the routes answer with.</summary>
    private sealed class Filter(AlpacaOpenGrantStandIn standIn) : IAsyncActionFilter, IAsyncResultFilter
    {
        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var result = context.ActionDescriptor is ControllerActionDescriptor action && action.ControllerTypeInfo.AsType() == typeof(AlpacaCommandsController)
                ? action.ActionName switch
                {
                    nameof(AlpacaCommandsController.GrantOverride) when context.ActionArguments.TryGetValue("request", out var request)
                        && request is RoofOverrideGrantRequest { Kind: RoofAlpacaCommandKind.Open } open => standIn.Grant(context.HttpContext, open),
                    nameof(AlpacaCommandsController.RevokeOverride) when context.ActionArguments.TryGetValue("kind", out var kind)
                        && kind is string text => standIn.Revoke(context.HttpContext, text),
                    _ => null
                }
                : null;
            if (result is not null)
            {
                context.Result = result;
                return;
            }

            await next();
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            if (context.Result is ObjectResult { Value: RoofStatusResponse status } answer)
            {
                answer.Value = standIn.Apply(status);
            }
            else if (context.Result is ObjectResult { Value: IReadOnlyList<RoofOverrideGrant> grants } list)
            {
                list.Value = standIn.Apply(grants);
            }

            await next();
        }
    }

    /// <summary>The status hub's sender, with the stand-in's Alpaca state over the controller's.</summary>
    private sealed class Sender(AlpacaOpenGrantStandIn standIn, IRoofStatusSender inner) : IRoofStatusSender
    {
        public Task SendAsync(string connectionId, RoofStatusHubMessage message, CancellationToken cancellationToken)
            => inner.SendAsync(connectionId, message.Status is { } status ? message with { Status = standIn.Apply(status) } : message, cancellationToken);
    }
}
