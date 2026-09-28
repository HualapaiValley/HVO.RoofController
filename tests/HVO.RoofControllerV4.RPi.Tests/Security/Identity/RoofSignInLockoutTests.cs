using FluentAssertions;
using HVO.RoofControllerV4.RPi.Security.Identity;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>Lockout after repeated failures: the threshold, doubling up to the cap, and what makes it forget.</summary>
[TestClass]
public sealed class RoofSignInLockoutTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private static (RoofSignInLockout Lockout, ManualTimeProvider Time, RoofIdentityOptions Options) Create()
    {
        var options = new RoofIdentityOptions
        {
            LockoutThreshold = 3,
            LockoutDuration = TimeSpan.FromMinutes(5),
            MaximumLockoutDuration = TimeSpan.FromMinutes(18),
            FailureMemory = TimeSpan.FromHours(1)
        };
        var time = new ManualTimeProvider(Start);
        return (new RoofSignInLockout(new TestOptionsMonitor<RoofIdentityOptions>(options), time), time, options);
    }

    [TestMethod]
    public void TheThresholdthFailure_StartsALockout_ThatEndsOnTime()
    {
        var (lockout, time, _) = Create();
        var key = RoofSignInLockout.ForName("Alice");

        lockout.RecordFailure(key).Should().BeNull();
        lockout.RecordFailure(key).Should().BeNull();
        lockout.IsLockedOut(key, out _).Should().BeFalse();
        lockout.RecordFailure(key).Should().Be(TimeSpan.FromMinutes(5));

        lockout.IsLockedOut(key, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromMinutes(5));
        time.Now += TimeSpan.FromMinutes(4);
        lockout.IsLockedOut(key, out retryAfter).Should().BeTrue();
        retryAfter.Should().Be(TimeSpan.FromMinutes(1));
        time.Now += TimeSpan.FromMinutes(1);
        lockout.IsLockedOut(key, out retryAfter).Should().BeFalse();
        retryAfter.Should().Be(TimeSpan.Zero);
    }

    [TestMethod]
    public void EachFurtherLockout_Doubles_UpToTheCap()
    {
        var (lockout, time, _) = Create();
        var key = RoofSignInLockout.ForName("alice");
        var lengths = new List<TimeSpan?>();

        for (var round = 0; round < 4; round++)
        {
            lockout.RecordFailure(key);
            lockout.RecordFailure(key);
            var length = lockout.RecordFailure(key);
            lengths.Add(length);
            time.Now += length!.Value;
        }

        lengths.Should().Equal(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(18));
    }

    [TestMethod]
    public void ASuccess_ForgetsFailuresAndLockouts()
    {
        var (lockout, time, _) = Create();
        var key = RoofSignInLockout.ForName("alice");
        for (var i = 0; i < 3; i++)
        {
            lockout.RecordFailure(key);
        }

        time.Now += TimeSpan.FromMinutes(5);
        lockout.RecordSuccess(key);
        lockout.Count.Should().Be(0);

        lockout.RecordFailure(key).Should().BeNull();
        lockout.RecordFailure(key).Should().BeNull();
        lockout.RecordFailure(key).Should().Be(TimeSpan.FromMinutes(5), "the doubling started again");
    }

    [TestMethod]
    public void FailuresFurtherApartThanTheMemory_NeverLockOut_AndTheDoublingIsForgotten()
    {
        var (lockout, time, options) = Create();
        var key = RoofSignInLockout.ForName("alice");
        for (var i = 0; i < 3; i++)
        {
            lockout.RecordFailure(key);
        }

        time.Now += options.FailureMemory;
        lockout.RecordFailure(key).Should().BeNull();
        lockout.RecordFailure(key).Should().BeNull();
        lockout.RecordFailure(key).Should().Be(TimeSpan.FromMinutes(5));

        for (var i = 0; i < 10; i++)
        {
            time.Now += options.FailureMemory;
            lockout.RecordFailure(RoofSignInLockout.ForName("bob")).Should().BeNull();
        }
    }

    [TestMethod]
    public void NamesAndKiosks_AreCountedSeparately_AndANameIgnoresCase()
    {
        var (lockout, _, _) = Create();
        for (var i = 0; i < 3; i++)
        {
            lockout.RecordFailure(RoofSignInLockout.ForName("Alice"));
        }

        lockout.IsLockedOut(RoofSignInLockout.ForName("ALICE"), out _).Should().BeTrue();
        lockout.IsLockedOut(RoofSignInLockout.ForName("bob"), out _).Should().BeFalse();
        lockout.IsLockedOut(RoofSignInLockout.ForDevice("0123456789abcdef"), out _).Should().BeFalse();
    }

    [TestMethod]
    public void BeyondTheMaximum_EntriesThatAreNotLockedOutAreForgottenFirst()
    {
        var (lockout, time, _) = Create();
        var locked = RoofSignInLockout.ForName("locked");
        for (var i = 0; i < 3; i++)
        {
            lockout.RecordFailure(locked);
        }

        for (var i = 0; i < RoofSignInLockout.MaximumEntries + 10; i++)
        {
            time.Now += TimeSpan.FromMilliseconds(1);
            lockout.RecordFailure(RoofSignInLockout.ForName("guess-" + i));
        }

        lockout.Count.Should().Be(RoofSignInLockout.MaximumEntries);
        lockout.IsLockedOut(locked, out _).Should().BeTrue("a flood of new names does not free a locked-out one");
    }
}
