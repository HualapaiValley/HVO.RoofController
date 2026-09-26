using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Microsoft.Extensions.Options;
using HVO.RoofControllerV4.RPi.Logic;
using HVO.RoofControllerV4.Common.Models;
using HVO.Core.Results;
using System.Timers;

namespace HVO.RoofControllerV4.RPi.Components.Pages;

/// <summary>
/// Blazor page component for observatory roof control operations.
/// Provides a touch-friendly interface for opening, closing, and stopping the roof,
/// with real-time status updates and safety features.
/// </summary>
public partial class RoofControl : ComponentBase, IDisposable
{
    #region Dependency Injection

    [Inject] private IRoofControllerServiceV4 RoofController { get; set; } = default!;
    [Inject] private ILogger<RoofControl> Logger { get; set; } = default!;
    [Inject] private IJSRuntime JSRuntime { get; set; } = default!;
    [Inject] private IOptions<RoofControllerOptionsV4> RoofControllerOptions { get; set; } = default!;

    #endregion

    #region Private Fields

    // Removed polling timer; updates are push-based via service events
    private readonly List<NotificationMessage> _notifications = new();
    private bool _isDisposed = false;
    private RoofControllerStopReason _lastNotifiedStopReason = RoofControllerStopReason.None;
    
    // Simulation removed

    #endregion

    #region Public Properties

    /// <summary>
    /// Gets the current roof controller status from the service.
    /// </summary>
    public RoofControllerStatus CurrentStatus => RoofController.Status;

    /// <summary>
    /// Gets a value indicating whether the roof controller is initialized from the service.
    /// </summary>
    public bool IsInitialized => RoofController.IsInitialized;

    /// <summary>
    /// Gets a value indicating whether the roof is currently moving from the service.
    /// </summary>
    public bool IsMoving => RoofController.IsMoving;

    /// <summary>
    /// Gets the CSS class for the Close button including blinking animation when appropriate.
    /// Uses service's authoritative state for consistency.
    /// </summary>
    public string GetCloseButtonClass()
    {
        var baseClasses = "btn btn-warning btn-lg control-btn";
        
        if (IsCloseDisabled)
        {
            Logger.LogDebug("GetCloseButtonClass: Disabled - Status: {Status}, IsMoving: {IsMoving}", RoofController.Status, RoofController.IsMoving);
            return $"{baseClasses} disabled";
        }
        
        // Blink when roof is closing to show active operation, or when safety watchdog is running
        if (RoofController.Status == RoofControllerStatus.Closing || IsSafetyWatchdogRunning)
        {
            Logger.LogDebug("GetCloseButtonClass: Blinking - Status: {Status}, SafetyWatchdog: {IsSafetyWatchdog}", RoofController.Status, IsSafetyWatchdogRunning);
            return $"{baseClasses} btn-blinking";
        }
        
        Logger.LogDebug("GetCloseButtonClass: Normal - Status: {Status}", RoofController.Status);
        return baseClasses;
    }

    /// <summary>
    /// Gets the CSS class for the Stop button including blinking animation when appropriate.
    /// Uses service's authoritative state for consistency.
    /// </summary>
    public string GetStopButtonClass()
    {
        var baseClasses = "btn btn-danger btn-lg control-btn";
        
        if (IsStopDisabled)
        {
            Logger.LogDebug("GetStopButtonClass: Disabled - Status: {Status}, IsMoving: {IsMoving}", RoofController.Status, RoofController.IsMoving);
            return $"{baseClasses} disabled";
        }
        
        // Urgent blinking when roof is moving or safety watchdog is running (emergency situations)
        if (RoofController.IsMoving || IsSafetyWatchdogRunning)
        {
            Logger.LogDebug("GetStopButtonClass: Urgent Blinking - Status: {Status}, IsMoving: {IsMoving}, SafetyWatchdog: {IsSafetyWatchdog}", RoofController.Status, RoofController.IsMoving, IsSafetyWatchdogRunning);
            return $"{baseClasses} btn-urgent-blink";
        }
        
        Logger.LogDebug("GetStopButtonClass: Normal - Status: {Status}, IsMoving: {IsMoving}", RoofController.Status, RoofController.IsMoving);
        return baseClasses;
    }

    #endregion

    #region Notification System

