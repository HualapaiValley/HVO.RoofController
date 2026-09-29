using System.Security.Claims;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>The web UI's Stop key: read once from a file, and never shown.</summary>
[TestClass]
public sealed class WebStopKeyTests
{
    private const string Key = "stop-key-0123456789abcdef";

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void Load_WithoutAFile_IsNone(string? path)
    {
        var key = WebStopKey.Load(path);

        key.Should().BeSameAs(WebStopKey.None);
        key.Value.Should().BeNull();
        key.ToString().Should().Be("no Stop key");
    }

    [TestMethod]
    [DataRow(Key + "\n")]
    [DataRow(Key + "\r\n")]
    [DataRow(Key)]
    public void Load_OneKeyOnOneLine_IsRead_AndNeverShown(string content)
    {
        using var directory = new WebTestSupport.TempDirectory();
        File.WriteAllText(directory.File("stop-key"), content);

        var key = WebStopKey.Load(directory.File("stop-key"));

        key.Value.Should().Be(Key);
        key.ToString().Should().Be("Stop key");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("\n")]
    [DataRow(Key + "\n" + Key + "\n")]
    [DataRow(" " + Key)]
    [DataRow(Key + " ")]
    [DataRow(Key + "é")]
    [DataRow(Key + "\t")]
    public void Load_AFileThatIsNotOneKey_IsRefused_WithoutShowingIt(string content)
    {
        using var directory = new WebTestSupport.TempDirectory();
        File.WriteAllText(directory.File("stop-key"), content);

        var act = () => WebStopKey.Load(directory.File("stop-key"));

        act.Should().Throw<RoofWebSettingsException>()
            .Which.Message.Should().Contain("RoofWeb:StopKeyFile names").And.Contain("one API key on one line").And.NotContain(Key);
    }

    [TestMethod]
    public void Load_AFileThatCannotBeRead_IsRefused()
    {
        using var directory = new WebTestSupport.TempDirectory();

        var act = () => WebStopKey.Load(directory.File("missing"));

        act.Should().Throw<RoofWebSettingsException>().Which.Message.Should().Contain("could not be read (FileNotFoundException)");
    }
}

/// <summary>A person's credential in the web UI: their session everywhere, and the Stop key too for Stop.</summary>
[TestClass]
public sealed class RoofWebCredentialTests
{
    private const string StopKey = "stop-key-0123456789abcdef";

    [TestMethod]
    public void WithoutAStopKey_EveryUse_SendsTheSessionOnly()
    {
        var credential = new RoofWebCredential(Session("ada"), stopKey: null);

        credential.HasStopKey.Should().BeFalse();
        foreach (var use in Enum.GetValues<RoofCredentialUse>())
        {
            Headers(credential, use).Should().Equal("Authorization: Bearer token-ada");
        }
    }

    [TestMethod]
    public void WithAStopKey_Stop_AlsoSendsTheKey_OnBehalfOfThePerson()
    {
        var credential = new RoofWebCredential(Session("ada"), StopKey);

        credential.HasStopKey.Should().BeTrue();
        Headers(credential, RoofCredentialUse.Stop).Should().Equal(
            "Authorization: Bearer token-ada",
            $"{RoofControllerApiContract.ApiKeyHeaderName}: {StopKey}",
            $"{RoofIdentityContract.OnBehalfOfHeaderName}: ada");
        credential.GetHeaders(RoofCredentialUse.Request).Should().ContainSingle("only Stop carries the key");
        credential.GetHeaders(RoofCredentialUse.StatusHub).Should().ContainSingle();
        credential.ToString().Should().Be("web UI session for ada").And.NotContain(StopKey).And.NotContain("token-ada");
    }

    [TestMethod]
    public void WithAStopKey_ASessionWithoutAUsableName_SendsTheKeyWithoutNamingAnyone()
    {
        var credential = new RoofWebCredential(Session(name: null), StopKey);

        credential.GetHeaders(RoofCredentialUse.Stop).Select(header => header.Key).ToList()
            .Should().Equal("Authorization", RoofControllerApiContract.ApiKeyHeaderName);
    }

