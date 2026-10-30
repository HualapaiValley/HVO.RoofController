using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.RPi.Security.Identity;
using HVO.RoofControllerV4.RPi.Services;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using ManualClock = HVO.RoofControllerV4.RPi.Tests.Security.ManualTimeProvider;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

/// <summary>
/// The controller's summary for every role (#209), read from Linux's <c>/proc</c> and <c>/sys</c> files as the Pi and its
/// container show them: the files here are fixtures, so the figures are exact, and a file that is missing or garbled
/// leaves its value null instead of failing the summary.
/// </summary>
[TestClass]
public sealed class RoofHostMetricsTests
{
    private const string Stat = "cpu  100 0 100 700 100 0 0 0 0 0\ncpu0 50 0 50 350 50 0 0 0 0 0\nintr 1 2 3\n";
    private const string StatLater = "cpu  250 0 150 800 100 0 0 0 0 0\ncpu0 125 0 75 400 50 0 0 0 0 0\nintr 1 2 3\n";
    private const string LoadAverage = "0.52 0.58 0.61 1/467 12345\n";
    private const string MemoryInformation = "MemTotal:        3884340 kB\nMemFree:          802164 kB\nMemAvailable:    2449716 kB\nBuffers:          101772 kB\n";
    private const string Uptime = "350735.47 234388.90\n";
    private const string Temperature = "48312\n";

    [TestMethod]
    public async Task TheSummary_ReportsTheVersion_AndEveryFigureFromTheFiles()
    {
        var files = Files(Stat, StatLater);
        var metrics = Create(files, out _, addresses: ["192.0.2.10", "2001:db8::10"], inContainer: true);

        var summary = await metrics.GetSummaryAsync(CancellationToken.None);

        summary.ApplicationVersion.Should().Be(RoofProductVersion.Of(typeof(Program).Assembly));
        summary.UptimeSeconds.Should().BeGreaterThan(0);
        summary.HostUptimeSeconds.Should().Be(350735.47);
        summary.ProcessorCount.Should().Be(Environment.ProcessorCount);
        summary.CpuUsagePercent.Should().Be(66.7, "200 of the 300 ticks between the two readings were busy");
        summary.LoadAverage1Minute.Should().Be(0.52);
        summary.LoadAverage5Minutes.Should().Be(0.58);
        summary.LoadAverage15Minutes.Should().Be(0.61);
        summary.CpuTemperatureCelsius.Should().Be(48.3);
        summary.MemoryTotalBytes.Should().Be(3884340L * 1024);
        summary.MemoryAvailableBytes.Should().Be(2449716L * 1024);
        summary.ProcessWorkingSetBytes.Should().BePositive();
        summary.DiskTotalBytes.Should().Be(64L << 30);
        summary.DiskFreeBytes.Should().Be(40L << 30);
        summary.IpAddresses.Should().Equal("192.0.2.10", "2001:db8::10");
        summary.RunningInContainer.Should().BeTrue();
    }

    [TestMethod]
    public async Task UnreadableFiles_LeaveTheirFiguresNull_AndTheSummaryStillAnswers()
    {
        var metrics = Create(_ => null, out _, noDisk: true);

        var summary = await metrics.GetSummaryAsync(CancellationToken.None);

        summary.ApplicationVersion.Should().NotBeNullOrWhiteSpace();
        summary.HostUptimeSeconds.Should().BeNull();
        summary.CpuUsagePercent.Should().BeNull();
        summary.LoadAverage1Minute.Should().BeNull();
        summary.CpuTemperatureCelsius.Should().BeNull();
        summary.MemoryTotalBytes.Should().BeNull();
        summary.MemoryAvailableBytes.Should().BeNull();
        summary.DiskTotalBytes.Should().BeNull();
        summary.DiskFreeBytes.Should().BeNull();
        summary.IpAddresses.Should().BeEmpty();
        summary.RunningInContainer.Should().BeFalse();
    }

    [TestMethod]
    [DataRow("garbage")]
    [DataRow("cpu  a b c d e")]
    [DataRow("intr 1 2 3 4 5")]
    [DataRow("")]
    public void AGarbledStatFile_IsNotRead(string text)
        => RoofHostMetrics.ParseCpu(text, DateTimeOffset.UnixEpoch).Should().BeNull();

