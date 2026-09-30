using System.Collections.ObjectModel;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Installer.Wizard;

/// <summary>
/// Lines to read, each wrapped at the view's width (a plan's line or a container's can be longer than the terminal is
/// wide), its continuation lines indented under the column its text is in: the value after a "Label:" column, or a
/// plan step's purpose. The arrow keys, Page Up, Page Down, Home and End scroll it, and a scroll bar shows when there is
/// more than fits; Enter is left to the Next button.
/// </summary>
internal sealed class ReadingView : ListView
{
    private IReadOnlyList<string> _lines = [];
    private string[] _shown = [];
    private int _wrappedAt = -1;
    private bool _followsEnd;
    private int? _revealed;

    public ReadingView()
    {
        // A scroll bar when there is more to read than fits.
        ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        KeyBindings.ReplaceCommands(Key.CursorUp, Command.ScrollUp);
        KeyBindings.ReplaceCommands(Key.CursorDown, Command.ScrollDown);
        AddCommand(Command.PageUp, () => ScrollVertical(-Math.Max(1, Viewport.Height)));
        AddCommand(Command.PageDown, () => ScrollVertical(Math.Max(1, Viewport.Height)));
        AddCommand(Command.Start, () => ScrollTo(0));
        AddCommand(Command.End, () => ScrollTo(int.MaxValue));
    }

    /// <summary>The lines, as given (not wrapped).</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>The lines as the view shows them, wrapped at its width.</summary>
    public IReadOnlyList<string> Shown => _shown;

    /// <summary>
    /// Shows <paramref name="lines"/>; with <paramref name="followEnd"/>, scrolled to the last of them (and kept there as
    /// the view is resized), as a log is read.
    /// </summary>
    public void Show(IReadOnlyList<string> lines, bool followEnd = false)
    {
        _lines = lines;
        _followsEnd = followEnd;
        _revealed = null;
        Rewrap();
    }

    /// <summary>
    /// Scrolls so line <paramref name="line"/> of <see cref="Lines"/> is near the top, the line before it above it, and
    /// keeps it there as the view is resized: what the person must read first.
    /// </summary>
    public void Reveal(int line)
    {
        _revealed = Math.Clamp(line, 0, Math.Max(0, _lines.Count - 1));
        Rewrap();
    }

    /// <summary>
    /// <paramref name="line"/> wrapped at <paramref name="width"/>, at spaces where it can be, each continuation indented
    /// by <see cref="HangingIndent"/>.
    /// </summary>
    public static IEnumerable<string> Wrap(string line, int width)
    {
        if (width <= 0 || line.Length <= width)
        {
            yield return line;
            yield break;
        }

        var indent = new string(' ', HangingIndent(line, width));
        var rest = line;
        var first = true;
        while (true)
        {
            var prefix = first ? string.Empty : indent;
            var room = width - prefix.Length;
            if (rest.Length <= room)
            {
                yield return prefix + rest;
                yield break;
            }

            // Break at the last space that fits, past any indent of the line's own; a word too long to fit is cut.
            var start = first ? rest.Length - rest.TrimStart(' ').Length : 0;
            var space = rest.LastIndexOf(' ', room);
            var end = space > start ? space : room;
            yield return prefix + rest[..end].TrimEnd(' ');
            rest = rest[end..].TrimStart(' ');
            first = false;
            if (rest.Length == 0)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Where a wrapped line's continuations start: under its last column (text after two or more spaces) that begins
    /// within the first half of <paramref name="width"/>, or under its first character.
    /// </summary>
    public static int HangingIndent(string line, int width)
    {
        var limit = width / 2;
        var indent = Math.Min(line.Length - line.TrimStart(' ').Length, limit);
        for (var i = indent; i < line.Length - 2 && i < limit; i++)
        {
            if (line[i] != ' ' && line[i + 1] == ' ' && line[i + 2] == ' ')
            {
                var column = i + 1;
                while (column < line.Length && line[column] == ' ')
                {
                    column++;
                }

                if (column < line.Length && column <= limit)
                {
                    indent = column;
                }

                i = column - 1;
            }
        }

        return indent;
    }

    protected override void OnViewportChanged(DrawEventArgs e)
    {
        base.OnViewportChanged(e);
        if (e.NewViewport.Width != _wrappedAt)
        {
            Rewrap();
        }
    }

    private void Rewrap()
    {
        var width = Viewport.Width;
        _wrappedAt = width;
        _shown = _lines.SelectMany(line => Wrap(line, width)).ToArray();
        SetSource(new ObservableCollection<string>(_shown));
        if (_followsEnd)
        {
            ScrollTo(int.MaxValue);
        }
        else if (_revealed is { } revealed)
        {
            ScrollTo(_lines.Take(Math.Max(0, revealed - 1)).Sum(line => Wrap(line, width).Count()));
        }
    }

    private bool ScrollTo(int row)
    {
        var last = Math.Max(0, (Source?.Count ?? 0) - Viewport.Height);
        Viewport = Viewport with { Y = Math.Clamp(row, 0, last) };
        return true;
    }
}
