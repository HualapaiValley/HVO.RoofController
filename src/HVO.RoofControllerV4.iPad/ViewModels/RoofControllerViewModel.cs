using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HVO.RoofControllerV4.iPad.Configuration;
using HVO.RoofControllerV4.iPad.Models;
using HVO.RoofControllerV4.iPad.Popups;
using HVO.RoofControllerV4.iPad.Services;
using HVO.RoofControllerV4.Common.Models;
using HVO.Core.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;

namespace HVO.RoofControllerV4.iPad.ViewModels;

/// <summary>
/// Presentation model for the iPad roof controller experience.
/// </summary>
/// <remarks>
/// Every status snapshot (poll, command result, Stop, lease renewal) passes through <see cref="StatusOrderingGate"/>
/// on the main thread, so results that arrive out of order never overwrite newer ones. Stop has its own path
/// (<see cref="StopCommandCoordinator"/>) that never waits for the poll or for another command.
/// </remarks>
public sealed partial class RoofControllerViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private const int TicksPerStalenessUpdate = 4;
    private const int MaxNotificationItems = 100;

    private static readonly Color SuccessColor = Color.FromArgb("#198754");
    private static readonly Color NeutralColor = Color.FromArgb("#6c757d");
    private static readonly Color DarkColor = Color.FromArgb("#343a40");
    private static readonly Color DangerColor = Color.FromArgb("#dc3545");
    private static readonly Color WarningColor = Color.FromArgb("#f0ad4e");
    private static readonly Color InfoColor = Color.FromArgb("#0d6efd");

    private readonly IRoofControllerApiClient _apiClient;
    private readonly RoofControllerApiOptions _options;
    private readonly RoofControllerConnection _connection;
    private readonly StatusOrderingGate _statusGate;
    private readonly StopCommandCoordinator _stopCoordinator;
    private readonly LeaseRenewalTracker _leaseTracker;
    private readonly ILogger<RoofControllerViewModel> _logger;
    private readonly IDialogService _dialogService;
    private readonly IRoofControllerConfigurationService _configurationService;
    private readonly IServiceProvider _serviceProvider;
    private readonly SemaphoreSlim _pollGuard = new(1, 1);
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly SemaphoreSlim _promptSemaphore = new(1, 1);
    private readonly ObservableCollection<NotificationItem> _notificationHistory = new();
    private HealthStatusPopup? _activeHealthPopup;

    private CancellationTokenSource? _pollingCts;
    private CancellationTokenSource? _tickerCts;
    private bool _hasInitialized;
    private volatile bool _isForeground = true;
    private string? _apiKey;
    private bool _apiKeyPersisted;
    private RoofStatusResponse? _latestStatus;
    private long _lastStatusTimestamp;
    private long _leaseObserveFloor;
    private string? _lastFailureDescription;
    private bool _offlineNotified;
    private bool _leaseFailureNotified;
    private RoofControllerStatus _previousStatus = RoofControllerStatus.Unknown;
    private RoofControllerStopReason _previousStopReason = RoofControllerStopReason.None;
    private int _consecutiveFailures;
    private bool _configurationEditorInitialized;
    private bool _isLoadingConfigurationEditor;
    private bool _suppressConfigurationDirty;
    private bool _suppressRemoteConfigurationDirty;
    private RoofConfigurationResponse? _remoteConfigurationSnapshot;

    public RoofControllerViewModel(
        IRoofControllerApiClient apiClient,
        IOptions<RoofControllerApiOptions> options,
        RoofControllerConnection connection,
        StatusOrderingGate statusGate,
        StopCommandCoordinator stopCoordinator,
        LeaseRenewalTracker leaseTracker,
        ILogger<RoofControllerViewModel> logger,
        IDialogService dialogService,
        IRoofControllerConfigurationService configurationService,
        IServiceProvider serviceProvider)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value.Clone();
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _statusGate = statusGate ?? throw new ArgumentNullException(nameof(statusGate));
        _stopCoordinator = stopCoordinator ?? throw new ArgumentNullException(nameof(stopCoordinator));
        _leaseTracker = leaseTracker ?? throw new ArgumentNullException(nameof(leaseTracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        NotificationHistory = new ReadOnlyObservableCollection<NotificationItem>(_notificationHistory);
        _notificationHistory.CollectionChanged += OnNotificationHistoryChanged;
        _stopCoordinator.OutcomeChanged += OnStopOutcomeChanged;

        // The endpoint is usable immediately so Stop can always be sent; the API key is added once secure storage
        // has been read in InitializeAsync.
        ApplyEndpoint(CreateEndpoint(_options, apiKey: null), controllerChanged: true);
    }

    /// <summary>Raised on the main thread when the camera stream source may have changed.</summary>
    public event EventHandler? CameraSourceChanged;

    #region Bindable State

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFaultCommand))]
    private bool isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFaultCommand))]
    private bool isServiceAvailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFaultCommand))]
    private bool isStatusStale = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearFaultCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool isMoving;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFaultCommand))]
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
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFaultCommand))]
    private bool isFaultLatched;

    [ObservableProperty]
    private RoofControllerStopReason? latchedFaultReason;

    [ObservableProperty]
    private bool isIgnoringLimitSwitches;

    [ObservableProperty]
    private bool isUsingPhysicalHardware = true;

    [ObservableProperty]
    private RoofRelayRegisterState relayRegisterState = RoofRelayRegisterState.Unknown;

    [ObservableProperty]
    private bool inputsUnhealthy;

    [ObservableProperty]
    private int consecutiveInputReadFailures;

    [ObservableProperty]
    private double? leaseSecondsRemaining;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFaultCommand))]
    private bool isShuttingDown;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearFaultCommand))]
    private bool isClearFaultInProgress;

    [ObservableProperty]
    private string? controllerName;

    [ObservableProperty]
    private string? controllerInstanceId;

    [ObservableProperty]
    private string? controllerIdentityWarning;

    [ObservableProperty]
    private string? settingsNotice;

    [ObservableProperty]
    private StopOutcome stopOutcome = StopOutcome.Idle;

    [ObservableProperty]
    private bool isAppInForeground = true;

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
    private string configurationApiKey = string.Empty;

    [ObservableProperty]
    private bool configurationRemoveApiKey;

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
    private string remoteMaxConsecutiveInputReadFailures = string.Empty;

    [ObservableProperty]
    private string remoteOperatorLeaseTimeoutSeconds = string.Empty;

    [ObservableProperty]
    private string remoteAtSpeedConfirmationTimeoutSeconds = string.Empty;

    [ObservableProperty]
    private bool remoteEnableDigitalInputPolling;

    [ObservableProperty]
    private bool remoteEnablePeriodicVerificationWhileMoving;

    [ObservableProperty]
    private bool remoteUseNormallyClosedLimitSwitches;

    [ObservableProperty]
    private bool remoteIgnorePhysicalLimitSwitches;

    [ObservableProperty]
    private bool remoteFaultInputActiveHigh;

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

    partial void OnConfigurationBaseUrlChanged(string value)
    {
        OnPropertyChanged(nameof(ConfigurationUsesHttp));
        MarkConfigurationDirty();
    }

    partial void OnConfigurationCameraStreamUrlChanged(string value) => MarkConfigurationDirty();

    partial void OnConfigurationApiKeyChanged(string value) => MarkConfigurationDirty();

    partial void OnConfigurationRemoveApiKeyChanged(bool value) => MarkConfigurationDirty();

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

    partial void OnRemoteMaxConsecutiveInputReadFailuresChanged(string value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteOperatorLeaseTimeoutSecondsChanged(string value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteAtSpeedConfirmationTimeoutSecondsChanged(string value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteEnableDigitalInputPollingChanged(bool value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteEnablePeriodicVerificationWhileMovingChanged(bool value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteUseNormallyClosedLimitSwitchesChanged(bool value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteIgnorePhysicalLimitSwitchesChanged(bool value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteFaultInputActiveHighChanged(bool value) => MarkRemoteConfigurationDirty();

    partial void OnRemoteRestartOnFailureWaitTimeSecondsChanged(string value)
    {
        OnPropertyChanged(nameof(RemoteRestartOnFailureWaitTimeDisplay));
        OnPropertyChanged(nameof(HasRemoteRestartOnFailureWaitTime));
    }

    partial void OnRemoteConfigurationErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasRemoteConfigurationError));

    partial void OnRemoteConfigurationSuccessMessageChanged(string? value) => OnPropertyChanged(nameof(HasRemoteConfigurationSuccess));

    partial void OnIsRemoteConfigurationSavingChanged(bool value) => OnPropertyChanged(nameof(IsRemoteConfigurationBusy));

    partial void OnIsRemoteConfigurationRefreshingChanged(bool value) => OnPropertyChanged(nameof(IsRemoteConfigurationBusy));

    partial void OnIsStatusStaleChanged(bool value) => RaiseStatusPropertyChanges();

    partial void OnIsServiceAvailableChanged(bool value) => RaiseStatusPropertyChanges();

    partial void OnControllerIdentityWarningChanged(string? value) => OnPropertyChanged(nameof(HasControllerIdentityWarning));

    partial void OnSettingsNoticeChanged(string? value) => OnPropertyChanged(nameof(HasSettingsNotice));

    partial void OnStopOutcomeChanged(StopOutcome value)
    {
        OnPropertyChanged(nameof(HasStopOutcome));
        OnPropertyChanged(nameof(IsStopPending));
        OnPropertyChanged(nameof(StopOutcomeTitle));
        OnPropertyChanged(nameof(StopOutcomeMessage));
        OnPropertyChanged(nameof(StopOutcomeColor));
        OnPropertyChanged(nameof(StopOutcomeDetail));
    }

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

    #region Derived State

    public bool HasConfigurationError => !string.IsNullOrWhiteSpace(ConfigurationErrorMessage);

    public bool HasConfigurationSuccess => !string.IsNullOrWhiteSpace(ConfigurationSuccessMessage);

    public bool HasRemoteConfigurationError => !string.IsNullOrWhiteSpace(RemoteConfigurationErrorMessage);

    public bool HasRemoteConfigurationSuccess => !string.IsNullOrWhiteSpace(RemoteConfigurationSuccessMessage);

    public bool IsRemoteConfigurationBusy => IsRemoteConfigurationSaving || IsRemoteConfigurationRefreshing;

    /// <summary>True once any status snapshot has been accepted from the active controller.</summary>
    public bool HasStatus => _latestStatus is not null;

    public bool HasFault => IsFaultLatched || CurrentStatus == RoofControllerStatus.Error;

    public bool WasEmergencyStop => IsSafetyStopReason(LastStopReason);

    public string LastStopReasonLabel => LastStopReason switch
    {
        RoofControllerStopReason.None => string.Empty,
        RoofControllerStopReason.EmergencyStop or RoofControllerStopReason.SafetyWatchdogTimeout => "Emergency",
        RoofControllerStopReason.NormalStop or RoofControllerStopReason.StopButtonPressed => "Normal",
        RoofControllerStopReason.LimitSwitchReached => "Limit",
        _ => "Safety"
    };

    public string LastStopReasonText => DescribeStopReason(LastStopReason);

    public Color InitializationBadgeColor => !HasStatus
        ? NeutralColor
        : IsStatusStale ? WarningColor : IsInitialized ? SuccessColor : NeutralColor;

    public string InitializationBadgeText => !HasStatus
        ? "No status"
        : IsStatusStale ? "Status stale" : IsInitialized ? "Initialized" : "Initializing…";

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

            if (!HasStatus)
            {
                return "Checking…";
            }

            if (IsStatusStale || !IsServiceAvailable)
            {
                return "Unknown (status not current)";
            }

            if (HasSafetyWarning)
            {
                return "Attention required";
            }

            if (HealthReport is { Status.Length: > 0 } report)
            {
                return report.Status;
            }

            return "Healthy";
        }
    }

    public string MovementStatus => IsMoving ? "In Progress" : "Idle";

    public bool IsInitialized => _latestStatus is { } status
        && (status.StatusVersion == 0 || status.IsInitialized)
        && CurrentStatus is not (RoofControllerStatus.Unknown or RoofControllerStatus.NotInitialized);

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

    /// <summary>Status colours are only shown for current status; stale or missing status is neutral.</summary>
    public Color StatusBadgeColor => !IsStatusCurrent ? DarkColor : CurrentStatus switch
    {
        RoofControllerStatus.Open => SuccessColor,
        RoofControllerStatus.Closed => NeutralColor,
        RoofControllerStatus.Opening or RoofControllerStatus.Closing => Color.FromArgb("#0dcaf0"),
        RoofControllerStatus.Error => DangerColor,
        _ => DarkColor
    };

    public Color HealthBadgeColor
    {
        get
        {
            if (HasFault)
            {
                return DangerColor;
            }

            if (!IsServiceAvailable || !IsStatusCurrent)
            {
                return NeutralColor;
            }

            return HasSafetyWarning ? WarningColor : SuccessColor;
        }
    }

    public Color MovementBadgeColor => IsMoving ? InfoColor : NeutralColor;

    public bool ShowStopBadge => !string.IsNullOrEmpty(LastStopReasonLabel);

    public Color StopBadgeColor => WasEmergencyStop ? DangerColor : NeutralColor;

    /// <summary>True when a snapshot exists and is recent enough to act on.</summary>
    public bool IsStatusCurrent => HasStatus && !IsStatusStale;

    public string StaleStatusText
    {
        get
        {
            if (_latestStatus is null)
            {
                return "No status has been received from the controller yet. The display does not reflect the roof.";
            }

            var age = Stopwatch.GetElapsedTime(_lastStatusTimestamp);
            return $"Status last received {FormatAge(age)} ago. The roof may have changed since; do not rely on this display.";
        }
    }

    public string FaultBannerText => IsFaultLatched
        ? $"Safety fault latched: {LatchedFaultReasonText}. The roof will not move until the cause is fixed and the fault is cleared."
        : "Fault detected. Review the system and clear the fault when safe.";

    public string LatchedFaultReasonText => LatchedFaultReason is { } reason ? DescribeStopReason(reason) : "reason not reported";

    public string FaultDisplay
    {
        get
        {
            if (IsFaultLatched)
            {
                return $"Latched: {LatchedFaultReasonText}";
            }

            return string.IsNullOrWhiteSpace(LastErrorMessage) ? "—" : LastErrorMessage;
        }
    }

    public bool IsSimulationMode => HasStatus && !IsUsingPhysicalHardware;

    public bool IsRelayRegisterUnverified => RelayRegisterState == RoofRelayRegisterState.Unverified;

    public bool HasSafetyWarning => IsIgnoringLimitSwitches || IsRelayRegisterUnverified || InputsUnhealthy || IsShuttingDown;

    public string RelayRegisterText => RelayRegisterState switch
    {
        RoofRelayRegisterState.Verified => "Register reads as commanded",
        RoofRelayRegisterState.Unverified => "Register NOT verified",
        _ => "Not reported"
    };

    public string InputsUnhealthyText => ConsecutiveInputReadFailures > 0
        ? $"Safety inputs unhealthy ({ConsecutiveInputReadFailures} consecutive read failures). Limit and fault detection may not work."
        : "Safety inputs unhealthy. Limit and fault detection may not work.";

    public bool HasLeaseRemaining => LeaseSecondsRemaining is not null;

    public string LeaseDisplay
    {
        get
        {
            if (LeaseSecondsRemaining is not { } seconds)
            {
                return "No operator lease active";
            }

            var renewal = _leaseTracker.TrackedMotion == RoofMotionDirection.None
                ? "not renewed by this iPad"
                : IsAppInForeground ? "renewed by this iPad while it is open" : "NOT renewed while this app is in the background";
            return $"Operator lease {Math.Max(seconds, 0):F0}s remaining ({renewal})";
        }
    }

    public string ControllerIdentityText => $"{(string.IsNullOrWhiteSpace(ControllerName) ? "Controller" : ControllerName)} at {ActiveControllerHost}";

    public string ControllerInstanceDisplay => string.IsNullOrWhiteSpace(ControllerInstanceId)
        ? "Instance not reported"
        : $"Instance {ShortenInstanceId(ControllerInstanceId)}";

    public bool HasControllerIdentityWarning => !string.IsNullOrWhiteSpace(ControllerIdentityWarning);

    public bool HasSettingsNotice => !string.IsNullOrWhiteSpace(SettingsNotice);

    public string ActiveControllerHost => _connection.Endpoint?.DisplayHost ?? "not configured";

    public bool IsUsingHttp => _connection.Endpoint is { IsHttps: false };

    public string HttpWarningText => "Plain HTTP: the API key and every command cross the network unencrypted. Use an https:// controller URL where possible.";

    public bool HasApiKey => !string.IsNullOrEmpty(_apiKey);

    public bool ShowMissingApiKeyWarning => _hasInitialized && !HasApiKey;

    public string ApiKeyStatusText => !HasApiKey
        ? "No API key is set. The controller will refuse requests until one is entered."
        : _apiKeyPersisted ? "An API key is stored in the iOS Keychain." : "An API key is set for this session only (it could not be stored in the Keychain).";

    public string ConfigurationApiKeyPlaceholder => HasApiKey ? "Stored (leave blank to keep the current key)" : "Enter the controller API key";

    public bool ConfigurationUsesHttp => RoofControllerApiOptions.TryParseHttpUri(ConfigurationBaseUrl, out var uri) && uri.Scheme == Uri.UriSchemeHttp;

    public bool HasStopOutcome => StopOutcome.State != StopState.None;

    public bool IsStopPending => StopOutcome.IsPending;

    public string StopOutcomeTitle => StopOutcome.State switch
    {
        StopState.Sending => "Stop: sending…",
        StopState.Acknowledged => "Stop acknowledged (relay register not verified)",
        StopState.RelayRegisterVerified => "Stop acknowledged; relay register reads off",
        StopState.RelayRegisterUnverified => "STOP NOT VERIFIED",
        StopState.Rejected => "STOP REFUSED",
        StopState.OutcomeUnknown => "STOP OUTCOME UNKNOWN",
        StopState.NotSent => "STOP NOT SENT",
        _ => string.Empty
    };

    public string StopOutcomeMessage => StopOutcome.Message;

    public Color StopOutcomeColor => StopOutcome.State switch
    {
        StopState.Sending => InfoColor,
        StopState.RelayRegisterVerified => SuccessColor,
        StopState.Acknowledged => WarningColor,
        StopState.None => NeutralColor,
        _ => DangerColor
    };

    public string StopOutcomeDetail
    {
        get
        {
            var outcome = StopOutcome;
            if (outcome.State == StopState.None)
            {
                return string.Empty;
            }

            var started = outcome.StartedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var target = outcome.TargetHost ?? "no controller";
            if (outcome.CompletedUtc is not { } completed)
            {
                return $"Pressed {started} · {target}";
            }

            var elapsed = (completed - outcome.StartedUtc).TotalSeconds;
            var attempts = outcome.Attempts == 1 ? "1 attempt" : $"{outcome.Attempts} attempts";
            return $"Pressed {started} · {target} · {attempts} · {elapsed:F1}s";
        }
    }

    public Uri? CameraStreamUri => _connection.Endpoint?.CameraStreamUri;

    public string? CameraStreamUrl => CameraStreamUri?.ToString();

    public bool HasCameraStream => CameraStreamUri is not null;

    public bool HasLatestNotification => LatestNotification is not null;

    public ReadOnlyObservableCollection<NotificationItem> NotificationHistory { get; }

    public bool HasNotificationHistory => NotificationHistory.Count > 0;

    public string ApiBaseUrl => _connection.Endpoint?.ApiBaseUri.ToString() ?? _options.BaseUrl;

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

    public bool RemoteAllowsIgnoringLimitSwitchesOnHardware => _remoteConfigurationSnapshot?.AllowIgnoringLimitSwitchesOnPhysicalHardware == true;

    public string RemoteConfigurationVersionDisplay => _remoteConfigurationSnapshot is { } snapshot
        ? $"Loaded version {snapshot.Version} from {ActiveControllerHost}"
        : "Not loaded";

    public Color ServiceBannerColor => ServiceBannerIsError ? DangerColor : WarningColor;

    #endregion

    #region Roof Commands

    public bool CanOpen() => CanSendMotionCommand() && !HasFault && CurrentStatus is not (RoofControllerStatus.Open or RoofControllerStatus.Opening);

    public bool CanClose() => CanSendMotionCommand() && !HasFault && CurrentStatus is not (RoofControllerStatus.Closed or RoofControllerStatus.Closing);

    public bool CanClearFault() => !IsBusy && IsServiceAvailable && !IsStatusStale && !IsMoving && !IsShuttingDown && !IsClearFaultInProgress && HasFault;

    private bool CanSendMotionCommand() => !IsBusy && IsServiceAvailable && !IsStatusStale && !IsMoving && !IsShuttingDown && !IsClearFaultInProgress;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync()
        => ExecuteCommandAsync("Open", RoofMotionDirection.Opening, cancellationToken => _apiClient.OpenAsync(cancellationToken), "Open accepted; the roof is opening.");

    [RelayCommand(CanExecute = nameof(CanClose))]
    private Task CloseAsync()
        => ExecuteCommandAsync("Close", RoofMotionDirection.Closing, cancellationToken => _apiClient.CloseAsync(cancellationToken), "Close accepted; the roof is closing.");

    [RelayCommand(CanExecute = nameof(CanClearFault))]
    private Task ClearFaultAsync()
    {
        var pulseMs = _options.ClearFaultPulseMs;
        return ExecuteCommandAsync("Clear fault", motion: null, cancellationToken => _apiClient.ClearFaultAsync(pulseMs, cancellationToken), "Clear fault pulse sent.");
    }

    /// <summary>
    /// Stop is always enabled and never waits for the poll or another command. Each press sends; the newest press's
    /// outcome is what the dashboard shows.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task StopAsync()
    {
        _leaseTracker.Clear();
        var ticket = _statusGate.Begin();

        StopOutcome outcome;
        try
        {
            outcome = await _stopCoordinator.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stop coordinator failed unexpectedly");
            AddNotification("Stop", "Stop failed inside the app; its outcome is unknown. Use the physical stop if the roof is moving.", NotificationLevel.Error);
            _ = QueryStatusNowAsync();
            return;
        }

        if (outcome.Snapshot is { } snapshot)
        {
            await ApplyStatusAsync(ticket, snapshot, markAvailable: outcome.Error is null).ConfigureAwait(false);
        }

        var level = outcome.State switch
        {
            StopState.RelayRegisterVerified => NotificationLevel.Info,
            StopState.Acknowledged => NotificationLevel.Warning,
            _ => NotificationLevel.Error
        };
        AddNotification("Stop", outcome.Message, level);

        if (outcome.State != StopState.NotSent)
        {
            _ = QueryStatusNowAsync();
        }
    }

    private async Task ExecuteCommandAsync(
        string operation,
        RoofMotionDirection? motion,
        Func<CancellationToken, Task<Result<RoofStatusResponse>>> send,
        string successMessage)
    {
        if (!await _commandLock.WaitAsync(0).ConfigureAwait(false))
        {
            AddNotification("Command", $"{operation} not sent: another command is still in progress.", NotificationLevel.Warning);
            return;
        }

        try
        {
            await MainThread.InvokeOnMainThreadAsync(() => IsBusy = true).ConfigureAwait(false);

            var ticket = _statusGate.Begin();
            Result<RoofStatusResponse> result;
            try
            {
                result = await send(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = ex;
            }

            if (result.IsSuccessful)
            {
                if (motion is { } requested)
                {
                    Interlocked.Exchange(ref _leaseObserveFloor, ticket.Sequence);
                    _leaseTracker.NoteMotionRequested(requested, result.Value);
                }

                await ApplyStatusAsync(ticket, result.Value, markAvailable: true).ConfigureAwait(false);
                AddNotification("Command", successMessage, NotificationLevel.Info);
                return;
            }

            var failure = RoofControllerApiException.From(result.Error);
            _logger.LogWarning(failure, "{Operation} failed ({Kind}, HTTP {StatusCode}, code {Code})", operation, failure.Kind, failure.StatusCode, failure.RawCode);

            if (failure.RoofStatus is { } snapshot)
            {
                await ApplyStatusAsync(ticket, snapshot, markAvailable: false).ConfigureAwait(false);
            }

            var description = RoofControllerFailureDescriber.Describe(failure, operation);
            if (IsUncertainOutcome(failure))
            {
                // The request may have reached the controller. It is never re-sent; the status query shows what happened.
                if (motion is { } requested)
                {
                    Interlocked.Exchange(ref _leaseObserveFloor, ticket.Sequence);
                    _leaseTracker.NoteMotionRequested(requested, null);
                }

                AddNotification(
                    "Command",
                    $"{description} The outcome is unknown and the request was not re-sent; checking the controller status.",
                    NotificationLevel.Warning);
                _ = QueryStatusNowAsync();
            }
            else
            {
                AddNotification("Command", description, NotificationLevel.Error);
            }
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() => IsBusy = false).ConfigureAwait(false);
            _commandLock.Release();
        }
    }

    private static bool IsUncertainOutcome(RoofControllerApiException failure)
        => failure.IsConnectivityFailure
            || failure.Kind is RoofControllerFailureKind.InvalidResponse
            || (failure.StatusCode is >= 500 && failure.ErrorCode is null or RoofControllerErrorCode.Unknown);

    [RelayCommand]
    private void DismissControllerIdentityWarning() => ControllerIdentityWarning = null;

    [RelayCommand]
    private void DismissSettingsNotice() => SettingsNotice = null;

    #endregion

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
    private async Task CloseHealthDialogAsync()
    {
        HealthStatusPopup? popup = null;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (!IsHealthDialogOpen)
            {
                return;
            }

            IsHealthDialogOpen = false;
            HealthDialogError = null;
            popup = _activeHealthPopup;
        }).ConfigureAwait(false);

        if (popup is not null)
        {
            await MainThread.InvokeOnMainThreadAsync(async () => await popup.CloseAsync()).ConfigureAwait(false);
        }
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
                var popup = _serviceProvider.GetRequiredService<HealthStatusPopup>();
                if (popup.BindingContext is not HealthStatusPopupViewModel viewModel)
                {
                    throw new InvalidOperationException("Health status popup view model was not provided.");
                }

                viewModel.Dashboard = this;
                _activeHealthPopup = popup;
                _activeHealthPopup.Closed += OnHealthPopupClosed;
                (Shell.Current ?? throw new InvalidOperationException("Shell is not initialized.")).ShowPopup(_activeHealthPopup);
            }).ConfigureAwait(false);
        }
    }

    private void OnHealthPopupClosed(object? sender, EventArgs e)
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
            else
            {
                await HandleHealthDialogErrorAsync(result.Error).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await HandleHealthDialogErrorAsync(ex).ConfigureAwait(false);
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                IsHealthDialogLoading = false;
            }).ConfigureAwait(false);
        }
    }

    private async Task HandleHealthDialogErrorAsync(Exception? error)
    {
        var failure = RoofControllerApiException.From(error);
        _logger.LogWarning(failure, "Failed to retrieve health report ({Kind}, HTTP {StatusCode})", failure.Kind, failure.StatusCode);

        var message = RoofControllerFailureDescriber.Describe(failure, "Health check");

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            HealthDialogError = message;
        }).ConfigureAwait(false);
    }

    #endregion

    #region Local Settings

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

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                // Unsaved edits survive switching tabs.
                if (IsConfigurationDirty)
                {
                    return;
                }

                ConfigurationSuccessMessage = null;
                ConfigurationErrorMessage = !loadResult.IsSuccessful && loadResult.Error is not null
                    ? $"Failed to load saved configuration. Using current runtime values. {loadResult.Error.Message}"
                    : null;

                PopulateConfigurationFields(loadResult.IsSuccessful ? loadResult.Value : _options, markClean: true);
            }).ConfigureAwait(false);

            if (!IsRemoteConfigurationDirty)
            {
                await FetchRemoteConfigurationAsync(showSuccessMessage: false, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _isLoadingConfigurationEditor = false;
            _configurationEditorInitialized = true;
        }
    }

    public bool CanSaveConfiguration() => !IsConfigurationSaving && IsConfigurationDirty;

    /// <summary>
    /// Saves and applies the local settings. Runs on the UI thread from start to finish (no ConfigureAwait(false)),
    /// and applies the controller URL, camera URL and API key together, or not at all.
    /// </summary>
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

        if (!TryParsePositiveInt(ConfigurationStatusPollIntervalSeconds, "Status poll interval", out var pollInterval, out var parseError)
            || !TryParsePositiveInt(ConfigurationClearFaultPulseMs, "Clear fault pulse", out var clearFaultPulse, out parseError)
            || !TryParsePositiveInt(ConfigurationRequestRetryCount, "Request retry count", out var retryCount, out parseError)
            || !TryParsePositiveInt(ConfigurationFailurePromptThreshold, "Failure prompt threshold", out var promptThreshold, out parseError))
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

        var enteredKey = string.IsNullOrWhiteSpace(ConfigurationApiKey) ? null : ConfigurationApiKey.Trim();
        var newKey = enteredKey ?? (ConfigurationRemoveApiKey ? null : _apiKey);
        var keyChanged = !string.Equals(newKey, _apiKey, StringComparison.Ordinal);

        if (!RoofControllerEndpoint.TryCreate(updatedOptions.BaseUrl, updatedOptions.CameraStreamUrl, newKey, out var endpoint, out var endpointError, updatedOptions.RequestRetryCount))
        {
            ConfigurationErrorMessage = endpointError;
            return;
        }

        var previous = _connection.Endpoint;
        var controllerChanged = previous is null
            || previous.ApiBaseUri != endpoint.ApiBaseUri
            || !string.Equals(previous.ApiKey, endpoint.ApiKey, StringComparison.Ordinal);

        if (!endpoint.IsHttps && (keyChanged || previous is null || previous.ApiBaseUri != endpoint.ApiBaseUri))
        {
            var useHttp = await _dialogService.ConfirmAsync(
                "Unencrypted connection",
                $"{endpoint.DisplayHost} uses plain http://. The API key and every roof command will cross the network unencrypted, and anyone on the network can read the key. Use https:// if the controller supports it.",
                "Use HTTP anyway",
                "Cancel");
            if (!useHttp)
            {
                ConfigurationErrorMessage = "Not saved. Change the controller URL to https:// or confirm the unencrypted connection.";
                return;
            }
        }

        if (controllerChanged && (IsMoving || _leaseTracker.TrackedMotion != RoofMotionDirection.None))
        {
            var switchWhileMoving = await _dialogService.ConfirmAsync(
                "Roof may be moving",
                $"The roof at {previous?.DisplayHost ?? "the current controller"} may be moving. After switching, this iPad stops renewing its operator lease and cannot send Stop to that controller. Switch anyway?",
                "Switch",
                "Cancel");
            if (!switchWhileMoving)
            {
                return;
            }
        }

        try
        {
            IsConfigurationSaving = true;

            var saveResult = await _configurationService.SaveAsync(updatedOptions);
            if (!saveResult.IsSuccessful)
            {
                _logger.LogError(saveResult.Error, "Failed to persist roof controller configuration");
                ConfigurationErrorMessage = $"Settings were not saved or applied: {saveResult.Error?.Message ?? "unknown error"}";
                return;
            }

            string? keyNotice = null;
            if (keyChanged)
            {
                var keyResult = await _configurationService.SaveApiKeyAsync(newKey);
                if (keyResult.IsSuccessful)
                {
                    _apiKeyPersisted = newKey is not null;
                }
                else
                {
                    _apiKeyPersisted = false;
                    keyNotice = newKey is null
                        ? " The stored API key could not be removed from the Keychain; it will be used again after a restart."
                        : " The API key could not be stored in the Keychain; it is used until the app closes.";
                }
            }

            _apiKey = newKey;
            _options.CopyFrom(saveResult.Value);
            ApplyEndpoint(endpoint, controllerChanged);
            RestartPolling();

            PopulateConfigurationFields(saveResult.Value, markClean: true);
            ConfigurationSuccessMessage = $"Settings saved and applied. Commands now go to {endpoint.DisplayHost}.{keyNotice}";
            AddNotification("Settings", $"Settings applied; controller {endpoint.DisplayHost}.{keyNotice}", keyNotice is null ? NotificationLevel.Info : NotificationLevel.Warning);

            _ = QueryStatusNowAsync();
            if (controllerChanged)
            {
                _ = FetchRemoteConfigurationAsync(showSuccessMessage: false);
            }
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
            ConfigurationApiKey = string.Empty;
            ConfigurationRemoveApiKey = false;
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

    private static RoofControllerEndpoint? CreateEndpoint(RoofControllerApiOptions options, string? apiKey)
    {
        return RoofControllerEndpoint.TryCreate(options.BaseUrl, options.CameraStreamUrl, apiKey, out var endpoint, out _, options.RequestRetryCount)
            ? endpoint
            : null;
    }

    /// <summary>
    /// Makes <paramref name="endpoint"/> active. In-flight requests to the previous endpoint are cancelled (Stop is
    /// not) and their results are discarded. When the controller itself changed, everything shown about the old
    /// controller is cleared.
    /// </summary>
    private void ApplyEndpoint(RoofControllerEndpoint? endpoint, bool controllerChanged)
    {
        var generation = _connection.Apply(endpoint);
        _statusGate.Reset(generation);
        Interlocked.Exchange(ref _consecutiveFailures, 0);

        if (controllerChanged)
        {
            _leaseTracker.Clear();
            _latestStatus = null;
            _lastStatusTimestamp = 0;
            _remoteConfigurationSnapshot = null;
            _offlineNotified = false;
            ControllerIdentityWarning = null;
            ControllerName = null;
            ControllerInstanceId = null;
            IsServiceAvailable = false;
            IsStatusStale = true;
            CurrentStatus = RoofControllerStatus.Unknown;
            IsMoving = false;
            IsFaultLatched = false;
            LatchedFaultReason = null;
            IsIgnoringLimitSwitches = false;
            IsUsingPhysicalHardware = true;
            RelayRegisterState = RoofRelayRegisterState.Unknown;
            InputsUnhealthy = false;
            ConsecutiveInputReadFailures = 0;
            LeaseSecondsRemaining = null;
            IsShuttingDown = false;
            IsClearFaultInProgress = false;
            IsWatchdogActive = false;
            WatchdogSecondsRemaining = 0;
            LastErrorMessage = null;
            _previousStatus = RoofControllerStatus.Unknown;
            _previousStopReason = RoofControllerStopReason.None;
            RaiseStatusPropertyChanges();
        }

        if (endpoint is null)
        {
            UpdateServiceBanner(isError: true, "Controller not configured", "No valid controller URL is configured. Commands, including Stop, cannot be sent. Set the controller URL on the Configuration tab.");
        }

        RaiseConfigurationPropertyChanges();
        CameraSourceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseConfigurationPropertyChanges()
    {
        OnPropertyChanged(nameof(ApiBaseUrl));
        OnPropertyChanged(nameof(ActiveControllerHost));
        OnPropertyChanged(nameof(ControllerIdentityText));
        OnPropertyChanged(nameof(IsUsingHttp));
        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(ShowMissingApiKeyWarning));
        OnPropertyChanged(nameof(ApiKeyStatusText));
        OnPropertyChanged(nameof(ConfigurationApiKeyPlaceholder));
        OnPropertyChanged(nameof(CameraStreamDisplay));
        OnPropertyChanged(nameof(CameraStreamUri));
        OnPropertyChanged(nameof(CameraStreamUrl));
        OnPropertyChanged(nameof(HasCameraStream));
        OnPropertyChanged(nameof(ConfiguredStatusPollIntervalSeconds));
        OnPropertyChanged(nameof(ConfiguredRequestRetryCount));
        OnPropertyChanged(nameof(ConfiguredFailurePromptThreshold));
        OnPropertyChanged(nameof(ConfiguredClearFaultPulseMs));
        OnPropertyChanged(nameof(SafetyWatchdogTimeoutDisplay));
        OnPropertyChanged(nameof(WatchdogSecondaryText));
        OnPropertyChanged(nameof(WatchdogPercentage));
        OnPropertyChanged(nameof(RemoteConfigurationVersionDisplay));
        OnPropertyChanged(nameof(RemoteAllowsIgnoringLimitSwitchesOnHardware));
    }

    #endregion

    #region Remote Configuration

    public bool CanSaveRemoteConfiguration() => !IsRemoteConfigurationSaving && IsRemoteConfigurationDirty;

    /// <summary>
    /// Sends the edited controller configuration once, with the version it was loaded at. Safety-critical changes
    /// need explicit confirmation. Runs on the UI thread.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveRemoteConfiguration))]
    private async Task SaveRemoteConfigurationAsync()
    {
        RemoteConfigurationErrorMessage = null;
        RemoteConfigurationSuccessMessage = null;

        if (_remoteConfigurationSnapshot is not { } snapshot)
        {
            RemoteConfigurationErrorMessage = "Load the controller configuration (Refresh Snapshot) before saving changes.";
            return;
        }

        if (!TryBuildRemoteConfigurationRequest(snapshot.Version, out var request, out var validationError))
        {
            RemoteConfigurationErrorMessage = validationError;
            return;
        }

        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
        {
            RemoteConfigurationErrorMessage = string.Join(Environment.NewLine, validationResults.Select(r => r.ErrorMessage).Where(static message => !string.IsNullOrWhiteSpace(message)).Distinct());
            return;
        }

        var safetyChanges = DescribeSafetyCriticalChanges(snapshot, request);
        if (safetyChanges.Count > 0)
        {
            var confirmed = await _dialogService.ConfirmAsync(
                "Confirm safety-critical change",
                $"This changes how {ActiveControllerHost} drives or supervises the roof:\n\n• {string.Join("\n• ", safetyChanges)}\n\n"
                + "A wrong value can stop the roof from stopping at its limits or on a fault. Only apply this after checking the wiring on site.",
                "Apply change",
                "Cancel");
            if (!confirmed)
            {
                RemoteConfigurationErrorMessage = "Not applied: the safety-critical change was not confirmed.";
                return;
            }

            request = request with { ConfirmSafetyCriticalChange = true };
        }

        try
        {
            IsRemoteConfigurationSaving = true;
            var result = await _apiClient.UpdateConfigurationAsync(request);
            if (result.IsSuccessful)
            {
                PopulateRemoteConfigurationFields(result.Value, markClean: true);
                RemoteConfigurationSuccessMessage = $"Controller configuration updated (version {result.Value.Version}).";
                AddNotification("Configuration", $"Controller configuration updated to version {result.Value.Version}.", NotificationLevel.Info);
                return;
            }

            var failure = RoofControllerApiException.From(result.Error);
            _logger.LogWarning(failure, "Controller configuration update failed ({Kind}, HTTP {StatusCode}, code {Code})", failure.Kind, failure.StatusCode, failure.RawCode);

            if (failure.ErrorCode == RoofControllerErrorCode.ConfigurationVersionConflict)
            {
                IsRemoteConfigurationDirty = false;
                await FetchRemoteConfigurationAsync(showSuccessMessage: false);
                RemoteConfigurationErrorMessage = "Not applied: the controller configuration was changed elsewhere after it was loaded here. "
                    + "The current values have been reloaded; review them and make your change again.";
                return;
            }

            var description = RoofControllerFailureDescriber.Describe(failure, "Configuration update");
            RemoteConfigurationErrorMessage = IsUncertainOutcome(failure)
                ? $"{description} It may or may not have been applied, and it was not re-sent. Use Refresh Snapshot to check."
                : description;
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
        }).ConfigureAwait(false);

        try
        {
            var generation = _connection.Generation;
            var result = await _apiClient.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccessful)
            {
                var failure = RoofControllerApiException.From(result.Error);
                if (failure.Kind == RoofControllerFailureKind.Cancelled)
                {
                    return;
                }

                _logger.LogWarning(failure, "Failed to retrieve roof controller configuration snapshot ({Kind}, HTTP {StatusCode})", failure.Kind, failure.StatusCode);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    RemoteConfigurationErrorMessage = RoofControllerFailureDescriber.Describe(failure, "Loading the controller configuration");
                }).ConfigureAwait(false);
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (generation != _connection.Generation)
                {
                    return;
                }

                RemoteConfigurationErrorMessage = null;
                PopulateRemoteConfigurationFields(result.Value, markClean: true);
                if (showSuccessMessage)
                {
                    RemoteConfigurationSuccessMessage = $"Loaded configuration version {result.Value.Version} from the controller.";
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await MainThread.InvokeOnMainThreadAsync(() => IsRemoteConfigurationRefreshing = false).ConfigureAwait(false);
        }
    }

    private bool TryBuildRemoteConfigurationRequest(long expectedVersion, out RoofConfigurationRequest request, out string? errorMessage)
    {
        request = default!;

        if (!TryParseNonNegativeDouble(RemoteSafetyWatchdogTimeoutSeconds, "Safety watchdog timeout (seconds)", out var watchdogSeconds, out errorMessage)
            || !TryParsePositiveInt(RemoteOpenRelayId, "Open relay ID", out var openRelayId, out errorMessage)
            || !TryParsePositiveInt(RemoteCloseRelayId, "Close relay ID", out var closeRelayId, out errorMessage)
            || !TryParsePositiveInt(RemoteClearFaultRelayId, "Clear fault relay ID", out var clearFaultRelayId, out errorMessage)
            || !TryParsePositiveInt(RemoteStopRelayId, "Stop relay ID", out var stopRelayId, out errorMessage)
            || !TryParseNonNegativeDouble(RemoteDigitalInputPollIntervalMilliseconds, "Digital input poll interval (ms)", out var pollIntervalMs, out errorMessage)
            || !TryParseNonNegativeDouble(RemotePeriodicVerificationIntervalSeconds, "Verification interval (seconds)", out var verificationSeconds, out errorMessage)
            || !TryParseNonNegativeDouble(RemoteLimitSwitchDebounceMilliseconds, "Limit switch debounce (ms)", out var debounceMs, out errorMessage)
            || !TryParsePositiveInt(RemoteMaxConsecutiveInputReadFailures, "Max consecutive input read failures", out var maxInputFailures, out errorMessage)
            || !TryParseOptionalPositiveDouble(RemoteOperatorLeaseTimeoutSeconds, "Operator lease (seconds)", out var leaseSeconds, out errorMessage)
            || !TryParseOptionalPositiveDouble(RemoteAtSpeedConfirmationTimeoutSeconds, "At-speed confirmation (seconds)", out var atSpeedSeconds, out errorMessage))
        {
            return false;
        }

        request = new RoofConfigurationRequest
        {
            ExpectedVersion = expectedVersion,
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
            IgnorePhysicalLimitSwitches = RemoteIgnorePhysicalLimitSwitches,
            FaultInputActiveHigh = RemoteFaultInputActiveHigh,
            MaxConsecutiveInputReadFailures = maxInputFailures,
            OperatorLeaseTimeoutSeconds = leaseSeconds,
            AtSpeedConfirmationTimeoutSeconds = atSpeedSeconds
        };

        return true;
    }

    private static List<string> DescribeSafetyCriticalChanges(RoofConfigurationResponse current, RoofConfigurationRequest request)
    {
        var changes = new List<string>();

        void AddRelayChange(string name, int currentId, int? requestedId)
        {
            if (requestedId != currentId)
            {
                changes.Add($"{name} relay {currentId} → {requestedId}");
            }
        }

        AddRelayChange("Open", current.OpenRelayId, request.OpenRelayId);
        AddRelayChange("Close", current.CloseRelayId, request.CloseRelayId);
        AddRelayChange("Clear fault", current.ClearFaultRelayId, request.ClearFaultRelayId);
        AddRelayChange("Stop", current.StopRelayId, request.StopRelayId);

        if (request.UseNormallyClosedLimitSwitches != current.UseNormallyClosedLimitSwitches)
        {
            changes.Add(request.UseNormallyClosedLimitSwitches == true
                ? "Limit switches: normally open → normally closed"
                : "Limit switches: normally closed → normally open");
        }

        if (request.FaultInputActiveHigh != current.FaultInputActiveHigh)
        {
            changes.Add(request.FaultInputActiveHigh == true
                ? "Drive fault input: active low → active high"
                : "Drive fault input: active high → active low");
        }

        if (request.IgnorePhysicalLimitSwitches != current.IgnorePhysicalLimitSwitches)
        {
            changes.Add(request.IgnorePhysicalLimitSwitches == true
                ? "IGNORE the physical limit switches (the roof will not stop at its limits in software)"
                : "Stop ignoring the physical limit switches");
        }

        return changes;
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
            RemoteMaxConsecutiveInputReadFailures = configuration.MaxConsecutiveInputReadFailures.ToString(CultureInfo.InvariantCulture);
            RemoteOperatorLeaseTimeoutSeconds = configuration.OperatorLeaseTimeoutSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            RemoteAtSpeedConfirmationTimeoutSeconds = configuration.AtSpeedConfirmationTimeoutSeconds?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            RemoteEnableDigitalInputPolling = configuration.EnableDigitalInputPolling;
            RemoteEnablePeriodicVerificationWhileMoving = configuration.EnablePeriodicVerificationWhileMoving;
            RemoteUseNormallyClosedLimitSwitches = configuration.UseNormallyClosedLimitSwitches;
            RemoteIgnorePhysicalLimitSwitches = configuration.IgnorePhysicalLimitSwitches;
            RemoteFaultInputActiveHigh = configuration.FaultInputActiveHigh;
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
        OnPropertyChanged(nameof(RemoteConfigurationVersionDisplay));
        OnPropertyChanged(nameof(RemoteAllowsIgnoringLimitSwitchesOnHardware));
    }

    #endregion

    #region Lifecycle and Polling

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_hasInitialized)
        {
            return;
        }

        _hasInitialized = true;

        try
        {
            ReportStartupSettings();

            var keyResult = await _configurationService.LoadApiKeyAsync(cancellationToken).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _apiKey = keyResult.ApiKey;
                _apiKeyPersisted = keyResult.IsPersisted;
                if (keyResult.Notice is { } notice)
                {
                    ShowSettingsNotice(notice);
                }

                ApplyEndpoint(CreateEndpoint(_options, _apiKey), controllerChanged: true);
            }).ConfigureAwait(false);

            await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
            await FetchRemoteConfigurationAsync(showSuccessMessage: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Roof controller dashboard start-up did not complete");
            AddNotification("Startup", $"Start-up did not complete: {ex.Message}. Status polling continues.", NotificationLevel.Error);
        }
        finally
        {
            // Polling and the staleness ticker must run even when start-up failed, so the dashboard never
            // keeps showing a status it has stopped checking.
            StartPolling();
            StartTicker();
        }
    }

    /// <summary>
    /// Called by the window lifecycle. The operator lease is only renewed while the app is in the foreground.
    /// </summary>
    public void SetAppInForeground(bool isForeground)
    {
        if (_isForeground == isForeground)
        {
            return;
        }

        _isForeground = isForeground;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsAppInForeground = isForeground;
            OnPropertyChanged(nameof(LeaseDisplay));
        });

        if (!isForeground)
        {
            if (_leaseTracker.TrackedMotion != RoofMotionDirection.None)
            {
                AddNotification(
                    "Lease",
                    "The app is in the background and has stopped renewing the operator lease. The controller stops the roof when the lease runs out.",
                    NotificationLevel.Warning);
            }
        }
        else if (_hasInitialized)
        {
            _ = QueryStatusNowAsync();
        }
    }

    /// <summary>
    /// Polls the status once, unless a poll is already running.
    /// </summary>
    public async Task RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!await _pollGuard.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await QueryStatusCoreAsync(countTowardsPrompt: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pollGuard.Release();
        }
    }

    /// <summary>
    /// Queries the status immediately, without waiting for a running poll. Used after Stop and after commands
    /// whose outcome is unknown.
    /// </summary>
    private async Task QueryStatusNowAsync()
    {
        try
        {
            await QueryStatusCoreAsync(countTowardsPrompt: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Immediate status query failed");
        }
    }

    private async Task QueryStatusCoreAsync(bool countTowardsPrompt, CancellationToken cancellationToken)
    {
        var ticket = _statusGate.Begin();
        Result<RoofStatusResponse> result;
        try
        {
            result = await _apiClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = ex;
        }

        if (result.IsSuccessful)
        {
            await ApplyStatusAsync(ticket, result.Value, markAvailable: true).ConfigureAwait(false);
        }
        else
        {
            await HandleStatusFailureAsync(ticket, RoofControllerApiException.From(result.Error), countTowardsPrompt).ConfigureAwait(false);
        }
    }

    private Task<bool> ApplyStatusAsync(StatusTicket ticket, RoofStatusResponse snapshot, bool markAvailable)
        => MainThread.InvokeOnMainThreadAsync(() => ApplyStatusOnMainThread(ticket, snapshot, markAvailable));

    /// <summary>
    /// Offers a snapshot to the ordering gate and, if it is the newest, shows it. The gate decision and the UI update
    /// happen together on the main thread, so accepted snapshots are always displayed in order.
    /// </summary>
    private bool ApplyStatusOnMainThread(StatusTicket ticket, RoofStatusResponse snapshot, bool markAvailable)
    {
        var acceptance = _statusGate.TryAccept(ticket, snapshot);
        if (!acceptance.Accepted)
        {
            return false;
        }

        _latestStatus = snapshot;
        _lastStatusTimestamp = Stopwatch.GetTimestamp();

        // Snapshots requested before the latest Open/Close cannot describe that motion; they must not end lease tracking.
        if (ticket.Sequence > Interlocked.Read(ref _leaseObserveFloor))
        {
            _leaseTracker.Observe(snapshot);
        }

        var isExtendedStatus = snapshot.StatusVersion > 0;
        CurrentStatus = snapshot.Status;
        IsMoving = snapshot.IsMoving;
        LastStopReason = snapshot.LastStopReason;
        LastTransitionUtc = snapshot.LastTransitionUtc;
        IsWatchdogActive = snapshot.IsWatchdogActive;
        WatchdogSecondsRemaining = snapshot.WatchdogSecondsRemaining ?? 0d;
        IsFaultLatched = snapshot.IsFaultLatched;
        LatchedFaultReason = snapshot.LatchedFaultReason;
        IsIgnoringLimitSwitches = snapshot.IsIgnoringPhysicalLimitSwitches;
        IsUsingPhysicalHardware = snapshot.IsUsingPhysicalHardware;
        RelayRegisterState = snapshot.RelayRegisterState;
        InputsUnhealthy = isExtendedStatus && !snapshot.InputsHealthy;
        ConsecutiveInputReadFailures = snapshot.ConsecutiveInputReadFailures;
        LeaseSecondsRemaining = snapshot.LeaseSecondsRemaining;
        IsShuttingDown = snapshot.IsShuttingDown;
        IsClearFaultInProgress = snapshot.IsClearFaultInProgress;
        ControllerName = _statusGate.ControllerName;
        ControllerInstanceId = _statusGate.ControllerInstanceId;
        LastErrorMessage = snapshot.LastError;

        if (acceptance.InstanceChanged)
        {
            var previousName = string.IsNullOrWhiteSpace(acceptance.PreviousControllerName) ? "the controller" : acceptance.PreviousControllerName;
            var warning = $"{previousName} at {ActiveControllerHost} restarted or was replaced (instance {ShortenInstanceId(acceptance.PreviousInstanceId)} → {ShortenInstanceId(snapshot.ControllerInstanceId)}). "
                + "Any motion or lease from before the restart has ended; check the roof before sending commands.";
            ControllerIdentityWarning = warning;
            _leaseTracker.Clear();
            AddNotification("Controller", warning, NotificationLevel.Warning);
        }

        if (_previousStatus != snapshot.Status)
        {
            AddNotification("Status", $"Roof is now {StatusText}", NotificationLevel.Info);
        }

        if (WasEmergencyStop && _previousStopReason != LastStopReason)
        {
            AddNotification("Safety", $"Safety stop: {DescribeStopReason(LastStopReason)}", NotificationLevel.Error);
        }

        _previousStatus = snapshot.Status;
        _previousStopReason = snapshot.LastStopReason;

        if (markAvailable)
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _offlineNotified = false;
            _lastFailureDescription = null;
            IsServiceAvailable = true;
            UpdateServiceBanner(isError: false, title: null, message: null);
        }

        UpdateStaleness();
        RaiseStatusPropertyChanges();
        return true;
    }

    private Task HandleStatusFailureAsync(StatusTicket ticket, RoofControllerApiException failure, bool countTowardsPrompt)
    {
        if (failure.Kind == RoofControllerFailureKind.Cancelled)
        {
            return Task.CompletedTask;
        }

        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            // A failure is only shown if no newer request has succeeded since this one was sent.
            if (!_statusGate.TryAcceptFailure(ticket))
            {
                return;
            }

            if (failure.RoofStatus is { } snapshot)
            {
                ApplyStatusOnMainThread(ticket, snapshot, markAvailable: false);
            }

            var description = RoofControllerFailureDescriber.Describe(failure, "Status update");
            _lastFailureDescription = description;
            IsServiceAvailable = false;

            var (title, hint) = failure switch
            {
                { Kind: RoofControllerFailureKind.NotConfigured } => ("Controller not configured", " Set the controller URL on the Configuration tab."),
                { IsConnectivityFailure: true } => ("Controller unreachable", " The status shown is not current. Stop is still sent when pressed, but may not arrive; use the physical stop if the roof is moving."),
                { Kind: RoofControllerFailureKind.Unauthorized or RoofControllerFailureKind.Forbidden } => ("Access refused by the controller", string.Empty),
                _ => ("Controller reported a problem", string.Empty)
            };
            UpdateServiceBanner(isError: true, title, description + hint);

            if (!_offlineNotified)
            {
                _logger.LogWarning(failure, "Status update failed ({Kind}, HTTP {StatusCode}, code {Code})", failure.Kind, failure.StatusCode, failure.RawCode);
                AddNotification("Service", description, NotificationLevel.Warning);
                _offlineNotified = true;
            }

            if (countTowardsPrompt && failure.IsConnectivityFailure)
            {
                MaybePromptOffline(Interlocked.Increment(ref _consecutiveFailures));
            }
        });
    }

    /// <summary>
    /// Shows the offline prompt without blocking polling. Whatever the operator chooses, the failure count restarts,
    /// so the prompt reappears only after another run of failures.
    /// </summary>
    private void MaybePromptOffline(int failures)
    {
        if (failures < Math.Max(1, _options.ConnectionFailurePromptThreshold))
        {
            return;
        }

        if (!_promptSemaphore.Wait(0))
        {
            return;
        }

        _ = PromptOfflineAsync(failures, _lastFailureDescription);
    }

    private async Task PromptOfflineAsync(int failures, string? detail)
    {
        try
        {
            var choice = await _dialogService.ShowConnectivityPromptAsync(
                "Roof Controller Offline",
                $"Unable to reach the roof controller at {ActiveControllerHost} after {failures} attempts. The app keeps trying in the background. "
                + "Stop is still sent when pressed but may not arrive; use the physical stop if the roof is moving.",
                detail).ConfigureAwait(false);

            Interlocked.Exchange(ref _consecutiveFailures, 0);

            if (choice == ConnectivityPromptResult.Retry)
            {
                await QueryStatusNowAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Offline prompt failed");
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

    private void ReportStartupSettings()
    {
        var startup = _configurationService.StartupLoadResult;
        if (startup.Status != SettingsLoadStatus.Recovered)
        {
            return;
        }

        var backup = startup.BackupPath is null ? string.Empty : $" A copy was kept as {Path.GetFileName(startup.BackupPath)}.";
        ShowSettingsNotice($"The saved settings could not be read ({startup.Problem ?? "unknown problem"}), so the built-in defaults are in use.{backup} Review the Configuration tab.");
    }

    private void ShowSettingsNotice(string notice)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            SettingsNotice = string.IsNullOrWhiteSpace(SettingsNotice) ? notice : $"{SettingsNotice}\n{notice}";
        });
        AddNotification("Settings", notice, NotificationLevel.Warning);
    }

    private void OnStopOutcomeChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() => StopOutcome = _stopCoordinator.Current);
    }

    private void UpdateStaleness()
    {
        var threshold = TimeSpan.FromSeconds(Math.Max((2 * _options.StatusPollIntervalSeconds) + 3, 8));
        IsStatusStale = _latestStatus is null || Stopwatch.GetElapsedTime(_lastStatusTimestamp) > threshold;
        OnPropertyChanged(nameof(StaleStatusText));
    }

    private void RaiseStatusPropertyChanges()
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(IsStatusCurrent));
        OnPropertyChanged(nameof(StaleStatusText));
        OnPropertyChanged(nameof(HasFault));
        OnPropertyChanged(nameof(FaultBannerText));
        OnPropertyChanged(nameof(FaultDisplay));
        OnPropertyChanged(nameof(LatchedFaultReasonText));
        OnPropertyChanged(nameof(WasEmergencyStop));
        OnPropertyChanged(nameof(LastStopReasonLabel));
        OnPropertyChanged(nameof(LastStopReasonText));
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
        OnPropertyChanged(nameof(IsSimulationMode));
        OnPropertyChanged(nameof(IsRelayRegisterUnverified));
        OnPropertyChanged(nameof(RelayRegisterText));
        OnPropertyChanged(nameof(HasSafetyWarning));
        OnPropertyChanged(nameof(InputsUnhealthyText));
        OnPropertyChanged(nameof(HasLeaseRemaining));
        OnPropertyChanged(nameof(LeaseDisplay));
        OnPropertyChanged(nameof(ControllerIdentityText));
        OnPropertyChanged(nameof(ControllerInstanceDisplay));
        OnPropertyChanged(nameof(ShowMissingApiKeyWarning));
    }

    private void StartPolling()
    {
        StopPolling();
        var interval = TimeSpan.FromSeconds(Math.Max(_options.StatusPollIntervalSeconds, 1));
        var cts = new CancellationTokenSource();
        _pollingCts = cts;
        _ = RunPollingAsync(interval, cts.Token);
    }

    private void RestartPolling()
    {
        if (_pollingCts is not null)
        {
            StartPolling();
        }
    }

    private async Task RunPollingAsync(TimeSpan interval, CancellationToken token)
    {
        try
        {
            await Task.Delay(interval, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await RefreshStatusAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while polling roof status");
                }

                await Task.Delay(interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void StopPolling()
    {
        // The loop is not awaited: a poll in progress finishes (or is cancelled) on its own and its result still
        // passes through the ordering gate.
        var cts = Interlocked.Exchange(ref _pollingCts, null);
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private void StartTicker()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _tickerCts, cts)?.Cancel();
        _ = RunTickerAsync(cts.Token);
    }

    private async Task RunTickerAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TickInterval);
        var ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (++ticks % TicksPerStalenessUpdate == 0)
                {
                    MainThread.BeginInvokeOnMainThread(UpdateStaleness);
                }

                if (_leaseTracker.TryBeginRenewal(_isForeground))
                {
                    _ = RenewLeaseAsync();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task RenewLeaseAsync()
    {
        var ticket = _statusGate.Begin();
        RoofStatusResponse? status = null;
        var leaseNotActive = false;

        try
        {
            var result = await _apiClient.RenewLeaseAsync().ConfigureAwait(false);
            if (result.IsSuccessful)
            {
                status = result.Value;
                _leaseFailureNotified = false;
                await ApplyStatusAsync(ticket, status, markAvailable: true).ConfigureAwait(false);
                return;
            }

            var failure = RoofControllerApiException.From(result.Error);
            leaseNotActive = failure.ErrorCode == RoofControllerErrorCode.LeaseNotActive;
            if (failure.RoofStatus is { } snapshot)
            {
                await ApplyStatusAsync(ticket, snapshot, markAvailable: false).ConfigureAwait(false);
            }

            if (!leaseNotActive && failure.Kind != RoofControllerFailureKind.Cancelled)
            {
                _logger.LogWarning(failure, "Lease renewal failed ({Kind}, HTTP {StatusCode}, code {Code})", failure.Kind, failure.StatusCode, failure.RawCode);
                if (!_leaseFailureNotified)
                {
                    _leaseFailureNotified = true;
                    AddNotification(
                        "Lease",
                        $"{RoofControllerFailureDescriber.Describe(failure, "Lease renewal")} If renewals keep failing, the controller stops the roof when the lease runs out.",
                        NotificationLevel.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lease renewal failed unexpectedly");
        }
        finally
        {
            _leaseTracker.CompleteRenewal(status, leaseNotActive);
        }
    }

    #endregion

    #region Camera

    /// <summary>
    /// Resolves the URL the camera view should load. The controller's own camera proxy needs a short-lived ticket
    /// because a WebView cannot send the API key header; any other camera URL is used as configured.
    /// </summary>
    public async Task<CameraStreamResolution> ResolveCameraStreamAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = _connection.Endpoint;
        if (endpoint?.CameraStreamUri is not { } cameraUri)
        {
            return new CameraStreamResolution(null, "No camera stream URL is configured. Set it on the Configuration tab.");
        }

        if (!CameraStreamResolver.TryGetProxyCameraId(cameraUri, endpoint.RootUri, out var cameraId))
        {
            return new CameraStreamResolution(cameraUri, null);
        }

        var ticket = await _apiClient.CreateCameraTicketAsync(cameraId, cancellationToken).ConfigureAwait(false);
        if (!ticket.IsSuccessful)
        {
            return new CameraStreamResolution(null, RoofControllerFailureDescriber.Describe(ticket.Error, "Camera stream"));
        }

        return CameraStreamResolver.TryResolveTicketUrl(endpoint.RootUri, ticket.Value.Url, out var streamUri)
            ? new CameraStreamResolution(streamUri, null)
            : new CameraStreamResolution(null, "The controller returned a camera stream address that does not belong to it.");
    }

    #endregion

    #region Helpers

    private static bool IsSafetyStopReason(RoofControllerStopReason reason) => reason is not (
        RoofControllerStopReason.None
        or RoofControllerStopReason.NormalStop
        or RoofControllerStopReason.StopButtonPressed
        or RoofControllerStopReason.LimitSwitchReached);

    private static string DescribeStopReason(RoofControllerStopReason reason) => reason switch
    {
        RoofControllerStopReason.None => "None",
        RoofControllerStopReason.NormalStop => "Normal stop",
        RoofControllerStopReason.LimitSwitchReached => "Limit switch reached",
        RoofControllerStopReason.EmergencyStop => "Emergency stop",
        RoofControllerStopReason.StopButtonPressed => "Stop requested",
        RoofControllerStopReason.SafetyWatchdogTimeout => "Safety watchdog timeout",
        RoofControllerStopReason.SystemDisposal => "Controller service stopped",
        RoofControllerStopReason.InputReadFailure => "Safety inputs could not be read",
        RoofControllerStopReason.ContradictoryLimitInputs => "Both limit switches reported active",
        RoofControllerStopReason.StartLimitReasserted => "Starting limit switch re-asserted",
        RoofControllerStopReason.DriveFault => "Drive fault",
        RoofControllerStopReason.RelayVerificationFailed => "Relay register verification failed",
        RoofControllerStopReason.OperatorLeaseExpired => "Operator lease expired",
        RoofControllerStopReason.DriveNotRunning => "Drive not confirmed running",
        RoofControllerStopReason.HostShutdown => "Controller host shut down",
        _ => reason.ToString()
    };

    private static string ShortenInstanceId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return "unknown";
        }

        return instanceId.Length <= 8 ? instanceId : instanceId[..8];
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 90)
        {
            return $"{Math.Max(age.TotalSeconds, 0):F0}s";
        }

        return age.TotalMinutes < 90 ? $"{age.TotalMinutes:F0} min" : $"{age.TotalHours:F1} h";
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
        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsedValue)
            && double.IsFinite(parsedValue)
            && parsedValue >= 0)
        {
            errorMessage = null;
            return true;
        }

        errorMessage = $"{fieldName} must be a valid number greater than or equal to zero.";
        parsedValue = 0d;
        return false;
    }

    private static bool TryParseOptionalPositiveDouble(string value, string fieldName, out double? parsedValue, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsedValue = null;
            errorMessage = null;
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed)
            && parsed > 0)
        {
            parsedValue = parsed;
            errorMessage = null;
            return true;
        }

        parsedValue = null;
        errorMessage = $"{fieldName} must be a positive number, or blank to disable it.";
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
        "healthy" => SuccessColor,
        "degraded" => Color.FromArgb("#ffc107"),
        "unhealthy" => DangerColor,
        _ => NeutralColor
    };

    private static Color GetHealthStatusText(string? status) => status?.ToLowerInvariant() switch
    {
        "degraded" => Color.FromArgb("#212529"),
        _ => Colors.White
    };

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

            while (_notificationHistory.Count > MaxNotificationItems)
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

    partial void OnServiceBannerIsErrorChanged(bool value)
    {
        MainThread.BeginInvokeOnMainThread(() => OnPropertyChanged(nameof(ServiceBannerColor)));
    }

    #endregion

    public void Dispose()
    {
        StopPolling();
        Interlocked.Exchange(ref _tickerCts, null)?.Cancel();
        _stopCoordinator.OutcomeChanged -= OnStopOutcomeChanged;
        _notificationHistory.CollectionChanged -= OnNotificationHistoryChanged;
        if (_activeHealthPopup is not null)
        {
            _activeHealthPopup.Closed -= OnHealthPopupClosed;
            _activeHealthPopup = null;
        }
    }
}
