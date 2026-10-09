using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>Base VM for one editable property row.</summary>
public abstract partial class PropertyItemViewModel : ObservableObject, Controls.IRowStandIn
{
    protected PropertyItemViewModel(PropertyDef def)
    {
        Def = def;
    }

    /// <summary>The same kind of row at its default, for a pooled editor row to park on: no asset, no callbacks.</summary>
    object? Controls.IRowStandIn.CreateStandIn() => this switch
    {
        TextPropertyViewModel => new TextPropertyViewModel(Def, Def.Default),
        NumberPropertyViewModel => new NumberPropertyViewModel(Def, Def.Default),
        TogglePropertyViewModel => new TogglePropertyViewModel(Def, Def.Default),
        ChoicePropertyViewModel => new ChoicePropertyViewModel(Def, Def.Default),
        RefPropertyViewModel => new RefPropertyViewModel(Def, Def.Default, null),
        FilePropertyViewModel => new FilePropertyViewModel(Def, Def.Default),
        ColorPropertyViewModel => new ColorPropertyViewModel(Def, Def.Default),
        FileListPropertyViewModel => new FileListPropertyViewModel(Def, Def.Default),
        SurfacePropertyViewModel => new SurfacePropertyViewModel(Def, Def.Default, null),
        BonePropertyViewModel => new BonePropertyViewModel(Def, Def.Default, null),
        LinesPropertyViewModel => new LinesPropertyViewModel(Def, Def.Default, null, null, null),
        RecordsPropertyViewModel records => new RecordsPropertyViewModel(records.List, "", null, null),
        PartsPropertyViewModel parts => new PartsPropertyViewModel(Def, parts.PartDefs, "", null),
        _ => null,
    };

    public PropertyDef Def { get; }
    public string Label => Def.Label;

    private string? _accessibleName;

    /// <summary>What a screen reader calls the editor: the label, or for a table cell its column and row ("Pitch row 2"),
    /// since a row's cells share their labels with every other row's.</summary>
    public string AccessibleName
    {
        get => _accessibleName ?? Label;
        set => SetProperty(ref _accessibleName, value);
    }
    public string Key => Def.Key;
    public string Description => Def.Description;

    /// <summary>
    /// How wide every value editor grows, whatever its kind: one edge down the form, so a number, a dropdown and a path
    /// line up and the row actions beside them sit in one column.
    /// </summary>
    public const double ValueWidth = 360;

    /// <summary>
    /// How wide a row that holds several named boxes or several columns (a vector, a list of pairs) may grow, where one
    /// <see cref="ValueWidth"/> would clip their labels and names while the form has room beside them.
    /// </summary>
    public const double WideValueWidth = 640;

    /// <summary>How wide this row's value editor grows (<see cref="ValueWidth"/> unless the row is compound).</summary>
    public virtual double EditorMaxWidth => ValueWidth;

    /// <summary>The editor grows past one line (a line list in an editor row); the row grows with it.</summary>
    public virtual bool IsMultiLine => false;

    /// <summary>What a section's count adds for this row: one, or for a list of the model's materials each of them.</summary>
    public virtual int RowCount => 1;

    /// <summary>Raised when <see cref="RowCount"/> changed under a row already in a section.</summary>
    public event Action? RowCountChanged;

    protected void OnRowCountChanged() => RowCountChanged?.Invoke();

    private string? _tooltip;

    public string Tooltip => _tooltip ??= $"{Def.Key}\n\n{Def.Description}\n\nDefault: {(Def.Default.Length == 0 ? "(empty)" : Def.Default)}"
        + (Def.Extension.Length > 0 ? $"\n\nFrom the {Def.Extension} extension. Saved as {StoredAs} in the .gdtx beside the GDT." : "");

    /// <summary>The key a value is saved under, as the tooltip names it (a table's numbered keys: wtKick1, wtKick2…).</summary>
    private string StoredAs => Def.Key.EndsWith('#') ? $"{Def.Key[..^1]}1, {Def.Key[..^1]}2…" : Def.Key;

