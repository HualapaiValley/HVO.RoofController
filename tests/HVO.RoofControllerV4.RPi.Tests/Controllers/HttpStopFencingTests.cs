using System.Diagnostics;
using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Alpaca;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Simulation;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.RoofControllerV4.RPi.Tests.Controllers;

/// <summary>
/// A person's Stop through the real HTTP pipeline (#220, PR #230 review F2): the human Stop hold it sets publishes the
/// Alpaca state to the status hub, and a hub subscriber reads the controller's status for it, which needs the controller's
/// lock. Nothing of that may run on the Stop's own thread: Stop raises the motion fence at once, answers within its bound
/// while a start holds the lock, and no relay is switched on after it, however slow a subscriber is.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class HttpStopFencingTests
{
    [TestMethod]
    public async Task AnHttpStop_WithAHubSubscriberAndASlowChangeHandler_FencesAtOnce_AnswersInBound_AndNothingEnergizes()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(plant => plant with { InitialPosition = 0.15 }) with
        {
            InitialStatus = null,
            RemoteAddress = IPAddress.Loopback
        });
        var service = (RoofControllerServiceV4)rig.Controller;

        // A status hub subscriber, as every UI is, and a slow handler of the Alpaca state's changes.
        using var subscriber = ClientTestSupport.CreateClient(() => rig.App.Server, new RoofApiKeyCredential(TestApiKeys.Operator));
        await using var feed = subscriber.CreateStatusFeed();
        feed.Start();
        await ClientTestSupport.WaitUntilAsync(() => feed.Status is { IsInitialized: true }, "the hub's first status");
        var alpaca = rig.App.Services.GetRequiredService<RoofAlpacaCommands>();
        alpaca.Changed += () => Thread.Sleep(3000);

        // A Close that has passed its checks and holds the controller's lock just before its relay.
        using var paused = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        service.BeforeEnergizeHook = () =>
        {
            service.BeforeEnergizeHook = null;
            paused.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        var relayClosesBefore = RelayContactsClosed(rig);
        using var http = rig.CreateApiClient(TestApiKeys.Operator);
        var close = http.PostAsync("api/v4.0/RoofControl/Close", null);
        paused.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the Close reached its relay");
        var admitted = service.CommandGeneration;

        var watch = Stopwatch.StartNew();
        var stop = http.PostAsync("api/v4.0/RoofControl/Stop", null);
        var fenced = SpinWait.SpinUntil(() => service.CommandGeneration > admitted, TimeSpan.FromSeconds(1));
        var answered = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(3))) == stop;
        var elapsed = watch.Elapsed;
        release.Set();
        using var closeResponse = await close;
        using var stopResponse = await stop;

        fenced.Should().BeTrue("Stop raises the fence before any notification or subscriber");
        answered.Should().BeTrue("Stop's wait for the controller's lock is bounded");
        elapsed.Should().BeLessThan(RoofControllerServiceV4.StopLockWait + TimeSpan.FromMilliseconds(600));
        stopResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the start held the lock past 2 s: the relays are unverified, the stop queued");
        closeResponse.IsSuccessStatusCode.Should().BeFalse("the Stop came after its checks: it is superseded");

        await rig.WaitForControllerAsync(status => !status.IsMoving && status.RelayRegisterState == RoofRelayRegisterState.Verified, "the queued stop to verify the relays off");
        RelayContactsClosed(rig).Should().Be(relayClosesBefore, "no relay contact closed after the Stop");
        rig.Plant.RelayRegister.Should().Be(0);
        alpaca.GetStatus().Hold.Held.Should().BeTrue("a person's Stop sets the human Stop hold");
    }

    [TestMethod]
    public async Task AWebCloseRightAfterAStop_WaitsWithoutTheLock_AndAStopDuringTheWaitAnswersAtOnce()
    {
        // §21.1: a web-UI Close right after a Stop waits out the direction-change delay without holding the controller's
        // lock, so a Stop during the wait answers at once and nothing energizes.
        await using var rig = await EmulatedRoofRig.StartAsync(Scenario.Production(plant => plant with { InitialPosition = 0.15 },
            new Dictionary<string, string?> { ["RoofControllerOptionsV4:DirectionChangeDelay"] = "00:00:02" }) with
        {
            InitialStatus = null,
            RemoteAddress = IPAddress.Loopback
        });
        using var http = rig.CreateApiClient(TestApiKeys.Operator);
        await Task.Delay(TimeSpan.FromSeconds(2)); // the delay since the controller's start
        (await http.PostAsync("api/v4.0/RoofControl/Close", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.PostAsync("api/v4.0/RoofControl/Stop", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var relayClosesBefore = RelayContactsClosed(rig);

        var close = http.PostAsync("api/v4.0/RoofControl/Close", null);
        await rig.WaitForControllerAsync(status => status.IsStartPending, "the Close to wait out the delay");
        var watch = Stopwatch.StartNew();
        using var stop = await http.PostAsync("api/v4.0/RoofControl/Stop", null);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), "the waiting Close holds no lock");
        stop.StatusCode.Should().Be(HttpStatusCode.OK);

        using var closeResponse = await close;
        closeResponse.IsSuccessStatusCode.Should().BeFalse("the Stop superseded the waiting Close");
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        RelayContactsClosed(rig).Should().Be(relayClosesBefore, "no relay closed after the Stop");
        rig.Plant.RelayRegister.Should().Be(0);
    }

    private static int RelayContactsClosed(EmulatedRoofRig rig)
        => rig.Session.Plant.History.Count(e => e.Kind == PlantEventKind.Relay && e.Detail.EndsWith("closed", StringComparison.Ordinal));
}
