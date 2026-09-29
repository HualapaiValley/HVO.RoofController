using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>
/// The Stop pass: issued at sign-in when the web UI has a Stop key, sent by the browser only with Stop, protected, and
/// good until <see cref="RoofWebOptions.StopAfterSessionHours"/> after the session would have expired.
/// </summary>
[TestClass]
public sealed class WebStopPassTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Issue_NamesThePerson_UntilHoursAfterTheirSessionWouldHaveExpired()
    {
        using var fixture = new Fixture(WebTestSupport.StopKey());
        var session = fixture.Open("olga", RoofControllerApiContract.OperatorRole);

        var cookie = fixture.Issue(session);

        var holder = fixture.Pass.Read(Request(cookie));
        holder.Should().Be(new WebStopPassHolder("olga", RoofControllerApiContract.OperatorRole, "session-1", session.ExpiresUtc.AddHours(12)));
        fixture.Pass.IsAvailable.Should().BeTrue();
        fixture.Pass.OutlastsSessions.Should().BeTrue();
    }

    [TestMethod]
    public void TheCookie_IsSentOnlyWithStop_NeverReadByScripts_NorByOtherSites()
    {
        using var fixture = new Fixture(WebTestSupport.StopKey());
        var session = fixture.Open("olga", RoofControllerApiContract.OperatorRole);

        var header = fixture.IssueHeader(session, https: false, pathBase: "/roof");
        var secure = fixture.IssueHeader(session, https: true);

        header.Name.ToString().Should().Be(WebStopPass.CookieName);
        header.HttpOnly.Should().BeTrue();
        header.SameSite.Should().Be(Microsoft.Net.Http.Headers.SameSiteMode.Strict);
        header.Path.ToString().Should().Be("/roof/stop");
        header.Secure.Should().BeFalse();
        header.Expires.Should().BeNull("it is a session cookie; its own expiry is inside it");
        header.Value.ToString().Should().NotContain("olga").And.NotContain("token", "it holds no controller token, and is protected");
        secure.Secure.Should().BeTrue();
        secure.Path.ToString().Should().Be("/stop");
    }

    [TestMethod]
    public void APass_IsGoodUntilItRunsOut_AndNotAfter()
    {
        using var fixture = new Fixture(WebTestSupport.StopKey(), new RoofWebOptions { StopAfterSessionHours = 2 });
        var session = fixture.Open("olga", RoofControllerApiContract.OperatorRole);
        var cookie = fixture.Issue(session);

        fixture.Time.Advance(session.ExpiresUtc.AddHours(2) - Start - TimeSpan.FromSeconds(1));
        fixture.Pass.Read(Request(cookie)).Should().NotBeNull("a second is left");

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        fixture.Pass.Read(Request(cookie)).Should().BeNull("it has run out");
    }

    [TestMethod]
    public void WithNoHoursAfter_APass_EndsWhenTheSessionWouldHaveExpired()
    {
        using var fixture = new Fixture(WebTestSupport.StopKey(), new RoofWebOptions { StopAfterSessionHours = 0 });
        var session = fixture.Open("olga", RoofControllerApiContract.OperatorRole);
        var cookie = fixture.Issue(session);

        fixture.Pass.OutlastsSessions.Should().BeFalse("the pages do not promise Stop after an expired session");
        fixture.Pass.Read(Request(cookie)).Should().NotBeNull("a session ended before it expired still has its Stop");
        fixture.Time.Advance(session.ExpiresUtc - Start);
        fixture.Pass.Read(Request(cookie)).Should().BeNull();
    }

    [TestMethod]
    public void APassThatWasChanged_OrIsAnotherWebUis_OrIsNotAPass_IsNotRead()
    {
        using var fixture = new Fixture(WebTestSupport.StopKey());
        var cookie = fixture.Issue(fixture.Open("olga", RoofControllerApiContract.OperatorRole));
        using var other = new Fixture(WebTestSupport.StopKey());
        var tampered = cookie[..^4] + (cookie[^4] == 'A' ? 'B' : 'A') + cookie[^3..];

        fixture.Pass.Read(Request(tampered)).Should().BeNull();
        other.Pass.Read(Request(cookie)).Should().BeNull("another web UI's keys did not protect it");
        fixture.Pass.Read(Request("not a pass")).Should().BeNull();
        fixture.Pass.Read(Request(string.Empty)).Should().BeNull();
        fixture.Pass.Read(new DefaultHttpContext()).Should().BeNull();
    }

    [TestMethod]
    public void APassNamingNobody_OrARoleTheWebUiDoesNotKnow_IsNotRead()
    {
        using var fixture = new Fixture(WebTestSupport.StopKey());
        var protector = fixture.Protection.CreateProtector("HVO.RoofControllerV4.Web.StopPass.v1");
        var notAfter = Start.AddHours(1).ToUnixTimeSeconds();
        string Protect(string json) => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(protector.Protect(System.Text.Encoding.UTF8.GetBytes(json)));

        fixture.Pass.Read(Request(Protect($$"""{"Name":"olga","Role":"roofoperator","SessionId":"session-1","NotAfter":{{notAfter}}}"""))).Should().NotBeNull("the payload as the web UI writes it");
        fixture.Pass.Read(Request(Protect($$"""{"Name":"","Role":"roofoperator","SessionId":"session-1","NotAfter":{{notAfter}}}"""))).Should().BeNull();
        fixture.Pass.Read(Request(Protect($$"""{"Name":"olga","Role":"superuser","SessionId":"session-1","NotAfter":{{notAfter}}}"""))).Should().BeNull();
        fixture.Pass.Read(Request(Protect($$"""{"Name":"olga","Role":"roofoperator","SessionId":"","NotAfter":{{notAfter}}}"""))).Should().BeNull();
        fixture.Pass.Read(Request(Protect("[1,2]"))).Should().BeNull();
        fixture.Pass.Read(Request(Protect("null"))).Should().BeNull();
    }

    [TestMethod]
    public void WithoutAStopKey_NoPassIsIssued_AndAnyOldOneIsRemoved()
    {
        using var fixture = new Fixture(WebStopKey.None);
        var session = fixture.Open("olga", RoofControllerApiContract.OperatorRole);

        var header = fixture.IssueHeader(session);

        fixture.Pass.IsAvailable.Should().BeFalse();
        fixture.Pass.OutlastsSessions.Should().BeFalse();
        header.Name.ToString().Should().Be(WebStopPass.CookieName);
        header.Value.ToString().Should().BeEmpty();
        header.Expires.Should().BeBefore(Start, "the browser drops it");
        header.Path.ToString().Should().Be("/stop");
    }

    [TestMethod]
    public void Remove_TellsTheBrowserToDropThePass()
    {
        using var fixture = new Fixture(WebTestSupport.StopKey());
        var http = new DefaultHttpContext();

        fixture.Pass.Remove(http);

        var header = SetCookieHeaderValue.Parse(http.Response.Headers.SetCookie.ToString());
        header.Name.ToString().Should().Be(WebStopPass.CookieName);
        header.Value.ToString().Should().BeEmpty();
        header.Expires.Should().BeBefore(Start);
        header.Path.ToString().Should().Be("/stop");
    }

    private static DefaultHttpContext Request(string cookie)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = $"{WebStopPass.CookieName}={cookie}";
        return http;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly WebSessionStoreTests.Fixture _sessions;
        private int _opened;

        public Fixture(WebStopKey stopKey, RoofWebOptions? options = null)
        {
            _sessions = new WebSessionStoreTests.Fixture(stopKey);
            Time = _sessions.Time;
            Pass = new WebStopPass(Protection, stopKey, Options.Create(options ?? new RoofWebOptions()), Time);
        }

        public ManualTimeProvider Time { get; }

        public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();

        public WebStopPass Pass { get; }

        public WebSession Open(string name, string role)
            => _sessions.Store.Open(WebSessionStoreTests.Credential($"session-{++_opened}", name, role));

        /// <summary>The pass's cookie value, as the browser will send it back.</summary>
        public string Issue(WebSession session) => IssueHeader(session).Value.ToString();

        public SetCookieHeaderValue IssueHeader(WebSession session, bool https = false, string pathBase = "")
        {
            var http = new DefaultHttpContext();
            http.Request.IsHttps = https;
            http.Request.PathBase = pathBase;
            Pass.Issue(http, session);
            return SetCookieHeaderValue.Parse(http.Response.Headers.SetCookie.ToString());
        }

        public void Dispose() => _sessions.Dispose();
    }
}

