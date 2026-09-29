using System.Runtime.InteropServices;
using FluentAssertions;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// The termination signals of <c>hvo-roof</c> (#45): the first cancels the command instead of ending the process, the
/// process still ends within a grace period, and nothing cuts a Stop on its way short.
/// </summary>
[TestClass]
public sealed class RoofCliTerminationTests
{
    private readonly ManualTimeProvider _clock = new();
    private readonly List<int> _exits = [];

    private RoofCliTermination Create() => new(code => _exits.Add(code), _clock);

    private static PosixSignalContext Signal(PosixSignal signal = PosixSignal.SIGINT) => new(signal);

    [TestMethod]
    [DataRow(PosixSignal.SIGINT)]
    [DataRow(PosixSignal.SIGTERM)]
    [DataRow(PosixSignal.SIGHUP)]
    public void TheFirstSignal_CancelsTheCommand_AndDoesNotEndTheProcess(PosixSignal signal)
    {
        using var termination = Create();
        var context = Signal(signal);

        termination.OnSignal(context);

        context.Cancel.Should().BeTrue("the runtime's default would end the process at once");
        termination.Token.IsCancellationRequested.Should().BeTrue();
        _exits.Should().BeEmpty();
    }

    [TestMethod]
    public void WithNoStopOnItsWay_TheProcessEnds_TheCommandGraceAfterTheFirstSignal()
    {
        using var termination = Create();
        termination.OnSignal(Signal());

        _clock.Advance(RoofCliTermination.CommandGrace - TimeSpan.FromMilliseconds(1));
        _exits.Should().BeEmpty();

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        _exits.Should().Equal((int)RoofExitCode.Interrupted);
    }

    [TestMethod]
    public void WithAStopOnItsWay_TheProcessWaitsForIt_UntilTheStopGrace()
    {
        using var termination = Create();
        using var hold = termination.Hold();
        termination.OnSignal(Signal());

        _clock.Advance(RoofCliTermination.CommandGrace);
        _exits.Should().BeEmpty("a Stop is on its way");

        _clock.Advance(RoofCliTermination.StopGrace - RoofCliTermination.CommandGrace - TimeSpan.FromMilliseconds(1));
        _exits.Should().BeEmpty();

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        _exits.Should().Equal((int)RoofExitCode.Interrupted);
    }

    [TestMethod]
    public void AStopAnsweredWithinTheCommandGrace_LetsTheProcessEndThen()
    {
        using var termination = Create();
        var hold = termination.Hold();
        termination.OnSignal(Signal());
        _clock.Advance(TimeSpan.FromSeconds(2));

        hold.Dispose();
        _clock.Advance(RoofCliTermination.CommandGrace - TimeSpan.FromSeconds(2));

        _exits.Should().Equal((int)RoofExitCode.Interrupted);
    }

    [TestMethod]
    public void ASecondSignal_EndsTheProcessAtOnce()
    {
        using var termination = Create();
        termination.OnSignal(Signal());

        var second = Signal(PosixSignal.SIGTERM);
        termination.OnSignal(second);

        second.Cancel.Should().BeTrue();
        _exits.Should().Equal((int)RoofExitCode.Interrupted);
    }

    [TestMethod]
    public void ASecondSignal_DoesNotCutAStopShort()
    {
        using var termination = Create();
        using var hold = termination.Hold();
        termination.OnSignal(Signal());

        termination.OnSignal(Signal());
        termination.OnSignal(Signal(PosixSignal.SIGHUP));

        _exits.Should().BeEmpty("a Stop is on its way");
    }

    [TestMethod]
    public void ReleasingAHoldTwice_ReleasesItOnce()
    {
        using var termination = Create();
        var first = termination.Hold();
        using var second = termination.Hold();
        termination.OnSignal(Signal());

        first.Dispose();
        first.Dispose();
        termination.OnSignal(Signal());

        _exits.Should().BeEmpty("the second hold is still on");
    }

    [TestMethod]
    public void AfterTheCommandEnds_ASignalOrTheGraceDoesNothing()
    {
        var termination = Create();
        termination.OnSignal(Signal());
        termination.Dispose();

        _clock.Advance(RoofCliTermination.StopGrace);
        termination.OnSignal(Signal());

        _exits.Should().BeEmpty();
    }
}
