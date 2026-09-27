using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using HVO.Core.Results;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.RPi.Security;
using HVO.RoofControllerV4.RPi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.RoofControllerV4.RPi.Components.Pages;

/// <summary>
/// Base class providing roof control logic, status handling and UI helpers for the operator console.
/// Status comes from coherent controller snapshots; commands run off the circuit thread so Stop is never queued
/// behind a slow command; every command re-checks the caller's role.
/// </summary>
public class RoofControlBase : ComponentBase, IDisposable
{
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(10);

    private static readonly RoofStatusResponse EmptySnapshot = new(
        RoofControllerStatus.Unknown,
        IsMoving: false,
        RoofControllerStopReason.None,
        LastTransitionUtc: null,
        IsWatchdogActive: false,
        WatchdogSecondsRemaining: null,
        IsAtSpeed: false,
        IsUsingPhysicalHardware: false,
        IsIgnoringPhysicalLimitSwitches: false);

    #region Dependency Injection

    [Inject] protected IRoofControllerServiceV4 RoofController { get; set; } = default!;
    [Inject] protected ILogger<RoofControlBase> Logger { get; set; } = default!;
    [Inject] protected IOptions<RoofControllerOptionsV4> RoofControllerOptions { get; set; } = default!;
    [Inject] protected FooterStatusService? FooterStatusService { get; set; }
    [Inject] protected HealthCheckService HealthCheckService { get; set; } = default!;
    [Inject] protected IAuthorizationService AuthorizationService { get; set; } = default!;
    [Inject] protected IServiceProvider ServiceProvider { get; set; } = default!;

    [CascadingParameter] protected Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    #endregion

    #region Private Fields

    protected readonly List<NotificationMessage> _notifications = new();
    protected bool _isDisposed;

    private RoofStatusResponse? _appliedStatus;
    private bool _authorizationResolved;
    private Task<AuthenticationState>? _resolvedAuthenticationStateTask;
    private bool _commandInFlight;
    private bool _clearFaultInFlight;
    private int _stopSequence;
    private int _healthFetchSequence;
    private bool _leaseOwned;
    private bool _leaseRenewalInFlight;
    private DateTimeOffset? _leaseRenewalDueUtc;
    private Timer? _leaseTimer;

    #endregion

    #region Authorization

    protected ClaimsPrincipal CurrentUser { get; private set; } = new(new ClaimsIdentity());
    protected bool IsAuthenticated => CurrentUser.Identity?.IsAuthenticated == true;
    protected bool CanOperate { get; private set; }
    protected bool IsAdmin { get; private set; }
    protected string UserDisplayName => string.IsNullOrWhiteSpace(CurrentUser.Identity?.Name) ? "Signed in" : CurrentUser.Identity!.Name!;
    protected string RoleLabel => IsAdmin ? "Admin" : CanOperate ? "Operator" : IsAuthenticated ? "Viewer" : "Signed out";

    protected async Task<ClaimsPrincipal> GetUserAsync()
    {
        try
        {
            if (AuthenticationStateTask is not null)
            {
                return (await AuthenticationStateTask).User;
            }

            var provider = ServiceProvider.GetService<AuthenticationStateProvider>();
            if (provider is not null)
            {
                return (await provider.GetAuthenticationStateAsync()).User;
            }
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogWarning(ex, "Authentication state is not available; treating the user as signed out");
        }

        return new ClaimsPrincipal(new ClaimsIdentity());
    }

    protected async Task RefreshAuthorizationAsync()
    {
        var user = await GetUserAsync();
        CurrentUser = user;
        CanOperate = await IsAuthorizedAsync(user, RoofControllerSecurityDefaults.OperatorPolicy);
        IsAdmin = await IsAuthorizedAsync(user, RoofControllerSecurityDefaults.AdminPolicy);
    }

