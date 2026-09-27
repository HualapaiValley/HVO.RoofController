using System;
using System.Collections.Generic;
using FluentAssertions;
using HVO.RoofControllerV4.Simulation.LimitSwitches;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Simulation;

/// <summary>
/// The ME-8108 model on its own: 20° pretravel, 10° differential travel, 75° overtravel; snap action with a 5 ms
/// transfer (break before make) and 3 ms of bounce by default.
/// </summary>
[TestClass]
public class Me8108LimitSwitchTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private static (bool Nc, bool No) Contacts(Me8108LimitSwitch limit) => (limit.ContactNcClosed, limit.ContactNoClosed);

    private static List<(bool Nc, bool No)> StepAt(Me8108LimitSwitch limit, double angle, int milliseconds)
    {
        var trace = new List<(bool, bool)>();
        for (var i = 0; i < milliseconds; i++)
        {
            limit.Step(Ms, angle);
            trace.Add(Contacts(limit));
        }

        return trace;
    }

    [TestMethod]
    [DataRow(0.0, false)]
    [DataRow(19.9, false)]
    [DataRow(20.0, true)]
    [DataRow(40.0, true)]
    public void InitialState_IsSettledForTheInitialAngle(double angle, bool actuated)
    {
        var limit = new Me8108LimitSwitch("test", new Me8108Options(), angle);

        limit.Actuated.Should().Be(actuated);
        Contacts(limit).Should().Be((!actuated, actuated));
        limit.IsSettled.Should().BeTrue();
    }

    [TestMethod]
    public void Operate_BreaksTheNcContact_ThenMakesTheNoContactAfterTheTransfer_WithBounce()
    {
        var limit = new Me8108LimitSwitch("test", new Me8108Options(), 0);
        var changes = new List<string>();
        limit.Changed += changes.Add;

        StepAt(limit, 19.9, 5).Should().AllBeEquivalentTo((true, false));

        var trace = StepAt(limit, 20, 10);

        trace.Should().Equal(
            (false, false),      // crossing step: NC breaks
            (false, false),
            (false, false),
            (false, false),
            (false, false),
            (false, true),       // +5 ms: NO makes
            (false, false),      // bounce
            (false, true),
            (false, true),
            (false, true));
        limit.Actuated.Should().BeTrue();
        limit.IsSettled.Should().BeTrue();
        changes.Should().Equal("test operated");
    }

    [TestMethod]
    public void Release_MirrorsTheOperate_AtTheReleasePoint()
    {
        var limit = new Me8108LimitSwitch("test", new Me8108Options(), 30);

        StepAt(limit, 10.1, 20).Should().AllBeEquivalentTo((false, true), "the release point is 20° - 10°");

        var trace = StepAt(limit, 10, 9);

        trace.Should().Equal(
            (false, false),
            (false, false),
            (false, false),
            (false, false),
            (false, false),
            (true, false),
            (false, false),
            (true, false),
            (true, false));
        limit.Actuated.Should().BeFalse();
    }

    [TestMethod]
    public void Hysteresis_BackingOffLessThanTheDifferential_KeepsItOperated()
    {
        var limit = new Me8108LimitSwitch("test", new Me8108Options(), 0);
        StepAt(limit, 25, 20);

        StepAt(limit, 15, 20);
        limit.Actuated.Should().BeTrue();

        StepAt(limit, 19, 20);
        StepAt(limit, 25, 20);
        Contacts(limit).Should().Be((false, true), "no new operation without a release first");
    }

    [TestMethod]
    public void ZeroTransferAndBounce_SwitchInTheCrossingStep()
    {
        var limit = new Me8108LimitSwitch("test", new Me8108Options { TransferTime = TimeSpan.Zero, BounceTime = TimeSpan.Zero }, 0);

        StepAt(limit, 20, 1);

        Contacts(limit).Should().Be((false, true));
        limit.IsSettled.Should().BeTrue();
    }

    [TestMethod]
    public void NotSettled_DuringTheTransferAndBounce()
    {
        var limit = new Me8108LimitSwitch("test", new Me8108Options(), 0);

        var settled = new List<bool>();
        for (var i = 0; i < 9; i++)
        {
            limit.Step(Ms, 20);
            settled.Add(limit.IsSettled);
        }

        settled.Should().Equal(false, false, false, false, false, false, false, false, true);
    }

    [TestMethod]
    public void SlowAction_ContactsFollowTheLever_WithAGapAndNoHysteresis()
    {
        var limit = new Me8108LimitSwitch("test", new Me8108Options { ContactAction = Me8108ContactAction.SlowAction }, 0);

        StepAt(limit, 20, 1);
        Contacts(limit).Should().Be((false, false));

        StepAt(limit, 24.9, 1);
        Contacts(limit).Should().Be((false, false));

        StepAt(limit, 25, 1);
        Contacts(limit).Should().Be((false, true));

        StepAt(limit, 19.9, 1);
        Contacts(limit).Should().Be((true, false));
        limit.Actuated.Should().BeFalse();
    }

    [TestMethod]
    public void Overtravel_IsReportedOnce_PastPretravelPlusOvertravel()
    {
        var limit = new Me8108LimitSwitch("test", new Me8108Options(), 0);
        var changes = new List<string>();
        limit.Changed += changes.Add;

        StepAt(limit, 95, 10);
        limit.OvertravelExceeded.Should().BeFalse();

        StepAt(limit, 95.1, 1);
        StepAt(limit, 100, 1);

        limit.OvertravelExceeded.Should().BeTrue();
        limit.MaximumAngleDegrees.Should().Be(100);
        changes.Should().ContainSingle(c => c.Contains("overtravel"));
    }

    [TestMethod]
    public void NegativeAngles_ClampToRest()
    {
        var limit = new Me8108LimitSwitch("test", new Me8108Options(), -5);

        limit.AngleDegrees.Should().Be(0);
        StepAt(limit, -10, 1);
        limit.AngleDegrees.Should().Be(0);
    }

    [TestMethod]
    public void StuckFaults_OverrideTheLever()
    {
        var limit = new Me8108LimitSwitch("test", new Me8108Options(), 0) { Fault = LimitSwitchFault.StuckActuated };
        limit.Actuated.Should().BeTrue();
        Contacts(limit).Should().Be((false, true));

        limit.Fault = LimitSwitchFault.StuckReleased;
        StepAt(limit, 40, 20);
        limit.Actuated.Should().BeFalse();
        Contacts(limit).Should().Be((true, false));
    }

    [TestMethod]
    public void BrokenWires_OpenOnlyTheirPath()
    {
        var atRest = new Me8108LimitSwitch("rest", new Me8108Options(), 0) { Fault = LimitSwitchFault.BrokenNcWire };
        atRest.ContactNcClosed.Should().BeTrue();
        atRest.NcPathClosed.Should().BeFalse();

        var operated = new Me8108LimitSwitch("operated", new Me8108Options(), 30) { Fault = LimitSwitchFault.BrokenMonitorWire };
        operated.ContactNoClosed.Should().BeTrue();
        operated.NoPathClosed.Should().BeFalse();
    }

    [TestMethod]
    public void TravelForDegrees_UsesTheLeverArm()
    {
        new Me8108Options().TravelForDegrees(20).Should().BeApproximately(0.017453, 1e-6);
        new Me8108Options { LeverArmMeters = 0.1 }.TravelForDegrees(180).Should().BeApproximately(Math.PI * 0.1, 1e-12);
    }

    [TestMethod]
    public void Specification_IsTheManufacturersValues()
    {
        var spec = new Me8108Specification();

        spec.PretravelDegrees.Should().Be(20);
        spec.DifferentialTravelDegrees.Should().Be(10);
        spec.OvertravelDegrees.Should().Be(75);
        spec.MaximumOperatingSpeed.Should().Be(0.5);
        spec.MinimumOperatingSpeed.Should().Be(0.0005);
    }

    [TestMethod]
    public void Validate_RejectsImpossibleOptions()
    {
        var invalid = new[]
        {
            new Me8108Options { LeverArmMeters = 0 },
            new Me8108Options { TransferTime = TimeSpan.FromMilliseconds(-1) },
            new Me8108Options { BounceTime = TimeSpan.FromMilliseconds(-1) },
            new Me8108Options { SlowActionGapDegrees = -1 },
            new Me8108Options { Specification = new Me8108Specification { DifferentialTravelDegrees = 20 } },
            new Me8108Options { Specification = new Me8108Specification { OvertravelDegrees = 0 } }
        };

        foreach (var options in invalid)
        {
            options.Invoking(o => o.Validate()).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => new Me8108LimitSwitch("bad", options, 0)).Should().Throw<ArgumentOutOfRangeException>();
        }
    }
}
