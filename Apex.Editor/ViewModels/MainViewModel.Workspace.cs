using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Commands;
using Apex.Editor.Models;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>How the Explorer groups assets when no free-text search is active.</summary>
public enum ExplorerGrouping { Gdt, Type }

/// <summary>Workspace presets. Edit: Explorer · editor · Preview over Inspector. Preview: the preview takes the room.</summary>
public enum WorkspaceLayout { Edit, Preview }

/// <summary>Which reference list the Inspector shows.</summary>
public enum InspectorLinks { Uses, UsedBy, Family }

/// <summary>A type facet over search results ("All 38 · weapon 3 · xmodel 9").</summary>
public sealed partial class SearchFacet : ObservableObject
{
    private readonly Action<SearchFacet> _select;

    public SearchFacet(string label, string? type, bool isActive, Action<SearchFacet> select)
    {
        Label = label;
        Type = type;
        _isActive = isActive;
        _select = select;
    }

    public string Label { get; }
    public string? Type { get; }

    [ObservableProperty]
    private bool _isActive;

    [RelayCommand]
    private void Select() => _select(this);
}

/// <summary>An asset in the start page's Recent/Pinned lists.</summary>
public sealed partial class AssetLinkViewModel : ObservableObject
{
    private readonly Action<AssetRecord> _open;

    public AssetLinkViewModel(AssetRecord record, string detail, Action<AssetRecord> open)
    {
        Record = record;
        Detail = detail;
        _open = open;
    }

    public AssetRecord Record { get; }
    public string Name => Record.Name;
    public string Detail { get; }

    /// <summary>The name and the GDT's full path, which the row shortens.</summary>
    public string Tip => $"{Record.Name} · {Record.GdtName}";

    public string Glyph => TypeStyles.Glyph(Record.Type);
    public IBrush GlyphBrush => TypeStyles.Brush(Record.Type);

    [RelayCommand]
    private void Open() => _open(Record);
}

/// <summary>One row of the Inspector's Uses / Used by / Family lists.</summary>
public sealed partial class InspectorLinkViewModel : ObservableObject, Controls.IRowStandIn
{
    private readonly Action? _open;

    public InspectorLinkViewModel(string name, string type, string via, bool missing, bool isSelf, Action? open)
    {
        Name = name;
        TypeName = type;
        Via = via;
        IsMissing = missing;
        IsSelf = isSelf;
        _open = open;
    }

    public string Name { get; }
    public string TypeName { get; }
    public string Via { get; }
    public bool IsMissing { get; }
    public bool IsSelf { get; }
    public string Glyph => TypeStyles.Glyph(TypeName);
    public IBrush GlyphBrush => TypeStyles.Brush(TypeName);

    [RelayCommand]
    private void Open() => _open?.Invoke();

    object? Controls.IRowStandIn.CreateStandIn() => new InspectorLinkViewModel("", "", "", false, false, null);
}

/// <summary>A reference the Preview pane can show for the active asset ("View Model", "World Model"…).</summary>
public sealed record PreviewSubject(string Label, AssetRecord Record)
{
    public string Detail => Record.Name;
}

public sealed partial class MainViewModel
{
    private readonly UiSettings _settings = UiSettings.Load();
    private readonly List<string> _pinned = new();
    private bool _restoringSettings;

