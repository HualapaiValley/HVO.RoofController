using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.RPi.HealthChecks;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Tests.HealthChecks;

[TestClass]
public sealed class RoofControllerHealthCheckTests
{
    [TestMethod]
    public async Task CheckHealthAsync_ShouldReturnHealthy_WhenControllerReady()
    {
        // Arrange
        var service = new TestRoofControllerService
        {
            IsInitialized = true,
            Status = RoofControllerStatus.Open,
            IsServiceDisposed = false,
            IsWatchdogActive = true,
            WatchdogSecondsRemaining = 42.5,
            LastStopReason = RoofControllerStopReason.NormalStop,
            LastTransitionUtc = DateTimeOffset.UtcNow,
            IsMoving = false
        };

        var check = CreateHealthCheck(service, new RoofControllerOptionsV4());

        // Act
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        // Assert
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data.Should().ContainKey("Ready").WhoseValue.Should().Be(true);
    }

    [TestMethod]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenServiceDisposed()
    {
        var service = new TestRoofControllerService
        {
            IsInitialized = true,
            IsServiceDisposed = true,
            Status = RoofControllerStatus.Open
        };

        var result = await CreateHealthCheck(service, new RoofControllerOptionsV4()).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller service is disposed");
    }

    [TestMethod]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenNotInitialized()
    {
        var service = new TestRoofControllerService
        {
            IsInitialized = false,
            IsServiceDisposed = false,
            Status = RoofControllerStatus.NotInitialized
        };

        var result = await CreateHealthCheck(service, new RoofControllerOptionsV4()).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller is not initialized");
    }

    [TestMethod]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenStatusIsError()
    {
        var service = new TestRoofControllerService
        {
            IsInitialized = true,
            Status = RoofControllerStatus.Error
        };

        var result = await CreateHealthCheck(service, new RoofControllerOptionsV4()).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Be("Roof controller is in error state");
    }

    [TestMethod]
    public async Task CheckHealthAsync_ShouldReturnDegraded_WhenStatusUnknown()
    {
        var service = new TestRoofControllerService
        {
            IsInitialized = true,
            Status = RoofControllerStatus.Unknown
        };

        var result = await CreateHealthCheck(service, new RoofControllerOptionsV4()).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Be("Roof controller status is unknown");
    }

    [TestMethod]
    public async Task CheckHealthAsync_ShouldReturnUnhealthy_WhenExceptionThrown()
    {
        var service = new ThrowingRoofControllerService();

        var result = await CreateHealthCheck(service, new RoofControllerOptionsV4()).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Exception.Should().BeOfType<InvalidOperationException>();
    }

    private static RoofControllerHealthCheck CreateHealthCheck(IRoofControllerServiceV4 service, RoofControllerOptionsV4 options)
    {
        return new RoofControllerHealthCheck(service, NullLogger<RoofControllerHealthCheck>.Instance, Options.Create(options));
    }

    private sealed class TestRoofControllerService : IRoofControllerServiceV4
    {
        public event EventHandler<RoofStatusChangedEventArgs>? StatusChanged;

        private RoofControllerOptionsV4 _configuration = new();

        public bool IsInitialized { get; set; }
        public bool IsServiceDisposed { get; set; }
        public RoofControllerStatus Status { get; set; }
        public bool IsMoving { get; set; }
        public RoofControllerStopReason LastStopReason { get; set; }
        public DateTimeOffset? LastTransitionUtc { get; set; }
        public bool IsWatchdogActive { get; set; }
        public double? WatchdogSecondsRemaining { get; set; }
        public bool IsAtSpeed { get; set; }
        public bool IsUsingPhysicalHardware { get; set; }
        public bool IsIgnoringPhysicalLimitSwitches { get; set; }

        public RoofStatusResponse GetCurrentStatusSnapshot() => new(Status, IsMoving, LastStopReason, LastTransitionUtc, IsWatchdogActive, WatchdogSecondsRemaining, IsAtSpeed, IsUsingPhysicalHardware, IsIgnoringPhysicalLimitSwitches);

        public Task<Result<bool>> Initialize(CancellationToken cancellationToken) => Task.FromResult(Result<bool>.Success(true));
        public Result<RoofControllerStatus> Stop(RoofControllerStopReason reason = RoofControllerStopReason.NormalStop) => RoofControllerStatus.Stopped;
        public Result<RoofControllerStatus> Open() => RoofControllerStatus.Open;
        public Result<RoofControllerStatus> Close() => RoofControllerStatus.Closed;
        public void RefreshStatus(bool forceHardwareRead = false)
        {
            StatusChanged?.Invoke(this, new RoofStatusChangedEventArgs(GetCurrentStatusSnapshot()));
        }

        public Task<Result<bool>> ClearFault(int pulseMs = 250, CancellationToken cancellationToken = default) => Task.FromResult(Result<bool>.Success(true));

        public RoofControllerOptionsV4 GetConfigurationSnapshot() => _configuration;

        public Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 configuration)
        {
            _configuration = configuration;
            return Result<RoofControllerOptionsV4>.Success(configuration);
        }
    }

    private sealed class ThrowingRoofControllerService : IRoofControllerServiceV4
    {
        public event EventHandler<RoofStatusChangedEventArgs>? StatusChanged;

        private RoofControllerOptionsV4 _configuration = new();

        public bool IsInitialized => throw new InvalidOperationException("status failure");
        public bool IsServiceDisposed => false;
        public RoofControllerStatus Status => RoofControllerStatus.Open;
        public bool IsMoving => false;
        public RoofControllerStopReason LastStopReason => RoofControllerStopReason.None;
        public DateTimeOffset? LastTransitionUtc => null;
        public bool IsWatchdogActive => false;
        public double? WatchdogSecondsRemaining => 0;
        public bool IsAtSpeed => true;
        public bool IsUsingPhysicalHardware => true;
        public bool IsIgnoringPhysicalLimitSwitches => false;
        public RoofStatusResponse GetCurrentStatusSnapshot() => new(RoofControllerStatus.Open, false, RoofControllerStopReason.None, null, false, 0, true, IsUsingPhysicalHardware, IsIgnoringPhysicalLimitSwitches);
        public Task<Result<bool>> Initialize(CancellationToken cancellationToken) => Task.FromResult(Result<bool>.Success(true));
        public Result<RoofControllerStatus> Stop(RoofControllerStopReason reason = RoofControllerStopReason.NormalStop) => RoofControllerStatus.Stopped;
        public Result<RoofControllerStatus> Open() => RoofControllerStatus.Open;
        public Result<RoofControllerStatus> Close() => RoofControllerStatus.Closed;
        public void RefreshStatus(bool forceHardwareRead = false)
        {
            StatusChanged?.Invoke(this, new RoofStatusChangedEventArgs(GetCurrentStatusSnapshot()));
        }

        public Task<Result<bool>> ClearFault(int pulseMs = 250, CancellationToken cancellationToken = default) => Task.FromResult(Result<bool>.Success(true));

        public RoofControllerOptionsV4 GetConfigurationSnapshot() => _configuration;

        public Result<RoofControllerOptionsV4> UpdateConfiguration(RoofControllerOptionsV4 configuration) => throw new InvalidOperationException("configuration failure");
    }
}