    [TestMethod]
    public async Task CpuUse_IsMeasuredSinceThePreviousSummary_WhenThatIsRecent_AndAfreshOtherwise()
    {
        var readings = new Queue<string>([Stat, StatLater, "cpu  250 0 150 1100 100 0 0 0 0 0\n", "cpu  250 0 150 1100 100 0 0 0 0 0\n", "cpu  350 0 150 1200 100 0 0 0 0 0\n"]);
        var metrics = Create(path => path == RoofHostMetrics.StatPath ? readings.Dequeue() : null, out var clock);

        var first = await metrics.GetSummaryAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromMilliseconds(500);
        var tooSoon = await metrics.GetSummaryAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(5);
        var idle = await metrics.GetSummaryAsync(CancellationToken.None);
        clock.Now += RoofHostMetrics.MaximumCpuWindow + TimeSpan.FromSeconds(1);
        var afterAPause = await metrics.GetSummaryAsync(CancellationToken.None);

        first.CpuUsagePercent.Should().Be(66.7);
        tooSoon.CpuUsagePercent.Should().Be(66.7, "under a second is too short a window, so the last figure stands");
        idle.CpuUsagePercent.Should().Be(0, "only idle ticks passed since the previous summary");
        afterAPause.CpuUsagePercent.Should().Be(50, "an old reading is not used: the CPU is sampled afresh");
        readings.Should().BeEmpty();
    }

    [TestMethod]
    public void TheParsers_ReadTheKernelsFormats()
    {
        RoofHostMetrics.ParseLoadAverage(LoadAverage).Should().Be((0.52, 0.58, 0.61));
        RoofHostMetrics.ParseLoadAverage("0.5 x 1").Should().BeNull();
        RoofHostMetrics.ParseMemory(MemoryInformation).Should().Be((3884340L * 1024, 2449716L * 1024));
        RoofHostMetrics.ParseMemory("MemTotal: 1000 kB\n").Should().BeNull("MemAvailable is what tells memory in use");
        RoofHostMetrics.ParseUptime(Uptime).Should().Be(350735.47);
        RoofHostMetrics.ParseUptime("-1 0").Should().BeNull();
        RoofHostMetrics.ParseTemperature(Temperature).Should().Be(48.3);
        RoofHostMetrics.ParseTemperature("-5250").Should().Be(-5.3);
        RoofHostMetrics.ParseTemperature("999999999").Should().BeNull("no CPU runs at a million degrees: the sensor is broken");
        RoofHostMetrics.ParseTemperature("hot").Should().BeNull();
    }

    [TestMethod]
    public void TheAddresses_LeaveOutLoopbackAndLinkLocal_IPv4First()
        => RoofHostMetrics.SelectAddresses(
            [
                IPAddress.Parse("2001:db8::10"),
                IPAddress.Loopback,
                IPAddress.IPv6Loopback,
                IPAddress.Parse("fe80::1"),
                IPAddress.Parse("169.254.10.1"),
                IPAddress.Parse("192.0.2.10"),
                IPAddress.Parse("192.0.2.10")
            ])
            .Should().Equal("192.0.2.10", "2001:db8::10");

    [TestMethod]
    public void TheDisk_IsTheIdentityStores_OrTheApplicationsWithoutOne()
    {
        using var directory = new TemporaryDirectory();
        var environment = new TestHostEnvironment { ContentRootPath = "/app" };
        var options = new RoofIdentityOptions { StorePath = directory.File("identity.json") };
        var metrics = new RoofHostMetrics(new ManualClock(DateTimeOffset.UnixEpoch), environment, new TestOptionsMonitor<RoofIdentityOptions>(options));

        metrics.DataDirectory().Should().Be(directory.Path);
        options.StorePath = null;
        metrics.DataDirectory().Should().Be("/app");
        options.StorePath = Path.Combine(directory.Path, "missing", "identity.json");
        metrics.DataDirectory().Should().Be("/app", "a directory that is not there has no disk to read");
    }

    private static Func<string, string?> Files(params string[] stats)
    {
        var readings = new Queue<string>(stats);
        return path => path switch
        {
            RoofHostMetrics.StatPath => readings.Count > 1 ? readings.Dequeue() : readings.Peek(),
            RoofHostMetrics.LoadAveragePath => LoadAverage,
            RoofHostMetrics.MemoryPath => MemoryInformation,
            RoofHostMetrics.UptimePath => Uptime,
            RoofHostMetrics.TemperaturePath => Temperature,
            _ => null
        };
    }

    private static RoofHostMetrics Create(
        Func<string, string?> files,
        out ManualClock clock,
        bool noDisk = false,
        string[]? addresses = null,
        bool inContainer = false)
    {
        // Now, so the controller process's uptime is not negative.
        var time = clock = new ManualClock(DateTimeOffset.UtcNow);
        (long Total, long Free)? disk = noDisk ? null : (64L << 30, 40L << 30);
        return new RoofHostMetrics(
            time,
            new TestHostEnvironment(),
            new TestOptionsMonitor<RoofIdentityOptions>(new RoofIdentityOptions()),
            files,
            _ => disk,
            () => addresses ?? [],
            () => inContainer,
            TimeSpan.Zero);
    }
}
