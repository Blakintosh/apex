using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Apex.Editor.ViewModels;

/// <summary>A card of the xanim's properties panel: what it lists now, and whether it is open.</summary>
public sealed partial class CategoryViewModel
{
    /// <summary>The section's rows (and deffile buttons) as the form lists them now, for a card.</summary>
    [ObservableProperty]
    private IReadOnlyList<object> _visibleRows = Array.Empty<object>();

    /// <summary>The card is open (a closed one keeps its header and a summary).</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>What a closed card says about its values: "Defaults", or how many are set.</summary>
    [ObservableProperty]
    private string _summary = "";
}

/// <summary>
/// The xanim's properties panel: with the notetracks in the dock, the short form (Source Files, Settings, MP Specific,
/// AssetViewer Preview) sits in the right column as cards. Its All / Changed / Problems counts describe the panel only.
/// </summary>
public sealed partial class AssetEditorViewModel
{
    /// <summary>The deffile sections the dock replaces: exported notes and the three kinds of GDT notes.</summary>
    public static bool IsNotetrackSection(string name) =>
        name.StartsWith("Exported Notetracks", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Notetracks", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("FX Notetracks", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("SOUND Notetracks", StringComparison.OrdinalIgnoreCase);

    /// <summary>The panel's cards, in form order (sections with nothing to show drop out, as they do in the form).</summary>
    public RangeObservableCollection<CategoryViewModel> PanelCards { get; } = new();

    [ObservableProperty]
    private int _panelShownCount;

    [ObservableProperty]
    private int _panelChangedCount;

    [ObservableProperty]
    private int _panelProblemCount;

    private bool _panelOpened;

    private IEnumerable<CategoryViewModel> PanelSections => _categories.Where(c => !IsNotetrackSection(c.Name));

    /// <summary>Brings the cards to the form's current rows (after a filter, a view switch, a rule run).</summary>
    private void RefreshPanel()
    {
        var rows = new Dictionary<CategoryViewModel, List<object>>();
        List<object>? open = null;
        foreach (var row in FlatRows)
        {
            if (row is CategoryViewModel section)
                open = IsNotetrackSection(section.Name) ? null : rows[section] = new List<object>();
            else
                open?.Add(row);
        }
        foreach (var (section, list) in rows)
            if (!section.VisibleRows.SequenceEqual(list))
                section.VisibleRows = list;
        var cards = PanelSections.Where(rows.ContainsKey).ToList();
        if (!PanelCards.SequenceEqual(cards))
            PanelCards.ReplaceAll(cards);
        if (!_panelOpened)
        {
            // Preview options are for AssetViewer: closed until they hold something.
            _panelOpened = true;
            foreach (var c in PanelSections.Where(c => c.Name.StartsWith("AssetViewer", StringComparison.OrdinalIgnoreCase)))
                c.IsExpanded = c.All.Any(IsSet);
        }
        RefreshPanelCounts();
    }

    private static bool IsSet(PropertyItemViewModel p) => !p.IsRuleHidden && p.RawValue != p.Def.Default;

    /// <summary>The panel's own counts and each card's summary: a few dozen rows, so after every edit.</summary>
    private void RefreshPanelCounts()
    {
        int shown = 0, changed = 0, problems = 0;
        foreach (var c in PanelSections)
        {
            var set = 0;
            foreach (var p in c.All)
            {
                if (p.IsRuleHidden)
                    continue;
                shown++;
                if (p.IsChanged)
                    changed++;
                if (p.HasProblem)
                    problems++;
                if (IsSet(p))
                    set++;
            }
            c.Summary = set == 0 ? "Defaults" : set == 1 ? "1 set" : $"{set} set";
        }
        PanelShownCount = shown;
        PanelChangedCount = changed;
        PanelProblemCount = problems;
    }
}
