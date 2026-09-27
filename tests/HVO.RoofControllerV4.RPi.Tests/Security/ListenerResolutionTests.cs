using FluentAssertions;
using HVO.RoofControllerV4.RPi.Security;
using Microsoft.Extensions.Configuration;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>
/// <see cref="RoofSecurityStartup.ResolveListeners"/>: the listeners Kestrel binds, by its precedence. The deployment
/// check and the HSTS decision (<see cref="RoofSecurityStartup.IsHttpsConfigured"/>) must not count settings Kestrel ignores.
/// </summary>
[TestClass]
public sealed class ListenerResolutionTests
{
    [TestMethod]
    public void HttpsPortsBesideHttpUrls_AreIgnored()
    {
        // The image's ASPNETCORE_URLS=http://+:8080 plus ASPNETCORE_HTTPS_PORTS=8443, as both environment providers load them.
        var configuration = Build(new()
        {
            ["urls"] = "http://+:8080",
            ["https_ports"] = "8443",
            ["ASPNETCORE_URLS"] = "http://+:8080",
            ["ASPNETCORE_HTTPS_PORTS"] = "8443"
        });

        var listeners = RoofSecurityStartup.ResolveListeners(configuration);

        listeners.Source.Should().Be(ListenerSource.Urls);
        listeners.Addresses.Should().Equal("http://+:8080");
        listeners.Ignored.Should().Equal("ASPNETCORE_HTTPS_PORTS");
        listeners.HasHttps.Should().BeFalse();
        RoofSecurityStartup.IsHttpsConfigured(configuration).Should().BeFalse();
        listeners.ToString().Should().Be("http://+:8080 (from ASPNETCORE_URLS; ignored: ASPNETCORE_HTTPS_PORTS)");
    }

    [TestMethod]
    public void HttpKestrelEndpoint_OverridesHttpsUrls()
    {
        var configuration = Build(new()
        {
            ["urls"] = "https://+:8443",
            ["Kestrel:Endpoints:Http:Url"] = "http://+:8080"
        });

        var listeners = RoofSecurityStartup.ResolveListeners(configuration);

        listeners.Source.Should().Be(ListenerSource.KestrelEndpoints);
        listeners.Addresses.Should().Equal("http://+:8080");
        listeners.Ignored.Should().Equal("ASPNETCORE_URLS");
        RoofSecurityStartup.IsHttpsConfigured(configuration).Should().BeFalse();
    }

    [TestMethod]
    public void UrlsFromALaterSource_OverrideTheRawAspNetCoreUrlsKey()
    {
        // The environment gives both urls and the raw ASPNETCORE_URLS key; a /run/secrets/urls file, added last,
        // overrides urls only. Kestrel never reads the raw key.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["urls"] = "http://localhost:8080;https://+:8443",
                ["ASPNETCORE_URLS"] = "http://localhost:8080;https://+:8443"
            })
            .AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = "http://+:8080" })
            .Build();

        var listeners = RoofSecurityStartup.ResolveListeners(configuration);

        listeners.Addresses.Should().Equal("http://+:8080");
        RoofSecurityStartup.IsHttpsConfigured(configuration).Should().BeFalse();
    }

    [TestMethod]
    public void RawAspNetCoreUrlsKeyAlone_IsNotAListener()
    {
        var configuration = Build(new() { ["ASPNETCORE_URLS"] = "https://+:8443" });

        var listeners = RoofSecurityStartup.ResolveListeners(configuration);

        listeners.Source.Should().Be(ListenerSource.Default);
        listeners.Addresses.Should().Equal("http://localhost:5000");
        listeners.HasHttps.Should().BeFalse();
    }

    [TestMethod]
    public void HttpsUrls_AreHttps()
    {
        var configuration = Build(new() { ["urls"] = "http://localhost:8080; https://+:8443" });

        var listeners = RoofSecurityStartup.ResolveListeners(configuration);

        listeners.Source.Should().Be(ListenerSource.Urls);
        listeners.Addresses.Should().Equal("http://localhost:8080", "https://+:8443");
        listeners.Ignored.Should().BeEmpty();
        RoofSecurityStartup.IsHttpsConfigured(configuration).Should().BeTrue();
        listeners.ToString().Should().Be("http://localhost:8080, https://+:8443 (from ASPNETCORE_URLS)");
    }

    [TestMethod]
    public void PortsWithoutUrls_AreExpandedAsKestrelDoes()
    {
        var configuration = Build(new() { ["http_ports"] = "8080", ["https_ports"] = "8443" });

        var listeners = RoofSecurityStartup.ResolveListeners(configuration);

        listeners.Source.Should().Be(ListenerSource.Ports);
        listeners.Addresses.Should().Equal("http://*:8080", "https://*:8443");
        RoofSecurityStartup.IsHttpsConfigured(configuration).Should().BeTrue();
    }

    [TestMethod]
    public void HttpsKestrelEndpoint_IsHttps_AndOverridesHttpUrls()
    {
        var configuration = Build(new()
        {
            ["urls"] = "http://+:8080",
            ["Kestrel:Endpoints:Http:Url"] = "http://+:8080",
            ["Kestrel:Endpoints:Https:Url"] = "https://+:8443"
        });

        var listeners = RoofSecurityStartup.ResolveListeners(configuration);

        listeners.Source.Should().Be(ListenerSource.KestrelEndpoints);
        listeners.Addresses.Should().Equal("http://+:8080", "https://+:8443");
        listeners.Ignored.Should().Equal("ASPNETCORE_URLS");
        RoofSecurityStartup.IsHttpsConfigured(configuration).Should().BeTrue();
    }

    [TestMethod]
    [DataRow("true")]
    [DataRow("True")]
    [DataRow("1")]
    public void PreferHostingUrls_LetsUrlsOverrideKestrelEndpoints(string preferHostingUrls)
    {
        var configuration = Build(new()
        {
            ["urls"] = "http://+:8080",
            ["preferHostingUrls"] = preferHostingUrls,
            ["Kestrel:Endpoints:Https:Url"] = "https://+:8443"
        });

        var listeners = RoofSecurityStartup.ResolveListeners(configuration);

        listeners.Source.Should().Be(ListenerSource.Urls);
        listeners.Ignored.Should().Equal("Kestrel:Endpoints");
        RoofSecurityStartup.IsHttpsConfigured(configuration).Should().BeFalse();
    }

    [TestMethod]
    public void NothingConfigured_IsKestrelsHttpDefault()
    {
        var listeners = RoofSecurityStartup.ResolveListeners(Build(new()));

        listeners.Source.Should().Be(ListenerSource.Default);
        listeners.Addresses.Should().Equal("http://localhost:5000");
        listeners.HasHttps.Should().BeFalse();
        listeners.ToString().Should().Be("http://localhost:5000 (from Kestrel's default)");
    }

    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
