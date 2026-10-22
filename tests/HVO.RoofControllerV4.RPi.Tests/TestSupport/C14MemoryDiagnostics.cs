using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// Supplementary whole-process diagnostics for C14. Nothing here participates in the commissioning criteria.
/// Rows are flushed before mapping snapshots, so an interrupted run keeps its completed observations.
/// </summary>
internal sealed class C14MemoryDiagnostics : IDisposable
{
    internal const string SamplesFile = "soak-memory.jsonl";
    internal const string EnvironmentFile = "soak-environment.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Runtime and allocator settings that change what C14 measures. The runtime reads each DOTNET_ name also as COMPlus_.
    private static readonly string[] RuntimeSettingNames =
    [
        "TieredCompilation", "TieredPGO", "TC_QuickJitForLoops", "TC_CallCountThreshold", "ReadyToRun",
        "gcServer", "gcConcurrent", "GCHeapCount", "GCHeapHardLimit", "GCgen0size", "GCConserveMemory",
        "GCDynamicAdaptationMode"
    ];

    private static readonly string[] AllocatorSettingNames =
        ["MALLOC_ARENA_MAX", "MALLOC_TRIM_THRESHOLD_", "MALLOC_MMAP_THRESHOLD_", "MALLOC_TOP_PAD_"];

    // Runtime values are hexadecimal by default; allocator values are decimal. Anything else is withheld.
    private static readonly Regex PlainNumber = new("^(?:0[xX])?[0-9A-Fa-f]{1,16}$", RegexOptions.CultureInvariant);
    private readonly string directory;
    private readonly Func<string, TextReader> openProc;
    private readonly Action<string> attach;
    private readonly Func<string, string?> environmentVariable;
    private readonly FileStream stream;
    private readonly StreamWriter writer;
    private bool firstSettled;
    private int sampleIndex;

    internal C14MemoryDiagnostics(string directory, Action<string>? attach = null, Func<string, TextReader>? openProc = null,
        Func<string, string?>? environmentVariable = null)
    {
        this.directory = directory;
        this.attach = attach ?? (_ => { });
        this.environmentVariable = environmentVariable ?? Environment.GetEnvironmentVariable;
        this.openProc = openProc ?? (OperatingSystem.IsLinux()
            ? name => File.OpenText("/proc/self/" + name)
            : _ => throw new PlatformNotSupportedException());
        Directory.CreateDirectory(directory);
        // A reused results directory must not make an interrupted run appear to have a final snapshot.
        foreach (var phase in new[] { "first", "settled", "final" })
        {
            var snapshot = Path.Combine(directory, $"soak-smaps-{phase}.txt");
            File.Delete(snapshot);
            File.Delete(snapshot + ".partial");
        }

        File.WriteAllText(Path.Combine(directory, EnvironmentFile), JsonSerializer.Serialize(EnvironmentInfo(), JsonOptions));
        this.attach(Path.Combine(directory, EnvironmentFile));
        stream = new FileStream(Path.Combine(directory, SamplesFile), FileMode.Create, FileAccess.Write, FileShare.Read);
        writer = new StreamWriter(stream);
        this.attach(Path.Combine(directory, SamplesFile));
    }

