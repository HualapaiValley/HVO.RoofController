namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// The managed heap that C14 judges: the bytes still live after a full blocking collection, as the GC recorded that
/// collection (its heap size less its fragmentation).
/// </summary>
/// <remarks>
/// <see cref="GC.GetTotalMemory(bool)"/> forces the collection, as it always has. Its own figure is only the runtime's
/// approximation of the bytes in use: in the soaks of October and November 2026 it read more than 0.1 MB under the
/// live bytes in 43% of the samples, at random and by up to 8.2 MB, and never more than 0.1 MB over. That alone took
/// the check to -21% and to +32% in runs whose live bytes moved under 1% (#220, PR #232; <c>docs/c14-memory-investigation.md</c>). The approximation is
/// kept as <see cref="ManagedHeapReading.TotalMemoryBytes"/>, for the diagnostics only.
/// </remarks>
internal static class C14ManagedHeap
{
    /// <summary>Forces a full blocking collection and reads what it left.</summary>
    internal static ManagedHeapReading Read() => Read(() => GC.GetTotalMemory(forceFullCollection: true), LastFullCollection);

    /// <summary>
    /// Runs <paramref name="forceFullCollection"/>, which returns its own approximation, and then reads the full
    /// blocking collection it made from <paramref name="lastFullCollection"/>. A record no newer than the one before
    /// is not that collection, so it is refused rather than reported.
    /// </summary>
    internal static ManagedHeapReading Read(Func<long> forceFullCollection, Func<FullCollection> lastFullCollection)
    {
        var before = lastFullCollection().Index;
        var totalMemory = forceFullCollection();
        var collection = lastFullCollection();
        if (collection.Index <= before)
        {
            throw new InvalidOperationException(
                $"The forced collection was not recorded as a full blocking one (the last is still #{collection.Index}).");
        }

        return new ManagedHeapReading(collection.HeapSizeBytes - collection.FragmentedBytes, totalMemory, collection.Index);
    }

    private static FullCollection LastFullCollection()
    {
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return new FullCollection(info.Index, info.HeapSizeBytes, info.FragmentedBytes);
    }

    /// <summary>The GC's record of a full blocking collection.</summary>
    internal readonly record struct FullCollection(long Index, long HeapSizeBytes, long FragmentedBytes);
}

/// <summary>
/// A reading of the managed heap: <paramref name="LiveBytes"/> after the forced collection, which C14 judges, and
/// <paramref name="TotalMemoryBytes"/>, <see cref="GC.GetTotalMemory(bool)"/>'s approximation, kept for the diagnostics.
/// </summary>
internal readonly record struct ManagedHeapReading(long LiveBytes, long TotalMemoryBytes, long CollectionIndex);
