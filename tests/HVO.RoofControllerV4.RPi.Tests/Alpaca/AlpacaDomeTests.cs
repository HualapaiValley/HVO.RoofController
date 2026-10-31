using System.Diagnostics;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Alpaca.Protocol;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>
/// The roof as Alpaca Dome 0 (#220, phase 1): what it reads from the controller's status, who must connect for what,
/// AbortSlew as the controller's Stop (always allowed), and Open and Close refused without reaching the controller.
/// </summary>
[TestClass]
public sealed class AlpacaDomeTests
{
    [TestMethod]
    public async Task TheCommonMembers_AnswerBeforeConnecting()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost());

        (await alpaca.GetAsync("connected")).GetProperty("Value").GetBoolean().Should().BeFalse();
        (await alpaca.GetAsync("connecting")).GetProperty("Value").GetBoolean().Should().BeFalse();
        (await alpaca.GetAsync("interfaceversion")).GetProperty("Value").GetInt32().Should().Be(3);
        (await alpaca.GetAsync("name")).GetProperty("Value").GetString().Should().Be(AlpacaDome.DeviceName);
        var description = (await alpaca.GetAsync("description")).GetProperty("Value").GetString();
        description.Should().NotBeNullOrWhiteSpace();
        description!.Length.Should().BeLessThanOrEqualTo(64, "ASCOM limits Description to 64 characters");
        (await alpaca.GetAsync("driverversion")).GetProperty("Value").GetString().Should().MatchRegex(@"^\d+\.\d+$", "DriverVersion is major.minor");
        (await alpaca.GetAsync("driverinfo")).GetProperty("Value").GetString().Should().Contain(RoofProductVersionOf());
        (await alpaca.GetAsync("supportedactions")).GetProperty("Value").EnumerateArray().Select(a => a.GetString()).Should().Equal(AlpacaDome.StatusAction);
        var state = (await alpaca.GetAsync("devicestate")).GetProperty("Value").EnumerateArray()
            .ToDictionary(item => item.GetProperty("Name").GetString()!, item => item.GetProperty("Value"));
        state.Keys.Should().BeEquivalentTo("ShutterStatus", "Slewing", "TimeStamp");
        state["ShutterStatus"].GetInt32().Should().Be((int)DomeShutterState.Closed);
        state["Slewing"].GetBoolean().Should().BeFalse();
        DateTime.TryParse(state["TimeStamp"].GetString(), out _).Should().BeTrue();
    }

    [TestMethod]
    [DataRow("shutterstatus")]
    [DataRow("slewing")]
    [DataRow("cansetshutter")]
    [DataRow("canpark")]
    [DataRow("slaved")]
    public async Task TheRoofsMembers_NeedTheClientConnected(string member)
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost());

        var answer = await alpaca.GetAsync(member);

        answer.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.NotConnected);
        answer.GetProperty("ErrorMessage").GetString().Should().NotBeNullOrWhiteSpace();
        answer.TryGetProperty("Value", out _).Should().BeTrue("a property's error still carries a Value");
    }

    [TestMethod]
    public async Task AnErrorFromShutterStatus_CarriesTheErrorState_AsItsValue()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost());

        var answer = await alpaca.GetAsync("shutterstatus");

        answer.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.NotConnected);
        answer.GetProperty("Value").GetInt32().Should().Be((int)DomeShutterState.Error, "a client that overlooks ErrorNumber must not see an open or closed roof");
    }

    [TestMethod]
    public async Task AConnectedClient_ReadsTheRoof_AndTheCapabilitiesOfARollOffRoof()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost());
        await alpaca.ConnectAsync();

        (await alpaca.GetAsync("connected")).GetProperty("Value").GetBoolean().Should().BeTrue();
        (await alpaca.GetAsync("shutterstatus")).GetProperty("Value").GetInt32().Should().Be((int)DomeShutterState.Closed);
        (await alpaca.GetAsync("slewing")).GetProperty("Value").GetBoolean().Should().BeFalse();
        (await alpaca.GetAsync("cansetshutter")).GetProperty("Value").GetBoolean().Should().BeTrue();
        foreach (var capability in new[] { "canfindhome", "canpark", "cansetaltitude", "cansetazimuth", "cansetpark", "canslave", "cansyncazimuth" })
        {
            (await alpaca.GetAsync(capability)).GetProperty("Value").GetBoolean().Should().BeFalse(capability);
        }

        (await alpaca.GetAsync("slaved")).GetProperty("Value").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    public async Task ConnectionIsPerClient_AndDisconnectTouchesNothing()
    {
        var roof = RoofServiceMock.Create();
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof));
        await alpaca.ConnectAsync(clientId: 1);

        (await alpaca.GetAsync("shutterstatus", "ClientID=1")).GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        (await alpaca.GetAsync("shutterstatus", "ClientID=2")).GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.NotConnected);

        await alpaca.PutAsync("disconnect", "ClientID=1");

        (await alpaca.GetAsync("connected", "ClientID=1")).GetProperty("Value").GetBoolean().Should().BeFalse();
        roof.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never(), "disconnecting never stops the roof");
        roof.Verify(service => service.Open(), Times.Never());
        roof.Verify(service => service.Close(), Times.Never());
    }

    [TestMethod]
    [DataRow(RoofControllerStatus.Open, DomeShutterState.Open)]
    [DataRow(RoofControllerStatus.Closed, DomeShutterState.Closed)]
    [DataRow(RoofControllerStatus.Opening, DomeShutterState.Opening)]
    [DataRow(RoofControllerStatus.Closing, DomeShutterState.Closing)]
    [DataRow(RoofControllerStatus.PartiallyOpen, DomeShutterState.Error)]
    [DataRow(RoofControllerStatus.Error, DomeShutterState.Error)]
    public async Task ShutterStatus_FollowsTheControllersStatus(RoofControllerStatus status, DomeShutterState expected)
    {
        var motion = status switch
        {
            RoofControllerStatus.Opening => RoofMotionDirection.Opening,
            RoofControllerStatus.Closing => RoofMotionDirection.Closing,
            _ => RoofMotionDirection.None,
        };
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => RoofServiceMock.Snapshot(status, motion));
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof));
        await alpaca.ConnectAsync();

        (await alpaca.GetAsync("shutterstatus")).GetProperty("Value").GetInt32().Should().Be((int)expected);
        (await alpaca.GetAsync("slewing")).GetProperty("Value").GetBoolean().Should().Be(motion != RoofMotionDirection.None);
    }

    [TestMethod]
    [DataRow("altitude")]
    [DataRow("athome")]
    [DataRow("atpark")]
    [DataRow("azimuth")]
    public async Task TheMembersARollOffRoofHasNot_AreNotImplemented(string member)
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost());
        await alpaca.ConnectAsync();

        var answer = await alpaca.GetAsync(member);

        answer.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.NotImplemented);
        answer.TryGetProperty("Value", out _).Should().BeTrue();
    }

    [TestMethod]
    [DataRow("findhome", "")]
    [DataRow("park", "")]
    [DataRow("setpark", "")]
    [DataRow("slewtoazimuth", "&Azimuth=90")]
    [DataRow("slewtoaltitude", "&Altitude=45")]
    [DataRow("synctoazimuth", "&Azimuth=90")]
    [DataRow("slaved", "&Slaved=true")]
    [DataRow("commandblind", "&Command=x&Raw=false")]
    [DataRow("commandbool", "&Command=x&Raw=false")]
    [DataRow("commandstring", "&Command=x&Raw=false")]
    public async Task TheMethodsARollOffRoofHasNot_AreNotImplemented_AndNeverReachTheController(string member, string parameters)
    {
        var roof = RoofServiceMock.Create();
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof));
        await alpaca.ConnectAsync();

        var answer = await alpaca.PutAsync(member, "ClientID=1" + parameters);

        answer.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.NotImplemented);
        roof.Verify(service => service.Open(), Times.Never());
        roof.Verify(service => service.Close(), Times.Never());
        roof.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    [DataRow("slewtoazimuth", "Azimuth=north")]
    [DataRow("slewtoazimuth", "azimuth=90")]
    [DataRow("slaved", "Slaved=maybe")]
    [DataRow("action", "Action=status")]
    public async Task AMissingOrMalformedParameter_IsAMalformedRequest(string member, string form)
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost());
        await alpaca.ConnectAsync();

        using var response = await alpaca.PutRawAsync(member, "ClientID=1&" + form);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [TestMethod]
    public async Task SlavedFalse_IsAccepted()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost());
        await alpaca.ConnectAsync();

        (await alpaca.PutAsync("slaved", "Slaved=false&ClientID=1")).GetProperty("ErrorNumber").GetInt32().Should().Be(0);
    }

    [TestMethod]
    [DataRow("openshutter")]
    [DataRow("closeshutter")]
    public async Task OpenAndClose_AreRefused_AsCommandsDisabled_AndNeverReachTheController(string member)
    {
        var roof = RoofServiceMock.Create();
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof));

        var notConnected = await alpaca.PutAsync(member, "ClientID=1");
        await alpaca.ConnectAsync();
        var connected = await alpaca.PutAsync(member, "ClientID=1");

        notConnected.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.NotConnected);
        connected.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.InvalidOperation);
        connected.GetProperty("ErrorMessage").GetString().Should().Be(AlpacaDome.CommandsDisabled);
        (await alpaca.GetAsync("shutterstatus")).GetProperty("Value").GetInt32().Should().Be((int)DomeShutterState.Closed, "a refused command leaves the roof as it was");
        roof.Verify(service => service.Open(), Times.Never());
        roof.Verify(service => service.Close(), Times.Never());
        alpaca.Controller!.RoofService.Invocations.Select(invocation => invocation.Method.Name)
            .Should().NotIntersectWith(["Open", "Close", "ClearFault", "Stop", "RenewLease"]);
    }

    [TestMethod]
    public async Task AbortSlew_StopsTheRoof_WithoutConnecting()
    {
        var roof = RoofServiceMock.Create();
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof));

        var answer = await alpaca.PutAsync("abortslew", "ClientID=12&ClientTransactionID=34");

        answer.GetProperty("ErrorNumber").GetInt32().Should().Be(0, answer.GetProperty("ErrorMessage").GetString());
        answer.GetProperty("ClientTransactionID").GetUInt32().Should().Be(34);
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
        roof.Verify(service => service.Open(), Times.Never());
        roof.Verify(service => service.Close(), Times.Never());
    }

    [TestMethod]
    public async Task AbortSlew_StopsTheRoof_WithAFaultLatched()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => RoofServiceMock.Snapshot(RoofControllerStatus.Error, faultLatched: true));
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof));
        await alpaca.ConnectAsync();

        (await alpaca.PutAsync("abortslew")).GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        (await alpaca.GetAsync("shutterstatus")).GetProperty("Value").GetInt32().Should().Be((int)DomeShutterState.Error);
        roof.Verify(service => service.Stop(RoofControllerStopReason.NormalStop), Times.Once());
    }

    [TestMethod]
    public async Task AbortSlew_ReportsAStopTheControllerCouldNotVerify_AsAFailure()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "The relays could not be read back (test).")));
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => RoofServiceMock.Snapshot(relayState: RoofRelayRegisterState.Unverified));
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof));

        var answer = await alpaca.PutAsync("abortslew");

        answer.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.RelayStateUnverified);
        answer.GetProperty("ErrorMessage").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task AbortSlew_ReportsARefusal_WithTheControllersCode()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
            new RoofControllerException(RoofControllerErrorCode.HardwareUnavailable, "The relay HAT did not answer (test).")));
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof));

        (await alpaca.PutAsync("abortslew")).GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.HardwareUnavailable);
    }

    [TestMethod]
    public async Task AbortSlew_ToAControllerThatCannotBeReached_IsAFailure()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(controller: null);

        var answer = await alpaca.PutAsync("abortslew");

        answer.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.ControllerUnavailable);
    }

    [TestMethod]
    public async Task AbortSlew_ReturnsWithinASecond_AndTheNextReadReflectsTheStop()
    {
        var roof = RoofServiceMock.Create();
        var moving = true;
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => moving
            ? RoofServiceMock.Snapshot(RoofControllerStatus.Opening, RoofMotionDirection.Opening) with { StatusVersion = 20 }
            : RoofServiceMock.Snapshot(RoofControllerStatus.PartiallyOpen) with { StatusVersion = 21 });
        roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(() =>
        {
            moving = false;
            return Result<RoofControllerStatus>.Success(RoofControllerStatus.PartiallyOpen);
        });
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof));
        await alpaca.ConnectAsync();
        (await alpaca.GetAsync("slewing")).GetProperty("Value").GetBoolean().Should().BeTrue();

        var watch = Stopwatch.StartNew();
        (await alpaca.PutAsync("abortslew")).GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));

        (await alpaca.GetAsync("slewing")).GetProperty("Value").GetBoolean().Should().BeFalse("the Stop's answer is applied at once, before the hub's next message");
        (await alpaca.GetAsync("shutterstatus")).GetProperty("Value").GetInt32().Should().Be((int)DomeShutterState.Error, "a roof stopped part way reads as Error");
    }

    [TestMethod]
    public async Task WithTheControllerUnreachable_ReadsAreErrors_NeverAStaleValue()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(controller: null);
        await alpaca.ConnectAsync();

        var shutter = await alpaca.GetAsync("shutterstatus");
        var state = await alpaca.GetAsync("devicestate");

        shutter.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.ControllerUnavailable);
        shutter.GetProperty("Value").GetInt32().Should().Be((int)DomeShutterState.Error);
        state.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.ControllerUnavailable);
        (await alpaca.GetAsync("name")).GetProperty("ErrorNumber").GetInt32().Should().Be(0, "the common members do not need the controller");
    }

    [TestMethod]
    public async Task WhenTheControllerStops_ReadsBecomeErrors_WithinTheStaleTime()
    {
        var controller = RoofClientApiTests.CreateHost();
        await using var alpaca = await AlpacaTestHost.StartAsync(controller);
        await alpaca.ConnectAsync();
        (await alpaca.GetAsync("shutterstatus")).GetProperty("ErrorNumber").GetInt32().Should().Be(0);

        controller.Dispose();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        int number;
        while ((number = (await alpaca.GetAsync("shutterstatus")).GetProperty("ErrorNumber").GetInt32()) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        number.Should().Be(AlpacaErrors.ControllerUnavailable);
    }

    [TestMethod]
    public async Task AKeyTheControllerRefuses_MakesReadsErrors_ThatSaySo()
    {
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(), apiKey: "a-key-the-controller-does-not-know-00", waitForStatus: false);
        await alpaca.ConnectAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        int number;
        while ((number = (await alpaca.GetAsync("shutterstatus")).GetProperty("ErrorNumber").GetInt32()) != AlpacaErrors.ControllerRefusedKey && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        number.Should().Be(AlpacaErrors.ControllerRefusedKey);
        (await alpaca.PutAsync("abortslew")).GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.ControllerRefusedKey);
    }

    [TestMethod]
    public async Task TheStatusAction_DescribesTheRoof_AndOtherActionsAreNotImplemented()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(() => RoofServiceMock.Snapshot(RoofControllerStatus.Error, faultLatched: true)
            with { LatchedFaultReason = RoofControllerStopReason.SafetyWatchdogTimeout });
        await using var alpaca = await AlpacaTestHost.StartAsync(RoofClientApiTests.CreateHost(roof));

        (await alpaca.PutAsync("action", "Action=status&Parameters=&ClientID=1")).GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.NotConnected);
        await alpaca.ConnectAsync();
        var status = await alpaca.PutAsync("action", "Action=status&Parameters=&ClientID=1");
        var other = await alpaca.PutAsync("action", "Action=clearfault&Parameters=&ClientID=1");

        status.GetProperty("ErrorNumber").GetInt32().Should().Be(0);
        status.GetProperty("Value").GetString().Should().Contain("Shutter: Error").And.Contain("LATCHED");
        other.GetProperty("ErrorNumber").GetInt32().Should().Be(AlpacaErrors.ActionNotImplemented);
        roof.Verify(service => service.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never(), "no action clears a fault");
    }

    private static string RoofProductVersionOf()
        => HVO.RoofControllerV4.Common.RoofProductVersion.WithoutCommit(HVO.RoofControllerV4.Common.RoofProductVersion.Of(typeof(AlpacaDome).Assembly));
}
