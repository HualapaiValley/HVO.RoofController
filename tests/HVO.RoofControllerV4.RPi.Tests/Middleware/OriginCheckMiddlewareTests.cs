using HVO.RoofControllerV4.RPi.Middleware;
using Microsoft.AspNetCore.Http;

namespace HVO.RoofControllerV4.RPi.Tests.Middleware;

[TestClass]
public sealed class OriginCheckMiddlewareTests
{
    [TestMethod]
    [DataRow("GET", "/_blazor", true)]
    [DataRow("POST", "/_blazor/negotiate", true)]
    [DataRow("GET", "/_BLAZOR/disconnect", true)]
    [DataRow("POST", "/account/login", true)]
    [DataRow("POST", "/account/logout", true)]
    [DataRow("GET", "/account/login", false)]
    [DataRow("GET", "/_blazorx", false)]
    [DataRow("POST", "/api/v4.0/RoofControl/Stop", false)]
    [DataRow("GET", "/health", false)]
    public void AppliesTo_CoversHubAndAccountPostsOnly(string method, string path, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.AreEqual(expected, OriginCheckMiddleware.AppliesTo(context.Request));
    }

    [TestMethod]
    [DataRow("https://roof.local:5195", true)]
    [DataRow("https://ROOF.local:5195/", true)]
    [DataRow("https://roof.local:5196", false)]
    [DataRow("http://roof.local:5195", false)]
    [DataRow("https://evil.example", false)]
    [DataRow("https://roof.local.evil.example:5195", false)]
    [DataRow("null", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    [DataRow("file://roof.local", false)]
    [DataRow("chrome-extension://abc", false)]
    public void IsAllowedOrigin_SameOriginOnlyByDefault(string? origin, bool expected)
    {
        Assert.AreEqual(expected, OriginCheckMiddleware.IsAllowedOrigin(origin, Request("https", "roof.local:5195"), allowedOrigins: null));
    }

    [TestMethod]
    public void IsAllowedOrigin_ConfiguredOrigin_IsAccepted()
    {
        var allowed = new[] { "not a url", "https://observatory.example/" };
        var request = Request("https", "roof.local:5195");

        Assert.IsTrue(OriginCheckMiddleware.IsAllowedOrigin("https://observatory.example", request, allowed));
        Assert.IsFalse(OriginCheckMiddleware.IsAllowedOrigin("http://observatory.example", request, allowed));
        Assert.IsFalse(OriginCheckMiddleware.IsAllowedOrigin("not a url", request, allowed));
    }

    [TestMethod]
    public void IsAllowedOrigin_DefaultPorts_Match()
    {
        Assert.IsTrue(OriginCheckMiddleware.IsAllowedOrigin("https://roof.local", Request("https", "roof.local:443"), allowedOrigins: null));
        Assert.IsTrue(OriginCheckMiddleware.IsAllowedOrigin("http://roof.local:80", Request("http", "roof.local"), allowedOrigins: null));
    }

    private static HttpRequest Request(string scheme, string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        return context.Request;
    }
}