    /// <summary>Differs from the schema default ("off-default"), as is:modified means it.</summary>
    [ObservableProperty]
    private bool _isModified;

    // ── Session change ("Changed" everywhere in the UI) ──────────────────────
    /// <summary>The value before this session touched the asset; see <see cref="Models.AssetRecord.SessionBaseline"/>.</summary>
    public string BaselineValue { get; private set; } = "";

    /// <summary>Edited this session: the one thing amber means (row bar, rail dot, Changed view).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRevertParent), nameof(ShowWas))]
    private bool _isChanged;

    /// <summary>The asset holds a value for this key (a line in its GDT, or its parent's): what the Set view lists.</summary>
    public bool IsHeld { get; set; }

    /// <summary>Put in the Set view by hand (Add property, Go to field) or edited there: it stays for the session.</summary>
    public bool IsRevealed { get; set; }

    public void InitBaseline(string baseline)
    {
        BaselineValue = baseline;
        RefreshChanged();
        OnMarksChanged();
    }

    private void RefreshChanged() => IsChanged = !AssetQuery.ValuesEqual(RawValue, BaselineValue);

    /// <summary>Reverts this session's edit (↶). Reverting to the parent lives on ↑.</summary>
    [RelayCommand]
    private void RevertChange() => RawValue = BaselineValue;

    // ── Row interaction state ────────────────────────────────────────────────
    /// <summary>The row the editor's keyboard focus is in; its details follow in the Inspector.</summary>
    [ObservableProperty]
    private bool _isFocused;

    /// <summary>↑ marks an override of the parent that this session didn't create (↶ covers those).</summary>
    public bool ShowRevertParent => IsOverride && !IsChanged;

    /// <summary>
    /// The value before this session's change, struck through after the new one: for a single value. A list's or a
    /// switch's raw text says nothing a reader can use, and a file's path would take its field's room (the ↶ tooltip
    /// still has each).
    /// </summary>
    public bool ShowWas => IsChanged && !IsMultiLine && this is not TogglePropertyViewModel and not FilePropertyViewModel;

    /// <summary>Bright is yours, quiet is inherited: values that just follow the parent render dim.</summary>
    public bool IsOwned => !IsInherited;

    // ── Inheritance provenance ───────────────────────────────────────────────
    /// <summary>Effective value on the parent chain, or null when the asset has no parent value for this key.</summary>
    public string? ParentValue { get; private set; }

    public bool HasProvenance => ParentValue is not null;

    [ObservableProperty]
    private bool _isOverride;

    public bool IsInherited => HasProvenance && !IsOverride;

    public string ProvenanceTip => IsOverride
        ? $"Overrides parent value: {(ParentValue?.Length == 0 ? "(empty)" : ParentValue)}\nClick to revert to the inherited value."
        : "Inherited — matches the parent chain value.";

    public void InitProvenance(string? parentValue)
    {
        ParentValue = parentValue;
        if (!UpdateProvenance())
            NotifyProvenanceDerived();
        OnPropertyChanged(nameof(HasProvenance));
        OnMarksChanged();
    }

    /// <summary>Recomputes <see cref="IsOverride"/>; returns true (having notified its dependents) when it flipped.</summary>
    private bool UpdateProvenance()
    {
        var wasOverride = IsOverride;
        IsOverride = ParentValue is not null && !AssetQuery.ValuesEqual(RawValue, ParentValue);
        if (wasOverride == IsOverride)
            return false;
        // ParentValue is fixed once provenance is set, so these only move when IsOverride does.
        NotifyProvenanceDerived();
        return true;
    }

    private void NotifyProvenanceDerived()
    {
        OnPropertyChanged(nameof(IsInherited));
        OnPropertyChanged(nameof(IsOwned));
        OnPropertyChanged(nameof(ProvenanceTip));
        OnPropertyChanged(nameof(ShowRevertParent));
    }

