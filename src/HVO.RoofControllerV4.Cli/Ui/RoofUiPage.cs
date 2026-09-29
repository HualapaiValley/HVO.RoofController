using System.Collections.ObjectModel;
using HVO.RoofControllerV4.Common.Models;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace HVO.RoofControllerV4.Cli.Ui;

/// <summary>One page of the terminal interface. The shell tells it when it is shown and when the status or connection changes.</summary>
internal abstract class RoofUiPage : View
{
    protected RoofUiPage(RoofTerminalUi ui, string title)
    {
        Ui = ui;
        Title = title;
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
    }

    private readonly Dictionary<ListView, IReadOnlyList<string>> _lines = [];

    protected RoofTerminalUi Ui { get; }

    /// <summary>The lines last shown in <paramref name="list"/>.</summary>
    protected IReadOnlyList<string> LinesOf(ListView list) => _lines.GetValueOrDefault(list) ?? [];

    /// <summary>The page was switched to.</summary>
    public virtual void Shown()
    {
    }

    /// <summary>The status, or whether it is stale, changed.</summary>
    public virtual void StatusChanged()
    {
    }

    /// <summary>The connection or the caller changed.</summary>
    public virtual void ConnectionChanged()
    {
    }

    /// <summary>The page's text, for tests and for reading without a screen.</summary>
    public abstract string Describe();

    protected bool IsAdmin => Ui.Caller?.Role == RoofControllerApiContract.AdminRole;

    protected bool IsOperator => Ui.Caller?.Role is RoofControllerApiContract.OperatorRole or RoofControllerApiContract.AdminRole;

    protected Button AddButton(string text, View? left, Action run, Pos? y = null)
    {
        var button = new Button { Text = text, X = left is null ? 0 : Pos.Right(left) + 1, Y = y ?? Pos.AnchorEnd(1) };
        button.Accepting += (_, e) =>
        {
            e.Handled = true;
            run();
        };
        Add(button);
        return button;
    }

    protected static Label TextBlock(Pos y, Dim height) => new() { X = 0, Y = y, Width = Dim.Fill(), Height = height };

    protected static string Rows(IEnumerable<(string Label, string Value)> rows)
    {
        using var writer = new StringWriter();
        RoofCliFormat.WriteRows(writer, rows);
        return writer.ToString().TrimEnd();
    }

    protected static string[] Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        using var writer = new StringWriter();
        RoofCliFormat.WriteTable(writer, headers, rows);
        return writer.ToString().TrimEnd().Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
    }

    /// <summary>A list whose items are lines of text; its selection survives a refresh when the line count allows.</summary>
    protected void SetLines(ListView list, IReadOnlyList<string> lines)
    {
        _lines[list] = lines;
        var selected = list.SelectedItem;
        list.SetSource(new ObservableCollection<string>(lines));
        if (lines.Count > 0)
        {
            list.SelectedItem = Math.Clamp(selected ?? 0, 0, lines.Count - 1);
        }
    }
}
