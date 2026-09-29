using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HVO.Core.Results;
using HVO.RoofControllerV4.Cli;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Tests.Client;
using HVO.RoofControllerV4.RPi.Tests.Controllers;
using HVO.RoofControllerV4.RPi.Tests.Security;
using HVO.RoofControllerV4.RPi.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using static HVO.RoofControllerV4.RPi.Tests.Client.ClientTestSupport;
using ManualTimeProvider = HVO.RoofControllerV4.RPi.Tests.TestSupport.ManualTimeProvider;

namespace HVO.RoofControllerV4.RPi.Tests.Cli;

/// <summary>
/// <c>hvo-roof</c>'s roof and sign-in commands against the in-process controller (#45): status and <c>--watch</c>
/// (changes, staleness, a refused key), health and its probes, stop and its exit codes, open and close (following the
/// motion, renewing the lease, Stop on Ctrl+C), lease, clear-fault, whoami, login, logout and passwd.
/// </summary>
[TestClass]
public sealed class RoofCliRoofCommandTests
{
    private const string WrongKey = "test-wrong-key-not-a-real-secret-99";
    private const string Person = "olive";

    private static readonly DateTimeOffset ClientStart = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static void RaiseStatus(RoofApiTestHost host, RoofStatusResponse status)
        => host.RoofService.Raise(service => service.StatusChanged += null, new RoofStatusChangedEventArgs(status));

