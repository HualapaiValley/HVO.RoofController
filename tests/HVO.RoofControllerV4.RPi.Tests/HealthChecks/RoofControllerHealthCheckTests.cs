using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.HealthChecks;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Services.HatEmulation;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.HealthChecks;

[TestClass]
public sealed class RoofControllerHealthCheckTests
{
    private static RoofControllerHealthCheck CreateHealthCheck(
        IRoofControllerServiceV4 service,
        RoofControllerOptionsV4? options = null,
        RoofHatConnection? hatConnection = null)
        => new(service, NullLogger<RoofControllerHealthCheck>.Instance, Options.Create(options ?? new RoofControllerOptionsV4()), hatConnection);

    private static Task<HealthCheckResult> CheckAsync(FakeRoofControllerService service)
        => CreateHealthCheck(service).CheckHealthAsync(new HealthCheckContext());

    [TestMethod]
    public async Task Healthy_WhenInitializedOnHardware_WithFreshInputsAndVerifiedRegister()
    {
        var service = new FakeRoofControllerService();

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("Ready").WhoseValue.Should().Be(true);
        result.Data.Should().ContainKey("RelayRegisterState").WhoseValue.Should().Be(nameof(RoofRelayRegisterState.Verified));
        result.Data.Should().ContainKey("InputsHealthy").WhoseValue.Should().Be(true);
        result.Data.Should().ContainKey("RelayRegisterReadsHealthy").WhoseValue.Should().Be(true);
    }

    [TestMethod]
    public async Task Unhealthy_WhenServiceDisposed()
    {
        var service = new FakeRoofControllerService { IsServiceDisposed = true };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller service is disposed");
    }

    [TestMethod]
    public async Task Unhealthy_WhenShuttingDown()
    {
        var service = new FakeRoofControllerService { Snapshot = FakeRoofControllerService.HealthySnapshot() with { IsShuttingDown = true } };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller is shutting down");
    }

