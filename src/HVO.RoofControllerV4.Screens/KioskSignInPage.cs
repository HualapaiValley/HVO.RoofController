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
/// The Mac app's sign-in page: the person's name and password, which the controller checks. The password box never shows
/// what is typed, and is emptied as the password is sent. Return signs in and Escape cancels.
/// </summary>
public sealed class KioskSignInPage : UserControl
{
    private readonly KioskPasswordForm _form;
    private readonly TextBox _name;
    private readonly TextBox _password;
    private readonly TextBlock _error;
    private readonly Button _signIn;
    private bool _showing;
    private bool _wasSigningIn;

    /// <param name="form">The sign-in this page shows.</param>
    /// <param name="metrics">The screens' sizes.</param>
    /// <param name="cancel">Leaves the page without signing in.</param>
    public KioskSignInPage(KioskPasswordForm form, KioskMetrics metrics, Action cancel)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(cancel);
        _form = form;

        _name = Box("signin-name", RoofIdentityContract.MaximumNameLength, metrics);
        _name.PlaceholderText = "Your name on the controller";
        _password = Box("signin-password", RoofIdentityContract.MaximumPasswordLength, metrics);
        _password.PasswordChar = '●';
        _password.RevealPassword = false;
        // TextChanging, not TextChanged: it is raised as the text changes (TextChanged comes later), so Return sends what
        // the boxes show, and the page's own changes (while showing the form) are not taken for typing.
        _name.TextChanging += (_, _) =>
        {
            if (!_showing)
            {
                form.SetName(_name.Text);
            }
        };
        _password.TextChanging += (_, _) =>
        {
            if (!_showing)
            {
                form.SetPassword(_password.Text);
            }
        };

        _error = KioskTheme.Label(string.Empty, metrics.Font, KioskTheme.Colours(KioskNoticeLevel.Danger).Foreground);
        _error.Name = "signin-error";
        _signIn = KioskTheme.TouchButton("Sign in", "signin-submit", metrics, () => _ = form.SignInAsync());
        KioskTheme.Colour(_signIn, RoofUiPalette.AccentStrong, RoofUiPalette.Text);
        _signIn.MinWidth = metrics.Touch * 2.4;
        var back = KioskTheme.TouchButton("Cancel", "signin-cancel", metrics, cancel);
        back.MinWidth = metrics.Touch * 1.6;

        Content = new StackPanel
        {
            Spacing = metrics.Gap,
            MaxWidth = metrics.Touch * 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                KioskTheme.Label("Sign in", metrics.Large, weight: FontWeight.SemiBold),
                KioskTheme.Label("With your name and password on the controller, as in the web UI.", metrics.Font, KioskTheme.Muted),
                KioskTheme.Label("Name", metrics.Small, KioskTheme.Muted),
                _name,
                KioskTheme.Label("Password", metrics.Small, KioskTheme.Muted),
                _password,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = metrics.Gap, Margin = new Thickness(0, metrics.Gap, 0, 0), Children = { _signIn, back } },
                _error
            }
        };
        // Return signs in and Escape cancels, from either box; the boxes have no use for them.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = form.SignInAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                cancel();
            }
        }, RoutingStrategies.Tunnel);
        form.Changed += Update;
        Update();
    }

    /// <summary>The name box.</summary>
    public TextBox NameBox => _name;

    /// <summary>The password box: it shows a dot for each character typed.</summary>
    public TextBox PasswordBox => _password;

    /// <summary>Shows the form as it is now.</summary>
    public void Update()
    {
        _showing = true;
        try
        {
            if ((_name.Text ?? string.Empty) != _form.Name)
            {
                _name.Text = _form.Name;
            }

            // The form forgets the password as it sends it, and when the page opens again; so does the box.
            if (!_form.HasPassword && !string.IsNullOrEmpty(_password.Text))
            {
                _password.Text = string.Empty;
            }
        }
        finally
        {
            _showing = false;
        }

        _name.IsReadOnly = _form.SigningIn;
        _password.IsReadOnly = _form.SigningIn;
        _error.Text = _form.Error ?? (_form.SigningIn ? "Signing in…" : string.Empty);
        _error.Foreground = _form.Error is null ? KioskTheme.Muted : KioskTheme.Colours(KioskNoticeLevel.Danger).Foreground;
        _error.IsVisible = _error.Text.Length > 0;
        _signIn.IsEnabled = _form.CanSignIn;
        // Refused: the password box has the keyboard again, for the next try.
        if (_wasSigningIn && !_form.SigningIn && _form.Error is not null && this.IsAttachedToVisualTree())
        {
            _password.Focus();
        }

        _wasSigningIn = _form.SigningIn;
    }

    /// <summary>
    /// The name box has the keyboard when the page opens, or the password box once there is a name. (On load, not on
    /// attach: the boxes are attached after the page, and a box not yet attached cannot take the keyboard.)
    /// </summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        (_form.Name.Length > 0 ? _password : _name).Focus();
    }

    private static TextBox Box(string name, int maxLength, KioskMetrics metrics) => new()
    {
        Name = name,
        MaxLength = maxLength,
        FontSize = metrics.Font,
        MinHeight = metrics.Touch,
        VerticalContentAlignment = VerticalAlignment.Center,
        AcceptsReturn = false,
        TextWrapping = TextWrapping.NoWrap
    };
}
