using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>The kiosk's pages.</summary>
public enum KioskPage
{
    Roof,
    Unlock,
    Settings,
    System,
    About,
    Readings,
    Notices,
    StopResult,
    Terminal,
    ChangePin
}

/// <summary>
/// The kiosk's screen, and the Mac app's: a header with where the status comes from and who unlocked the kiosk (or signed
/// in to the app), a bounded safety summary, bottom navigation, the page, and Stop at the right of every page, never disabled
/// and never behind the PIN or the password. A blank screen is black; the touch that wakes it goes no further. The kiosk
/// is unlocked with a PIN (<see cref="PinPage"/>); the app is signed in to with a name and password
/// (<see cref="SignInPage"/>), as <see cref="KioskConsole.Wording"/> says.
/// </summary>
public sealed class KioskShell : UserControl
{
    private readonly KioskConsole _console;
    private readonly KioskMetrics _metrics;
    private readonly TextBlock _title;
    private readonly WrapPanel _badges;
    private readonly StackPanel _banners;
    private readonly ContentControl _page;
    private readonly ScrollViewer _desktopScroller = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly KioskPager _readings;
    private readonly KioskPager _notices;
    private readonly Button _navReadings;
    private readonly Button _navNotices;
    private readonly Button _navRoof;
    private readonly Button _navSettings;
    private readonly Button _navSystem;
    private readonly Button _navAbout;
    private readonly Button _navLock;
    private readonly Button _navPin;
    private readonly Button _navTerminal;
    private readonly KioskTerminalExit? _terminalExit;
    private readonly KioskTerminalPage? _terminalPage;
    private readonly TextBlock _stopMessage;
    private readonly Button _stopDetails;
    private readonly KioskPager _stopResult;
    private readonly Grid _stopColumn;
    private readonly Border _blank;
    private bool _wasUnlocked;
    private KioskView? _shown;

    public KioskShell(KioskConsole console, KioskMetrics metrics, KioskTerminalExit? terminalExit = null)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(metrics);
        _console = console;
        _metrics = metrics;
        _terminalExit = terminalExit;
        _terminalPage = terminalExit is null ? null : new KioskTerminalPage(terminalExit, metrics);
        PinPad = new KioskPinPad(console);
        Settings = new KioskSettingsPanel(console);
        System = new KioskSystemPanel(console);
        RoofPage = new KioskRoofPage(console, metrics);
        PinPage = new KioskPinPage(PinPad, metrics, () => ShowPage(KioskPage.Roof));
        PasswordForm = new KioskPasswordForm(console);
        SignInPage = new KioskSignInPage(PasswordForm, metrics, () => ShowPage(KioskPage.Roof));
        SettingsPage = new KioskSettingsPage(Settings, console, metrics);
        SystemPage = new KioskSystemPage(System, console, metrics);
        AboutPage = new KioskAboutPage(console, System, metrics);
        Background = KioskTheme.Background;
        Foreground = KioskTheme.Text;
        FontSize = metrics.Font;