    [TestMethod]
    public async Task Unhealthy_WhenNotInitialized()
    {
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with { IsInitialized = false, Status = RoofControllerStatus.NotInitialized }
        };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller is not initialized");
    }

    [TestMethod]
    public async Task Unhealthy_WhenRelayRegisterUnverified()
    {
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with
            {
                Status = RoofControllerStatus.Error,
                RelayRegisterState = RoofRelayRegisterState.Unverified,
                RelayRegisterMask = null
            }
        };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller relay register state is unverified");
    }

    [TestMethod]
    public async Task Unhealthy_WhenSafetyFaultLatched_IncludingAStartupFault()
    {
        // QA-09: a drive fault present at startup latches; the controller initializes but is not healthy.
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with
            {
                Status = RoofControllerStatus.Error,
                IsFaultLatched = true,
                LatchedFaultReason = RoofControllerStopReason.DriveFault,
                IsDriveFaultActive = true
            }
        };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("latched").And.Contain(nameof(RoofControllerStopReason.DriveFault));
        result.Data.Should().ContainKey("LatchedFaultReason").WhoseValue.Should().Be(nameof(RoofControllerStopReason.DriveFault));
    }

    [TestMethod]
    public async Task Unhealthy_WhenSafetyInputsNotHealthy()
    {
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with { InputsHealthy = false, ConsecutiveInputReadFailures = 2 }
        };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller safety inputs are not healthy");
        result.Data.Should().ContainKey("ConsecutiveInputReadFailures").WhoseValue.Should().Be(2);
    }

    [TestMethod]
    public async Task Unhealthy_WhenRelayRegisterReadsNotHealthy()
    {
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with { RelayRegisterReadsHealthy = false, ConsecutiveRelayReadFailures = 1 }
        };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller relay register reads are failing or stale");
        result.Data.Should().ContainKey("ConsecutiveRelayReadFailures").WhoseValue.Should().Be(1);
    }

    [TestMethod]
    public async Task Unhealthy_WhenStatusIsError()
    {
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with { Status = RoofControllerStatus.Error }
        };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller is in error state");
    }

    [TestMethod]
    public async Task Degraded_WhenStatusUnknown()
    {
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with { Status = RoofControllerStatus.Unknown }
        };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Roof controller status is unknown");
    }

    [TestMethod]
    public async Task Degraded_WhenIgnoringPhysicalLimitSwitches()
    {
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with { IsIgnoringPhysicalLimitSwitches = true }
        };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Roof controller is ignoring physical limit switches");
    }

    [TestMethod]
    public async Task Degraded_InSimulationMode()
    {
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with { IsUsingPhysicalHardware = false }
        };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Roof controller is running in simulation mode");
        result.Data.Should().ContainKey("HardwareMode").WhoseValue.Should().Be("Simulation");
    }

    [TestMethod]
    public async Task Degraded_WhenLiveConfigurationDisablesInputPolling()
    {
        var service = new FakeRoofControllerService();
        service.UpdateConfiguration(new RoofControllerOptionsV4 { EnableDigitalInputPolling = false });

        // Startup options still say polling is on; the live configuration wins.
        var result = await CreateHealthCheck(service, new RoofControllerOptionsV4 { EnableDigitalInputPolling = true })
            .CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Roof controller digital input polling is disabled");
    }

    [TestMethod]
    public async Task Unhealthy_WhenSnapshotThrows()
    {
        var service = new FakeRoofControllerService { SnapshotException = new InvalidOperationException("status failure") };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [TestMethod]
    public async Task RealController_WithStartupDriveFault_IsUnhealthy()
    {
        // QA-09 end to end: IN3 active at startup latches DriveFault; Initialize still succeeds.
        var hat = new FakeRoofHat(hardwareBacked: true);
        hat.SetInputs(true, true, true, false);
        using var controller = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider());
        (await controller.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var result = await CreateHealthCheck(controller).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain(nameof(RoofControllerStopReason.DriveFault));
    }

    [TestMethod]
    public async Task RealController_OnHardwareWithPolling_IsHealthy()
    {
        var hat = new FakeRoofHat(hardwareBacked: true);
        hat.SetInputs(true, true, false, false);
        using var controller = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), opts => opts.EnableDigitalInputPolling = true);
        (await controller.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var result = await CreateHealthCheck(controller).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy, result.Description);
    }

    [TestMethod]
    public async Task RealController_WithStaleInputs_IsUnhealthy()
    {
        var hat = new FakeRoofHat(hardwareBacked: true);
        hat.SetInputs(true, true, false, false);
        var time = new ManualTimeProvider();
        using var controller = SimulatedRoofControllerService.Create(hat, time, opts => opts.EnableDigitalInputPolling = true);
        (await controller.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        // No supervision cycle runs, so the last successful read ages past the staleness limit.
        time.Advance(TimeSpan.FromSeconds(30));

        var result = await CreateHealthCheck(controller).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller safety inputs are not healthy");
    }

    [TestMethod]
    public async Task Degraded_AgainstTheHatEmulator_NamingTheEndpoint()
    {
        var service = new FakeRoofControllerService { Snapshot = FakeRoofControllerService.HealthySnapshot() with { HatMode = RoofHatMode.Emulated } };

        var result = await CreateHealthCheck(service, hatConnection: HatConnections.Emulated("hat-emulator", 5391)).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Roof controller is running against the HAT emulator (hat-emulator:5391), not the physical HAT");
        result.Data.Should().ContainKey("HardwareMode").WhoseValue.Should().Be("Emulated");
        result.Data.Should().ContainKey("HatEmulatorEndpoint").WhoseValue.Should().Be("hat-emulator:5391");
    }

    [TestMethod]
    public async Task Degraded_AgainstTheHatEmulator_WithoutTheConnection_OmitsTheEndpoint()
    {
        var service = new FakeRoofControllerService { Snapshot = FakeRoofControllerService.HealthySnapshot() with { HatMode = RoofHatMode.Emulated } };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Roof controller is running against the HAT emulator, not the physical HAT");
        result.Data.Should().ContainKey("HatEmulatorEndpoint").WhoseValue.Should().Be(string.Empty);
    }

    [TestMethod]
    public async Task IgnoredLimitSwitches_AreReportedBeforeTheEmulator()
    {
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with { HatMode = RoofHatMode.Emulated, IsIgnoringPhysicalLimitSwitches = true }
        };

        var result = await CreateHealthCheck(service, hatConnection: HatConnections.Emulated()).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be($"Roof controller is ignoring physical limit switches (HAT emulator at 127.0.0.1:{HatEmulatorOptions.DefaultPort})");
    }

    [TestMethod]
    [DataRow("latched", HealthStatus.Unhealthy, "Roof controller safety fault is latched (RelayVerificationFailed) (HAT emulator at hat-emulator:5391)")]
    [DataRow("unverified", HealthStatus.Unhealthy, "Roof controller relay register state is unverified (HAT emulator at hat-emulator:5391)")]
    [DataRow("inputs", HealthStatus.Unhealthy, "Roof controller safety inputs are not healthy (HAT emulator at hat-emulator:5391)")]
    [DataRow("shutting-down", HealthStatus.Unhealthy, "Roof controller is shutting down (HAT emulator at hat-emulator:5391)")]
    [DataRow("error", HealthStatus.Unhealthy, "Roof controller is in error state (HAT emulator at hat-emulator:5391)")]
    [DataRow("unknown", HealthStatus.Degraded, "Roof controller status is unknown (HAT emulator at hat-emulator:5391)")]
    public async Task InEmulatorMode_EveryResultNamesTheEmulator(string state, HealthStatus expectedStatus, string expected)
    {
        // A fault on a test rig must not read as a fault on the observatory roof.
        var snapshot = FakeRoofControllerService.HealthySnapshot() with { HatMode = RoofHatMode.Emulated };
        snapshot = state switch
        {
            "latched" => snapshot with
            {
                Status = RoofControllerStatus.Error,
                IsFaultLatched = true,
                LatchedFaultReason = RoofControllerStopReason.RelayVerificationFailed
            },
            "unverified" => snapshot with { Status = RoofControllerStatus.Error, RelayRegisterState = RoofRelayRegisterState.Unverified, RelayRegisterMask = null },
            "inputs" => snapshot with { InputsHealthy = false, ConsecutiveInputReadFailures = 2 },
            "shutting-down" => snapshot with { IsShuttingDown = true },
            "error" => snapshot with { Status = RoofControllerStatus.Error },
            "unknown" => snapshot with { Status = RoofControllerStatus.Unknown },
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
        var service = new FakeRoofControllerService { Snapshot = snapshot };

        var result = await CreateHealthCheck(service, hatConnection: HatConnections.Emulated("hat-emulator", 5391)).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(expectedStatus);
        result.Description.Should().Be(expected);
    }

    [TestMethod]
    public async Task InEmulatorMode_WithoutTheConnection_AFaultStillNamesTheEmulator()
    {
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with { HatMode = RoofHatMode.Emulated, Status = RoofControllerStatus.Error }
        };

        var result = await CheckAsync(service);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller is in error state (HAT emulator)");
    }

    [TestMethod]
    [DataRow(RoofHatMode.Physical, true, "Physical")]
    [DataRow(RoofHatMode.Simulation, false, "Simulation")]
    [DataRow(RoofHatMode.Unknown, true, "Physical")]
    [DataRow(RoofHatMode.Unknown, false, "Simulation")]
    public async Task HardwareMode_ReportsTheHatMode_OrFallsBackToTheHardwareFlag(RoofHatMode mode, bool physical, string expected)
    {
        var service = new FakeRoofControllerService
        {
            Snapshot = FakeRoofControllerService.HealthySnapshot() with { HatMode = mode, IsUsingPhysicalHardware = physical }
        };

        var result = await CreateHealthCheck(service, hatConnection: HatConnections.Hardware()).CheckHealthAsync(new HealthCheckContext());

        result.Data.Should().ContainKey("HardwareMode").WhoseValue.Should().Be(expected);
        result.Data.Should().ContainKey("HatEmulatorEndpoint").WhoseValue.Should().Be(string.Empty);
    }

    [TestMethod]
    public async Task RealController_AgainstTheHatEmulator_ReportsEmulated_AndIsDegraded()
    {
        var hat = new FakeRoofHat(hardwareBacked: true);
        hat.SetInputs(true, true, false, false);
        var connection = HatConnections.Emulated("hat-emulator", 5391);
        var logger = new CapturingLogger<RoofControllerServiceV4>();
        using var controller = SimulatedRoofControllerService.Create(
            hat, new ManualTimeProvider(), opts => opts.EnableDigitalInputPolling = true, logger, hatConnection: connection);
        (await controller.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        var result = await CreateHealthCheck(controller, hatConnection: connection).CheckHealthAsync(new HealthCheckContext());

        controller.HatMode.Should().Be(RoofHatMode.Emulated);
        controller.GetCurrentStatusSnapshot().HatMode.Should().Be(RoofHatMode.Emulated);
        controller.GetCurrentStatusSnapshot().IsUsingPhysicalHardware.Should().BeTrue("the controller takes its physical-hardware paths");
        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Roof controller is running against the HAT emulator (hat-emulator:5391), not the physical HAT");
        logger.Contains(LogLevel.Warning, "using the HAT emulator at hat-emulator:5391").Should().BeTrue();
    }

    [TestMethod]
    [DataRow(true, RoofHatMode.Physical)]
    [DataRow(false, RoofHatMode.Simulation)]
    public async Task RealController_WithoutTheEmulator_ReportsPhysicalOrSimulation(bool hardwareBacked, RoofHatMode expected)
    {
        var hat = new FakeRoofHat(hardwareBacked);
        hat.SetInputs(true, true, false, false);
        using var controller = SimulatedRoofControllerService.Create(hat, new ManualTimeProvider(), hatConnection: HatConnections.Hardware());
        (await controller.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        controller.HatMode.Should().Be(expected);
        controller.GetCurrentStatusSnapshot().HatMode.Should().Be(expected);
    }
}
