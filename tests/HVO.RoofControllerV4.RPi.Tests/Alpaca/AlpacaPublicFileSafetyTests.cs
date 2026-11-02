using System.Runtime.InteropServices;
using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.Alpaca;
using HVO.RoofControllerV4.Alpaca.Devices;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services;
using Microsoft.Extensions.Configuration;

namespace HVO.RoofControllerV4.RPi.Tests.Alpaca;

/// <summary>Public Alpaca metadata paths never follow links, read special files, or accept more than 4096 bytes.</summary>
[TestClass]
public sealed class AlpacaPublicFileSafetyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string Ids = "{\"dome/0\":\"11111111-1111-1111-1111-111111111111\",\"safetymonitor/0\":\"22222222-2222-2222-2222-222222222222\"}";

    [TestMethod]
    [DataRow("symlink")]
    [DataRow("fifo")]
    [DataRow("oversized")]
    public async Task Heartbeat_RefusesAnUnsafePublicFile_WithoutBlocking(string kind)
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("The supported platforms are Unix.");
        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-safe-heartbeat-").FullName;
        var path = Path.Combine(directory, "status.json");
        try
        {
            var contents = JsonSerializer.Serialize(new AlpacaServerHeartbeat(Now, 11111,
                "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", 2, 1));
            MakeUnsafe(path, kind, contents);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Alpaca:Enabled"] = "true", ["Alpaca:StatusFilePath"] = path
            }).Build();
            AlpacaServerSummary? summary = null;
            var error = await WithoutBlockingAsync(path, kind == "fifo", () => summary = new RoofAlpacaServerStatus(config, new Clock()).Read());
            error.Should().BeNull("an unreadable public heartbeat is reported as unavailable");
            summary!.Running.Should().BeFalse("only a bounded regular file may report a running Alpaca process");
            summary.DomeConnectedClients.Should().BeNull();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow("symlink", false)]
    [DataRow("symlink", true)]
    [DataRow("dangling-symlink", false)]
    [DataRow("dangling-symlink", true)]
    [DataRow("fifo", false)]
    [DataRow("fifo", true)]
    [DataRow("oversized", false)]
    [DataRow("oversized", true)]
    public async Task IdentityStore_RefusesAnUnsafePublicFile_WithoutBlocking(string kind, bool preflight)
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("The supported platforms are Unix.");
        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-safe-identities-").FullName;
        var path = Path.Combine(directory, AlpacaDeviceIds.FileName);
        try
        {
            MakeUnsafe(path, kind, Ids);
            var error = await WithoutBlockingAsync(path, kind == "fifo", () =>
            {
                if (preflight) AlpacaDeviceIds.ValidateExisting(directory);
                else AlpacaDeviceIds.Load(directory);
            });
            error.Should().BeOfType<AlpacaSettingsException>("unsafe public metadata must be refused in preflight and at server start");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void RegularPublicMetadata_AtThe4096ByteLimit_IsRead_AndPreflightKeepsMissingDataMissing()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-alpaca-safe-valid-").FullName;
        try
        {
            var idsPath = Path.Combine(directory, AlpacaDeviceIds.FileName);
            File.WriteAllText(idsPath, Ids.PadRight(4096));
            AlpacaDeviceIds.ValidateExisting(directory);
            var ids = AlpacaDeviceIds.Load(directory);
            ids.Dome.Should().Be("11111111-1111-1111-1111-111111111111");
            ids.SafetyMonitor.Should().Be("22222222-2222-2222-2222-222222222222");
            var missing = Path.Combine(directory, "missing");
            AlpacaDeviceIds.ValidateExisting(missing);
            Directory.Exists(missing).Should().BeFalse();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void MakeUnsafe(string path, string kind, string contents)
    {
        switch (kind)
        {
            case "symlink":
                File.WriteAllText(path + ".target", contents);
                File.CreateSymbolicLink(path, path + ".target");
                break;
            case "dangling-symlink":
                File.CreateSymbolicLink(path, path + ".missing");
                break;
            case "fifo":
                mkfifo(path, 0x180).Should().Be(0);
                break;
            case "oversized":
                File.WriteAllText(path, contents.PadRight(4097));
                break;
        }
    }

    // Let a regressed FIFO reader finish so red runs fail without leaving a blocked test thread behind.
    private static async Task<Exception?> WithoutBlockingAsync(string path, bool fifo, Action read)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = Task.Run(() =>
        {
            started.SetResult();
            try { read(); return (Exception?)null; }
            catch (Exception ex) { return ex; }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var released = 0;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        if (fifo) await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(1)));
        while (!reading.IsCompleted && DateTime.UtcNow < deadline)
        {
            if (fifo)
            {
                var descriptor = open(path, 1 | (OperatingSystem.IsMacOS() ? 0x4 | 0x1000000 : 0x800 | 0x80000));
                if (descriptor >= 0) { close(descriptor); released++; }
            }
            await Task.Delay(10);
        }
        var result = await reading.WaitAsync(TimeSpan.FromSeconds(5));
        released.Should().Be(0, "a FIFO must be refused without waiting for a writer");
        return result;
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(string path, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}
