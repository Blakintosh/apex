using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Apex.Editor.Models;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>
/// A file field (the deffile's Path, XModel and Texture entries): a text field with a browse button that picks a file
/// under the field's folder, and a ⚠ when the value names a file that isn't on disk (see <see cref="FieldFiles"/>).
/// </summary>
public sealed partial class FilePropertyViewModel : PropertyItemViewModel
{
    public FilePropertyViewModel(PropertyDef def, string value) : base(def)
    {
        _value = value;
        RefreshModified();
    }

    [ObservableProperty]
    private string _value;

    partial void OnValueChanged(string value) => OnEdited();

    public override string RawValue
    {
        get => Value;
        set => Value = value;
    }

    /// <summary>Browsing needs the install the value is relative to; mock data has none.</summary>
    public bool CanBrowse => FieldFiles.HasInstall;

    public string BrowseTip => CanBrowse
        ? $"Choose a file in {FieldFiles.RootName(Def)}"
        : "Browsing needs the BO3 install. This is mock data.";
}

/// <summary>
/// A colour (the deffile's Color entries): "r g b a" floats from 0 to 1, as the GDTs spell them. A swatch shows it and
/// opens R, G, B (and A, when the entry shows alpha) as scrub fields plus a hex field; the text stays editable. A picker
/// edit rewrites only the component it touched, so the others keep their exact original text.
/// </summary>
public sealed partial class ColorPropertyViewModel : PropertyItemViewModel
{
    private static readonly string[] Names = { "R", "G", "B", "A" };

    private NumberPropertyViewModel[]? _components;
    private bool _syncing;
    private IBrush? _swatch;

    public ColorPropertyViewModel(PropertyDef def, string value) : base(def)
    {
        _value = value;
        RefreshModified();
    }

    [ObservableProperty]
    private string _value;

    partial void OnValueChanged(string value)
    {
        _swatch = null;
        OnEdited();
        OnPropertyChanged(nameof(Swatch));
        OnPropertyChanged(nameof(Hex));
        SyncComponents();
    }

    public override string RawValue
    {
        get => Value;
        set => Value = value;
    }

    public bool ShowAlpha => Def.ShowAlpha;

    /// <summary>The colour as the swatch paints it (components clamped to 0–1; alpha over the checkerboard).</summary>
    public IBrush Swatch => _swatch ??= new SolidColorBrush(ToColor(Parts(Value), ShowAlpha));

