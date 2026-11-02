using System.Globalization;
using System.Runtime.CompilerServices;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Tests.Scenarios;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>
/// C14's managed heap reading (#220, PR #232): the live bytes the forced full collection left, as the GC recorded it,
/// not <see cref="GC.GetTotalMemory(bool)"/>'s approximation, which read the same heap megabytes short at random.
/// </summary>
[TestClass]
public sealed class C14ManagedHeapTests
{
    /// <summary>
    /// The settled samples of the C14 soak that failed on PR #232 (run 41349362192, at d1fd160): the seconds, then
    /// <see cref="GC.GetTotalMemory(bool)"/>'s figure, then the heap size and fragmentation of the full blocking
    /// collection it forced, from the run's <c>soak-memory.jsonl</c>.
    /// </summary>
    private const string RecordedSoak = """
        1816.734,15365592,26256712,6079184
        1884.353,15368576,25663168,5465776
        1950.610,18449032,25208768,5007168
        2018.049,17159200,25200840,4981840
        2084.369,16031568,26306928,6093208
        2152.020,17150256,26215728,5997448
        2219.500,16634896,25652680,5472744
        2285.763,16475704,26118848,5829712
        2353.286,17236712,25293016,4988280
        2419.516,17226368,27691328,7396936
        2486.949,15443696,25286368,5013856
        2554.385,15505184,25354848,5020848
        2620.631,17240080,25271184,4963080
        2688.053,20336392,29623936,9287544
        2754.248,20348464,27964944,7616480
        2821.713,20410248,25671520,5277720
        2889.162,20384208,27250648,6866440
        2955.345,20386136,26240584,5854448
        3022.813,20391616,27757152,7365536
        3089.059,20437136,25435480,4998344
        3156.540,20458648,25448208,4989560
        3223.954,20425184,25375624,4958664
        3290.344,20443592,25453872,5010280
        3357.774,17441800,25489016,4979192
        3423.971,17422440,26993520,6503056
        3491.467,17453328,25489248,4976120
        3558.932,20503848,25502584,4998736
        3625.170,13498056,27696512,7178032
        3692.609,18802856,26592056,6028408
        3758.840,20569656,27368384,6798728
        3826.333,20561224,25964576,5419800
        3893.793,20621632,26554568,5932936
        3959.983,20691544,25652176,4960072
        4027.415,20603472,25591392,4987920
        4093.604,20597568,26197520,5608176
        4161.005,20716256,27760936,7044680
        4228.433,20691776,25675640,4992088
        4294.599,20681832,26569984,5888152
        4362.018,20686584,29354752,8676392
        4428.261,20710832,25707296,4975896
        4495.702,20771576,28317632,7554280
        4563.196,20855832,26614552,5775168
        4629.381,19002816,25750144,4986536
        4696.856,17747704,25797272,4981544
        4763.035,19001624,25714296,4951320
        4830.600,19053976,25805328,4998224
        4898.005,19001248,25764752,5010376
        4964.220,19104928,25867816,5017984
        5031.689,15992312,25837056,5016288
        5097.920,16021616,28585272,7734840
        5165.322,17794496,25824224,4969928
        5232.773,16043056,25817184,4962168
        5299.089,16130024,25939672,4989488
        5366.586,16059008,25865432,4978064
        5432.810,16077024,25892208,4986368
        5500.343,16043200,25820736,4957376
        5567.786,16141880,26701288,5747496
        5634.023,16117328,25947432,5001288
        5701.571,20992000,25974976,4998864
        5767.855,17939832,25954000,4962032
        5835.270,17925528,27008520,6014408
        5902.671,17960744,26744736,5716424
        5968.892,17934216,27845536,6860152
        6036.317,17908208,25938664,4970096
        6102.588,21039560,25982216,4962976
        6170.133,21072544,25997360,4933328
        6237.537,21100664,26496824,5395600
        6303.789,21070088,27530056,6468192
        6371.286,21146856,27029808,5882952
        6437.541,21106440,26071896,4965456
        6504.980,21129696,26475176,5345480
        6572.479,21113120,26060320,4947656
        6638.751,21194888,26179208,4984320
        6706.159,21143752,27539048,6403520
        6772.352,21208904,27646960,6438056
        6839.846,21205544,26161968,4956424
        6907.339,21205688,26289912,5084224
        6973.620,21249552,26217776,4968224
        7041.129,21267344,26218504,4968040
        7107.339,21262704,26603584,5340880
        7174.720,21275760,26214696,4946600
        7200.330,21332536,26319728,4986632
        """;

    [TestMethod]
    public void TheReading_IsTheLiveBytesOfTheCollectionItForced_NotGetTotalMemorysApproximation()
    {
        // The run's first settled sample (00:30:17): GetTotalMemory read 4,811,936 bytes under what the collection left live.
        var index = 41L;
        var reading = C14ManagedHeap.Read(
            () => { index++; return 15_365_592; },
            () => new C14ManagedHeap.FullCollection(index, 26_256_712, 6_079_184));

        reading.LiveBytes.Should().Be(26_256_712 - 6_079_184, "C14 judges the bytes the collection left live");
        reading.TotalMemoryBytes.Should().Be(15_365_592, "the approximation is kept for the diagnostics");
        reading.CollectionIndex.Should().Be(42);
    }

