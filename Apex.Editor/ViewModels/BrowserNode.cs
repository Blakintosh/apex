using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;

namespace Apex.Editor.ViewModels;

/// <summary>
/// A row in the asset browser: a GDT file or asset-type group, an asset (derived assets nest under
/// their parent template), or a section heading ("Pinned", "All assets"). The tree renders as a
/// flattened, virtualized list — only visible rows are realized, so tens of thousands of assets stay
/// smooth. Levels are relative to the current grouping: the same asset can sit at level 0 in a flat
/// list and level 2 under GDT → type.
/// </summary>
public sealed partial class BrowserNode : ObservableObject
{
    private readonly Action<BrowserNode>? _toggle;

    public BrowserNode(int level, Action<BrowserNode>? toggle = null)
    {
        _level = level;
        _toggle = toggle;
        _indent = IndentFor(level);
    }

    /// <summary>What the row stands for: an asset's name, a type, or a GDT's full relative path (the key its expansion is kept by).</summary>
    public required string Title { get; init; }

    private readonly string? _displayName;

    /// <summary>The name the row shows: <see cref="Title"/>, except a GDT row shows the file's short name ("zm_weapons").</summary>
    public string DisplayName { get => _displayName ?? Title; init => _displayName = value; }

    /// <summary>Dim text after the name and suffix: a GDT row's folder ("source_data/zm"), trimmed from the front.</summary>
    public string Folder { get; init; } = "";

    public bool HasFolder => Folder.Length > 0;

    /// <summary>A GDT row's full path, for its tooltip; null on other rows.</summary>
    public string? Tip { get; init; }

    /// <summary>
    /// The assets a group row stands for (its hits in the current view), so its changed and problem marks can be worked
    /// out again when one of them changes; null on other rows.
    /// </summary>
    public IReadOnlyList<AssetRecord>? Members { get; init; }

    public string Badge { get; init; } = "";
    public string Glyph { get; init; } = "";

    /// <summary>A GDT row is its name alone; asset and type rows lead with their type's icon.</summary>
    public bool HasGlyph => Gdt is null && Glyph.Length > 0;
    public IBrush? GlyphBrush { get; init; }
    public AssetRecord? Asset { get; init; }

    private List<BrowserNode>? _children;

    /// <summary>Child rows. Allocated on first use: nearly every node is a leaf that never has any.</summary>
    public List<BrowserNode> Children => _children ??= new List<BrowserNode>();

    public int ChildCount => _children?.Count ?? 0;

    public void ClearChildren() => _children?.Clear();

    /// <summary>Section heading row (not an asset or group): "Pinned", "All assets", "38 results".</summary>
    public bool IsHeader { get; init; }

    /// <summary>Row inside the Pinned section (a second node for an asset that is also in the tree).</summary>
    public bool IsPinnedEntry { get; init; }

    /// <summary>GDT group row (context menu offers GDT-scoped actions).</summary>
    public GdtFile? Gdt { get; init; }

    /// <summary>Asset-type group row.</summary>
    public string? GroupType { get; init; }

    // ── Lazy children (live mode) ────────────────────────────────────────────
    // In live mode a GDT group holds up to ~95k assets across all files, so building the whole
    // asset subtree eagerly per browser refresh is wasteful. A collapsed group instead carries a
    // factory that materializes its type/asset rows the first time it is expanded.
    private Func<List<BrowserNode>>? _childFactory;

    /// <summary>True when this node has children that have not been built yet (a collapsed lazy group).</summary>
    public bool HasUnbuiltChildren => _childFactory is not null;

    /// <summary>Defers this node's children to a factory invoked on first expansion.</summary>
    public void SetLazyChildren(Func<List<BrowserNode>> factory)
    {
        _childFactory = factory;
        OnPropertyChanged(nameof(ShowChevron));
        OnPropertyChanged(nameof(Chevron));
    }

    /// <summary>Builds deferred children if any are pending. Safe to call repeatedly.</summary>
    public void EnsureChildren()
    {
        if (_childFactory is not { } factory)
            return;
        _childFactory = null;
        Children.AddRange(factory());
        RefreshStructure();
    }

    [ObservableProperty]
    private int _level;

    [ObservableProperty]
    private Thickness _indent;

    /// <summary>One chevron's width per level, so a child's chevron sits under its parent's glyph.</summary>
    public const double IndentStep = 24;

    private static Thickness IndentFor(int level) => new(level * IndentStep, 0, 0, 0);

    partial void OnLevelChanged(int value) => Indent = IndentFor(value);

