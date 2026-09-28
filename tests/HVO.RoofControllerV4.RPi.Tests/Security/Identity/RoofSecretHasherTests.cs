using FluentAssertions;
using HVO.RoofControllerV4.RPi.Security.Identity;

namespace HVO.RoofControllerV4.RPi.Tests.Security;

/// <summary>Hashing and checking passwords and PINs, including the limit on how many run at once.</summary>
[TestClass]
public sealed class RoofSecretHasherTests
{
    [TestMethod]
    public async Task AHash_ChecksTheRightSecret_AndRefusesAWrongOne()
    {
        using var hasher = IdentityRig.CreateHasher(1_000);

        var hash = await hasher.HashAsync(TestSecrets.Password, CancellationToken.None);

        hash.Should().NotBeNullOrEmpty().And.NotContain(TestSecrets.Password);
        (await hasher.HashAsync(TestSecrets.Password, CancellationToken.None)).Should().NotBe(hash, "each hash has its own salt");
        (await hasher.VerifyAsync(hash, TestSecrets.Password, CancellationToken.None)).Should().Be(RoofSecretCheck.Succeeded);
        (await hasher.VerifyAsync(hash, TestSecrets.OtherPassword, CancellationToken.None)).Should().Be(RoofSecretCheck.Failed);
    }

    [TestMethod]
    public async Task NoHash_OrAnUnreadableOne_IsAWrongSecret()
    {
        using var hasher = IdentityRig.CreateHasher(1_000);

        (await hasher.VerifyAsync(null, TestSecrets.Password, CancellationToken.None)).Should().Be(RoofSecretCheck.Failed);
        (await hasher.VerifyAsync("not base64 !", TestSecrets.Password, CancellationToken.None)).Should().Be(RoofSecretCheck.Failed);
        (await hasher.VerifyAsync("AAAA", TestSecrets.Password, CancellationToken.None)).Should().Be(RoofSecretCheck.Failed);
        hasher.FreeSlots.Should().Be(2, "every check gives its turn back");
    }

    [TestMethod]
    public async Task AHashMadeWithFewerIterations_AsksToBeReplaced()
    {
        using var weak = IdentityRig.CreateHasher(1_000);
        using var strong = IdentityRig.CreateHasher(2_000);
        var hash = await weak.HashAsync(TestSecrets.Pin, CancellationToken.None);

        (await strong.VerifyAsync(hash, TestSecrets.Pin, CancellationToken.None)).Should().Be(RoofSecretCheck.SucceededRehashNeeded);
        (await strong.VerifyAsync(hash, TestSecrets.OtherPin, CancellationToken.None)).Should().Be(RoofSecretCheck.Failed);
    }

    [TestMethod]
    public async Task WhenEveryTurnIsTaken_ACheckIsBusy_AndAHashIsNull()
    {
        using var hasher = IdentityRig.CreateHasher(3_000_000, slots: 1, slotWait: TimeSpan.FromMilliseconds(50));
        var slow = Task.Run(() => hasher.HashAsync(TestSecrets.Password, CancellationToken.None));
        SpinWait.SpinUntil(() => hasher.FreeSlots == 0, TimeSpan.FromSeconds(10)).Should().BeTrue();

        var check = await hasher.VerifyAsync("AAAA", TestSecrets.Password, CancellationToken.None);
        var hash = await hasher.HashAsync(TestSecrets.Password, CancellationToken.None);

        var slowFinished = slow.IsCompleted;
        (await slow).Should().NotBeNull();
        if (slowFinished)
        {
            Assert.Inconclusive("The slow hash finished before the others were tried; this machine is too fast for the test.");
        }

        check.Should().Be(RoofSecretCheck.Busy);
        hash.Should().BeNull();
        hasher.FreeSlots.Should().Be(1);
    }

    [TestMethod]
    public async Task ACancelledWait_Throws_AndTakesNoTurn()
    {
        using var hasher = IdentityRig.CreateHasher(1_000);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await FluentActions.Awaiting(() => hasher.VerifyAsync(null, TestSecrets.Password, cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        hasher.FreeSlots.Should().Be(2);
    }
}
