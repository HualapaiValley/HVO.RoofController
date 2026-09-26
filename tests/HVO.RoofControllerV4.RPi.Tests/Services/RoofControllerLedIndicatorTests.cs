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
public class RoofControllerLedIndicatorTests
{
    private RoofControllerServiceV4 CreateService(FakeRoofHat hat)
    {
        var options = RoofControllerTestFactory.CreateDefaultOptions(opts =>
        {
            opts.DigitalInputPollInterval = System.TimeSpan.FromMilliseconds(10);
            opts.SafetyWatchdogTimeout = System.TimeSpan.FromSeconds(10);
        });
        return new RoofControllerServiceV4(new NullLogger<RoofControllerServiceV4>(), Options.Create(options), hat);
    }

    [TestMethod]
    public async Task LedMask_ShouldReflectOpenClosedAndFaultStates()
    {
        // Arrange
        var hat = new FakeRoofHat();
        var svc = CreateService(hat);
        (await svc.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // Initial (NC): mid-travel (no limits) -> inputs HIGH/HIGH, no fault -> all off
        hat.SetInputs(true,true,false,false);
        svc.ForceStatusRefresh();
        hat.LedMask.Should().Be(0x00);

        // Act & Assert
        // Open limit only (NC: open limit actuated => IN1 LOW, IN2 HIGH) -> LED1
        hat.SetInputs(false,true,false,false);
        svc.ForceStatusRefresh();
        hat.LedMask.Should().Be(0x01);

        // Closed limit only (NC: closed limit actuated => IN2 LOW, IN1 HIGH) -> LED2
        hat.SetInputs(true,false,false,false);
        svc.ForceStatusRefresh();
        hat.LedMask.Should().Be(0x02);

        // Fault only (NC mid-travel = IN1 HIGH, IN2 HIGH, fault HIGH) -> LED3
        hat.SetInputs(true,true,true,false);
        svc.ForceStatusRefresh();
        hat.LedMask.Should().Be(0x04);

        // Open + Fault (open limit LOW, closed HIGH, fault HIGH) -> LED1 + LED3
        hat.SetInputs(false,true,true,false);
        svc.ForceStatusRefresh();
        hat.LedMask.Should().Be(0x05);

        // Closed + Fault (closed limit LOW, open HIGH, fault HIGH) -> LED2 + LED3
        hat.SetInputs(true,false,true,false);
        svc.ForceStatusRefresh();
        hat.LedMask.Should().Be(0x06);

        // All three (both limits LOW simultaneously + fault) -> LED1+2+3 (error condition)
        hat.SetInputs(false,false,true,false);
        svc.ForceStatusRefresh();
        hat.LedMask.Should().Be(0x07);
    }

    // Use reflection to invoke protected UpdateRoofStatus to keep test non-invasive
    // Reflection helper removed; using internal ForceStatusRefresh instead via InternalsVisibleTo
}