namespace HVO.RoofControllerV4.TerminalUi;

/// <summary>A cell-wrapped, keyboard-scrollable block whose complete text remains available.</summary>
public sealed class TerminalTextView : TerminalReadingView
{
    private string _text = string.Empty;

    /// <summary>The complete text, before wrapping.</summary>
    public new string Text
    {
        get => _text;
        set
        {
            _text = value;
            Show(value.Split('\n'));
        }
    }
}
