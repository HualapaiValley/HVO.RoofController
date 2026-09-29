using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using HVO.RoofControllerV4.Web;
using HVO.RoofControllerV4.Web.Security;
using HVO.RoofControllerV4.Web.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.Web;

/// <summary>Sign-in attempts are limited per address, in one-minute windows.</summary>
[TestClass]
public sealed class WebSignInLimiterTests
{
    private static readonly IPAddress Alice = IPAddress.Parse("192.0.2.10");
    private static readonly IPAddress Bob = IPAddress.Parse("2001:db8::20");

    [TestMethod]
    public void EachAddress_GetsItsOwnAttempts_UntilTheWindowPasses()
    {
        var time = new ManualTimeProvider();
        var limiter = Limiter(3, time);

        Enumerable.Range(0, 3).Select(_ => limiter.TryAcquire(Alice)).ToList().Should().Equal(true, true, true);
        limiter.TryAcquire(Alice).Should().BeFalse();
        limiter.TryAcquire(Bob).Should().BeTrue("another address has its own attempts");

        time.Advance(WebSignInLimiter.Window - TimeSpan.FromSeconds(1));
        limiter.TryAcquire(Alice).Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(1));
        limiter.TryAcquire(Alice).Should().BeTrue();
    }

    [TestMethod]
    public void RequestsWithoutAnAddress_ShareOneLimit()
    {
        var limiter = Limiter(1, new ManualTimeProvider());

        limiter.TryAcquire(null).Should().BeTrue();
        limiter.TryAcquire(null).Should().BeFalse();
        limiter.TryAcquire(Alice).Should().BeTrue();
    }

    [TestMethod]
    public void AddressesWhoseWindowPassed_AreForgotten()
    {
        var time = new ManualTimeProvider();
        var limiter = Limiter(3, time);
        for (var i = 0; i < 50; i++)
        {
            limiter.TryAcquire(IPAddress.Parse($"192.0.2.{i}"));
        }

        limiter.Count.Should().Be(50);

        time.Advance(WebSignInLimiter.Window);
        limiter.TryAcquire(Bob);

        limiter.Count.Should().Be(1);
    }

    private static WebSignInLimiter Limiter(int perMinute, TimeProvider time)
        => new(Options.Create(new RoofWebOptions { SignInAttemptsPerMinute = perMinute }), time);
}

/// <summary>Only the web UI's own pages (or an allowed origin) may post to it or open its live connection.</summary>
[TestClass]
public sealed class OriginCheckTests
{
    [TestMethod]
    [DataRow("GET", "/", false)]
    [DataRow("HEAD", "/signin", false)]
    [DataRow("OPTIONS", "/stop", false)]
    [DataRow("POST", "/stop", true)]
    [DataRow("POST", "/account/signin", true)]
    [DataRow("DELETE", "/anything", true)]
    [DataRow("GET", "/_blazor", true)]
    [DataRow("GET", "/_BLAZOR/negotiate", true)]
    [DataRow("GET", "/_blazorx", false)]
    public void AppliesTo_TheLiveConnection_AndEverythingThatCanChangeSomething(string method, string path, bool applies)
    {
        OriginCheck.AppliesTo(Request(method, path, "http", "roof:8088")).Should().Be(applies);
    }

    [TestMethod]
    [DataRow("http://roof:8088", "http", "roof:8088", true)]
    [DataRow("http://ROOF:8088/", "http", "roof:8088", true)]
    [DataRow("https://roof", "https", "roof:443", true)]
    [DataRow("https://roof:443", "https", "roof", true)]
    [DataRow("http://roof:8089", "http", "roof:8088", false)]
    [DataRow("https://roof:8088", "http", "roof:8088", false)]
    [DataRow("http://roof.evil:8088", "http", "roof:8088", false)]
    [DataRow("null", "http", "roof:8088", false)]
    [DataRow("", "http", "roof:8088", false)]
    [DataRow("file:///etc/passwd", "http", "roof:8088", false)]
    [DataRow("roof:8088", "http", "roof:8088", false)]
    public void IsAllowedOrigin_TheRequestsOwnOrigin(string origin, string scheme, string host, bool allowed)
    {
        OriginCheck.IsAllowedOrigin(origin, Request("POST", "/stop", scheme, host), allowedOrigins: null).Should().Be(allowed);
    }

