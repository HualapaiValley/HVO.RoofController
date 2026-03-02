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
public class RoofControllerWatchdogTests
{
    private RoofControllerServiceV4 CreateService(FakeRoofHat hat, TimeSpan watchdog)
    {
        var options = RoofControllerTestFactory.CreateDefaultOptions(opts => opts.SafetyWatchdogTimeout = watchdog);
        return new RoofControllerServiceV4(new NullLogger<RoofControllerServiceV4>(), Options.Create(options), hat);
    }

    [TestMethod]
    public async Task WatchdogTimeout_ShouldTransitionToErrorAndSetTimestamp()
    {
        // Arrange
        var hat = new FakeRoofHat();
        // Mid-travel (no limits) NC => HIGH/HIGH
        hat.SetInputs(true,true,false,false);
        var svc = CreateService(hat, TimeSpan.FromMilliseconds(120));
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Act
        var result = svc.Open();
        result.IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Opening);
        var startTransition = svc.LastTransitionUtc;

        // Wait beyond watchdog
        await Task.Delay(350);

        // Assert
        svc.Status.Should().Be(RoofControllerStatus.Error, "watchdog should force error state");
        svc.LastStopReason.Should().Be(RoofControllerStopReason.SafetyWatchdogTimeout);
        svc.LastTransitionUtc.Should().NotBeNull();
        svc.LastTransitionUtc.Should().NotBe(startTransition);
    }
}