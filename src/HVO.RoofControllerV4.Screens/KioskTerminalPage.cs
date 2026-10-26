using Avalonia.Controls;
using Avalonia.Media;

namespace HVO.RoofControllerV4.Screens;

/// <summary>A bounded local-console handoff document; Stop remains in the shell's permanent column.</summary>
public sealed class KioskTerminalPage : UserControl
{
    private readonly KioskPager _document;
    private readonly KioskTerminalExit _exit;
    private readonly KioskMetrics _metrics;
    private readonly ContentControl _actions = new();

    /// <summary>Builds the local exit document with permanent confirmation/cancel actions.</summary>
    public KioskTerminalPage(KioskTerminalExit exit, KioskMetrics metrics)
    {
        _exit = exit;
        _metrics = metrics;
        _document = new KioskPager(metrics, "terminal-pages");
        var page = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = metrics.Gap };
        page.Children.Add(_document);
        Grid.SetRow(_actions, 1);
        page.Children.Add(_actions);
        Content = page;
        exit.Changed += Update;
        Update();
    }

    /// <summary>Shows the current authorization/confirmation state using the existing bounded document.</summary>
    public void Update()
    {
        var items = new List<Control>
        {
            KioskTheme.Label(KioskTerminalExit.Title, _metrics.Large, KioskTheme.Accent, FontWeight.SemiBold),
            KioskTheme.Label("Closes only this kiosk. A fresh Linux login appears; your PIN does not sign in to Linux.", _metrics.Font),
            KioskTheme.Label("The controller stays available. Lease renewal ends: a moving roof stops when its enabled operator lease expires. Stop roof is available here before exiting.", _metrics.Font),
            KioskTheme.Label("Return: sign in to Linux, then run sudo systemctl start hvo-roof-kiosk.service. Linux authorizes this command. The kiosk returns locked. Log out of other terminals when finished.", _metrics.Font)
        };
        if (_exit.Message is { } message) items.Add(KioskTheme.Label(message, _metrics.Font));
        var action = KioskTheme.TouchButton(_exit.IsConfirming ? "Confirm exit" : KioskTerminalExit.Title,
            "terminal-exit", _metrics, () => { if (_exit.IsConfirming) _ = _exit.ConfirmAsync(); else _exit.Ask(); });
        action.IsEnabled = !_exit.IsBusy;
        var actions = new List<Control> { action };
        if (_exit.IsConfirming) actions.Add(KioskTheme.TouchButton("Cancel", "terminal-cancel", _metrics, _exit.Cancel));
        _actions.Content = KioskPageParts.Row(_metrics, [.. actions]);
        _document.SetItems(items);
    }
}
