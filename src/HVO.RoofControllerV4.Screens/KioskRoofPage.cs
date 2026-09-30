using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The kiosk's roof page: the roof's position and what it was commanded to do, its limits, and the controller's status
/// rows; with Open, Close and Clear fault while an operator unlocked the kiosk. The buttons stay the same controls while
/// the status changes, so a press is never lost to an update.
/// </summary>
public sealed class KioskRoofPage : UserControl
{
    public const string LockedHint = "Locked: unlock the kiosk with a PIN to open or close the roof.";

    public const string RoleHint = "Open, Close and Clear fault need the Operator role.";

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
    private readonly StackPanel _notices;
    private readonly Grid _rows;
    private readonly Border _rowsCard;
    private IReadOnlyList<KioskNotice>? _shownNotices;

    public KioskRoofPage(KioskConsole console, KioskMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(metrics);
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

        var state = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = metrics.Gap * 2
        };
        state.Children.Add(_positionLabel);
        state.Children.Add(Place(_position, 1, 0));
        state.Children.Add(Place(_commandedLabel, 0, 1));
        state.Children.Add(Place(_commanded, 1, 1));

        Open = KioskTheme.TouchButton("Open", "open", metrics, () => _ = console.OpenAsync());
        KioskTheme.Colour(Open, RoofUiPalette.OpenButton, RoofUiPalette.OpenButtonText);
        Close = KioskTheme.TouchButton("Close", "close", metrics, () => _ = console.CloseAsync());
        KioskTheme.Colour(Close, RoofUiPalette.CloseButton, RoofUiPalette.CloseButtonText);
        ClearFault = KioskTheme.TouchButton("Clear fault", "clear-fault", metrics, () => _ = console.ClearFaultAsync());
        foreach (var button in new[] { Open, Close, ClearFault })
        {
            button.FontSize = metrics.Large;
            button.MinWidth = metrics.Touch * 2;
            button.FontWeight = FontWeight.SemiBold;
        }

        _commands = new WrapPanel { ItemSpacing = metrics.Gap * 2, LineSpacing = metrics.Gap * 2, Children = { Open, Close, ClearFault } };
        _hint = KioskTheme.Label(string.Empty, metrics.Font, KioskTheme.MutedWeak);
        _hint.Name = "hint";
        _blocked = KioskTheme.Label(string.Empty, metrics.Font, KioskTheme.Muted);
        _blocked.Name = "blocked";
        _notices = new StackPanel { Name = "notices", Spacing = metrics.Gap };
        _rows = new Grid { Name = "status-rows", ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = metrics.Gap * 3, RowSpacing = metrics.Gap / 2 };
        _rowsCard = KioskTheme.Card(_rows, metrics);

        Content = new StackPanel
        {
            Spacing = metrics.Gap * 2,
            Children =
            {
                KioskTheme.Card(new StackPanel { Spacing = metrics.Gap, Children = { state, _chips, _statusTime } }, metrics),
                _commands,
                _hint,
                _blocked,
                _notices,
                _rowsCard
            }
        };
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
        if (status is not null)
        {
            // While the inputs cannot be read, the controller reports the limits from the last read that worked.
            var lastRead = status.InputsHealthy ? string.Empty : " (last read)";
            _chips.Children.Add(Chip($"Open limit{lastRead}: {RoofStatusText.DescribeInput(status.IsOpenLimitActive)}", status.IsOpenLimitActive == true));
            _chips.Children.Add(Chip($"Closed limit{lastRead}: {RoofStatusText.DescribeInput(status.IsClosedLimitActive)}", status.IsClosedLimitActive == true));
            if (status.IsWatchdogActive && status.WatchdogSecondsRemaining is { } watchdog)
            {
                _chips.Children.Add(Chip($"Watchdog: {RoofStatusText.Seconds(watchdog)} left", true));
            }
        }

        _statusTime.Text = status is null ? KioskText.NoStatusYet
            : stale ? $"Last known state, as of {RoofStatusText.Time(status.SnapshotUtc)}. It may not be the roof's state now."
            : $"Status at {RoofStatusText.Time(status.SnapshotUtc)}";
        _statusTime.Foreground = stale ? KioskTheme.Colours(KioskNoticeLevel.Warning).Foreground : KioskTheme.Muted;

        _commands.IsVisible = view.IsOperator;
        Open.IsEnabled = view.OpenBlock is null;
        Close.IsEnabled = view.CloseBlock is null;
        ClearFault.IsEnabled = view.ClearFaultBlock is null;

        _hint.Text = !view.IsUnlocked ? $"{LockedHint} {RoofStopText.AlwaysAvailable}"
            : !view.IsOperator ? $"{RoleHint} {RoofStopText.AlwaysAvailable}"
            : string.Empty;
        _hint.IsVisible = _hint.Text.Length > 0;

        _blocked.Text = view.Busy ?? (view.IsOperator && view.IsStarted ? RoofCommandRules.DescribeMotionBlocks(view.OpenBlock, view.CloseBlock) : null) ?? string.Empty;
        _blocked.IsVisible = _blocked.Text.Length > 0;

        if (!ReferenceEquals(_shownNotices, view.Notices))
        {
            _shownNotices = view.Notices;
            _notices.Children.Clear();
            foreach (var notice in view.Notices)
            {
                _notices.Children.Add(KioskTheme.Banner($"{RoofStatusText.Time(notice.At)}  {notice.Text}", notice.Level, _metrics, "notice"));
            }
        }

        _rows.Children.Clear();
        _rows.RowDefinitions.Clear();
        _rowsCard.IsVisible = status is not null;
        if (status is not null)
        {
            var row = 0;
            foreach (var (label, value) in RoofStatusText.DescribeRows(status))
            {
                _rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                _rows.Children.Add(Place(KioskTheme.Label(label, _metrics.Small, KioskTheme.Muted), row, 0));
                _rows.Children.Add(Place(KioskTheme.Label(value, _metrics.Small), row, 1));
                row++;
            }
        }
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