    [TestMethod]
    public void IsAllowedOrigin_OneOfTheAllowedOrigins()
    {
        var request = Request("POST", "/stop", "http", "10.0.0.5:8088");
        string[] allowed = ["https://roof.example.org/", "not an origin", "https://other.example.org:8443"];

        OriginCheck.IsAllowedOrigin("https://roof.example.org", request, allowed).Should().BeTrue();
        OriginCheck.IsAllowedOrigin("https://other.example.org:8443", request, allowed).Should().BeTrue();
        OriginCheck.IsAllowedOrigin("https://other.example.org", request, allowed).Should().BeFalse("the port differs");
        OriginCheck.IsAllowedOrigin("http://roof.example.org", request, allowed).Should().BeFalse("the scheme differs");
        OriginCheck.IsAllowedOrigin("https://example.org", request, allowed).Should().BeFalse();
    }

    [TestMethod]
    public void IsAllowedOrigin_WithoutAHost_OnlyTheAllowedOrigins()
    {
        var request = Request("POST", "/stop", "http", host: null);

        OriginCheck.IsAllowedOrigin("http://localhost", request, null).Should().BeFalse();
        OriginCheck.IsAllowedOrigin("http://localhost", request, ["http://localhost"]).Should().BeTrue();
    }

    [TestMethod]
    [DataRow("https://roof.example.org", true)]
    [DataRow("https://roof.example.org/", true)]
    [DataRow("http://10.0.0.5:8088", true)]
    [DataRow("https://roof.example.org/path", false)]
    [DataRow("https://roof.example.org?x=1", false)]
    [DataRow("https://roof.example.org#x", false)]
    [DataRow("https://user@roof.example.org", false)]
    [DataRow("ftp://roof.example.org", false)]
    [DataRow("roof.example.org", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void IsValidAllowedOrigin_SchemeHostAndPortOnly(string? value, bool valid)
    {
        OriginCheck.IsValidAllowedOrigin(value).Should().Be(valid);
    }

    [TestMethod]
    public void Validate_AnAllowedOriginThatIsNotAnOrigin_IsRefused()
    {
        new RoofWebOptions { AllowedOrigins = ["https://roof.example.org", "https://roof.example.org/web"] }.Validate(false)
            .Should().ContainSingle().Which.Should().Contain("'https://roof.example.org/web'");
    }

    private static HttpRequest Request(string method, string path, string scheme, string? host)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = scheme;
        if (host is not null)
        {
            context.Request.Host = new HostString(host);
        }

        return context.Request;
    }
}

/// <summary>The settings added for sign-in: attempts a minute, the Stop key and the data protection keys.</summary>
[TestClass]
public sealed class RoofWebSignInOptionsTests
{
    [TestMethod]
    public void Defaults_AllowTenSignInsAMinute_WithoutAStopKeyOrSharedKeys()
    {
        var options = new RoofWebOptions();

        options.SignInAttemptsPerMinute.Should().Be(10);
        options.StopKeyFile.Should().BeNull();
        options.DataProtectionPath.Should().BeNull();
        options.AllowedOrigins.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1001)]
    public void Validate_SignInAttemptsOutsideOneToAThousand_IsRefused(int attempts)
    {
        new RoofWebOptions { SignInAttemptsPerMinute = attempts }.Validate(false).Should().ContainSingle()
            .Which.Should().Contain("SignInAttemptsPerMinute must be between 1 and 1000");
    }

    [TestMethod]
    public void Validate_AStopKeyFileOrKeyDirectoryThatDoesNotExist_IsNamed()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var options = new RoofWebOptions { StopKeyFile = directory.File("missing-key"), DataProtectionPath = directory.File("missing-keys") };

        options.Validate(false).Should().HaveCount(2)
            .And.Contain(problem => problem.Contains("StopKeyFile names", StringComparison.Ordinal) && problem.Contains("missing-key", StringComparison.Ordinal))
            .And.Contain(problem => problem.Contains("DataProtectionPath names", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Validate_AStopKeyFileAndKeyDirectoryThatExist_AreAccepted()
    {
        using var directory = new WebTestSupport.TempDirectory();
        File.WriteAllText(directory.File("stop-key"), "key\n");

        new RoofWebOptions { StopKeyFile = directory.File("stop-key"), DataProtectionPath = directory.Path }.Validate(false).Should().BeEmpty();
    }
}
