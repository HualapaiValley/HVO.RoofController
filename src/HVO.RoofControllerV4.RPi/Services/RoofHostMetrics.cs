using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HVO.RoofControllerV4.Common;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Security.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Services;

/// <summary>
/// Reads the controller's version and the basic resource use of the machine it runs on, for
/// <c>GET api/v4.0/System/Summary</c> (#209): CPU use and load, the CPU's temperature, memory, the data disk, the
/// controller's addresses and the uptimes. On Linux it reads <c>/proc</c> and <c>/sys</c>, which a container shares with
/// its host. A value it cannot read is null, never an error, so the summary always answers.
/// </summary>
public sealed class RoofHostMetrics
{
    internal const string StatPath = "/proc/stat";
    internal const string LoadAveragePath = "/proc/loadavg";
    internal const string MemoryPath = "/proc/meminfo";
    internal const string UptimePath = "/proc/uptime";

    /// <summary>The Pi's SoC sensor; the controller's container mounts it read-only (docker-compose.yaml, the deploy script).</summary>
    internal const string TemperaturePath = "/sys/class/thermal/thermal_zone0/temp";

    /// <summary>CPU use since the previous summary is reported only over at least this long; a shorter window is noise.</summary>
    internal static readonly TimeSpan MinimumCpuWindow = TimeSpan.FromSeconds(1);

    /// <summary>A previous reading older than this is not used: the summary samples the CPU afresh instead.</summary>
    internal static readonly TimeSpan MaximumCpuWindow = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan DefaultCpuSample = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time;
    private readonly IHostEnvironment _environment;
    private readonly IOptionsMonitor<RoofIdentityOptions> _identity;
    private readonly Func<string, string?> _readFile;
    private readonly Func<string, (long Total, long Free)?> _readDisk;
    private readonly Func<IReadOnlyList<string>> _readAddresses;
    private readonly Func<bool> _inContainer;
    private readonly TimeSpan _cpuSample;
    private readonly object _gate = new();
    private CpuReading? _lastCpu;
    private double? _lastCpuPercent;

    public RoofHostMetrics(
        TimeProvider time,
        IHostEnvironment environment,
        IOptionsMonitor<RoofIdentityOptions> identity)
        : this(time, environment, identity, ReadFile, ReadDisk, ReadAddresses, RunningInContainer, DefaultCpuSample)
    {
    }