    /// <summary>Called only after the original managed, working-set and health sample has been taken.</summary>
    internal void Append(TimeSpan at, TimeSpan warmUp, object originalSample, bool final)
    {
        // GetGCMemoryInfo describes the most recent GC, not a new collection. Do not force another GC here.
        var gc = GC.GetGCMemoryInfo();
        var generations = new List<object>(gc.GenerationInfo.Length);
        for (var i = 0; i < gc.GenerationInfo.Length; i++)
        {
            var generation = gc.GenerationInfo[i];
            generations.Add(new
            {
                generation = i, generation.SizeBeforeBytes, generation.SizeAfterBytes,
                generation.FragmentationBeforeBytes, generation.FragmentationAfterBytes
            });
        }

        var index = sampleIndex++;
        WriteRow(new
        {
            kind = "sample", sampleIndex = index, seconds = at.TotalSeconds, final,
            originalSample,
            gc = new
            {
                gc.TotalCommittedBytes, gc.HeapSizeBytes, gc.FragmentedBytes, gc.Index, gc.Generation,
                gc.Compacted, gc.Concurrent, generations,
                collectionCounts = Enumerable.Range(0, GC.MaxGeneration + 1).Select(GC.CollectionCount).ToArray(),
                totalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false)
            },
            jit = new
            {
                compiledIlBytes = JitInfo.GetCompiledILBytes(), compiledMethodCount = JitInfo.GetCompiledMethodCount(),
                compilationTimeSeconds = JitInfo.GetCompilationTime().TotalSeconds
            },
            linuxRollup = ReadRollup()
        });

        if (index == 0) Snapshot(index, "first");
        if (!firstSettled && at >= warmUp)
        {
            firstSettled = true;
            Snapshot(index, "settled");
        }

        if (final) Snapshot(index, "final");
    }

    private LinuxRollup ReadRollup()
    {
        try
        {
            using var reader = openProc("smaps_rollup");
            return LinuxRollup.Parse(reader);
        }
        catch (Exception exception) when (OptionalProcFailure(exception))
        {
            // Exception messages can contain paths. Only the type is safe to publish.
            return LinuxRollup.Unavailable(exception.GetType().Name);
        }
    }

    private void Snapshot(int index, string phase)
    {
        var file = $"soak-smaps-{phase}.txt";
        var destination = Path.Combine(directory, file);
        var partial = destination + ".partial";
        string status = "available";
        string? unavailableReason = null;
        try
        {
            using (var reader = openProc("smaps"))
            using (var output = new StreamWriter(partial))
            {
                LinuxSmaps.CopySanitized(reader, output);
            }

            File.Move(partial, destination, overwrite: true);
            attach(destination);
        }
        catch (Exception exception) when (OptionalProcFailure(exception))
        {
            status = "unavailable";
            unavailableReason = exception.GetType().Name;
        }
        finally
        {
            File.Delete(partial);
        }

        WriteRow(new { kind = "smaps", sampleIndex = index, phase, status, unavailableReason, file = status == "available" ? file : null });
    }

    private static bool OptionalProcFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or NotSupportedException;

    private void WriteRow(object row)
    {
        writer.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private object EnvironmentInfo()
    {
        var assembly = typeof(C14MemoryDiagnostics).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        return new
        {
            schemaVersion = 2,
            startedUtc = DateTimeOffset.UtcNow,
            runtime = new
            {
                framework = RuntimeInformation.FrameworkDescription, version = Environment.Version.ToString(),
                coreLibVersion = typeof(object).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                os = OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "Other",
                osVersion = Environment.OSVersion.Version.ToString(), Environment.ProcessorCount,
                GCSettings.IsServerGC, latencyMode = GCSettings.LatencyMode.ToString(),
                largeObjectHeapCompactionMode = GCSettings.LargeObjectHeapCompactionMode.ToString()
            },
            build = new
            {
                sdkVersion = metadata.GetValueOrDefault("C14BuildSdkVersion"),
                configuration = metadata.GetValueOrDefault("C14BuildConfiguration"),
                targetFramework = assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName,
                version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            },
            // Explicitly allowlisted names and plain numbers only: no environment, arguments, hostnames or directory dumps.
            runtimeSettings = RuntimeSettings(),
            githubActions = environmentVariable("GITHUB_ACTIONS") == "true"
        };
    }

    /// <summary>The allowlisted settings that are set, each with its value when that is a plain number; an absent name was not set.</summary>
    private SortedDictionary<string, string> RuntimeSettings()
    {
        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var names = RuntimeSettingNames.SelectMany(name => new[] { "DOTNET_" + name, "COMPlus_" + name }).Concat(AllocatorSettingNames);
        foreach (var name in names)
        {
            if (environmentVariable(name) is { } value)
                settings[name] = PlainNumber.IsMatch(value) ? value : "[value withheld]";
        }

        return settings;
    }

    public void Dispose() => writer.Dispose();
}

