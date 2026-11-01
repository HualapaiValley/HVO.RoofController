using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The operator lease keeper (#220): a heartbeat carries whether the person did anything since the last one, and a grant
/// sent through <see cref="RoofOperatorLeaseKeeper.StepAwayAsync{T}"/> is never crossed by a heartbeat, and forgets the
/// input noted before it (the click that asked for it), so the controller, which ends the person's lease with the grant,
/// is not handed that click as a reason to take it back and drop the grant.
/// </summary>
[TestClass]
public sealed class RoofOperatorLeaseKeeperTests
{
    private sealed class ScriptedController
    {
        public ConcurrentQueue<string> Requests { get; } = new();

        public TaskCompletionSource? HoldGrant { get; set; }

        public TaskCompletionSource? HoldBeat { get; set; }

        public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Enqueue($"{request.Method} {path}");
            if (path.Contains("/grants", StringComparison.OrdinalIgnoreCase))
            {
                if (HoldGrant is { } grant)
                {
                    await grant.Task.WaitAsync(cancellationToken);
                }

                return Json(new RoofOverrideGrant { Id = "g1", Kind = RoofAlpacaCommandKind.Close, ReasonKeys = ["scope-not-clear:Main"] }, HttpStatusCode.Created);
            }

            if (HoldBeat is { } beat)
            {
                await beat.Task.WaitAsync(cancellationToken);
            }

            return Json(new RoofOperatorLeaseState { Held = true, Holder = "ada" }, HttpStatusCode.OK);
        }

        private static HttpResponseMessage Json<T>(T body, HttpStatusCode status)
            => new(status) { Content = JsonContent.Create(body, options: JsonSerializerOptions.Web) };
    }

    private static (RoofControllerClient Client, ScriptedController Controller) Create()
    {
        var controller = new ScriptedController();
        var client = ClientTestSupport.CreateClient(
            () => throw new InvalidOperationException("No server: the handler answers."),
            new RoofSessionCredential("test-session-token-not-a-secret", "ada", "RoofOperator"),
            handler: () => new FailingHandler(controller.RespondAsync));
        return (client, controller);
    }

    private static bool BeatHadInput(string request) => request.Contains("input=true", StringComparison.Ordinal);

    [TestMethod]
    public async Task AGrant_ThroughStepAway_ForgetsTheInputNotedBeforeIt_AndLaterInputCountsAgain()
    {
        var (client, controller) = Create();
        using var _ = client;
        await using var keeper = new RoofOperatorLeaseKeeper(client);
        await keeper.BeatAsync();
        keeper.NoteInput(); // the click on "Allow this Close once"

        await keeper.StepAwayAsync(token => client.Alpaca.GrantAsync(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"], token));
        await keeper.BeatAsync();
        keeper.NoteInput(); // the person comes back
        await keeper.BeatAsync();

        var beats = controller.Requests.Where(request => request.Contains("/lease", StringComparison.OrdinalIgnoreCase)).ToArray();
        beats.Should().HaveCount(3);
        BeatHadInput(beats[0]).Should().BeTrue("the first heartbeat takes the lease");
        BeatHadInput(beats[1]).Should().BeFalse("the click before the grant does not take the lease back");
        BeatHadInput(beats[2]).Should().BeTrue("input after the grant does, which drops the grant");
    }

    [TestMethod]
    public async Task NoHeartbeat_CrossesAGrant()
    {
        var (client, controller) = Create();
        using var _ = client;
        await using var keeper = new RoofOperatorLeaseKeeper(client);
        controller.HoldGrant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        keeper.NoteInput();

        var granting = keeper.StepAwayAsync(token => client.Alpaca.GrantAsync(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"], token));
        await ClientTestSupport.WaitUntilAsync(() => controller.Requests.Any(request => request.Contains("/grants", StringComparison.OrdinalIgnoreCase)), "the grant is sent");
        await keeper.BeatAsync(); // the timer's heartbeat while the grant is answered: skipped, its input kept for later

        controller.Requests.Should().NotContain(request => request.Contains("/lease", StringComparison.OrdinalIgnoreCase));
        controller.HoldGrant.SetResult();
        await granting;
        await keeper.BeatAsync();
        BeatHadInput(controller.Requests.Last()).Should().BeFalse("input noted before the grant answered is forgotten");
    }

    [TestMethod]
    public async Task AGrant_WaitsForAHeartbeatInFlight()
    {
        var (client, controller) = Create();
        using var _ = client;
        await using var keeper = new RoofOperatorLeaseKeeper(client);
        controller.HoldBeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beating = keeper.BeatAsync();
        await ClientTestSupport.WaitUntilAsync(() => !controller.Requests.IsEmpty, "the heartbeat is sent");

        var granting = keeper.StepAwayAsync(token => client.Alpaca.GrantAsync(RoofAlpacaCommandKind.Close, ["scope-not-clear:Main"], token));
        await Task.Delay(100);
        controller.Requests.Should().HaveCount(1, "the grant waits until the heartbeat has been answered");

        controller.HoldBeat.SetResult();
        controller.HoldBeat = null;
        await beating;
        (await granting).Id.Should().Be("g1");
        controller.Requests.Last().Should().Contain("/grants");
    }

    [TestMethod]
    public async Task AFailedGrant_KeepsTheInput()
    {
        var (client, controller) = Create();
        using var _ = client;
        await using var keeper = new RoofOperatorLeaseKeeper(client);
        keeper.NoteInput();

        var act = () => keeper.StepAwayAsync<RoofOverrideGrant>(_ => throw new HttpRequestException("refused (test)"));
        await act.Should().ThrowAsync<HttpRequestException>();
        await keeper.BeatAsync();

        BeatHadInput(controller.Requests.Single()).Should().BeTrue("nothing was granted, so the person is still at the UI");
    }

    [TestMethod]
    public async Task AHeartbeatAfterOrDuringDisposal_EndsQuietly_AndDisposingTwiceIsHarmless()
    {
        var (client, controller) = Create();
        using var _ = client;
        var keeper = new RoofOperatorLeaseKeeper(client);
        await keeper.BeatAsync();
        controller.HoldBeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = keeper.BeatAsync();
        await ClientTestSupport.WaitUntilAsync(() => controller.Requests.Count >= 2, "the second heartbeat is sent");

        await keeper.DisposeAsync();
        await keeper.DisposeAsync();
        await inFlight; // cancelled by the disposal, never ObjectDisposedException
        await keeper.BeatAsync(); // the timer's last tick after disposal: nothing is sent

        controller.Requests.Count(request => request.Contains("/lease", StringComparison.OrdinalIgnoreCase) && request.StartsWith("POST", StringComparison.Ordinal))
            .Should().Be(2);
    }
}