/// <summary>Stop from one sender: a burst at once, then four a second; a person pressing Stop never reaches it.</summary>
[TestClass]
public sealed class WebStopLimiterTests
{
    private static readonly string Phone = WebStopLimiter.ForAddress(IPAddress.Parse("192.168.1.20"), null);
    private static readonly string Laptop = WebStopLimiter.ForAddress(IPAddress.Parse("192.168.1.21"), null);

    [TestMethod]
    public void APerson_ASignedOutAddress_AndRefusals_AreCountedApart()
    {
        var address = IPAddress.Parse("192.168.1.20");
        var person = WebStopLimiter.ForSession("session-1");
        var signedOut = WebStopLimiter.ForAddress(address, null);
        string[] senders =
        [
            person, signedOut, WebStopLimiter.ForRefusals(address, null), WebStopLimiter.ForTooMany(person), WebStopLimiter.ForTooMany(signedOut),
        ];

        senders.Should().OnlyHaveUniqueItems();
        foreach (var spent in senders)
        {
            var limiter = new WebStopLimiter(new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
            Enumerable.Range(0, WebStopLimiter.Burst).Should().OnlyContain(_ => limiter.TryAcquire(spent));
            limiter.TryAcquire(spent).Should().BeFalse();
            foreach (var other in senders.Where(sender => sender != spent))
            {
                limiter.TryAcquire(other).Should().BeTrue($"{spent} uses up nothing of {other}");
            }
        }
    }

    [TestMethod]
    public void APublicIPv6Address_IsCountedByItsNetwork()
    {
        WebStopLimiter.ForAddress(IPAddress.Parse("2001:db8:1:2::5"), null)
            .Should().Be(WebStopLimiter.ForAddress(IPAddress.Parse("2001:db8:1:2:ffff::9"), null));
        WebStopLimiter.ForAddress(IPAddress.Parse("fd12:3456:789a:1::5"), null)
            .Should().NotBe(WebStopLimiter.ForAddress(IPAddress.Parse("fd12:3456:789a:1::6"), null), "on the LAN each host is itself");
    }

    [TestMethod]
    public void AnAddress_MaySendABurst_ThenFourASecond()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var limiter = new WebStopLimiter(time);

        Enumerable.Range(0, WebStopLimiter.Burst).Should().OnlyContain(_ => limiter.TryAcquire(Phone));
        limiter.TryAcquire(Phone).Should().BeFalse();
        limiter.TryAcquire(Laptop).Should().BeTrue("each address has its own");

        time.Advance(WebStopLimiter.RefillInterval - TimeSpan.FromMilliseconds(1));
        limiter.TryAcquire(Phone).Should().BeFalse();
        time.Advance(TimeSpan.FromMilliseconds(1));
        limiter.TryAcquire(Phone).Should().BeTrue();
        limiter.TryAcquire(Phone).Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        Enumerable.Range(0, 4).Should().OnlyContain(_ => limiter.TryAcquire(Phone));
        limiter.TryAcquire(Phone).Should().BeFalse();
    }

