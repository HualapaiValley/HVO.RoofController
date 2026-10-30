using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// Separate bounded people and private PIN steps; setup never grants an operator session. The PIN step is a compact,
/// centred keypad of fixed-size keys. The people step lists each person's name, role and login type, and its Cancel goes
/// back to the PIN step (with the person chosen before, if any); the PIN step's Cancel leaves the page.
/// </summary>
public sealed class KioskPinPage : UserControl
{
    private readonly KioskPinPad _pad;
    private readonly KioskMetrics _metrics;
    private readonly KioskPager _people;
    private readonly Grid _peopleStep;
    private readonly Control _entry;
    private readonly TextBlock _prompt;
    private readonly TextBlock _masked;
    private readonly Button _unlock;
    private readonly Button _person;
    private readonly List<Button> _keys = [];
    private readonly Action _cancel;
    private IReadOnlyList<RoofPinUserResponse>? _shownUsers;
    private string? _dismissedError;
    private bool _wasLoading;
    // The people step was cancelled: the keypad shows even without a person, and the person chosen before comes back.
    private bool _keypad;
    private string? _returnTo;
    private bool? _needsPerson;

    /// <summary>Creates the bounded people, private keypad and recovery steps without changing PIN authorization.</summary>
    public KioskPinPage(KioskPinPad pad, KioskMetrics metrics, Action cancel)
    {
        _pad = pad;
        _metrics = metrics;
        _cancel = cancel;
        _people = new KioskPager(metrics, "pin-people");
        // The people step: its heading and Cancel stay at the top of every page of people.
        var back = KioskTheme.TouchButton("Cancel", "pin-people-cancel", metrics, BackToPin);
        var peopleHeading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = metrics.Gap * 2 };
        peopleHeading.Children.Add(KioskTheme.Label("Who are you?", metrics.Large, KioskTheme.Accent, FontWeight.SemiBold));
        Grid.SetColumn(back, 1);
        peopleHeading.Children.Add(back);
        _peopleStep = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = metrics.Gap };
        _peopleStep.Children.Add(peopleHeading);
        Grid.SetRow(_people, 1);
        _peopleStep.Children.Add(_people);
        _prompt = KioskTheme.Label(string.Empty, metrics.Font);
        _prompt.Name = "pin-prompt";
        _masked = KioskTheme.Label(string.Empty, metrics.Large, weight: FontWeight.Bold);
        _masked.Name = "pin-masked";
        _masked.TextAlignment = TextAlignment.Center;
        _masked.HorizontalAlignment = HorizontalAlignment.Center;

        // Fixed-size keys: digits a little wider than the touch target, actions wide enough for "Confirm PIN".
        var digitWidth = Math.Round(metrics.Touch * 1.3);
        var actionWidth = Math.Round(metrics.Touch * 2.4);
        var digits = new UniformGrid { Columns = 4, ColumnSpacing = metrics.Gap, RowSpacing = metrics.Gap };
        foreach (var key in new[] { "1", "2", "3", "⌫", "4", "5", "6", "0", "7", "8", "9", "Clear" })
        {
            var button = key switch
            {
                "⌫" => KioskTheme.TouchButton(key, "pin-backspace", metrics, pad.Backspace),
                "Clear" => KioskTheme.TouchButton(key, "pin-clear", metrics, pad.Clear),
                _ => KioskTheme.TouchButton(key, $"pin-digit-{key}", metrics, () => pad.Press(key[0]))
            };
            button.FontSize = key.Length > 1 ? metrics.Font : metrics.Large;
            Size(button, digitWidth);
            _keys.Add(button);
            digits.Children.Add(button);
        }

        _unlock = KioskTheme.TouchButton("Unlock", "pin-unlock", metrics, () => _ = pad.UnlockAsync());
        KioskTheme.Colour(_unlock, RoofUiPalette.AccentStrong, RoofUiPalette.Text);
        _person = KioskTheme.TouchButton("Person", "pin-person", metrics, ChoosePerson);
        var cancelKey = KioskTheme.TouchButton("Cancel", "pin-cancel", metrics, Cancel);
        var actions = new StackPanel { Spacing = metrics.Gap };
        foreach (var button in new[] { _unlock, _person, cancelKey })
        {
            Size(button, actionWidth);
            _keys.Add(button);
            actions.Children.Add(button);
        }

        var keypad = new StackPanel { Orientation = Orientation.Horizontal, Spacing = metrics.Gap * 3, Children = { digits, actions } };
        var display = new Border
        {
            Background = KioskTheme.SurfaceRaised,
            BorderBrush = KioskTheme.Frame,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            MinWidth = Math.Round(metrics.Touch * 2.4),
            Padding = new Thickness(metrics.Gap * 2, metrics.Gap / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _masked
        };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = metrics.Gap * 2 };
        heading.Children.Add(_prompt);
        Grid.SetColumn(display, 1);
        heading.Children.Add(display);
        // The heading is as wide as the keypad, so the step stays one compact block.
        heading.Width = (digitWidth * 4) + (metrics.Gap * 3) + keypad.Spacing + actionWidth;
        var entry = new StackPanel { Name = "pin-entry", Spacing = metrics.Gap * 2, Children = { heading, keypad } };
        _entry = new Border
        {
            Background = KioskTheme.Surface,
            BorderBrush = KioskTheme.Frame,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(metrics.Gap * 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = entry
        };
        pad.Changed += Update;
        Update();
    }

    /// <summary>Starts the page afresh: the people step first, unless the person is already known.</summary>
    public void Reset()
    {
        _keypad = false;
        _returnTo = null;
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
            key.IsEnabled = !_pad.Unlocking && !(key == _person && _pad.ChoosingPin);
        // Without a person, choosing one is the next step.
        if (_needsPerson != (_pad.Selected is null))
        {
            _needsPerson = _pad.Selected is null;
            if (_needsPerson == true) KioskTheme.Colour(_person, RoofUiPalette.AccentStrong, RoofUiPalette.Text);
            else KioskTheme.Colour(_person, RoofUiPalette.Badge, RoofUiPalette.Text, RoofUiPalette.Border);
        }
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
        else if (_pad.Selected is null && !_keypad)
        {
            if (!ReferenceEquals(_shownUsers, _pad.Users) || Content != _peopleStep)
            {
                _shownUsers = _pad.Users;
                var items = new List<Control>
                {
                    KioskTheme.Label(_pad.Loading ? "Reading people…" : "PIN access is Operator-only. Settings and System need password sign-in in another client.", _metrics.Font, KioskTheme.Muted)
                };
                // Two people a row: each by their display name (#209), then their role, how they sign in and when they
                // last did.
                var people = _pad.Users.Select(user =>
                {
                    var name = KioskPinPad.SelectionKey(user);
                    return KioskPageParts.TwoLineButton(Heading(user), DescribePerson(user), $"pin-user-{name}", _metrics, () => _pad.Select(name));
                }).ToArray();
                for (var i = 0; i < people.Length; i += 2)
                {
                    var row = new UniformGrid { Columns = 2, ColumnSpacing = _metrics.Gap };
                    row.Children.Add(people[i]);
                    if (i + 1 < people.Length) row.Children.Add(people[i + 1]);
                    items.Add(row);
                }
                _people.SetItems(items);
            }
            Content = _peopleStep;
        }
        else Content = _entry;
    }

    /// <summary>
    /// A person's button heading: their display name with the name they sign in by, or their name
    /// (<see cref="RoofIdentityText.DescribePerson"/>), as the PIN prompt names them.
    /// </summary>
    public static string Heading(RoofPinUserResponse user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return RoofIdentityText.DescribePerson(user.Name, user.DisplayName);
    }

    /// <summary>
    /// What the people step says under a person's heading: their role, how they sign in, when they last did, their short
    /// name when they have one, and whether setup is pending.
    /// </summary>
    public static string DescribePerson(RoofPinUserResponse user)
    {
        ArgumentNullException.ThrowIfNull(user);
        List<string> parts =
        [
            KioskText.DescribeRole(user.Role),
            $"{RoofIdentityText.DescribeLoginType(user.LoginType)} account",
            $"last sign-in {RoofIdentityText.DescribeLastLogin(user.LastLoginUtc)}"
        ];
        if (!string.IsNullOrWhiteSpace(user.ShortName)) parts.Add($"short name {user.ShortName}");
        if (user.SetupPending) parts.Add("first-time setup");
        return string.Join(" · ", parts);
    }

    private static void Size(Button button, double width)
    {
        button.Width = width;
        button.Height = button.MinHeight;
        button.Padding = new Thickness(button.Padding.Top / 2);
    }

    private void ChoosePerson()
    {
        _returnTo = _pad.Selected;
        _keypad = false;
        _pad.Select(null);
    }

    // The people step's Cancel: back to the PIN step, with the person chosen before when they are still listed.
    private void BackToPin()
    {
        _keypad = true;
        var person = _returnTo;
        _returnTo = null;
        if (person is not null && _pad.Users.Any(user => KioskPinPad.SelectionKey(user) == person)) _pad.Select(person);
        else Update();
    }

    private void Cancel()
    {
        Reset();
        _pad.Select(null);
        _cancel();
    }
}
