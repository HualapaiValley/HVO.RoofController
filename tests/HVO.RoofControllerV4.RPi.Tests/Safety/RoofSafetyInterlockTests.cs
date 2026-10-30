using System.Security.Claims;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Moq;
using CapturingLogger = HVO.RoofControllerV4.RPi.Tests.TestSupport.CapturingLogger<HVO.RoofControllerV4.RPi.Safety.RoofSafetyInterlock>;

namespace HVO.RoofControllerV4.RPi.Tests.Safety;

/// <summary>
/// The Open interlock in each mode against each safety state: Off only logs, Warn refuses unless an override comes from
/// the override role (by default anyone who may open), Block refuses with no override. A Safe state is never refused,
/// and Unknown is refused as Unsafe is. What Open did is logged once the controller has answered: an override is audited
/// only when the controller accepted Open.
/// </summary>
[TestClass]
public sealed class RoofSafetyInterlockTests
{
    private const string Audit = "AUDIT safety override: Open by night-operator";

    private readonly CapturingLogger _logger = new();
    private readonly Mock<IAuthorizationService> _authorization = new(MockBehavior.Strict);
    private readonly ClaimsPrincipal _operator = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "night-operator")], "test"));
    private RoofSafetyState _state = RoofSafetyState.Unsafe;

    [TestMethod]
    [DataRow(RoofSafetyInterlockMode.Off)]
    [DataRow(RoofSafetyInterlockMode.Warn)]
    [DataRow(RoofSafetyInterlockMode.Block)]
    public async Task Safe_IsNeverRefused_InAnyMode_AndNothingIsLogged(RoofSafetyInterlockMode mode)
    {
        _state = RoofSafetyState.Safe;
        var interlock = Create(mode);

        var decision = await interlock.CheckOpenAsync(_operator, null, overrideRequested: true);
        interlock.ReportOutcome(decision, controllerError: null);

        decision.Refusal.Should().BeNull();
        decision.Overridden.Should().BeFalse("nothing needed overriding");
        _logger.Entries.Should().BeEmpty("nothing was overridden or refused");
    }

    [TestMethod]
    [DataRow(RoofSafetyState.Unsafe)]
    [DataRow(RoofSafetyState.Unknown)]
    public async Task Off_LetsOpenThrough_AndLogsThatItWasNotSafe_OnceTheControllerAcceptsIt(RoofSafetyState state)
    {
        _state = state;
        var interlock = Create(RoofSafetyInterlockMode.Off);

        var decision = await interlock.CheckOpenAsync(_operator, null, overrideRequested: false);

        decision.Refusal.Should().BeNull("Off is advisory: Open behaves as it does without the safety service");
        _logger.Entries.Should().BeEmpty("the controller has not answered yet");
        interlock.ReportOutcome(decision, controllerError: null);
        _logger.Contains(LogLevel.Information, $"Open by night-operator accepted while the safety conditions are {state} (interlock Off, advisory only): reason one; reason two").Should().BeTrue();
    }

    [TestMethod]
    public async Task Off_AnOpenTheControllerRefuses_IsNotLoggedAsAdvisory()
    {
        var interlock = Create(RoofSafetyInterlockMode.Off);

        interlock.ReportOutcome(await interlock.CheckOpenAsync(_operator, null, overrideRequested: false), Refused());

        _logger.Entries.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(RoofSafetyState.Unsafe)]
    [DataRow(RoofSafetyState.Unknown)]
    public async Task Warn_WithoutAnOverride_AsksForOne(RoofSafetyState state)
    {
        _state = state;

        var refusal = (await Create(RoofSafetyInterlockMode.Warn).CheckOpenAsync(_operator, null, overrideRequested: false)).Refusal;

        refusal.Should().NotBeNull();
        refusal!.Code.Should().Be(RoofControllerErrorCode.SafetyOverrideRequired);
        refusal.Message.Should().Be(
            $"The safety conditions are {state.ToString().ToLowerInvariant()} (reason one; reason two). To open anyway, send Open again with overrideSafety=true; " +
            "anyone who can open the roof may override, and the override is logged. Close and Stop are not affected.");
        _logger.Contains(LogLevel.Information, "refused by the safety interlock (Warn, no override)").Should().BeTrue();
    }

    [TestMethod]
    [DataRow(RoofSafetyState.Unsafe)]
    [DataRow(RoofSafetyState.Unknown)]
    public async Task Warn_ByDefault_TakesTheOverrideOfAnyoneWhoMayOpen_AndAuditsItOnceOpenIsAccepted(RoofSafetyState state)
    {
        _state = state;
        var interlock = Create(RoofSafetyInterlockMode.Warn);

        var decision = await interlock.CheckOpenAsync(_operator, null, overrideRequested: true);

        decision.Refusal.Should().BeNull("an operator, who may open, may override; Unknown is overridden as Unsafe is");
        decision.Overridden.Should().BeTrue();
        _logger.Entries.Should().BeEmpty("the override is audited only once the controller accepts Open");
        interlock.ReportOutcome(decision, controllerError: null);
        _logger.Entries.Should().ContainSingle().Which.Should().Be(
            (LogLevel.Warning, $"{Audit} accepted while the conditions are {state} (interlock Warn): reason one; reason two", (Exception?)null));
        _authorization.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Warn_AnOverride_TheControllerRefuses_IsNotAudited_AndSaysItHadNoEffect()
    {
        var interlock = Create(RoofSafetyInterlockMode.Warn);

        interlock.ReportOutcome(await interlock.CheckOpenAsync(_operator, null, overrideRequested: true), Refused());

        _logger.Entries.Should().NotContain(entry => entry.Message.StartsWith("AUDIT", StringComparison.Ordinal));
        _logger.Entries.Should().ContainSingle().Which.Should().Be(
            (LogLevel.Information, "Open by night-operator with a safety override was refused by the controller (FaultLatched), so the override had no effect (conditions Unsafe: reason one; reason two)", (Exception?)null));
    }

    [TestMethod]
    public async Task Warn_NarrowedToAdmins_TakesAnAdminsOverride_AndAuditsIt()
    {
        AuthorizeAdmin(true);
        var request = new object();
        var interlock = Create(RoofSafetyInterlockMode.Warn, RoofSafetyOverrideRole.Admin);

        var decision = await interlock.CheckOpenAsync(_operator, request, overrideRequested: true);
        interlock.ReportOutcome(decision, controllerError: null);

        decision.Refusal.Should().BeNull();
        _logger.Contains(LogLevel.Warning, $"{Audit} accepted while the conditions are Unsafe (interlock Warn): reason one; reason two").Should().BeTrue();
        _authorization.Verify(a => a.AuthorizeAsync(_operator, request, RoofControllerSecurityDefaults.AdminPolicy), Times.Once());
    }

    [TestMethod]
    public async Task Warn_NarrowedToAdmins_RefusesAnOperatorsOverride_AndAuditsTheAttempt()
    {
        AuthorizeAdmin(false);

        var refusal = (await Create(RoofSafetyInterlockMode.Warn, RoofSafetyOverrideRole.Admin).CheckOpenAsync(_operator, null, overrideRequested: true)).Refusal;

        refusal!.Code.Should().Be(RoofControllerErrorCode.SafetyInterlockActive);
        refusal.Message.Should().Contain("only an admin may override them");
        _logger.Contains(LogLevel.Warning, "AUDIT safety override by night-operator refused: the override needs the Admin role").Should().BeTrue();
    }

    [TestMethod]
    [DataRow(RoofSafetyState.Unsafe, false)]
    [DataRow(RoofSafetyState.Unknown, false)]
    [DataRow(RoofSafetyState.Unsafe, true)]
    [DataRow(RoofSafetyState.Unknown, true)]
    public async Task Block_RefusesOpen_AndTakesNoOverride(RoofSafetyState state, bool overrideRequested)
    {
        _state = state;
        var interlock = Create(RoofSafetyInterlockMode.Block);

        var decision = await interlock.CheckOpenAsync(_operator, null, overrideRequested);
        interlock.ReportOutcome(decision, controllerError: null);

        decision.Refusal!.Code.Should().Be(RoofControllerErrorCode.SafetyInterlockActive);
        decision.Refusal.Message.Should().Be(
            $"The safety interlock refuses Open while the safety conditions are {state.ToString().ToLowerInvariant()} (reason one; reason two). " +
            "It takes no override in this mode. Close and Stop are not affected.");
        _logger.Entries.Should().ContainSingle().Which.Message.Should().Contain(overrideRequested ? "(Block, override not accepted)" : "(Block)");
        _authorization.VerifyNoOtherCalls();
    }

    private static RoofControllerException Refused() => new(RoofControllerErrorCode.FaultLatched, "A fault is latched (test).");

    private RoofSafetyInterlock Create(RoofSafetyInterlockMode mode, RoofSafetyOverrideRole role = RoofSafetyOverrideRole.Operator)
    {
        // The mode and the override role as the safety service reports them.
        var safety = new Mock<IRoofSafetyService>();
        safety.Setup(s => s.GetCurrent()).Returns(() => new RoofSafetyStatus
        {
            State = _state,
            Reasons = _state == RoofSafetyState.Safe ? [] : ["reason one", "reason two"],
            InterlockMode = mode,
            OverrideRole = mode != RoofSafetyInterlockMode.Warn ? null
                : role == RoofSafetyOverrideRole.Operator ? RoofControllerApiContract.OperatorRole : RoofControllerApiContract.AdminRole
        });
        return new RoofSafetyInterlock(safety.Object, _authorization.Object, _logger);
    }

    private void AuthorizeAdmin(bool allowed)
        => _authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), RoofControllerSecurityDefaults.AdminPolicy))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
}