        // The title and the badges share the header (KioskHeaderPanel): the title (the controller's name) is whole while
        // the badges fit beside it, and keeps at least half the header when they do not; a longer name ends in an
        // ellipsis, and the status card's Controller row shows it whole. The badges wrap onto more lines in what is
        // left. The unlocking person's name is cut short on its pill (see UpdateBadges), so a long one cannot squeeze
        // the title.
        _title = KioskTheme.Label("Roof", metrics.Large, weight: FontWeight.SemiBold);
        _title.Name = "title";
        _title.TextWrapping = TextWrapping.NoWrap;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.Classes.Add(KioskTheme.Abbreviated);
        _badges = new WrapPanel { Name = "badges", ItemSpacing = metrics.Gap, LineSpacing = metrics.Gap / 2, HorizontalAlignment = HorizontalAlignment.Right };
        var header = new KioskHeaderPanel { Spacing = metrics.Gap * 2, Margin = new Thickness(0, 0, 0, metrics.Gap), Children = { _title, _badges } };
        _banners = new StackPanel { Name = "banners", Spacing = metrics.Gap, Margin = new Thickness(0, 0, 0, metrics.Gap) };
        _page = new ContentControl { Name = "page", ClipToBounds = true, Content = RoofPage, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        _readings = new KioskPager(metrics, "readings");
        _notices = new KioskPager(metrics, "recent-notices");
        _stopResult = new KioskPager(metrics, "stop-result");
        var middle = new Grid { Margin = new Thickness(metrics.Gap * 2), RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = metrics.Gap };
        middle.Children.Add(_banners);
        Grid.SetRow(_page, 1);
        middle.Children.Add(_page);

        _navRoof = Nav("Roof", "nav-roof", () => ShowPage(KioskPage.Roof));
        _navReadings = Nav("Readings", "nav-readings", () => ShowPage(KioskPage.Readings));
        _navNotices = Nav("Notices", "nav-notices", () => ShowPage(KioskPage.Notices));
        _navSettings = Nav("Settings", "nav-settings", () => ShowPage(KioskPage.Settings));
        _navSystem = Nav("System", "nav-system", () => ShowPage(KioskPage.System));
        _navAbout = Nav("About", "nav-about", () => ShowPage(KioskPage.About));
        _navLock = Nav(console.Wording.SignInAction, "nav-lock", LockOrUnlock);
        _navPin = Nav("Change PIN", "nav-change-pin", () => ShowPage(KioskPage.ChangePin));
        _navTerminal = Nav("Terminal", "nav-terminal", () => ShowPage(KioskPage.Terminal));
        _navTerminal.IsVisible = terminalExit is not null;
        var nav = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = metrics.Gap, LineSpacing = metrics.Gap, Margin = new Thickness(metrics.Gap), Children = { _navRoof, _navReadings, _navNotices, _navSettings, _navSystem, _navAbout, _navTerminal, _navPin, _navLock } };
        var navFrame = new Border { Background = KioskTheme.Surface, BorderBrush = KioskTheme.Frame, BorderThickness = new Thickness(0, 1, 0, 0), Child = nav };

