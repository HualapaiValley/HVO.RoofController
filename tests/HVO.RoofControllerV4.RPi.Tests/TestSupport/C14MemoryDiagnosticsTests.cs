using System.Text.Json;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.Web;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

[TestClass]
public sealed class C14MemoryDiagnosticsTests
{
    private const string Rollup = """
        00400000-7fffffffffff ---p 00000000 00:00 0 [rollup]
        Rss: 12345 kB
        Pss: 12000 kB
        Pss_Anon: 10000 kB
        Pss_File: 1800 kB
        Pss_Shmem: 200 kB
        Anonymous: 11000 kB
        Shared_Clean: 100 kB
        Shared_Dirty: 0 kB
        Private_Clean: 200 kB
        Private_Dirty: 12045 kB
        Swap: 0 kB
        SwapPss: 0 kB
        AnonHugePages: 0 kB
        Future_Field: 999 kB
        """;

    [TestMethod]
    public void KernelKilobytesAreConvertedToBytes_AndFutureFieldsAreIgnored()
    {
        var rollup = LinuxRollup.Parse(new StringReader(Rollup));

        rollup.Status.Should().Be("available");
        rollup.Bytes["Rss"].Should().Be(12_641_280);
        rollup.Bytes["Pss_Anon"].Should().Be(10_240_000);
        rollup.Bytes["Shared_Dirty"].Should().Be(0, "an observed zero differs from a missing value");
        rollup.Bytes.Should().NotContainKey("Future_Field");
    }

    [TestMethod]
    [DataRow("-1 kB")]
    [DataRow("1 MB")]
    [DataRow("n/a kB")]
    [DataRow("9007199254740992 kB")]
    [DataRow("1 kB extra")]
    [DataRow("1 kB\nRss: 2 kB")]
    public void MalformedOrAmbiguousCountersStayNull(string value)
    {
        var rollup = LinuxRollup.Parse(new StringReader("Rss: " + value + "\nPss: 2 kB"));

        rollup.Status.Should().Be("partial");
        rollup.UnavailableReason.Should().Be("MalformedFields");
        rollup.Bytes["Rss"].Should().BeNull();
        rollup.Bytes["Pss"].Should().Be(2048);
        rollup.Bytes["Pss_File"].Should().BeNull();
    }

    [TestMethod]
    public void OlderKernelMissingFieldsAndUnsupportedProcAreExplicit()
    {
        var partial = LinuxRollup.Parse(new StringReader("Rss: 1 kB"));
        partial.Status.Should().Be("partial");
        partial.UnavailableReason.Should().Be("MissingFields");
        partial.Bytes["Pss_File"].Should().BeNull();

        using var directory = new WebTestSupport.TempDirectory();
        File.WriteAllText(directory.File("soak-smaps-final.txt"), "stale snapshot from a previous run");
        File.WriteAllText(directory.File("soak-smaps-settled.txt.partial"), "interrupted previous copy");
        using var diagnostics = new C14MemoryDiagnostics(directory.Path, openProc: _ => throw new PlatformNotSupportedException());
        diagnostics.Append(TimeSpan.Zero, TimeSpan.Zero, new { managedBytes = 1024 }, final: true);

        using var sample = JsonDocument.Parse(File.ReadLines(directory.File(C14MemoryDiagnostics.SamplesFile)).First());
        var linux = sample.RootElement.GetProperty("linuxRollup");
        linux.GetProperty("status").GetString().Should().Be("unavailable");
        linux.GetProperty("unavailableReason").GetString().Should().Be(nameof(PlatformNotSupportedException));
        linux.GetProperty("bytes").EnumerateObject().Should().OnlyContain(p => p.Value.ValueKind == JsonValueKind.Null);
        Directory.GetFiles(directory.Path, "soak-smaps-*").Should().BeEmpty();
    }

