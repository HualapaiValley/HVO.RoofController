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
public class RoofControllerLimitEdgeTests
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
    public async Task StopAfterOpenLimitReached_ShouldRemainOpen()
    {
        // Arrange
        var hat = new FakeRoofHat();
        var svc = CreateService(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Mid travel start (NC: both limits not actuated -> HIGH/HIGH)
        hat.SetInputs(true,true,false,false);
        svc.ForceStatusRefresh();

        // Act
        svc.Open().IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Opening);

        // Engage open limit (IN1 LOW, IN2 HIGH)
        hat.SetInputs(false,true,false,false);
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Open);

        // Assert
        var stopResult = svc.Stop(RoofControllerStopReason.NormalStop);
        stopResult.IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Open); // should not become PartiallyOpen
    }

    [TestMethod]
    public async Task StopAfterClosedLimitReached_ShouldRemainClosed()
    {
        // Arrange
        var hat = new FakeRoofHat();
        var svc = CreateService(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Mid travel start (NC)
        hat.SetInputs(true,true,false,false);
        svc.ForceStatusRefresh();

        // Act
        svc.Close().IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Closing);

        // Engage closed limit (IN2 LOW, IN1 HIGH)
        hat.SetInputs(true,false,false,false);
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Closed);

        // Assert
        var stopResult = svc.Stop(RoofControllerStopReason.NormalStop);
        stopResult.IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Closed); // should not become PartiallyClose
    }

    [TestMethod]
    public async Task BothLimitsEngaged_ShouldEnterErrorAndRemainOnStop()
    {
        // Arrange
        var hat = new FakeRoofHat();
        var svc = CreateService(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Ensure starting from mid-travel (no limits active under NC => HIGH/HIGH)
        hat.SetInputs(true,true,false,false);
        svc.ForceStatusRefresh();

        // Act
        svc.Open().IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Opening);

        // Engage both limits (invalid hardware state) (both LOW)
        hat.SetInputs(false,false,false,false);
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Error);

        // Assert
        var stopResult = svc.Stop(RoofControllerStopReason.NormalStop);
        stopResult.IsSuccessful.Should().BeTrue();
        svc.Status.Should().Be(RoofControllerStatus.Error);
    }

    // Reflection helper removed; using internal ForceStatusRefresh instead via InternalsVisibleTo
}
