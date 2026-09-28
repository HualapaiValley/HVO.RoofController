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
            lockout.TryBeginAttempt(key, known: true, out _).Should().Be(RoofSignInAdmission.Admitted);
        }

        lockout.TryBeginAttempt(key, known: true, out var retryAfter).Should().Be(RoofSignInAdmission.Busy, "three guesses are already being checked");
        retryAfter.Should().Be(RoofSignInLockout.InProgressRetry);

        lockout.EndAttempt(key, RoofSignInOutcome.Failed).Should().BeNull();
        lockout.EndAttempt(key, RoofSignInOutcome.Failed).Should().BeNull();
        lockout.TryBeginAttempt(key, known: true, out _).Should().Be(RoofSignInAdmission.Busy, "two failures and one guess in progress reach three");
        lockout.EndAttempt(key, RoofSignInOutcome.Failed).Should().Be(TimeSpan.FromMinutes(5));

        lockout.TryBeginAttempt(key, known: true, out retryAfter).Should().Be(RoofSignInAdmission.LockedOut);
        retryAfter.Should().Be(TimeSpan.FromMinutes(5));
    }

    [TestMethod]
    public void AnAttemptThatWasNotChecked_GivesItsPlaceBack_WithoutCountingAFailure()
    {
        var (lockout, _, _) = Create();
        var key = RoofSignInLockout.ForName("alice");
        for (var i = 0; i < 3; i++)
        {
            lockout.TryBeginAttempt(key, known: true, out _).Should().Be(RoofSignInAdmission.Admitted);
        }

        lockout.EndAttempt(key, RoofSignInOutcome.NotChecked).Should().BeNull();
        lockout.TryBeginAttempt(key, known: true, out _).Should().Be(RoofSignInAdmission.Admitted);
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
        lockout.TryBeginAttempt(key, known: true, out _).Should().Be(RoofSignInAdmission.Admitted);

        lockout.EndAttempt(key, RoofSignInOutcome.Succeeded).Should().BeNull();

        lockout.Count.Should().Be(0);
        for (var i = 0; i < 3; i++)
        {
            lockout.TryBeginAttempt(key, known: true, out _).Should().Be(RoofSignInAdmission.Admitted, "the failures were forgotten");
        }

        lockout.TryBeginAttempt(key, known: true, out _).Should().Be(RoofSignInAdmission.Busy);
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
            if (lockout.TryBeginAttempt(key, known: true, out _) != RoofSignInAdmission.Admitted)
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
    public void AnUnknownName_LocksOutJustLikeAPerson()
    {
        var (lockout, _, _) = Create();
        var key = RoofSignInLockout.ForName("nobody");

        lockout.RecordFailure(key, known: false).Should().BeNull();
        lockout.RecordFailure(key, known: false).Should().BeNull();
        lockout.RecordFailure(key, known: false).Should().Be(TimeSpan.FromMinutes(5));

        lockout.TryBeginAttempt(key, known: false, out _).Should().Be(RoofSignInAdmission.LockedOut);
    }

    [TestMethod]
    public void AFloodOfMadeUpNames_NeverRefusesOrForgetsAPersonOrAKiosk()
    {
        var (lockout, _, _) = Create();
        var alice = RoofSignInLockout.ForName("alice");
        var kiosk = RoofSignInLockout.ForDevice("0123456789abcdef");
        lockout.RecordFailure(alice);
        lockout.RecordFailure(alice);
        lockout.RecordFailure(kiosk);
        lockout.RecordFailure(kiosk);

        for (var i = 0; i < RoofSignInLockout.MaximumUnknownEntries + 100; i++)
        {
            lockout.RecordFailure(RoofSignInLockout.ForName("guess-" + i), known: false).Should().BeNull();
        }

        lockout.UnknownCount.Should().Be(RoofSignInLockout.MaximumUnknownEntries);
        lockout.RecordFailure(alice).Should().Be(TimeSpan.FromMinutes(5), "alice's two failures were remembered through the flood");
        lockout.RecordFailure(kiosk).Should().Be(TimeSpan.FromMinutes(5), "the kiosk's two failures were remembered through the flood");
        lockout.TryBeginAttempt(RoofSignInLockout.ForName("bob"), known: true, out _).Should().Be(RoofSignInAdmission.Admitted, "a person is never refused for room");
    }

    [TestMethod]
    public void BeyondTheCap_TheUnknownNameTriedLeastRecently_IsForgotten()
    {
        var (lockout, _, _) = Create();
        for (var i = 0; i < RoofSignInLockout.MaximumUnknownEntries; i++)
        {
            lockout.RecordFailure(RoofSignInLockout.ForName("guess-" + i), known: false);
        }

        lockout.RecordFailure(RoofSignInLockout.ForName("guess-0"), known: false).Should().BeNull("guess-0 is now the most recent");
        lockout.RecordFailure(RoofSignInLockout.ForName("one-more"), known: false);

        lockout.UnknownCount.Should().Be(RoofSignInLockout.MaximumUnknownEntries);
        lockout.RecordFailure(RoofSignInLockout.ForName("guess-0"), known: false).Should().Be(TimeSpan.FromMinutes(5), "its failures were kept");
        lockout.RecordFailure(RoofSignInLockout.ForName("guess-1"), known: false).Should().BeNull("it was forgotten to make room, so this is its first failure");
        lockout.RecordFailure(RoofSignInLockout.ForName("guess-1"), known: false).Should().BeNull();
    }

    [TestMethod]
    public void AnUnknownNameWithAnAttemptInProgress_IsNeverForgotten()
    {
        var (lockout, _, _) = Create();
        var pending = RoofSignInLockout.ForName("pending");
        lockout.TryBeginAttempt(pending, known: false, out _).Should().Be(RoofSignInAdmission.Admitted);

        for (var i = 0; i < RoofSignInLockout.MaximumUnknownEntries + 10; i++)
        {
            lockout.RecordFailure(RoofSignInLockout.ForName("guess-" + i), known: false);
        }

        lockout.EndAttempt(pending, RoofSignInOutcome.Failed).Should().BeNull("its attempt was still in progress, so it was kept");
        lockout.UnknownCount.Should().Be(RoofSignInLockout.MaximumUnknownEntries);
    }

    [TestMethod]
    public void ANameThatBecomesAPerson_LeavesTheUnknownNames_AndKeepsItsCount()
    {
        var (lockout, _, _) = Create();
        var bob = RoofSignInLockout.ForName("bob");
        lockout.RecordFailure(bob, known: false);
        lockout.RecordFailure(bob, known: false);
        lockout.UnknownCount.Should().Be(1);

        lockout.RecordFailure(bob, known: true).Should().Be(TimeSpan.FromMinutes(5));

        lockout.UnknownCount.Should().Be(0);
        lockout.Count.Should().Be(1);
    }

    [TestMethod]
    public void AnUnknownNameThatSucceedsOrIsNeverCounted_IsNotKept()
    {
        var (lockout, _, _) = Create();
        var key = RoofSignInLockout.ForName("nobody");

        lockout.TryBeginAttempt(key, known: false, out _).Should().Be(RoofSignInAdmission.Admitted);
        lockout.EndAttempt(key, RoofSignInOutcome.NotChecked);

        lockout.Count.Should().Be(0);
        lockout.UnknownCount.Should().Be(0);
    }
}
