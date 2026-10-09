using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Apex.Editor.Models;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>One square of the matrix: a column title, a row title, or a property's value.</summary>
public sealed partial class MatrixCell : ObservableObject
{
    internal MatrixCell(string text, bool isHeader, bool isRowHeader, PropertyItemViewModel? item, int row, int column)
    {
        _text = text;
        IsHeader = isHeader;
        IsRowHeader = isRowHeader;
        Item = item;
        Row = row;
        Column = column;
    }

    public bool IsHeader { get; }

    public bool IsRowHeader { get; }

    /// <summary>The property this square holds; null for a title, or for a square no key of the deffile fills.</summary>
    public PropertyItemViewModel? Item { get; }

    public int Row { get; }

    public int Column { get; }

    /// <summary>A value square with nothing behind it (the deffile has no such key, or hides it for this asset).</summary>
    [ObservableProperty]
    private bool _isNone;

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isChanged;

    [ObservableProperty]
    private bool _hasProblem;

    [ObservableProperty]
    private string _tip = "";
}

/// <summary>
/// A section laid out as the grid its keys make (see <see cref="KeyGridDetector"/>): one flat-list row holding every
/// square, with the selected property's own editor under it. The squares only show values; every edit goes through
/// the property's row, so undo, changes and problems work as they do in the list.
/// </summary>
public sealed partial class MatrixRowViewModel : ObservableObject, Controls.IRowStandIn
{
    private const double SquareHeight = 34, BarHeight = 56, Gaps = 20;

    private readonly MatrixCell[,] _values;

    internal MatrixRowViewModel(KeyGrid grid, Func<string, PropertyItemViewModel?> find)
    {
        Grid = grid;
        var rows = grid.Rows.Count;
        var columns = grid.Columns.Count;
        _values = new MatrixCell[rows, columns];
        var cells = new List<MatrixCell> { new("", true, true, null, -1, -1) };
        cells.AddRange(grid.Columns.Select((name, c) => new MatrixCell(name, true, false, null, -1, c)));
        for (var r = 0; r < rows; r++)
        {
            cells.Add(new MatrixCell(grid.Rows[r], false, true, null, r, -1));
            for (var c = 0; c < columns; c++)
            {
                var item = grid.Keys[r, c] is { } key ? find(key) : null;
                var cell = new MatrixCell("", false, false, item, r, c);
                if (item is not null)
                {
                    var square = cell;
                    item.Edited += _ => Refresh(square);
                }
                _values[r, c] = cell;
                cells.Add(cell);
            }
        }
        Cells = cells;
        ColumnCount = columns + 1;
        Height = SquareHeight * (rows + 1) + BarHeight + Gaps;
        Refresh();
        // Start on the first square that has a value, else the first there is.
        var start = Squares().FirstOrDefault(s => s.Item is { IsHeld: true, IsRuleHidden: false }) ?? Squares().FirstOrDefault(s => !s.IsNone);
        if (start is not null)
            Select(start.Row, start.Column);
    }

    public KeyGrid Grid { get; }

    /// <summary>Every square in reading order, titles included (the view lays them out in <see cref="ColumnCount"/> columns).</summary>
    public IReadOnlyList<MatrixCell> Cells { get; }

    public int ColumnCount { get; }

    public double Height { get; }

    private IEnumerable<MatrixCell> Squares()
    {
        for (var r = 0; r < Grid.Rows.Count; r++)
            for (var c = 0; c < Grid.Columns.Count; c++)
                yield return _values[r, c];
    }

    /// <summary>The keys the grid holds (what the list shows in its place).</summary>
    public IEnumerable<PropertyItemViewModel> Items => Squares().Where(s => s.Item is not null).Select(s => s.Item!);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedItem), nameof(HasSelectedItem), nameof(SelectedLabel), nameof(NoSelectionText))]
    private MatrixCell? _selected;

    public PropertyItemViewModel? SelectedItem => Selected is { IsNone: false } s ? s.Item : null;

    public bool HasSelectedItem => SelectedItem is not null;

    /// <summary>"Zombie · Head fatal": which property the editor under the grid is for.</summary>
    public string SelectedLabel => Selected is { } s ? Grid.Rows[s.Row] + " · " + Grid.Columns[s.Column] : "";

    public string NoSelectionText => Selected is null ? "" : "The deffile has no property for this square";

    public void Select(int row, int column)
    {
        if (row < 0 || column < 0 || row >= Grid.Rows.Count || column >= Grid.Columns.Count)
            return;
        if (Selected is { } was)
            was.IsSelected = false;
        Selected = _values[row, column];
        Selected.IsSelected = true;
    }

    /// <summary>Arrow keys: one square over, stopping at the edge.</summary>
    public void Move(int rows, int columns)
    {
        var from = Selected ?? _values[0, 0];
        Select(Math.Clamp(from.Row + rows, 0, Grid.Rows.Count - 1), Math.Clamp(from.Column + columns, 0, Grid.Columns.Count - 1));
    }

    /// <summary>Delete: the selected square's value cleared, as clearing its row would.</summary>
    public void ClearSelected()
    {
        if (SelectedItem is { } item && item.RawValue.Length > 0)
            item.RawValue = "";
    }

    /// <summary>Texts and states read again (an edit, undo, or the deffile hiding or showing a key for the asset).</summary>
    public void Refresh()
    {
        foreach (var s in Squares())
            Refresh(s);
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(HasSelectedItem));
    }

    private void Refresh(MatrixCell cell)
    {
        if (cell.Item is not { } p || p.IsRuleHidden)
        {
            cell.IsNone = true;
            cell.Text = "";
            cell.IsChanged = cell.HasProblem = false;
            cell.Tip = cell.Item is null ? "No such property" : "Hidden for this asset";
            return;
        }
        cell.IsNone = false;
        cell.Text = TextOf(p);
        cell.IsChanged = p.IsChanged;
        cell.HasProblem = p.HasProblem;
        cell.Tip = p.RawValue.Length > 0 ? p.Key + "\n" + p.RawValue : p.Key;
    }

    /// <summary>What a square shows: a file's name rather than its folder (the folder is the same all down the column).</summary>
    private static string TextOf(PropertyItemViewModel p)
    {
        var value = p.RawValue ?? "";
        switch (p.Def.Kind)
        {
            case PropertyKind.Toggle:
                return value is "1" or "true" ? "On" : "Off";
            case PropertyKind.Choice:
                return p.Def.ChoiceLabel(value);
        }
        var slash = Math.Max(value.LastIndexOf('\\'), value.LastIndexOf('/'));
        return slash >= 0 ? value[(slash + 1)..] : value;
    }

    object? Controls.IRowStandIn.CreateStandIn() => null;
}
