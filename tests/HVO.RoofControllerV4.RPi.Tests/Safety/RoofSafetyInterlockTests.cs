using System.Security.Claims;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Safety;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Moq;
using CapturingLogger = HVO.RoofControllerV4.RPi.Tests.TestSupport.CapturingLogger<HVO.RoofControllerV4.RPi.Safety.RoofSafetyInterlock>;

namespace HVO.RoofControllerV4.RPi.Tests.Safety;

/// <summary>
/// The Open interlock in each mode against each safety state: Off only logs, Warn refuses unless an override comes from
/// the override role (audit-logged), Block refuses with no override. A Safe state is never refused.
/// </summary>
[TestClass]
public sealed class RoofSafetyInterlockTests
{
    private readonly CapturingLogger _logger = new();
    private readonly Mock<IAuthorizationService> _authorization = new(MockBehavior.Strict);
    private readonly ClaimsPrincipal _operator = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "night-operator")], "test"));
    private RoofSafetyState _state = RoofSafetyState.Unsafe;

    [TestMethod]
    [DataRow(RoofSafetyInterlockMode.Off)]
    [DataRow(RoofSafetyInterlockMode.Warn)]
    [DataRow(RoofSafetyInterlockMode.Block)]
    public async Task Safe_IsNeverRefused_InAnyMode(RoofSafetyInterlockMode mode)
    {
        _state = RoofSafetyState.Safe;

        var refusal = await Create(mode).CheckOpenAsync(_operator, null, overrideRequested: true);

        refusal.Should().BeNull();
        _logger.Entries.Should().BeEmpty("nothing was overridden or refused");
    }

    [TestMethod]
    [DataRow(RoofSafetyState.Unsafe)]
    [DataRow(RoofSafetyState.Unknown)]
    public async Task Off_LetsOpenThrough_AndLogsThatItWasNotSafe(RoofSafetyState state)
    {
        _state = state;

        var refusal = await Create(RoofSafetyInterlockMode.Off).CheckOpenAsync(_operator, null, overrideRequested: false);

        refusal.Should().BeNull("Off is advisory: Open behaves as it does without the safety service");
        _logger.Contains(LogLevel.Information, $"Open by night-operator while the safety conditions are {state} (interlock Off, advisory only): reason one; reason two").Should().BeTrue();
    }

    [TestMethod]
    [DataRow(RoofSafetyState.Unsafe)]
    [DataRow(RoofSafetyState.Unknown)]
    public async Task Warn_WithoutAnOverride_AsksForOne(RoofSafetyState state)
    {
        _state = state;

        var refusal = await Create(RoofSafetyInterlockMode.Warn).CheckOpenAsync(_operator, null, overrideRequested: false);

        refusal.Should().NotBeNull();
        refusal!.Code.Should().Be(RoofControllerErrorCode.SafetyOverrideRequired);
        refusal.Message.Should().Be(
            $"The safety conditions are {state.ToString().ToLowerInvariant()} (reason one; reason two). To open anyway, send Open again with overrideSafety=true; " +
            "an admin may, and the override is logged. Close and Stop are not affected.");
    }

    [TestMethod]
    public async Task Warn_AnOverrideFromAnAdmin_LetsOpenThrough_AndIsAudited()
    {
        AuthorizeAdmin(true);
        var request = new object();

        var refusal = await Create(RoofSafetyInterlockMode.Warn).CheckOpenAsync(_operator, request, overrideRequested: true);

        refusal.Should().BeNull();
        _logger.Contains(LogLevel.Warning, "AUDIT safety override: Open by night-operator while the conditions are Unsafe (interlock Warn): reason one; reason two").Should().BeTrue();
        _authorization.Verify(a => a.AuthorizeAsync(_operator, request, RoofControllerSecurityDefaults.AdminPolicy), Times.Once());
    }

    [TestMethod]
    public async Task Warn_AnOverrideFromAnOperator_IsRefused_WhenTheOverrideNeedsAnAdmin_AndTheAttemptIsAudited()
    {
        AuthorizeAdmin(false);

        var refusal = await Create(RoofSafetyInterlockMode.Warn).CheckOpenAsync(_operator, null, overrideRequested: true);

        refusal!.Code.Should().Be(RoofControllerErrorCode.SafetyInterlockActive);
        refusal.Message.Should().Contain("only an admin may override them");
        _logger.Contains(LogLevel.Warning, "AUDIT safety override by night-operator refused: the override needs the Admin role").Should().BeTrue();
    }

    [TestMethod]
    public async Task Warn_WithTheOperatorOverrideRole_TakesAnOperatorsOverride()
    {
        var refusal = await Create(RoofSafetyInterlockMode.Warn, RoofSafetyOverrideRole.Operator).CheckOpenAsync(_operator, null, overrideRequested: true);

        refusal.Should().BeNull();
        _logger.Contains(LogLevel.Warning, "AUDIT safety override: Open by night-operator").Should().BeTrue();
        _authorization.VerifyNoOtherCalls();
    }

    [TestMethod]
    [DataRow(RoofSafetyState.Unsafe, false)]
    [DataRow(RoofSafetyState.Unknown, false)]
    [DataRow(RoofSafetyState.Unsafe, true)]
    [DataRow(RoofSafetyState.Unknown, true)]
    public async Task Block_RefusesOpen_AndTakesNoOverride(RoofSafetyState state, bool overrideRequested)
    {
        _state = state;

        var refusal = await Create(RoofSafetyInterlockMode.Block).CheckOpenAsync(_operator, null, overrideRequested);

        refusal!.Code.Should().Be(RoofControllerErrorCode.SafetyInterlockActive);
        refusal.Message.Should().Be(
            $"The safety interlock refuses Open while the safety conditions are {state.ToString().ToLowerInvariant()} (reason one; reason two). " +
            "It takes no override in this mode. Close and Stop are not affected.");
        _logger.Contains(LogLevel.Warning, overrideRequested ? "(Block, override not accepted)" : "(Block)").Should().BeTrue();
        _authorization.VerifyNoOtherCalls();
    }

    private RoofSafetyInterlock Create(RoofSafetyInterlockMode mode, RoofSafetyOverrideRole role = RoofSafetyOverrideRole.Admin)
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
