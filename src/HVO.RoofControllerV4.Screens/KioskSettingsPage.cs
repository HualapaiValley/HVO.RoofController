using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using HVO.RoofControllerV4.Client;
using HVO.RoofControllerV4.Common.Models;

namespace HVO.RoofControllerV4.Screens;

/// <summary>
/// The kiosk's settings page: the groups, a group's settings, one setting's details, and a touch editor for it (a keypad
/// for numbers and durations, a keyboard for text, a button per value for the rest). Rebuilt when the panel changes,
/// which only a touch here or its answer does.
/// </summary>
public sealed class KioskSettingsPage : UserControl
{
    /// <summary>The keyboard's letters page, seven keys a row.</summary>
    internal static readonly string[] Letters =
    [
        "a", "b", "c", "d", "e", "f", "g",
        "h", "i", "j", "k", "l", "m", "n",
        "o", "p", "q", "r", "s", "t", "u",
        "v", "w", "x", "y", "z", ".", "-"
    ];

    /// <summary>The keyboard's digits and symbols page.</summary>
    internal static readonly string[] Symbols =
    [
        "1", "2", "3", "4", "5", "6", "7",
        "8", "9", "0", "_", ":", "/", "@",
        ",", "+", "=", "?", "!", "#", "'"
    ];

    private readonly KioskSettingsPanel _panel;
    private readonly KioskConsole _console;
    private readonly KioskMetrics _metrics;
    private bool _symbols;

    public KioskSettingsPage(KioskSettingsPanel panel, KioskConsole console, KioskMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(metrics);
        _panel = panel;
        _console = console;
        _metrics = metrics;
        panel.Changed += Update;
        Update();
    }

    /// <summary>Shows the panel as it is now.</summary>
    public void Update()
    {
        var page = new StackPanel { Spacing = _metrics.Gap * 2 };
        if (_panel.Editing is null || _panel.Question is not null)
        {
            // While a value is typed, the editor's first line names the setting, and the room is the keyboard's: all of
            // it fits on the smallest screen without scrolling.
            page.Children.Add(KioskTheme.Label("Settings", _metrics.Large, weight: FontWeight.SemiBold));
        }

        foreach (var status in KioskPageParts.Status(_panel.Busy, _panel.Message, _metrics))
        {
            page.Children.Add(status);
        }

        if (_panel.Question is { } question)
        {
            page.Children.Add(KioskPageParts.Question(question, _metrics, _panel.Dismiss));
        }
        else if (_panel.Editing is { } editing)
        {
            page.Children.Add(Editor(editing));
        }
        else if (_panel.Field is { } field)
        {
            page.Children.Add(Details(field));
        }
        else if (_panel.Group is { } group)
        {
            page.Children.Add(Fields(group));
        }
        else if (_panel.Form is { } form)
        {
            page.Children.Add(Groups(form));
        }
        else if (_panel.Busy is null)
        {
            page.Children.Add(KioskPageParts.Row(_metrics, KioskTheme.TouchButton("Read the settings", "settings-reload", _metrics, () => _ = _panel.LoadAsync())));
        }

        Content = page;
    }

    private Control Groups(RoofSettingsForm form)
    {
        var panel = new StackPanel { Spacing = _metrics.Gap };
        panel.Children.Add(KioskTheme.Label(_panel.Header, _metrics.Font, KioskTheme.Muted));
        foreach (var group in form.Groups)
        {
            var name = group.Name;
            panel.Children.Add(KioskPageParts.TwoLineButton(
                group.Title,
                group.CanWrite ? group.Description : $"{group.Description} (read-only)",
                $"settings-group-{name}",
                _metrics,
                () => _panel.SelectGroup(name)));
        }

        var actions = new List<Control> { KioskTheme.TouchButton("Read again", "settings-reload", _metrics, () => _ = _panel.LoadAsync()) };
        if (_console.View.IsAdmin)
        {
            actions.Add(KioskTheme.TouchButton("Hand edit", "settings-hand-edit", _metrics, _panel.ReviewHandEdit));
        }

        panel.Children.Add(KioskPageParts.Row(_metrics, [.. actions]));
        return panel;
    }

