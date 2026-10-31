using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The kiosk's bounded overview: position and commanded motion, the safety conditions with their first reason, compact
/// readings when space permits, and Open, Close and Clear fault while an operator unlocked the kiosk, with the safety
/// override question in their place while it is asked. The buttons stay the same controls while the status changes, so a
/// press is never lost to an update.
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
    private readonly TextBlock _safetyReasons;
    private readonly TextBlock _safetyOpen;
    private readonly Border _question;
    private readonly TextBlock _questionText;

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
        // #207: why the conditions are not safe, and what Open does with them. Both may be cut short: Readings has the
        // whole Safety row and each source.
        _safetyReasons = Abbreviated(KioskTheme.Label(string.Empty, metrics.Small), "safety-reasons", 2);
        _safetyOpen = Abbreviated(KioskTheme.Label(string.Empty, metrics.Small, KioskTheme.Muted), "safety-open", 1);

        _facts = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = metrics.Gap * 2, RowSpacing = metrics.Gap * 2, Margin = new Thickness(0, metrics.Gap * 2, 0, 0) };
        SizeChanged += (_, _) => _facts.IsVisible = Bounds.Height >= metrics.Touch * 6;
        var state = new StackPanel { Spacing = metrics.Gap, Children = { _positionLabel, _position, _commandedLabel, _commanded, _chips, _safetyReasons, _safetyOpen, _statusTime, _facts } };
        Open = KioskTheme.TouchButton("↑  Open roof", "open", metrics, () => _ = console.OpenAsync());
        Close = KioskTheme.TouchButton("↓  Close roof", "close", metrics, () => _ = console.CloseAsync());
        ClearFault = KioskTheme.TouchButton("Clear fault", "clear-fault", metrics, () => _ = console.ClearFaultAsync());
        foreach (var button in new[] { Open, Close, ClearFault })
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.FontWeight = FontWeight.SemiBold;
        }
        // Nothing is offered before the first view.
        KioskTheme.Colour(Open, OpenState = RoofActionState.Disabled);
        KioskTheme.Colour(Close, CloseState = RoofActionState.Disabled);
        _commands = new StackPanel { Spacing = metrics.Gap, Children = { Open, Close, ClearFault } };
        _hint = KioskTheme.Label(string.Empty, metrics.Font, KioskTheme.MutedWeak);
        _hint.Name = "hint";
        _blocked = KioskTheme.Label(string.Empty, metrics.Small, KioskTheme.Muted);
        _blocked.Name = "blocked";

        // #215: asked before Open while the conditions are not safe; Open goes with the override only once confirmed.
        // The same controls throughout, as the motion buttons are, so a touch is never lost to an update.
        var (warningText, warningBackground, warningEdge) = KioskTheme.Colours(KioskNoticeLevel.Warning);
        _questionText = KioskTheme.Label(string.Empty, metrics.Small, warningText);
        _questionText.Name = "safety-override-text";
        ConfirmOverride = KioskTheme.TouchButton($"↑  {RoofSafetyText.OverrideConfirm}", "safety-override-confirm", metrics, () => _ = console.OpenWithSafetyOverrideAsync());
        KioskTheme.Colour(ConfirmOverride, RoofUiPalette.WarningBackground, RoofUiPalette.WarningText, RoofUiPalette.Warning);
        CancelOverride = KioskTheme.TouchButton(RoofSafetyText.OverrideCancel, "safety-override-cancel", metrics, console.CancelSafetyOverride);
        foreach (var button in new[] { ConfirmOverride, CancelOverride })
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.FontWeight = FontWeight.SemiBold;
        }

        // Side by side, so the question fits the card on every screen.
        var answers = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = metrics.Gap };
        answers.Children.Add(ConfirmOverride);
        answers.Children.Add(Place(CancelOverride, 0, 1));

        _question = new Border
        {
            Name = "safety-override",
            Background = warningBackground,
            BorderBrush = warningEdge,
            BorderThickness = new Thickness(metrics.Gap / 2, 1, 1, 1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(metrics.Gap * 1.5),
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = metrics.Gap,
                Children =
                {
                    KioskTheme.Label(RoofSafetyText.OverrideTitle, metrics.Font, warningText, FontWeight.SemiBold),
                    // Many reasons scroll, so the answers are always on the card.
                    new ScrollViewer
                    {
                        Name = "safety-override-scroll",
                        MaxHeight = metrics.Touch * 2.6,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        Content = _questionText
                    },
                    answers
                }
            }
        };
        var controls = new StackPanel { Spacing = metrics.Gap, Children =
        {
            KioskTheme.Label("Motion controls", metrics.Font, KioskTheme.Accent, FontWeight.SemiBold), _question, _commands, _hint, _blocked
        }};
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = metrics.Gap * 2 };
        layout.Children.Add(KioskTheme.Card(state, metrics));
        layout.Children.Add(Place(KioskTheme.Card(controls, metrics), 0, 1));
        Content = layout;
    }

    public Button Open { get; }

    public Button Close { get; }

    public Button ClearFault { get; }

    /// <summary>Sends Open with the safety override, once the question is asked (<see cref="KioskView.SafetyQuestion"/>).</summary>
    public Button ConfirmOverride { get; }

    /// <summary>Answers the safety override question with Cancel: nothing is sent.</summary>
    public Button CancelOverride { get; }

    /// <summary>How Open is drawn (<see cref="RoofActionStates.ForMotion"/>).</summary>
    public RoofActionState OpenState { get; private set; }

    /// <summary>How Close is drawn (<see cref="RoofActionStates.ForMotion"/>).</summary>
    public RoofActionState CloseState { get; private set; }

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
        if (status?.Safety is { } safety)
            _chips.Children.Add(SafetyChip(safety.State, stale));
        var notSafe = status?.Safety is { State: not RoofSafetyState.Safe };
        _safetyReasons.Text = status?.Safety is { } shown ? RoofSafetyText.DescribeFirstReason(shown) : string.Empty;
        _safetyReasons.Foreground = stale ? KioskTheme.MutedWeak
            : status?.Safety?.State == RoofSafetyState.Unsafe ? KioskTheme.Tone(KioskNoticeLevel.Danger)
            : KioskTheme.Tone(KioskNoticeLevel.Warning);
        _safetyReasons.IsVisible = notSafe && _safetyReasons.Text.Length > 0;
        _safetyOpen.Text = status?.Safety is { } open ? RoofSafetyText.DescribeOpen(open) : string.Empty;
        _safetyOpen.IsVisible = notSafe;
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
                ("input-open-limit", "Open limit", Input(status.IsOpenLimitActive)),
                ("input-closed-limit", "Closed limit", Input(status.IsClosedLimitActive)),
                ("input-drive-running", "Drive running", Input(status.IsAtSpeed)),
                ("input-drive-fault", "Drive fault", Input(status.IsDriveFaultActive)),
                ("input-health", "Safety inputs", RoofStatusText.DescribeInputs(status)),
                ("relay-read-back", "Relay read-back", RoofStatusText.DescribeRelays(status))
            };
            for (var i = 0; i < values.Length; i++)
            {
                var value = KioskTheme.Label(values[i].Item3, _metrics.Font);
                value.Name = values[i].Item1;
                var fact = new StackPanel { Children = { KioskTheme.Label(values[i].Item2, _metrics.Small, KioskTheme.Muted), value } };
                _facts.Children.Add(Place(fact, i / 2, i % 2));
            }

            string Input(bool? active)
            {
                if (status.InputsHealthy && !stale) return RoofStatusText.DescribeInput(active);
                // SnapshotUtc advances even while the controller retains cached raw inputs. IsAtSpeed is a bool,
                // so its default false is no evidence of inactivity before the first successful input read.
                return status.LastSuccessfulInputReadUtc is { } read
                    ? $"{RoofStatusText.DescribeInput(active)} (last read)\n{RoofStatusText.Time(read)}"
                    : "unavailable (no successful read)";
            }
        }
        var asking = view.IsOperator && view.SafetyQuestion is not null;
        _question.IsVisible = asking;
        _questionText.Text = view.SafetyQuestion is { } question ? string.Join("\n", question.Lines) : string.Empty;
        _commands.IsVisible = view.IsOperator && !asking;
        Open.IsEnabled = view.OpenBlock is null;
        Close.IsEnabled = view.CloseBlock is null;
        // Green while offered, bright green while the roof moves that way, neutral while not offered: the reason is below.
        OpenState = Show(Open, RoofActionStates.ForMotion(RoofMotionDirection.Opening, status, view.IsStale, view.OpenBlock), OpenState, "↑  Open roof", "↑  Opening…");
        CloseState = Show(Close, RoofActionStates.ForMotion(RoofMotionDirection.Closing, status, view.IsStale, view.CloseBlock), CloseState, "↓  Close roof", "↓  Closing…");
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
        _blocked.IsVisible = _blocked.Text.Length > 0 && !asking;

    }

    // Recolours a motion button only when its state changes; while running, its label says so as well as its colour.
    private static RoofActionState Show(Button button, RoofActionState state, RoofActionState shown, string label, string running)
    {
        if (state == shown) return state;
        KioskTheme.Colour(button, state);
        KioskTheme.SetText(button, state == RoofActionState.Running ? running : label);
        button.Classes.Set("running", state == RoofActionState.Running);
        button.FontWeight = state == RoofActionState.Disabled ? FontWeight.Normal : FontWeight.SemiBold;
        return state;
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

    // A line that may be cut short on purpose (KioskTheme.Abbreviated): its whole text is on Readings.
    private static TextBlock Abbreviated(TextBlock label, string name, int lines)
    {
        label.Name = name;
        label.MaxLines = lines;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.Classes.Add(KioskTheme.Abbreviated);
        label.IsVisible = false;
        return label;
    }

    private static Control Place(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        return control;
    }

    // The whole reasons are on Readings, with each source.
    private Border SafetyChip(RoofSafetyState state, bool stale) => KioskTheme.Pill(
        $"Safety: {RoofStatusText.DescribeSafetyState(state)}{(stale ? " (last known)" : string.Empty)}",
        stale ? (KioskTheme.Muted, KioskTheme.Badge, KioskTheme.Frame)
            : state switch
            {
                RoofSafetyState.Safe => KioskTheme.SuccessColours,
                RoofSafetyState.Unsafe => KioskTheme.Colours(KioskNoticeLevel.Danger),
                _ => KioskTheme.Colours(KioskNoticeLevel.Warning)
            },
        _metrics,
        "safety");

    private Border Chip(string text, bool on) => KioskTheme.Pill(
        text,
        on ? KioskTheme.Colours(KioskNoticeLevel.Info) : (KioskTheme.Muted, KioskTheme.Badge, KioskTheme.Frame),
        _metrics);
}