    /// <summary>A roof whose snapshot the test controls: the commands, the hub and its heartbeat all read it.</summary>
    private static Mock<IRoofControllerServiceV4> RoofShowing(Func<RoofStatusResponse> snapshot)
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.GetCurrentStatusSnapshot()).Returns(snapshot);
        return roof;
    }

    private static RoofStatusResponse Moving(RoofMotionDirection direction, double? leaseSeconds)
        => RoofServiceMock.Snapshot(
            direction == RoofMotionDirection.Opening ? RoofControllerStatus.Opening : RoofControllerStatus.Closing,
            direction) with { LeaseSecondsRemaining = leaseSeconds };

    private static Mock<IRoofControllerServiceV4> Refusing(RoofControllerErrorCode code, string detail, Action<Mock<IRoofControllerServiceV4>, Result<RoofControllerStatus>> setup)
    {
        var roof = RoofServiceMock.Create();
        setup(roof, Result<RoofControllerStatus>.Failure(new RoofControllerException(code, detail)));
        return roof;
    }

    private static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Adds <see cref="Person"/> (operator) and signs in as them with <c>hvo-roof login</c>.</summary>
    private static async Task LoginAsync(RoofApiTestHost host, CliRig rig)
    {
        await RoofClientApiTests.AddUserAsync(host, Person, RoofControllerApiContract.OperatorRole);
        rig.Input.Enqueue(TestSecrets.Password);
        var login = await rig.RunAsync("login", Person);
        login.Code.Should().Be(RoofExitCode.Success, login.ToString());
        rig.Prompts.Clear();
    }

    // ---- Status ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Status_WithAViewerKey_PrintsTheRows()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("Roof:        Closed")
            .And.Contain("Last stop:   Stopped by operator")
            .And.Contain("Fault:       none")
            .And.Contain("Relays:      register read-back matched");
        result.Error.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Status_Json_WritesTheControllersSnapshot()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("status", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("status").GetString().Should().Be("Closed");
        result.Json.GetProperty("statusVersion").GetInt64().Should().Be(11);
        result.Json.GetProperty("relayRegisterState").GetString().Should().Be("Verified");
        result.Error.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Status_WithAWrongKey_ExitsSignedOut()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(WrongKey);

        var result = await rig.RunAsync("status");

        result.Code.Should().Be(RoofExitCode.SignedOut, result.ToString());
        result.Error.Should().Contain("Sign in again.");
        result.Out.Should().BeEmpty();
    }

    // ---- Status --watch --------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task StatusWatch_PrintsALinePerChange_AndExitsZero_WhenInterruptedWhileLive()
    {
        var current = RoofServiceMock.Snapshot();
        using var host = RoofClientApiTests.CreateHost(RoofShowing(() => Volatile.Read(ref current)));
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);
        await using var run = RunningCommand.Start(rig, "status", "--watch");
        await run.WaitForOutAsync("Closed; last stop: Stopped by operator", "the first snapshot");

        Volatile.Write(ref current, Moving(RoofMotionDirection.Opening, leaseSeconds: 45) with { StatusVersion = 12 });
        RaiseStatus(host, Volatile.Read(ref current));
        await run.WaitForOutAsync("Opening, commanded to open; lease 45 s; last stop: Stopped by operator", "the change");
        Volatile.Write(ref current, RoofServiceMock.Snapshot(faultLatched: true) with { StatusVersion = 13 });
        RaiseStatus(host, Volatile.Read(ref current));
        await run.WaitForOutAsync("fault LATCHED", "the fault");
        var result = await run.InterruptAsync();

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("SAFETY: Fault latched.");
        result.Out.Should().NotContain("STALE", "the feed was live when Ctrl+C ended the watch");
        result.Error.Should().BeEmpty("Ctrl+C ends a watch quietly");
    }

    [TestMethod]
    public async Task StatusWatch_Json_WritesAnObjectPerLine_ThenStale_AndExitsStale()
    {
        // The controller's clock is frozen (no heartbeat); the client's moves only when told.
        var serverClock = new ManualTimeProvider();
        var clientClock = new ManualTimeProvider(ClientStart);
        using var host = RoofClientApiTests.CreateHost(configureServices: services => services.AddSingleton<TimeProvider>(serverClock));
        using var rig = new CliRig(host) { Time = clientClock };
        rig.UseApiKey(TestApiKeys.Viewer);
        await using var run = RunningCommand.Start(rig, "status", "--watch", "--json");
        await run.WaitForOutAsync("\"stale\":false", "the first snapshot");

        clientClock.Advance(FastFeed.StaleAfter);
        await run.WaitForOutAsync("\"stale\":true", "the stale line");
        var result = await run.InterruptAsync();

        result.Code.Should().Be(RoofExitCode.Stale, result.ToString());
        var lines = result.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();
        lines.Should().HaveCount(2, result.ToString());
        lines[0].GetProperty("stale").GetBoolean().Should().BeFalse();
        lines[0].GetProperty("sequence").GetInt64().Should().BePositive();
        lines[0].GetProperty("instanceId").GetString().Should().NotBeNullOrEmpty();
        lines[0].GetProperty("restarted").GetBoolean().Should().BeFalse();
        lines[0].GetProperty("status").GetProperty("status").GetString().Should().Be("Closed");
        lines[1].GetProperty("stale").GetBoolean().Should().BeTrue();
        lines[1].GetProperty("staleSince").GetDateTimeOffset().Should().Be(ClientStart + FastFeed.StaleAfter);
    }

    [TestMethod]
    public async Task StatusWatch_WhenTheControllerFallsSilent_SaysStale_AndExitsStale()
    {
        var serverClock = new ManualTimeProvider();
        var clientClock = new ManualTimeProvider(ClientStart);
        using var host = RoofClientApiTests.CreateHost(configureServices: services => services.AddSingleton<TimeProvider>(serverClock));
        using var rig = new CliRig(host) { Time = clientClock };
        rig.UseApiKey(TestApiKeys.Viewer);
        await using var run = RunningCommand.Start(rig, "status", "--watch");
        await run.WaitForOutAsync("Closed; last stop:", "the first snapshot");

        clientClock.Advance(FastFeed.StaleAfter);
        await run.WaitForOutAsync("STALE:", "the stale line");
        var result = await run.InterruptAsync();

        result.Code.Should().Be(RoofExitCode.Stale, result.ToString());
        result.Out.Should().Contain(
            $"STALE: no status from the controller since {Time(ClientStart + FastFeed.StaleAfter)}. Last known: Closed. Stop still works: 'hvo-roof stop'.");
    }

    [TestMethod]
    public async Task StatusWatch_WithAWrongKey_EndsByItself_AndExitsSignedOut()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(WrongKey);
        await using var run = RunningCommand.Start(rig, "status", "--watch");

        var result = await run.EndAsync();

        result.Code.Should().Be(RoofExitCode.SignedOut, result.ToString());
        result.Error.Should().Contain("Sign in again.");
        result.Out.Should().BeEmpty();
    }

    // ---- Health ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Health_Degraded_PrintsTheChecks_AndExitsUnhealthy()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("health");

        result.Code.Should().Be(RoofExitCode.Unhealthy, result.ToString());
        result.Out.Should().StartWith("Health: Degraded")
            .And.Contain("CHECK")
            .And.Contain("roof_controller")
            .And.Contain("identity_store");
    }

    [TestMethod]
    public async Task Health_Healthy_ExitsZero()
    {
        // Without the identity store check (which reports Degraded when no store file is configured).
        using var host = RoofClientApiTests.CreateHost(configureServices: services => services.PostConfigure<HealthCheckServiceOptions>(
            options =>
            {
                foreach (var check in options.Registrations.Where(r => r.Name == "identity_store").ToList())
                {
                    options.Registrations.Remove(check);
                }
            }));
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("health");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().StartWith("Health: Healthy");
    }

    [TestMethod]
    public async Task Health_Json_WritesTheReport()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("health", "--json");

        result.Code.Should().Be(RoofExitCode.Unhealthy, result.ToString());
        result.Json.GetProperty("status").GetString().Should().Be("Degraded");
        result.Json.GetProperty("checks").EnumerateArray().Select(check => check.GetProperty("name").GetString())
            .Should().Contain(["roof_controller", "identity_store"]);
    }

    [TestMethod]
    [DataRow("live")]
    [DataRow("ready")]
    public async Task HealthProbe_NeedsNoCredential_AndExitsZeroWhenHealthy(string probe)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);

        var result = await rig.RunAsync("health", "--probe", probe, "--controller", "http://localhost/");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be($"{probe}: Healthy{Environment.NewLine}");
    }

    [TestMethod]
    public async Task HealthProbe_ReadyUnhealthy_Json_ExitsUnhealthy()
    {
        using var host = RoofClientApiTests.CreateHost(configureServices: services => services.AddHealthChecks()
            .AddCheck("test_failing", () => HealthCheckResult.Unhealthy("A hardware check failed (test)."), tags: ["hardware"]));
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("health", "--probe", "ready", "--json");

        result.Code.Should().Be(RoofExitCode.Unhealthy, result.ToString());
        result.Json.GetProperty("probe").GetString().Should().Be("ready");
        result.Json.GetProperty("statusCode").GetInt32().Should().Be(503);
        result.Json.GetProperty("status").GetString().Should().Be("Unhealthy");
        result.Json.GetProperty("isHealthy").GetBoolean().Should().BeFalse();
    }

    [TestMethod]
    public async Task HealthProbe_WithAnotherValue_IsAUsageError()
    {
        using var rig = new CliRig();

        var result = await rig.RunAsync("health", "--probe", "dead");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("'dead'").And.Contain("Run 'hvo-roof health --help' for usage.");
        result.Out.Should().BeEmpty();
    }

    // ---- Stop ------------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Stop_Verified_SaysSo_AndExitsZero()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("stop");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be(RoofStopText.AcknowledgedVerified + Environment.NewLine);
        result.Error.Should().BeEmpty();
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once());
    }

    [TestMethod]
    public async Task Stop_WithAViewerKey_AsksNoConfirmationAndNoSignIn()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host) { Interactive = true };
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("stop");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().BeEmpty("Stop is sent at once, whoever asks");
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once());
    }

    [TestMethod]
    [DataRow(true, RoofStopText.SentUnverified)]
    [DataRow(false, RoofStopText.AcknowledgedUnverified)]
    public async Task Stop_RelaysNotVerified_SendsTheOperatorToTheRoof_AndExitsStopNotVerified(bool refused, string message)
    {
        var roof = RoofShowing(() => RoofServiceMock.Snapshot(relayState: RoofRelayRegisterState.Unverified));
        if (refused)
        {
            roof.Setup(service => service.Stop(It.IsAny<RoofControllerStopReason>())).Returns(Result<RoofControllerStatus>.Failure(
                new RoofControllerException(RoofControllerErrorCode.RelayStateUnverified, "The relays could not be read back (test).")));
        }

        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("stop");

        result.Code.Should().Be(RoofExitCode.StopNotVerified, result.ToString());
        result.Error.Should().Be(message + Environment.NewLine);
        result.Out.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Stop_Unreachable_SaysStopFailed_AndExitsStopNotVerified()
    {
        using var rig = new CliRig();
        rig.UseApiKey(TestApiKeys.Operator);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new RoofCliHost
        {
            Out = output,
            Error = error,
            GetEnvironmentVariable = _ => null,
            ReadLine = (_, _) => null,
            CreateHandler = FailingHandler.Refused,
            StatusFeed = FastFeed
        };

        var code = await RoofCli.RunAsync(["stop", "--credentials-file", rig.CredentialsPath], host);
        var result = new CliResult(code, output.ToString(), error.ToString());

        result.Code.Should().Be(RoofExitCode.StopNotVerified, result.ToString());
        result.Error.Should().Be("Stop failed: The controller could not be reached. Use the stop control at the roof." + Environment.NewLine);
        result.Out.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Stop_Json_WritesTheOutcome()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("stop", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("outcome").GetString().Should().Be("Acknowledged");
        result.Json.GetProperty("message").GetString().Should().Be(RoofStopText.AcknowledgedVerified);
        result.Json.GetProperty("exitCode").GetInt32().Should().Be(0);
        result.Json.GetProperty("code").ValueKind.Should().Be(JsonValueKind.Null);
        result.Json.GetProperty("status").GetProperty("relayRegisterState").GetString().Should().Be("Verified");
        result.Error.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Stop_WithAWrongKey_SaysTheKeyWasRefused_AndExitsSignedOut()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(WrongKey);

        var result = await rig.RunAsync("stop");

        result.Code.Should().Be(RoofExitCode.SignedOut, result.ToString());
        result.Error.Should().Be(RoofStopText.KeyRefused + Environment.NewLine);
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    // ---- Open and close --------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Open_NoWait_PrintsTheRoofAndTheLease()
    {
        using var host = RoofClientApiTests.CreateHost(RoofShowing(() => Moving(RoofMotionDirection.Opening, leaseSeconds: 45)));
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("open", "--no-wait");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be(
            "Open accepted. Roof: Opening, commanded to open." + Environment.NewLine
            + "The controller holds this motion on an operator lease (45 s): it stops the roof unless 'hvo-roof lease' renews it."
            + Environment.NewLine);
        host.RoofService.Verify(service => service.Open(), Times.Once());
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    public async Task Close_NoWait_Json_WritesTheStatus()
    {
        using var host = RoofClientApiTests.CreateHost(RoofShowing(() => Moving(RoofMotionDirection.Closing, leaseSeconds: 45)));
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("close", "--no-wait", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("status").GetString().Should().Be("Closing");
        result.Json.GetProperty("commandedMotion").GetString().Should().Be("Closing");
        result.Json.GetProperty("leaseSecondsRemaining").GetDouble().Should().Be(45);
        host.RoofService.Verify(service => service.Close(), Times.Once());
    }

    [TestMethod]
    public async Task Open_WhenTheRoofIsNotMoving_ReturnsAtOnce()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("open");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be("Open accepted. Roof: Closed." + Environment.NewLine);
    }

    [TestMethod]
    public async Task Open_Refused_PrintsTheReason_AndExitsRefused()
    {
        var roof = Refusing(RoofControllerErrorCode.FaultLatched, "A drive fault is latched (test).", (mock, failure) => mock.Setup(s => s.Open()).Returns(failure));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("open");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("A fault is latched. Resolve the cause, then clear the fault. [FaultLatched]")
            .And.Contain("A drive fault is latched (test).");
        result.Out.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Close_Refused_Json_ReportsTheHttpStatusAndTheCode()
    {
        var roof = Refusing(RoofControllerErrorCode.InterlockActive, "The closed limit is active (test).", (mock, failure) => mock.Setup(s => s.Close()).Returns(failure));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("close", "--json");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        var error = result.Json.GetProperty("error");
        error.GetProperty("exitCode").GetInt32().Should().Be(7);
        error.GetProperty("kind").GetString().Should().Be("Refused");
        error.GetProperty("status").GetInt32().Should().Be(409);
        error.GetProperty("code").GetString().Should().Be("InterlockActive");
        error.GetProperty("message").GetString().Should().Be("A safety interlock refused the command. [InterlockActive]");
        error.GetProperty("detail").GetString().Should().Be("The closed limit is active (test).");
        result.Error.Should().BeEmpty("with --json the error is on standard output");
    }

    [TestMethod]
    [DataRow("open")]
    [DataRow("close")]
    public async Task Motion_WithAViewerKey_IsForbidden(string command)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync(command);

        result.Code.Should().Be(RoofExitCode.Forbidden, result.ToString());
        result.Error.Should().Contain("Your role does not permit this.");
        host.RoofService.Verify(service => service.Open(), Times.Never());
        host.RoofService.Verify(service => service.Close(), Times.Never());
    }

    [TestMethod]
    public async Task Open_Following_RenewsTheLease_UntilTheRoofIsOpen()
    {
        var clock = new ManualTimeProvider(ClientStart);
        var current = Moving(RoofMotionDirection.Opening, leaseSeconds: 1.5);
        var renewals = 0;
        var roof = RoofShowing(() => Volatile.Read(ref current));
        roof.Setup(service => service.RenewLease()).Returns(() =>
        {
            Interlocked.Increment(ref renewals);
            return Result<RoofStatusResponse>.Success(Volatile.Read(ref current));
        });
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host) { Time = clock };
        rig.UseApiKey(TestApiKeys.Operator);
        await using var run = RunningCommand.Start(rig, "open");
        await run.WaitForOutAsync("Open accepted. Following the motion; Ctrl+C sends Stop.", "the motion to be followed");

        // A third of the 1.5 s lease (the 0.5 s minimum): the client's clock moves in steps until the renewal is sent.
        await WaitUntilAsync(
            () =>
            {
                if (Volatile.Read(ref renewals) > 0)
                {
                    return true;
                }

                clock.Advance(TimeSpan.FromMilliseconds(500));
                return false;
            },
            "the lease renewal");
        Volatile.Write(ref current, RoofServiceMock.Snapshot(RoofControllerStatus.Open) with { StatusVersion = 12 });
        RaiseStatus(host, Volatile.Read(ref current));
        var result = await run.EndAsync();

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("Opening, commanded to open; lease 2 s; last stop: Stopped by operator")
            .And.EndWith("Done. Roof: Open." + Environment.NewLine);
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Never());
    }

    [TestMethod]
    public async Task Close_Following_StoppedBeforeTheEnd_ExitsFailed()
    {
        var current = Moving(RoofMotionDirection.Closing, leaseSeconds: null);
        using var host = RoofClientApiTests.CreateHost(RoofShowing(() => Volatile.Read(ref current)));
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        await using var run = RunningCommand.Start(rig, "close");
        await run.WaitForOutAsync("Close accepted. Following the motion; Ctrl+C sends Stop.", "the motion to be followed");

        Volatile.Write(ref current, RoofServiceMock.Snapshot(RoofControllerStatus.Stopped) with { StatusVersion = 12 });
        RaiseStatus(host, Volatile.Read(ref current));
        var result = await run.EndAsync();

        result.Code.Should().Be(RoofExitCode.Failed, result.ToString());
        result.Error.Should().StartWith("The roof stopped before the end: Stopped. Stopped by operator, ");
        result.Out.Should().NotContain("Done.");
    }

    [TestMethod]
    public async Task Open_Following_CtrlC_SendsStop_AndExitsInterrupted()
    {
        using var host = RoofClientApiTests.CreateHost(RoofShowing(() => Moving(RoofMotionDirection.Opening, leaseSeconds: null)));
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        await using var run = RunningCommand.Start(rig, "open");
        await run.WaitForOutAsync("Open accepted. Following the motion; Ctrl+C sends Stop.", "the motion to be followed");

        var result = await run.InterruptAsync();

        result.ExitCode.Should().Be(130, result.ToString());
        result.Code.Should().Be(RoofExitCode.Interrupted, result.ToString());
        result.Error.Should().Contain("Interrupted: Stop sent.");
        result.Out.Should().Contain(RoofStopText.AcknowledgedVerified);
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once());
    }

    [TestMethod]
    public async Task Open_CtrlC_BeforeTheAnswerArrives_SendsStop_AndExitsInterrupted()
    {
        using var host = RoofClientApiTests.CreateHost(RoofShowing(() => Moving(RoofMotionDirection.Opening, leaseSeconds: null)));
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The controller gets the Open, but its answer never arrives: the roof may be moving when Ctrl+C is pressed.
        await using var run = new RunningCommand(
            (output, error) =>
            {
                var rigHost = rig.CreateHost(output, error);
                return new RoofCliHost
                {
                    Out = rigHost.Out,
                    Error = rigHost.Error,
                    GetEnvironmentVariable = rigHost.GetEnvironmentVariable,
                    ReadLine = rigHost.ReadLine,
                    StatusFeed = rigHost.StatusFeed,
                    WebSocketFactory = rigHost.WebSocketFactory,
                    CreateHandler = () => new AnswerLostHandler(rigHost.CreateHandler!(), "/Open", delivered)
                };
            },
            "open", "--credentials-file", rig.CredentialsPath);
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var result = await run.InterruptAsync();

        result.Code.Should().Be(RoofExitCode.Interrupted, result.ToString());
        result.Error.Should().Contain("Interrupted: Stop sent.");
        result.Out.Should().Contain(RoofStopText.AcknowledgedVerified).And.NotContain("Open accepted");
        host.RoofService.Verify(service => service.Open(), Times.Once());
        host.RoofService.Verify(service => service.Stop(It.IsAny<RoofControllerStopReason>()), Times.Once());
    }

    [TestMethod]
    public async Task Open_Following_LeaseCannotBeRenewed_SaysSo_AndExitsUnreachable()
    {
        var clock = new ManualTimeProvider(ClientStart);
        using var host = RoofClientApiTests.CreateHost(RoofShowing(() => Moving(RoofMotionDirection.Opening, leaseSeconds: 1.5)));
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        await using var run = new RunningCommand(
            (output, error) => new RoofCliHost
            {
                Out = output,
                Error = error,
                GetEnvironmentVariable = _ => null,
                ReadLine = (_, _) => null,
                Time = clock,
                CreateHandler = () => new LeaseUnreachableHandler(CreateHandler(() => host.Server)),
                WebSocketFactory = WebSocketFactory(() => host.Server),
                StatusFeed = FastFeed
            },
            "open", "--credentials-file", rig.CredentialsPath);
        await run.WaitForOutAsync("Open accepted. Following the motion; Ctrl+C sends Stop.", "the motion to be followed");

        await WaitUntilAsync(
            () =>
            {
                if (run.Error.Contains("The lease could not be renewed", StringComparison.Ordinal))
                {
                    return true;
                }

                clock.Advance(TimeSpan.FromMilliseconds(500));
                return false;
            },
            "the failed renewal");
        var result = await run.EndAsync();

        result.Code.Should().Be(RoofExitCode.Unreachable, result.ToString());
        result.Error.Should().Be(
            "The lease could not be renewed: The controller could not be reached. If the controller is running, it stops the roof when the lease runs out."
            + Environment.NewLine);
        host.RoofService.Verify(service => service.RenewLease(), Times.Never());
    }

    // ---- Lease -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Lease_Renewed_PrintsTheTimeLeft()
    {
        using var host = RoofClientApiTests.CreateHost(RoofShowing(() => Moving(RoofMotionDirection.Opening, leaseSeconds: 42)));
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("lease");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be("Lease renewed: 42 s left. Roof: Opening, commanded to open." + Environment.NewLine);
        host.RoofService.Verify(service => service.RenewLease(), Times.Once());
    }

    [TestMethod]
    public async Task Lease_WithNoMotion_IsRefused()
    {
        var roof = RoofServiceMock.Create();
        roof.Setup(service => service.RenewLease()).Returns(Result<RoofStatusResponse>.Failure(
            new RoofControllerException(RoofControllerErrorCode.LeaseNotActive, "No motion holds a lease (test).")));
        using var host = RoofClientApiTests.CreateHost(roof);
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("lease");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("No leased motion is active. [LeaseNotActive]");
    }

    // ---- Clear fault -----------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(null, RoofControllerLimits.DefaultClearFaultPulseMilliseconds)]
    [DataRow("500", 500)]
    public async Task ClearFault_SendsThePulse_AndPrintsTheFault(string? pulse, int sent)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync(pulse is null ? ["clear-fault"] : ["clear-fault", "--pulse-ms", pulse]);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be("Clear fault accepted. Fault: none. Roof: Closed." + Environment.NewLine);
        host.RoofService.Verify(service => service.ClearFault(sent, It.IsAny<CancellationToken>()), Times.Once());
    }

    [TestMethod]
    public async Task ClearFault_WithANonPositivePulse_IsAUsageError()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("clear-fault", "--pulse-ms", "0");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("--pulse-ms must be a positive number of milliseconds.")
            .And.Contain("Run 'hvo-roof clear-fault --help' for usage.");
        host.RoofService.Verify(service => service.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [TestMethod]
    public async Task ClearFault_OutsideTheControllersRange_IsRefused()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("clear-fault", "--pulse-ms", "10");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("The request was invalid.");
        host.RoofService.Verify(service => service.ClearFault(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    // ---- Whoami ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task WhoAmI_WithAKey_ShowsTheControllerAndTheKey()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);

        var result = await rig.RunAsync("whoami");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Contain("Controller: http://localhost/")
            .And.Contain($"Credential: {rig.CredentialsPath}")
            .And.Contain("Name:       test-operator")
            .And.Contain("Role:       operator")
            .And.Contain("Kind:       API key")
            .And.NotContain("Expires:");
    }

    [TestMethod]
    public async Task WhoAmI_WithASession_Json_ShowsThePerson()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        await LoginAsync(host, rig);

        var result = await rig.RunAsync("whoami", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("controller").GetString().Should().Be("http://localhost/");
        result.Json.GetProperty("credentialSource").GetString().Should().Be(rig.CredentialsPath);
        var caller = result.Json.GetProperty("caller");
        caller.GetProperty("name").GetString().Should().Be(Person);
        caller.GetProperty("role").GetString().Should().Be(RoofControllerApiContract.OperatorRole);
        caller.GetProperty("kind").GetString().Should().Be("Session");
        caller.GetProperty("expiresUtc").ValueKind.Should().Be(JsonValueKind.String);
    }

    // ---- Login -----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Login_ReadsThePasswordAsASecret_AndSavesTheSessionPrivately()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        await RoofClientApiTests.AddUserAsync(host, Person, RoofControllerApiContract.OperatorRole);
        rig.Input.Enqueue(TestSecrets.Password);

        var result = await rig.RunAsync("login", Person);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        rig.Prompts.Should().Equal([("Password for olive: ", true)]);
        var session = rig.Stored!.Session!;
        session.Name.Should().Be(Person);
        session.Role.Should().Be(RoofControllerApiContract.OperatorRole);
        session.Token.Should().NotBeNullOrEmpty();
        session.ExpiresUtc.Should().NotBeNull();
        result.Out.Should().Be(
            $"Signed in as olive (operator) until {Time(session.ExpiresUtc!.Value)}. Saved in {rig.CredentialsPath}.{Environment.NewLine}");
        result.Error.Should().BeEmpty();
        result.ToString().Should().NotContain(TestSecrets.Password);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(rig.CredentialsPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [TestMethod]
    public async Task Login_Json_WritesTheSession()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        await RoofClientApiTests.AddUserAsync(host, Person, RoofControllerApiContract.OperatorRole);
        rig.Input.Enqueue(TestSecrets.Password);

        var result = await rig.RunAsync("login", Person, "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("name").GetString().Should().Be(Person);
        result.Json.GetProperty("role").GetString().Should().Be(RoofControllerApiContract.OperatorRole);
        result.Json.GetProperty("expiresUtc").GetDateTimeOffset().Should().Be(rig.Stored!.Session!.ExpiresUtc!.Value);
        result.Json.GetProperty("credentialsFile").GetString().Should().Be(rig.CredentialsPath);
    }

    [TestMethod]
    public async Task Login_WithAWrongPassword_SavesNothing_AndExitsSignedOut()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);
        await RoofClientApiTests.AddUserAsync(host, Person, RoofControllerApiContract.OperatorRole);
        rig.Input.Enqueue(TestSecrets.OtherPassword);

        var result = await rig.RunAsync("login", Person);

        result.Code.Should().Be(RoofExitCode.SignedOut, result.ToString());
        result.Error.Should().Contain("Sign-in failed. Check the name and the password or PIN. [SignInFailed]");
        rig.Stored!.Session.Should().BeNull();
        rig.Stored!.ApiKey.Should().Be(TestApiKeys.Viewer, "a failed sign-in leaves the saved key alone");
    }

    [TestMethod]
    [DataRow("login", "--name", "olive")]
    [DataRow("login")]
    public async Task Login_TakesTheNameAsAnArgument_NotAnOption(params string[] args)
    {
        using var rig = new CliRig();
        rig.Input.Enqueue(TestSecrets.Password);

        var result = await rig.RunAsync(args);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("Run 'hvo-roof login --help' for usage.");
        rig.Prompts.Should().BeEmpty("nothing is asked for a command line that does not parse");
    }

    [TestMethod]
    public async Task Login_WithNoPasswordGiven_IsAUsageError()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);

        var result = await rig.RunAsync("login", Person);

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Contain("No value was given for: Password for olive.");
        rig.Stored!.Session.Should().BeNull();
    }

    [TestMethod]
    [DataRow("HVO_ROOF_API_KEY")]
    [DataRow("HVO_ROOF_SESSION")]
    public async Task Login_WithACredentialInTheEnvironment_SaysTheEnvironmentWins(string variable)
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        rig.Environment[variable] = "test-environment-credential-not-real-07";
        await RoofClientApiTests.AddUserAsync(host, Person, RoofControllerApiContract.OperatorRole);
        rig.Input.Enqueue(TestSecrets.Password);

        var result = await rig.RunAsync("login", Person);

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Error.Should().Be(
            "Note: HVO_ROOF_API_KEY or HVO_ROOF_SESSION is set, and commands use it instead of the saved credential." + Environment.NewLine);
        rig.Stored!.Session.Should().NotBeNull("the session is saved all the same");
    }

    // ---- Logout ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Logout_EndsTheSavedSession_AndKeepsTheSavedKey()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);
        await LoginAsync(host, rig);
        var saved = rig.Stored!;

        var result = await rig.RunAsync("logout");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be($"Signed out. The session was removed from {rig.CredentialsPath}.{Environment.NewLine}");
        rig.Stored!.Session.Should().BeNull();
        rig.Stored!.ApiKey.Should().Be(TestApiKeys.Viewer);
        using var client = CreateClient(host, (saved with { ApiKey = null }).ToCredential());
        (await RoofClientApiTests.RefusedAsync(() => client.Auth.GetCallerAsync())).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "the controller ended the session");
    }

    [TestMethod]
    public async Task Logout_Json_WritesTheResult()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        await LoginAsync(host, rig);

        var result = await rig.RunAsync("logout", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("signedOut").GetBoolean().Should().BeTrue();
        result.Json.GetProperty("message").GetString().Should().Be("Signed out.");
    }

    [TestMethod]
    public async Task Logout_WithNoSession_SaysSo()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("logout");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be("No session is saved." + Environment.NewLine);
        rig.Stored!.ApiKey.Should().Be(TestApiKeys.Viewer);
    }

    [TestMethod]
    public async Task Logout_Json_WithNoSession_WritesJson()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Viewer);

        var result = await rig.RunAsync("logout", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("signedOut").GetBoolean().Should().BeFalse(result.ToString());
    }

    [TestMethod]
    public async Task Logout_AfterTheSessionEnded_SaysItHadAlreadyEnded_AndRemovesIt()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        await LoginAsync(host, rig);
        using (var admin = CreateClient(host, new RoofApiKeyCredential(TestApiKeys.Admin)))
        {
            await admin.Identity.EndSessionAsync(rig.Stored!.Session!.SessionId!);
        }

        var result = await rig.RunAsync("logout");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be($"The session had already ended. The session was removed from {rig.CredentialsPath}.{Environment.NewLine}");
        rig.Stored!.Session.Should().BeNull();
    }

    // ---- Passwd ----------------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Passwd_Interactive_AsksForTheNewPasswordTwice_AndChangesIt()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        await LoginAsync(host, rig);
        rig.Interactive = true;
        rig.Input.Enqueue(TestSecrets.Password);
        rig.Input.Enqueue(TestSecrets.OtherPassword);
        rig.Input.Enqueue(TestSecrets.OtherPassword);

        var result = await rig.RunAsync("passwd");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Out.Should().Be("Password changed. Your other sessions were ended." + Environment.NewLine);
        rig.Prompts.Should().Equal([("Current password: ", true), ("New password: ", true), ("Again: ", true)]);
        rig.Input.Enqueue(TestSecrets.OtherPassword);
        var again = await rig.RunAsync("login", Person);
        again.Code.Should().Be(RoofExitCode.Success, "the new password signs in: " + again);
    }

    [TestMethod]
    public async Task Passwd_WhenTheTwoEntriesDiffer_ChangesNothing()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        await LoginAsync(host, rig);
        rig.Interactive = true;
        rig.Input.Enqueue(TestSecrets.Password);
        rig.Input.Enqueue(TestSecrets.OtherPassword);
        rig.Input.Enqueue("test-password-not-real-03");

        var result = await rig.RunAsync("passwd");

        result.Code.Should().Be(RoofExitCode.Usage, result.ToString());
        result.Error.Should().Be("The two entries do not match." + Environment.NewLine);
        rig.Input.Enqueue(TestSecrets.Password);
        var again = await rig.RunAsync("login", Person);
        again.Code.Should().Be(RoofExitCode.Success, "the old password still signs in: " + again);
    }

    [TestMethod]
    public async Task Passwd_FromRedirectedInput_ReadsEachSecretOnce_Json()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        await LoginAsync(host, rig);
        rig.Input.Enqueue(TestSecrets.Password);
        rig.Input.Enqueue(TestSecrets.OtherPassword);

        var result = await rig.RunAsync("passwd", "--json");

        result.Code.Should().Be(RoofExitCode.Success, result.ToString());
        result.Json.GetProperty("changed").GetBoolean().Should().BeTrue();
        rig.Prompts.Should().Equal([("Current password: ", true), ("New password: ", true)]);
    }

    [TestMethod]
    public async Task Passwd_WithAWrongCurrentPassword_IsRefused_AndKeepsTheSession()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(null);
        await LoginAsync(host, rig);
        rig.Input.Enqueue(TestSecrets.OtherPassword);
        rig.Input.Enqueue("test-password-not-real-03");

        var result = await rig.RunAsync("passwd");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Be("The current password is not correct. The password was not changed." + Environment.NewLine);
        var whoami = await rig.RunAsync("whoami");
        whoami.Code.Should().Be(RoofExitCode.Success, "a wrong current password does not end the session: " + whoami);
    }

    [TestMethod]
    public async Task Passwd_WithAnApiKey_IsRefused()
    {
        using var host = RoofClientApiTests.CreateHost();
        using var rig = new CliRig(host);
        rig.UseApiKey(TestApiKeys.Operator);
        rig.Input.Enqueue(TestSecrets.Password);
        rig.Input.Enqueue(TestSecrets.OtherPassword);

        var result = await rig.RunAsync("passwd");

        result.Code.Should().Be(RoofExitCode.Refused, result.ToString());
        result.Error.Should().Contain("[InvalidRequest]")
            .And.Contain("Only a signed-in person can change their password; this request used an API key.");
    }
}