    private Control Fields(RoofSettingsFormGroup group)
    {
        var panel = new StackPanel { Spacing = _metrics.Gap };
        panel.Children.Add(KioskTheme.Label(group.Title, _metrics.Large, weight: FontWeight.SemiBold));
        panel.Children.Add(KioskTheme.Label(group.Description, _metrics.Font, KioskTheme.Muted));
        if (group.Secrets.Count > 0)
        {
            panel.Children.Add(KioskTheme.Label(KioskSettingsPanel.SecretsElsewhere, _metrics.Font, KioskTheme.MutedWeak));
        }

        panel.Children.Add(Back(() => _panel.SelectGroup(null)));
        foreach (var field in group.Fields)
        {
            var key = field.Key;
            panel.Children.Add(KioskPageParts.TwoLineButton(
                field.Label,
                KioskSettingsPanel.DescribeLine(field),
                $"settings-field-{key}",
                _metrics,
                () => _panel.SelectField(key)));
        }

        return panel;
    }

    private Control Details(RoofSettingsFormField field)
    {
        var panel = new StackPanel { Spacing = _metrics.Gap };
        foreach (var line in KioskSettingsPanel.Describe(field))
        {
            panel.Children.Add(KioskTheme.Label(line, _metrics.Font));
        }

        var actions = new List<Control>();
        if (field.CanWrite && !field.Setting.Secret)
        {
            var change = KioskTheme.TouchButton("Change", "settings-change", _metrics, _panel.BeginEdit);
            KioskTheme.Colour(change, RoofUiPalette.AccentStrong, RoofUiPalette.Text);
            actions.Add(change);
        }
        else if (field.Setting.Secret)
        {
            panel.Children.Add(KioskTheme.Label(KioskSettingsPanel.SecretsElsewhere, _metrics.Font, KioskTheme.MutedWeak));
        }

        actions.Add(Back(() => _panel.SelectField(null)));
        panel.Children.Add(KioskPageParts.Row(_metrics, [.. actions]));
        return KioskTheme.Card(panel, _metrics);
    }