    [RelayCommand]
    private void RevertToParent()
    {
        if (ParentValue is not null)
            RawValue = ParentValue;
    }

    // ── Deffile display rules (APE conditional visibility) ───────────────────
    /// <summary>Hidden by the type's GenerateUI script for this asset's current state.</summary>
    [ObservableProperty]
    private bool _isRuleHidden;

    /// <summary>Shown but not editable per the script (e.g. explosion fields without bulletImpactExplode).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisabledTip), nameof(ShowDisabledMark))]
    private bool _isRuleDisabled;

    /// <summary>What enables this row, when a field the deffile reads is found to ("Bullet Impact Explode is Off. Set it to
    /// On to edit this."); null when unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisabledTip))]
    private string? _enabledBy;

    public string? DisabledTip => !IsRuleDisabled ? null
        : EnabledBy is { } how ? "Disabled by this type's deffile: " + how
        : "Disabled by a rule in this type's deffile for the asset's current values.";

    /// <summary>The ⊘ shows in the mark slot while the row is disabled and has no problem to show there instead.</summary>
    public bool ShowDisabledMark => IsRuleDisabled && !HasProblem;

    // ── Validation ───────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem), nameof(ShowDisabledMark))]
    private string? _problem;

    public bool HasProblem => Problem is not null;

    partial void OnProblemChanged(string? value) => ProblemChanged();

    /// <summary>A kind whose affordances depend on validity (a reference's →) refreshes them here.</summary>
    protected virtual void ProblemChanged()
    {
    }

    /// <summary>Raised after any user edit; the editor VM persists the value back to the record.</summary>
    public event Action<PropertyItemViewModel>? Edited;

    /// <summary>Canonical GDT string value.</summary>
    public abstract string RawValue { get; set; }

    protected bool Suppress { get; set; }

    /// <summary>The edit being raised is a scrub tick or arrow step: it joins the run before it in the undo history.</summary>
    public bool IsContinuousEdit { get; protected set; }

    protected void OnEdited()
    {
        if (Suppress)
            return;
        IsModified = !AssetQuery.ValuesEqual(RawValue, Def.Default);
        UpdateProvenance();
        RefreshChanged();
        OnValueDisplayChanged();
        Edited?.Invoke(this);
    }

    public void RefreshModified()
    {
        IsModified = !AssetQuery.ValuesEqual(RawValue, Def.Default);
        UpdateProvenance();
    }

    /// <summary>Hook for display-only derived properties (range fraction, formatted value).</summary>
    protected virtual void OnValueDisplayChanged() { }

    /// <summary>What an empty value means, shown in the empty field: the default the game uses, unless the host names
    /// the field instead (a notetrack parameter says what the deffile calls it for its action).</summary>
    public string Placeholder => _placeholderName ?? DefaultPlaceholder;

    protected virtual string DefaultPlaceholder => Def.Default;

    private string? _placeholderName;
    public string? PlaceholderName
    {
        get => _placeholderName;
        set
        {
            if (_placeholderName == value) return;
            _placeholderName = value;
            OnPropertyChanged(nameof(Placeholder));
        }
    }

    /// <summary>The value as the Inspector shows it (empty values spelled out).</summary>
    public virtual string DisplayValue => RawValue.Length == 0 ? "(empty)" : RawValue;

    public bool MatchesFilter(string query) =>
        Label.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Key.Contains(query, StringComparison.OrdinalIgnoreCase)
        || RawValue.Contains(query, StringComparison.OrdinalIgnoreCase)
        || MatchesMore(query);

    /// <summary>A row made of parts or columns also matches by their names.</summary>
    protected virtual bool MatchesMore(string query) => false;

