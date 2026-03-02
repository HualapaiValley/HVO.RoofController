using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HVO.RoofControllerV4.iPad.Configuration;
using HVO.RoofControllerV4.iPad.Models;
using HVO.RoofControllerV4.iPad.Popups;
using HVO.RoofControllerV4.iPad.Services;
using HVO.RoofControllerV4.Common.Models;
using HVO.Core.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;

namespace HVO.RoofControllerV4.iPad.ViewModels;

/// <summary>
/// Presentation model for the iPad roof controller experience.
/// </summary>
public sealed partial class RoofControllerViewModel : ObservableObject, IDisposable
{
    private readonly IRoofControllerApiClient _apiClient;
    private readonly RoofControllerApiOptions _options;
    private readonly ILogger<RoofControllerViewModel> _logger;
    private readonly IDialogService _dialogService;
    private readonly IRoofControllerConfigurationService _configurationService;
    private readonly IPopupService _popupService;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _promptSemaphore = new(1, 1);
    private int _promptThreshold;
    private readonly ObservableCollection<NotificationItem> _notificationHistory = new();
    private HealthStatusPopup? _activeHealthPopup;

    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private bool _hasInitialized;
    private bool _serviceUnavailableNotified;
    private RoofControllerStatus _previousStatus = RoofControllerStatus.Unknown;
    private RoofControllerStopReason _previousStopReason = RoofControllerStopReason.None;
    private int _consecutiveFailures;
    private bool _configurationEditorInitialized;
    private bool _isLoadingConfigurationEditor;
    private bool _suppressConfigurationDirty;
    private bool _suppressRemoteConfigurationDirty;
    private RoofConfigurationResponse? _remoteConfigurationSnapshot;
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(10);