    [TestMethod]
    public void AQuietAddress_EarnsBackItsWholeBurst_AndNoMore()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var limiter = new WebStopLimiter(time);
        Enumerable.Range(0, WebStopLimiter.Burst).Should().OnlyContain(_ => limiter.TryAcquire(Phone));

        time.Advance(TimeSpan.FromHours(1));

        Enumerable.Range(0, WebStopLimiter.Burst).Should().OnlyContain(_ => limiter.TryAcquire(Phone));
        limiter.TryAcquire(Phone).Should().BeFalse();
    }

    [TestMethod]
    public void APersonPressingStop_OverAndOver_NeverReachesIt()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var limiter = new WebStopLimiter(time);

        // Eight presses a second for five seconds, then three a second for a minute.
        for (var press = 0; press < 40; press++)
        {
            limiter.TryAcquire(Phone).Should().BeTrue($"quick press {press}");
            time.Advance(TimeSpan.FromMilliseconds(125));
        }

        for (var press = 0; press < 180; press++)
        {
            limiter.TryAcquire(Phone).Should().BeTrue($"press {press}");
            time.Advance(TimeSpan.FromMilliseconds(333));
        }
    }

    [TestMethod]
    public void QuietAddresses_AreForgotten()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var limiter = new WebStopLimiter(time);
        for (var host = 1; host <= 50; host++)
        {
            limiter.TryAcquire(WebStopLimiter.ForAddress(IPAddress.Parse($"10.0.0.{host}"), null)).Should().BeTrue();
        }

        limiter.TryAcquire(WebStopLimiter.ForAddress(null, null)).Should().BeTrue("an address the server does not know is counted together");
        limiter.Count.Should().Be(51);

        time.Advance(WebStopLimiter.RefillInterval * WebStopLimiter.Burst);
        limiter.TryAcquire(Phone).Should().BeTrue();

        limiter.Count.Should().Be(1, "only the address just counted is kept");
    }
}
