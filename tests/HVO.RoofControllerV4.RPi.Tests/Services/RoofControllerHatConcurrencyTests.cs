using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.RoofControllerV4.RPi.Tests.Services;

[DoNotParallelize]
[TestClass]
public class RoofControllerHatConcurrencyTests
{
    private const int OpenRelayIndex = 1;
    private const int CloseRelayIndex = 2;
    private const int ClearFaultRelayIndex = 3;
    private const int StopRelayIndex = 4;

    private static FakeRoofHat _sharedHat = null!;

    [ClassInitialize]
    public static void ClassInitialize(TestContext context)
    {
        _sharedHat = new FakeRoofHat();
    }

    [TestInitialize]
    public void TestInitialize()
    {
        _sharedHat.SetInputs(true, true, false, false);
        var resetResult = _sharedHat.SetRelaysMask(0x00);
        if (!resetResult.IsSuccessful)
        {
            throw new AssertFailedException($"Failed to reset shared fake hat before test execution: {resetResult.Error?.Message}");
        }

        _sharedHat.ClearRelayWriteLog();
    }

    [TestMethod]
    public async Task ConcurrentRelayToggles_ShouldMaintainConsistentState()
    {
        const int iterations = 250;
        var workerCount = Math.Max(2, Environment.ProcessorCount);

        var tasks = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    EnsureSuccess(_sharedHat.SetRelay(OpenRelayIndex, true), "SetRelay(Open, true)");
                    EnsureSuccess(_sharedHat.SetRelay(CloseRelayIndex, true), "SetRelay(Close, true)");
                    EnsureSuccess(_sharedHat.SetRelay(OpenRelayIndex, false), "SetRelay(Open, false)");
                    EnsureSuccess(_sharedHat.SetRelay(CloseRelayIndex, false), "SetRelay(Close, false)");
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        _sharedHat.RelayMask.Should().Be(0x00, "concurrent operations should leave relays de-energized");

        var writes = _sharedHat.RelayWriteLog;
        writes.Count.Should().Be(workerCount * iterations * 4, "each relay toggle should log an I2C write");
        writes.Should().OnlyContain(entry => entry.Register == 0x01 || entry.Register == 0x02,
            "relay toggles should only use set/clear registers");
    }

    [TestMethod]
    public async Task ConcurrentDigitalInputUpdates_ShouldReturnLatestSnapshot()
    {
        const int iterations = 500;

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                _sharedHat.SetInputs(
                    forwardLimitHigh: (i & 0b001) == 0,
                    reverseLimitHigh: (i & 0b010) == 0,
                    faultHigh: (i & 0b100) == 0,
                    atSpeedHigh: (i & 0b1000) == 0);
            }
        });

        var reader = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var result = _sharedHat.GetAllDigitalInputs();
                if (result.IsFailure)
                {
                    throw new AssertFailedException($"Concurrent read of digital inputs failed: {result.Error?.Message}");
                }
            }
        });

        await Task.WhenAll(writer, reader);

        var finalResult = _sharedHat.GetAllDigitalInputs();
        finalResult.IsSuccessful.Should().BeTrue();
        var finalState = finalResult.Value;

        var expected = (
            in1: ((iterations - 1) & 0b001) == 0,
            in2: ((iterations - 1) & 0b010) == 0,
            in3: ((iterations - 1) & 0b100) == 0,
            in4: ((iterations - 1) & 0b1000) == 0);

        finalState.Should().Be(expected, "the last write should be observable after concurrent readers complete");
    }

    [TestMethod]
    public async Task ConcurrentServices_ShouldNotConflictOnSharedHat()
    {
        var serviceA = CreateService();
        var serviceB = CreateService();

        (await serviceA.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();
        (await serviceB.Initialize(CancellationToken.None)).IsSuccessful.Should().BeTrue();

        const int iterations = 40;

        var openSequence = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                _sharedHat.SetInputs(true, true, false, false);
                EnsureSuccess(serviceA.Open(), "ServiceA.Open");

                _sharedHat.SetInputs(false, true, false, false);
                serviceA.SimForwardLimitRaw(false);

                await Task.Delay(1);
            }
        });

        var closeSequence = Task.Run(async () =>
        {
            for (var i = 0; i < iterations; i++)
            {
                _sharedHat.SetInputs(true, true, false, false);
                EnsureSuccess(serviceB.Close(), "ServiceB.Close");

                _sharedHat.SetInputs(true, false, false, false);
                serviceB.SimReverseLimitRaw(false);

                await Task.Delay(1);
            }
        });

        await Task.WhenAll(openSequence, closeSequence);

        await Task.Delay(5); // allow final stop transitions to settle

        _sharedHat.RelayMask.Should().Be(0x00, "shared hat should not leave any direction relays energized");
        serviceA.Status.Should().NotBe(RoofControllerStatus.Error);
        serviceB.Status.Should().NotBe(RoofControllerStatus.Error);
    }

    private static TestableRoofControllerService CreateService()
    {
        var options = RoofControllerTestFactory.CreateWrappedOptions(opts =>
        {
            opts.SafetyWatchdogTimeout = TimeSpan.FromSeconds(10);
            opts.LimitSwitchDebounce = TimeSpan.FromMilliseconds(10);
            opts.OpenRelayId = OpenRelayIndex;
            opts.CloseRelayId = CloseRelayIndex;
            opts.ClearFaultRelayId = ClearFaultRelayIndex;
            opts.StopRelayId = StopRelayIndex;
        });

        return new TestableRoofControllerService(options, _sharedHat);
    }

    private static void EnsureSuccess<T>(Result<T> result, string operation)
    {
        if (!result.IsSuccessful)
        {
            throw new AssertFailedException($"Operation '{operation}' failed: {result.Error?.Message}");
        }
    }

    private sealed class TestableRoofControllerService : RoofControllerServiceV4
    {
        public TestableRoofControllerService(IOptions<RoofControllerOptionsV4> options, FakeRoofHat hat)
            : base(new NullLogger<RoofControllerServiceV4>(), options, hat)
        {
        }

        public void SimForwardLimitRaw(bool high) => OnForwardLimitSwitchChanged(high);
        public void SimReverseLimitRaw(bool high) => OnReverseLimitSwitchChanged(high);
    }
}