/// <summary>Selected smaps_rollup counters, converted from kernel kB (1024 bytes); absent values stay null.</summary>
internal sealed record LinuxRollup(string Status, string? UnavailableReason, IReadOnlyDictionary<string, long?> Bytes)
{
    private static readonly string[] Fields =
    [
        "Rss", "Pss", "Pss_Anon", "Pss_File", "Pss_Shmem", "Anonymous", "Shared_Clean", "Shared_Dirty",
        "Private_Clean", "Private_Dirty", "Swap", "SwapPss", "AnonHugePages"
    ];

    internal static LinuxRollup Unavailable(string reason) => new("unavailable", reason, EmptyFields());

    private static Dictionary<string, long?> EmptyFields() => Fields.ToDictionary(key => key, _ => (long?)null);

    internal static LinuxRollup Parse(TextReader reader)
    {
        var values = EmptyFields();
        var seen = new HashSet<string>();
        var malformed = false;
        while (reader.ReadLine() is { } line)
        {
            var colon = line.IndexOf(':');
            if (colon < 0 || !values.ContainsKey(line[..colon])) continue;
            var key = line[..colon];
            var tokens = line[(colon + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!seen.Add(key) || tokens.Length != 2 || tokens[1] != "kB"
                || !long.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kib)
                || kib > long.MaxValue / 1024)
            {
                values[key] = null;
                malformed = true;
                continue;
            }

            values[key] = kib * 1024;
        }

        return new(malformed || values.Values.Any(value => value is null) ? "partial" : "available",
            malformed ? "MalformedFields" : values.Values.Any(value => value is null) ? "MissingFields" : null, values);
    }
}

/// <summary>Streams mapping metadata only, replacing every pathname and arbitrary anonymous mapping name with a category.</summary>
internal static class LinuxSmaps
{
    private static readonly Regex Header = new(@"^([0-9a-f]+-[0-9a-f]+\s+[rwxps-]{4}\s+[0-9a-f]+\s+[0-9a-f]+:[0-9a-f]+\s+[0-9]+)(?:\s+(.*))?$", RegexOptions.CultureInvariant);
    private static readonly Regex Counter = new(@"^[A-Za-z_]+:\s+[0-9]+(?:\s+kB)?\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Flags = new(@"^VmFlags:\s+(?:[a-z]{2}\s*)+$", RegexOptions.CultureInvariant);

    internal static void CopySanitized(TextReader reader, TextWriter writer)
    {
        while (reader.ReadLine() is { } line)
        {
            var header = Header.Match(line);
            writer.WriteLine(header.Success
                ? header.Groups[1].Value + " " + MappingCategory(header.Groups[2].Value)
                : Counter.IsMatch(line) || Flags.IsMatch(line) ? line : "[unrecognized metadata omitted]");
        }
    }

    private static string MappingCategory(string path)
    {
        if (path.Length == 0) return "[anonymous]";
        if (path is "[heap]" or "[stack]" or "[vdso]" or "[vvar]" or "[vvar_vclock]" or "[vsyscall]") return path;
        if (path.StartsWith("[", StringComparison.Ordinal)) return "[named-anonymous]";
        if (path.Contains("memfd:doublemapper", StringComparison.Ordinal)) return "[runtime-doublemapper]";
        var name = Path.GetFileName(path).Replace(" (deleted)", "", StringComparison.Ordinal);
        if (name is "libcoreclr.so" or "libclrjit.so" or "libclrgc.so" or "libhostpolicy.so" or "libhostfxr.so" or "dotnet")
            return "[runtime:" + name + "]";
        if (name.EndsWith(".dll", StringComparison.Ordinal)) return "[managed-image]";
        if (name.Contains(".so", StringComparison.Ordinal)) return "[shared-library]";
        return "[file]";
    }
}
