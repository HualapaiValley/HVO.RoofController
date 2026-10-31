using System.Collections.ObjectModel;
using HVO.RoofControllerV4.TerminalUi;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>
/// Items to read and pick from: each item wrapped at the view's width (as <see cref="TerminalReadingView"/> wraps a line),
/// the arrow keys moving the selection row by row, and Enter (or a double click) accepting the item the selected row
/// belongs to. The selection stays on its item when the items are shown again.
/// </summary>
internal sealed class RoofUiItemList : ListView
{
    private IReadOnlyList<string> _items = [];
    private int[] _owners = [];
    private int _wrappedAt = -1;

    public RoofUiItemList()
    {
        // A scroll bar when there is more to read than fits.
        ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
    }

    /// <summary>The items, as given (not wrapped).</summary>
    public IReadOnlyList<string> Items => _items;

    /// <summary>The item the selected row belongs to; null when there is none.</summary>
    public int? SelectedIndex => SelectedItem is { } row && row >= 0 && row < _owners.Length ? _owners[row] : null;

    /// <summary>
    /// Shows <paramref name="items"/>, keeping the selection on the item of the same index. The same items again change
    /// nothing, so a status that arrives every second does not move what the person is reading.
    /// </summary>
    public void Show(IReadOnlyList<string> items)
    {
        if (items.SequenceEqual(_items, StringComparer.Ordinal))
        {
            return;
        }

        var keep = SelectedIndex;
        _items = items;
        Rewrap(keep);
    }

    /// <summary>Selects item <paramref name="index"/>: its first row.</summary>
    public void Select(int index)
    {
        var row = Array.IndexOf(_owners, index);
        if (row < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"There is no item {index}.");
        }

        SelectedItem = row;
    }

    protected override void OnViewportChanged(DrawEventArgs e)
    {
        base.OnViewportChanged(e);
        if (e.NewViewport.Width != _wrappedAt)
        {
            Rewrap(SelectedIndex);
        }
    }

    private void Rewrap(int? keep)
    {
        var width = Viewport.Width;
        _wrappedAt = width;
        var rows = new List<string>();
        var owners = new List<int>();
        for (var index = 0; index < _items.Count; index++)
        {
            foreach (var row in TerminalReadingView.Wrap(_items[index], width))
            {
                rows.Add(row);
                owners.Add(index);
            }
        }

        _owners = [.. owners];
        SetSource(new ObservableCollection<string>(rows));
        if (rows.Count > 0)
        {
            var item = Math.Clamp(keep ?? 0, 0, _items.Count - 1);
            SelectedItem = Math.Max(0, Array.IndexOf(_owners, item));
        }
    }
}
