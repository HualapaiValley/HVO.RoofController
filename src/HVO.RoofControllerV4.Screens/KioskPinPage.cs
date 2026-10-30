using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>Separate bounded people and private PIN steps; setup never grants an operator session.</summary>
public sealed class KioskPinPage : UserControl
{
    private readonly KioskPinPad _pad;
    private readonly KioskMetrics _metrics;
    private readonly KioskPager _people;
    private readonly Grid _entry;
    private readonly TextBlock _prompt;
    private readonly TextBlock _masked;
    private readonly Button _unlock;
    private readonly List<Button> _keys = [];
    private readonly Action _cancel;
    private IReadOnlyList<Common.Models.RoofPinUserResponse>? _shownUsers;
    private string? _dismissedError;
    private bool _wasLoading;

    /// <summary>Creates the bounded people, private keypad and recovery steps without changing PIN authorization.</summary>
    public KioskPinPage(KioskPinPad pad, KioskMetrics metrics, Action cancel)
    {
        _pad = pad;
        _metrics = metrics;
        _cancel = cancel;
        _people = new KioskPager(metrics, "pin-people");
        _prompt = KioskTheme.Label(string.Empty, metrics.Font);
        _prompt.Name = "pin-prompt";
        _masked = KioskTheme.Label(string.Empty, metrics.Large, weight: FontWeight.Bold);
        _masked.Name = "pin-masked";
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = metrics.Gap };
        heading.Children.Add(_prompt);
        Grid.SetColumn(_masked, 1);
        heading.Children.Add(_masked);
        var keys = new UniformGrid { Columns = 5, ColumnSpacing = metrics.Gap, RowSpacing = metrics.Gap };
        foreach (var key in new[] { "1", "2", "3", "⌫", "Clear", "4", "5", "6", "0", "Person", "7", "8", "9", "Unlock", "Cancel" })
        {
            var button = key switch
            {
                "⌫" => KioskTheme.TouchButton(key, "pin-backspace", metrics, pad.Backspace),
                "Clear" => KioskTheme.TouchButton(key, "pin-clear", metrics, pad.Clear),
                "Person" => KioskTheme.TouchButton(key, "pin-person", metrics, () => pad.Select(null)),
                "Unlock" => KioskTheme.TouchButton(key, "pin-unlock", metrics, () => _ = pad.UnlockAsync()),
                "Cancel" => KioskTheme.TouchButton(key, "pin-cancel", metrics, Cancel),
                _ => KioskTheme.TouchButton(key, $"pin-digit-{key}", metrics, () => pad.Press(key[0]))
            };
            button.FontSize = key.Length > 1 ? metrics.Font : metrics.Large;
            button.Height = metrics.Touch;
            button.Padding = new Thickness(metrics.Gap / 2);
            if (key == "Unlock")
            {
                _unlock = button;
                KioskTheme.Colour(button, RoofUiPalette.AccentStrong, RoofUiPalette.Text);
            }
            _keys.Add(button);
            keys.Children.Add(button);
        }
        _unlock = _keys.Single(button => button.Name == "pin-unlock");
        _entry = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = metrics.Gap };
        _entry.Children.Add(heading);
        Grid.SetRow(keys, 1);
        keys.VerticalAlignment = VerticalAlignment.Top;
        _entry.Children.Add(keys);
        pad.Changed += Update;
        Update();
    }

    /// <summary>Updates the current step without exposing digits or replacing the keypad under a touch.</summary>
    public void Update()
    {
        if (_pad.Error is null || (_wasLoading && !_pad.Loading)) _dismissedError = null;
        _wasLoading = _pad.Loading;
        _prompt.Text = _pad.Prompt;
        _masked.Text = _pad.Length == 0 ? "—" : _pad.Masked;
        KioskTheme.SetText(_unlock, _pad.ActionLabel);
        _unlock.IsEnabled = _pad.CanUnlock;
        foreach (var key in _keys.Where(button => button != _unlock))
            key.IsEnabled = !_pad.Unlocking && !(key.Name == "pin-person" && _pad.ChoosingPin);
        if (_pad.Error is { } error && error != _dismissedError)
        {
            var message = new KioskPager(_metrics, "pin-result");
            var text = KioskTheme.Label(error, _metrics.Font, KioskTheme.Brush(RoofUiPalette.Danger));
            text.Name = "pin-error";
            message.SetItems([
                KioskTheme.Label("PIN / setup result", _metrics.Large, KioskTheme.Accent), text,
                KioskTheme.TouchButton(_pad.Users.Count == 0 ? "Read people again" : "Try again", "pin-retry", _metrics, () =>
                {
                    _dismissedError = error;
                    if (_pad.Users.Count == 0) _ = _pad.LoadUsersAsync();
                    Update();
                }),
                KioskTheme.TouchButton("Cancel", "pin-cancel", _metrics, Cancel)
            ]);
            Content = message;
        }
        else if (_pad.Selected is null)
        {
            if (!ReferenceEquals(_shownUsers, _pad.Users) || Content != _people)
            {
                _shownUsers = _pad.Users;
                var items = new List<Control>
                {
                    KioskTheme.Label("Who are you?", _metrics.Large, KioskTheme.Accent, FontWeight.SemiBold),
                    KioskTheme.Label(_pad.Loading ? "Reading people…" : "PIN access is Operator-only. Settings and System need password sign-in in another client.", _metrics.Font, KioskTheme.Muted)
                };
                foreach (var user in _pad.Users)
                {
                    var name = KioskPinPad.SelectionKey(user);
                    // The friendly name when set (#209), and the login type for a Linux account.
                    var person = RoofIdentityText.DescribePerson(user.Name, user.DisplayName);
                    var label = user.LoginType == RoofLoginTypes.Linux ? $"{person} ({RoofIdentityText.DescribeLoginType(user.LoginType)})" : person;
                    items.Add(KioskTheme.TouchButton(label, $"pin-user-{name}", _metrics, () => _pad.Select(name)));
                }
                items.Add(KioskTheme.TouchButton("Cancel", "pin-cancel", _metrics, Cancel));
                _people.SetItems(items);
            }
            Content = _people;
        }
        else Content = _entry;
    }

    private void Cancel()
    {
        _pad.Select(null);
        _cancel();
    }
}