    public bool IsGroup => Asset is null && !IsHeader;
    public bool IsAssetRow => Asset is not null;
    public bool HasBadge => Badge.Length > 0;

    /// <summary>True for an asset nested under its parent template in the tree.</summary>
    [ObservableProperty]
    private bool _isDerived;

    /// <summary>Set when the asset has schema-validation problems (red ⚠ in the tree) — for a group, any asset beneath it.</summary>
    [ObservableProperty]
    private bool _hasProblem;

    /// <summary>Edited this session (amber dot) — for a group, any asset beneath it.</summary>
    [ObservableProperty]
    private bool _hasChanges;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Chevron));

    // ── Secondary text and search highlighting ───────────────────────────────
    /// <summary>Faint text after the name: the GDT in type/flat views, the lone type of a single-type GDT.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuffix))]
    private string _suffix = "";

    public bool HasSuffix => Suffix.Length > 0;

    /// <summary>Name split around the search match, so the view can underline the hit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHit))]
    private string _hit = "";

    [ObservableProperty]
    private string _prefix = "";

    [ObservableProperty]
    private string _postfix = "";

    public bool HasHit => Hit.Length > 0;

    /// <summary>Marks the part of <see cref="Title"/> matching <paramref name="query"/> (clears it when empty).</summary>
    public void SetHighlight(string query)
    {
        var i = query.Length == 0 ? -1 : Title.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
        {
            Prefix = Title;
            Hit = "";
            Postfix = "";
            return;
        }
        Prefix = Title[..i];
        Hit = Title.Substring(i, query.Length);
        Postfix = Title[(i + query.Length)..];
    }

    /// <summary>
    /// What a screen reader announces for the row, in the words the row shows: "wpn_ar_havoc, weapon, in zm_weapons.gdt,
    /// changed", "zm_weapons.gdt, weapon, 38 assets", "Pinned".
    /// </summary>
    public string AutomationName
    {
        get
        {
            if (IsHeader)
                return Title;
            var parts = new List<string>(5) { DisplayName };
            if (Asset is not null)
            {
                parts.Add(Asset.Type);
                if (HasSuffix && !Suffix.Equals(Asset.Type, StringComparison.OrdinalIgnoreCase))
                    parts.Add("in " + Suffix);
            }
            else
            {
                if (HasSuffix)
                    parts.Add(Suffix);
                if (HasFolder)
                    parts.Add("in " + Folder);
                if (HasBadge)
                    parts.Add(Badge == "1" ? "1 asset" : $"{Badge} assets");
            }
            if (HasChanges)
                parts.Add("changed");
            if (HasProblem)
                parts.Add("has problems");
            return string.Join(", ", parts);
        }
    }

    partial void OnSuffixChanged(string value) => OnPropertyChanged(nameof(AutomationName));
    partial void OnHasChangesChanged(bool value) => OnPropertyChanged(nameof(AutomationName));
    partial void OnHasProblemChanged(bool value) => OnPropertyChanged(nameof(AutomationName));

    public string Chevron => (ChildCount == 0 && !HasUnbuiltChildren) ? "" : (IsExpanded ? "▾" : "▸");
    public bool ShowChevron => ChildCount > 0 || HasUnbuiltChildren;

    /// <summary>Raise structure-derived bindings after Children were rebuilt for a new filter.</summary>
    public void RefreshStructure()
    {
        OnPropertyChanged(nameof(Chevron));
        OnPropertyChanged(nameof(ShowChevron));
    }

    [RelayCommand]
    private void Toggle() => _toggle?.Invoke(this);

    public static BrowserNode ForAsset(AssetRecord asset, Action<BrowserNode>? toggle = null) => new(2, toggle)
    {
        Title = asset.Name,
        Glyph = TypeStyles.Glyph(asset.Type),
        GlyphBrush = TypeStyles.Brush(asset.Type),
        Asset = asset,
        Prefix = asset.Name,
    };

    public static BrowserNode Heading(string title) => new(0) { Title = title, IsHeader = true };

    /// <summary>
    /// Works a group's changed and problem marks out again from its members. <paramref name="mayHaveProblems"/> false
    /// (the caller knows none of them has any) skips the problem lookups.
    /// </summary>
    public void RefreshMarks(Func<AssetRecord, bool> hasProblem, bool mayHaveProblems = true)
    {
        if (Members is not { } members)
            return;
        var changes = false;
        var problem = false;
        foreach (var a in members)
        {
            changes |= a.HasSessionEdits;
            problem = problem || (mayHaveProblems && hasProblem(a));
            if (changes && (problem || !mayHaveProblems))
                break;
        }
        HasChanges = changes;
        HasProblem = problem;
    }
}
