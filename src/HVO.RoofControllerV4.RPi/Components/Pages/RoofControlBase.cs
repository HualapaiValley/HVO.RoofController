using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using HVO.RoofControllerV4.RPi.Services;
using HVO.Core.Results;
using System.Collections.Generic;
using System.Timers;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace HVO.RoofControllerV4.RPi.Components.Pages;

/// <summary>
/// Base class providing all roof control logic, status handling, and UI helpers.
/// The modern UI (RoofControlV2) inherits this to render the experience.
/// </summary>
public class RoofControlBase : ComponentBase, IDisposable
{
    #region Dependency Injection

    [Inject] protected IRoofControllerServiceV4 RoofController { get; set; } = default!;
    [Inject] protected ILogger<RoofControlBase> Logger { get; set; } = default!;
    [Inject] protected IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] protected IOptions<RoofControllerOptionsV4> RoofControllerOptions { get; set; } = default!;
    [Inject] protected FooterStatusService? FooterStatusService { get; set; }
    [Inject] protected IHttpClientFactory HttpClientFactory { get; set; } = default!;
    [Inject] protected NavigationManager NavigationManager { get; set; } = default!;

    #endregion

    #region Private Fields

    // Removed polling timer; updates are push-based via service events
    protected readonly List<NotificationMessage> _notifications = new();
    protected bool _isDisposed = false;
    protected RoofControllerStopReason _lastNotifiedStopReason = RoofControllerStopReason.None;
    private bool _footerStatusReady;
    private HttpClient? _healthHttpClient;

    #endregion

    #region Public Properties

    public RoofControllerStatus CurrentStatus => RoofController.Status;
    public bool IsInitialized => RoofController.IsInitialized;
    public bool IsMoving => RoofController.IsMoving;
    public bool HasFault => RoofController.Status == RoofControllerStatus.Error;
    public bool IsRoofOpen => RoofController.Status == RoofControllerStatus.Open;
    public bool IsServiceDisposed => RoofController.IsServiceDisposed;
    public bool IsServiceAvailable => RoofController.IsInitialized && !RoofController.IsServiceDisposed;
    public bool IsUsingPhysicalHardware => RoofController.IsUsingPhysicalHardware;
    public bool IsIgnoringLimitSwitches => RoofController.IsIgnoringPhysicalLimitSwitches;
    protected bool IsHealthDialogOpen { get; private set; }
    protected bool IsHealthDialogLoading { get; private set; }
    protected string? HealthDialogError { get; private set; }
    protected HealthReportPayload? HealthReport { get; private set; }

    public bool IsOpenDisabled 
    { 
        get 
        {
            var baseDisabled = !IsServiceAvailable || RoofController.IsMoving || 
                               RoofController.Status == RoofControllerStatus.Opening || 
                               RoofController.Status == RoofControllerStatus.Open ||
                               RoofController.Status == RoofControllerStatus.Error;
            return baseDisabled;
        }
    }

    public bool IsCloseDisabled 
    { 
        get 
        {
            var baseDisabled = !IsServiceAvailable || RoofController.IsMoving || 
                               RoofController.Status == RoofControllerStatus.Closing || 
                               RoofController.Status == RoofControllerStatus.Closed ||
                               RoofController.Status == RoofControllerStatus.Error;
            return baseDisabled;
        }
    }

    public bool IsStopDisabled => !IsServiceAvailable || !RoofController.IsMoving;
    public bool IsClearFaultDisabled => !IsServiceAvailable || RoofController.IsMoving || !HasFault;
    public IReadOnlyList<NotificationMessage> Notifications => _notifications.AsReadOnly();
    public bool IsSafetyWatchdogRunning => RoofController.IsWatchdogActive;
    public double SafetyWatchdogTimeRemaining => RoofController.WatchdogSecondsRemaining ?? 0;
    public double SafetyWatchdogTimeoutSeconds => RoofControllerOptions.Value.SafetyWatchdogTimeout.TotalSeconds;
    public DateTimeOffset? LastTransitionUtc => RoofController.LastTransitionUtc;
    public RoofControllerStopReason LastStopReason => RoofController.LastStopReason;
    public bool WasEmergencyStop => LastStopReason is RoofControllerStopReason.EmergencyStop or RoofControllerStopReason.SafetyWatchdogTimeout;
    public bool IsInStopState => CurrentStatus is RoofControllerStatus.Stopped or RoofControllerStatus.PartiallyOpen or RoofControllerStatus.PartiallyClose;
    public string GetLastStopTypeLabel()
    {
        if (!IsInStopState)
        {
            return string.Empty;
        }

        return LastStopReason switch
        {
            RoofControllerStopReason.None => "",
            RoofControllerStopReason.EmergencyStop => "Emergency",
            RoofControllerStopReason.SafetyWatchdogTimeout => "Emergency",
            _ => "Normal"
        };
    }

    public string GetLastStopTypeBadgeClass()
    {
        if (string.IsNullOrEmpty(GetLastStopTypeLabel())) return "d-none";
        return WasEmergencyStop ? "badge bg-danger text-white" : "badge bg-secondary";
    }

    #endregion

    #region Component Lifecycle

    protected override async Task OnInitializedAsync()
    {
        try
        {
            Logger.LogInformation("RoofControlBase initializing");
            await UpdateStatusAsync();
            RoofController.StatusChanged += OnServiceStatusChanged;

            AddNotification("UI", "Roof control UI loaded", NotificationType.Info);
            _lastNotifiedStopReason = RoofController.LastStopReason;

            if (IsServiceDisposed)
            {
                AddNotification("Service", "Roof controller disposed", NotificationType.Warning);
            }
            else if (!IsInitialized)
            {
                AddNotification("Service", "Roof controller initializing…", NotificationType.Info);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during initialization");
            AddNotification("Error", "Initialization error", NotificationType.Error);
        }
    }

    protected override void OnParametersSet()
    {
        if (!_footerStatusReady && FooterStatusService is not null)
        {
            _footerStatusReady = true;
            UpdateFooterStatus();
        }
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !_footerStatusReady && FooterStatusService is not null)
        {
            _footerStatusReady = true;
            UpdateFooterStatus();
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        RoofController.StatusChanged -= OnServiceStatusChanged;
        FooterStatusService?.Reset();
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Event Handling

    private async void OnServiceStatusChanged(object? sender, EventArgs e)
    {
        try
        {
            await UpdateStatusAsync();

            // Emergency notification on change
            if (RoofController.LastStopReason != _lastNotifiedStopReason &&
                (RoofController.LastStopReason == RoofControllerStopReason.EmergencyStop || RoofController.LastStopReason == RoofControllerStopReason.SafetyWatchdogTimeout))
            {
                AddNotification("Safety", "Emergency stop triggered", NotificationType.Error);
                _lastNotifiedStopReason = RoofController.LastStopReason;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in status changed handler");
        }
    }

    private async Task UpdateStatusAsync()
    {
        try
        {
            UpdateFooterStatus();
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
            // Component disposed - ignore
        }
    }

    #endregion

    #region UI Helpers

    public string GetStatusBadgeClass() => CurrentStatus switch
    {
        RoofControllerStatus.Open => "bg-success",
        RoofControllerStatus.Closed => "bg-secondary",
        RoofControllerStatus.Opening => "bg-info",
        RoofControllerStatus.Closing => "bg-info",
        RoofControllerStatus.Error => "bg-danger",
        _ => "bg-dark"
    };

    public string GetHardwareBadgeClass() => IsUsingPhysicalHardware ? "bg-primary" : "bg-warning text-dark";

    public string GetHardwareModeLabel() => IsUsingPhysicalHardware ? "Physical I²C" : "Simulation";

    public string GetLimitSwitchBadgeClass() => "bg-warning text-dark";

    public string GetLimitSwitchLabel() => "Limits Ignored";

    public string GetHealthCheckBadgeClass() => CurrentStatus == RoofControllerStatus.Error ? "bg-danger" : "bg-success";
    public string GetHealthStatusBadgeClass(string? status) => status?.ToLowerInvariant() switch
    {
        "healthy" => "bg-success",
        "degraded" => "bg-warning text-dark",
        "unhealthy" => "bg-danger",
        _ => "bg-secondary"
    };

    public string GetHealthCheckStatus()
    {
        if (IsServiceDisposed || CurrentStatus == RoofControllerStatus.Error)
            return "Error Detected";
        if (IsInitialized)
            return "Healthy";
        return "Checking...";
    }

    protected IEnumerable<HealthCheckEntry> GetOrderedHealthChecks()
    {
        if (HealthReport?.Checks is null)
        {
            return Enumerable.Empty<HealthCheckEntry>();
        }

        return HealthReport.Checks
            .OrderByDescending(c => NormalizeStatusRank(c.Status))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string GetWatchdogBadgeClass() => IsSafetyWatchdogRunning ? "bg-warning text-dark" : "bg-success";

    public string GetOpenButtonClass() => $"btn btn-success btn-lg control-btn{(IsOpenDisabled ? " disabled" : string.Empty)}";
    public string GetStopButtonClass() => $"btn btn-warning btn-lg control-btn{(IsStopDisabled ? " disabled" : string.Empty)}";
    public string GetCloseButtonClass() => $"btn btn-danger btn-lg control-btn{(IsCloseDisabled ? " disabled" : string.Empty)}";

    public string GetWatchdogProgressBarClass()
    {
        var percent = (SafetyWatchdogTimeoutSeconds - SafetyWatchdogTimeRemaining) / SafetyWatchdogTimeoutSeconds * 100;
        if (percent < 50) return "progress-bar bg-success";
        if (percent < 85) return "progress-bar bg-warning text-dark";
        return "progress-bar bg-danger";
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

    #region Operations

    public void OpenRoof()
    {
        if (!IsServiceAvailable) return;
        var result = RoofController.Open();
        if (result.IsSuccessful)
        {
            AddNotification("Command", "Opening roof", NotificationType.Info);
        }
        else
        {
            AddNotification("Error", result.Error?.Message ?? "Failed to open", NotificationType.Error);
        }
    }

    public void CloseRoof()
    {
        if (!IsServiceAvailable) return;
        var result = RoofController.Close();
        if (result.IsSuccessful)
        {
            AddNotification("Command", "Closing roof", NotificationType.Info);
        }
        else
        {
            AddNotification("Error", result.Error?.Message ?? "Failed to close", NotificationType.Error);
        }
    }

    public void StopRoof()
    {
        if (!IsServiceAvailable) return;
        var result = RoofController.Stop(RoofControllerStopReason.NormalStop);
        if (result.IsSuccessful)
        {
            AddNotification("Command", "Stop requested", NotificationType.Info);
        }
        else
        {
            AddNotification("Error", result.Error?.Message ?? "Failed to stop", NotificationType.Error);
        }
    }

    public async Task ClearFaultAsync()
    {
        try
        {
            var result = await RoofController.ClearFault();
            if (result.IsSuccessful)
            {
                AddNotification("Command", "Fault cleared", NotificationType.Success);
            }
            else
            {
                AddNotification("Error", result.Error?.Message ?? "Failed to clear fault", NotificationType.Error);
            }
            await UpdateStatusAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error clearing fault");
            AddNotification("Error", "Error clearing fault", NotificationType.Error);
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
        _ = InvokeAsync(StateHasChanged);
    }

    protected void RemoveNotification(NotificationMessage message)
    {
        _notifications.Remove(message);
        UpdateFooterStatus();
        _ = InvokeAsync(StateHasChanged);
    }

    #endregion

    #region Health Dialog

    protected async Task OpenHealthDialogAsync()
    {
        IsHealthDialogOpen = true;
        await FetchHealthReportAsync().ConfigureAwait(false);
    }

    protected async Task RefreshHealthDialogAsync()
    {
        if (!IsHealthDialogOpen)
        {
            IsHealthDialogOpen = true;
        }

        await FetchHealthReportAsync().ConfigureAwait(false);
    }

    protected void CloseHealthDialog()
    {
        IsHealthDialogOpen = false;
        HealthDialogError = null;
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

        var serializerOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        return JsonSerializer.Serialize(data!.Value, serializerOptions);
    }

    private async Task FetchHealthReportAsync()
    {
        HealthDialogError = null;
        IsHealthDialogLoading = true;
        await InvokeAsync(StateHasChanged);

        try
        {
            var client = GetHealthHttpClient();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var payload = await client.GetFromJsonAsync<HealthReportPayload>("health", cancellation.Token).ConfigureAwait(false);
            HealthReport = payload;
        }
        catch (OperationCanceledException ex)
        {
            Logger.LogWarning(ex, "Timed out retrieving health report");
            HealthDialogError = "Timed out retrieving health details. Please try again.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to retrieve health report");
            HealthDialogError = "Unable to retrieve health details. Check server logs for more information.";
        }
        finally
        {
            IsHealthDialogLoading = false;
            if (!_isDisposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private HttpClient GetHealthHttpClient()
    {
        if (_healthHttpClient is not null)
        {
            return _healthHttpClient;
        }

        var client = HttpClientFactory.CreateClient("roof-health");

        if (client.BaseAddress is null)
        {
            client.BaseAddress = new Uri(NavigationManager.BaseUri);
        }

        if (!client.DefaultRequestHeaders.Accept.Any())
        {
            client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        }

        client.Timeout = TimeSpan.FromSeconds(15);

        _healthHttpClient = client;
        return _healthHttpClient;
    }

    private static int NormalizeStatusRank(string? status) => status?.ToLowerInvariant() switch
    {
        "unhealthy" => 0,
        "degraded" => 1,
        "healthy" => 2,
        _ => -1
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
        if (FooterStatusService is null)
        {
            return;
        }

        var footerNotifications = _notifications
            .Select(n => new FooterNotification(n.Title, n.Message, MapLevel(n.Type), n.Timestamp))
            .ToArray();

        FooterStatusService.SetLeftNotifications(footerNotifications);

        var centerMessage = $"Status: {CurrentStatus} • Mode: {GetHardwareModeLabel()}";
        if (IsIgnoringLimitSwitches)
        {
            centerMessage += " • Limits Ignored";
        }

        FooterStatusService.SetCenterMessage(new FooterStatusMessage(centerMessage, MapStatusLevel(CurrentStatus)));

        var watchdogMessage = IsSafetyWatchdogRunning
            ? $"Watchdog: {Math.Ceiling(SafetyWatchdogTimeRemaining)}s remaining"
            : $"Watchdog: standby ({SafetyWatchdogTimeoutSeconds}s)";
        var watchdogLevel = IsSafetyWatchdogRunning ? FooterStatusLevel.Warning : FooterStatusLevel.Info;
        FooterStatusService.SetRightMessage(new FooterStatusMessage(watchdogMessage, watchdogLevel));
    }

    private static FooterStatusLevel MapLevel(NotificationType type) => type switch
    {
        NotificationType.Error => FooterStatusLevel.Error,
        NotificationType.Warning => FooterStatusLevel.Warning,
        NotificationType.Success => FooterStatusLevel.Success,
        _ => FooterStatusLevel.Info
    };

    private static FooterStatusLevel MapStatusLevel(RoofControllerStatus status) => status switch
    {
        RoofControllerStatus.Error => FooterStatusLevel.Error,
        RoofControllerStatus.Opening => FooterStatusLevel.Warning,
        RoofControllerStatus.Closing => FooterStatusLevel.Warning,
        RoofControllerStatus.Open => FooterStatusLevel.Success,
        RoofControllerStatus.Closed => FooterStatusLevel.Info,
        _ => FooterStatusLevel.Info
    };

    #endregion
}