    /// <summary>
    /// What an edit from <paramref name="before"/> to <paramref name="after"/> did, as the words after "Undid" or "Redid",
    /// for a row whose value isn't for reading ("Kick patterns row 2", "Spring, view, hip: Stiffness"); null to name
    /// the row and show its value.
    /// </summary>
    public virtual string? DescribeStep(string before, string after) => null;

    /// <summary>The baseline or the parent's value moved: a row made of parts passes them on.</summary>
    protected virtual void OnMarksChanged() { }
}

public sealed partial class TextPropertyViewModel : PropertyItemViewModel
{
    public TextPropertyViewModel(PropertyDef def, string value) : base(def)
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
}

public sealed partial class NumberPropertyViewModel : PropertyItemViewModel
{
    public NumberPropertyViewModel(PropertyDef def, string value) : base(def)
    {
        // The GDT string is kept exactly as the file spells it until the user edits it: formatting
        // it back from a number would round 0.3125 to 0.313 and flag an untouched row as changed.
        // The editor never clamps data either; out-of-range values are flagged by validation.
        _text = value;
        _isNumber = TryParse(value, out _value);
        RefreshModified();
    }

    private string _text;
    private decimal _value;
    private bool _isNumber;

    public bool HasRange => Def.HasRange;

    /// <summary>The value as a number (0 when the GDT holds something that isn't one).</summary>
    public decimal Value => _value;

    /// <summary>
    /// The value as the field shows it: the GDT's own text, digit for digit (no rounding, no thousands separators), as
    /// APE reads it. A shown value that isn't the saved one would be a lie, and the field would jump when clicked.
    /// </summary>
    public string DisplayText => _text;

    public override string DisplayValue => _text.Length == 0 ? "(empty)" : _text;

    protected override void OnValueDisplayChanged()
    {
        OnPropertyChanged(nameof(DisplayText));
    }

    /// <summary>
    /// Steps the value by <paramref name="steps"/> increments (scrub drag, ↑↓, ‹ ›). The delta is what
    /// moves, not a grid the value snaps to: Ctrl+↑ on 0.1 with a step of 1 is 1.1, never 1. Decimal
    /// arithmetic keeps 0.1 + 0.01 exact, so no 0.30000001-style residue builds up.
    /// </summary>
    public void Nudge(double steps)
    {
        var step = (decimal)(Def.Step > 0 ? Def.Step : 1);
        var delta = Math.Round(step * (decimal)steps, 10);
        if (Def.IsInteger)
        {
            // Integers move by whole units; a fine (Alt) step still moves one.
            delta = Math.Round(delta, MidpointRounding.AwayFromZero);
            if (delta == 0)
                delta = Math.Sign(steps) * Math.Max(1, Math.Round(step));
        }
        IsContinuousEdit = true;
        try
        {
            SetNumber(Value + delta);
        }
        finally
        {
            IsContinuousEdit = false;
        }
    }

    /// <summary>
    /// Set by a row made of parts: typed or pasted text that is a comma-separated list (not a number with thousands
    /// separators) goes there to fill the parts, instead of being read as one number with its commas dropped.
    /// </summary>
    public Func<string, bool>? ListPaste { get; set; }

    private static readonly System.Text.RegularExpressions.Regex Thousands =
        new(@"^[+-]?\d{1,3}(,\d{3})+(\.\d+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Parses typed input; returns false (leaving the value alone) when it isn't a number.</summary>
    public bool TryCommitText(string text)
    {
        if (ListPaste is { } paste && text.Contains(',') && !Thousands.IsMatch(text))
            return paste(text);
        if (!TryParse(text.Replace(",", ""), out var d))
            return false;
        SetNumber(d);
        return true;
    }

    /// <summary>Sets a new number; the same number keeps its original spelling (0.50 stays 0.50).</summary>
    private void SetNumber(decimal value)
    {
        if (_isNumber && value == _value)
            return;
        RawValue = Format(value);
    }

    public override string RawValue
    {
        get => _text;
        set
        {
            value ??= "";
            if (value == _text)
                return;
            _text = value;
            _isNumber = TryParse(value, out _value);
            OnPropertyChanged(nameof(Value));
            OnEdited();
        }
    }

    private static bool TryParse(string s, out decimal d)
    {
        if (decimal.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out d))
            return true;
        d = 0;
        return false;
    }

