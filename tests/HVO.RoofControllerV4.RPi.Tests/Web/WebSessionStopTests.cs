using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.Web.Sessions;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// A person's Stop and status feed, shared by all their pages, against the controller's real API: every page hears of a
/// Stop as it is sent, and the pages share one hub connection while any of them is open.
/// </summary>
[TestClass]
public sealed class WebSessionStopTests
{
    [TestMethod]
    public async Task Stop_IsAnnouncedAsItIsSent_ThenAnswered()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        var session = harness.Session!;
        var heard = new List<string>();
        session.StopSent += (_, _) => heard.Add($"sent {session.StopsSent}, controller stops {harness.Calls("Stop")}");
        session.StopAnswered += (_, result) => heard.Add($"answered {result.Outcome}, controller stops {harness.Calls("Stop")}");

        var first = await session.StopAsync();
        var second = await session.StopAsync();

        foreach (var result in new[] { first, second })
        {
            result.Outcome.Should().Be(RoofStopOutcome.Acknowledged);
            result.Message.Should().Be(RoofStopText.AcknowledgedVerified);
        }

        heard.Should().Equal(
            "sent 1, controller stops 0",
            "answered Acknowledged, controller stops 1",
            "sent 2, controller stops 1",
            "answered Acknowledged, controller stops 2");
        session.StopsSent.Should().Be(2);
    }

    [TestMethod]
    public async Task Stop_AfterTheSessionsClientWasClosed_IsSentOnAClientOfItsOwn()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        var session = harness.Session!;
        session.Client.Dispose();

        var result = await session.StopAsync();

        result.Outcome.Should().Be(RoofStopOutcome.Acknowledged);
        harness.Calls("Stop").Should().Be(1);
    }

    [TestMethod]
    public async Task Stop_ForASessionTheControllerEnded_SaysThePageIsSignedOut()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        var session = harness.Session!;
        // Signed out from another client of the same session (as a password change or an admin would end it).
        using (var other = ClientTestSupport.CreateClient(harness.Host, new RoofSessionCredential(session.Credential.Session.Token)))
        {
            await other.Auth.SignOutAsync();
        }

        var result = await session.StopAsync();

        result.Outcome.Should().Be(RoofStopOutcome.Failed);
        result.Message.Should().Be(RoofStopText.PageSignedOut);
        result.Error.Should().BeOfType<RoofApiException>();
        harness.Calls("Stop").Should().Be(0);
        session.StopsSent.Should().Be(1, "it was sent; the controller refused it");
    }

    [TestMethod]
    public async Task ThePages_ShareOneFeed_ClosedWithTheLastHold()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        var session = harness.Session!;

        var first = session.HoldStatusFeed();
        var second = session.HoldStatusFeed();
        second.Feed.Should().BeSameAs(first.Feed);
        await ClientTestSupport.WaitUntilAsync(() => first.Feed.State == RoofStatusFeedState.Connected, "the feed to connect");

        first.Dispose();
        first.Dispose();
        second.Feed.State.Should().Be(RoofStatusFeedState.Connected, "the other page still holds it");

        second.Dispose();
        await ClientTestSupport.WaitUntilAsync(() => second.Feed.State == RoofStatusFeedState.Stopped, "the feed to close");
        var closed = () => second.Feed.Start();
        closed.Should().Throw<ObjectDisposedException>();

        using var third = session.HoldStatusFeed();
        third.Feed.Should().NotBeSameAs(first.Feed, "the next page starts a new one");
    }

    [TestMethod]
    public async Task AClosedSession_ClosesItsFeed_AndStartsNoOther()
    {
        using var harness = await WebRoofHarness.CreateAsync();
        var session = harness.Session!;
        using var hold = session.HoldStatusFeed();

        session.Dispose();

        await ClientTestSupport.WaitUntilAsync(() => hold.Feed.State == RoofStatusFeedState.Stopped, "the feed to close");
        var another = () => session.HoldStatusFeed();
        another.Should().Throw<ObjectDisposedException>();
    }
}