    private Control Editor(RoofSettingsFormField field)
    {
        var panel = new StackPanel { Name = "editor", Spacing = _metrics.Gap };
        // The default is on the setting's page, a press before the editor; here the caption is kept to one short line.
        var label = $"Change {field.Label}";
        if (_panel.EditError is { } error)
        {
            // Above the editor, so it is seen without scrolling past the keyboard.
            panel.Children.Add(KioskTheme.Banner(error, KioskNoticeLevel.Danger, _metrics, "editor-error"));
        }

        if (_panel.EditorKind == KioskEditorKind.Choices)
        {
            panel.Children.Insert(0, KioskTheme.Label(label, _metrics.Font, KioskTheme.Muted));
            var choices = new WrapPanel { ItemSpacing = _metrics.Gap * 2, LineSpacing = _metrics.Gap };
            foreach (var choice in _panel.Choices)
            {
                var value = choice;
                var button = KioskTheme.TouchButton(choice, $"choice-{choice}", _metrics, () => _ = _panel.ChooseAsync(value));
                button.MinWidth = _metrics.Touch * 1.6;
                if (string.Equals(field.EditText, choice, StringComparison.OrdinalIgnoreCase) || (choice == KioskSettingsPanel.NoValue && field.EditText.Length == 0))
                {
                    KioskTheme.Colour(button, RoofUiPalette.Accent, RoofUiPalette.Text);
                }

                choices.Children.Add(button);
            }

            choices.Children.Add(KioskTheme.TouchButton("Cancel", "editor-cancel", _metrics, _panel.CancelEdit));
            panel.Children.Add(choices);
        }
        else
        {
            var value = new Border
            {
                Background = KioskTheme.Background,
                BorderBrush = KioskTheme.Accent,
                BorderThickness = new Avalonia.Thickness(2),
                CornerRadius = new Avalonia.CornerRadius(4),
                Padding = new Avalonia.Thickness(_metrics.Gap * 1.5, 0),
                MinHeight = _metrics.Touch,
                // The setting's name is a caption in the box, not a line above it: the keyboard's five rows, the value
                // and the page's header are then all on the smallest screen (about 88 mm tall) without scrolling.
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        // One line, never cut: the render checks refuse it cut short.
                        new TextBlock
                        {
                            Name = "editor-label",
                            Text = label,
                            FontSize = _metrics.Small,
                            Foreground = KioskTheme.Muted,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        },
                        // A value too long for the box shows its end, where the typing is.
                        new TextBlock
                        {
                            Name = "editor-value",
                            Text = _panel.EditText.Length == 0 ? (field.Setting.Nullable ? KioskSettingsPanel.NoValue : string.Empty) : _panel.EditText,
                            FontSize = _metrics.Large,
                            Foreground = _panel.EditText.Length == 0 ? KioskTheme.MutedWeak : KioskTheme.Text,
                            TextTrimming = TextTrimming.LeadingCharacterEllipsis,
                            Classes = { KioskTheme.Abbreviated }
                        }
                    }
                }
            };
            var save = KioskTheme.TouchButton("Save", "editor-save", _metrics, () => _ = _panel.ReviewAsync());
            KioskTheme.Colour(save, RoofUiPalette.AccentStrong, RoofUiPalette.Text);
            var cancel = KioskTheme.TouchButton("Cancel", "editor-cancel", _metrics, _panel.CancelEdit);
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = _metrics.Gap };
            top.Children.Add(value);
            Grid.SetColumn(save, 1);
            top.Children.Add(save);
            Grid.SetColumn(cancel, 2);
            top.Children.Add(cancel);
            panel.Children.Add(top);
            panel.Children.Add(_panel.EditorKind == KioskEditorKind.Keypad ? Keypad(field) : Keyboard());
        }

        return panel;
    }

    /// <summary>Four rows, so the keypad fits under the value on the smallest screen; a duration's units are a fifth column.</summary>
    private Control Keypad(RoofSettingsFormField field) => field.Setting.Type == RoofSettingType.Duration
        ? Keys(5, ["7", "8", "9", "⌫", "ms", "4", "5", "6", "Clear", "s", "1", "2", "3", "-", "m", "0", ".", ":", "", "h"])
        : Keys(4, ["7", "8", "9", "⌫", "4", "5", "6", "Clear", "1", "2", "3", "-", "0", ".", ":"]);

    private Control Keyboard()
    {
        var keys = new List<string>(_symbols ? Symbols : Letters);
        var grid = Keys(7, keys);
        var shift = KioskTheme.TouchButton(_panel.Shift ? "⇧ ON" : "⇧", "editor-key-shift", _metrics, _panel.ToggleShift);
        var page = KioskTheme.TouchButton(_symbols ? "abc" : "123", "editor-key-page", _metrics, () =>
        {
            _symbols = !_symbols;
            Update();
        });
        var space = KioskTheme.TouchButton("Space", "editor-key-space", _metrics, () => _panel.Type(" "));
        space.MinWidth = _metrics.Touch * 2;
        var backspace = KioskTheme.TouchButton("⌫", "editor-key-⌫", _metrics, _panel.Backspace);
        var clear = KioskTheme.TouchButton("Clear", "editor-key-Clear", _metrics, _panel.ClearText);
        return new StackPanel
        {
            Spacing = _metrics.Gap,
            Children = { grid, new StackPanel { Orientation = Orientation.Horizontal, Spacing = _metrics.Gap, Children = { shift, page, space, backspace, clear } } }
        };
    }

    private UniformGrid Keys(int columns, IReadOnlyList<string> keys)
    {
        var grid = new UniformGrid { Columns = columns, ColumnSpacing = _metrics.Gap, RowSpacing = _metrics.Gap, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var key in keys)
        {
            if (key.Length == 0)
            {
                grid.Children.Add(new Border());
                continue;
            }

            Action press = key switch
            {
                "⌫" => _panel.Backspace,
                "Clear" => _panel.ClearText,
                _ => () => _panel.Type(key)
            };
            var button = KioskTheme.TouchButton(_panel.Shift && key.Length == 1 && char.IsLetter(key[0]) ? key.ToUpperInvariant() : key, $"editor-key-{key}", _metrics, press);
            button.Width = _metrics.Touch;
            button.Height = _metrics.Touch;
            button.Padding = new Avalonia.Thickness(0);
            button.FontSize = key.Length > 1 ? _metrics.Font : _metrics.Large;
            grid.Children.Add(button);
        }

        return grid;
    }

    private Button Back(Action back)
    {
        var button = KioskTheme.TouchButton("Back", "settings-back", _metrics, back);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }
}
