using System;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Components;
using HVO.RoofControllerV4.RPi.Components.ConsoleLog;
using HVO.RoofControllerV4.RPi.Components.Pages;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.RoofControllerV4.RPi.Tests.Components;

/// <summary>
/// bUnit host for the operator console: a mocked controller whose snapshot the test controls, a mocked health-check
/// service, a circuit monitor the test can drop and restore, stubbed camera and log viewer, and test authorization with
/// the console's policies.
/// </summary>
internal sealed class RoofConsoleHarness : IAsyncDisposable
{
    private RoofStatusResponse _current;

    public RoofConsoleHarness(RoofStatusResponse initial)
    {
        _current = initial;

        Context = new BunitContext();
        Context.JSInterop.Mode = JSRuntimeMode.Loose;

        Roof = new Mock<IRoofControllerServiceV4>();
        Roof.SetupGet(r => r.IsInitialized).Returns(true);
        Roof.Setup(r => r.GetCurrentStatusSnapshot()).Returns(() => Current);

        Health = new Mock<HealthCheckService>();
        Footer = new FooterStatusService();
        Circuit = new ConsoleCircuitMonitor();

        Context.Services.AddLogging();
        Context.Services.AddSingleton(Roof.Object);
        Context.Services.AddSingleton(Health.Object);
        Context.Services.AddSingleton(Footer);
        Context.Services.AddSingleton(Circuit);
        Context.Services.AddSingleton(Options.Create(new RoofControllerOptionsV4()));

        Context.ComponentFactories.AddStub<CameraStream>();
        Context.ComponentFactories.AddStub<ConsoleLogViewer>();

        Authorization = Context.AddAuthorization();
    }

    public BunitContext Context { get; }

    public Mock<IRoofControllerServiceV4> Roof { get; }

    public Mock<HealthCheckService> Health { get; }

    public FooterStatusService Footer { get; }

    /// <summary>The console's circuit monitor: <c>SetConnected(false)</c> simulates the browser connection dropping.</summary>
    public ConsoleCircuitMonitor Circuit { get; }

    public BunitAuthorizationContext Authorization { get; }

    /// <summary>The snapshot the mocked controller returns from <c>GetCurrentStatusSnapshot()</c>.</summary>
    public RoofStatusResponse Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }

    public RoofConsoleHarness SignInAsViewer() => SignIn("viewer", RoofControllerApiContract.ViewerRole,
        RoofControllerSecurityDefaults.ViewerPolicy, RoofControllerSecurityDefaults.StopPolicy);

    public RoofConsoleHarness SignInAsOperator() => SignIn("operator", RoofControllerApiContract.OperatorRole,
        RoofControllerSecurityDefaults.ViewerPolicy, RoofControllerSecurityDefaults.StopPolicy, RoofControllerSecurityDefaults.OperatorPolicy);

    public RoofConsoleHarness SignInAsAdmin() => SignIn("admin", RoofControllerApiContract.AdminRole,
        RoofControllerSecurityDefaults.ViewerPolicy, RoofControllerSecurityDefaults.StopPolicy, RoofControllerSecurityDefaults.OperatorPolicy,
        RoofControllerSecurityDefaults.AdminPolicy);

    public IRenderedComponent<RoofControlV2> Render() => Context.Render<RoofControlV2>();

    /// <summary>Raises <c>StatusChanged</c> on the calling thread, as the controller does from its own threads.</summary>
    public void RaiseStatus(RoofStatusResponse status, bool updateCurrent = true)
    {
        if (updateCurrent)
        {
            Current = status;
        }

        Roof.Raise(r => r.StatusChanged += null, Roof.Object, new RoofStatusChangedEventArgs(status));
    }

    public static RoofStatusResponse Status(RoofControllerStatus status, long version)
    {
        var moving = status is RoofControllerStatus.Opening or RoofControllerStatus.Closing;
        return new RoofStatusResponse(
            status,
            IsMoving: moving,
            RoofControllerStopReason.None,
            LastTransitionUtc: DateTimeOffset.UtcNow,
            IsWatchdogActive: moving,
            WatchdogSecondsRemaining: moving ? 60 : null,
            IsAtSpeed: moving,
            IsUsingPhysicalHardware: true,
            IsIgnoringPhysicalLimitSwitches: false)
        {
            StatusVersion = version,
            SnapshotUtc = DateTimeOffset.UtcNow,
            CommandedMotion = status switch
            {
                RoofControllerStatus.Opening => RoofMotionDirection.Opening,
                RoofControllerStatus.Closing => RoofMotionDirection.Closing,
                _ => RoofMotionDirection.None
            },
            RelayRegisterState = RoofRelayRegisterState.Verified,
            InputsHealthy = true,
            IsInitialized = true,
            ControllerName = "Test Roof"
        };
    }

    public ValueTask DisposeAsync() => Context.DisposeAsync();

    private RoofConsoleHarness SignIn(string userName, string role, params string[] policies)
    {
        Authorization.SetAuthorized(userName);
        Authorization.SetRoles(role);
        Authorization.SetPolicies(policies);
        return this;
    }
}