    /// <summary>Round-trip spelling: every digit the decimal holds, no trailing zeros, no exponent.</summary>
    internal static string Format(decimal d) =>
        d.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>A GDT number as the editor displays it: as the GDT spells it (compare mode shows values the same way).</summary>
    internal static string DisplayOf(string raw) => raw;
}

public sealed partial class TogglePropertyViewModel : PropertyItemViewModel
{
    public TogglePropertyViewModel(PropertyDef def, string value) : base(def)
    {
        _isOn = value == "1";
        RefreshModified();
    }

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value) => OnEdited();

    public override string RawValue
    {
        get => IsOn ? "1" : "0";
        set => IsOn = value == "1";
    }

    public string StateText => IsOn ? "On" : "Off";
    public override string DisplayValue => StateText;

    protected override void OnValueDisplayChanged() => OnPropertyChanged(nameof(StateText));
}

public sealed partial class ChoicePropertyViewModel : PropertyItemViewModel
{
    public ChoicePropertyViewModel(PropertyDef def, string value) : base(def)
    {
        _value = value;
        _options = def.Choices;
        _labels = def.ChoiceLabels;
        _choices = WithValue(def.Choices, value);
        RefreshModified();
    }

    private string[] _options;
    private string[]? _labels;

    /// <summary>Options offered (the asset's value among them); the deffile can rebuild these per asset state (SchemaOverlay).</summary>
    [ObservableProperty]
    private string[] _choices;

    /// <summary>
    /// The options the deffile offers this asset, without the value the list keeps for display: what the value is
    /// checked against. A deffile can build a different list per asset (image's compressionMethod for a diffuse map).
    /// </summary>
    public string[] Options => _options;

    /// <summary>
    /// Swaps in script-computed options (and what APE shows for each). The current value is always kept in the list —
    /// replacing the ItemsSource would otherwise coerce SelectedItem to null and erase the asset's data. True when the
    /// options changed.
    /// </summary>
    public bool UpdateChoices(string[] choices, string[]? labels = null)
    {
        var current = Value ?? "";
        var relabelled = !LabelsEqual(labels, _labels);
        var changed = relabelled || !choices.AsSpan().SequenceEqual(_options);
        _options = choices;
        _labels = labels;
        if (relabelled)
        {
            _items = null;
            OnPropertyChanged(nameof(HasLabels));
        }
        choices = WithValue(choices, current);
        if (!relabelled && choices.AsSpan().SequenceEqual(Choices))
            return changed;
        Suppress = true;
        Choices = choices;
        if (Value != current)
            Value = current;
        Suppress = false;
        OnPropertyChanged(nameof(Placeholder));
        OnPropertyChanged(nameof(DisplayValue));
        return changed;
    }

    private static bool LabelsEqual(string[]? a, string[]? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.AsSpan().SequenceEqual(b));

    /// <summary>
    /// The options with the asset's value among them, spelled exactly as the GDT has it: a value
    /// the list lacks is appended, and one differing only in case replaces its option. Either way
    /// the dropdown can select it, instead of coercing the selection (and the data) to null.
    /// </summary>
    private static string[] WithValue(string[] choices, string value)
    {
        if (value.Length == 0 || Array.IndexOf(choices, value) >= 0)
            return choices;
        var copy = new string[choices.Length + 1];
        choices.CopyTo(copy, 0);
        for (var i = 0; i < choices.Length; i++)
            if (string.Equals(choices[i], value, StringComparison.OrdinalIgnoreCase))
            {
                copy[i] = value;
                return copy[..^1];
            }
        copy[^1] = value;
        return copy;
    }

