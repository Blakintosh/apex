using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>
/// One item of a line list: its editor (a material reference, a bone, plain text), or for skinOverride the surface
/// material and its replacement. The list's last item is the "Add" field, which appends what is committed in it.
/// </summary>
public sealed class LineItemViewModel : ObservableObject
{
    private string _line;

    internal LineItemViewModel(PropertyItemViewModel main, PropertyItemViewModel? replacement, string line, bool isAdd, string placeholder,
        bool isModelSurface = false, int order = 0)
    {
        Main = main;
        Replacement = replacement;
        _line = line;
        IsAdd = isAdd;
        Placeholder = placeholder;
        IsModelSurface = isModelSurface;
        Order = order;
    }

    /// <summary>The line's editor (the surface material, for skinOverride).</summary>
    public PropertyItemViewModel Main { get; }

    /// <summary>skinOverride's replacement material (or nodraw); null for one-value lines.</summary>
    public PropertyItemViewModel? Replacement { get; }

    public bool IsPair => Replacement is not null;

    /// <summary>Opens the surface's material (a surface of the model whose material exists); null otherwise.</summary>
    public IRelayCommand? GoToCommand { get; internal set; }

    public bool CanGoTo => GoToCommand is not null;

    public bool IsPlainSurface => IsModelSurface && !CanGoTo;

    public int Columns => IsPair ? 2 : 1;

    /// <summary>The trailing field that adds an item.</summary>
    public bool IsAdd { get; }

    /// <summary>✕: removes the line, or for a surface of the model clears its replacement (the surface stays listed).</summary>
    public bool CanRemove => !IsAdd && (!IsModelSurface || IsOverridden);

    public string Placeholder { get; }

    /// <summary>
    /// skinOverride: a surface material of the model itself, listed whether or not it is overridden (as APE lists
    /// them). Its name is fixed; only its replacement is edited, and clearing that drops the line.
    /// </summary>
    public bool IsModelSurface { get; }

    /// <summary>The surface's name, for a surface of the model.</summary>
    public string Surface => Main.RawValue;

    /// <summary>The item has a line in the value (a surface of the model with no replacement has none).</summary>
    public bool IsOverridden => Line.Trim().Length > 0;

    /// <summary>The line as the GDT holds it; an item the user never edits keeps it byte for byte.</summary>
    internal string Line
    {
        get => _line;
        set
        {
            if (!SetProperty(ref _line, value))
                return;
            OnPropertyChanged(nameof(IsOverridden));
            OnPropertyChanged(nameof(CanRemove));
        }
    }

    /// <summary>Where the line sits in the value: the order it was read in, or after them all once it is added.</summary>
    internal int Order { get; set; }
}

/// <summary>
/// A line-list field (the deffile's Text entries: hideTags, xmodel materials and skinOverride, comments…): the raw value
/// is lines separated by the literal <c>\r\n</c>, edited one item per line. The raw string is the only state the record
/// sees; it is left exactly as read until an item changes, and every change (commit, add, remove) is one edit through
/// <see cref="RawValue"/>, so undo, change tracking and the journal treat it like any other field. See
/// <see cref="LineList"/> for how the value is written back.
/// </summary>
public sealed partial class LinesPropertyViewModel : PropertyItemViewModel
{
    private readonly Action<string, string>? _navigateToRef;
    private readonly Func<string, string?>? _valueOf;
    private readonly Func<string, string, bool>? _exists;
    private bool _writing;
    private bool _resetting;

    public LinesPropertyViewModel(PropertyDef def, string value, Action<string, string>? navigateToRef,
        Func<string, string?>? valueOf, Func<string, string, bool>? exists) : base(def)
    {
        _value = value;
        _navigateToRef = navigateToRef;
        _valueOf = valueOf;
        _exists = exists;
        Shape = LineList.ShapeOf(def);
        _items = Build(value);
        AddItem = MakeItem("", isAdd: true);
        RefreshModified();
        if (Shape == LineShape.SkinOverride && _valueOf is not null)
            _ = LoadSurfacesAsync();
    }

    public override double EditorMaxWidth => Shape == LineShape.Plain ? ValueWidth : WideValueWidth;

    /// <summary>The model's own surface materials (skinOverride), once read; null until then or when there are none.</summary>
    private IReadOnlyList<string>? _surfaces;

    private int _nextOrder;

    /// <summary>The list ends in an Add field (skinOverride lists the model's surfaces instead, once they are known).</summary>
    [ObservableProperty]
    private bool _canAdd = true;

    /// <summary>The xmodel's model files, as the deffile names its LODs.</summary>
    private static readonly string[] ModelFileKeys =
        { "filename", "mediumLod", "lowLod", "lowestLod", "lod4File", "lod5File", "lod6File", "lod7File" };