    /// <summary>For the tests: the files, disk, addresses and container check, and how long a fresh CPU sample takes.</summary>
    internal RoofHostMetrics(
        TimeProvider time,
        IHostEnvironment environment,
        IOptionsMonitor<RoofIdentityOptions> identity,
        Func<string, string?> readFile,
        Func<string, (long Total, long Free)?> readDisk,
        Func<IReadOnlyList<string>> readAddresses,
        Func<bool> inContainer,
        TimeSpan cpuSample)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _readFile = readFile;
        _readDisk = readDisk;
        _readAddresses = readAddresses;
        _inContainer = inContainer;
        _cpuSample = cpuSample;
    }

    /// <summary>The summary as it is now. A fresh CPU sample (at most once a minute) takes a quarter of a second.</summary>
    public async Task<SystemSummaryResponse> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var cpuPercent = await ReadCpuPercentAsync(cancellationToken).ConfigureAwait(false);
        using var process = Process.GetCurrentProcess();
        var now = _time.GetUtcNow();
        var load = ParseLoadAverage(_readFile(LoadAveragePath));
        var memory = ParseMemory(_readFile(MemoryPath));
        var disk = _readDisk(DataDirectory());
        return new SystemSummaryResponse(
            RoofProductVersion.Of(typeof(Program).Assembly),
            Math.Max((now - process.StartTime.ToUniversalTime()).TotalSeconds, 0d),
            now)
        {
            HostUptimeSeconds = ParseUptime(_readFile(UptimePath)),
            ProcessorCount = Environment.ProcessorCount,
            CpuUsagePercent = cpuPercent,
            LoadAverage1Minute = load?.One,
            LoadAverage5Minutes = load?.Five,
            LoadAverage15Minutes = load?.Fifteen,
            CpuTemperatureCelsius = ParseTemperature(_readFile(TemperaturePath)),
            MemoryTotalBytes = memory?.Total,
            MemoryAvailableBytes = memory?.Available,
            ProcessWorkingSetBytes = process.WorkingSet64,
            DiskTotalBytes = disk?.Total,
            DiskFreeBytes = disk?.Free,
            IpAddresses = [.. _readAddresses()],
            RunningInContainer = _inContainer()
        };
    }

    /// <summary>
    /// The directory the controller keeps its data in: the identity store's, or the application's when the store is in
    /// memory.
    /// </summary>
    internal string DataDirectory()
    {
        var store = _identity.CurrentValue.StorePath;
        if (!string.IsNullOrWhiteSpace(store))
        {
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(store)) is { Length: > 0 } directory && Directory.Exists(directory))
                {
                    return directory;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // The store reports its own path problems; the disk is read where the application is.
            }
        }

        return _environment.ContentRootPath;
    }

    /// <summary>
    /// CPU use since the previous reading when that is between <see cref="MinimumCpuWindow"/> and
    /// <see cref="MaximumCpuWindow"/> old; the last figure when it is more recent; otherwise over a fresh sample.
    /// </summary>
    private async Task<double?> ReadCpuPercentAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        CpuReading? baseline;
        lock (_gate)
        {
            baseline = _lastCpu;
            if (baseline is not null && now - baseline.At < MinimumCpuWindow && _lastCpuPercent is not null)
            {
                return _lastCpuPercent;
            }
        }

        if (baseline is null || now - baseline.At > MaximumCpuWindow || now - baseline.At < MinimumCpuWindow)
        {
            baseline = ParseCpu(_readFile(StatPath), now);
            if (baseline is null)
            {
                return null;
            }

            // Real time, whatever the clock: the kernel counts real clock ticks.
            await Task.Delay(_cpuSample, cancellationToken).ConfigureAwait(false);
        }

        var current = ParseCpu(_readFile(StatPath), _time.GetUtcNow());
        if (current is null)
        {
            return null;
        }

        var percent = CpuPercent(baseline, current);
        lock (_gate)
        {
            _lastCpu = current;
            _lastCpuPercent = percent ?? _lastCpuPercent;
            return _lastCpuPercent;
        }
    }

    /// <summary>Busy time over the time between two readings of the <c>cpu</c> line, in percent; null when no time passed.</summary>
    internal static double? CpuPercent(CpuReading before, CpuReading after)
    {
        var total = after.Total - before.Total;
        var idle = after.Idle - before.Idle;
        if (total <= 0 || idle < 0)
        {
            return null;
        }

        return Math.Round(Math.Clamp((total - idle) * 100d / total, 0d, 100d), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The first line of <c>/proc/stat</c>: <c>cpu user nice system idle iowait irq softirq steal …</c>, in clock ticks.
    /// Idle is idle and iowait; the total leaves out guest time, which user time already counts.
    /// </summary>
    internal static CpuReading? ParseCpu(string? stat, DateTimeOffset at)
    {
        var line = stat?.Split('\n', 2)[0];
        var fields = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields is not { Length: >= 5 } || fields[0] != "cpu")
        {
            return null;
        }

        var values = new long[Math.Min(fields.Length - 1, 8)];
        for (var i = 0; i < values.Length; i++)
        {
            if (!long.TryParse(fields[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return new CpuReading(values.Sum(), idle, at);
    }

    /// <summary><c>/proc/loadavg</c>: <c>0.52 0.58 0.59 1/467 12345</c>.</summary>
    internal static (double One, double Five, double Fifteen)? ParseLoadAverage(string? text)
    {
        var fields = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields is { Length: >= 3 }
            && TryParseDouble(fields[0], out var one)
            && TryParseDouble(fields[1], out var five)
            && TryParseDouble(fields[2], out var fifteen)
            ? (one, five, fifteen)
            : null;
    }

    /// <summary><c>/proc/meminfo</c>'s MemTotal and MemAvailable, in bytes (the file counts in kB, meaning KiB).</summary>
    internal static (long Total, long Available)? ParseMemory(string? text)
    {
        long? total = null;
        long? available = null;
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            var bytes = fields.Length > 2 && fields[2] == "kB" ? value * 1024 : value;
            switch (fields[0])
            {
                case "MemTotal:":
                    total = bytes;
                    break;
                case "MemAvailable:":
                    available = bytes;
                    break;
            }
        }

        return total is { } t && available is { } a ? (t, a) : null;
    }

    /// <summary><c>/proc/uptime</c>: the seconds since boot, then the idle seconds.</summary>
    internal static double? ParseUptime(string? text)
    {
        var first = text?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && TryParseDouble(first, out var seconds) && seconds >= 0 ? seconds : null;
    }

    /// <summary>A thermal zone's <c>temp</c>: millidegrees Celsius, to one decimal place.</summary>
    internal static double? ParseTemperature(string? text)
        => long.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var millidegrees)
            && millidegrees is > -100_000 and < 200_000
            ? Math.Round(millidegrees / 1000d, 1, MidpointRounding.AwayFromZero)
            : null;

    /// <summary>The addresses worth showing: up interfaces, no loopback or link-local, IPv4 first.</summary>
    internal static IReadOnlyList<string> SelectAddresses(IEnumerable<IPAddress> addresses)
        => addresses
            .Where(address => !IPAddress.IsLoopback(address)
                && !address.IsIPv6LinkLocal
                && !address.IsIPv6Multicast
                && !(address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..]))
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Select(address => address.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool TryParseDouble(string text, out double value)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static (long Total, long Free)? ReadDisk(string path)
    {
        try
        {
            var drive = new DriveInfo(path);
            return drive.IsReady ? (drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadAddresses()
    {
        try
        {
            return SelectAddresses(NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up
                    && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(unicast => unicast.Address));
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return [];
        }
    }

    private static bool RunningInContainer()
        => string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>cpu</c> line's total and idle clock ticks, and when they were read.</summary>
    internal sealed record CpuReading(long Total, long Idle, DateTimeOffset At);
}