    [TestMethod]
    [DataRow("key\nX-Evil: 1")]
    [DataRow("")]
    public void AStopKeyThatCannotBeAHeader_IsRefused(string stopKey)
    {
        var act = () => new RoofWebCredential(Session("ada"), stopKey);

        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Refused_EndsTheSession()
    {
        var session = Session("ada");
        RoofCredential credential = new RoofWebCredential(session, StopKey);

        credential.OnRefused(RoofCredentialUse.Stop).Should().BeFalse("there is nothing to retry with");

        session.IsEnded.Should().BeTrue();
    }

    private static List<string> Headers(RoofCredential credential, RoofCredentialUse use)
        => credential.GetHeaders(use).Select(header => $"{header.Key}: {header.Value}").ToList();

    private static RoofSessionCredential Session(string? name)
        => new("token-ada", name, RoofControllerApiContract.AdminRole, "session-1", DateTimeOffset.UtcNow.AddHours(1));
}

/// <summary>The web UI's sessions: held in memory, picked up again from cookies, forgotten when they expire.</summary>
[TestClass]
public sealed class WebSessionStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Open_HoldsTheSession_WithItsRoleAndClient()
    {
        using var fixture = new Fixture();

        var session = fixture.Store.Open(Credential("session-1", "ada", "roofadmin"));

        session.Id.Should().Be("session-1");
        session.Name.Should().Be("ada");
        session.Role.Should().Be(RoofControllerApiContract.AdminRole, "the role is the controller's own name for it");
        session.ExpiresUtc.Should().Be(Start.AddHours(12));
        session.Credential.HasStopKey.Should().BeFalse();
        session.IsOpen.Should().BeTrue();
        session.ToString().Should().Be("web session session-1 for ada");
        fixture.Store.TryGet("session-1", out var found).Should().BeTrue();
        found.Should().BeSameAs(session);
        fixture.Store.TryGet(null, out _).Should().BeFalse();
        fixture.Store.TryGet("session-2", out _).Should().BeFalse();
    }

    [TestMethod]
    public void Open_WithTheStopKey_GivesStopTheKey()
    {
        using var directory = new WebTestSupport.TempDirectory();
        File.WriteAllText(directory.File("stop-key"), "stop-key-0123456789abcdef\n");
        using var fixture = new Fixture(WebStopKey.Load(directory.File("stop-key")));

        fixture.Store.Open(Credential("session-1", "ada")).Credential.HasStopKey.Should().BeTrue();
    }

    [TestMethod]
    [DataRow("RoofOwner")]
    [DataRow(null)]
    public void Open_ARoleTheWebUiDoesNotKnow_IsRefused(string? role)
    {
        using var fixture = new Fixture();

        var act = () => fixture.Store.Open(new RoofSessionCredential("token", "ada", role, "session-1", Start.AddHours(1)));

        act.Should().Throw<ArgumentException>().WithMessage("*role*");
        fixture.Store.Count.Should().Be(0);
    }

    [TestMethod]
    public void Open_ASessionWithoutAnIdentifierOrExpiry_IsRefused()
    {
        using var fixture = new Fixture();

        FluentActions.Invoking(() => fixture.Store.Open(new RoofSessionCredential("token", "ada", RoofControllerApiContract.AdminRole, null, Start.AddHours(1))))
            .Should().Throw<ArgumentException>().WithMessage("*identifier*");
        FluentActions.Invoking(() => fixture.Store.Open(new RoofSessionCredential("token", "ada", RoofControllerApiContract.AdminRole, "session-1", null)))
            .Should().Throw<ArgumentException>().WithMessage("*expiry*");
        FluentActions.Invoking(() => fixture.Store.Open(new RoofSessionCredential("token", null, RoofControllerApiContract.AdminRole, "session-1", Start.AddHours(1))))
            .Should().Throw<ArgumentException>().WithMessage("*nobody*");
    }

    [TestMethod]
    public void End_ClosesTheSession_Once_AndRemembersWhen()
    {
        using var fixture = new Fixture();
        var session = fixture.Store.Open(Credential("session-1", "ada"));
        var ended = 0;
        session.Ended += (_, _) => ended++;
        fixture.Time.Advance(TimeSpan.FromMinutes(5));

        session.End();
        session.End();

        ended.Should().Be(1);
        session.IsEnded.Should().BeTrue();
        session.IsOpen.Should().BeFalse();
        session.EndedAt.Should().Be(Start.AddMinutes(5));
        fixture.Store.TryGet("session-1", out _).Should().BeFalse();
    }

