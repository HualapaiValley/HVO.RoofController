using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>Who a sign-in attempt is counted against, and how often a refusal is logged.</summary>
[TestClass]
public sealed class RoofSignInRateLimitingTests
{
    [TestMethod]
    [DataRow(null, null, "unknown")]
    [DataRow("192.0.2.10", null, "192.0.2.10")]
    [DataRow("::ffff:192.0.2.10", "192.0.2.1", "192.0.2.10")]
    [DataRow("2001:db8:1:2:3:4:5:6", null, "2001:db8:1:2::/64")]
    [DataRow("2001:db8:1:2:ffff:ffff:ffff:ffff", null, "2001:db8:1:2::/64")]
    [DataRow("2001:db8:1:3::1", "2001:db8:1:2::1", "2001:db8:1:3::/64")]
    public void AnAddress_IsCountedAsItself_OrAPublicIPv6AddressByItsNetwork(string? address, string? local, string expected)
    {
        RoofSignInRateLimiting.AddressOf(Parse(address), Parse(local)).Should().Be(expected);
    }

    [TestMethod]
    [DataRow("::1", null)]
    [DataRow("fe80::1234:5678", null)]
    [DataRow("fd12:3456:789a:1::5", null)]
    [DataRow("2001:db8:1:2::9", "2001:db8:1:2::1")]
    public void AnIPv6AddressOnTheLan_IsCountedAsItself_NotWithTheWholeLan(string address, string? local)
    {
        var first = RoofSignInRateLimiting.AddressOf(IPAddress.Parse(address), Parse(local));
        var neighbour = IPAddress.Parse(address).GetAddressBytes();
        neighbour[15] ^= 0x01;

        first.Should().Be(address);
        RoofSignInRateLimiting.AddressOf(new IPAddress(neighbour), Parse(local)).Should().NotBe(first);
    }

    [TestMethod]
    public void AnAnonymousEndpoint_IsCountedByAddress_EvenWithAnApiKey()
    {
        var context = new DefaultHttpContext
        {
            User = RoofPrincipalFactory.Create(
                new RoofApiKeyIdentity("cfg-viewer", RoofControllerApiContract.ViewerRole, "0123456789abcdef", Kiosk: false),
                RoofControllerSecurityDefaults.ApiKeyScheme)
        };
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new AllowAnonymousAttribute()), "Auth/Session"));
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");

        RoofSignInRateLimiting.PartitionFor(context).Should().Be("address:192.0.2.10");
    }

    [TestMethod]
    public void AnAnonymousCaller_IsCountedByAddress()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("2001:db8::7");

        RoofSignInRateLimiting.PartitionFor(context).Should().Be("address:2001:db8::/64");
    }

    [TestMethod]
    public void AKey_IsCountedByItsKeyId_WhateverTheAddress()
    {
        var context = new DefaultHttpContext
        {
            User = RoofPrincipalFactory.Create(
                new RoofApiKeyIdentity("cfg-kiosk", RoofControllerApiContract.ViewerRole, "0123456789abcdef", Kiosk: true),
                "ApiKey")
        };
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");

        RoofSignInRateLimiting.PartitionFor(context).Should().Be("key:0123456789abcdef");
    }

    [TestMethod]
    public void ASession_IsCountedByItsPerson()
    {
        var session = new StoredSession(
            "0123456789abcdef0123456789abcdef",
            new string('a', 64),
            "Olive",
            RoofControllerApiContract.OperatorRole,
            RoofCredentialKind.Session,
            Device: null,
            DeviceKeyId: null,
            IdentityRig.Start,
            IdentityRig.Start.AddHours(1));
        var context = new DefaultHttpContext { User = RoofPrincipalFactory.CreateForSession(session, "RoofSession") };
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");

        RoofSignInRateLimiting.PartitionFor(context).Should().Be("person:olive");
    }

    [TestMethod]
    public void Refusals_AreLoggedAtMostEveryThirtySeconds_CountingTheRest()
    {
        var time = new ManualTimeProvider(IdentityRig.Start);
        var logger = new CapturingLogger<RoofSignInRefusalLog>();
        var log = new RoofSignInRefusalLog(time, logger);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v4.0/Auth/Session";

        log.Refused(context);
        log.Refused(context);
        time.Now += RoofSignInRefusalLog.LogEvery - TimeSpan.FromSeconds(1);
        log.Refused(context);
        LogAssertions.Count(logger, LogLevel.Warning, "SECURITY sign-in refused").Should().Be(1);

        time.Now += TimeSpan.FromSeconds(1);
        log.Refused(context);

        LogAssertions.Count(logger, LogLevel.Warning, "SECURITY sign-in refused").Should().Be(2);
        LogAssertions.Messages(logger).Last().Should().Contain("address:unknown").And.Contain("(2 more refusals not logged)");
    }

    private static IPAddress? Parse(string? address) => address is null ? null : IPAddress.Parse(address);
}
