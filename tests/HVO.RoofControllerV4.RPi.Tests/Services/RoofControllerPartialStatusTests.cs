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
public class RoofControllerPartialStatusTests
{
    private RoofControllerServiceV4 CreateService(FakeRoofHat hat)
    {
        var options = RoofControllerTestFactory.CreateDefaultOptions(opts =>
        {
            opts.DigitalInputPollInterval = TimeSpan.FromMilliseconds(10);
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(30);
        });
        return new RoofControllerServiceV4(new NullLogger<RoofControllerServiceV4>(), Options.Create(options), hat);
    }

    [TestMethod]
    public async Task Open_ThenManualStop_ShouldTransitionToPartiallyOpen()
    {
        // Arrange
        var hat = new FakeRoofHat();
        var svc = CreateService(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Ensure we start with no limits active (mid-travel scenario, NC => HIGH/HIGH)
        hat.SetInputs(true,true,false,false);
        svc.ForceStatusRefresh();
        svc.Status.Should().NotBe(RoofControllerStatus.Open); // not actually at open

        // Issue Open command -> should enter Opening
        // Act
        var openResult = svc.Open();
        openResult.IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Opening);

        // Immediately stop (simulate manual stop while mid-travel)
        // Assert
        var stopResult = svc.Stop(RoofControllerStopReason.NormalStop);
        stopResult.IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.PartiallyOpen);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
    }

    [TestMethod]
    public async Task Close_ThenManualStop_ShouldTransitionToPartiallyClose()
    {
        // Arrange
        var hat = new FakeRoofHat();
        var svc = CreateService(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Mid-travel scenario (NC => HIGH/HIGH)
        hat.SetInputs(true,true,false,false);
        svc.ForceStatusRefresh();

        // Act
        var closeResult = svc.Close();
        closeResult.IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Closing);

        // Assert
        var stopResult = svc.Stop(RoofControllerStopReason.NormalStop);
        stopResult.IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.PartiallyClose);
        svc.LastStopReason.Should().Be(RoofControllerStopReason.NormalStop);
    }

    // Reflection helper removed; using internal ForceStatusRefresh instead via InternalsVisibleTo
}
