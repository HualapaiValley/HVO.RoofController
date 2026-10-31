using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Cli.Ui;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.Common.Safety;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.Simulation.Safety;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Terminal.Gui.Input;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// The terminal interface's safety override (#215) and its safety rows (#207), against the controller in process with its
/// interlock in Warn mode: Open while the conditions are not Safe asks over the page and sends nothing; "Open anyway"
/// sends it with the override; Esc sends nothing; F9 still stops the roof while it asks.
/// </summary>
[TestClass, DoNotParallelize]
public sealed class RoofTerminalUiSafetyTests
{
    private static readonly Dictionary<string, string?> Warn = new() { ["RoofSafety:InterlockMode"] = "Warn" };

    [TestMethod]
    public void Open_WhileTheConditionsAreUnknown_AsksOverThePage_AndOpenAnywaySendsItWithTheOverride()
    {
        var roof = OpeningRoof();
        using var host = RoofClientApiTests.CreateHost(roof, settings: Warn);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the question", () => tui.Ui.Panel is not null);

        var panel = tui.Ui.Panel!;
        panel.Prompt.Title.Should().Be(RoofSafetyText.OverrideTitle);
        panel.Prompt.Message.Should().Be(string.Join('\n',
            "The safety conditions are UNKNOWN: No safety sources configured.",
            RoofSafetyText.UnknownCountsAsUnsafe,
            RoofSafetyText.OverrideConsequence));
        panel.Buttons.Select(button => button.Text).Should().Equal(RoofSafetyText.OverrideConfirm, RoofSafetyText.OverrideCancel);
        roof.Verify(service => service.Open(), Times.Never(), "nothing is sent before the person confirms");

        panel.Press(RoofSafetyText.OverrideConfirm);
        tui.WaitIdle("Open with the override", () => tui.Ui.HoldsLease);

        roof.Verify(service => service.Open(), Times.Once(), "the controller lets Open through only with the override");
        tui.Ui.Panel.Should().BeNull();
        tui.Ui.Message.Should().StartWith("Open accepted.");
    }

    [TestMethod]
    public void Esc_SendsNothing_AndF9StillStopsTheRoofWhileItAsks()
    {
        var roof = OpeningRoof();
        using var host = RoofClientApiTests.CreateHost(roof, settings: Warn);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the question", () => tui.Ui.Panel is not null);

        tui.Press(Terminal.Gui.Input.Key.F9);
        tui.WaitIdle("Stop", () => tui.Ui.StopResult != RoofStopText.Sending);
        roof.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once());