    /// <summary>
    /// Reads the surface materials of every model file the xmodel names (its LODs too, as APE's list does) and lists
    /// each, overridden or not. Without a readable model file the list stays as the value holds it.
    /// </summary>
    private async Task LoadSurfacesAsync()
    {
        var files = ModelFileKeys.Select(k => _valueOf!(k) ?? "").Where(f => f.Trim().Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
            return;
        var surfaces = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (await FieldFiles.SurfaceMaterialsAsync(file) is { } names)
                surfaces.UnionWith(names.Where(n => n.Length > 0));
        if (surfaces.Count == 0)
            return;
        _surfaces = surfaces.ToList();
        CanAdd = false;
        Items = Build(Value);
        OnPropertyChanged(nameof(SurfaceTitle));
        OnRowCountChanged();
    }

    public LineShape Shape { get; }

    public bool IsPairList => Shape == LineShape.SkinOverride;

    public override bool IsMultiLine => true;

    /// <summary>The model's materials count one each (the section says how many there are to look at).</summary>
    public override int RowCount => _surfaces is not null ? Math.Max(1, Items.Count) : 1;

    /// <summary>The column titles over a pair list: what is on the left, and that the right is what replaces it.</summary>
    public string SurfaceTitle => CanAdd ? "Surface material" : "Material in model";

    public string ReplacementTitle => "Override";

    [ObservableProperty]
    private string _value;

    partial void OnValueChanged(string value)
    {
        // Undo, redo, revert or another view moved the value: the items follow it. An edit made here already did.
        if (!_writing)
            Items = Build(value);
        OnEdited();
    }

    public override string RawValue
    {
        get => Value;
        set => Value = value ?? "";
    }

    /// <summary>The items (blank lines aren't items).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(ItemsTip))]
    private ObservableCollection<LineItemViewModel> _items;

    /// <summary>The trailing "Add" field.</summary>
    public LineItemViewModel AddItem { get; }

    /// <summary>"3 tags": what a table cell shows (for skinOverride, the surfaces overridden).</summary>
    public string Summary => LineList.Summary(Shape, Lines().Count());

    /// <summary>The summary's tooltip: the items one per line (not the raw value with its separators and padding).</summary>
    public string ItemsTip => Lines().Any() ? string.Join("\n", Lines().Select(i => i.Line)) : Summary;

    /// <summary>The items that have a line in the value, in the value's order.</summary>
    private IEnumerable<LineItemViewModel> Lines() => Items.Where(i => i.IsOverridden).OrderBy(i => i.Order);

    /// <summary>
    /// Removes <paramref name="item"/> (✕, or Delete in an empty item): one edit. A surface of the model stays listed,
    /// with its replacement cleared.
    /// </summary>
    public void Remove(LineItemViewModel item)
    {
        if (item.IsAdd || !Items.Contains(item))
            return;
        if (item.IsModelSurface)
        {
            if (!item.IsOverridden || item.Replacement is not { } r)
                return;
            _resetting = true;
            try
            {
                r.RawValue = "";
            }
            finally
            {
                _resetting = false;
            }
            item.Line = "";
            Validate(item);
        }
        else
        {
            Items.Remove(item);
        }
        Write();
    }

    // ── Building items ───────────────────────────────────────────────────────

    private ObservableCollection<LineItemViewModel> Build(string value)
    {
        var lines = LineList.Items(value);
        _nextOrder = lines.Count;
        if (_surfaces is null)
            return new(lines.Select((line, i) => MakeItem(line, isAdd: false, order: i)));

        // Every surface of the model, in name order, each with its line if the value overrides it (the first line, if
        // two name it); then the lines for surfaces the model doesn't have, as the value holds them.
        var used = new bool[lines.Count];
        var items = new List<LineItemViewModel>();
        foreach (var surface in _surfaces)
        {
            var at = -1;
            for (var i = 0; i < lines.Count && at < 0; i++)
                if (!used[i] && LineList.SplitPair(lines[i]).Surface.Equals(surface, StringComparison.OrdinalIgnoreCase))
                    at = i;
            if (at >= 0)
                used[at] = true;
            items.Add(at >= 0
                ? MakeItem(lines[at], isAdd: false, order: at, surface: surface)
                : MakeItem("", isAdd: false, order: int.MaxValue, surface: surface));
        }
        for (var i = 0; i < lines.Count; i++)
            if (!used[i])
                items.Add(MakeItem(lines[i], isAdd: false, order: i));
        return new(items);
    }

    /// <param name="surface">A surface of the model: the item lists it whether or not <paramref name="line"/> overrides it.</param>
    private LineItemViewModel MakeItem(string line, bool isAdd, int order = 0, string? surface = null)
    {
        PropertyItemViewModel main;
        PropertyItemViewModel? replacement = null;
        string placeholder;
        switch (Shape)
        {
            case LineShape.Material:
                main = MaterialEditor(line.Trim());
                placeholder = "Add a material";
                break;
            case LineShape.SkinOverride:
                var (named, replaced) = LineList.SplitPair(line);
                main = new SurfacePropertyViewModel(ItemDef(PropertyKind.Text), surface ?? named, _valueOf);
                replacement = MaterialEditor(replaced);
                placeholder = isAdd ? "Surface material" : "";
                break;
            case LineShape.Bone:
                main = new BonePropertyViewModel(ItemDef(PropertyKind.Text) with { TextEditor = PropertyTextEditor.Bone }, line, _valueOf);
                placeholder = "Add a tag";
                break;
            default:
                main = new TextPropertyViewModel(ItemDef(PropertyKind.Text), line);
                placeholder = "Add a line";
                break;
        }
        var item = new LineItemViewModel(main, replacement, line, isAdd, isAdd ? placeholder : "", surface is not null, order);
        // A surface of the model is a material of its own: its name opens it, when there is one to open.
        if (surface is not null && _navigateToRef is not null && (_exists?.Invoke("material", surface) ?? true))
            item.GoToCommand = new RelayCommand(() => _navigateToRef("material", surface));
        main.Edited += _ => ItemEdited(item);
        if (replacement is not null)
            replacement.Edited += _ => ItemEdited(item);
        Validate(item);
        return item;
    }

    private PropertyDef ItemDef(PropertyKind kind) =>
        new(Def.Key, Def.Label, Def.Category, kind, Def.Description) { ModelKeys = Def.ModelKeys };

    private RefPropertyViewModel MaterialEditor(string value) =>
        new(ItemDef(PropertyKind.AssetRef) with { RefType = "material" }, value,
            _navigateToRef is null ? null : r => _navigateToRef(r.RefType, r.Value));

    // ── Editing ──────────────────────────────────────────────────────────────

    // A pair is its surface material and what replaces it: without a surface there is no line (the replacement alone
    // would read back as a surface), so an emptied surface empties the line. A surface of the model without a
    // replacement isn't overridden, so it has no line either.
    private static string LineOf(LineItemViewModel item) => item.Replacement is { } r
        ? item.Main.RawValue.Trim().Length == 0 || item.IsModelSurface && r.RawValue.Trim().Length == 0
            ? ""
            : LineList.JoinPair(item.Main.RawValue, r.RawValue)
        : item.Main.RawValue;

    private void ItemEdited(LineItemViewModel item)
    {
        // A commit can land after its item was removed (focus leaving a field whose ✕ was pressed): it has no line.
        if (_resetting || !item.IsAdd && !Items.Contains(item))
            return;
        var line = LineOf(item);
        if (item.IsAdd)
        {
            if (line.Trim().Length == 0)
                return;
            // Only the surface typed so far (skinOverride): wait for its replacement before adding the line.
            if (item.Replacement is { RawValue.Length: 0 })
                return;
            Items.Add(MakeItem(line.Trim(), isAdd: false, order: _nextOrder++));
            OnPropertyChanged(nameof(Summary));
            _resetting = true;
            try
            {
                item.Main.RawValue = "";
                if (item.Replacement is { } r)
                    r.RawValue = "";
            }
            finally
            {
                _resetting = false;
            }
            Validate(item);
            Write();
            return;
        }
        if (line == item.Line)
            return;
        Validate(item);
        if (line.Trim().Length == 0 && !item.IsModelSurface)
        {
            Items.Remove(item);
            Write();
            return;
        }
        // A surface overridden for the first time adds its line after the others.
        if (!item.IsOverridden)
            item.Order = _nextOrder++;
        item.Line = line;
        Write();
    }

    /// <summary>The items written back as one edit, in the value's own shape.</summary>
    private void Write()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ItemsTip));
        var composed = LineList.Compose(Lines().Select(i => i.Line).ToList(), Value, Shape);
        if (composed == Value)
            return;
        _writing = true;
        try
        {
            Value = composed;
        }
        finally
        {
            _writing = false;
        }
    }

    /// <summary>A material that isn't in the index shows ⚠ on its own item (nodraw is not a material).</summary>
    private void Validate(LineItemViewModel item)
    {
        if (_exists is null)
            return;
        var material = Shape switch
        {
            LineShape.Material => item.Main,
            LineShape.SkinOverride => item.Replacement,
            _ => null,
        };
        if (material is null)
            return;
        material.Problem = material.RawValue.Equals(LineList.NoDraw, StringComparison.OrdinalIgnoreCase)
            ? null
            : Validator.Check(material.Def, material.RawValue.Trim(), _exists);
    }
}
