using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The kiosk's bounded overview: position and commanded motion, compact readings when space permits,
/// and Open, Close and Clear fault while an operator unlocked the kiosk. The buttons stay the same controls while
/// the status changes, so a press is never lost to an update.
/// </summary>
public sealed class KioskRoofPage : UserControl
{
    public const string RoleHint = "Open, Close and Clear fault need the Operator role.";

    private readonly KioskWording _wording;
    private readonly KioskMetrics _metrics;
    private readonly TextBlock _positionLabel;
    private readonly TextBlock _position;
    private readonly TextBlock _commandedLabel;
    private readonly TextBlock _commanded;
    private readonly WrapPanel _chips;
    private readonly TextBlock _statusTime;
    private readonly Control _commands;
    private readonly TextBlock _hint;
    private readonly TextBlock _blocked;
    private readonly Grid _facts;

    public KioskRoofPage(KioskConsole console, KioskMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(metrics);
        _wording = console.Wording;
        _metrics = metrics;

        _positionLabel = KioskTheme.Label("Position", metrics.Small, KioskTheme.Muted);
        _position = KioskTheme.Label("Unknown", metrics.Huge, weight: FontWeight.Bold);
        _position.Name = "position";
        _commandedLabel = KioskTheme.Label("Commanded", metrics.Small, KioskTheme.Muted);
        _commanded = KioskTheme.Label("Unknown", metrics.Large, weight: FontWeight.SemiBold);
        _commanded.Name = "commanded";
        _chips = new WrapPanel { ItemSpacing = metrics.Gap, LineSpacing = metrics.Gap };
        _statusTime = KioskTheme.Label(string.Empty, metrics.Small, KioskTheme.Muted);
        _statusTime.Name = "status-time";

        _facts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = metrics.Gap * 2, RowSpacing = metrics.Gap * 2, Margin = new Thickness(0, metrics.Gap * 2, 0, 0) };
        SizeChanged += (_, _) => _facts.IsVisible = Bounds.Height >= metrics.Touch * 6;
        var state = new StackPanel { Spacing = metrics.Gap, Children = { _positionLabel, _position, _commandedLabel, _commanded, _chips, _statusTime, _facts } };
        Open = KioskTheme.TouchButton("↑  Open roof", "open", metrics, () => _ = console.OpenAsync());
        Close = KioskTheme.TouchButton("↓  Close roof", "close", metrics, () => _ = console.CloseAsync());
        ClearFault = KioskTheme.TouchButton("Clear fault", "clear-fault", metrics, () => _ = console.ClearFaultAsync());
        foreach (var button in new[] { Open, Close, ClearFault })
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.FontWeight = FontWeight.SemiBold;
        }
        _commands = new StackPanel { Spacing = metrics.Gap, Children = { Open, Close, ClearFault } };
        _hint = KioskTheme.Label(string.Empty, metrics.Font, KioskTheme.MutedWeak);
        _hint.Name = "hint";
        _blocked = KioskTheme.Label(string.Empty, metrics.Small, KioskTheme.Muted);
        _blocked.Name = "blocked";
        var controls = new StackPanel { Spacing = metrics.Gap, Children =
        {
            KioskTheme.Label("Motion controls", metrics.Font, KioskTheme.Accent, FontWeight.SemiBold), _commands, _hint, _blocked
        }};
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = metrics.Gap * 2 };
        layout.Children.Add(KioskTheme.Card(state, metrics));
        layout.Children.Add(Place(KioskTheme.Card(controls, metrics), 0, 1));
        Content = layout;
    }

    public Button Open { get; }

    public Button Close { get; }

    public Button ClearFault { get; }

    /// <summary>Shows <paramref name="view"/>.</summary>
    public void Update(KioskView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var status = view.Status;
        var stale = view.IsStale && status is not null;
        _positionLabel.Text = stale ? "Position (last known)" : "Position";
        _position.Text = status is null ? "Unknown" : RoofText.DescribePosition(status.Status);
        _position.Foreground = stale ? KioskTheme.MutedWeak : PositionBrush(status);
        _commandedLabel.Text = stale ? "Commanded (last known)" : "Commanded";
        _commanded.Text = status?.CommandedMotion switch
        {
            RoofMotionDirection.Opening => "Opening",
            RoofMotionDirection.Closing => "Closing",
            null => "Unknown",
            _ => "None"
        };

        _chips.Children.Clear();
        if (status is { IsWatchdogActive: true, WatchdogSecondsRemaining: { } watchdog })
            _chips.Children.Add(Chip($"Watchdog: {RoofStatusText.Seconds(watchdog)} left", true));
        _statusTime.Text = status is null ? KioskText.NoStatusYet
            : stale ? $"Last known state: {RoofStatusText.Time(status.SnapshotUtc)}"
            : $"Status at {RoofStatusText.Time(status.SnapshotUtc)}";
        _statusTime.Foreground = stale ? KioskTheme.Tone(KioskNoticeLevel.Warning) : KioskTheme.Muted;

        _facts.Children.Clear();
        if (status is not null)
        {
            var values = new[]
            {
                ("Open limit", RoofStatusText.DescribeInput(status.IsOpenLimitActive)),
                ("Closed limit", RoofStatusText.DescribeInput(status.IsClosedLimitActive)),
                ("Drive running", RoofStatusText.DescribeInput(status.IsAtSpeed)),
                ("Drive fault", RoofStatusText.DescribeInput(status.IsDriveFaultActive)),
                ("Safety inputs", RoofStatusText.DescribeInputs(status)),
                ("Relay read-back", RoofStatusText.DescribeRelays(status))
            };
            for (var i = 0; i < values.Length; i++)
            {
                var fact = new StackPanel { Children = { KioskTheme.Label(values[i].Item1, _metrics.Small, KioskTheme.Muted), KioskTheme.Label(values[i].Item2, _metrics.Font) } };
                _facts.Children.Add(Place(fact, i / 2, i % 2));
            }
        }
        _commands.IsVisible = view.IsOperator;
        Open.IsEnabled = view.OpenBlock is null;
        Close.IsEnabled = view.CloseBlock is null;
        ClearFault.IsEnabled = view.ClearFaultBlock is null;

        _hint.Text = !view.IsUnlocked ? $"{_wording.SignedOutHint} {RoofStopText.AlwaysAvailable}"
            : !view.IsOperator ? $"{RoleHint} {RoofStopText.AlwaysAvailable}"
            : string.Empty;
        _hint.IsVisible = _hint.Text.Length > 0;

        _blocked.Text = view.Busy ?? (view.IsOperator && view.IsStarted
            ? view.IsStale || view.IsUnreachable ? "Live status required."
                : status is not null && RoofCommandRules.HasFault(status) ? "Fault must be cleared. See Readings."
                : RoofCommandRules.DescribeMotionBlocks(view.OpenBlock, view.CloseBlock)
            : null) ?? string.Empty;
        if (_blocked.Text.Length > 100) _blocked.Text = "Motion unavailable. Readings has details.";
        _blocked.IsVisible = _blocked.Text.Length > 0;

    }

    private static IBrush PositionBrush(RoofStatusResponse? status) => status?.Status switch
    {
        RoofControllerStatus.Open => KioskTheme.Brush(RoofUiPalette.Success),
        RoofControllerStatus.Closed => KioskTheme.Text,
        RoofControllerStatus.Opening or RoofControllerStatus.Closing => KioskTheme.Brush(RoofUiPalette.Info),
        RoofControllerStatus.Error => KioskTheme.Brush(RoofUiPalette.Danger),
        RoofControllerStatus.Stopped or RoofControllerStatus.PartiallyOpen or RoofControllerStatus.PartiallyClose => KioskTheme.Brush(RoofUiPalette.Warning),
        _ => KioskTheme.MutedWeak
    };

    private static Control Place(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        return control;
    }

    private Border Chip(string text, bool on) => KioskTheme.Pill(
        text,
        on ? KioskTheme.Colours(KioskNoticeLevel.Info) : (KioskTheme.Muted, KioskTheme.Badge, KioskTheme.Frame),
        _metrics);
}
