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
    System
}

/// <summary>
/// The kiosk's screen: a header with where the status comes from and who unlocked the kiosk, the banners, a rail of pages
/// at the left, the page, and Stop at the right of every page, never disabled and never behind the PIN. A blank screen
/// is black; the touch that wakes it goes no further.
/// </summary>
public sealed class KioskShell : UserControl
{
    private readonly KioskConsole _console;
    private readonly KioskMetrics _metrics;
    private readonly TextBlock _title;
    private readonly WrapPanel _badges;
    private readonly StackPanel _banners;
    private readonly ScrollViewer _scroller;
    private readonly Button _navRoof;
    private readonly Button _navSettings;
    private readonly Button _navSystem;
    private readonly Button _navLock;
    private readonly TextBlock _stopMessage;
    private readonly Border _blank;
    private bool _wasUnlocked;
    private KioskView? _shown;

    public KioskShell(KioskConsole console, KioskMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(metrics);
        _console = console;
        _metrics = metrics;
        PinPad = new KioskPinPad(console);
        Settings = new KioskSettingsPanel(console);
        System = new KioskSystemPanel(console);
        RoofPage = new KioskRoofPage(console, metrics);
        PinPage = new KioskPinPage(PinPad, metrics, () => ShowPage(KioskPage.Roof));
        SettingsPage = new KioskSettingsPage(Settings, console, metrics);
        SystemPage = new KioskSystemPage(System, console, metrics);
        Background = KioskTheme.Background;
        Foreground = KioskTheme.Text;
        FontSize = metrics.Font;

        // The title and the badges share the header: the title (the controller's name) keeps its width, wrapping between
        // words past a limit, and the badges wrap onto more lines in what is left. The unlocking person's name is cut
        // short on its pill (see UpdateBadges), so a long one cannot squeeze the title.
        _title = KioskTheme.Label("Roof", metrics.Large, weight: FontWeight.SemiBold);
        _title.Name = "title";
        _title.MaxWidth = metrics.Touch * 4;
        _badges = new WrapPanel { Name = "badges", ItemSpacing = metrics.Gap, LineSpacing = metrics.Gap / 2, HorizontalAlignment = HorizontalAlignment.Right };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = metrics.Gap * 2, Margin = new Thickness(0, 0, 0, metrics.Gap) };
        header.Children.Add(_title);
        Grid.SetColumn(_badges, 1);
        header.Children.Add(_badges);
        _banners = new StackPanel { Name = "banners", Spacing = metrics.Gap, Margin = new Thickness(0, 0, 0, metrics.Gap) };
        _scroller = new ScrollViewer
        {
            Name = "page",
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = RoofPage
        };
        var middle = new DockPanel { Margin = new Thickness(metrics.Gap * 2) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_banners, Dock.Top);
        middle.Children.Add(header);
        middle.Children.Add(_banners);
        middle.Children.Add(_scroller);