    [ObservableProperty]
    private string? _value;

    /// <summary>
    /// A value set from outside the dropdown (undo, table, compare, sync) joins the options before the dropdown hears of
    /// it; a value the list lacks would otherwise be coerced to nothing and written back, erasing it.
    /// </summary>
    partial void OnValueChanging(string? value)
    {
        if (value is { Length: > 0 } && Array.IndexOf(Choices, value) < 0)
            Choices = WithValue(Choices, value);
    }

    partial void OnValueChanged(string? value) => OnEdited();

    public override string RawValue
    {
        get => Value ?? "";
        set => Value = value;
    }

    /// <summary>
    /// The options have names of their own to show: an extension's labelled choices, or a deffile's "Display{value}"
    /// options, which APE lists by their display text ("Best color compression"). The dropdown shows each label and
    /// stores its value.
    /// </summary>
    public bool HasLabels => _labels is not null;

    /// <summary>A stored value as the dropdown shows it: its option's label; a value the options lack as it is stored (an
    /// extension's labelled list says "custom: x").</summary>
    public string LabelOf(string value)
    {
        if (_labels is null)
            return value;
        for (var i = 0; i < _options.Length; i++)
            if (_options[i].Equals(value, StringComparison.OrdinalIgnoreCase))
                return _labels[i];
        return Def.Extension.Length > 0 ? Def.ChoiceLabel(value) : value;
    }

    private (string[] Of, ChoiceItem[] Items)? _items;

    /// <summary>The options as the dropdown lists them when they are labelled.</summary>
    public IReadOnlyList<ChoiceItem> Items
    {
        get
        {
            if (_items is { } cached && ReferenceEquals(cached.Of, Choices))
                return cached.Items;
            var items = Choices.Select(c => new ChoiceItem(c, LabelOf(c))).ToArray();
            _items = (Choices, items);
            return items;
        }
    }

    public override string DisplayValue => HasLabels && RawValue.Length > 0 ? LabelOf(RawValue) : base.DisplayValue;

    /// <summary>What an empty value means, as the dropdown's placeholder shows it.</summary>
    protected override string DefaultPlaceholder => HasLabels ? LabelOf(Def.Default) : Def.Default;

    protected override bool MatchesMore(string query) =>
        HasLabels && RawValue.Length > 0 && LabelOf(RawValue).Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A labelled option: <see cref="Value"/> is stored, <see cref="Label"/> shown (the dropdown shows ToString).</summary>
public sealed record ChoiceItem(string Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class RefPropertyViewModel : PropertyItemViewModel
{
    private readonly Action<RefPropertyViewModel>? _navigate;

    public RefPropertyViewModel(PropertyDef def, string value, Action<RefPropertyViewModel>? navigate) : base(def)
    {
        _value = value;
        _navigate = navigate;
        RefreshModified();
    }

    public string RefType => Def.RefType;
    public string RefGlyph => TypeStyles.Glyph(Def.RefType);
    public Avalonia.Media.IBrush RefBrush => TypeStyles.Brush(Def.RefType);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoTo))]
    private string _value;

    partial void OnValueChanged(string value) => OnEdited();

    public override string RawValue
    {
        get => Value;
        set => Value = value;
    }

    /// <summary>
    /// The → button: rows can follow a reference that resolves; table cells, which have nowhere to go, can't. A
    /// reference to nothing offers no → at all: its ⚠ beside the value says why, so following it never raises a
    /// message somewhere else in the window.
    /// </summary>
    public bool CanGoTo => _navigate is not null && Value.Length > 0 && !HasProblem;

    protected override void ProblemChanged() => OnPropertyChanged(nameof(CanGoTo));

    [RelayCommand]
    private void GoTo()
    {
        if (CanGoTo)
            _navigate!.Invoke(this);
    }
}