    /// <summary>R, G, B as #RRGGBB (alpha has its own field).</summary>
    public string Hex
    {
        get
        {
            var c = ToColor(Parts(Value), false);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
    }

    /// <summary>The picker's scrub fields, built the first time the picker opens.</summary>
    public IReadOnlyList<NumberPropertyViewModel> Components => _components ??= BuildComponents();

    private NumberPropertyViewModel[] BuildComponents()
    {
        var parts = Parts(Value);
        var defaults = Parts(Def.Default);
        var count = ShowAlpha ? 4 : 3;
        var result = new NumberPropertyViewModel[count];
        for (var i = 0; i < count; i++)
        {
            var def = new PropertyDef($"{Def.Key}.{Names[i]}", Names[i], Def.Category, PropertyKind.Number, "")
            {
                Default = defaults[i],
                Min = 0,
                Max = 1,
                Step = 0.01,
            };
            var component = new NumberPropertyViewModel(def, parts[i]);
            var index = i;
            component.Edited += c => ComponentEdited(index, (NumberPropertyViewModel)c);
            result[i] = component;
        }
        return result;
    }

    private void ComponentEdited(int index, NumberPropertyViewModel component)
    {
        if (_syncing)
            return;
        // A scrub joins one undo step, exactly as it does on a number row.
        IsContinuousEdit = component.IsContinuousEdit;
        try
        {
            Value = WithComponent(Value, index, component.RawValue);
        }
        finally
        {
            IsContinuousEdit = false;
        }
    }

    /// <summary>The value moved from elsewhere (typing, undo, another view): the picker's fields follow it.</summary>
    private void SyncComponents()
    {
        if (_components is null || _syncing)
            return;
        var parts = Parts(Value);
        _syncing = true;
        try
        {
            for (var i = 0; i < _components.Length; i++)
                if (_components[i].RawValue != parts[i])
                    _components[i].RawValue = parts[i];
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Sets R, G, B from "#RRGGBB" or "RRGGBB"; false (nothing written) when the text isn't one.</summary>
    public bool CommitHex(string text)
    {
        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;
        if (string.Equals(text.Trim().TrimStart('#'), Hex.TrimStart('#'), StringComparison.OrdinalIgnoreCase))
            return true; // the same colour: every component keeps its exact text
        var value = WithComponent(Value, 0, Channel((rgb >> 16) & 0xFF));
        value = WithComponent(value, 1, Channel((rgb >> 8) & 0xFF));
        Value = WithComponent(value, 2, Channel(rgb & 0xFF));
        return true;
    }

    /// <summary>
    /// <paramref name="value"/> with component <paramref name="index"/> replaced by <paramref name="text"/> and every
    /// other character left as written (spacing, extra tokens, a missing alpha). A value too short for the index is
    /// padded with the components <see cref="Parts"/> would show.
    /// </summary>
    internal static string WithComponent(string value, int index, string text)
    {
        var found = 0;
        for (var i = 0; i < value.Length;)
        {
            if (value[i] == ' ')
            {
                i++;
                continue;
            }
            var end = value.IndexOf(' ', i);
            if (end < 0)
                end = value.Length;
            if (found++ == index)
                return string.Concat(value.AsSpan(0, i), text, value.AsSpan(end));
            i = end;
        }
        var parts = Parts(value);
        var padded = new System.Text.StringBuilder(value.TrimEnd());
        for (var i = found; i <= index; i++)
            padded.Append(padded.Length > 0 ? " " : "").Append(i == index ? text : parts[i]);
        return padded.ToString();
    }

    /// <summary>A byte channel as the GDTs write a colour component: six significant digits (10 → 0.0392157).</summary>
    internal static string Channel(uint value) => (value / 255.0).ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>The value's four components as written ("0" fills a missing one, "1" a missing alpha).</summary>
    internal static string[] Parts(string value)
    {
        var parts = new[] { "0", "0", "0", "1" };
        var given = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Math.Min(4, given.Length); i++)
            parts[i] = given[i];
        return parts;
    }

    private static Color ToColor(string[] parts, bool alpha) =>
        Color.FromArgb(alpha ? Byte(parts[3]) : (byte)255, Byte(parts[0]), Byte(parts[1]), Byte(parts[2]));

    private static byte Byte(string component) =>
        double.TryParse(component, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (byte)Math.Round(Math.Clamp(v, 0, 1) * 255)
            : (byte)0;
}

/// <summary>
/// A text field that offers values from a list it loads on demand (a FileCombo's files, a BoneCombo's bones). Free
/// text is always allowed; the list only suggests. Loading runs off the UI thread and a newer request supersedes an
/// older one.
/// </summary>
public abstract partial class SuggestPropertyViewModel : PropertyItemViewModel
{
    private int _request;

    protected SuggestPropertyViewModel(PropertyDef def, string value) : base(def)
    {
        _value = value;
        RefreshModified();
    }

    [ObservableProperty]
    private string _value;

    partial void OnValueChanged(string value) => OnEdited();

    public override string RawValue
    {
        get => Value;
        set => Value = value;
    }

    /// <summary>The offered values; null until loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSuggest))]
    private IReadOnlyList<string>? _suggestions;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Whether the field offers a list (its ▾): not without an install, and not once a load found nothing to offer —
    /// then it is a plain text field, with no message about it.
    /// </summary>
    public bool CanSuggest => FieldFiles.HasInstall && HasSource && Suggestions is not { Count: 0 };

    /// <summary>Whether there is anything to list from, as far as can be told without the disk.</summary>
    protected virtual bool HasSource => true;

    /// <summary>What the list is loaded from (a bone list's model); a list loaded for another source is stale.</summary>
    protected virtual string Source => "";

    private string? _loadedFor;

    /// <summary>
    /// The field is about to be used (hovered or focused): its ▾ follows a source that changed meanwhile (gunModel set
    /// after the row was built), and a list missing or loaded for another source loads now.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(CanSuggest));
        if (!IsLoading && (Suggestions is null || _loadedFor != Source))
            _ = LoadSuggestionsAsync();
    }

    /// <summary>One line for an open list with nothing in it.</summary>
    public abstract string EmptyText { get; }

    /// <summary>Loads (or reloads; the load itself is cached) the suggestions; the latest call wins.</summary>
    public async Task LoadSuggestionsAsync()
    {
        if (!FieldFiles.HasInstall)
            return;
        var request = ++_request;
        var source = Source;
        IsLoading = true;
        IReadOnlyList<string>? loaded;
        try
        {
            loaded = await Fetch();
        }
        catch (Exception)
        {
            loaded = null; // nothing readable to offer: the field stays a plain text field
        }
        if (request != _request)
            return;
        IsLoading = false;
        _loadedFor = source;
        Suggestions = loaded ?? Array.Empty<string>();
    }

    protected abstract Task<string[]?> Fetch();
}

/// <summary>A FileCombo: a file name from the entry's folder (e.g. share/raw/accuracy/aivsai/), or any text.</summary>
public sealed class FileListPropertyViewModel : SuggestPropertyViewModel
{
    public FileListPropertyViewModel(PropertyDef def, string value) : base(def, value)
    {
    }

    public override string EmptyText => $"No files in {FieldFiles.RootName(Def)}";

    protected override async Task<string[]?> Fetch() => await FieldFiles.ListFilesAsync(Def);
}

/// <summary>
/// The surface side of a skinOverride line: one of the materials the xmodel's own model file (its <c>filename</c>)
/// uses, or any text. Without a readable model file it is a plain text field.
/// </summary>
public sealed class SurfacePropertyViewModel : SuggestPropertyViewModel
{
    private readonly Func<string, string?>? _valueOf;

    public SurfacePropertyViewModel(PropertyDef def, string value, Func<string, string?>? valueOf) : base(def, value)
    {
        _valueOf = valueOf;
    }

    /// <summary>The model file whose surfaces are offered.</summary>
    public string ModelFile => _valueOf?.Invoke("filename") ?? "";

    public override string EmptyText => "No materials in the model file";

    protected override bool HasSource => ModelFile.Length > 0;

    protected override Task<string[]?> Fetch() => FieldFiles.SurfaceMaterialsAsync(ModelFile);
}

/// <summary>
/// A BoneCombo: a bone (tag) of the model another property names (attachViewModelTag1 offers gunModel's bones), or
/// any text. When that model can't be resolved to a readable xmodel_bin, the field is a plain text field.
/// </summary>
public sealed class BonePropertyViewModel : SuggestPropertyViewModel
{
    private readonly Func<string, string?>? _valueOf;

    public BonePropertyViewModel(PropertyDef def, string value, Func<string, string?>? valueOf) : base(def, value)
    {
        _valueOf = valueOf;
    }

    /// <summary>The model the bones come from: the first of the entry's model keys that holds a value.</summary>
    public string Model
    {
        get
        {
            if (_valueOf is null)
                return "";
            foreach (var key in Def.ModelKeys)
                if (_valueOf(key) is { Length: > 0 } model)
                    return model;
            return "";
        }
    }

    public override string EmptyText => "No bones to offer";

    /// <summary>No model named (a weapon without gunModel): a plain text field from the start, no ▾ to vanish.</summary>
    protected override bool HasSource => Model.Length > 0;

    protected override string Source => Model;

    protected override Task<string[]?> Fetch() => FieldFiles.BonesAsync(Model);
}