    private async Task<bool> IsAuthorizedAsync(ClaimsPrincipal user, string policy)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return await TryAuthorizeAsync(user, policy);
    }

    private async Task<bool> CanSendStopAsync(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        return await TryAuthorizeAsync(user, RoofControllerSecurityDefaults.StopPolicy);
    }

    private async Task<bool> TryAuthorizeAsync(ClaimsPrincipal user, string policy)
    {
        try
        {
            var result = await AuthorizationService.AuthorizeAsync(user, null, policy);
            return result.Succeeded;
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogError(ex, "Authorization policy {Policy} is not registered; denying", policy);
            return false;
        }
    }

    #endregion

    #region Status

    protected RoofStatusResponse Snapshot => _appliedStatus ?? EmptySnapshot;

    public RoofControllerStatus CurrentStatus => Snapshot.Status;
    public bool IsMoving => Snapshot.IsMoving;
    public bool IsInitialized => Snapshot.IsInitialized || RoofController.IsInitialized;
    public bool IsServiceDisposed => RoofController.IsServiceDisposed;
    public bool IsShuttingDown => Snapshot.IsShuttingDown || RoofController.IsShuttingDown;
    public bool IsServiceAvailable => IsInitialized && !IsServiceDisposed && !IsShuttingDown;
    public bool IsUsingPhysicalHardware => Snapshot.IsUsingPhysicalHardware;
    public bool IsIgnoringLimitSwitches => Snapshot.IsIgnoringPhysicalLimitSwitches;
    public bool IsFaultLatched => Snapshot.IsFaultLatched;
    public bool HasFault => Snapshot.IsFaultLatched || Snapshot.Status == RoofControllerStatus.Error || Snapshot.IsDriveFaultActive == true;
    public bool IsRelayRegisterUnverified => Snapshot.RelayRegisterState == RoofRelayRegisterState.Unverified;

    /// <summary>Only reported by controllers that publish input health; older snapshots leave the timestamp unset.</summary>
    public bool AreInputsUnhealthy => Snapshot.SnapshotUtc != default && !Snapshot.InputsHealthy;

    public bool IsClearFaultInProgress => _clearFaultInFlight || Snapshot.IsClearFaultInProgress;
    public bool IsCommandInFlight => _commandInFlight;
    public RoofMotionDirection CommandedMotion => RoofConsoleRules.GetCommandedMotion(Snapshot);
    public string PositionLabel => RoofConsoleRules.DescribePosition(Snapshot.Status);
    public string ControllerName => string.IsNullOrWhiteSpace(Snapshot.ControllerName) ? "Observatory Roof Controller" : Snapshot.ControllerName!;
    public double? LeaseSecondsRemaining => Snapshot.LeaseSecondsRemaining;
    public bool IsSafetyWatchdogRunning => Snapshot.IsWatchdogActive;
    public double SafetyWatchdogTimeRemaining => Snapshot.WatchdogSecondsRemaining ?? 0;
    public double SafetyWatchdogTimeoutSeconds => RoofControllerOptions.Value.SafetyWatchdogTimeout.TotalSeconds;
    public DateTimeOffset? LastTransitionUtc => Snapshot.LastTransitionUtc;
    public RoofControllerStopReason LastStopReason => Snapshot.LastStopReason;
    public bool WasEmergencyStop => RoofConsoleRules.IsSafetyStopReason(LastStopReason);
    public bool IsInStopState => CurrentStatus is RoofControllerStatus.Stopped or RoofControllerStatus.PartiallyOpen or RoofControllerStatus.PartiallyClose;
    public IReadOnlyList<NotificationMessage> Notifications => _notifications.AsReadOnly();

    public string FaultDescription
    {
        get
        {
            if (Snapshot.IsFaultLatched)
            {
                return RoofConsoleRules.DescribeStopReason(Snapshot.LatchedFaultReason ?? Snapshot.LastStopReason);
            }

            if (Snapshot.IsDriveFaultActive == true)
            {
                return RoofConsoleRules.DescribeStopReason(RoofControllerStopReason.DriveFault);
            }

            return string.IsNullOrWhiteSpace(Snapshot.LastError) ? "The controller reported an error state." : Snapshot.LastError!;
        }
    }

    public string CommandedMotionLabel => CommandedMotion switch
    {
        RoofMotionDirection.Opening => "Opening",
        RoofMotionDirection.Closing => "Closing",
        _ => "None"
    };

    /// <summary>
    /// Applies <paramref name="candidate"/> unless it is older than the snapshot already shown. Returns true when the
    /// displayed state changed.
    /// </summary>
    protected bool ApplySnapshot(RoofStatusResponse candidate)
    {
        if (!RoofConsoleRules.ShouldApply(_appliedStatus, candidate))
        {
            return false;
        }

        var previous = _appliedStatus;
        _appliedStatus = candidate;

        var alert = RoofConsoleRules.DetectSafetyAlert(previous, candidate);
        if (alert is not null)
        {
            AddNotification(alert.Title, alert.Message, NotificationType.Error);
        }

        if (!candidate.IsMoving)
        {
            _leaseOwned = false;
        }

        UpdateLeaseTimer();
        UpdateFooterStatus();
        return true;
    }

    protected void RefreshSnapshotFromService()
    {
        try
        {
            var snapshot = RoofController.GetCurrentStatusSnapshot();
            if (snapshot is not null)
            {
                ApplySnapshot(snapshot);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Unable to read the roof controller status snapshot");
        }
    }

    #endregion

    #region Component Lifecycle

    protected override void OnInitialized()
    {
        RoofController.StatusChanged += OnServiceStatusChanged;
        RefreshSnapshotFromService();

        if (IsServiceDisposed)
        {
            AddNotification("Service", "Roof controller service is stopped", NotificationType.Warning);
        }
        else if (!IsInitialized)
        {
            AddNotification("Service", "Roof controller initializing…", NotificationType.Info);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_authorizationResolved && ReferenceEquals(_resolvedAuthenticationStateTask, AuthenticationStateTask))
        {
            return;
        }

        _authorizationResolved = true;
        _resolvedAuthenticationStateTask = AuthenticationStateTask;
        await RefreshAuthorizationAsync();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        RoofController.StatusChanged -= OnServiceStatusChanged;
        _leaseTimer?.Dispose();
        _leaseTimer = null;
        FooterStatusService?.Reset();
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Event Handling

    private void OnServiceStatusChanged(object? sender, RoofStatusChangedEventArgs e)
    {
        if (_isDisposed || e?.Status is null)
        {
            return;
        }

        _ = ApplyStatusFromEventAsync(e.Status);
    }

    private async Task ApplyStatusFromEventAsync(RoofStatusResponse status)
    {
        try
        {
            await InvokeAsync(() =>
            {
                if (_isDisposed)
                {
                    return;
                }

                if (ApplySnapshot(status))
                {
                    StateHasChanged();
                }
            });
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to apply a roof status update");
        }
    }

    #endregion

    #region Command Enablement

    /// <summary>Why Open is unavailable, or null when it may be sent.</summary>
    protected string? GetOpenBlockReason() => GetMotionBlockReason(RoofMotionDirection.Opening);

    /// <summary>Why Close is unavailable, or null when it may be sent.</summary>
    protected string? GetCloseBlockReason() => GetMotionBlockReason(RoofMotionDirection.Closing);

    public bool IsOpenDisabled => _commandInFlight || GetOpenBlockReason() is not null;
    public bool IsCloseDisabled => _commandInFlight || GetCloseBlockReason() is not null;
    public bool IsClearFaultDisabled => GetClearFaultBlockReason() is not null;

    protected string GetOpenTitle() => _commandInFlight ? "A command is in progress" : GetOpenBlockReason() ?? "Open the roof";
    protected string GetCloseTitle() => _commandInFlight ? "A command is in progress" : GetCloseBlockReason() ?? "Close the roof";
    protected string GetClearFaultTitle() => GetClearFaultBlockReason() ?? "Pulse the drive clear-fault relay";

    private string? GetMotionBlockReason(RoofMotionDirection direction)
    {
        if (!CanOperate)
        {
            return "The Operator role is required to open or close the roof";
        }

        var availability = GetAvailabilityBlockReason();
        if (availability is not null)
        {
            return availability;
        }

        if (IsClearFaultInProgress)
        {
            return "A clear-fault pulse is in progress";
        }

        if (HasFault)
        {
            return "A fault is active: clear it before moving the roof";
        }

        if (Snapshot.IsMoving)
        {
            return "The roof is moving: stop it first";
        }

        if (direction == RoofMotionDirection.Opening && CurrentStatus is RoofControllerStatus.Open or RoofControllerStatus.Opening)
        {
            return "The roof is already open";
        }

        if (direction == RoofMotionDirection.Closing && CurrentStatus is RoofControllerStatus.Closed or RoofControllerStatus.Closing)
        {
            return "The roof is already closed";
        }

        return null;
    }

    private string? GetClearFaultBlockReason(bool includeThisRequest = true)
    {
        if (!CanOperate)
        {
            return "The Operator role is required to clear a fault";
        }

        var availability = GetAvailabilityBlockReason();
        if (availability is not null)
        {
            return availability;
        }

        if (Snapshot.IsClearFaultInProgress || (includeThisRequest && _clearFaultInFlight))
        {
            return "A clear-fault pulse is in progress";
        }

        if (Snapshot.IsMoving)
        {
            return "The roof is moving: stop it first";
        }

        return HasFault ? null : "No fault is active";
    }

    private string? GetAvailabilityBlockReason()
    {
        if (IsServiceDisposed)
        {
            return "The roof controller service is stopped";
        }

        if (IsShuttingDown)
        {
            return "The roof controller is shutting down";
        }

        return IsInitialized ? null : "The roof controller is initializing";
    }

    #endregion

    #region Operations

    protected Task OpenRoofAsync() => RunMotionCommandAsync(RoofMotionDirection.Opening);

    protected Task CloseRoofAsync() => RunMotionCommandAsync(RoofMotionDirection.Closing);

    private async Task RunMotionCommandAsync(RoofMotionDirection direction)
    {
        if (_commandInFlight)
        {
            return;
        }

        _commandInFlight = true;
        var label = direction == RoofMotionDirection.Opening ? "Open" : "Close";
        try
        {
            await RefreshAuthorizationAsync();
            RefreshSnapshotFromService();

            var blockReason = GetMotionBlockReason(direction);
            if (blockReason is not null)
            {
                AddNotification($"{label} not sent", blockReason, NotificationType.Warning);
                return;
            }

            Result<RoofControllerStatus> result = await Task.Run(() =>
                direction == RoofMotionDirection.Opening ? RoofController.Open() : RoofController.Close());

            if (result.IsSuccessful)
            {
                _leaseOwned = true;
                AddNotification("Command", direction == RoofMotionDirection.Opening ? "Opening roof" : "Closing roof", NotificationType.Info);
            }
            else
            {
                Logger.LogWarning(result.Error, "{Command} command was refused", label);
                ApplyFailureSnapshot(result.Error);
                AddNotification($"{label} refused", RoofConsoleRules.DescribeFailure(result.Error), NotificationType.Error);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{Command} command failed", label);
            AddNotification($"{label} failed", RoofConsoleRules.DescribeFailure(ex), NotificationType.Error);
        }
        finally
        {
            _commandInFlight = false;
            RefreshSnapshotFromService();
        }
    }

    protected RoofStopOutcome StopOutcome { get; private set; }
    protected string? StopOutcomeMessage { get; private set; }
    protected DateTimeOffset? StopOutcomeTime { get; private set; }

    /// <summary>
    /// Sends Stop. Never gated on status, role or other commands in flight: any signed-in user may stop the roof, and
    /// the request runs off the circuit thread so a slow command cannot delay it.
    /// </summary>
    protected async Task StopRoofAsync()
    {
        var sequence = ++_stopSequence;
        SetStopOutcome(RoofStopOutcome.Sent, "Stop sent. Waiting for the controller…");

        var user = await GetUserAsync();
        if (!await CanSendStopAsync(user))
        {
            SetStopOutcome(RoofStopOutcome.Failed, "Stop was not sent because the session is signed out. Reload and sign in, or use the stop control at the roof.");
            return;
        }

        Result<RoofControllerStatus> result;
        RoofStatusResponse? snapshot;
        try
        {
            (result, snapshot) = await Task.Run(() =>
            {
                var stopResult = RoofController.Stop(RoofControllerStopReason.NormalStop);
                RoofStatusResponse? afterStop = null;
                try
                {
                    afterStop = RoofController.GetCurrentStatusSnapshot();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Unable to read status after Stop");
                }

                return (stopResult, afterStop);
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Stop command failed");
            if (sequence == _stopSequence)
            {
                SetStopOutcome(RoofStopOutcome.Failed, $"Stop failed: {RoofConsoleRules.DescribeFailure(ex)} Use the stop control at the roof.");
            }

            AddNotification("Stop failed", RoofConsoleRules.DescribeFailure(ex), NotificationType.Error);
            return;
        }

        if (snapshot is not null)
        {
            ApplySnapshot(snapshot);
        }

        if (!result.IsSuccessful)
        {
            Logger.LogWarning(result.Error, "Stop command reported a failure");
            ApplyFailureSnapshot(result.Error);
        }

        var (outcome, message) = RoofConsoleRules.ClassifyStop(result.IsSuccessful, result.Error, snapshot);
        if (sequence == _stopSequence)
        {
            SetStopOutcome(outcome, message);
        }

        AddNotification("Stop", message, outcome switch
        {
            RoofStopOutcome.Acknowledged => NotificationType.Success,
            RoofStopOutcome.RelayUnverified => NotificationType.Warning,
            _ => NotificationType.Error
        });
    }

    private void SetStopOutcome(RoofStopOutcome outcome, string message)
    {
        StopOutcome = outcome;
        StopOutcomeMessage = message;
        StopOutcomeTime = DateTimeOffset.Now;
    }

    protected async Task ClearFaultAsync()
    {
        if (_clearFaultInFlight)
        {
            return;
        }

        _clearFaultInFlight = true;
        try
        {
            await RefreshAuthorizationAsync();
            RefreshSnapshotFromService();

            var blockReason = GetClearFaultBlockReason(includeThisRequest: false);
            if (blockReason is not null)
            {
                AddNotification("Clear fault not sent", blockReason, NotificationType.Warning);
                return;
            }

            var result = await Task.Run(() => RoofController.ClearFault());
            RefreshSnapshotFromService();
            if (!result.IsSuccessful)
            {
                Logger.LogWarning(result.Error, "Clear fault was refused");
                ApplyFailureSnapshot(result.Error);
                AddNotification("Clear fault refused", RoofConsoleRules.DescribeFailure(result.Error), NotificationType.Error);
            }
            else if (!result.Value)
            {
                AddNotification("Clear fault", "The clear-fault pulse did not complete", NotificationType.Warning);
            }
            else if (IsFaultLatched)
            {
                AddNotification("Fault still latched", $"Clear-fault pulse sent, but the fault is still latched: {FaultDescription}", NotificationType.Warning);
            }
            else
            {
                AddNotification("Clear fault", "Clear-fault pulse sent", NotificationType.Success);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error clearing fault");
            AddNotification("Clear fault failed", RoofConsoleRules.DescribeFailure(ex), NotificationType.Error);
        }
        finally
        {
            _clearFaultInFlight = false;
        }
    }

    private void ApplyFailureSnapshot(Exception? error)
    {
        if (error is RoofControllerException { Snapshot: { } snapshot })
        {
            ApplySnapshot(snapshot);
        }
    }

    protected void AddNotification(string title, string message, NotificationType type)
    {
        _notifications.Insert(0, new NotificationMessage
        {
            Title = title,
            Message = message,
            Type = type,
            Timestamp = DateTime.Now
        });

        if (_notifications.Count > 5)
        {
            _notifications.RemoveAt(_notifications.Count - 1);
        }

        UpdateFooterStatus();
    }

    #endregion

    #region Operator Lease

    /// <summary>
    /// Keeps the operator lease alive for motion this console started, renewing at a third of the remaining time.
    /// Motion started elsewhere is not renewed here, so closing this page lets the lease lapse and stop the roof.
    /// </summary>
    private void UpdateLeaseTimer()
    {
        if (_isDisposed || !_leaseOwned || !Snapshot.IsMoving
            || RoofConsoleRules.GetLeaseRenewalDelay(Snapshot.LeaseSecondsRemaining) is not { } delay)
        {
            CancelLeaseRenewal();
            return;
        }

        var due = DateTimeOffset.UtcNow + delay;
        if (_leaseRenewalDueUtc is { } scheduled && scheduled <= due)
        {
            return;
        }

        _leaseRenewalDueUtc = due;
        _leaseTimer ??= new Timer(OnLeaseTimerElapsed);
        _leaseTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void CancelLeaseRenewal()
    {
        _leaseRenewalDueUtc = null;
        _leaseTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnLeaseTimerElapsed(object? state)
    {
        if (_isDisposed)
        {
            return;
        }

        _ = RenewLeaseFromTimerAsync();
    }

    private async Task RenewLeaseFromTimerAsync()
    {
        try
        {
            await InvokeAsync(RenewLeaseOnRendererAsync);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Operator lease renewal failed");
        }
    }

    private async Task RenewLeaseOnRendererAsync()
    {
        _leaseRenewalDueUtc = null;
        if (_isDisposed || !_leaseOwned || _leaseRenewalInFlight)
        {
            return;
        }

        _leaseRenewalInFlight = true;
        try
        {
            await RefreshAuthorizationAsync();
            if (!CanOperate)
            {
                _leaseOwned = false;
                AddNotification("Lease", "Lease not renewed: the Operator role is no longer granted", NotificationType.Warning);
                return;
            }

            var result = await Task.Run(() => RoofController.RenewLease());
            if (_isDisposed)
            {
                return;
            }

            if (result.IsSuccessful)
            {
                if (result.Value is { } snapshot)
                {
                    ApplySnapshot(snapshot);
                }
            }
            else if (RoofConsoleRules.GetErrorCode(result.Error) == RoofControllerErrorCode.LeaseNotActive)
            {
                _leaseOwned = false;
            }
            else
            {
                Logger.LogWarning(result.Error, "Operator lease renewal was refused");
                AddNotification("Lease renewal failed", RoofConsoleRules.DescribeFailure(result.Error), NotificationType.Warning);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Operator lease renewal failed");
            AddNotification("Lease renewal failed", RoofConsoleRules.DescribeFailure(ex), NotificationType.Warning);
        }
        finally
        {
            _leaseRenewalInFlight = false;
            if (!_isDisposed)
            {
                UpdateLeaseTimer();
                StateHasChanged();
            }
        }
    }

    #endregion

    #region UI Helpers

    public string GetHardwareBadgeClass() => IsUsingPhysicalHardware ? "bg-primary" : "bg-warning text-dark";

    public string GetHardwareModeLabel() => IsUsingPhysicalHardware ? "Physical I²C" : "Simulation";

    public string GetLimitSwitchBadgeClass() => "bg-warning text-dark";

    public string GetLimitSwitchLabel() => "Limits Ignored";

    public string GetHealthCheckBadgeClass()
    {
        if (HasFault || IsServiceDisposed)
        {
            return "bg-danger";
        }

        return IsRelayRegisterUnverified || AreInputsUnhealthy || !IsInitialized ? "bg-warning text-dark" : "bg-success";
    }

    public string GetHealthCheckStatus()
    {
        if (IsServiceDisposed)
        {
            return "Service stopped";
        }

        if (HasFault)
        {
            return "Fault";
        }

        if (IsRelayRegisterUnverified || AreInputsUnhealthy)
        {
            return "Attention";
        }

        return IsInitialized ? "Healthy" : "Initializing…";
    }

    public string GetHealthStatusBadgeClass(string? status) => status?.ToLowerInvariant() switch
    {
        "healthy" => "bg-success",
        "degraded" => "bg-warning text-dark",
        "unhealthy" => "bg-danger",
        _ => "bg-secondary"
    };

    public string GetLimitLabel(bool? active) => active switch
    {
        true => "Active",
        false => "Clear",
        _ => "Unknown"
    };

    public string GetLimitChipClass(bool? active) => active switch
    {
        true => "rc2-chip rc2-chip--on",
        false => "rc2-chip",
        _ => "rc2-chip rc2-chip--unknown"
    };

    public string GetCommandedMotionIcon() => CommandedMotion switch
    {
        RoofMotionDirection.Opening => "bi-arrow-up-circle-fill",
        RoofMotionDirection.Closing => "bi-arrow-down-circle-fill",
        _ => "bi-pause-circle"
    };

    public string GetStopOutcomeClass() => StopOutcome switch
    {
        RoofStopOutcome.Sent => "rc2-stop-outcome rc2-stop-outcome--sent",
        RoofStopOutcome.Acknowledged => "rc2-stop-outcome rc2-stop-outcome--ok",
        RoofStopOutcome.RelayUnverified => "rc2-stop-outcome rc2-stop-outcome--warn",
        RoofStopOutcome.Failed => "rc2-stop-outcome rc2-stop-outcome--failed",
        _ => "rc2-stop-outcome"
    };

    public string GetStopOutcomeIcon() => StopOutcome switch
    {
        RoofStopOutcome.Sent => "bi-hourglass-split",
        RoofStopOutcome.Acknowledged => "bi-check-circle-fill",
        RoofStopOutcome.RelayUnverified => "bi-exclamation-triangle-fill",
        RoofStopOutcome.Failed => "bi-x-octagon-fill",
        _ => "bi-dash"
    };

    public string GetLeaseLabel() => LeaseSecondsRemaining is { } seconds
        ? $"Lease {Math.Max(0, Math.Ceiling(seconds)):0}s"
        : "No lease";

    public string GetLastStopTypeLabel()
    {
        if (!IsInStopState || LastStopReason == RoofControllerStopReason.None)
        {
            return string.Empty;
        }

        return WasEmergencyStop ? "Safety" : "Normal";
    }

    public string GetLastStopTooltip() => RoofConsoleRules.DescribeStopReason(LastStopReason);

    protected IEnumerable<HealthCheckEntry> GetOrderedHealthChecks()
    {
        if (HealthReport?.Checks is null)
        {
            return Enumerable.Empty<HealthCheckEntry>();
        }

        return HealthReport.Checks
            .OrderBy(c => NormalizeStatusRank(c.Status))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string GetLastTransitionFriendly()
    {
        if (LastTransitionUtc is null) return "—";
        var local = LastTransitionUtc.Value.ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public string GetLastTransitionTooltip()
    {
        if (LastTransitionUtc is null) return "Timestamp of the last status change";
        return $"UTC: {LastTransitionUtc:yyyy-MM-dd HH:mm:ss}Z";
    }

    #endregion

    #region Health Dialog

    protected bool IsHealthDialogOpen { get; private set; }
    protected bool IsHealthDialogLoading { get; private set; }
    protected string? HealthDialogError { get; private set; }
    protected HealthReportPayload? HealthReport { get; private set; }

    protected Task OpenHealthDialogAsync()
    {
        IsHealthDialogOpen = true;
        return FetchHealthReportAsync();
    }

    protected Task RefreshHealthDialogAsync()
    {
        IsHealthDialogOpen = true;
        return FetchHealthReportAsync();
    }

    protected void CloseHealthDialog()
    {
        _healthFetchSequence++;
        IsHealthDialogOpen = false;
        IsHealthDialogLoading = false;
        HealthDialogError = null;
        HealthReport = null;
    }

    protected bool HasHealthData(JsonElement? data)
    {
        if (data is null)
        {
            return false;
        }

        return data.Value.ValueKind switch
        {
            JsonValueKind.Object => data.Value.EnumerateObject().Any(),
            JsonValueKind.Array => data.Value.GetArrayLength() > 0,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(data.Value.GetString()),
            JsonValueKind.Number => true,
            JsonValueKind.True or JsonValueKind.False => true,
            _ => false
        };
    }

    protected string? FormatHealthData(JsonElement? data)
    {
        if (!HasHealthData(data))
        {
            return null;
        }

        return JsonSerializer.Serialize(data!.Value, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Runs the registered health checks in-process. The previous report is cleared first so a failed refresh never
    /// leaves stale results on screen.
    /// </summary>
    private async Task FetchHealthReportAsync()
    {
        var sequence = ++_healthFetchSequence;
        HealthReport = null;
        HealthDialogError = null;
        IsHealthDialogLoading = true;
        StateHasChanged();

        try
        {
            await RefreshAuthorizationAsync();
            using var cancellation = new CancellationTokenSource(HealthCheckTimeout);
            var report = await Task.Run(() => HealthCheckService.CheckHealthAsync(cancellation.Token), cancellation.Token)
                .WaitAsync(cancellation.Token);

            if (sequence == _healthFetchSequence && !_isDisposed)
            {
                HealthReport = RoofConsoleRules.ToPayload(report, IsAdmin);
            }
        }
        catch (OperationCanceledException ex)
        {
            Logger.LogWarning(ex, "Timed out running health checks");
            if (sequence == _healthFetchSequence)
            {
                HealthDialogError = "Timed out running the health checks. Please try again.";
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to run health checks");
            if (sequence == _healthFetchSequence)
            {
                HealthDialogError = "Unable to run the health checks. Check the server log for details.";
            }
        }
        finally
        {
            if (sequence == _healthFetchSequence)
            {
                IsHealthDialogLoading = false;
            }
        }
    }

    private static int NormalizeStatusRank(string? status) => status?.ToLowerInvariant() switch
    {
        "unhealthy" => 0,
        "degraded" => 1,
        "healthy" => 2,
        _ => 3
    };

    #endregion

    #region Supporting Types

    public class NotificationMessage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public NotificationType Type { get; set; } = NotificationType.Info;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error
    }

    #endregion

    #region Footer Synchronization

    private void UpdateFooterStatus()
    {
        if (FooterStatusService is null || _isDisposed)
        {
            return;
        }

        FooterStatusService.SetLeftNotifications(_notifications
            .Select(n => new FooterNotification(n.Title, n.Message, MapLevel(n.Type), n.Timestamp))
            .ToArray());

        FooterStatusService.SetCenterMessage(new FooterStatusMessage(BuildFooterCenterText(), GetFooterCenterLevel()));

        var right = new List<string>();
        if (LeaseSecondsRemaining is not null)
        {
            right.Add(GetLeaseLabel());
        }

        right.Add(IsSafetyWatchdogRunning
            ? $"Watchdog: {Math.Ceiling(SafetyWatchdogTimeRemaining)}s remaining"
            : $"Watchdog: standby ({SafetyWatchdogTimeoutSeconds}s)");
        FooterStatusService.SetRightMessage(new FooterStatusMessage(
            string.Join(" • ", right),
            IsSafetyWatchdogRunning ? FooterStatusLevel.Warning : FooterStatusLevel.Info));
    }

    private string BuildFooterCenterText()
    {
        var parts = new List<string> { $"Roof: {PositionLabel}" };
        if (CommandedMotion != RoofMotionDirection.None && !string.Equals(CommandedMotionLabel, PositionLabel, StringComparison.Ordinal))
        {
            parts[0] += $" → {CommandedMotionLabel}";
        }

        if (HasFault) parts.Add(IsFaultLatched ? "Fault latched" : "Fault");
        if (IsRelayRegisterUnverified) parts.Add("Relay unverified");
        if (AreInputsUnhealthy) parts.Add("Inputs unhealthy");
        if (IsIgnoringLimitSwitches) parts.Add("Limits ignored");
        if (!IsUsingPhysicalHardware) parts.Add("Simulation");
        return string.Join(" • ", parts);
    }

    private FooterStatusLevel GetFooterCenterLevel()
    {
        if (HasFault)
        {
            return FooterStatusLevel.Error;
        }

        if (IsRelayRegisterUnverified || AreInputsUnhealthy || IsMoving)
        {
            return FooterStatusLevel.Warning;
        }

        return CurrentStatus == RoofControllerStatus.Open ? FooterStatusLevel.Success : FooterStatusLevel.Info;
    }

    private static FooterStatusLevel MapLevel(NotificationType type) => type switch
    {
        NotificationType.Error => FooterStatusLevel.Error,
        NotificationType.Warning => FooterStatusLevel.Warning,
        NotificationType.Success => FooterStatusLevel.Success,
        _ => FooterStatusLevel.Info
    };

    #endregion
}
