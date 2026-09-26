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
public class RoofControllerLimitPolarityTests
{
    private static RoofControllerServiceV4 Create(FakeRoofHat hat, bool useNc)
    {
        var options = RoofControllerTestFactory.CreateDefaultOptions(opts =>
        {
            opts.UseNormallyClosedLimitSwitches = useNc;
            opts.SafetyWatchdogTimeout = System.TimeSpan.FromSeconds(30);
        });
        return new RoofControllerServiceV4(new NullLogger<RoofControllerServiceV4>(), Options.Create(options), hat);
    }

    [TestMethod]
    public async Task NcPolarity_LowEqualsLimit()
    {
        // Arrange
        var hat = new FakeRoofHat();
        var svc = Create(hat, useNc: true);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // NC normal (travel) HIGH, limit reached LOW
        hat.SetInputs(true, true, false, false); // both normal, no limits
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Stopped);

        // Act
        hat.SetInputs(false, true, false, false); // forward raw LOW -> Open limit
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Open);

        hat.SetInputs(true, false, false, false); // reverse raw LOW -> Closed limit
        svc.ForceStatusRefresh();
        // Assert
        svc.Status.Should().Be(RoofControllerStatus.Closed);
    }

    [TestMethod]
    public async Task NoPolarity_HighEqualsLimit()
    {
        // Arrange
        var hat = new FakeRoofHat();
        var svc = Create(hat, useNc: false);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // NO normal (travel) LOW, limit reached HIGH
        hat.SetInputs(false, false, false, false); // both normal
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Stopped);

        // Act
        hat.SetInputs(true, false, false, false); // forward HIGH -> Open limit
        svc.ForceStatusRefresh();
        svc.Status.Should().Be(RoofControllerStatus.Open);

        hat.SetInputs(false, true, false, false); // reverse HIGH -> Closed limit
        svc.ForceStatusRefresh();
        // Assert
        svc.Status.Should().Be(RoofControllerStatus.Closed);
    }
}
