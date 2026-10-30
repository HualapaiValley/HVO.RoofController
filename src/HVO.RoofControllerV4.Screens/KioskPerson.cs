using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>A fact about a person shown as a chip; <see cref="Level"/> colours it, or none for a plain one.</summary>
public sealed record KioskPersonChip(string Text, KioskNoticeLevel? Level = null);

/// <summary>
/// A person as the kiosk shows them, read-only (#208): a heading (their display name with the name they sign in by, or
/// their name, as the PIN prompt names them), chips for their role, how they sign in and a pending first-time setup, then
/// labelled lines for what the controller has besides: their short name and last sign-in. The people step shows one per
/// person to choose from; History shows the unlocked person's, with a <see cref="Note"/> on where they change their names.
/// Nothing here is edited: names are changed in the web UI or the terminal, never at the kiosk.
/// </summary>
public sealed record KioskPerson(string Heading, IReadOnlyList<KioskPersonChip> Chips, IReadOnlyList<string> Details, string? Note = null)
{
    /// <summary>A person on the people step (<c>GET Auth/Pin/Users</c>).</summary>
    public static KioskPerson Describe(RoofPinUserResponse user)
    {
        ArgumentNullException.ThrowIfNull(user);
        List<KioskPersonChip> chips = [Role(user.Role), LoginType(user.LoginType)];
        if (user.SetupPending)
        {
            chips.Add(new KioskPersonChip("First-time setup", KioskNoticeLevel.Warning));
        }

        List<string> details = [];
        if (!string.IsNullOrWhiteSpace(user.ShortName))
        {
            details.Add($"Short name: {user.ShortName}");
        }

        details.Add($"Last sign-in: {RoofIdentityText.DescribeLastLogin(user.LastLoginUtc)}");
        return new KioskPerson(RoofIdentityText.DescribePerson(user.Name, user.DisplayName), chips, details);
    }

    /// <summary>
    /// The person who unlocked the kiosk (or signed in to the app), as <c>GET Auth/Me</c> has them: the role their session
    /// grants (a PIN's is Operator, an admin's too), how they sign in and how they signed in, their short name, and as the
    /// note where they may change their names (a Linux account's come from the operating system).
    /// </summary>
    public static KioskPerson Describe(RoofCallerResponse caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        List<KioskPersonChip> chips =
        [
            Role(caller.Role),
            LoginType(caller.LoginType),
            new($"Signed in with {(caller.Kind == RoofCredentialKind.Pin ? "a PIN" : "a password")}")
        ];
        List<string> details = [];
        if (!string.IsNullOrWhiteSpace(caller.ShortName))
        {
            details.Add($"Short name: {caller.ShortName}");
        }

        return new KioskPerson(
            RoofIdentityText.DescribePerson(caller.Name, caller.DisplayName),
            chips,
            details,
            caller.LoginType == RoofLoginTypes.Linux ? RoofIdentityText.LinuxNamesReadOnly : RoofIdentityText.OwnNamesWhere);
    }

    /// <summary>Everything the card says, on one line: the heading, the chips, then the details (not the note).</summary>
    public override string ToString() => string.Join(" · ", Chips.Select(chip => chip.Text).Prepend(Heading).Concat(Details));

    /// <summary>
    /// The card's contents: the heading (the first text, as the tests and a two-line button have it), a row of chips that
    /// wraps on a narrow card, and the details in a quieter, smaller text. On a wide card (<paramref name="wide"/>) the
    /// chips follow the heading on its line, so the card takes less of the page.
    /// </summary>
    public StackPanel Content(KioskMetrics metrics, bool wide = false)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var content = new StackPanel { Spacing = metrics.Gap };
        var heading = new TextBlock { Text = Heading, FontSize = metrics.Font, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var chips = new WrapPanel { ItemSpacing = metrics.Gap, LineSpacing = metrics.Gap / 2 };
        if (wide)
        {
            chips.ItemSpacing = metrics.Gap * 1.5;
            chips.Children.Add(heading);
        }
        else
        {
            content.Children.Add(heading);
        }

        foreach (var chip in Chips)
        {
            var colours = chip.Level is { } level ? KioskTheme.Colours(level) : (KioskTheme.Muted, KioskTheme.Badge, KioskTheme.Frame);
            chips.Children.Add(KioskTheme.Pill(chip.Text, colours, metrics));
        }

        content.Children.Add(chips);
        foreach (var detail in Details)
        {
            content.Children.Add(new TextBlock { Text = detail, FontSize = metrics.Small, Foreground = KioskTheme.Muted, TextWrapping = TextWrapping.Wrap });
        }

        return content;
    }

    /// <summary>A button to choose the person by, at least a touch target, with the card's contents on it.</summary>
    public Button Button(string name, KioskMetrics metrics, Action click)
    {
        var button = KioskTheme.TouchButton(Heading, name, metrics, click);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.VerticalContentAlignment = VerticalAlignment.Top;
        button.Padding = new Thickness(metrics.Gap * 2, metrics.Gap * 1.5);
        button.Content = Content(metrics);
        return button;
    }

    private static KioskPersonChip Role(string? role) => new(KioskText.DescribeRole(role), KioskNoticeLevel.Info);

    private static KioskPersonChip LoginType(string? loginType)
    {
        var type = RoofIdentityText.DescribeLoginType(string.IsNullOrWhiteSpace(loginType) ? null : loginType);
        return new KioskPersonChip($"{char.ToUpperInvariant(type[0])}{type[1..]} account");
    }
}
