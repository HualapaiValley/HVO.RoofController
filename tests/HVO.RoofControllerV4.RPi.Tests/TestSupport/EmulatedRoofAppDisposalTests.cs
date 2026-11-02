using System.Globalization;
using System.Net;
using FluentAssertions;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Simulation.Emulator;
using HVO.RoofControllerV4.Simulation.Hat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.RoofControllerV4.RPi.Tests.TestSupport;

/// <summary>The test host keeps its emulator alive until the production entry point has disposed its services.</summary>
[TestClass]
[DoNotParallelize]
public sealed class EmulatedRoofAppDisposalTests
{
    [TestMethod]
    public async Task DisposeAsync_WaitsForTheEntryPointsServiceDisposal_BeforeTheEmulatorCanClose()
    {
        using var session = new HatEmulatorSession(new HatEmulatorSessionOptions());
        await using var server = new HatEmulatorServer(session.CurrentClient, new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        var logs = new RecordingLoggerProvider();
        var gate = new DisposalGate();
        var app = new EmulatedRoofApp(new Dictionary<string, string?>
        {
            ["HatEmulator:Enabled"] = "true",
            ["HatEmulator:Host"] = "127.0.0.1",
            ["HatEmulator:Port"] = server.LocalEndPoint.Port.ToString(CultureInfo.InvariantCulture),
            ["RoofControllerOptionsV4:UseNormallyClosedLimitSwitches"] = "false"
        }, "Development", logs, consoleLog: false,
            configureServices: services => services.AddSingleton(_ => gate));
        Task? disposal = null;
        try
        {
            using var client = app.CreateApiClient();
            var controller = app.Services.GetRequiredService<IRoofControllerServiceV4>();
            // Resolved last, the gate is disposed before the controller. Let Program.Run start provider disposal first:
            // the factory's own later disposal sees an already-disposed provider and would otherwise return at once.
            app.Services.GetRequiredService<DisposalGate>();
            app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            disposal = app.DisposeAsync().AsTask();
            await FluentActions.Awaiting(() => disposal.WaitAsync(TimeSpan.FromMilliseconds(200)))
                .Should().ThrowAsync<TimeoutException>("the entry point still has not reached the controller's disposal");

            gate.Release.TrySetResult();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            controller.IsServiceDisposed.Should().BeTrue();
            var finalStatus = controller.GetCurrentStatusSnapshot();
            finalStatus.RelayRegisterState.Should().Be(RoofRelayRegisterState.Verified);
            finalStatus.RelayRegisterMask.Should().Be(0);
            logs.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error,
                "the emulator was still available for the disposal all-off write and read-back");
        }
        finally
        {
            gate.Release.TrySetResult();
            if (disposal is not null)
            {
                await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            }

            await app.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ARealDisposalReadBackFailure_StillLogsCritical()
    {
        await using var rig = await EmulatedRoofRig.StartAsync(new EmulatedRoofRigOptions { ConsoleLog = false });
        var controller = (RoofControllerServiceV4)rig.Controller;
        rig.Session.Client.FailWhen = access => access.IsRead && access.Covers(SmI010Board.RelayValueRegister);
        try
        {
            await controller.DisposeAsync();

            rig.Logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Critical
                && entry.Message.StartsWith("Disposal could not verify the relay register all-off state", StringComparison.Ordinal));
        }
        finally
        {
            rig.Session.Client.FailWhen = null;
        }
    }

    private sealed class DisposalGate : IAsyncDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            Entered.TrySetResult();
            await Release.Task;
        }
    }
}
