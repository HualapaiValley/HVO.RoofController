using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Client;

/// <summary>
/// The controller's summary and a person's names through the client library (#209): the call every UI makes, and the
/// words they all show for it.
/// </summary>
[TestClass]
public sealed class RoofClientSummaryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task AViewer_ReadsTheSummary_WhereTheDetailedInformationIsRefused()
    {
        using var host = new RoofApiTestHost();
        using var viewer = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Viewer));

        var summary = await viewer.System.GetSummaryAsync();

        summary.ApplicationVersion.Should().Be(RoofProductVersion.Of(typeof(Program).Assembly));
        (await RoofClientApiTests.RefusedAsync(() => viewer.System.GetInformationAsync())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public void TheSummary_IsDescribedTheSameEverywhere()
    {
        var summary = new SystemSummaryResponse("4.3.0-ci.7+0123456789abcdef", 3_700, At)
        {
            HostUptimeSeconds = 90_061,
            ProcessorCount = 4,
            CpuUsagePercent = 12.34,
            LoadAverage1Minute = 0.5,
            LoadAverage5Minutes = 0.25,
            LoadAverage15Minutes = 0.125,
            CpuTemperatureCelsius = 48.31,
            MemoryTotalBytes = 4L << 30,
            MemoryAvailableBytes = 3L << 30,
            DiskTotalBytes = 32L << 30,
            DiskFreeBytes = 20L << 30,
            IpAddresses = ["192.0.2.10", "2001:db8::10"]
        };

        RoofSystemText.DescribeSummary(summary).Should().Equal(
            ("Controller", "4.3.0-ci.7 (commit 0123456789ab)"),
            ("Uptime", "controller 1h 1m, host 1d 1h 1m"),
            ("CPU", "12.3 %, load 0.50 0.25 0.13, 4 processors"),
            ("Temperature", "48.3 °C"),
            ("Memory", "1.0 GiB used of 4.0 GiB (25.0 %)"),
            ("Disk", "20.0 GiB free of 32.0 GiB"),
            ("Addresses", "192.0.2.10, 2001:db8::10"));
    }

    [TestMethod]
    public void WhatTheControllerCouldNotRead_IsUnavailable_AndAContainersAddressesSaySo()
    {
        var summary = new SystemSummaryResponse("4.3.0", 59, At) { ProcessorCount = 1, IpAddresses = ["172.17.0.2"], RunningInContainer = true };

        RoofSystemText.DescribeSummary(summary).Should().Equal(
            ("Controller", "4.3.0"),
            ("Uptime", "controller 0m 59s"),
            ("CPU", "unavailable, 1 processor"),
            ("Temperature", "unavailable"),
            ("Memory", "unavailable"),
            ("Disk", "unavailable"),
            ("Addresses", "172.17.0.2 (the controller's container)"));
        RoofSystemText.DescribeSummary(summary with { IpAddresses = [], RunningInContainer = false })
            .Should().Contain(("Addresses", "none found"));
    }

    [TestMethod]
    public void AnUnreachableController_IsSaidSo_AndARefusalSaysWhy()
    {
        RoofSystemText.ControllerUnreachable.Should().Be("Controller unreachable.");
        RoofSystemText.ControllerNotConfigured.Should().Be("Controller not configured yet.");
        RoofSystemText.DescribeUnavailable(new HttpRequestException("refused")).Should().Be(RoofSystemText.ControllerUnreachable);
        RoofSystemText.DescribeUnavailable(new TimeoutException()).Should().Be(RoofSystemText.ControllerUnreachable);
        RoofSystemText.DescribeUnavailable(new InvalidOperationException("internal detail")).Should().NotContain("internal detail");
    }

    [TestMethod]
    [DataRow(1L << 30, "1.0 GiB")]
    [DataRow((1L << 30) - 1, "1024.0 MiB")]
    [DataRow(5L * (1L << 30) / 2, "2.5 GiB")]
    public void Bytes_AreGiBFromAGiB(long bytes, string expected)
        => RoofSystemText.Bytes(bytes).Should().Be(expected);

    [TestMethod]
    public void APerson_IsDescribedByTheirNames_LoginTypeAndLastSignIn()
    {
        RoofIdentityText.DescribePerson("alice", "Alice Example").Should().Be("Alice Example (alice)");
        RoofIdentityText.DescribePerson("alice", null).Should().Be("alice");
        RoofIdentityText.DescribePerson("alice", "alice").Should().Be("alice");
        RoofIdentityText.DescribeLoginType(RoofLoginTypes.Local).Should().Be("local");
        RoofIdentityText.DescribeLoginType(RoofLoginTypes.Linux).Should().Be("Linux");
        RoofIdentityText.DescribeLoginType(null).Should().Be("local");
        RoofIdentityText.DescribeLoginType("Directory").Should().Be("Directory", "a later login type shows as the controller names it");
        RoofIdentityText.DescribeLastLogin(At).Should().Be("2026-10-30 12:00:00Z");
        RoofIdentityText.DescribeLastLogin(null).Should().Be("none recorded");
    }

    [TestMethod]
    [DataRow(null, null, null)]
    [DataRow("  ", "", null)]
    [DataRow("Alice Example", "Ali", null)]
    [DataRow("Alice\u0007", null, "A display name is optional: up to 64 characters, without control characters.")]
    [DataRow(null, "A short name too long", "A short name is optional: up to 16 characters, for narrow places such as the kiosk.")]
    public void TypedNames_AreChecked(string? displayName, string? shortName, string? expected)
        => RoofIdentityText.CheckNames(displayName, shortName).Should().Be(expected);
}
