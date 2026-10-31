using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// A bounded local-console handoff page; Stop remains in the shell's permanent column. The exit action (and its Cancel
/// while confirming) and any answer are at the top of the page, always on screen; what exiting does and how to come back
/// are a framed document below them, paged only when it overflows.
/// </summary>
public sealed class KioskTerminalPage : UserControl
{
    /// <summary>What exiting does.</summary>
    public const string WhatHappens = "Closes only this kiosk. A fresh Linux login appears; your PIN does not sign in to Linux.";

    /// <summary>What happens to the roof.</summary>
    public const string RoofStaysSafe = "The controller stays available. Lease renewal ends: a moving roof stops when its enabled operator lease expires. Stop roof is available here before exiting.";

    /// <summary>How to come back.</summary>
    public const string HowToReturn = "Sign in to Linux, then run sudo systemctl start hvo-roof-kiosk.service. Linux authorizes this command. The kiosk returns locked. Log out of other terminals when finished.";

    private readonly KioskPager _document;
    private readonly KioskTerminalExit _exit;
    private readonly KioskMetrics _metrics;
    private readonly WrapPanel _actions;
    private readonly ContentControl _message = new();

    /// <summary>Builds the local exit page with its confirmation and cancel actions above the explanation.</summary>
    public KioskTerminalPage(KioskTerminalExit exit, KioskMetrics metrics)
    {
        _exit = exit;
        _metrics = metrics;
        _document = new KioskPager(metrics, "terminal-pages");
        _actions = new WrapPanel { Name = "terminal-actions", ItemSpacing = metrics.Gap * 2, LineSpacing = metrics.Gap, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var title = KioskTheme.Label(KioskTerminalExit.Title, metrics.Large, KioskTheme.Accent, FontWeight.SemiBold);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = metrics.Gap * 2 };
        top.Children.Add(title);
        Grid.SetColumn(_actions, 1);
        top.Children.Add(_actions);
        // The explanation is one framed document of headings and paragraphs, so the pager can split its text.
        var document = new Border
        {
            Background = KioskTheme.Surface,
            BorderBrush = KioskTheme.Frame,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(metrics.Gap * 2),
            Child = _document
        };
        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = metrics.Gap * 2 };
        page.Children.Add(top);
        Grid.SetRow(_message, 1);
        page.Children.Add(_message);
        Grid.SetRow(document, 2);
        page.Children.Add(document);
        Content = page;
        _document.SetItems([
            Heading("What happens"), KioskTheme.Label($"{WhatHappens} {RoofStaysSafe}", metrics.Font),
            Heading("To come back"), KioskTheme.Label(HowToReturn, metrics.Font)
        ]);
        exit.Changed += Update;
        Update();
    }

    /// <summary>Shows the current authorization/confirmation state: the actions and the answer, above the explanation.</summary>
    public void Update()
    {
        var action = KioskTheme.TouchButton(_exit.IsConfirming ? "Confirm exit" : KioskTerminalExit.Title,
            "terminal-exit", _metrics, () => { if (_exit.IsConfirming) _ = _exit.ConfirmAsync(); else _exit.Ask(); });
        action.IsEnabled = !_exit.IsBusy;
        if (_exit.IsConfirming) KioskTheme.Colour(action, RoofUiPalette.AccentStrong, RoofUiPalette.Text);
        _actions.Children.Clear();
        _actions.Children.Add(action);
        if (_exit.IsConfirming) _actions.Children.Add(KioskTheme.TouchButton("Cancel", "terminal-cancel", _metrics, _exit.Cancel));
        _message.Content = _exit.Message is { } message
            ? KioskTheme.Banner(message, _exit.IsConfirming || _exit.IsBusy ? KioskNoticeLevel.Info : KioskNoticeLevel.Warning, _metrics, "terminal-message")
            : null;
    }

    private TextBlock Heading(string text) => KioskTheme.Label(text, _metrics.Font, KioskTheme.Accent, FontWeight.SemiBold);
}