    /// <summary>
    /// Adds a notification message to the UI.
    /// </summary>
    private void AddNotification(string title, string message, NotificationType type)
    {
        try
        {
            var notification = new NotificationMessage
            {
                Id = Guid.NewGuid(),
                Title = title,
                Message = message,
                Type = type,
                Timestamp = DateTime.Now
            };

            _notifications.Insert(0, notification);

            // Keep only the last 5 notifications
            while (_notifications.Count > 5)
            {
                _notifications.RemoveAt(_notifications.Count - 1);
            }

            // Auto-remove success and info notifications after 5 seconds
            if (type == NotificationType.Success || type == NotificationType.Info)
            {
                _ = Task.Delay(5000).ContinueWith(_ => RemoveNotification(notification));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error adding notification");
        }
    }

    /// <summary>
    /// Removes a notification from the UI.
    /// </summary>
    public void RemoveNotification(NotificationMessage notification)
    {
        try
        {
            _notifications.Remove(notification);
            InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error removing notification");
        }
    }

    /// <summary>
    /// Gets the toast CSS class for a notification type.
    /// </summary>
    public string GetToastClass(NotificationType type)
    {
        return type switch
        {
            NotificationType.Success => "toast-success",
            NotificationType.Error => "toast-error",
            NotificationType.Warning => "toast-warning",
            NotificationType.Info => "toast-info",
            _ => "toast-info"
        };
    }

    public string GetToastStyle(NotificationType type)
    {
        return type switch
        {
            NotificationType.Success => "background-color: rgba(25, 135, 84, 0.9); color: white;",
            NotificationType.Error => "background-color: rgba(220, 53, 69, 0.9); color: white;",
            NotificationType.Warning => "background-color: rgba(255, 193, 7, 0.9); color: black;",
            NotificationType.Info => "background-color: rgba(13, 110, 253, 0.9); color: white;",
            _ => "background-color: rgba(108, 117, 125, 0.9); color: white;"
        };
    }

    public string GetToastIcon(NotificationType type)
    {
        return type switch
        {
            NotificationType.Success => "bi bi-check-circle-fill",
            NotificationType.Error => "bi bi-x-circle-fill",
            NotificationType.Warning => "bi bi-exclamation-triangle-fill",
            NotificationType.Info => "bi bi-info-circle-fill",
            _ => "bi bi-info-circle"
        };
    }

    public string GetHealthCheckBadgeClass()
    {
        if (IsServiceDisposed || RoofController.Status == RoofControllerStatus.Error)
        {
            return "bg-danger";
        }

        if (IsServiceAvailable)
        {
            return "bg-success";
        }

        return "bg-secondary";
    }

    /// <summary>
    /// Gets the CSS class for the operation progress bar.
    /// </summary>
    public string GetProgressBarClass()
    {
        // Simplified progress bar without timing
        return IsMoving ? "progress-bar-warning" : "progress-bar-success";
    }

    /// <summary>
    /// Gets the operation progress percentage for the progress bar.
    /// </summary>
    public int GetOperationProgressPercentage()
    {
        // Simplified progress without timing
        return IsMoving ? 50 : 0;
    }

    /// <summary>
    /// Gets the health check status string for display using service's authoritative state.
    /// </summary>
    public string GetHealthCheckStatus()
    {
        if (RoofController.IsInitialized && RoofController.Status != RoofControllerStatus.Error)
        {
            return "Healthy";
        }
        else if (RoofController.Status == RoofControllerStatus.Error)
        {
            return "Error Detected";
        }
        else
        {
            return "Checking...";
        }
    }

    /// <summary>
    /// Returns a Bootstrap badge class depending on the watchdog remaining time.
    /// </summary>
    public string GetWatchdogBadgeClass()
    {
        var remaining = SafetyWatchdogTimeRemaining;
        if (IsSafetyWatchdogRunning && remaining <= 5)
        {
            return "badge bg-danger text-white fs-6";
        }
        return "badge bg-warning text-dark fs-6";
    }

    /// <summary>
    /// Returns the progress bar class for watchdog remaining time (danger near timeout).
    /// </summary>
    public string GetWatchdogProgressBarClass()
    {
        var remaining = SafetyWatchdogTimeRemaining;
        if (IsSafetyWatchdogRunning && remaining <= 5)
        {
            return "progress-bar bg-danger progress-bar-striped progress-bar-animated";
        }
        return "progress-bar bg-warning progress-bar-striped progress-bar-animated";
    }

    /// <summary>
    /// Human-friendly relative time for last transition.
    /// </summary>
    public string GetLastTransitionFriendly()
    {
        if (LastTransitionUtc is null) return "n/a";
        var delta = DateTimeOffset.UtcNow - LastTransitionUtc.Value;
        if (delta.TotalSeconds < 60) return $"{Math.Floor(delta.TotalSeconds)}s ago";
        if (delta.TotalMinutes < 60) return $"{Math.Floor(delta.TotalMinutes)}m ago";
        if (delta.TotalHours < 24) return $"{Math.Floor(delta.TotalHours)}h ago";
        return LastTransitionUtc.Value.ToLocalTime().ToString("g");
    }

    /// <summary>
    /// Full tooltip text for last transition (UTC).
    /// </summary>
    public string GetLastTransitionTooltip()
    {
        return LastTransitionUtc?.ToString("u") ?? "unknown";
    }

    #endregion

    // Simulation control methods removed

    #region Supporting Classes

    /// <summary>
    /// Represents a notification message in the UI.
    /// </summary>
    public class NotificationMessage
    {
        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public NotificationType Type { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Defines the types of notifications.
    /// </summary>
    public enum NotificationType
    {
        Info,
        Success,
        Warning,
        Error
    }

    #endregion
}