/// <summary>
/// A command running in the background, as a person at a terminal runs it: its output can be read while it runs, and
/// <see cref="InterruptAsync"/> is Ctrl+C.
/// </summary>
file sealed class RunningCommand : IAsyncDisposable
{
    private readonly LockedWriter _out = new();
    private readonly LockedWriter _error = new();
    private readonly CancellationTokenSource _interrupt = new();
    private readonly Task<int> _run;

    public RunningCommand(Func<TextWriter, TextWriter, RoofCliHost> createHost, params string[] args)
    {
        var host = createHost(_out, _error);
        _run = Task.Run(() => RoofCli.RunAsync(args, host, handleTermination: false, _interrupt.Token));
    }

    /// <summary>Runs <paramref name="args"/> with the rig's host and credentials file.</summary>
    public static RunningCommand Start(CliRig rig, params string[] args)
        => new(rig.CreateHost, [.. args, "--credentials-file", rig.CredentialsPath]);

    public string Out => _out.Text;

    public string Error => _error.Text;

    public Task WaitForOutAsync(string text, string what)
        => ClientTestSupport.WaitUntilAsync(() => Out.Contains(text, StringComparison.Ordinal) || _run.IsCompleted, what)
            .ContinueWith(
                wait =>
                {
                    wait.GetAwaiter().GetResult();
                    if (!Out.Contains(text, StringComparison.Ordinal))
                    {
                        throw new AssertFailedException($"The command ended before {what}.\n--- out ---\n{Out}\n--- error ---\n{Error}");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

    /// <summary>Ctrl+C: cancels the command and returns what it did.</summary>
    public async Task<CliResult> InterruptAsync()
    {
        await _interrupt.CancelAsync();
        return await EndAsync();
    }

    /// <summary>Waits for the command to end by itself.</summary>
    public async Task<CliResult> EndAsync()
    {
        var code = await _run.WaitAsync(TimeSpan.FromSeconds(30));
        return new CliResult(code, Out, Error);
    }

    public async ValueTask DisposeAsync()
    {
        await _interrupt.CancelAsync();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            // The test has already failed; the command is abandoned.
        }

        _interrupt.Dispose();
        _out.Dispose();
        _error.Dispose();
    }
}

/// <summary>
/// Delivers every request, but for the one whose path ends in <paramref name="path"/> the answer never arrives: the
/// request waits until it is cancelled.
/// </summary>
file sealed class AnswerLostHandler(HttpMessageHandler inner, string path, TaskCompletionSource delivered) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (request.RequestUri?.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase) != true)
        {
            return response;
        }

        response.Dispose();
        delivered.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Unreachable.");
    }
}

/// <summary>A writer that the command and the test can use from different threads.</summary>
file sealed class LockedWriter : TextWriter
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _text = new();

    public override Encoding Encoding => Encoding.UTF8;

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    public override void Write(char value)
    {
        lock (_gate)
        {
            _text.Append(value);
        }
    }

    public override void Write(string? value)
    {
        lock (_gate)
        {
            _text.Append(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
        {
            _text.Append(buffer, index, count);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            _text.Append(value).Append(CoreNewLine);
        }
    }
}

/// <summary>Sends each request to the controller except the lease renewal, which cannot reach it.</summary>
file sealed class LeaseUnreachableHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => request.RequestUri!.AbsolutePath.EndsWith("/RoofControl/Lease", StringComparison.OrdinalIgnoreCase)
            ? throw new HttpRequestException("Connection refused (test).")
            : base.SendAsync(request, cancellationToken);
}