    [TestMethod]
    public void SamplesAreReadableBeforeDisposal_AndOnlyThreeMappingPhasesAreKept()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var attachments = new List<string>();
        using var diagnostics = new C14MemoryDiagnostics(directory.Path, attachments.Add,
            name => new StringReader(name == "smaps_rollup" ? Rollup : MappingFixture));

        diagnostics.Append(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), new { managedBytes = 1024, workingSetBytes = 4096 }, final: false);
        ReadRows(directory).Should().HaveCount(2, "a flushed sample and its first mapping are readable while the writer is open");
        diagnostics.Append(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), new { managedBytes = 2048 }, final: false);
        diagnostics.Append(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10), new { managedBytes = 2048 }, final: true);

        var rows = ReadRows(directory);
        rows.Where(row => row.GetProperty("kind").GetString() == "sample").Should().HaveCount(3);
        rows.Where(row => row.GetProperty("kind").GetString() == "smaps").Select(row => row.GetProperty("phase").GetString())
            .Should().Equal("first", "settled", "final");
        var first = rows[0];
        first.GetProperty("originalSample").GetProperty("workingSetBytes").GetInt64().Should().Be(4096);
        first.GetProperty("gc").GetProperty("generations").GetArrayLength().Should().Be(5);
        first.GetProperty("gc").GetProperty("totalCommittedBytes").GetInt64().Should().BeGreaterThan(0);
        first.GetProperty("jit").GetProperty("compiledMethodCount").GetInt64().Should().BeGreaterThan(0);
        attachments.Should().HaveCount(5);
        using var environment = JsonDocument.Parse(File.ReadAllText(directory.File(C14MemoryDiagnostics.EnvironmentFile)));
        environment.RootElement.GetProperty("build").GetProperty("sdkVersion").GetString().Should().NotBeNullOrWhiteSpace();
        environment.RootElement.GetProperty("runtime").GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public void TheEnvironmentRecordsOnlyAllowlistedRuntimeSettings_AndOnlyPlainNumbers()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_TieredCompilation"] = "0",
            ["COMPlus_TieredPGO"] = "0",
            ["DOTNET_GCgen0size"] = "0x4000000",
            ["MALLOC_ARENA_MAX"] = "2",
            ["DOTNET_gcServer"] = "/sensitive-path",
            ["DOTNET_SOME_TOKEN"] = "1",
            ["HOSTNAME"] = "sensitive-host",
            ["GITHUB_ACTIONS"] = "true"
        };
        using (new C14MemoryDiagnostics(directory.Path, openProc: _ => throw new PlatformNotSupportedException(),
                   environmentVariable: name => environment.GetValueOrDefault(name))) { }

        var text = File.ReadAllText(directory.File(C14MemoryDiagnostics.EnvironmentFile));
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("githubActions").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("runtimeSettings").EnumerateObject().Select(p => (p.Name, p.Value.GetString()))
            .Should().Equal(
                ("COMPlus_TieredPGO", "0"), ("DOTNET_GCgen0size", "0x4000000"), ("DOTNET_TieredCompilation", "0"),
                ("DOTNET_gcServer", "[value withheld]"), ("MALLOC_ARENA_MAX", "2"));
        text.Should().NotContain("sensitive").And.NotContain("TOKEN");

        using var unset = new WebTestSupport.TempDirectory();
        using (new C14MemoryDiagnostics(unset.Path, openProc: _ => throw new PlatformNotSupportedException(),
                   environmentVariable: _ => null)) { }
        using var empty = JsonDocument.Parse(File.ReadAllText(unset.File(C14MemoryDiagnostics.EnvironmentFile)));
        empty.RootElement.GetProperty("runtimeSettings").EnumerateObject().Should().BeEmpty("an absent name was not set");
    }

    [TestMethod]
    [DataRow("DOTNET_TieredCompilation", "1\n")]
    [DataRow("COMPlus_gcServer", "")]
    [DataRow("DOTNET_TC_QuickJit", "true")]
    [DataRow("MALLOC_ARENA_MAX", "deadbeef")]
    [DataRow("MALLOC_TRIM_THRESHOLD_", "0x100")]
    [DataRow("MALLOC_TOP_PAD_", "12345678901234567")]
    public void NonNumericRuntimeSettingsAreWithheld(string name, string value)
    {
        using var directory = new WebTestSupport.TempDirectory();
        using (new C14MemoryDiagnostics(directory.Path, openProc: _ => throw new PlatformNotSupportedException(),
                   environmentVariable: key => key == name ? value : null)) { }

        using var document = JsonDocument.Parse(File.ReadAllText(directory.File(C14MemoryDiagnostics.EnvironmentFile)));
        document.RootElement.GetProperty("runtimeSettings").GetProperty(name).GetString().Should().Be("[value withheld]");
    }

    [TestMethod]
    public void AnInterruptedMappingReadKeepsTheSample_ClosesItsReader_AndDeletesItsPartialFile()
    {
        using var directory = new WebTestSupport.TempDirectory();
        var reader = new FailingReader();
        Action run = () =>
        {
            using var diagnostics = new C14MemoryDiagnostics(directory.Path, openProc: name =>
            {
                if (name == "smaps_rollup") return new StringReader(Rollup);
                ReadRows(directory).Should().HaveCount(1, "the observation is flushed before the mapping read starts");
                return reader;
            });
            diagnostics.Append(TimeSpan.Zero, TimeSpan.FromSeconds(10), new { managedBytes = 1024 }, final: false);
            throw new InvalidOperationException("simulated cycle failure");
        };

        run.Should().Throw<InvalidOperationException>();
        reader.Disposed.Should().BeTrue();
        Directory.GetFiles(directory.Path, "*.partial").Should().BeEmpty();
        Directory.GetFiles(directory.Path, "soak-smaps-*").Should().BeEmpty();
        var rows = ReadRows(directory);
        rows.Should().HaveCount(2);
        rows[1].GetProperty("status").GetString().Should().Be("unavailable");
        rows[1].GetProperty("unavailableReason").GetString().Should().Be(nameof(IOException));
        File.ReadAllText(directory.File(C14MemoryDiagnostics.SamplesFile)).Should().NotContain("sensitive-path");
    }

    [TestMethod]
    public void MappingSnapshotsKeepCountersAndCategories_AndNeverCopyPathsOrMappingNames()
    {
        using var output = new StringWriter();
        LinuxSmaps.CopySanitized(new StringReader(MappingFixture), output);
        var text = output.ToString();

        text.Should().Contain("[runtime:libcoreclr.so]").And.Contain("[managed-image]").And.Contain("[named-anonymous]")
            .And.Contain("[file]").And.Contain("Rss: 12 kB").And.Contain("VmFlags: rd wr mr mw me ac sd");
        text.Should().NotContain("sensitive-path").And.NotContain("arbitrary-mapping-name");
    }

    private const string MappingFixture = """
        7f000000-7f001000 r-xp 00000000 08:01 123 /sensitive-path/libcoreclr.so
        Rss: 12 kB
        Private_Dirty: 4 kB
        VmFlags: rd wr mr mw me ac sd
        7f001000-7f002000 r--p 00000000 08:01 124 /sensitive-path/arbitrary.dll
        7f002000-7f003000 rw-p 00000000 00:00 0 [anon:arbitrary-mapping-name]
        7f003000-7f004000 r--p 00000000 08:01 125 /sensitive-path/arbitrary-data
        """;

    private static JsonElement[] ReadRows(WebTestSupport.TempDirectory directory)
        => File.ReadAllLines(directory.File(C14MemoryDiagnostics.SamplesFile)).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    private sealed class FailingReader : StringReader
    {
        internal FailingReader() : base("Rss: 1 kB") { }
        internal bool Disposed { get; private set; }
        private bool first = true;
        public override string? ReadLine()
        {
            if (!first) throw new IOException("sensitive-path");
            first = false;
            return base.ReadLine();
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