        Stop = new Button
        {
            Name = "stop",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = metrics.Touch * 2,
            FontSize = metrics.Huge,
            FontWeight = FontWeight.Bold,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(3),
            Content = new TextBlock { Text = RoofStopText.ButtonLabel, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }
        };
        ColourStop(false);
        Stop.Click += (_, _) => _ = _console.StopAsync();
        _stopMessage = KioskTheme.Label(RoofStopText.AlwaysAvailable, metrics.Font);
        _stopMessage.Name = "stop-message";
        _stopMessage.MinHeight = metrics.Font * 2.5;
        _stopMessage.VerticalAlignment = VerticalAlignment.Top;
        _stopDetails = KioskTheme.TouchButton("Full result", "stop-details", metrics, () => ShowPage(KioskPage.StopResult));
        _stopDetails.IsVisible = false;
        _stopColumn = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = metrics.Gap * 2, Margin = new Thickness(metrics.Gap * 2) };
        _stopColumn.Children.Add(Stop);
        Grid.SetRow(_stopMessage, 1);
        _stopColumn.Children.Add(_stopMessage);
        Grid.SetRow(_stopDetails, 2);
        _stopColumn.Children.Add(_stopDetails);
        _stopColumn.SizeChanged += (_, _) => FitStopResult();
        var stopFrame = new Border
        {
            Width = metrics.StopWidth,
            Background = KioskTheme.Surface,
            BorderBrush = KioskTheme.Brush(RoofUiPalette.Danger),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(metrics.Gap),
            Child = _stopColumn
        };

        var body = new DockPanel();
        DockPanel.SetDock(stopFrame, Dock.Right);
        body.Children.Add(stopFrame);
        body.Children.Add(middle);
        var main = new DockPanel();
        var headerFrame = new Border { Background = KioskTheme.Surface, BorderBrush = KioskTheme.Frame, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(metrics.Gap * 2, metrics.Gap), Child = header };
        DockPanel.SetDock(headerFrame, Dock.Top);
        DockPanel.SetDock(navFrame, Dock.Bottom);
        main.Children.Add(headerFrame);
        main.Children.Add(navFrame);
        main.Children.Add(body);

        _blank = new Border { Name = "blank", Background = KioskTheme.Black, IsVisible = false };
        Content = new Panel { Children = { main, _blank } };

        // Every touch keeps the kiosk awake; the one that wakes a blank screen is not passed on to what is under it.
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        console.Changed += OnConsoleChanged;
        Update(console.View);
    }

    /// <summary>Stop: at the right of every page.</summary>
    public Button Stop { get; }

    /// <summary>
    /// True while Stop is drawn bright: the newest status, live or last known, shows the roof moving
    /// (<see cref="RoofActionStates.IsStopActive"/>). Idle, Stop is the dim red; in both it is enabled and acts the same.
    /// </summary>
    public bool StopActive { get; private set; }

    public KioskPinPad PinPad { get; }

    public KioskSettingsPanel Settings { get; }

    public KioskSystemPanel System { get; }

    public KioskRoofPage RoofPage { get; }

    public KioskPinPage PinPage { get; }

    /// <summary>The Mac app's name and password.</summary>
    public KioskPasswordForm PasswordForm { get; }

    /// <summary>The Mac app's sign-in page: the kiosk's <see cref="PinPage"/>, with a name and password.</summary>
    public KioskSignInPage SignInPage { get; }

    public KioskSettingsPage SettingsPage { get; }

    public KioskSystemPage SystemPage { get; }

    /// <summary>Build identification and the controller's summary, available without unlocking.</summary>
    public KioskAboutPage AboutPage { get; }

    /// <summary>The page shown.</summary>
    public KioskPage Page { get; private set; } = KioskPage.Roof;

    /// <summary>
    /// Shows a page. Settings and System need a password session, and Unlock needs the client signed out; each reads what it shows
    /// when it opens. Unlock is the PIN pad on the kiosk and the name and password in the app. Change PIN (#205) is the PIN
    /// pad again, for the person who unlocked the kiosk with a PIN; leaving it forgets every PIN typed.
    /// </summary>
    public void ShowPage(KioskPage page)
    {
        var view = _console.View;
        if (Page == KioskPage.Terminal && page != KioskPage.Terminal) _terminalExit?.Cancel();
        if (page == KioskPage.Terminal && _terminalPage is null) page = KioskPage.Roof;
        if ((page is KioskPage.Settings or KioskPage.System && (!view.IsUnlocked || _console.Wording.SignIn == KioskSignIn.Pin)) || (page == KioskPage.Unlock && view.IsUnlocked)
            || (page == KioskPage.ChangePin && (!view.IsUnlocked || _console.Wording.SignIn != KioskSignIn.Pin)))
        {
            page = KioskPage.Roof;
        }

        if (Page == KioskPage.ChangePin && page != KioskPage.ChangePin) PinPad.EndChange();
        Page = page;
        Control content = page switch
        {
            KioskPage.Unlock when _console.Wording.SignIn == KioskSignIn.Password => SignInPage,
            KioskPage.Unlock or KioskPage.ChangePin => PinPage,
            KioskPage.Settings => SettingsPage,
            KioskPage.System => SystemPage,
            KioskPage.About => AboutPage,
            KioskPage.Readings => _readings,
            KioskPage.Notices => _notices,
            KioskPage.StopResult => _stopResult,
            KioskPage.Terminal => _terminalPage!,
            _ => RoofPage
        };

        if (_console.Wording.SignIn == KioskSignIn.Password && page is KioskPage.Settings or KioskPage.System or KioskPage.About)
        {
            _desktopScroller.Content = content;
            _page.Content = _desktopScroller;
        }
        else _page.Content = content;
        switch (page)
        {
            case KioskPage.Unlock when _console.Wording.SignIn == KioskSignIn.Password:
                PasswordForm.Clear();
                break;
            case KioskPage.Unlock:
                PinPage.Reset();
                PinPad.Select(PinPad.Users.Count == 1 ? PinPad.Selected : null);
                _ = PinPad.LoadUsersAsync();
                break;
            case KioskPage.ChangePin:
                PinPage.Reset();
                PinPad.BeginChange();
                break;
            case KioskPage.Settings when Settings.Form is null:
                _ = Settings.LoadAsync();
                break;
            case KioskPage.System when System.Health is null:
                _ = System.LoadAsync();
                break;
            case KioskPage.About:
                // Every role, a locked kiosk too: the summary is read afresh each time About opens (#209).
                _ = System.LoadSummaryAsync();
                break;
        }

        UpdateNav(view);
    }

    /// <summary>Shows <paramref name="view"/>: called on the UI thread whenever the console changes.</summary>
    public void Update(KioskView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (ReferenceEquals(view, _shown))
        {
            return;
        }

        _shown = view;
        if (view.IsUnlocked != _wasUnlocked)
        {
            _wasUnlocked = view.IsUnlocked;
            // Nothing read while unlocked stays on screen after the kiosk locks; the next person reads it again.
            Settings.Reset();
            System.Reset();
            if (Page != KioskPage.Roof)
            {
                ShowPage(KioskPage.Roof);
            }
        }

        _title.Text = view.Status?.ControllerName is { Length: > 0 } name ? name : "Roof";
        UpdateBadges(view);
        UpdateBanners(view);
        RoofPage.Update(view);
        AboutPage.Update(view);
        UpdateNav(view);

        _stopMessage.Text = view.StopMessage;
        _stopResult.SetItems([KioskTheme.Label("Stop result", _metrics.Large, KioskTheme.Accent, FontWeight.SemiBold), KioskTheme.Label(view.StopMessage, _metrics.Font)]);
        FitStopResult();
        _stopMessage.Foreground = view.StopOutcome switch
        {
            RoofStopOutcome.Failed => KioskTheme.Brush(RoofUiPalette.Danger),
            RoofStopOutcome.RelayUnverified => KioskTheme.Brush(RoofUiPalette.Warning),
            RoofStopOutcome.Sent => KioskTheme.Brush(RoofUiPalette.Info),
            RoofStopOutcome.Acknowledged => KioskTheme.Brush(RoofUiPalette.Success),
            _ => KioskTheme.Muted
        };

        // Stop is never disabled: not while a Stop is on its way, not while locked, not while the controller is unreachable.
        // Only its colour follows the roof: bright while it moves (or was last seen moving), dim while there is nothing to stop.
        Stop.IsEnabled = true;
        if (RoofActionStates.IsStopActive(view.Status) != StopActive) ColourStop(!StopActive);
        _blank.IsVisible = view.IsBlank;
    }

    private void ColourStop(bool active)
    {
        StopActive = active;
        var (background, foreground, border) = KioskTheme.StopColours(active);
        KioskTheme.Colour(Stop, background, foreground, border);
        // The permanent action retains its exact red pair under pointer, press and keyboard focus.
        Stop.Resources["ButtonBackgroundPointerOver"] = KioskTheme.Brush(background);
        Stop.Resources["ButtonBackgroundPressed"] = KioskTheme.Brush(background);
        Stop.Classes.Set("active", active);
    }

    private void FitStopResult()
    {
        if (_shown is not { } view || _stopColumn.Bounds.Width <= 0) return;
        _stopMessage.Text = view.StopMessage;
        _stopMessage.Measure(new Size(_stopColumn.Bounds.Width, double.PositiveInfinity));
        var budget = _stopColumn.Bounds.Height - _metrics.Touch * 2 - _metrics.Gap * 2;
        var breaksWord = _stopMessage.TextLayout.TextLines.Skip(1).Any(line =>
        {
            var at = line.FirstTextSourceIndex;
            return at > 0 && at < view.StopMessage.Length && char.IsLetterOrDigit(view.StopMessage[at - 1]) && char.IsLetterOrDigit(view.StopMessage[at]);
        });
        var longAnswer = _stopMessage.DesiredSize.Height > budget || breaksWord;
        _stopDetails.IsVisible = longAnswer;
        if (longAnswer)
        {
            // Preserve the complete shared message in the adjacent bounded result document. A compact outcome never
            // infers verification from Acknowledged: the actual message's first sentence is the only summary.
            var end = view.StopMessage.IndexOf(". ", StringComparison.Ordinal);
            _stopMessage.Text = end >= 0 ? view.StopMessage[..(end + 1)] : view.StopMessage;
            _stopMessage.Measure(new Size(_stopColumn.Bounds.Width, double.PositiveInfinity));
            if (_stopMessage.DesiredSize.Height > budget - _metrics.Touch - _metrics.Gap * 2)
                _stopMessage.Text = view.StopOutcome switch
                {
                    RoofStopOutcome.Failed => "Stop failed.",
                    RoofStopOutcome.RelayUnverified => "Stop: relay register unverified.",
                    RoofStopOutcome.Sent => "Stop sent. Waiting…",
                    RoofStopOutcome.Acknowledged => RoofStopText.Acknowledged,
                    _ => RoofStopText.AlwaysAvailable
                };
        }
    }

    private void OnConsoleChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Update(_console.View);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Update(_console.View), DispatcherPriority.Normal);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => Touched(e);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        Touched(e);
        if (e.Handled || e.Key != Key.Escape) return;
        if (Page == KioskPage.Settings && Settings.Question is not null) { Settings.Dismiss(); e.Handled = true; }
        else if (Page == KioskPage.System && System.Question is not null) { System.Dismiss(); e.Handled = true; }
        else if (Page == KioskPage.Terminal) { _terminalExit?.Cancel(); ShowPage(KioskPage.Roof); e.Handled = true; }
        else if (Page is KioskPage.Unlock or KioskPage.ChangePin)
        {
            PinPad.Select(null);
            PasswordForm.Clear();
            ShowPage(KioskPage.Roof);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Records the touch, and swallows it only when the screen showed black. What is on the glass decides, not whether
    /// the console had already blanked: in the moment between the console blanking and the view showing it, the person
    /// sees the buttons and a press on Stop must stop the roof.
    /// </summary>
    private void Touched(RoutedEventArgs e)
    {
        var shownBlank = _blank.IsVisible;
        _console.Touch();
        if (shownBlank)
        {
            e.Handled = true;
        }
    }

    private void LockOrUnlock()
    {
        if (_console.View.IsUnlocked)
        {
            _ = _console.LockAsync();
        }
        else
        {
            ShowPage(KioskPage.Unlock);
        }
    }

    private Button Nav(string text, string name, Action click)
    {
        var button = KioskTheme.TouchButton(text, name, _metrics, click);
        button.MinWidth = _metrics.NavWidth;
        button.FontSize = _metrics.Font;
        return button;
    }

    private void UpdateNav(KioskView view)
    {
        _navSettings.IsVisible = view.IsUnlocked && _console.Wording.SignIn == KioskSignIn.Password;
        _navSystem.IsVisible = view.IsUnlocked && _console.Wording.SignIn == KioskSignIn.Password;
        _navPin.IsVisible = view.IsUnlocked && _console.Wording.SignIn == KioskSignIn.Pin;
        KioskTheme.SetText(_navLock, view.IsUnlocked ? _console.Wording.SignOutAction : _console.Wording.SignInAction);
        foreach (var (button, page) in new[] { (_navRoof, KioskPage.Roof), (_navReadings, KioskPage.Readings), (_navNotices, KioskPage.Notices), (_navSettings, KioskPage.Settings), (_navSystem, KioskPage.System), (_navAbout, KioskPage.About), (_navTerminal, KioskPage.Terminal), (_navPin, KioskPage.ChangePin), (_navLock, KioskPage.Unlock) })
        {
            KioskTheme.Selected(button, page == Page);
            if (page == Page)
            {
                KioskTheme.Colour(button, RoofUiPalette.AccentStrong, RoofUiPalette.Text);
            }
            else
            {
                KioskTheme.Colour(button, RoofUiPalette.Badge, RoofUiPalette.Text, RoofUiPalette.Border);
            }
        }
    }

    private void UpdateBadges(KioskView view)
    {
        _badges.Children.Clear();
        var feed = view.FeedLabel switch
        {
            "live" => KioskTheme.SuccessColours,
            "refused" or "STALE" or "unreachable" => KioskTheme.Colours(KioskNoticeLevel.Danger),
            _ => KioskTheme.Colours(KioskNoticeLevel.Warning)
        };
        _badges.Children.Add(KioskTheme.Pill($"Status: {view.FeedLabel}", feed, _metrics, "feed-state"));
        if (view.HoldsLease)
        {
            var left = view.Status?.LeaseSecondsRemaining is { } lease ? $" ({RoofStatusText.Seconds(lease)} left)" : string.Empty;
            _badges.Children.Add(KioskTheme.Pill($"Renewing lease{left}", KioskTheme.Colours(KioskNoticeLevel.Info), _metrics, "lease"));
        }

        // A name may be 64 characters: past three touch widths it ends in an ellipsis, and the role still shows. The whole
        // name is in the notice "Unlocked by …".
        _badges.Children.Add(view.UnlockedBy is { } by
            ? KioskTheme.Pill(by, KioskTheme.Colours(KioskNoticeLevel.Info), _metrics, "unlocked-by", _metrics.Touch * 3, $" ({KioskText.DescribeRole(view.Role)})")
            : KioskTheme.Pill(_console.Wording.SignedOutBadge, (KioskTheme.Muted, KioskTheme.Badge, KioskTheme.Frame), _metrics, "locked"));
    }

    private void UpdateBanners(KioskView view)
    {
        _banners.Children.Clear();
        var warnings = new List<(string Name, string Text, KioskNoticeLevel Level)>();
        if (!view.IsStarted) warnings.Add(("connecting", "Connecting to the controller…", KioskNoticeLevel.Info));
        warnings.AddRange(view.ModeWarnings.Select(text => ("mode-warning", text, KioskNoticeLevel.Warning)));
        if (view.FeedBanner is { } feed) warnings.Add(("feed-banner", feed, KioskNoticeLevel.Warning));
        if (view.Status is { } status && RoofCommandRules.HasFault(status))
            warnings.Add(("fault-banner", $"Fault: {RoofStatusText.DescribeFault(status)}. Motion refused until the cause is fixed and the fault is cleared.", KioskNoticeLevel.Danger));
        if (view.Status is { } relays && (relays.RelayRegisterState == RoofRelayRegisterState.Unverified || !relays.RelayRegisterReadsHealthy))
            warnings.Add(("relays-banner", $"Relays: {RoofStatusText.DescribeRelays(relays)}.", KioskNoticeLevel.Warning));
        if (view.Status is { InputsHealthy: false } inputs)
            warnings.Add(("inputs-banner", $"Safety inputs: {RoofStatusText.DescribeInputs(inputs)}.", KioskNoticeLevel.Warning));
        // A bounded summary stays visible on every page. Full, unabridged warnings are in Readings, before the values.
        if (warnings.Count > 0)
        {
            var text = view.FeedRefused ? "Status refused. Motion unavailable. Readings has details."
                : view.IsUnreachable ? "Controller unreachable. Motion unavailable."
                : view.IsStale ? "STALE: last known status. Motion unavailable."
                : view.Status is { } roof && RoofCommandRules.HasFault(roof) ? "Fault latched. Motion refused. Readings has details."
                : view.ModeWarnings.Count > 0 ? "Simulation / safety mode warning. Readings has details."
                : !view.IsStarted ? "Connecting to the controller…" : "Safety I/O warning. Readings has details.";
            _banners.Children.Add(KioskTheme.Banner(text, warnings.Max(w => w.Level), _metrics, "safety-summary"));
        }
        var readings = new List<Control> { KioskTheme.Label("Controller readings", _metrics.Large, KioskTheme.Accent, FontWeight.SemiBold) };
        foreach (var warning in warnings)
        {
            var label = KioskTheme.Label(warning.Text, _metrics.Font, KioskTheme.Tone(warning.Level));
            label.Name = warning.Name;
            readings.Add(label);
        }
        foreach (var reason in new[] { view.Busy, view.OpenBlock is { } open ? $"Open: {open}" : null,
            view.CloseBlock is { } close ? $"Close: {close}" : null, view.ClearFaultBlock is { } clear ? $"Clear fault: {clear}" : null }.Where(text => text is not null))
            readings.Add(KioskTheme.Label(reason!, _metrics.Font, KioskTheme.Muted));
        if (view.Status is { } sample)
            foreach (var (label, value) in RoofStatusText.DescribeRows(sample))
                readings.Add(KioskTheme.Label($"{label}: {value}", _metrics.Font));
        else readings.Add(KioskTheme.Label(KioskText.NoStatusYet, _metrics.Font));
        _readings.SetItems(readings);
        _notices.SetItems(new[] { KioskTheme.Label("Recent notices", _metrics.Large, KioskTheme.Accent, FontWeight.SemiBold) }
            .Concat(view.Notices.Count == 0 ? new[] { KioskTheme.Label("No notices yet.", _metrics.Font, KioskTheme.Muted) }
                : view.Notices.Select(notice => KioskTheme.Label($"{RoofStatusText.Time(notice.At)}  {notice.Text}", _metrics.Font, KioskTheme.Tone(notice.Level)))));
    }
}
