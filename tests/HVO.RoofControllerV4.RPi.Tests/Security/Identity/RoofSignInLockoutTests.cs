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
    public void AttemptsInProgress_CountTowardsTheThreshold_SoParallelGuessesNeverExceedIt()
    {
        var (lockout, _, _) = Create();
        var key = RoofSignInLockout.ForDevice("0123456789abcdef");

        for (var i = 0; i < 3; i++)
        {
            lockout.TryBeginAttempt(key, out _).Should().Be(RoofSignInAdmission.Admitted);
        }

        lockout.TryBeginAttempt(key, out var retryAfter).Should().Be(RoofSignInAdmission.Busy, "three guesses are already being checked");
        retryAfter.Should().Be(RoofSignInLockout.InProgressRetry);

        lockout.EndAttempt(key, RoofSignInOutcome.Failed).Should().BeNull();
        lockout.EndAttempt(key, RoofSignInOutcome.Failed).Should().BeNull();
        lockout.TryBeginAttempt(key, out _).Should().Be(RoofSignInAdmission.Busy, "two failures and one guess in progress reach three");
        lockout.EndAttempt(key, RoofSignInOutcome.Failed).Should().Be(TimeSpan.FromMinutes(5));

        lockout.TryBeginAttempt(key, out retryAfter).Should().Be(RoofSignInAdmission.LockedOut);
        retryAfter.Should().Be(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public void AnAttemptThatWasNotChecked_GivesItsPlaceBack_WithoutCountingAFailure()
    {
        var (lockout, _, _) = Create();
        var key = RoofSignInLockout.ForName("alice");
        for (var i = 0; i < 3; i++)
        {
            lockout.TryBeginAttempt(key, out _).Should().Be(RoofSignInAdmission.Admitted);
        }

        lockout.EndAttempt(key, RoofSignInOutcome.NotChecked).Should().BeNull();
        lockout.TryBeginAttempt(key, out _).Should().Be(RoofSignInAdmission.Admitted);
        for (var i = 0; i < 3; i++)
        {
            lockout.EndAttempt(key, RoofSignInOutcome.NotChecked).Should().BeNull();
        }

        lockout.Count.Should().Be(0, "nothing was counted");
        lockout.IsLockedOut(key, out _).Should().BeFalse();
    }

    [TestMethod]
    public void ASucceededAttempt_ForgetsTheFailures_ButOtherAttemptsStillInProgressKeepTheirPlace()
    {
        var (lockout, _, _) = Create();
        var key = RoofSignInLockout.ForName("alice");
        lockout.RecordFailure(key);
        lockout.RecordFailure(key);
        lockout.TryBeginAttempt(key, out _).Should().Be(RoofSignInAdmission.Admitted);

        lockout.EndAttempt(key, RoofSignInOutcome.Succeeded).Should().BeNull();

        lockout.Count.Should().Be(0);
        for (var i = 0; i < 3; i++)
        {
            lockout.TryBeginAttempt(key, out _).Should().Be(RoofSignInAdmission.Admitted, "the failures were forgotten");
        }

        lockout.TryBeginAttempt(key, out _).Should().Be(RoofSignInAdmission.Busy);
    }

    [TestMethod]
    public void EndingAnAttemptThatWasNeverBegun_Throws()
    {
        var (lockout, _, _) = Create();

        var act = () => lockout.EndAttempt(RoofSignInLockout.ForName("alice"), RoofSignInOutcome.Failed);

        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public async Task ManyThreadsGuessingAtOnce_GetNoMoreGuessesThanTheThreshold()
    {
        var (lockout, _, _) = Create();
        var key = RoofSignInLockout.ForDevice("0123456789abcdef");
        var admitted = 0;
        using var gate = new ManualResetEventSlim();
        using var checking = new CountdownEvent(3);

        var guesses = Enumerable.Range(0, 64).Select(guess => Task.Run(() =>
        {
            gate.Wait();
            if (lockout.TryBeginAttempt(key, out _) != RoofSignInAdmission.Admitted)
            {
                return;
            }

            Interlocked.Increment(ref admitted);
            checking.Signal();
            checking.Wait();
            lockout.EndAttempt(key, RoofSignInOutcome.Failed);
        })).ToArray();
        gate.Set();
        await Task.WhenAll(guesses);

        admitted.Should().Be(3);
        lockout.IsLockedOut(key, out _).Should().BeTrue();
    }

    [TestMethod]
    public void AFloodOfNewNames_NeverForgetsANameWithRecentFailures_AndIsRefusedOnceTheTableIsFull()
    {
        var (lockout, time, _) = Create();
        var alice = RoofSignInLockout.ForName("alice");
        lockout.RecordFailure(alice);
        lockout.RecordFailure(alice);

        for (var i = 0; i < RoofSignInLockout.MaximumEntries - 1; i++)
        {
            lockout.RecordFailure(RoofSignInLockout.ForName("guess-" + i));
        }

        lockout.Count.Should().Be(RoofSignInLockout.MaximumEntries);
        lockout.TryBeginAttempt(RoofSignInLockout.ForName("one-more"), out var retryAfter).Should().Be(RoofSignInAdmission.Busy);
        retryAfter.Should().Be(RoofSignInLockout.FullRetry);

        time.Now += TimeSpan.FromMinutes(59);
        lockout.RecordFailure(alice).Should().Be(TimeSpan.FromMinutes(5), "alice's two failures were remembered through the flood");
    }

    [TestMethod]
    public void OnceTheFailuresAreOlderThanTheMemory_AFullTableMakesRoom()
    {
        var (lockout, time, options) = Create();
        for (var i = 0; i < RoofSignInLockout.MaximumEntries; i++)
        {
            lockout.RecordFailure(RoofSignInLockout.ForName("guess-" + i));
        }

        var locked = RoofSignInLockout.ForName("locked");
        time.Now += options.FailureMemory - TimeSpan.FromMinutes(10);
        lockout.TryBeginAttempt(locked, out _).Should().Be(RoofSignInAdmission.Busy);

        time.Now += TimeSpan.FromMinutes(10);
        lockout.TryBeginAttempt(locked, out _).Should().Be(RoofSignInAdmission.Admitted);
        lockout.EndAttempt(locked, RoofSignInOutcome.Failed);
        lockout.Count.Should().Be(1, "every stale entry was forgotten to make room");
    }

    [TestMethod]
    public void ALockedOutName_IsNeverForgotten_ToMakeRoom()
    {
        var (lockout, time, options) = Create();
        options.LockoutDuration = TimeSpan.FromHours(2);
        options.MaximumLockoutDuration = TimeSpan.FromHours(2);
        var locked = RoofSignInLockout.ForName("locked");
        for (var i = 0; i < 3; i++)
        {
            lockout.RecordFailure(locked);
        }

        for (var i = 0; i < RoofSignInLockout.MaximumEntries - 1; i++)
        {
            lockout.RecordFailure(RoofSignInLockout.ForName("guess-" + i));
        }

        time.Now += options.FailureMemory;
        lockout.TryBeginAttempt(RoofSignInLockout.ForName("new"), out _).Should().Be(RoofSignInAdmission.Admitted);

        lockout.IsLockedOut(locked, out var retryAfter).Should().BeTrue("its lockout is still in force");
        retryAfter.Should().Be(TimeSpan.FromHours(1));
    }
}