    [TestMethod]
    public void AReading_WithNoNewFullBlockingCollection_IsRefused()
    {
        var read = () => C14ManagedHeap.Read(() => 15_365_592, () => new C14ManagedHeap.FullCollection(41, 26_256_712, 6_079_184));

        read.Should().Throw<InvalidOperationException>("an older collection's record is not what the forced one left");
    }

    [TestMethod]
    public void TheSoakThatFailedOnTheApproximation_PassesOnTheLiveBytes()
    {
        var readings = Recorded().Select(sample => (sample.At, Reading: Replay(sample))).ToArray();

        var (_, _, growth, trend) = Judge(readings.Select(r => (r.At, r.Reading.LiveBytes)).ToArray());
        growth.Should().BeApproximately(0.053, 0.001);
        trend.Should().BeApproximately(0.055, 0.001);
        C14SoakScenarios.GrewLessThanTheLimit(growth, trend).Should().BeTrue("the live bytes grew 5.3% after the warm-up");

        // The same samples judged as the soak judged them: its own failure, to the tenth of a percent.
        var (_, _, approximateGrowth, approximateTrend) = Judge(readings.Select(r => (r.At, r.Reading.TotalMemoryBytes)).ToArray());
        approximateGrowth.Should().BeApproximately(0.327, 0.001);
        approximateTrend.Should().BeApproximately(0.179, 0.001);
        C14SoakScenarios.GrewLessThanTheLimit(approximateGrowth, approximateTrend).Should().BeFalse();
    }

    [TestMethod]
    public void ALeakInTheLiveBytes_StillFailsTheCheck()
    {
        var recorded = Recorded();
        var start = recorded[0].At;
        var span = (recorded[^1].At - start).TotalSeconds;
        var startBytes = recorded[0].HeapSizeBytes - recorded[0].FragmentedBytes;

        // The recorded heap, with a leak of 6% of it added evenly over the settled run on top of its own 5.3%.
        var leaking = recorded.Select(sample => (sample.At, Bytes: Replay(sample).LiveBytes
            + (long)(0.06 * startBytes * (sample.At - start).TotalSeconds / span))).ToArray();
        var (_, _, growth, trend) = Judge(leaking);

        C14SoakScenarios.GrewLessThanTheLimit(growth, trend).Should().BeFalse("the live bytes now grow over 10%");
    }

    [TestMethod]
    [DoNotParallelize]
    public void TheLiveBytes_FollowWhatTheProcessKeeps()
    {
        const long Kept = KeptArrays * 1_048L; // 1,024 bytes of data and a 24-byte header each
        var before = C14ManagedHeap.Read();
        var holding = ReadWhileKeeping();
        var released = C14ManagedHeap.Read();

        holding.CollectionIndex.Should().BeGreaterThan(before.CollectionIndex);
        released.CollectionIndex.Should().BeGreaterThan(holding.CollectionIndex);
        (holding.LiveBytes - before.LiveBytes).Should().BeCloseTo(Kept, 4 * 1_048_576, "the reading sees what the process keeps");
        (released.LiveBytes - before.LiveBytes).Should().BeCloseTo(0, 4 * 1_048_576, "and no longer sees it once it is let go");
    }

    private const int KeptArrays = 32_768;

    /// <summary>
    /// Reads the heap while it keeps <see cref="KeptArrays"/> arrays of 1 KB, in a frame of its own: a Debug build keeps
    /// a method's locals alive to its end, so once this returns nothing of them is left to keep the arrays.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ManagedHeapReading ReadWhileKeeping()
    {
        var kept = new List<byte[]>(KeptArrays);
        for (var i = 0; i < KeptArrays; i++)
        {
            kept.Add(new byte[1_024]);
        }

        var reading = C14ManagedHeap.Read();
        GC.KeepAlive(kept);
        return reading;
    }

    private static (double Start, double End, double Growth, double Trend) Judge((TimeSpan At, long Bytes)[] settled)
        => C14SoakScenarios.Growth(
            settled,
            settled.Take(C14SoakScenarios.EndSamples).Select(s => s.Bytes).ToArray(),
            settled.TakeLast(C14SoakScenarios.EndSamples).Select(s => s.Bytes).ToArray());

    /// <summary>A recorded sample read again: its approximation, then its collection's record.</summary>
    private static ManagedHeapReading Replay(RecordedSample sample)
    {
        var index = 0L;
        return C14ManagedHeap.Read(
            () => { index++; return sample.TotalMemoryBytes; },
            () => new C14ManagedHeap.FullCollection(index, sample.HeapSizeBytes, sample.FragmentedBytes));
    }

    private static RecordedSample[] Recorded()
        => RecordedSoak.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(','))
            .Select(f => new RecordedSample(
                TimeSpan.FromSeconds(double.Parse(f[0], CultureInfo.InvariantCulture)),
                long.Parse(f[1], CultureInfo.InvariantCulture),
                long.Parse(f[2], CultureInfo.InvariantCulture),
                long.Parse(f[3], CultureInfo.InvariantCulture)))
            .ToArray();

    private sealed record RecordedSample(TimeSpan At, long TotalMemoryBytes, long HeapSizeBytes, long FragmentedBytes);
}