    [TestMethod]
    public void ARefusedSession_Ends()
    {
        using var fixture = new Fixture();
        var session = fixture.Store.Open(Credential("session-1", "ada"));

        ((RoofCredential)session.Credential).OnRefused(RoofCredentialUse.Request);

        session.IsEnded.Should().BeTrue();
        fixture.Store.TryGet("session-1", out _).Should().BeFalse();
    }

    [TestMethod]
    public void AnExpiredSession_IsNotOpen()
    {
        using var fixture = new Fixture();
        var session = fixture.Store.Open(Credential("session-1", "ada"));

        fixture.Time.AdvanceWithoutTimers(TimeSpan.FromHours(12));

        session.IsOpen.Should().BeFalse();
        session.IsEnded.Should().BeFalse("it expired, nobody ended it");
        fixture.Store.TryGet("session-1", out _).Should().BeFalse();
    }

    [TestMethod]
    public void TryResume_ASessionTheWebUiDoesNotHold_PicksItUpFromTheCookie_Once()
    {
        using var fixture = new Fixture();
        var ticket = new WebTicket("session-7", "token-7", "olga", RoofControllerApiContract.OperatorRole, Start.AddHours(2));

        fixture.Store.TryResume(ticket, out var session).Should().BeTrue();
        fixture.Store.TryResume(ticket, out var again).Should().BeTrue();

        again.Should().BeSameAs(session);
        session.Name.Should().Be("olga");
        session.Role.Should().Be(RoofControllerApiContract.OperatorRole);
        session.Credential.Session.Token.Should().Be("token-7");
        session.ExpiresUtc.Should().Be(Start.AddHours(2));
        fixture.Logs.Entries.Where(entry => entry.Message.Contains("picked up again", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Message.Should().Be("Web session for olga (RoofOperator) picked up again from its cookie")
            .And.NotContain("token-7");
    }

    [TestMethod]
    public void TryResume_AnEndedSession_IsRefused_SoItsCookieCannotBringItBack()
    {
        using var fixture = new Fixture();
        var session = fixture.Store.Open(Credential("session-1", "ada"));
        var ticket = Ticket(session);
        session.End();

        fixture.Store.TryResume(ticket, out _).Should().BeFalse();

        fixture.Time.Advance(WebSessionStore.EndedGrace * 3);
        fixture.Store.TryResume(ticket, out _).Should().BeFalse("an ended session is remembered until it would have expired");
    }

    [TestMethod]
    public void TryResume_AnExpiredCookie_IsRefused_AndNotHeld()
    {
        using var fixture = new Fixture();
        var ticket = new WebTicket("session-7", "token-7", "olga", RoofControllerApiContract.OperatorRole, Start);

        fixture.Store.TryResume(ticket, out _).Should().BeFalse();

        fixture.Store.Count.Should().Be(0);
    }

    [TestMethod]
    public async Task Sweep_ClosesAnEndedSessionsClient_AfterTheGrace_AndForgetsExpiredSessions()
    {
        using var fixture = new Fixture();
        var ended = fixture.Store.Open(Credential("session-1", "ada"));
        var open = fixture.Store.Open(Credential("session-2", "olga", RoofControllerApiContract.OperatorRole, TimeSpan.FromHours(1)));
        ended.End();

        fixture.Time.Advance(WebSessionStore.EndedGrace - TimeSpan.FromSeconds(1));
        await ended.Client.Auth.GetCallerAsync().ContinueWith(_ => { }, TaskScheduler.Default);
        fixture.Controller.Requests.Should().HaveCount(1, "an ended session's client stays usable for requests under way");

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        var afterGrace = () => ended.Client.Auth.GetCallerAsync();
        await afterGrace.Should().ThrowAsync<ObjectDisposedException>();
        fixture.Store.Count.Should().Be(2, "the ended session is remembered until it would have expired");

        fixture.Time.Advance(TimeSpan.FromHours(1));
        fixture.Store.Count.Should().Be(1, "the other session expired");
        fixture.Store.TryGet(open.Id, out _).Should().BeFalse();
        var expired = () => open.Client.Auth.GetCallerAsync();
        await expired.Should().ThrowAsync<ObjectDisposedException>();

        fixture.Time.Advance(TimeSpan.FromHours(11));
        fixture.Store.Count.Should().Be(0);
    }

    [TestMethod]
    public async Task Open_TheSameSessionAgain_ClosesTheOldClient()
    {
        using var fixture = new Fixture();
        var first = fixture.Store.Open(Credential("session-1", "ada"));

        var second = fixture.Store.Open(Credential("session-1", "ada"));

        fixture.Store.TryGet("session-1", out var held).Should().BeTrue();
        held.Should().BeSameAs(second);
        var act = () => first.Client.Auth.GetCallerAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [TestMethod]
    public void WebTicket_ToString_LeavesTheTokenOut()
    {
        var ticket = new WebTicket("session-7", "token-secret", "olga", RoofControllerApiContract.OperatorRole, Start);

        ticket.ToString().Should().Contain("session-7").And.Contain("olga").And.NotContain("token-secret");
    }

    [TestMethod]
    public void Principal_CarriesTheNameTheSessionAndEveryImpliedRole_AndTheCookieCarriesTheToken()
    {
        using var fixture = new Fixture();
        var session = fixture.Store.Open(Credential("session-1", "olga", RoofControllerApiContract.OperatorRole));

        var principal = WebAuthentication.CreatePrincipal(session);
        var properties = WebAuthentication.CreateProperties(session);

        principal.Identity!.Name.Should().Be("olga");
        principal.Identity.AuthenticationType.Should().Be(WebAuthentication.Scheme);
        principal.IsInRole(WebRoles.Operator).Should().BeTrue();
        principal.IsInRole(WebRoles.Viewer).Should().BeTrue();
        principal.IsInRole(WebRoles.Admin).Should().BeFalse();
        principal.Claims.Select(claim => claim.Value).Should().NotContain("token-olga", "the token is never a claim");
        WebAuthentication.GetSessionId(principal).Should().Be("session-1");
        properties.IsPersistent.Should().BeFalse();
        properties.AllowRefresh.Should().BeFalse();
        properties.ExpiresUtc.Should().Be(session.ExpiresUtc);
        WebAuthentication.ReadTicket(principal, properties).Should().Be(
            new WebTicket("session-1", "token-olga", "olga", RoofControllerApiContract.OperatorRole, session.ExpiresUtc));
    }

    [TestMethod]
    public void ReadTicket_WithoutEverythingItNeeds_IsNull()
    {
        using var fixture = new Fixture();
        var session = fixture.Store.Open(Credential("session-1", "ada"));
        var principal = WebAuthentication.CreatePrincipal(session);
        var properties = WebAuthentication.CreateProperties(session);

        WebAuthentication.ReadTicket(null, properties).Should().BeNull();
        WebAuthentication.ReadTicket(principal, null).Should().BeNull();
        WebAuthentication.ReadTicket(new ClaimsPrincipal(new ClaimsIdentity()), properties).Should().BeNull();
        properties.ExpiresUtc = null;
        WebAuthentication.ReadTicket(principal, properties).Should().BeNull();
        var withoutToken = WebAuthentication.CreateProperties(session);
        withoutToken.Items.Clear();
        WebAuthentication.ReadTicket(principal, withoutToken).Should().BeNull();
    }

    internal static RoofSessionCredential Credential(string id, string name, string role = RoofControllerApiContract.AdminRole, TimeSpan? lifetime = null)
        => new($"token-{name}", name, role, id, Start + (lifetime ?? TimeSpan.FromHours(12)));

    private static WebTicket Ticket(WebSession session)
        => WebAuthentication.ReadTicket(WebAuthentication.CreatePrincipal(session), WebAuthentication.CreateProperties(session))!;

    internal sealed class Fixture : IDisposable
    {
        public Fixture(WebStopKey? stopKey = null)
        {
            Time = new ManualTimeProvider(Start);
            Controller = new FakeController(Time);
            var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(Logs));
            var connector = new FakeControllerConnector(Controller, Options.Create(new RoofWebOptions()), loggerFactory, Time);
            Store = new WebSessionStore(stopKey ?? WebStopKey.None, connector, Time, loggerFactory.CreateLogger<WebSessionStore>());
        }

        public ManualTimeProvider Time { get; }

        public FakeController Controller { get; }

        public RecordingLoggerProvider Logs { get; } = new();

        public WebSessionStore Store { get; }

        public void Dispose() => Store.Dispose();
    }
}

/// <summary>A live page is signed out as soon as its session ends.</summary>
[TestClass]
public sealed class WebAuthenticationStateProviderTests
{
    [TestMethod]
    public async Task WhenTheSessionEnds_ThePageIsSignedOut()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        using var provider = new WebAuthenticationStateProvider(NullLoggerFactory.Instance, fixture.Store);
        var changes = new List<bool>();
        provider.AuthenticationStateChanged += async task => changes.Add((await task).User.Identity?.IsAuthenticated == true);
        provider.SetAuthenticationState(SignedIn(session));
        (await IsSignedInAsync(provider)).Should().BeTrue();

        session.End();

        (await IsSignedInAsync(provider)).Should().BeFalse();
        changes.Should().Equal(true, false);
        provider.PersonSignedOut.Should().BeFalse("the session ended without the person signing out");
    }

    [TestMethod]
    public async Task WhenThePersonSignsOut_ThePageIsSignedOut_AndKnowsTheyDid()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        using var provider = new WebAuthenticationStateProvider(NullLoggerFactory.Instance, fixture.Store);
        provider.SetAuthenticationState(SignedIn(session));

        await session.SignOutAsync();

        session.SignedOut.Should().BeTrue();
        session.IsEnded.Should().BeTrue();
        fixture.Controller.Logged(HttpMethod.Delete, RoofApiRoutesTest.Session).Should().ContainSingle("the controller ends the session too");
        (await IsSignedInAsync(provider)).Should().BeFalse();
        provider.PersonSignedOut.Should().BeTrue();
    }

