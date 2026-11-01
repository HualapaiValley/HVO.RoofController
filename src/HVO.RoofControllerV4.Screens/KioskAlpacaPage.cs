using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The Alpaca page (#220), offered while the controller takes Alpaca commands or has something about them to show
/// (<see cref="KioskView.OffersAlpaca"/>): which commands are on, the controller's Alpaca warnings (the person's idle
/// warning is the summary under the header, on every page), the human Stop hold with <see cref="RoofAlpacaText.ClearHoldLabel"/>, the operator lease, the override grants
/// with <see cref="RoofAlpacaText.RevokeLabel"/>, and the alerts, newest first, with
/// <see cref="RoofAlpacaText.AcknowledgeLabel"/> and, on a refused Close (or Open) whose reasons a grant can lift,
/// <see cref="RoofAlpacaText.GrantCloseLabel"/> (or <see cref="RoofAlpacaText.GrantOpenLabel"/>), which asks first; it is
/// disabled, with the controller's reason under the alert, while no grant of its kind can be made (an Open's in Block
/// mode). Anyone may read it; the actions need a person signed in (the hold, grants and revoking the Operator role). The
/// words are every client's (<see cref="RoofAlpacaText"/>).
/// </summary>
/// <remarks>
/// The document is bounded pages (<see cref="KioskPager"/>), paged only when it overflows. It is built again only when
/// what it holds changes (a grant, an alert, the hold, a button, a message); a countdown or a count changes its text in
/// place, so the buttons stay the same controls while the status changes and a press is never lost to an update. While
/// the grant question is asked, its answers stay below the document, always on the screen.
/// </remarks>
public sealed class KioskAlpacaPage : UserControl
{
    /// <summary>Said when nobody holds the operator lease.</summary>
    public const string NoLease = "No one holds the operator lease.";

    /// <summary>Said when the human Stop hold is clear.</summary>
    public const string NoHold = "No Stop hold: Alpaca commands are not held back by a person's Stop.";

    /// <summary>Said when no override grant is in force.</summary>
    public const string NoGrants = "No override grant is in force.";

    /// <summary>Said when there are no alerts.</summary>
    public const string NoAlerts = "No Alpaca alerts.";

    /// <summary>Said for a controller that does not report its Alpaca command state (one older than the screens).</summary>
    public const string NotReported = "This controller does not report Alpaca commands: it is older than these screens.";

    private readonly KioskAlpacaPanel _panel;
    private readonly KioskConsole _console;
    private readonly KioskMetrics _metrics;
    private readonly KioskPager _document;
    private readonly WrapPanel _answers;
    private readonly Dictionary<string, TextBlock> _texts = [];
    private List<Control> _items = [];
    private string? _structure;
    private bool _asking;
    private KioskView _view;

    public KioskAlpacaPage(KioskAlpacaPanel panel, KioskConsole console, KioskMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(metrics);
        _panel = panel;
        _console = console;
        _metrics = metrics;
        _view = console.View;
        _document = new KioskPager(metrics, "alpaca-pages");
        var title = KioskTheme.Label(RoofAlpacaText.Title, metrics.Large, KioskTheme.Accent, FontWeight.SemiBold);
        title.Name = "alpaca-title";
        _answers = new WrapPanel { Name = "alpaca-answers", ItemSpacing = metrics.Gap * 2, LineSpacing = metrics.Gap, IsVisible = false };
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = metrics.Gap };
        page.Children.Add(title);
        Grid.SetRow(_document, 1);
        page.Children.Add(_document);
        Grid.SetRow(_answers, 2);
        page.Children.Add(_answers);
        page.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _panel.Question is not null)
            {
                e.Handled = true;
                _panel.Dismiss();
            }
        }, RoutingStrategies.Tunnel);
        Content = page;
        panel.Changed += () => Update(_view, force: true);
        Update(_view);
    }

    /// <summary>Shows <paramref name="view"/>, with the panel as it is now.</summary>
    public void Update(KioskView view) => Update(view, force: false);

    private void Update(KioskView view, bool force)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        var rows = Describe(view);
        var structure = string.Join("\n", rows.Select(row => row.Structure));
        if (!force && structure == _structure)
        {
            UpdateTexts(rows);
            return;
        }

        _structure = structure;
        _texts.Clear();
        _items = [.. rows.Select(Build)];
        var asking = _panel.Question is not null;
        _document.SetItems(_items, reset: asking != _asking);
        _asking = asking;
        _answers.Children.Clear();
        if (_panel.Question is { } question)
        {
            for (var i = 0; i < question.Answers.Count; i++)
            {
                var answer = question.Answers[i];
                var button = KioskTheme.TouchButton(answer.Label, $"alpaca-answer-{i}", _metrics, () => _ = answer.Act());
                // An override: the safety override's colours, as Open anyway has.
                KioskTheme.Colour(button, RoofUiPalette.WarningBackground, RoofUiPalette.WarningText, RoofUiPalette.Warning);
                button.FontWeight = FontWeight.SemiBold;
                _answers.Children.Add(button);
            }

            _answers.Children.Add(KioskTheme.TouchButton("Cancel", "alpaca-question-cancel", _metrics, _panel.Dismiss));
        }

        _answers.IsVisible = asking;
    }

    // The same rows: only their words may have moved on (a countdown, a count), changed in place. The pages are measured
    // again only when a changed text is no longer the height it was (or is not on the page shown).
    private void UpdateTexts(IEnumerable<Row> rows)
    {
        var remeasure = false;
        foreach (var row in rows)
        {
            if (!_texts.TryGetValue(row.Name, out var block) || block.Text == row.Text)
            {
                continue;
            }

            var height = block.Bounds.Height;
            block.Text = row.Text;
            if (!block.IsAttachedToVisualTree() || block.Bounds.Width <= 0)
            {
                remeasure = true;
                continue;
            }

            block.Measure(new Size(block.Bounds.Width, double.PositiveInfinity));
            remeasure |= Math.Abs(block.DesiredSize.Height - height) > 0.5;
        }

        if (remeasure)
        {
            _document.SetItems(_items);
        }
    }

    // What the page holds now, as rows: each its name, what it says, and what may be done with it.
    private List<Row> Describe(KioskView view)
    {
        var rows = new List<Row>();
        if (_panel.Busy is { } busy)
        {
            rows.Add(Row.Line("alpaca-busy", busy, KioskTheme.Muted));
        }

        if (_panel.Message is { } message)
        {
            rows.Add(Row.Banner("alpaca-message", message.Text, message.Level));
        }

        if (_panel.Question is { } question)
        {
            rows.Add(Row.Line("alpaca-question-title", question.Title, KioskTheme.Text, _metrics.Large, FontWeight.SemiBold));
            for (var i = 0; i < question.Lines.Count; i++)
            {
                rows.Add(Row.Line($"alpaca-question-{i}", question.Lines[i], KioskTheme.Text));
            }

            return rows;
        }

        if (view.Status is not { } status)
        {
            rows.Add(Row.Line("alpaca-none", KioskText.NoStatusYet, KioskTheme.Muted));
            return rows;
        }

        if (status.Alpaca is not { } alpaca)
        {
            rows.Add(Row.Line("alpaca-none", NotReported, KioskTheme.Muted));
            return rows;
        }

        var person = view.IsUnlocked;
        var operating = view.IsOperator;
        rows.Add(Row.Line("alpaca-mode", RoofAlpacaText.DescribeMode(alpaca.Mode), KioskTheme.Text, weight: FontWeight.SemiBold));
        if (!operating)
        {
            var needs = person ? KioskAlpacaPanel.NeedsOperator : _console.Wording.AlpacaNeedsPerson;
            rows.Add(Row.Line("alpaca-needs", $"{char.ToUpperInvariant(needs[0])}{needs[1..]}.", KioskTheme.MutedWeak, _metrics.Small));
        }

        // The person's idle warning is the summary under the header, on every page (KioskShell); the lease line counts down.
        for (var i = 0; i < alpaca.Warnings.Count; i++)
        {
            rows.Add(Row.Banner($"alpaca-warning-{i}", alpaca.Warnings[i], KioskNoticeLevel.Warning));
        }

        // The Stop hold, and Allow Alpaca again for an operator.
        var hold = RoofAlpacaText.DescribeHold(alpaca.Hold);
        rows.Add(Row.Card(
            "alpaca-hold",
            hold ?? NoHold,
            hold is null ? KioskTheme.Muted : KioskTheme.Tone(KioskNoticeLevel.Warning),
            hold is not null && operating ? [new RowAction("alpaca-clear-hold", RoofAlpacaText.ClearHoldLabel, () => _ = _panel.ClearHoldAsync(), Primary: true)] : [],
            identity: null));

        // The operator lease: who holds it, and that these screens hold it for the person (unless it lapsed, or they
        // stepped away by granting: the summary under the header says so).
        rows.Add(Row.Line("alpaca-lease", RoofAlpacaText.DescribeLease(alpaca.OperatorLease) ?? NoLease, KioskTheme.Text, countdown: true));
        if (view.KeepsOperatorLease && alpaca.OperatorLease is { Held: true } held && held.Holder == view.UnlockedBy)
        {
            rows.Add(Row.Line("alpaca-lease-kept", _console.Wording.KeepsOperatorLease, KioskTheme.Muted, _metrics.Small));
        }

        rows.Add(Row.Line("alpaca-grants-title", "Override grants", KioskTheme.Accent, weight: FontWeight.SemiBold));
        if (alpaca.Grants.Count == 0)
        {
            rows.Add(Row.Line("alpaca-grants-none", NoGrants, KioskTheme.Muted));
        }

        for (var i = 0; i < alpaca.Grants.Count; i++)
        {
            var grant = alpaca.Grants[i];
            var kind = grant.Kind;
            rows.Add(Row.Card(
                $"alpaca-grant-{i}",
                RoofAlpacaText.DescribeGrant(grant),
                KioskTheme.Tone(KioskNoticeLevel.Warning),
                operating ? [new RowAction($"alpaca-revoke-{i}", RoofAlpacaText.RevokeLabel, () => _ = _panel.RevokeAsync(kind), Primary: false)] : [],
                identity: grant.Id));
        }

        rows.Add(Row.Line("alpaca-alerts-title", "Alerts, newest first", KioskTheme.Accent, weight: FontWeight.SemiBold));
        if (alpaca.Alerts.Count == 0)
        {
            rows.Add(Row.Line("alpaca-alerts-none", NoAlerts, KioskTheme.Muted));
        }

        var alerts = alpaca.Alerts.OrderByDescending(alert => alert.LastUtc).ToList();
        for (var i = 0; i < alerts.Count; i++)
        {
            var alert = alerts[i];
            var id = alert.Id;
            var actions = new List<RowAction>();
            string? unavailable = null;
            if (operating && RoofAlpacaText.CanGrantFrom(alert))
            {
                // As the alert is when touched: the client the newest refusal named. Disabled, with why under the alert,
                // while the controller says no grant of its kind can be made (an Open's in Block mode).
                var kind = alert.Command ?? RoofAlpacaCommandKind.Close;
                unavailable = RoofAlpacaText.GrantUnavailable(alert, alpaca);
                actions.Add(new RowAction(
                    $"alpaca-grant-{kind.ToString().ToLowerInvariant()}-{i}",
                    RoofAlpacaText.GrantLabel(kind),
                    () => _panel.AskGrant(Current(id) ?? alert),
                    Primary: true,
                    Enabled: unavailable is null));
            }

            if (person && !alert.Acknowledged)
            {
                actions.Add(new RowAction($"alpaca-acknowledge-{i}", RoofAlpacaText.AcknowledgeLabel, () => _ = _panel.AcknowledgeAsync(alert), Primary: false));
            }

            var text = RoofAlpacaText.DescribeAlert(alert);
            if (alert.Acknowledged)
            {
                text += $" Acknowledged{(alert.AcknowledgedBy is { Length: > 0 } by ? $" by {by}" : string.Empty)}.";
            }

            // Why its grant is disabled is in the card, under the alert, so it is never on another page.
            rows.Add(Row.Card(
                $"alpaca-alert-{i}",
                text,
                alert.Acknowledged ? KioskTheme.Muted : KioskTheme.Tone(KioskNoticeLevel.Warning),
                actions,
                identity: $"{alert.Id}:{alert.Acknowledged}") with { Note = unavailable is null ? null : new RowNote($"alpaca-alert-{i}-unavailable", unavailable) });
        }

        return rows;
    }

    private RoofAlpacaAlert? Current(string id) => _view.Status?.Alpaca?.Alerts.FirstOrDefault(alert => alert.Id == id);

    private Control Build(Row row)
    {
        if (row.Level is { } level)
        {
            var banner = KioskTheme.Banner(row.Text, level, _metrics, row.Name);
            if (banner.Child is TextBlock text)
            {
                _texts[row.Name] = text;
            }

            return banner;
        }

        var label = KioskTheme.Label(row.Text, row.Size ?? _metrics.Font, row.Foreground, row.Weight);
        _texts[row.Name] = label;
        if (row.Actions is null)
        {
            label.Name = row.Name;
            return label;
        }

        // A card: what it is, and beside it at the right what may be done with it, so a card takes little of a page.
        label.Name = $"{row.Name}-text";
        var card = new DockPanel { HorizontalSpacing = _metrics.Gap * 2 };
        if (row.Actions.Count > 0)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = _metrics.Gap * 2, VerticalAlignment = VerticalAlignment.Center };
            foreach (var action in row.Actions)
            {
                var button = KioskTheme.TouchButton(action.Label, action.Name, _metrics, action.Click);
                if (action.Primary)
                {
                    KioskTheme.Colour(button, RoofUiPalette.AccentStrong, RoofUiPalette.Text);
                }

                button.IsEnabled = action.Enabled;

                buttons.Children.Add(button);
            }

            DockPanel.SetDock(buttons, Dock.Right);
            card.Children.Add(buttons);
        }

        if (row.Note is { } note)
        {
            var noted = KioskTheme.Label(note.Text, _metrics.Small, KioskTheme.Muted);
            noted.Name = note.Name;
            card.Children.Add(new StackPanel { Spacing = _metrics.Gap / 2, VerticalAlignment = VerticalAlignment.Center, Children = { label, noted } });
        }
        else
        {
            card.Children.Add(label);
        }

        return new Border
        {
            Name = row.Name,
            BorderBrush = KioskTheme.Frame,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, _metrics.Gap / 2, 0, _metrics.Gap),
            Child = card
        };
    }

    /// <summary>What may be done with a row: its button's name and label, what it does, and whether it may be done now.</summary>
    private sealed record RowAction(string Name, string Label, Action Click, bool Primary, bool Enabled = true);

    /// <summary>A card's note under its text (why its grant is disabled): its name and what it says.</summary>
    private sealed record RowNote(string Name, string Text);

    /// <summary>
    /// A row of the page: a line, a banner (<see cref="Level"/>) or a card with buttons (<see cref="Actions"/>).
    /// <see cref="Structure"/> is what makes it this row: its name, kind, buttons, its card's note and, unless its words
    /// count down (<see cref="Countdown"/>) or it is a grant or an alert (<see cref="Identity"/>), its words. While every
    /// row's is the same, the page is not built again.
    /// </summary>
    private sealed record Row(
        string Name,
        string Text,
        IBrush Foreground,
        KioskNoticeLevel? Level = null,
        IReadOnlyList<RowAction>? Actions = null,
        string? Identity = null,
        bool Countdown = false,
        double? Size = null,
        FontWeight Weight = FontWeight.Normal)
    {
        /// <summary>A card's note under its text, or null.</summary>
        public RowNote? Note { get; init; }

        public string Structure => string.Join(
            "|",
            Name,
            Level?.ToString() ?? "-",
            Identity ?? "-",
            Size?.ToString(CultureInfo.InvariantCulture) ?? "-",
            Weight.ToString(),
            Actions is null ? "-" : string.Join(",", Actions.Select(action => action.Enabled ? action.Name : $"{action.Name}:off")),
            Note is { } note ? $"{note.Name}:{note.Text}" : "-",
            Countdown || Identity is not null ? "-" : Text);

        public static Row Line(string name, string text, IBrush foreground, double? size = null, FontWeight weight = FontWeight.Normal, bool countdown = false)
            => new(name, text, foreground, Countdown: countdown, Size: size, Weight: weight);

        public static Row Banner(string name, string text, KioskNoticeLevel level) => new(name, text, KioskTheme.Text, level);

        public static Row Card(string name, string text, IBrush foreground, IReadOnlyList<RowAction> actions, string? identity)
            => new(name, text, foreground, Actions: actions, Identity: identity);
    }
}