    public RoofControllerViewModel(
        IRoofControllerApiClient apiClient,
        IOptions<RoofControllerApiOptions> options,
        ILogger<RoofControllerViewModel> logger,
        IDialogService dialogService,
        IRoofControllerConfigurationService configurationService,
        IPopupService popupService)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _popupService = popupService ?? throw new ArgumentNullException(nameof(popupService));
        _promptThreshold = Math.Max(1, _options.ConnectionFailurePromptThreshold);
        NotificationHistory = new ReadOnlyObservableCollection<NotificationItem>(_notificationHistory);
        _notificationHistory.CollectionChanged += OnNotificationHistoryChanged;
    }

    #region Bindable State

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFaultCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFaultCommand))]
    private bool isServiceAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFaultCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool isMoving;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private RoofControllerStatus currentStatus = RoofControllerStatus.Unknown;

    [ObservableProperty]
    private RoofControllerStopReason lastStopReason = RoofControllerStopReason.None;

    [ObservableProperty]
    private DateTimeOffset? lastTransitionUtc;

    [ObservableProperty]
    private bool isWatchdogActive;

    [ObservableProperty]
    private double watchdogSecondsRemaining;

    [ObservableProperty]
    private string? serviceBannerTitle;

    [ObservableProperty]
    private string? serviceBannerMessage;

    [ObservableProperty]
    private bool showServiceBanner;

    [ObservableProperty]
    private bool serviceBannerIsError;

    [ObservableProperty]
    private string? lastErrorMessage;

    [ObservableProperty]
    private NotificationItem? latestNotification;

    [ObservableProperty]
    private string configurationBaseUrl = string.Empty;

    [ObservableProperty]
    private string configurationCameraStreamUrl = string.Empty;

    [ObservableProperty]
    private string configurationStatusPollIntervalSeconds = string.Empty;

    [ObservableProperty]
    private string configurationRequestRetryCount = string.Empty;

    [ObservableProperty]
    private string configurationFailurePromptThreshold = string.Empty;

    [ObservableProperty]
    private string configurationClearFaultPulseMs = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveConfigurationCommand))]
    private bool isConfigurationSaving;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveConfigurationCommand))]
    private bool isConfigurationDirty;

    [ObservableProperty]
    private string? configurationErrorMessage;

    [ObservableProperty]
    private string? configurationSuccessMessage;

    [ObservableProperty]
    private string remoteSafetyWatchdogTimeoutSeconds = string.Empty;

    [ObservableProperty]
    private string remoteOpenRelayId = string.Empty;

    [ObservableProperty]
    private string remoteCloseRelayId = string.Empty;

    [ObservableProperty]
    private string remoteClearFaultRelayId = string.Empty;

    [ObservableProperty]
    private string remoteStopRelayId = string.Empty;

    [ObservableProperty]
    private string remoteDigitalInputPollIntervalMilliseconds = string.Empty;

    [ObservableProperty]
    private string remotePeriodicVerificationIntervalSeconds = string.Empty;

    [ObservableProperty]
    private string remoteLimitSwitchDebounceMilliseconds = string.Empty;

    [ObservableProperty]
    private bool remoteEnableDigitalInputPolling;

    [ObservableProperty]
    private bool remoteEnablePeriodicVerificationWhileMoving;

    [ObservableProperty]
    private bool remoteUseNormallyClosedLimitSwitches;

    [ObservableProperty]
    private bool remoteIgnorePhysicalLimitSwitches;

    [ObservableProperty]
    private string remoteRestartOnFailureWaitTimeSeconds = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRemoteConfigurationCommand))]
    private bool isRemoteConfigurationSaving;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRemoteConfigurationCommand))]
    private bool isRemoteConfigurationDirty;

    [ObservableProperty]
    private string? remoteConfigurationErrorMessage;

    [ObservableProperty]
    private string? remoteConfigurationSuccessMessage;

    [ObservableProperty]
    private bool isRemoteConfigurationRefreshing;

    [ObservableProperty]
    private bool isHealthDialogOpen;

    [ObservableProperty]
    private bool isHealthDialogLoading;

    [ObservableProperty]
    private string? healthDialogError;

    [ObservableProperty]
    private HealthReportPayload? healthReport;

    [ObservableProperty]
    private IReadOnlyList<HealthCheckDisplay> healthChecks = Array.Empty<HealthCheckDisplay>();

    #endregion

    partial void OnConfigurationBaseUrlChanged(string value) => MarkConfigurationDirty();

    partial void OnConfigurationCameraStreamUrlChanged(string value) => MarkConfigurationDirty();

    partial void OnConfigurationStatusPollIntervalSecondsChanged(string value) => MarkConfigurationDirty();

    partial void OnConfigurationRequestRetryCountChanged(string value) => MarkConfigurationDirty();

    partial void OnConfigurationFailurePromptThresholdChanged(string value) => MarkConfigurationDirty();

    partial void OnConfigurationClearFaultPulseMsChanged(string value) => MarkConfigurationDirty();

    partial void OnConfigurationErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasConfigurationError));

    partial void OnConfigurationSuccessMessageChanged(string? value) => OnPropertyChanged(nameof(HasConfigurationSuccess));

    partial void OnRemoteSafetyWatchdogTimeoutSecondsChanged(string value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteOpenRelayIdChanged(string value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteCloseRelayIdChanged(string value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteClearFaultRelayIdChanged(string value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteStopRelayIdChanged(string value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteDigitalInputPollIntervalMillisecondsChanged(string value) => MarkRemoteConfigurationDirty();

    partial void OnRemotePeriodicVerificationIntervalSecondsChanged(string value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteLimitSwitchDebounceMillisecondsChanged(string value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteEnableDigitalInputPollingChanged(bool value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteEnablePeriodicVerificationWhileMovingChanged(bool value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteUseNormallyClosedLimitSwitchesChanged(bool value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteIgnorePhysicalLimitSwitchesChanged(bool value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteRestartOnFailureWaitTimeSecondsChanged(string value)
    {
        OnPropertyChanged(nameof(RemoteRestartOnFailureWaitTimeDisplay));
        OnPropertyChanged(nameof(HasRemoteRestartOnFailureWaitTime));
    }

    partial void OnRemoteConfigurationErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasRemoteConfigurationError));

    partial void OnRemoteConfigurationSuccessMessageChanged(string? value) => OnPropertyChanged(nameof(HasRemoteConfigurationSuccess));

    partial void OnIsRemoteConfigurationSavingChanged(bool value) => OnPropertyChanged(nameof(IsRemoteConfigurationBusy));

    partial void OnIsRemoteConfigurationRefreshingChanged(bool value) => OnPropertyChanged(nameof(IsRemoteConfigurationBusy));

    partial void OnHealthDialogErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasHealthDialogError));
        OnPropertyChanged(nameof(ShowNoHealthChecksMessage));
    }

    partial void OnIsHealthDialogLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNoHealthChecksMessage));
    }

    partial void OnHealthReportChanged(HealthReportPayload? value)
    {
        var ordered = value?.Checks is { Count: > 0 } checks
            ? checks
                .OrderByDescending(c => NormalizeStatusRank(c.Status))
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new HealthCheckDisplay(c))
                .ToArray()
            : Array.Empty<HealthCheckDisplay>();

        HealthChecks = ordered;
        OnPropertyChanged(nameof(HasHealthReport));
        OnPropertyChanged(nameof(HealthStatus));
        OnPropertyChanged(nameof(HealthDialogStatusText));
        OnPropertyChanged(nameof(HealthDialogStatusColor));
        OnPropertyChanged(nameof(HealthDialogStatusTextColor));
        OnPropertyChanged(nameof(HealthDialogDuration));
        OnPropertyChanged(nameof(ShowNoHealthChecksMessage));
    }

    partial void OnHealthChecksChanged(IReadOnlyList<HealthCheckDisplay> value)
    {
        OnPropertyChanged(nameof(HasHealthChecks));
        OnPropertyChanged(nameof(ShowNoHealthChecksMessage));
    }

    partial void OnIsHealthDialogOpenChanged(bool value)
    {
        if (!value)
        {
            IsHealthDialogLoading = false;
        }

        OnPropertyChanged(nameof(ShowNoHealthChecksMessage));
    }

    public bool HasConfigurationError => !string.IsNullOrWhiteSpace(ConfigurationErrorMessage);

    public bool HasConfigurationSuccess => !string.IsNullOrWhiteSpace(ConfigurationSuccessMessage);

    public bool HasRemoteConfigurationError => !string.IsNullOrWhiteSpace(RemoteConfigurationErrorMessage);

    public bool HasRemoteConfigurationSuccess => !string.IsNullOrWhiteSpace(RemoteConfigurationSuccessMessage);

    public bool IsRemoteConfigurationBusy => IsRemoteConfigurationSaving || IsRemoteConfigurationRefreshing;

    public bool HasFault => CurrentStatus == RoofControllerStatus.Error;

    public bool WasEmergencyStop => LastStopReason is RoofControllerStopReason.EmergencyStop or RoofControllerStopReason.SafetyWatchdogTimeout;

    public string LastStopReasonLabel => LastStopReason switch
    {
        RoofControllerStopReason.EmergencyStop or RoofControllerStopReason.SafetyWatchdogTimeout => "Emergency",
        RoofControllerStopReason.None => string.Empty,
        _ => "Normal"
    };

    public Color InitializationBadgeColor => IsInitialized ? Color.FromArgb("#198754") : Color.FromArgb("#6c757d");

    public string InitializationBadgeText => IsInitialized ? "Initialized" : "Initializing…";

    public string StatusText => CurrentStatus switch
    {
        RoofControllerStatus.Unknown => "Unknown",
        RoofControllerStatus.NotInitialized => "Initializing…",
        RoofControllerStatus.Open => "Open",
        RoofControllerStatus.Opening => "Opening",
        RoofControllerStatus.Closed => "Closed",
        RoofControllerStatus.Closing => "Closing",
        RoofControllerStatus.Stopped => "Stopped",
        RoofControllerStatus.PartiallyOpen => "Partially Open",
        RoofControllerStatus.PartiallyClose => "Partially Closed",
        RoofControllerStatus.Error => "Fault",
        _ => CurrentStatus.ToString()
    };

    public string HealthStatus
    {
        get
        {
            if (HasFault)
            {
                return "Error Detected";
            }

            if (HealthReport is { Status.Length: > 0 } report)
            {
                return report.Status;
            }

            return IsServiceAvailable ? "Healthy" : "Checking…";
        }
    }

    public string MovementStatus => IsMoving ? "In Progress" : "Idle";

    public bool IsInitialized => CurrentStatus != RoofControllerStatus.Unknown && CurrentStatus != RoofControllerStatus.NotInitialized;

    public string LastTransitionDisplay => LastTransitionUtc is null
        ? "—"
        : LastTransitionUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public double WatchdogPercentage
    {
        get
        {
            if (!IsWatchdogActive)
            {
                return 0d;
            }

            var timeout = GetConfiguredWatchdogTimeoutSeconds();
            if (timeout is null or <= 0)
            {
                return 0d;
            }

            if (WatchdogSecondsRemaining <= 0)
            {
                return 1d;
            }

            return Math.Clamp(1d - (WatchdogSecondsRemaining / timeout.Value), 0d, 1d);
        }
    }

    public Color StatusBadgeColor => CurrentStatus switch
    {
        RoofControllerStatus.Open => Color.FromArgb("#198754"),
        RoofControllerStatus.Closed => Color.FromArgb("#6c757d"),
        RoofControllerStatus.Opening or RoofControllerStatus.Closing => Color.FromArgb("#0dcaf0"),
        RoofControllerStatus.Error => Color.FromArgb("#dc3545"),
        _ => Color.FromArgb("#343a40")
    };

    public Color HealthBadgeColor => HasFault ? Color.FromArgb("#dc3545") : Color.FromArgb("#198754");

    public Color MovementBadgeColor => IsMoving ? Color.FromArgb("#0d6efd") : Color.FromArgb("#6c757d");

    public bool ShowStopBadge => !string.IsNullOrEmpty(LastStopReasonLabel);

    public Color StopBadgeColor => WasEmergencyStop ? Color.FromArgb("#dc3545") : Color.FromArgb("#6c757d");

    public Uri? CameraStreamUri => _options.GetCameraStreamUri();

    public string? CameraStreamUrl => CameraStreamUri?.ToString();

    public bool HasLatestNotification => LatestNotification is not null;

    public ReadOnlyObservableCollection<NotificationItem> NotificationHistory { get; }

    public bool HasNotificationHistory => NotificationHistory.Count > 0;

    public string ApiBaseUrl => _options.BaseUrl;

    public string CameraStreamDisplay => string.IsNullOrWhiteSpace(_options.CameraStreamUrl)
        ? "Not configured"
        : _options.CameraStreamUrl;

    public int ConfiguredStatusPollIntervalSeconds => _options.StatusPollIntervalSeconds;

    public int ConfiguredRequestRetryCount => _options.RequestRetryCount;

    public int ConfiguredFailurePromptThreshold => _options.ConnectionFailurePromptThreshold;

    public int ConfiguredClearFaultPulseMs => _options.ClearFaultPulseMs;

    public string SafetyWatchdogTimeoutDisplay
    {
        get
        {
            var value = GetConfiguredWatchdogTimeoutSeconds();
            return value is { } seconds and > 0
                ? $"{seconds:F0} seconds"
                : "Not configured";
        }
    }

    public string WatchdogStatusText => IsWatchdogActive ? "Active" : "Standby";

    public string WatchdogSecondaryText
    {
        get
        {
            if (IsWatchdogActive)
            {
                return $"{Math.Max(WatchdogSecondsRemaining, 0):F0}s remaining";
            }

            var timeout = GetConfiguredWatchdogTimeoutSeconds();

            return timeout is { } configuredTimeout and > 0
                ? $"Timeout {configuredTimeout:F0}s"
                : "Timeout not configured";
        }
    }

    public bool HasHealthDialogError => !string.IsNullOrWhiteSpace(HealthDialogError);

    public bool HasHealthReport => HealthReport is not null;

    public bool HasHealthChecks => HealthChecks.Count > 0;

    public bool ShowNoHealthChecksMessage => !IsHealthDialogLoading && string.IsNullOrWhiteSpace(HealthDialogError) && HealthReport is not null && HealthChecks.Count == 0;

    public string HealthDialogStatusText => HealthReport?.Status ?? (IsServiceAvailable ? "Healthy" : "Unknown");

    public Color HealthDialogStatusColor => GetHealthStatusBackground(HealthReport?.Status);

    public Color HealthDialogStatusTextColor => GetHealthStatusText(HealthReport?.Status);

    public string? HealthDialogDuration => HealthReport?.TotalDuration;

    public string RemoteRestartOnFailureWaitTimeDisplay => string.IsNullOrWhiteSpace(RemoteRestartOnFailureWaitTimeSeconds)
        ? "Restart wait not reported"
        : $"Restart wait {RemoteRestartOnFailureWaitTimeSeconds}s";

    public bool HasRemoteRestartOnFailureWaitTime => !string.IsNullOrWhiteSpace(RemoteRestartOnFailureWaitTimeSeconds);

    public Color ServiceBannerColor => ServiceBannerIsError ? Color.FromArgb("#dc3545") : Color.FromArgb("#f0ad4e");

    public bool CanOpen() => !IsBusy && IsServiceAvailable && !IsMoving && CurrentStatus is not (RoofControllerStatus.Open or RoofControllerStatus.Opening or RoofControllerStatus.Error);

    public bool CanClose() => !IsBusy && IsServiceAvailable && !IsMoving && CurrentStatus is not (RoofControllerStatus.Closed or RoofControllerStatus.Closing or RoofControllerStatus.Error);

    public bool CanStop() => !IsBusy && IsServiceAvailable && IsMoving;

    public bool CanClearFault() => !IsBusy && IsServiceAvailable && !IsMoving && HasFault;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        await ExecuteCommandAsync(() => _apiClient.OpenAsync(), "Command", "Opening roof", "Failed to open roof");
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task CloseAsync()
    {
        await ExecuteCommandAsync(() => _apiClient.CloseAsync(), "Command", "Closing roof", "Failed to close roof");
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await ExecuteCommandAsync(() => _apiClient.StopAsync(), "Command", "Stop requested", "Failed to stop roof");
    }

    [RelayCommand(CanExecute = nameof(CanClearFault))]
    private async Task ClearFaultAsync()
    {
        await ExecuteCommandAsync(async () =>
        {
            var result = await _apiClient.ClearFaultAsync(_options.ClearFaultPulseMs).ConfigureAwait(false);
            if (result.IsSuccessful && result.Value)
            {
                return await _apiClient.GetStatusAsync().ConfigureAwait(false);
            }

            return Result<RoofStatusResponse>.Failure(result.Error ?? new InvalidOperationException("Clear fault failed"));
        }, "Command", "Fault cleared", "Failed to clear fault");
    }

    #region Health Dialog

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenHealthDialogAsync()
    {
        await EnsureHealthDialogPopupAsync().ConfigureAwait(false);
        await RefreshHealthDialogInternalAsync().ConfigureAwait(false);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshHealthDialogAsync()
    {
        await EnsureHealthDialogPopupAsync().ConfigureAwait(false);
        await RefreshHealthDialogInternalAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private void CloseHealthDialog()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!IsHealthDialogOpen)
            {
                return;
            }

            IsHealthDialogOpen = false;
            HealthDialogError = null;
            _activeHealthPopup?.Close();
        });
    }

    private async Task EnsureHealthDialogPopupAsync()
    {
        var shouldShowPopup = false;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (!IsHealthDialogOpen)
            {
                IsHealthDialogOpen = true;
                shouldShowPopup = true;
            }

            HealthDialogError = null;
        }).ConfigureAwait(false);

        if (shouldShowPopup)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                HealthStatusPopup? createdPopup = null;

                _popupService.ShowPopup<HealthStatusPopupViewModel>(viewModel =>
                {
                    if (viewModel is null)
                    {
                        throw new InvalidOperationException("Popup view model was not provided.");
                    }

                    viewModel.Dashboard = this;

                    createdPopup = viewModel.Popup;
                });

                if (createdPopup is null)
                {
                    throw new InvalidOperationException("Health status popup instance was not created.");
                }

                _activeHealthPopup = createdPopup;
                _activeHealthPopup.Closed += OnHealthPopupClosed;
            }).ConfigureAwait(false);
        }
    }

    private void OnHealthPopupClosed(object? sender, PopupClosedEventArgs e)
    {
        if (sender is HealthStatusPopup popup)
        {
            popup.Closed -= OnHealthPopupClosed;

            if (ReferenceEquals(_activeHealthPopup, popup))
            {
                _activeHealthPopup = null;
            }
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (IsHealthDialogOpen)
            {
                IsHealthDialogOpen = false;
            }

            HealthDialogError = null;
        });
    }

    private async Task RefreshHealthDialogInternalAsync()
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            IsHealthDialogLoading = true;
            HealthDialogError = null;
        }).ConfigureAwait(false);

        try
        {
            using var cancellation = new CancellationTokenSource(HealthRequestTimeout);
            var result = await _apiClient.GetHealthReportAsync(cancellation.Token).ConfigureAwait(false);

            if (result.IsSuccessful)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    HealthReport = result.Value;
                    HealthDialogError = null;
                }).ConfigureAwait(false);
            }
            else if (result.Error is OperationCanceledException)
            {
                await HandleHealthDialogErrorAsync(result.Error, timedOut: true).ConfigureAwait(false);
            }
            else if (result.Error is not null)
            {
                await HandleHealthDialogErrorAsync(result.Error, timedOut: false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex)
        {
            await HandleHealthDialogErrorAsync(ex, timedOut: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HandleHealthDialogErrorAsync(ex, timedOut: false).ConfigureAwait(false);
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                IsHealthDialogLoading = false;
            }).ConfigureAwait(false);
        }
    }

    private async Task HandleHealthDialogErrorAsync(Exception error, bool timedOut)
    {
        if (timedOut)
        {
            _logger.LogWarning(error, "Timed out retrieving health report");
        }
        else
        {
            _logger.LogError(error, "Failed to retrieve health report");
        }

        var message = timedOut
            ? "Timed out retrieving health details. Please try again."
            : "Unable to retrieve health details. Check server logs for more information.";

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            HealthDialogError = message;
        }).ConfigureAwait(false);
    }

    #endregion

    public async Task LoadConfigurationEditorAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoadingConfigurationEditor)
        {
            return;
        }

        _isLoadingConfigurationEditor = true;

        try
        {
            var loadResult = await _configurationService.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!loadResult.IsSuccessful && loadResult.Error is not null)
            {
                _logger.LogError(loadResult.Error, "Failed to load roof controller configuration overrides");
            }

            var effectiveOptions = loadResult.IsSuccessful ? loadResult.Value : _options;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                ConfigurationSuccessMessage = null;
                if (!loadResult.IsSuccessful && loadResult.Error is not null)
                {
                    ConfigurationErrorMessage = $"Failed to load saved configuration. Using current runtime values. {loadResult.Error.Message}";
                }
                else
                {
                    ConfigurationErrorMessage = null;
                }

                PopulateConfigurationFields(effectiveOptions, markClean: true);
            }).ConfigureAwait(false);

            await FetchRemoteConfigurationAsync(showSuccessMessage: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _isLoadingConfigurationEditor = false;
            _configurationEditorInitialized = true;
        }
    }

    public bool CanSaveConfiguration() => !IsConfigurationSaving && IsConfigurationDirty;

    [RelayCommand(CanExecute = nameof(CanSaveConfiguration))]
    private async Task SaveConfigurationAsync()
    {
        ConfigurationErrorMessage = null;
        ConfigurationSuccessMessage = null;

        var baseUrl = ConfigurationBaseUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ConfigurationErrorMessage = "API base URL is required.";
            return;
        }

        var cameraStream = string.IsNullOrWhiteSpace(ConfigurationCameraStreamUrl)
            ? null
            : ConfigurationCameraStreamUrl.Trim();

        if (!TryParsePositiveInt(ConfigurationStatusPollIntervalSeconds, "Status poll interval", out var pollInterval, out var parseError))
        {
            ConfigurationErrorMessage = parseError;
            return;
        }

        if (!TryParsePositiveInt(ConfigurationClearFaultPulseMs, "Clear fault pulse", out var clearFaultPulse, out parseError))
        {
            ConfigurationErrorMessage = parseError;
            return;
        }

        if (!TryParsePositiveInt(ConfigurationRequestRetryCount, "Request retry count", out var retryCount, out parseError))
        {
            ConfigurationErrorMessage = parseError;
            return;
        }

        if (!TryParsePositiveInt(ConfigurationFailurePromptThreshold, "Failure prompt threshold", out var promptThreshold, out parseError))
        {
            ConfigurationErrorMessage = parseError;
            return;
        }

        var updatedOptions = new RoofControllerApiOptions
        {
            BaseUrl = baseUrl,
            CameraStreamUrl = cameraStream,
            StatusPollIntervalSeconds = pollInterval,
            ClearFaultPulseMs = clearFaultPulse,
            SafetyWatchdogTimeoutSeconds = _options.SafetyWatchdogTimeoutSeconds,
            RequestRetryCount = retryCount,
            ConnectionFailurePromptThreshold = promptThreshold
        };

        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(updatedOptions, new ValidationContext(updatedOptions), validationResults, true))
        {
            ConfigurationErrorMessage = string.Join(Environment.NewLine, validationResults.Select(r => r.ErrorMessage).Where(static message => !string.IsNullOrWhiteSpace(message)).Distinct());
            return;
        }

        try
        {
            IsConfigurationSaving = true;

            var saveResult = await _configurationService.SaveAsync(updatedOptions).ConfigureAwait(false);
            if (!saveResult.IsSuccessful)
            {
                _logger.LogError(saveResult.Error, "Failed to persist roof controller configuration");
                ConfigurationErrorMessage = saveResult.Error?.Message ?? "Failed to save configuration.";
                return;
            }

            ApplyConfigurationToOptions(saveResult.Value);
            RaiseConfigurationPropertyChanges();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                PopulateConfigurationFields(saveResult.Value, markClean: true);
                ConfigurationSuccessMessage = "Configuration saved. Restart the app to apply updated connection settings.";
            }).ConfigureAwait(false);
        }
        finally
        {
            IsConfigurationSaving = false;
        }
    }

    private void PopulateConfigurationFields(RoofControllerApiOptions options, bool markClean)
    {
        _suppressConfigurationDirty = true;

        try
        {
            ConfigurationBaseUrl = options.BaseUrl;
            ConfigurationCameraStreamUrl = options.CameraStreamUrl ?? string.Empty;
            ConfigurationStatusPollIntervalSeconds = options.StatusPollIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            ConfigurationRequestRetryCount = options.RequestRetryCount.ToString(CultureInfo.InvariantCulture);
            ConfigurationFailurePromptThreshold = options.ConnectionFailurePromptThreshold.ToString(CultureInfo.InvariantCulture);
            ConfigurationClearFaultPulseMs = options.ClearFaultPulseMs.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressConfigurationDirty = false;
        }

        if (markClean)
        {
            IsConfigurationDirty = false;
        }
    }

    private void ApplyConfigurationToOptions(RoofControllerApiOptions options)
    {
        _options.BaseUrl = options.BaseUrl;
        _options.CameraStreamUrl = options.CameraStreamUrl;
        _options.StatusPollIntervalSeconds = options.StatusPollIntervalSeconds;
        _options.ClearFaultPulseMs = options.ClearFaultPulseMs;
        _options.SafetyWatchdogTimeoutSeconds = options.SafetyWatchdogTimeoutSeconds;
        _options.RequestRetryCount = options.RequestRetryCount;
        _options.ConnectionFailurePromptThreshold = options.ConnectionFailurePromptThreshold;
        _promptThreshold = Math.Max(1, _options.ConnectionFailurePromptThreshold);
    }

    private void RaiseConfigurationPropertyChanges()
    {
        OnPropertyChanged(nameof(ApiBaseUrl));
        OnPropertyChanged(nameof(CameraStreamDisplay));
        OnPropertyChanged(nameof(CameraStreamUri));
        OnPropertyChanged(nameof(CameraStreamUrl));
        OnPropertyChanged(nameof(ConfiguredStatusPollIntervalSeconds));
        OnPropertyChanged(nameof(ConfiguredRequestRetryCount));
        OnPropertyChanged(nameof(ConfiguredFailurePromptThreshold));
        OnPropertyChanged(nameof(ConfiguredClearFaultPulseMs));
        OnPropertyChanged(nameof(SafetyWatchdogTimeoutDisplay));
        OnPropertyChanged(nameof(WatchdogSecondaryText));
        OnPropertyChanged(nameof(WatchdogPercentage));
    }

    public bool CanSaveRemoteConfiguration() => !IsRemoteConfigurationSaving && IsRemoteConfigurationDirty;

    [RelayCommand(CanExecute = nameof(CanSaveRemoteConfiguration))]
    private async Task SaveRemoteConfigurationAsync()
    {
        RemoteConfigurationErrorMessage = null;
        RemoteConfigurationSuccessMessage = null;

        if (!TryBuildRemoteConfigurationRequest(out var request, out var validationError))
        {
            RemoteConfigurationErrorMessage = validationError;
            return;
        }

        try
        {
            IsRemoteConfigurationSaving = true;
            var result = await _apiClient.UpdateConfigurationAsync(request).ConfigureAwait(false);
            if (!result.IsSuccessful)
            {
                _logger.LogError(result.Error, "Failed to update roof controller configuration");
                RemoteConfigurationErrorMessage = result.Error?.Message ?? "Failed to update controller configuration.";
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                PopulateRemoteConfigurationFields(result.Value, markClean: true);
                RemoteConfigurationSuccessMessage = "Controller configuration updated.";
            }).ConfigureAwait(false);
        }
        finally
        {
            IsRemoteConfigurationSaving = false;
        }
    }

    [RelayCommand]
    private async Task RefreshRemoteConfigurationAsync()
    {
        await FetchRemoteConfigurationAsync(showSuccessMessage: true).ConfigureAwait(false);
    }

    private async Task FetchRemoteConfigurationAsync(bool showSuccessMessage, CancellationToken cancellationToken = default)
    {
        if (IsRemoteConfigurationRefreshing)
        {
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            IsRemoteConfigurationRefreshing = true;
            if (!showSuccessMessage)
            {
                RemoteConfigurationSuccessMessage = null;
            }
        });

        try
        {
            var result = await _apiClient.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccessful)
            {
                _logger.LogError(result.Error, "Failed to retrieve roof controller configuration snapshot");
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    RemoteConfigurationErrorMessage = result.Error?.Message ?? "Failed to load controller configuration.";
                }).ConfigureAwait(false);
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                RemoteConfigurationErrorMessage = null;
                PopulateRemoteConfigurationFields(result.Value, markClean: true);
                if (showSuccessMessage)
                {
                    RemoteConfigurationSuccessMessage = "Loaded configuration snapshot from controller.";
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() => IsRemoteConfigurationRefreshing = false).ConfigureAwait(false);
        }
    }

    private bool TryBuildRemoteConfigurationRequest(out RoofConfigurationRequest request, out string? errorMessage)
    {
        request = default!;
        errorMessage = null;

        if (!TryParseNonNegativeDouble(RemoteSafetyWatchdogTimeoutSeconds, "Safety watchdog timeout (seconds)", out var watchdogSeconds, out errorMessage))
        {
            return false;
        }

        if (!TryParsePositiveInt(RemoteOpenRelayId, "Open relay ID", out var openRelayId, out errorMessage))
        {
            return false;
        }

        if (!TryParsePositiveInt(RemoteCloseRelayId, "Close relay ID", out var closeRelayId, out errorMessage))
        {
            return false;
        }

        if (!TryParsePositiveInt(RemoteClearFaultRelayId, "Clear fault relay ID", out var clearFaultRelayId, out errorMessage))
        {
            return false;
        }

        if (!TryParsePositiveInt(RemoteStopRelayId, "Stop relay ID", out var stopRelayId, out errorMessage))
        {
            return false;
        }

        if (!TryParseNonNegativeDouble(RemoteDigitalInputPollIntervalMilliseconds, "Digital input poll interval (ms)", out var pollIntervalMs, out errorMessage))
        {
            return false;
        }

        if (!TryParseNonNegativeDouble(RemotePeriodicVerificationIntervalSeconds, "Verification interval (seconds)", out var verificationSeconds, out errorMessage))
        {
            return false;
        }

        if (!TryParseNonNegativeDouble(RemoteLimitSwitchDebounceMilliseconds, "Limit switch debounce (ms)", out var debounceMs, out errorMessage))
        {
            return false;
        }

        request = new RoofConfigurationRequest
        {
            SafetyWatchdogTimeoutSeconds = watchdogSeconds,
            OpenRelayId = openRelayId,
            CloseRelayId = closeRelayId,
            ClearFaultRelayId = clearFaultRelayId,
            StopRelayId = stopRelayId,
            EnableDigitalInputPolling = RemoteEnableDigitalInputPolling,
            DigitalInputPollIntervalMilliseconds = pollIntervalMs,
            EnablePeriodicVerificationWhileMoving = RemoteEnablePeriodicVerificationWhileMoving,
            PeriodicVerificationIntervalSeconds = verificationSeconds,
            UseNormallyClosedLimitSwitches = RemoteUseNormallyClosedLimitSwitches,
            LimitSwitchDebounceMilliseconds = debounceMs,
            IgnorePhysicalLimitSwitches = RemoteIgnorePhysicalLimitSwitches
        };

        return true;
    }

    private void PopulateRemoteConfigurationFields(RoofConfigurationResponse configuration, bool markClean)
    {
        _suppressRemoteConfigurationDirty = true;

        try
        {
            RemoteSafetyWatchdogTimeoutSeconds = configuration.SafetyWatchdogTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            RemoteOpenRelayId = configuration.OpenRelayId.ToString(CultureInfo.InvariantCulture);
            RemoteCloseRelayId = configuration.CloseRelayId.ToString(CultureInfo.InvariantCulture);
            RemoteClearFaultRelayId = configuration.ClearFaultRelayId.ToString(CultureInfo.InvariantCulture);
            RemoteStopRelayId = configuration.StopRelayId.ToString(CultureInfo.InvariantCulture);
            RemoteDigitalInputPollIntervalMilliseconds = configuration.DigitalInputPollIntervalMilliseconds.ToString(CultureInfo.InvariantCulture);
            RemotePeriodicVerificationIntervalSeconds = configuration.PeriodicVerificationIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            RemoteLimitSwitchDebounceMilliseconds = configuration.LimitSwitchDebounceMilliseconds.ToString(CultureInfo.InvariantCulture);
            RemoteEnableDigitalInputPolling = configuration.EnableDigitalInputPolling;
            RemoteEnablePeriodicVerificationWhileMoving = configuration.EnablePeriodicVerificationWhileMoving;
            RemoteUseNormallyClosedLimitSwitches = configuration.UseNormallyClosedLimitSwitches;
            RemoteIgnorePhysicalLimitSwitches = configuration.IgnorePhysicalLimitSwitches;
            RemoteRestartOnFailureWaitTimeSeconds = configuration.RestartOnFailureWaitTimeSeconds.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _suppressRemoteConfigurationDirty = false;
        }

        ApplyRemoteConfigurationSnapshot(configuration);

        if (markClean)
        {
            IsRemoteConfigurationDirty = false;
        }
    }

    private void MarkConfigurationDirty()
    {
        if (_suppressConfigurationDirty || !_configurationEditorInitialized)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(ConfigurationErrorMessage))
        {
            ConfigurationErrorMessage = null;
        }

        ConfigurationSuccessMessage = null;

        if (!IsConfigurationDirty)
        {
            IsConfigurationDirty = true;
        }
    }

    private void MarkRemoteConfigurationDirty()
    {
        if (_suppressRemoteConfigurationDirty)
        {
            return;
        }

        RemoteConfigurationErrorMessage = null;
        RemoteConfigurationSuccessMessage = null;

        if (!IsRemoteConfigurationDirty)
        {
            IsRemoteConfigurationDirty = true;
        }
    }

    private double? GetConfiguredWatchdogTimeoutSeconds()
        => _remoteConfigurationSnapshot?.SafetyWatchdogTimeoutSeconds
            ?? _options.SafetyWatchdogTimeoutSeconds;

    private void ApplyRemoteConfigurationSnapshot(RoofConfigurationResponse configuration)
    {
        _remoteConfigurationSnapshot = configuration;
        _options.SafetyWatchdogTimeoutSeconds = configuration.SafetyWatchdogTimeoutSeconds;

        OnPropertyChanged(nameof(SafetyWatchdogTimeoutDisplay));
        OnPropertyChanged(nameof(WatchdogSecondaryText));
        OnPropertyChanged(nameof(WatchdogPercentage));
    }

    private static bool TryParsePositiveInt(string value, string fieldName, out int parsedValue, out string? errorMessage)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedValue) && parsedValue > 0)
        {
            errorMessage = null;
            return true;
        }

        errorMessage = $"{fieldName} must be a positive whole number.";
        parsedValue = 0;
        return false;
    }

    private static bool TryParseNonNegativeDouble(string value, string fieldName, out double parsedValue, out string? errorMessage)
    {
        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsedValue) && parsedValue >= 0)
        {
            errorMessage = null;
            return true;
        }

        errorMessage = $"{fieldName} must be a valid number greater than or equal to zero.";
        parsedValue = 0d;
        return false;
    }

    private static int NormalizeStatusRank(string? status) => status?.ToLowerInvariant() switch
    {
        "unhealthy" => 0,
        "degraded" => 1,
        "healthy" => 2,
        _ => -1
    };

    private static Color GetHealthStatusBackground(string? status) => status?.ToLowerInvariant() switch
    {
        "healthy" => Color.FromArgb("#198754"),
        "degraded" => Color.FromArgb("#ffc107"),
        "unhealthy" => Color.FromArgb("#dc3545"),
        _ => Color.FromArgb("#6c757d")
    };

    private static Color GetHealthStatusText(string? status) => status?.ToLowerInvariant() switch
    {
        "degraded" => Color.FromArgb("#212529"),
        _ => Colors.White
    };


    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_hasInitialized)
        {
            return;
        }

        _hasInitialized = true;
        await RefreshStatusAsync(true, cancellationToken).ConfigureAwait(false);
        await FetchRemoteConfigurationAsync(showSuccessMessage: false, cancellationToken).ConfigureAwait(false);
        StartPolling();
    }

    public async Task RefreshStatusAsync(bool initialLoad = false, CancellationToken cancellationToken = default)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var shouldPrompt = false;

        try
        {
            if (initialLoad)
            {
                await MainThread.InvokeOnMainThreadAsync(() => IsBusy = true);
            }

            var result = await _apiClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsSuccessful)
            {
                await HandleStatusAsync(result.Value).ConfigureAwait(false);
                UpdateServiceBanner(isError: false, title: null, message: null);
                _serviceUnavailableNotified = false;
            }
            else
            {
                await HandleServiceFailureAsync(result.Error!).ConfigureAwait(false);
                shouldPrompt = true;
            }
        }
        finally
        {
            if (initialLoad)
            {
                await MainThread.InvokeOnMainThreadAsync(() => IsBusy = false);
            }

            _refreshLock.Release();
        }

        if (shouldPrompt)
        {
            await MaybePromptAsync().ConfigureAwait(false);
        }
    }

    private async Task ExecuteCommandAsync(Func<Task<Result<RoofStatusResponse>>> command, string notificationTitle, string successMessage, string failureMessage)
    {
        if (!await _refreshLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        var shouldPrompt = false;

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => IsBusy = true);
            var result = await command().ConfigureAwait(false);
            if (result.IsSuccessful)
            {
                await HandleStatusAsync(result.Value).ConfigureAwait(false);
                AddNotification(notificationTitle, successMessage, NotificationLevel.Info);
            }
            else
            {
                await HandleServiceFailureAsync(result.Error!).ConfigureAwait(false);
                AddNotification("Error", failureMessage, NotificationLevel.Error);
                shouldPrompt = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing roof controller command");
            await HandleServiceFailureAsync(ex).ConfigureAwait(false);
            AddNotification("Error", failureMessage, NotificationLevel.Error);
            shouldPrompt = true;
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() => IsBusy = false);
            _refreshLock.Release();
        }

        if (shouldPrompt)
        {
            await MaybePromptAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleStatusAsync(RoofStatusResponse status)
    {
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _consecutiveFailures = 0;
            IsServiceAvailable = true;
            LastErrorMessage = null;
            CurrentStatus = status.Status;
            IsMoving = status.IsMoving;
            LastStopReason = status.LastStopReason;
            LastTransitionUtc = status.LastTransitionUtc;
            IsWatchdogActive = status.IsWatchdogActive;
            WatchdogSecondsRemaining = status.WatchdogSecondsRemaining ?? 0d;

            if (_previousStatus != status.Status)
            {
                AddNotification("Status", $"Roof is now {StatusText}", NotificationLevel.Info);
            }

            if (WasEmergencyStop && _previousStopReason != LastStopReason)
            {
                AddNotification("Safety", "Emergency stop triggered", NotificationLevel.Error);
            }

            _previousStatus = status.Status;
            _previousStopReason = status.LastStopReason;

            OpenCommand.NotifyCanExecuteChanged();
            CloseCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            ClearFaultCommand.NotifyCanExecuteChanged();

            OnPropertyChanged(nameof(HasFault));
            OnPropertyChanged(nameof(WasEmergencyStop));
            OnPropertyChanged(nameof(LastStopReasonLabel));
            OnPropertyChanged(nameof(ShowStopBadge));
            OnPropertyChanged(nameof(StopBadgeColor));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HealthStatus));
            OnPropertyChanged(nameof(MovementStatus));
            OnPropertyChanged(nameof(IsInitialized));
            OnPropertyChanged(nameof(InitializationBadgeColor));
            OnPropertyChanged(nameof(InitializationBadgeText));
            OnPropertyChanged(nameof(LastTransitionDisplay));
            OnPropertyChanged(nameof(StatusBadgeColor));
            OnPropertyChanged(nameof(HealthBadgeColor));
            OnPropertyChanged(nameof(MovementBadgeColor));
            OnPropertyChanged(nameof(WatchdogPercentage));
            OnPropertyChanged(nameof(WatchdogStatusText));
            OnPropertyChanged(nameof(WatchdogSecondaryText));
        });
    }

    private async Task HandleServiceFailureAsync(Exception error)
    {
        Interlocked.Increment(ref _consecutiveFailures);
        _logger.LogError(error, "Roof controller API error");
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            IsServiceAvailable = false;
            LastErrorMessage = error.Message;
            ShowServiceBanner = true;
            ServiceBannerTitle = "Service unavailable";
            ServiceBannerMessage = BuildServiceBannerMessage(error);
            ServiceBannerIsError = true;

            if (!_serviceUnavailableNotified)
            {
                AddNotification("Service", "Roof controller offline", NotificationLevel.Warning);
                _serviceUnavailableNotified = true;
            }

            OpenCommand.NotifyCanExecuteChanged();
            CloseCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
            ClearFaultCommand.NotifyCanExecuteChanged();

            OnPropertyChanged(nameof(HealthStatus));
            OnPropertyChanged(nameof(MovementStatus));
            OnPropertyChanged(nameof(IsInitialized));
            OnPropertyChanged(nameof(InitializationBadgeColor));
            OnPropertyChanged(nameof(InitializationBadgeText));
            OnPropertyChanged(nameof(ShowStopBadge));
            OnPropertyChanged(nameof(StopBadgeColor));
            OnPropertyChanged(nameof(StatusBadgeColor));
            OnPropertyChanged(nameof(HealthBadgeColor));
            OnPropertyChanged(nameof(MovementBadgeColor));
            OnPropertyChanged(nameof(WatchdogStatusText));
            OnPropertyChanged(nameof(WatchdogSecondaryText));
        });
    }

    private async Task MaybePromptAsync()
    {
        if (_promptThreshold <= 0)
        {
            return;
        }

        if (_consecutiveFailures < _promptThreshold)
        {
            return;
        }

        if (!await _promptSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var choice = await _dialogService.ShowConnectivityPromptAsync(
                "Roof Controller Offline",
                $"Unable to reach the roof controller after {_consecutiveFailures} attempts.",
                LastErrorMessage).ConfigureAwait(false);

            switch (choice)
            {
                case ConnectivityPromptResult.Retry:
                    _consecutiveFailures = 0;
                    await RefreshStatusAsync().ConfigureAwait(false);
                    break;
                case ConnectivityPromptResult.Cancel:
                    _consecutiveFailures = 0;
                    break;
                case ConnectivityPromptResult.Exit:
                    await _dialogService.ExitApplicationAsync().ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            _promptSemaphore.Release();
        }
    }

    private void UpdateServiceBanner(bool isError, string? title, string? message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(message))
            {
                ShowServiceBanner = false;
                ServiceBannerTitle = null;
                ServiceBannerMessage = null;
                ServiceBannerIsError = false;
                return;
            }

            ShowServiceBanner = true;
            ServiceBannerTitle = title;
            ServiceBannerMessage = message;
            ServiceBannerIsError = isError;
        });
    }

    private static string BuildServiceBannerMessage(Exception error)
    {
        const string baseMessage = "The roof controller API could not be reached. Check connectivity and try again.";

        if (string.IsNullOrWhiteSpace(error.Message))
        {
            return baseMessage;
        }

        return $"{baseMessage} ({error.Message})";
    }

    private void AddNotification(string title, string message, NotificationLevel level)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = new NotificationItem
            {
                Title = title,
                Message = message,
                Level = level,
                Timestamp = DateTime.Now
            };

            LatestNotification = item;
            _notificationHistory.Insert(0, item);

            const int maxItems = 100;
            while (_notificationHistory.Count > maxItems)
            {
                _notificationHistory.RemoveAt(_notificationHistory.Count - 1);
            }
        });
    }

    partial void OnLatestNotificationChanged(NotificationItem? value)
    {
        OnPropertyChanged(nameof(HasLatestNotification));
    }

    private void OnNotificationHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            OnPropertyChanged(nameof(HasNotificationHistory));
        });
    }

    private void StartPolling()
    {
        StopPolling();
        var interval = TimeSpan.FromSeconds(Math.Max(_options.StatusPollIntervalSeconds, 1));
        _pollingCts = new CancellationTokenSource();
        var token = _pollingCts.Token;

        _pollingTask = Task.Run(async () =>
        {
            await Task.Delay(interval, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RefreshStatusAsync(cancellationToken: token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while polling roof status");
                }

                try
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private void StopPolling()
    {
        if (_pollingCts is null)
        {
            return;
        }

        try
        {
            _pollingCts.Cancel();
            _pollingTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error stopping polling task");
        }
        finally
        {
            _pollingCts.Dispose();
            _pollingCts = null;
            _pollingTask = null;
        }
    }

    public void Dispose()
    {
        StopPolling();
        _refreshLock.Dispose();
        _promptSemaphore.Dispose();
        _pollingCts?.Dispose();
        _notificationHistory.CollectionChanged -= OnNotificationHistoryChanged;
        if (_activeHealthPopup is not null)
        {
            _activeHealthPopup.Closed -= OnHealthPopupClosed;
            _activeHealthPopup = null;
        }
    }

    partial void OnServiceBannerIsErrorChanged(bool value)
    {
        MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(ServiceBannerColor)));
    }
}