    [TestMethod]
    public async Task APersonWhoSignsOut_IsSignedOutHere_EvenWhenTheControllerDoesNotAnswer()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        using var provider = new WebAuthenticationStateProvider(NullLoggerFactory.Instance, fixture.Store);
        provider.SetAuthenticationState(SignedIn(session));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var signOut = () => session.SignOutAsync(cancelled.Token);

        await signOut.Should().ThrowAsync<OperationCanceledException>();
        session.IsEnded.Should().BeTrue();
        provider.PersonSignedOut.Should().BeTrue();
        (await IsSignedInAsync(provider)).Should().BeFalse();
    }

    [TestMethod]
    public async Task ASessionThatEndedBeforeThePageWatchedIt_SignsThePageOut()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        using var provider = new WebAuthenticationStateProvider(NullLoggerFactory.Instance, fixture.Store);
        session.End();

        provider.SetAuthenticationState(SignedIn(session));

        (await IsSignedInAsync(provider)).Should().BeFalse();
    }

    [TestMethod]
    public async Task ASessionTheWebUiDoesNotHold_SignsThePageOut()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        using var other = new WebSessionStoreTests.Fixture();
        var elsewhere = other.Store.Open(WebSessionStoreTests.Credential("session-9", "ada"));
        using var provider = new WebAuthenticationStateProvider(NullLoggerFactory.Instance, fixture.Store);

        provider.SetAuthenticationState(SignedIn(elsewhere));

        (await IsSignedInAsync(provider)).Should().BeFalse();
    }

    [TestMethod]
    public async Task AfterTheNextSignIn_TheFirstSessionIsNoLongerWatched()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        var first = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        var second = fixture.Store.Open(WebSessionStoreTests.Credential("session-2", "ada"));
        using var provider = new WebAuthenticationStateProvider(NullLoggerFactory.Instance, fixture.Store);
        provider.SetAuthenticationState(SignedIn(first));
        provider.SetAuthenticationState(SignedIn(second));

        first.End();

        (await IsSignedInAsync(provider)).Should().BeTrue("the page now belongs to the second session");
        second.End();
        (await IsSignedInAsync(provider)).Should().BeFalse();
    }

    [TestMethod]
    public async Task Disposed_StopsWatching()
    {
        using var fixture = new WebSessionStoreTests.Fixture();
        var session = fixture.Store.Open(WebSessionStoreTests.Credential("session-1", "ada"));
        var provider = new WebAuthenticationStateProvider(NullLoggerFactory.Instance, fixture.Store);
        provider.SetAuthenticationState(SignedIn(session));
        ((IDisposable)provider).Dispose();

        session.End();

        (await IsSignedInAsync(provider)).Should().BeTrue("a closed page is left alone");
    }

    private static Task<AuthenticationState> SignedIn(WebSession session)
        => Task.FromResult(new AuthenticationState(WebAuthentication.CreatePrincipal(session)));

    private static async Task<bool> IsSignedInAsync(AuthenticationStateProvider provider)
        => (await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated == true;
}
