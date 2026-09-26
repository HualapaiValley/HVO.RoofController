using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using HVO.Iot.Devices.Iot.Devices.Sequent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

[TestClass]
public class RoofControllerConfigurationTests
{
    private sealed class ConfigurableRoofControllerService : RoofControllerServiceV4
    {
        public ConfigurableRoofControllerService(IOptions<RoofControllerOptionsV4> options, FourRelayFourInputHat hat)
            : base(new NullLogger<RoofControllerServiceV4>(), options, hat)
        {
        }

        public void ForceWatchdogActiveForTest()
        {
            StartSafetyWatchdog();
        }
    }

    private static ConfigurableRoofControllerService CreateService(FakeRoofHat hat, Action<RoofControllerOptionsV4>? configureOptions = null)
    {
        var options = RoofControllerTestFactory.CreateWrappedOptions(configureOptions);
        return new ConfigurableRoofControllerService(options, hat);
    }

    [TestMethod]
    public async Task UpdateConfiguration_ShouldFail_WhenRoofIsMoving()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);

        var service = CreateService(hat);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var originalOptions = service.GetConfigurationSnapshot();
        service.Open().IsSuccessful.Should().BeTrue();
        service.IsMoving.Should().BeTrue();

        var attemptedUpdate = originalOptions with { SafetyWatchdogTimeout = originalOptions.SafetyWatchdogTimeout + TimeSpan.FromSeconds(30) };

        var result = service.UpdateConfiguration(attemptedUpdate);

        result.IsSuccessful.Should().BeFalse();
        result.Error.Should().BeOfType<InvalidOperationException>();

        var snapshotAfterAttempt = service.GetConfigurationSnapshot();
        snapshotAfterAttempt.SafetyWatchdogTimeout.Should().Be(originalOptions.SafetyWatchdogTimeout);

        service.Stop();
        await service.DisposeAsync();
    }

    [TestMethod]
    public async Task UpdateConfiguration_ShouldFail_WhenWatchdogIsActive()
    {
        var hat = new FakeRoofHat();
        hat.SetInputs(true, true, false, false);

        var service = CreateService(hat);
        (await service.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var originalOptions = service.GetConfigurationSnapshot();

        service.ForceWatchdogActiveForTest();
        service.IsWatchdogActive.Should().BeTrue();

        var attemptedUpdate = originalOptions with { LimitSwitchDebounce = originalOptions.LimitSwitchDebounce + TimeSpan.FromMilliseconds(5) };

        var result = service.UpdateConfiguration(attemptedUpdate);

        result.IsSuccessful.Should().BeFalse();
        result.Error.Should().BeOfType<InvalidOperationException>();

        var snapshotAfterAttempt = service.GetConfigurationSnapshot();
        snapshotAfterAttempt.LimitSwitchDebounce.Should().Be(originalOptions.LimitSwitchDebounce);

        service.Stop();
        await service.DisposeAsync();
    }
}