    /// <summary>Applies saved preferences; called first thing from the constructor.</summary>
    private void InitWorkspace()
    {
        _restoringSettings = true;
        Grouping = Enum.TryParse<ExplorerGrouping>(_settings.Grouping, out var g) ? g : ExplorerGrouping.Gdt;
        IsExplorerVisible = _settings.ExplorerVisible;
        IsInspectorVisible = _settings.InspectorVisible;
        IsPreviewFloating = _settings.PreviewFloating;
        Theme = AppTheme.Parse(_settings.Theme);
        _pinned.AddRange(_settings.Pinned);
        // The preview lighting state is app-wide and remembered like APE's Preview/LightState.
        if (!Services.Preview.ToolsGfx.ToolsGfxPreviewService.LightStateFromEnvironment
            && Enum.TryParse<Apex.Render.Data.Lighting.PreviewLightState>(_settings.PreviewLightState, true, out var light))
            PreviewLighting.Shared.LightState = light;
        PreviewLighting.Shared.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewLighting.LightState))
                SaveSettings();
        };
        _restoringSettings = false;
    }

    private ThemeChoice _theme;

    /// <summary>
    /// The theme: applied to the whole app the moment the user changes it, and remembered. The saved one is applied
    /// once at startup (App), so a view model restoring its settings never changes the app's theme by itself.
    /// </summary>
    public ThemeChoice Theme
    {
        get => _theme;
        set
        {
            if (_theme == value)
                return;
            _theme = value;
            if (!_restoringSettings)
                AppTheme.Apply(value);
            OnPropertyChanged();
            SaveSettings();
        }
    }

    /// <summary>Pane sizes the window restores and saves (the view owns the splitters).</summary>
    public UiSettings Settings => _settings;

    public void SaveSettings()
    {
        if (_restoringSettings)
            return;
        _settings.Grouping = Grouping.ToString();
        _settings.ExplorerVisible = IsExplorerVisible;
        _settings.InspectorVisible = IsInspectorVisible;
        _settings.PreviewFloating = IsPreviewFloating;
        _settings.Theme = Theme.ToString();
        _settings.PreviewLightState = PreviewLighting.Shared.LightState.ToString();
        _settings.Pinned = new List<string>(_pinned);
        // Every tab switch lands here (recent list), so writes are coalesced and leave the UI thread:
        // one file write a second after the last change, from a snapshot taken here.
        _settingsSave ??= CreateSettingsTimer();
        _settingsSave.Stop();
        _settingsSave.Start();
    }

    private DispatcherTimer? _settingsSave;

    private DispatcherTimer CreateSettingsTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Task.Run(UiSettings.WriteLater(_settings.Serialize()));
        };
        return timer;
    }

    /// <summary>Writes pending preferences now (window closing, app exit).</summary>
    public void FlushSettings()
    {
        if (_settingsSave is not { IsEnabled: true } pending)
            return;
        pending.Stop();
        _settings.Save();
    }

    // ══ Explorer: grouping, search results, pinned ═══════════════════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupingLabel), nameof(GroupingTitle), nameof(IsGroupedByGdt), nameof(IsGroupedByType))]
    private ExplorerGrouping _grouping;

    public string GroupingLabel => Grouping == ExplorerGrouping.Type ? "Type" : "GDT file";

    /// <summary>The Explorer's section line: what the top-level rows are.</summary>
    public string GroupingTitle => Grouping == ExplorerGrouping.Type ? "Types" : "GDT files";

    public bool IsGroupedByGdt => Grouping == ExplorerGrouping.Gdt;
    public bool IsGroupedByType => Grouping == ExplorerGrouping.Type;

    partial void OnGroupingChanged(ExplorerGrouping value) => RegroupExplorer();

    private void RegroupExplorer()
    {
        if (_restoringSettings)
            return;
        _roots.Clear(); // a different shape: previous expansions don't carry over
        SaveSettings();
        ApplyFilterNow();
    }

    [RelayCommand]
    private void SetGrouping(string mode) =>
        Grouping = Enum.TryParse<ExplorerGrouping>(mode, true, out var g) ? g : ExplorerGrouping.Gdt;

    /// <summary>A free-text query is active: the Explorer shows flat, ranked results.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTreeMode), nameof(ReserveFacetRow))]
    private bool _isSearchResults;

    /// <summary>
    /// The facet row keeps its place from the first letter typed until the box is empty again, facets or not, so the
    /// list under it never jumps as a search starts, narrows to one type, or ends.
    /// </summary>
    public bool ReserveFacetRow => IsSearchResults || ExplorerQuery.Words.Trim().Length > 0;

    public bool IsTreeMode => !IsSearchResults;

    [ObservableProperty]
    private string _resultCountText = "";

    /// <summary>Shown under capped result lists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultsNote))]
    private string _resultsNote = "";

    public bool HasResultsNote => ResultsNote.Length > 0;

    public RangeObservableCollection<SearchFacet> SearchFacets { get; } = new();

    /// <summary>Raised when the Explorer should select and scroll to a row (Reveal in tree).</summary>
    public event Action<BrowserNode>? RevealRequested;

    /// <summary>
    /// Selects the asset's row in the Explorer, expanding its groups. The search stays as typed when
    /// the asset is among its results; only when the search hides it is it cleared, and the status
    /// line says so.
    /// </summary>
    public void RevealInTree(AssetRecord asset)
    {
        ShowExplorerPane();
        if (_debounce.IsEnabled)
            ApplyFilterNow();
        if (!TryReveal(asset) && FilterText.Length > 0)
        {
            FilterText = "";
            ApplyFilterNow();
            if (TryReveal(asset))
                Status = $"Cleared the search to show {asset.Name}";
        }
    }

    private bool TryReveal(AssetRecord asset)
    {
        foreach (var root in _roots.ToList())
        {
            if (!Contains(root, asset))
                continue;
            if (!root.IsExpanded)
                ToggleNode(root);
            foreach (var child in root.Children.ToList())
                if (child.IsGroup && Contains(child, asset) && !child.IsExpanded)
                    ToggleNode(child);
            break;
        }
        if (FlatRows.FirstOrDefault(n => n.Asset == asset && !n.IsPinnedEntry) is not { } row)
            return false;
        RevealRequested?.Invoke(row);
        return true;

        static bool Contains(BrowserNode node, AssetRecord target)
        {
            if (node.Gdt is { } gdt)
                return gdt.Name == target.GdtName;
            if (node.GroupType is { } type)
                return type.Equals(target.Type, StringComparison.OrdinalIgnoreCase);
            return false;
        }
    }

    [RelayCommand]
    private void RevealActiveInTree()
    {
        if (ActiveTab is { } tab)
            RevealInTree(tab.Record);
    }

    public bool IsPinned(AssetRecord asset) => _pinned.Contains(asset.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Pins or unpins an asset in the Explorer's Pinned section (and the start page).</summary>
    public void TogglePinned(AssetRecord asset)
    {
        var i = _pinned.FindIndex(n => n.Equals(asset.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            _pinned.RemoveAt(i);
        else
            _pinned.Add(asset.Name);
        SaveSettings();
        RefreshStartPage();
        OnPropertyChanged(nameof(IsActivePinned));
        ApplyFilterNow();
        Status = i >= 0 ? $"Unpinned {asset.Name} from Explorer" : $"Pinned {asset.Name} to Explorer";
    }

    public bool IsActivePinned => ActiveTab is { } t && IsPinned(t.Record);

    [RelayCommand]
    private void TogglePinActive()
    {
        if (ActiveTab is { } tab)
            TogglePinned(tab.Record);
    }

    // ══ Workspace layout ═════════════════════════════════════════════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreviewLayout), nameof(IsEditLayout), nameof(ShowExplorer), nameof(ShowInspector))]
    [NotifyPropertyChangedFor(nameof(PreviewMaximizeLabel))]
    private WorkspaceLayout _layout = WorkspaceLayout.Edit;

    public bool IsPreviewLayout => Layout == WorkspaceLayout.Preview;
    public bool IsEditLayout => Layout == WorkspaceLayout.Edit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExplorer))]
    private bool _isExplorerVisible = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInspector))]
    private bool _isInspectorVisible = true;

    /// <summary>The Preview lives in its own OS window; the right column is all Inspector. Remembered across sessions.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockedPreview), nameof(PreviewFloatLabel))]
    private bool _isPreviewFloating;

    /// <summary>The preview layout tucks the Explorer and Inspector away; Ctrl+B still overrides.</summary>
    public bool ShowExplorer => IsExplorerVisible && !IsPreviewLayout;
    public bool ShowInspector => IsInspectorVisible && !IsPreviewLayout;
    public bool ShowDockedPreview => !IsPreviewFloating;

    // The Preview header's ⧉ and ⤢ say what they will do now, not what they did the first time (their tooltips come
    // from the commands' on-names in the catalog; these are the matching accessible names).
    public string PreviewFloatLabel => OnOrOffName(CommandCatalog.PreviewWindow, IsPreviewFloating);
    public string PreviewMaximizeLabel => OnOrOffName(CommandCatalog.MaximizePreview, IsPreviewLayout);

    private static string OnOrOffName(string id, bool on)
    {
        var info = CommandCatalog.Get(id);
        return on && info.OnName is { } onName ? onName : info.Name;
    }

    /// <summary>The popped-out preview window's title: what it is showing.</summary>
    public string PreviewWindowTitle => PreviewSubject is { } s ? $"{s.Record.Name} — APEX Preview" : "APEX — Preview";

    partial void OnIsExplorerVisibleChanged(bool value) => SaveSettings();
    partial void OnIsInspectorVisibleChanged(bool value) => SaveSettings();
    partial void OnIsPreviewFloatingChanged(bool value)
    {
        NotifyPreviewPlacement();
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleExplorer()
    {
        if (IsPreviewLayout && !IsExplorerVisible)
            IsExplorerVisible = true;
        else if (IsPreviewLayout)
        {
            // In the preview layout the Explorer is tucked away regardless: bring it back by
            // returning to Edit rather than silently flipping a hidden preference.
            Layout = WorkspaceLayout.Edit;
            return;
        }
        IsExplorerVisible = !IsExplorerVisible;
    }

    [RelayCommand]
    private void ToggleInspector() => IsInspectorVisible = !IsInspectorVisible;

    /// <summary>The one layout switch — Preview's ⤢, the Layout menu, Ctrl+Shift+M: the preview takes
    /// the room while the editor stays beside it, or gives it back.</summary>
    [RelayCommand]
    private void TogglePreviewMaximized()
    {
        if (IsPreviewFloating)
            IsPreviewFloating = false;
        Layout = IsPreviewLayout ? WorkspaceLayout.Edit : WorkspaceLayout.Preview;
    }

    /// <summary>Preview's ⧉ / Ctrl+Shift+O: pop the preview out to its own window, or dock it back.</summary>
    [RelayCommand]
    private void TogglePreviewFloating()
    {
        if (!IsPreviewFloating && IsPreviewLayout)
            Layout = WorkspaceLayout.Edit;
        IsPreviewFloating = !IsPreviewFloating;
    }

    // ══ Tabs: overflow list ══════════════════════════════════════════════════

    /// <summary>Activation order, newest first — the "N more" list and Ctrl+Tab both follow it.</summary>
    private readonly List<AssetEditorViewModel> _mru = new();

    public IReadOnlyList<AssetEditorViewModel> TabsByRecency =>
        _mru.Where(OpenTabs.Contains).Concat(OpenTabs.Where(t => !_mru.Contains(t))).ToList();

    [RelayCommand]
    private void ActivateTab(AssetEditorViewModel tab)
    {
        if (OpenTabs.Contains(tab))
            ActiveTab = tab;
    }

    [RelayCommand]
    private void CloseOtherTabs(AssetEditorViewModel keep)
    {
        // The kept tab first: closing the others then never hands the editor to a tab that is about to go too.
        ActiveTab = keep;
        foreach (var t in OpenTabs.Where(t => t != keep).ToList())
            CloseTab(t);
    }

    // ══ Navigation history (Alt+← / Alt+→) ═══════════════════════════════════

    private readonly List<AssetRecord> _back = new();
    private readonly List<AssetRecord> _forward = new();
    private bool _navigatingHistory;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public string BackTip => CommandCatalog.Get(CommandCatalog.Back).TipFor(CanGoBack ? $"Back to {_back[^1].Name}" : "Back");
    public string ForwardTip => CommandCatalog.Get(CommandCatalog.Forward).TipFor(CanGoForward ? $"Forward to {_forward[^1].Name}" : "Forward");

    /// <summary>Records a visit when the active asset changes (not while walking the history).</summary>
    private void RecordVisit(AssetEditorViewModel? previous, AssetEditorViewModel? next)
    {
        // Mid Ctrl+Tab walk nothing is a visit yet; EndTabCycle records where the walk landed.
        if (_cyclingTabs)
        {
            NotifyHistory();
            return;
        }
        if (next is not null)
        {
            _mru.Remove(next);
            _mru.Insert(0, next);
            AddRecent(next.Record);
        }
        if (_navigatingHistory || _closingTab || previous is null || next is null || previous == next)
        {
            NotifyHistory();
            return;
        }
        _back.Add(previous.Record);
        if (_back.Count > 100)
            _back.RemoveAt(0);
        _forward.Clear();
        NotifyHistory();
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(BackTip));
        OnPropertyChanged(nameof(ForwardTip));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => Walk(_back, _forward);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward() => Walk(_forward, _back);

    private void Walk(List<AssetRecord> from, List<AssetRecord> to)
    {
        while (from.Count > 0)
        {
            var target = from[^1];
            from.RemoveAt(from.Count - 1);
            // Deleted assets fall out of the history silently.
            if (!_db.Assets.Contains(target))
                continue;
            if (ActiveTab is { } current)
                to.Add(current.Record);
            _navigatingHistory = true;
            try { OpenAsset(target, preview: true); }
            finally { _navigatingHistory = false; }
            break;
        }
        NotifyHistory();
    }

    // ══ Recent & start page ══════════════════════════════════════════════════

    public RangeObservableCollection<AssetLinkViewModel> RecentItems { get; } = new();

    public bool HasRecent => RecentItems.Count > 0;

    private void AddRecent(AssetRecord record)
    {
        _settings.Recent.RemoveAll(r => r.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase));
        _settings.Recent.Insert(0, new UiSettings.RecentEntry { Name = record.Name, OpenedUtc = DateTime.UtcNow });
        if (_settings.Recent.Count > 30)
            _settings.Recent.RemoveRange(30, _settings.Recent.Count - 30);
        SaveSettings();
    }

    /// <summary>Rebuilds the start page's list (only shown when nothing is open, so this is cheap).</summary>
    public void RefreshStartPage()
    {
        // Each row says where the asset lives; "opened now" was true of every row. Pinned assets aren't repeated here:
        // they are the Explorer's own list, at its top.
        var recent = new List<AssetLinkViewModel>(10);
        foreach (var r in _settings.Recent)
        {
            if (recent.Count >= 10)
                break;
            if (FindAsset("", r.Name) is { } rec)
                recent.Add(new AssetLinkViewModel(rec, ShortGdt(rec.GdtName), a => OpenAsset(a)));
        }
        RecentItems.ReplaceAll(recent);
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(SessionSummaryText));
        OnPropertyChanged(nameof(HasSessionChanges));
    }

    public bool HasSessionChanges => SessionEditCount > 0;

    public string SessionSummaryText
    {
        get
        {
            var assets = _db.Assets.Count(a => a.HasSessionEdits);
            return SessionEditCount == 1
                ? "1 change in this session"
                : $"{SessionEditCount:N0} changes across {assets:N0} asset{(assets == 1 ? "" : "s")}";
        }
    }

    /// <summary>Start page "Review": list every asset edited this session in the Explorer.</summary>
    [RelayCommand]
    private void ReviewChanges()
    {
        ShowExplorerPane();
        FilterText = "is:changed";
        ApplyFilterNow();
    }

    /// <summary>Commands that change what the Explorer lists bring it into view first — a filter
    /// changed in a hidden pane is a change nobody sees.</summary>
    private void ShowExplorerPane()
    {
        if (IsPreviewLayout)
            Layout = WorkspaceLayout.Edit;
        IsExplorerVisible = true;
    }

    // ══ Preview pane: subject picker ═════════════════════════════════════════

    public RangeObservableCollection<PreviewSubject> PreviewSubjects { get; } = new();

    /// <summary>What the Preview pane renders: the active asset if it has a visual, otherwise one of its references.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(PreviewSubjectLabel), nameof(DockedPreview), nameof(HasRecoilPreview))]
    private PreviewPaneViewModel? _currentPreview;

    /// <summary>The Preview shows a weapon's recoil preview: docked, it takes the right column's full height.</summary>
    public bool HasRecoilPreview => CurrentPreview?.Content is WeaponPreviewViewModel;

    /// <summary>
    /// The active asset is an xanim: the editor holds its preview above the notetracks dock, and the right column holds
    /// its short properties panel instead of Preview and Inspector.
    /// </summary>
    public bool IsAnimLayout => ActiveTab is { IsAnim: true };

    /// <summary>The active tab while it is an xanim (the properties panel's subject), else null.</summary>
    public AssetEditorViewModel? AnimTab => IsAnimLayout ? ActiveTab : null;

    /// <summary>
    /// What the Preview pane (the right column, or its own window) shows: the current preview, except while an xanim's
    /// editor shows it in place. One view per preview at a time, so a viewport is never built twice.
    /// </summary>
    public PreviewPaneViewModel? DockedPreview => IsAnimLayout && !IsPreviewFloating ? null : CurrentPreview;

    /// <summary>The preview moved between the window and its own (or the active tab changed kind).</summary>
    private void NotifyPreviewPlacement()
    {
        OnPropertyChanged(nameof(IsAnimLayout));
        OnPropertyChanged(nameof(AnimTab));
        OnPropertyChanged(nameof(DockedPreview));
        foreach (var tab in OpenTabs)
            tab.NotifyPreviewPlacement();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewSubjectLabel), nameof(PreviewWindowTitle))]
    private PreviewSubject? _previewSubject;

    public bool HasPreview => CurrentPreview is not null;

    public bool HasPreviewChoices => PreviewSubjects.Count > 1;

    /// <summary>
    /// The subject picker's label. With nothing to render the pane shrinks to its header, so the label is then the
    /// pane's one line: why it is empty, short enough for the header.
    /// </summary>
    public string PreviewSubjectLabel => PreviewSubject?.Label
        ?? (ActiveTab is null ? "" : _env.IsAvailable ? "Nothing to preview" : "Needs the BO3 install");

    /// <summary>Why the Preview is empty, in one line.</summary>
    public string PreviewEmptyText => ActiveTab is null
        ? "Open an asset to preview it."
        : _env.IsAvailable
            ? "Nothing to preview: this asset doesn't reference a model, material, image or anim."
            : "Model and anim previews need the BO3 install.";

    // Referenced-asset previews are cached per record so flipping tabs doesn't reload a model; the least recently
    // shown are disposed past SubjectPreviewLimit so a long session doesn't pin every model it ever looked at.
    private const int SubjectPreviewLimit = 6;
    private readonly Dictionary<AssetRecord, PreviewPaneViewModel?> _subjectPreviews = new();
    private readonly LinkedList<AssetRecord> _subjectPreviewOrder = new();
    private readonly Dictionary<AssetEditorViewModel, AssetRecord> _chosenSubject = new();

    private static readonly HashSet<string> RenderableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image", "material", "xmodel", "xanim",
    };

    // Mirrors PreviewPaneViewModel.Create's gate without constructing (and loading) a preview:
    // models and anims need the live install, images and materials always have a preview.
    private bool CanRender(AssetRecord record) =>
        record.Type.Equals("image", StringComparison.OrdinalIgnoreCase)
        || record.Type.Equals("material", StringComparison.OrdinalIgnoreCase)
        || (_env.IsAvailable && RenderableTypes.Contains(record.Type));

    // Preferred reference order for types that don't render themselves.
    private static readonly string[] SubjectKeyPreference =
    {
        "viewModel", "gunModel", "worldModel", "model", "xmodel", "material", "image",
    };

    private void RefreshPreviewSubjects()
    {
        _subjectRefreshTimer?.Stop();
        OnPropertyChanged(nameof(PreviewEmptyText));
        OnPropertyChanged(nameof(PreviewSubjectLabel));
        if (ActiveTab is not { } tab)
        {
            PreviewSubjects.ReplaceAll(Array.Empty<PreviewSubject>());
            PreviewSubject = null;
            CurrentPreview = null;
            OnPropertyChanged(nameof(HasPreviewChoices));
            return;
        }

        SyncWeaponPreview(tab);
        var subjects = new List<PreviewSubject>();
        if (tab.PreviewPane is not null)
            subjects.Add(new PreviewSubject("This asset", tab.Record));

        // References to renderable assets, in preference order, de-duplicated by target.
        var seen = new HashSet<AssetRecord> { tab.Record };
        var refs = tab.AllSentinel.All.OfType<RefPropertyViewModel>()
            .Where(r => r.Value.Length > 0 && RenderableTypes.Contains(r.RefType) && !r.IsRuleHidden)
            .OrderBy(r =>
            {
                var i = Array.FindIndex(SubjectKeyPreference, k => k.Equals(r.Key, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? SubjectKeyPreference.Length : i;
            });
        foreach (var r in refs)
        {
            if (subjects.Count >= 12)
                break;
            if (FindAsset(r.RefType, r.Value) is { } target && CanRender(target) && seen.Add(target))
                subjects.Add(new PreviewSubject(r.Label, target));
        }
        PreviewSubjects.ReplaceAll(subjects);
        OnPropertyChanged(nameof(HasPreviewChoices));

        var chosen = _chosenSubject.TryGetValue(tab, out var rec)
            ? PreviewSubjects.FirstOrDefault(s => s.Record == rec)
            : null;
        SelectPreviewSubject(chosen ?? PreviewSubjects.FirstOrDefault());
    }

    [RelayCommand]
    private void ChoosePreviewSubject(PreviewSubject subject)
    {
        if (ActiveTab is { } tab)
            _chosenSubject[tab] = subject.Record;
        SelectPreviewSubject(subject);
    }

    private void SelectPreviewSubject(PreviewSubject? subject)
    {
        PreviewSubject = subject;
        if (subject is null || ActiveTab is not { } tab)
        {
            CurrentPreview = null;
            return;
        }
        if (subject.Record == tab.Record)
        {
            CurrentPreview = tab.PreviewPane;
            return;
        }
        if (!_subjectPreviews.TryGetValue(subject.Record, out var pane))
        {
            MarkMaterialized(subject.Record);
            pane = PreviewPaneViewModel.Create(subject.Record, _env, FindAsset, NavigateToRef);
            _subjectPreviews[subject.Record] = pane;
        }
        _subjectPreviewOrder.Remove(subject.Record);
        _subjectPreviewOrder.AddFirst(subject.Record);
        while (_subjectPreviewOrder.Count > SubjectPreviewLimit)
        {
            var oldest = _subjectPreviewOrder.Last!.Value;
            _subjectPreviewOrder.RemoveLast();
            if (_subjectPreviews.Remove(oldest, out var evicted))
                evicted?.Dispose();
        }
        CurrentPreview = pane;
    }

    /// <summary>
    /// Gives a weapon its recoil preview while an extension with a preview module is on for it (live install only), and
    /// takes it away when none is: a weapon without one has no preview of its own, exactly as before extensions.
    /// </summary>
    private bool SyncWeaponPreview(AssetEditorViewModel tab)
    {
        var wants = _env.IsAvailable && tab.IsWeapon && tab.SimulatorRequests().Count > 0;
        if (wants == (tab.PreviewPane?.Content is WeaponPreviewViewModel))
            return false;
        if (wants)
        {
            tab.PreviewPane = PreviewPaneViewModel.ForWeapon(tab, _env, FindAsset, () => Simulators, ModuleQuestions);
            return true;
        }
        var old = tab.PreviewPane;
        tab.PreviewPane = null;
        old?.Dispose();
        return true;
    }

    private DispatcherTimer? _weaponPreviewTimer;

    /// <summary>An edit on a weapon an extension with a module targets may switch that extension on or off: once the
    /// edits settle, its preview follows (and the subjects with it).</summary>
    private void ScheduleWeaponPreviewSync()
    {
        if (_weaponPreviewTimer is null)
        {
            _weaponPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _weaponPreviewTimer.Tick += (_, _) =>
            {
                _weaponPreviewTimer.Stop();
                if (ActiveTab is { } tab && SyncWeaponPreview(tab))
                    RefreshPreviewSubjects();
            };
        }
        _weaponPreviewTimer.Stop();
        _weaponPreviewTimer.Start();
    }

    // Reference edits arrive per keystroke; the subject list is rebuilt once they settle.
    private DispatcherTimer? _subjectRefreshTimer;

    /// <summary>
    /// An edit on <paramref name="tab"/>'s asset: a cached preview of that asset shown for another tab reloads like the
    /// tab's own, and a changed reference on the active asset re-picks the preview's subjects (150 ms after typing stops).
    /// </summary>
    public void NotifyPreviewEdited(AssetEditorViewModel tab, PropertyItemViewModel item)
    {
        if (_subjectPreviews.TryGetValue(tab.Record, out var shown))
            shown?.NotifyEdited(item.Key);
        if (tab == ActiveTab && tab.HasSimulatorExtension && _env.IsAvailable)
            ScheduleWeaponPreviewSync();
        if (tab != ActiveTab || item is not RefPropertyViewModel)
            return;
        if (_subjectRefreshTimer is null)
        {
            _subjectRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _subjectRefreshTimer.Tick += (_, _) => RefreshPreviewSubjects();
        }
        _subjectRefreshTimer.Stop();
        _subjectRefreshTimer.Start();
    }

    /// <summary>Records changed outside their editor (table, compare): cached referenced-asset previews of them reload
    /// (a tab's own preview follows its <see cref="AssetEditorViewModel.SyncFromRecord"/>).</summary>
    private void NotifySubjectPreviewsChanged(Func<AssetRecord, bool> changed)
    {
        foreach (var (record, pane) in _subjectPreviews)
            if (changed(record))
                pane?.NotifyExternalChange();
    }

    /// <summary>Reloads every open preview (a tab's own or a cached referenced-asset one) of a record that matches.</summary>
    private void RefreshPreviewsOf(Func<AssetRecord, bool> changed)
    {
        foreach (var tab in OpenTabs)
            if (changed(tab.Record))
                tab.PreviewPane?.NotifyExternalChange();
        foreach (var (record, pane) in _subjectPreviews)
            if (changed(record))
                pane?.NotifyExternalChange();
    }

    // ══ Inspector: focused property, changes, links ══════════════════════════

    /// <summary>The active tab's focused row, flattened for the Inspector's property section.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInspectorProperty))]
    private PropertyItemViewModel? _inspectorProperty;

    public bool HasInspectorProperty => InspectorProperty is not null;

    [ObservableProperty] private string _inspectorThisValue = "";
    [ObservableProperty] private string _inspectorParentValue = "";
    [ObservableProperty] private string _inspectorDefaultValue = "";
    [ObservableProperty] private bool _inspectorHasParent;
    [ObservableProperty] private bool _inspectorCanUseParent;
    [ObservableProperty] private bool _inspectorCanUndo;
    [ObservableProperty] private string _inspectorUndoText = "";
    [ObservableProperty] private string _inspectorRangeText = "";

    /// <summary>Where an extension field's value is kept ("" for a deffile property).</summary>
    [ObservableProperty] private string _inspectorOriginText = "";

    /// <summary>Called by a tab when its focused row changes.</summary>
    public void OnFocusedPropertyChanged(AssetEditorViewModel tab)
    {
        if (tab == ActiveTab)
            RefreshInspectorProperty();
    }

    private PropertyItemViewModel? _watchedProperty;

    private void RefreshInspectorProperty()
    {
        var p = ActiveTab?.FocusedProperty;
        if (!ReferenceEquals(_watchedProperty, p))
        {
            if (_watchedProperty is not null)
                _watchedProperty.PropertyChanged -= WatchedProperty_Changed;
            _watchedProperty = p;
            if (p is not null)
                p.PropertyChanged += WatchedProperty_Changed;
        }
        InspectorProperty = p;
        if (p is null)
            return;
        InspectorThisValue = p.DisplayValue;
        InspectorHasParent = p.HasProvenance;
        InspectorParentValue = p.ParentValue is { } pv ? (pv.Length == 0 ? "(empty)" : pv) : "—";
        InspectorDefaultValue = p.Def.Default.Length == 0 ? "(empty)" : p.Def.Default;
        InspectorCanUseParent = p.IsOverride;
        InspectorCanUndo = p.IsChanged;
        InspectorUndoText = p.IsChanged ? $"(was {(p.BaselineValue.Length == 0 ? "empty" : p.BaselineValue)})" : "";
        InspectorRangeText = p is NumberPropertyViewModel { HasRange: true } n
            ? $"Allowed {Fmt(n.Def.Min)} to {Fmt(n.Def.Max)}."
            : "";
        InspectorOriginText = p.Def.Extension.Length > 0 && ActiveTab is { } tab
            ? $"From the {p.Def.Extension} extension. Saved in {System.IO.Path.GetFileName(tab.GdtName)}x, beside the GDT."
            : "";
    }

    private bool _inspectorPropertyQueued;

    private void WatchedProperty_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // One edit raises several of these (value, changed, override, off-default); refresh the
        // section once, after the edit, rather than once per notification.
        if (_inspectorPropertyQueued
            || e.PropertyName is not (nameof(PropertyItemViewModel.IsChanged) or nameof(PropertyItemViewModel.IsOverride)
                or nameof(PropertyItemViewModel.IsModified) or nameof(NumberPropertyViewModel.Value)
                or nameof(TogglePropertyViewModel.IsOn) or "Value"))
            return;
        _inspectorPropertyQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _inspectorPropertyQueued = false;
            RefreshInspectorProperty();
        }, DispatcherPriority.Background);
    }

    private static string Fmt(double v) => v.ToString(Math.Abs(v) >= 1000 ? "#,0.###" : "0.###", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void InspectorUseParent() => InspectorProperty?.RevertToParentCommand.Execute(null);

    [RelayCommand]
    private void InspectorUndoChange() => InspectorProperty?.RevertChangeCommand.Execute(null);

    /// <summary>Inspector "Go to field" on a problem row.</summary>
    [RelayCommand]
    private void GoToProblem(ProblemItem problem) => ActiveTab?.RevealProperty(problem.Key);

    [RelayCommand]
    private void UndoAllChanges()
    {
        // One undo step, so Ctrl+Z brings every value back: nothing to confirm.
        if (ActiveTab is not { } tab || tab.Changes.Count == 0)
            return;
        tab.RevertAllCommand.Execute(null);
        Status = $"{tab.LastHistoryText}. {CommandCatalog.Get(CommandCatalog.Undo).GestureText} brings them back.";
    }

    // ── Uses / Used by / Family ──────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsesTab), nameof(IsUsedByTab), nameof(IsFamilyTab), nameof(ActiveLinks), nameof(ActiveLinksEmptyText))]
    private InspectorLinks _linksTab = InspectorLinks.Uses;

    public bool IsUsesTab { get => LinksTab == InspectorLinks.Uses; set { if (value) LinksTab = InspectorLinks.Uses; } }
    public bool IsUsedByTab { get => LinksTab == InspectorLinks.UsedBy; set { if (value) LinksTab = InspectorLinks.UsedBy; } }
    public bool IsFamilyTab { get => LinksTab == InspectorLinks.Family; set { if (value) LinksTab = InspectorLinks.Family; } }

    public RangeObservableCollection<InspectorLinkViewModel> Uses { get; } = new();
    public RangeObservableCollection<InspectorLinkViewModel> UsedBy { get; } = new();
    public RangeObservableCollection<InspectorLinkViewModel> Family { get; } = new();

    [ObservableProperty] private int _usesCount;
    [ObservableProperty] private int _familyCount;

    public RangeObservableCollection<InspectorLinkViewModel> ActiveLinks => LinksTab switch
    {
        InspectorLinks.UsedBy => UsedBy,
        InspectorLinks.Family => Family,
        _ => Uses,
    };

    public string ActiveLinksEmptyText => LinksTab switch
    {
        // Partial: derivations come from the whole index, value references only from assets
        // opened or loaded this session (the Used by tab's tooltip says so too).
        InspectorLinks.UsedBy => "No opened or loaded asset references this one.",
        InspectorLinks.Family => "No parent template and no derived assets.",
        _ => "This asset references no other assets.",
    };

    private void RefreshLinks()
    {
        if (ActiveTab is not { } tab)
        {
            Uses.ReplaceAll(Array.Empty<InspectorLinkViewModel>());
            Family.ReplaceAll(Array.Empty<InspectorLinkViewModel>());
            UsedBy.ReplaceAll(Array.Empty<InspectorLinkViewModel>());
            UsesCount = FamilyCount = 0;
            return;
        }

        // Each list is built aside and swapped in with one notification.
        var uses = new List<InspectorLinkViewModel>();
        foreach (var r in tab.AllSentinel.All.OfType<RefPropertyViewModel>())
        {
            if (r.Value.Length == 0 || r.IsRuleHidden)
                continue;
            var target = FindAsset(r.RefType, r.Value);
            var name = r.Value;
            var refType = target?.Type ?? r.RefType;
            uses.Add(new InspectorLinkViewModel(name, refType, target is null ? "Missing" : r.Label, target is null, false,
                target is null ? null : () => NavigateToRef(refType, name)));
            if (uses.Count >= 200)
                break;
        }
        Uses.ReplaceAll(uses);
        UsesCount = Uses.Count;

        // Family: the parent chain (oldest first), this asset, then everything deriving from it.
        var chain = new List<AssetRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tab.Record.Name };
        var cur = tab.Record;
        while (cur.Parent is { } parentName && seen.Add(parentName) && FindAsset(tab.Record.Type, parentName) is { } parent)
        {
            chain.Insert(0, parent);
            cur = parent;
        }
        var family = new List<InspectorLinkViewModel>();
        foreach (var a in chain)
            family.Add(new InspectorLinkViewModel(a.Name, a.Type, a == chain[^1] ? "Parent" : "Ancestor", false, false, () => OpenAsset(a, preview: true)));
        if (chain.Count > 0 || (_byParent.TryGetValue(tab.Record.Name, out var any) && any.Count > 0))
            family.Add(new InspectorLinkViewModel(tab.Record.Name, tab.Record.Type, "This asset", false, true, null));
        if (_byParent.TryGetValue(tab.Record.Name, out var kids))
            foreach (var k in kids.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).Take(200))
                family.Add(new InspectorLinkViewModel(k.Name, k.Type, "Derived", false, false, () => OpenAsset(k, preview: true)));
        Family.ReplaceAll(family);
        FamilyCount = Family.Count(f => !f.IsSelf);

        // No lambda here may capture the tab: they would share one closure with every link's click action, and a link
        // left in a hidden recycled row would keep the closed tab alive.
        var tabName = tab.Name;
        UsedBy.ReplaceAll(References.Select(r => new InspectorLinkViewModel(r.Name, r.TypeName,
            r.Record.Parent is { } p && p.Equals(tabName, StringComparison.OrdinalIgnoreCase) ? "Derives" : "References",
            false, false, () => OpenAsset(r.Record, preview: true))));
        OnPropertyChanged(nameof(ActiveLinks));
    }

    [RelayCommand]
    private void SetLinksTab(string tab) =>
        LinksTab = Enum.TryParse<InspectorLinks>(tab, true, out var t) ? t : InspectorLinks.Uses;

    // ══ Command palette: > commands, @ properties (the commands come from MainViewModel.Commands.cs) ══

    [RelayCommand]
    private void OpenCommandPalette()
    {
        _paletteRecent = false;
        _paletteKeepsTab = false;
        PaletteText = ">";
        IsPaletteOpen = true;
        RefreshPaletteResults();
    }

    [RelayCommand]
    private void OpenRecentPalette()
    {
        _paletteRecent = true;
        _paletteKeepsTab = false;
        PaletteText = "";
        IsPaletteOpen = true;
        RefreshPaletteResults();
    }

    [RelayCommand]
    private void OpenPropertyPalette()
    {
        _paletteRecent = false;
        _paletteKeepsTab = false;
        PaletteText = "@";
        IsPaletteOpen = true;
        RefreshPaletteResults();
    }

    /// <summary>Palette hint line, which changes with the mode the prefix selects.</summary>
    [ObservableProperty]
    private string _paletteHint = AssetHint;

    private const string AssetHint = "↑↓ navigate · ⏎ open · > commands · @ property · Esc dismiss";
    private const string NewTabHint = "New tab · type to search · ⏎ opens it in its own tab";

    /// <summary>Handles the ">" and "@" palette modes; returns false for plain asset queries.</summary>
    private bool TryFillModalPalette(string text)
    {
        if (TryFillGdtPicker(text))
            return true;
        if (text.StartsWith('>'))
        {
            ShowPaletteRows(PaletteCommands(text[1..].Trim()));
            PaletteHint = "↑↓ navigate · ⏎ run · Esc dismiss";
            return true;
        }
        if (text.StartsWith('@'))
        {
            var q = text[1..].Trim();
            var items = new List<PaletteItemViewModel>();
            if (ActiveTab is { } tab)
            {
                // Label or GDT key, whichever matches better, ranked like the commands; form order breaks ties.
                var ranked = tab.AllSentinel.All
                    .Where(p => !p.IsRuleHidden)
                    .Select(p => (Property: p, Score: Best(TextScore(p.Label, q), TextScore(p.Key, q))))
                    .Where(s => s.Score is not null)
                    .OrderBy(s => s.Score)
                    .Take(40);
                foreach (var (p, _) in ranked)
                {
                    var captured = p;
                    items.Add(new PaletteItemViewModel(p.Label, p.Key, "≡", () => tab.RevealProperty(captured.Key))
                        { Detail = tab.CategoryOf(p)?.Name ?? "" });
                }
            }
            ShowPaletteRows(items);
            PaletteHint = ActiveTab is null
                ? "Open an asset first to jump to one of its properties"
                : "↑↓ navigate · ⏎ jump to property · Esc dismiss";
            return true;
        }
        PaletteHint = _paletteKeepsTab ? NewTabHint
            : text.Length > 0 ? AssetHint
            : _paletteRecent ? "Recently opened · type to search · > commands · @ property"
            : "Open tabs · type to search · > commands · @ property";
        return false;

        static int? Best(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
    }

    // ══ Explorer multi-select actions ════════════════════════════════════════

    /// <summary>Compare mode over an Explorer multi-selection (one type): the first is the base.</summary>
    public void CompareAssets(IReadOnlyList<AssetRecord> assets)
    {
        if (assets.Count < 2)
            return;
        var type = assets[0].Type;
        var candidates = _db.Assets
            .Where(a => a != assets[0] && a.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var cmp = new CompareViewModel(assets[0], candidates, assets[1], OnRecordEditedExternally, () => Compare = null, FindAsset, GdtOf);
        foreach (var extra in assets.Skip(2))
            if (extra.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                cmp.AddColumnCommand.Execute(extra);
        Compare = cmp;
    }

    /// <summary>Table mode over an Explorer multi-selection (the most common type among it).</summary>
    public void OpenTableFor(IReadOnlyList<AssetRecord> assets)
    {
        if (assets.Count == 0)
            return;
        var dominant = assets.GroupBy(a => a.Type).OrderByDescending(g => g.Count()).First();
        var rows = dominant.Take(TableViewModel.RowCap).ToList();
        ShowTable(dominant.Key, rows, dominant.Count(), otherTypesLeftOut: dominant.Count() < assets.Count);
    }

    // ══ Status bar ═══════════════════════════════════════════════════════════

    [RelayCommand]
    private void ToggleProblemsFilter()
    {
        if (!ShowExplorer)
        {
            // From a hidden Explorer the button means "show me the problems", never "hide them".
            ShowExplorerPane();
            ProblemsFilter = true;
            return;
        }
        ProblemsFilter = !ProblemsFilter;
    }
}
