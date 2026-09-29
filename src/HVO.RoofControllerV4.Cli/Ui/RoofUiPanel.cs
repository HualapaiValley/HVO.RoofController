using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>
/// A prompt drawn over the page, not a modal dialog: the Stop button and F9 stay live while it is open. Enter in the
/// last field, or a button, runs an action; an action's error keeps the prompt open and is shown in it.
/// </summary>
internal sealed class RoofUiPanel : FrameView
{
    private readonly RoofUiTheme _theme;
    private readonly Action<RoofUiPanel> _close;
    private readonly List<Button> _buttons = [];
    private readonly Label _error;

    public RoofUiPanel(RoofUiPrompt prompt, RoofUiTheme theme, Action<RoofUiPanel> close)
    {
        _theme = theme;
        _close = close;
        Prompt = prompt;
        Title = prompt.Title;
        X = Pos.Center();
        Y = Pos.Center();
        Width = Dim.Percent(90);
        Height = Dim.Auto(DimAutoStyle.Content);
        SetScheme(theme.Panel);
        theme.SetFrame(this, theme.PanelFrame);

        var message = new Label { Text = prompt.Message, X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Auto(DimAutoStyle.Text) };
        Add(message);
        View previous = message;
        var labelWidth = prompt.Fields.Count == 0 ? 0 : prompt.Fields.Max(field => field.Label.Length) + 2;
        var fields = new List<TextField>();
        foreach (var field in prompt.Fields)
        {
            var label = new Label { Text = field.Label + ":", X = 0, Y = Pos.Bottom(previous) + (fields.Count == 0 ? 1 : 0) };
            var input = new TextField
            {
                Text = field.Initial,
                Secret = field.Secret,
                ReadOnly = field.ReadOnly,
                X = labelWidth,
                Y = Pos.Top(label),
                Width = Dim.Fill()
            };
            Add(label, input);
            fields.Add(input);
            previous = label;
        }

        Fields = fields;
        _error = new Label { X = 0, Y = Pos.Bottom(previous) + 1, Width = Dim.Fill(), Height = Dim.Auto(DimAutoStyle.Text) };
        _error.SetScheme(theme.PanelError);
        Add(_error);

        View? left = null;
        foreach (var action in prompt.Actions)
        {
            left = AddButton(action.Label, left, () => Choose(action));
        }

        AddButton(prompt.CloseLabel, left, () => _close(this));

        // Enter in a field runs the first action, as it would in a form.
        foreach (var input in fields.Where(input => !input.ReadOnly))
        {
            input.Accepting += (_, e) =>
            {
                e.Handled = true;
                if (prompt.Actions.Count > 0)
                {
                    Choose(prompt.Actions[0]);
                }
            };
        }
    }

    public RoofUiPrompt Prompt { get; }

    public IReadOnlyList<TextField> Fields { get; }

    public IReadOnlyList<Button> Buttons => _buttons;

    public string Error => _error.Text;

    public void FocusFirst()
    {
        var first = (View?)Fields.FirstOrDefault(field => !field.ReadOnly) ?? _buttons.FirstOrDefault();
        first?.SetFocus();
    }

    /// <summary>Presses the button labelled <paramref name="label"/>, as Enter on it would.</summary>
    public void Press(string label)
    {
        var button = _buttons.FirstOrDefault(candidate => candidate.Text == label)
            ?? throw new InvalidOperationException($"The prompt '{Prompt.Title}' has no button '{label}'.");
        button.InvokeCommand(Terminal.Gui.Input.Command.Accept);
    }

    private Button AddButton(string text, View? left, Action run)
    {
        var button = _theme.Styled(new Button { Text = text, X = left is null ? 0 : Pos.Right(left) + 1, Y = Pos.Bottom(_error) + 1 });
        button.Accepting += (_, e) =>
        {
            e.Handled = true;
            run();
        };
        Add(button);
        _buttons.Add(button);
        return button;
    }

    private void Choose(RoofUiAction action)
    {
        string? error;
        try
        {
            error = action.Run(Fields.Select(field => field.Text).ToArray());
        }
        catch (Exception failure) when (failure is ArgumentException or FormatException or InvalidOperationException or RoofCliUsageException)
        {
            error = failure.Message;
        }

        if (error is not null)
        {
            _error.Text = error;
            return;
        }

        // An action that opened another prompt has already closed this one.
        _close(this);
    }
}