        tui.Press(Terminal.Gui.Input.Key.Esc);
        tui.WaitIdle("the question to close", () => tui.Ui.Panel is null);
        roof.Verify(service => service.Open(), Times.Never());
    }

    [TestMethod]
    public void F9_WithdrawsTheQuestion_AndItsOpenAnywayThenSendsNothing()
    {
        // Review F1: Stop wins over a question it crossed, even one still on screen.
        var roof = OpeningRoof();
        using var host = RoofClientApiTests.CreateHost(roof, settings: Warn);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the question", () => tui.Ui.Panel is not null);
        var question = tui.Ui.Panel!.Prompt;

        tui.Press(Terminal.Gui.Input.Key.F9);
        tui.WaitIdle("Stop", () => tui.Ui.StopResult != RoofStopText.Sending);

        tui.Ui.Panel.Should().BeNull("Stop withdraws the question");
        question.Actions.Single().Run([]).Should().BeNull();
        Settle(tui);
        roof.Verify(service => service.Open(), Times.Never(), "an Open anyway from before the Stop sends nothing");
    }

    [TestMethod]
    public void ARefusalThatArrivesAfterF9_AsksNothing()
    {
        // Review F3: the refusal belongs to an Open that Stop withdrew.
        var roof = OpeningRoof();
        using var host = RoofClientApiTests.CreateHost(roof, settings: Warn);
        using var rig = new CliRig(host);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.WrapHandler = inner => new HoldingHandler(inner, held, release);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        // The status says nothing of the conditions (an older controller's): Open is sent, and refused.
        tui.Ui.Apply(tui.Ui.Status! with { Safety = null, StatusVersion = tui.Ui.Status.StatusVersion + 100 });

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitFor("the Open to be on its way", () => held.Task.IsCompleted);
        tui.Press(Terminal.Gui.Input.Key.F9);
        tui.WaitFor("Stop", () => tui.Ui.StopResult != RoofStopText.Sending);
        release.SetResult();
        tui.WaitIdle("the refusal", () => tui.Ui.Message.StartsWith("The conditions are not confirmed safe.", StringComparison.Ordinal));

        tui.Ui.Panel.Should().BeNull("Stop withdrew the Open this refusal answers");
        roof.Verify(service => service.Open(), Times.Never());
    }

    [TestMethod]
    public void ARefusalThatReportsAFault_AsksNothing()
    {
        // Review F4: the refusal's status says Open is blocked for another reason.
        var faulted = false;
        var roof = OpeningRoof();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => Volatile.Read(ref faulted)
            ? RoofServiceMock.Snapshot(faultLatched: true) with { StatusVersion = 500 }
            : RoofServiceMock.Snapshot());
        using var host = RoofClientApiTests.CreateHost(roof, settings: Warn);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        tui.Ui.Apply(tui.Ui.Status! with { Safety = null, StatusVersion = tui.Ui.Status.StatusVersion + 100 });
        Volatile.Write(ref faulted, true);

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the refusal", () => tui.Ui.Status?.IsFaultLatched == true && tui.Ui.Message.Length > 0 && !tui.Ui.Message.StartsWith("Sending", StringComparison.Ordinal));
        Settle(tui);

        tui.Ui.Panel.Should().BeNull("Open is blocked by the fault, whatever the conditions");
        page.BlockReason(RoofMotionDirection.Opening).Should().Be(RoofCommandRules.FaultLatched);
        roof.Verify(service => service.Open(), Times.Never());
    }

    [TestMethod]
    public void ARefusalForWantOfTheOverride_AsksThen()
    {
        // The status the interface shows says nothing of the conditions (an older controller's), so it asks only once the
        // controller refuses.
        var roof = OpeningRoof();
        using var host = RoofClientApiTests.CreateHost(roof, settings: Warn);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;
        tui.Ui.Apply(tui.Ui.Status! with { Safety = null, StatusVersion = tui.Ui.Status.StatusVersion + 100 });

        page.OpenButton.InvokeCommand(Command.Accept);
        tui.WaitIdle("the refusal and the question", () => tui.Ui.Panel is not null);

        tui.Ui.Panel!.Prompt.Title.Should().Be(RoofSafetyText.OverrideTitle);
        tui.Ui.Panel.Prompt.Message.Should().StartWith("The safety conditions are UNKNOWN: No safety sources configured.");
        tui.Ui.Message.Should().StartWith("The conditions are not confirmed safe. Open needs a safety override.");
        roof.Verify(service => service.Open(), Times.Never());
    }

    [TestMethod]
    public void TheRoofState_ListsTheConditions_AndEachSource()
    {
        var mount = new SimulatedTelescopeParkSource("Main", isParked: false);
        using var host = RoofClientApiTests.CreateHost(
            configureServices: services => services.AddSingleton<ITelescopeParkSource>(mount),
            settings: Warn);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);
        using var tui = Started(rig);
        var page = (RoofUiRoofPage)tui.Ui.CurrentPage;

        tui.WaitIdle("the mount's reading", () => tui.Ui.Status?.Safety?.State == RoofSafetyState.Unsafe);
        page.StatusChanged();

        page.Describe().Should().Contain("Safety:  UNSAFE: Telescope 'Main' is not parked (Open needs a safety override)")
            .And.Contain("Safety source:  Unsafe: Telescope 'Main' is not parked");
    }

    private static Mock<HVO.RoofControllerV4.RPi.Logic.IRoofControllerServiceV4> OpeningRoof()
    {
        var moving = false;
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => Volatile.Read(ref moving)
            ? RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = 30 }
            : RoofServiceMock.Snapshot());
        roof.Setup(service => service.Open()).Returns(() =>
        {
            Volatile.Write(ref moving, true);
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Opening);
        });
        roof.Setup(service => service.RenewLease()).Returns(() => Result<RoofStatusResponse>.Success(
            RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { LeaseSecondsRemaining = 30 }));
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(() =>
        {
            Volatile.Write(ref moving, false);
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.Stopped);
        });
        return roof;
    }

    /// <summary>Runs the loop for long enough that a request sent now would have reached the controller.</summary>
    private static void Settle(TuiDriver tui)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
        while (DateTime.UtcNow < until)
        {
            tui.Pump();
            Thread.Sleep(10);
        }
    }

    /// <summary>Holds each Open on its way to the controller until the test releases it.</summary>
    private sealed class HoldingHandler(HttpMessageHandler inner, TaskCompletionSource held, TaskCompletionSource release) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/Open", StringComparison.Ordinal))
            {
                held.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private static TuiDriver Started(CliRig rig)
    {
        var tui = new TuiDriver(rig);
        try
        {
            tui.WaitIdle("the first status", () => tui.Ui.Status is not null && !tui.Ui.IsStale && tui.Ui.Caller is not null);
            return tui;
        }
        catch
        {
            tui.Dispose();
            throw;
        }
    }
}
