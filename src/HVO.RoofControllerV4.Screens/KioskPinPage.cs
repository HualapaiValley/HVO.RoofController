using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;

namespace HVO.RoofControllerV4.Screens;

/// <summary>The kiosk's unlock page: the people who may unlock it, and a PIN pad that never shows the digits.</summary>
public sealed class KioskPinPage : UserControl
{
    private readonly KioskPinPad _pad;
    private readonly KioskMetrics _metrics;
    private readonly StackPanel _people;
    private readonly TextBlock _loading;
    private readonly TextBlock _prompt;
    private readonly TextBlock _masked;
    private readonly TextBlock _error;
    private readonly Button _unlock;
    private readonly List<Button> _keys = [];
    private IReadOnlyList<Common.Models.RoofPinUserResponse>? _shownUsers;
    private string? _shownSelected;

    /// <param name="pad">The PIN pad this page shows.</param>
    /// <param name="metrics">The kiosk's sizes.</param>
    /// <param name="cancel">Leaves the page without unlocking.</param>
    public KioskPinPage(KioskPinPad pad, KioskMetrics metrics, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(pad);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(cancel);
        _pad = pad;
        _metrics = metrics;

        _loading = KioskTheme.Label("Reading who may unlock the kiosk…", metrics.Font, KioskTheme.Muted);
        _people = new StackPanel { Name = "pin-people", Spacing = metrics.Gap };
        var people = new StackPanel
        {
            Spacing = metrics.Gap,
            Children = { KioskTheme.Label("Who are you?", metrics.Large, weight: FontWeight.SemiBold), _loading, _people }
        };

        _prompt = KioskTheme.Label(string.Empty, metrics.Font);
        _prompt.Name = "pin-prompt";
        _masked = KioskTheme.Label(string.Empty, metrics.Huge, weight: FontWeight.Bold);
        _masked.Name = "pin-masked";
        _masked.MinHeight = metrics.Huge * 1.3;
        _error = KioskTheme.Label(string.Empty, metrics.Font, KioskTheme.Colours(KioskNoticeLevel.Danger).Foreground);
        _error.Name = "pin-error";

        var keys = new UniformGrid { Columns = 4, ColumnSpacing = metrics.Gap, RowSpacing = metrics.Gap, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var key in new[] { "1", "2", "3", "⌫", "4", "5", "6", "Clear", "7", "8", "9", "0" })
        {
            var button = key switch
            {
                "⌫" => KioskTheme.TouchButton(key, "pin-backspace", metrics, pad.Backspace),
                "Clear" => KioskTheme.TouchButton(key, "pin-clear", metrics, pad.Clear),
                _ => KioskTheme.TouchButton(key, $"pin-digit-{key}", metrics, () => pad.Press(key[0]))
            };
            // A word on a key is in the text size, so it fits the key; a digit or a symbol is larger.
            button.FontSize = key.Length > 1 ? metrics.Font : metrics.Large;
            button.Width = metrics.Touch * 1.15;
            button.Height = metrics.Touch;
            _keys.Add(button);
            keys.Children.Add(button);
        }

        _unlock = KioskTheme.TouchButton("Unlock", "pin-unlock", metrics, () => _ = pad.UnlockAsync());
        KioskTheme.Colour(_unlock, RoofUiPalette.AccentStrong, RoofUiPalette.Text);
        _unlock.MinWidth = metrics.Touch * 2.4;
        var back = KioskTheme.TouchButton("Cancel", "pin-cancel", metrics, cancel);
        back.MinWidth = metrics.Touch * 1.6;
        var entry = new StackPanel
        {
            Spacing = metrics.Gap,
            Children =
            {
                _prompt,
                _masked,
                keys,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = metrics.Gap, Children = { _unlock, back } },
                _error
            }
        };

        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = metrics.Gap * 3 };
        layout.Children.Add(people);
        Grid.SetColumn(entry, 1);
        layout.Children.Add(entry);
        Content = layout;
        pad.Changed += Update;
        Update();
    }

    /// <summary>Shows the pad as it is now.</summary>
    public void Update()
    {
        _loading.IsVisible = _pad.Loading && _pad.Users.Count == 0;
        if (!ReferenceEquals(_shownUsers, _pad.Users) || _shownSelected != _pad.Selected)
        {
            _shownUsers = _pad.Users;
            _shownSelected = _pad.Selected;
            _people.Children.Clear();
            foreach (var user in _pad.Users)
            {
                var name = user.Name;
                var button = KioskTheme.TouchButton(name, $"pin-user-{name}", _metrics, () => _pad.Select(name));
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                if (name == _pad.Selected)
                {
                    KioskTheme.Colour(button, RoofUiPalette.Accent, RoofUiPalette.Text);
                }

                _people.Children.Add(button);
            }
        }

        _prompt.Text = _pad.Prompt;
        _masked.Text = _pad.Length == 0 ? " " : _pad.Masked;
        _error.Text = _pad.Error ?? (_pad.Unlocking ? "Unlocking…" : string.Empty);
        _error.Foreground = _pad.Error is null ? KioskTheme.Muted : KioskTheme.Colours(KioskNoticeLevel.Danger).Foreground;
        _error.IsVisible = _error.Text.Length > 0;
        _unlock.IsEnabled = _pad.CanUnlock;
        foreach (var key in _keys)
        {
            key.IsEnabled = _pad.Selected is not null && !_pad.Unlocking;
        }
    }
}