        _navRoof = Nav("Roof", "nav-roof", () => ShowPage(KioskPage.Roof));
        _navSettings = Nav("Settings", "nav-settings", () => ShowPage(KioskPage.Settings));
        _navSystem = Nav("System", "nav-system", () => ShowPage(KioskPage.System));
        _navLock = Nav("Unlock", "nav-lock", LockOrUnlock);
        var navTop = new StackPanel { Spacing = metrics.Gap, Children = { _navRoof, _navSettings, _navSystem } };
        var nav = new DockPanel { Width = metrics.NavWidth, Margin = new Thickness(metrics.Gap) };
        DockPanel.SetDock(_navLock, Dock.Bottom);
        nav.Children.Add(_navLock);
        nav.Children.Add(navTop);
        var navFrame = new Border { Background = KioskTheme.Surface, BorderBrush = KioskTheme.Frame, BorderThickness = new Thickness(0, 0, 1, 0), Child = nav };

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
        KioskTheme.Colour(Stop, RoofUiPalette.StopButton, RoofUiPalette.StopButtonText, "#000000");
        Stop.Click += (_, _) => _ = _console.StopAsync();
        _stopMessage = KioskTheme.Label(RoofStopText.AlwaysAvailable, metrics.Font);
        _stopMessage.Name = "stop-message";
        _stopMessage.MinHeight = metrics.Font * 4.5;
        _stopMessage.VerticalAlignment = VerticalAlignment.Top;
        var stopColumn = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = metrics.Gap * 2, Margin = new Thickness(metrics.Gap * 2) };
        stopColumn.Children.Add(Stop);
        Grid.SetRow(_stopMessage, 1);
        stopColumn.Children.Add(_stopMessage);
        var stopFrame = new Border
        {
            Width = metrics.StopWidth,
            Background = KioskTheme.Surface,
            BorderBrush = KioskTheme.Frame,
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = stopColumn
        };

        var main = new DockPanel();
        DockPanel.SetDock(stopFrame, Dock.Right);
        DockPanel.SetDock(navFrame, Dock.Left);
        main.Children.Add(stopFrame);
        main.Children.Add(navFrame);
        main.Children.Add(middle);

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

    public KioskPinPad PinPad { get; }

    public KioskSettingsPanel Settings { get; }

    public KioskSystemPanel System { get; }

    public KioskRoofPage RoofPage { get; }

    public KioskPinPage PinPage { get; }

    public KioskSettingsPage SettingsPage { get; }

    public KioskSystemPage SystemPage { get; }

    /// <summary>The page shown.</summary>
    public KioskPage Page { get; private set; } = KioskPage.Roof;

    /// <summary>
    /// Shows a page. Settings and System need the kiosk unlocked, and Unlock needs it locked; each reads what it shows
    /// when it opens.
    /// </summary>
    public void ShowPage(KioskPage page)
    {
        var view = _console.View;
        if ((page is KioskPage.Settings or KioskPage.System && !view.IsUnlocked) || (page == KioskPage.Unlock && view.IsUnlocked))
        {
            page = KioskPage.Roof;
        }

        Page = page;
        _scroller.Content = page switch
        {
            KioskPage.Unlock => PinPage,
            KioskPage.Settings => SettingsPage,
            KioskPage.System => SystemPage,
            _ => RoofPage
        };
        _scroller.Offset = default;
        switch (page)
        {
            case KioskPage.Unlock:
                PinPad.Select(PinPad.Users.Count == 1 ? PinPad.Selected : null);
                _ = PinPad.LoadUsersAsync();
                break;
            case KioskPage.Settings when Settings.Form is null:
                _ = Settings.LoadAsync();
                break;
            case KioskPage.System when System.Health is null:
                _ = System.LoadAsync();
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
        UpdateNav(view);

        _stopMessage.Text = view.StopMessage;
        _stopMessage.Foreground = view.StopOutcome switch
        {
            RoofStopOutcome.Failed => KioskTheme.Colours(KioskNoticeLevel.Danger).Foreground,
            RoofStopOutcome.RelayUnverified or RoofStopOutcome.Sent => KioskTheme.Colours(KioskNoticeLevel.Warning).Foreground,
            RoofStopOutcome.Acknowledged => KioskTheme.SuccessColours.Foreground,
            _ => KioskTheme.Muted
        };

        // Stop is never disabled: not while a Stop is on its way, not while locked, not while the controller is unreachable.
        Stop.IsEnabled = true;
        _blank.IsVisible = view.IsBlank;
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

    private void OnKeyDown(object? sender, KeyEventArgs e) => Touched(e);

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
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.FontSize = _metrics.Font;
        return button;
    }

    private void UpdateNav(KioskView view)
    {
        _navSettings.IsVisible = view.IsUnlocked;
        _navSystem.IsVisible = view.IsUnlocked;
        KioskTheme.SetText(_navLock, view.IsUnlocked ? "Lock" : "Unlock");
        foreach (var (button, page) in new[] { (_navRoof, KioskPage.Roof), (_navSettings, KioskPage.Settings), (_navSystem, KioskPage.System), (_navLock, KioskPage.Unlock) })
        {
            if (page == Page)
            {
                KioskTheme.Colour(button, RoofUiPalette.Accent, RoofUiPalette.Text);
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
            : KioskTheme.Pill("Locked", (KioskTheme.Muted, KioskTheme.Badge, KioskTheme.Frame), _metrics, "locked"));
    }

    private void UpdateBanners(KioskView view)
    {
        _banners.Children.Clear();
        if (!view.IsStarted)
        {
            _banners.Children.Add(KioskTheme.Banner("Connecting to the controller…", KioskNoticeLevel.Info, _metrics, "connecting"));
        }

        foreach (var warning in view.ModeWarnings)
        {
            _banners.Children.Add(KioskTheme.Banner(warning, KioskNoticeLevel.Warning, _metrics, "mode-warning"));
        }

        if (view.FeedBanner is { } banner)
        {
            _banners.Children.Add(KioskTheme.Banner(
                banner,
                view.FeedRefused || view.IsUnreachable ? KioskNoticeLevel.Danger : KioskNoticeLevel.Warning,
                _metrics,
                "feed-banner"));
        }

        if (view.Status is { } status && RoofCommandRules.HasFault(status))
        {
            _banners.Children.Add(KioskTheme.Banner(
                $"Fault: {RoofStatusText.DescribeFault(status)}. Open and Close are refused until the cause is fixed and the fault is cleared.{(view.IsOperator ? string.Empty : " An operator must clear it.")}",
                KioskNoticeLevel.Danger,
                _metrics,
                "fault-banner"));
        }

        if (view.Status is { } relays && (relays.RelayRegisterState == RoofRelayRegisterState.Unverified || !relays.RelayRegisterReadsHealthy))
        {
            _banners.Children.Add(KioskTheme.Banner($"Relays: {RoofStatusText.DescribeRelays(relays)}.", KioskNoticeLevel.Warning, _metrics, "relays-banner"));
        }

        if (view.Status is { InputsHealthy: false } inputs)
        {
            _banners.Children.Add(KioskTheme.Banner(
                $"Safety inputs: {RoofStatusText.DescribeInputs(inputs)}. Motion may be refused.",
                KioskNoticeLevel.Warning,
                _metrics,
                "inputs-banner"));
        }
    }
}
