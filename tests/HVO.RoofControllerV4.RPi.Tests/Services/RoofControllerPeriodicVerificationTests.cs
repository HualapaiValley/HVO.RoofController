using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

[TestClass]
public class RoofControllerPeriodicVerificationTests
{
    private RoofControllerServiceV4 CreateService(FakeRoofHat hat, TimeSpan verificationInterval)
    {
        var options = RoofControllerTestFactory.CreateDefaultOptions(opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(5);
            opts.EnablePeriodicVerificationWhileMoving = true;
            opts.PeriodicVerificationInterval = verificationInterval;
        });
        return new RoofControllerServiceV4(new NullLogger<RoofControllerServiceV4>(), Options.Create(options), hat);
    }

    [TestMethod]
    public async Task PeriodicVerification_ShouldDetectLimitWithoutEvent()
    {
        // Arrange
        var hat = new FakeRoofHat();
        hat.SetInputs(true,true,false,false); // Mid travel (NC: both HIGH)
        var svc = CreateService(hat, TimeSpan.FromMilliseconds(120));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var openSignal = new TaskCompletionSource<RoofControllerStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<RoofStatusChangedEventArgs> handler = (_, args) =>
        {
            if (args.Status.Status == RoofControllerStatus.Open)
            {
                openSignal.TrySetResult(args.Status.Status);
            }
        };
        svc.StatusChanged += handler;

        try
        {
            // Act
            var openResult = svc.Open();
            openResult.IsSuccessful.Should().BeTrue();
            svc.Status.Should().Be(RoofControllerStatus.Opening);

            // Now simulate we reached open limit (IN1 LOW) but no event fired (because polling disabled)
            hat.SetInputs(false,true,false,false);

            await openSignal.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // Assert
            svc.Status.Should().Be(RoofControllerStatus.Open, "periodic verification should force hardware refresh and detect open limit");
            svc.LastStopReason.Should().Be(RoofControllerStopReason.LimitSwitchReached);
            svc.IsWatchdogActive.Should().BeFalse();
            hat.RelayMask.Should().Be(0x00, "periodic verification must de-energize all relays after detecting a limit");
        }
        finally
        {
            svc.StatusChanged -= handler;
            await svc.DisposeAsync();
        }
    }
}
