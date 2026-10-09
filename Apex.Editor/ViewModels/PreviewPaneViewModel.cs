using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Preview;
using Apex.Editor.Services.Preview.Notetracks;
using Apex.Editor.Services.Preview.ToolsGfx;
using Apex.Render.Assets;
using Apex.Render.Data.Animation;
using Apex.Render.Data.Gdt;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Apex.Editor.ViewModels;

/// <summary>
/// What the Preview pane shows for one asset with a visual representation (APE's AssetViewer analogue). The concrete
/// content VM is chosen by asset type; unsupported types get no preview at all. It starts loading when created. Dispose
/// it when its tab goes away: that cancels any load in flight and releases what it shows.
/// </summary>
public sealed class PreviewPaneViewModel : IDisposable
{
    // Edits arrive per keystroke / scrub step; the preview reloads once they settle.
    private static readonly TimeSpan EditSettle = TimeSpan.FromMilliseconds(150);

    private bool _disposed;
    private DispatcherTimer? _refreshTimer;

    public IPreviewContent Content { get; }

    private PreviewPaneViewModel(IPreviewContent content)
    {
        Content = content;
        Refresh();
    }

    /// <summary>Starts a load. Each content shows its own error state for a failed load; anything that still escapes
    /// is logged here, so a preview can never take the editor down or leave a faulted task behind.</summary>
    private void Refresh() => _ = Observe(Content.RefreshAsync());

    private static async Task Observe(Task load)
    {
        try
        {
            await load;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (Services.CrashGuard.IsRecoverable(ex))
        {
            Services.CrashGuard.WriteLog(ex, "preview load");
        }
    }

    public static PreviewPaneViewModel? Create(
        AssetRecord record,
        GameEnvironment env,
        Func<string, string, AssetRecord?> resolve,
        Action<string, string> navigateToRef)
    {
        return record.Type.ToLowerInvariant() switch
        {
            "image" => new PreviewPaneViewModel(new ImagePreviewViewModel(record, env)),
            "material" => new PreviewPaneViewModel(new MaterialPreviewViewModel(record, env, resolve, navigateToRef)),
            // Gated on the live install: mock mode (and the headless screenshot harness) must never
            // construct the GL viewport.
            "xmodel" when env.IsAvailable => new PreviewPaneViewModel(new ModelPreviewViewModel(record, env, resolve)),
            "xanim" when env.IsAvailable => new PreviewPaneViewModel(new AnimPreviewViewModel(record, env, resolve)),
            _ => null,
        };
    }

    /// <summary>
    /// A weapon's recoil preview: only for a weapon an extension with a preview module is on for, and only with the
    /// install (<see cref="MainViewModel"/> decides, and takes it away when the extension goes off).
    /// </summary>
    public static PreviewPaneViewModel ForWeapon(AssetEditorViewModel tab, GameEnvironment env, Func<string, string, AssetRecord?> resolve,
        Func<Services.Extensions.Simulation.SimulatorHost> simulators, ModuleQuestions? questions = null) =>
        new(new WeaponPreviewViewModel(tab, env, resolve, simulators, questions));

    /// <summary>An in-editor edit changed <paramref name="key"/> — refresh if the preview depends on it.</summary>
    public void NotifyEdited(string key)
    {
        // A notetrack action edit only redraws the anim's timeline; the anim itself is not reloaded.
        if (!_disposed && Content is AnimPreviewViewModel anim && anim.TryReloadNotetracks(key))
            return;
        if (!_disposed && Content.DependsOn(key))
            ScheduleRefresh();
    }

    /// <summary>External change (table/compare edit, disk reload) — cheap full refresh.</summary>
    public void NotifyExternalChange()
    {
        if (!_disposed)
            ScheduleRefresh();
    }

    /// <summary>Refreshes once no further change has arrived for <see cref="EditSettle"/> (latest wins).</summary>
    private void ScheduleRefresh()
    {
        if (_refreshTimer is null)
        {
            _refreshTimer = new DispatcherTimer { Interval = EditSettle };
            _refreshTimer.Tick += (_, _) =>
            {
                _refreshTimer.Stop();
                if (!_disposed)
                    Refresh();
            };
        }
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _refreshTimer?.Stop();
        Content.Dispose();
    }
}

/// <summary>Typed content hosted inside the preview pane. <see cref="IDisposable.Dispose"/> cancels its load and
/// releases what it shows; it is not refreshed again afterwards.</summary>
public interface IPreviewContent : IDisposable
{
    Task RefreshAsync();
    bool DependsOn(string propertyKey);
}

/// <summary>
/// A preview load that failed in a way nobody anticipated (a malformed model, a conversion bug): the pane shows one plain
/// line and the detail goes to the crash log. The next load (an edit, reopening the asset) starts clean.
/// </summary>
internal static class PreviewFailure
{
    public const string Text = "This preview failed. Reopen the asset to try again.";

    /// <summary>Exception filter: logs and catches everything but runtime corruption.</summary>
    public static bool Contain(Exception ex, AssetRecord record, CancellationTokenSource cts)
    {
        if (!Services.CrashGuard.IsRecoverable(ex))
            return false;
        if (!cts.IsCancellationRequested)
            Services.CrashGuard.WriteLog(ex, $"preview of {record.Name} ({record.Type})");
        return true;
    }
}

/// <summary>
/// A preview's "Loading…", shown only once a load has run past <see cref="Threshold"/>: a cached model or a debounced
/// edit that settles fast never flashes it. <see cref="Begin"/> when a load starts (a newer one supersedes it),
/// <see cref="End"/> when the latest settles.
/// </summary>
internal sealed class DelayedLoading
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(150);

    private readonly Action<bool> _show;
    private int _generation;

    public DelayedLoading(Action<bool> show) => _show = show;

    public void Begin()
    {
        int generation = ++_generation;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation == _generation)
                _show(true);
        }, Threshold);
    }

    public void End()
    {
        _generation++;
        _show(false);
    }
}

// ── Image ────────────────────────────────────────────────────────────────────

/// <summary>One selectable channel-view mode button (RGB / R / G / B / A / normal-Z).</summary>
public sealed partial class ChannelModeOption : ObservableObject
{
    private readonly ImagePreviewViewModel _owner;

    public string Label { get; }
    public string Tip { get; }
    public ImageChannelMode Mode { get; }

    [ObservableProperty]
    private bool _isActive;

    public ChannelModeOption(ImagePreviewViewModel owner, string label, string tip, ImageChannelMode mode)
    {
        _owner = owner;
        Label = label;
        Tip = tip;
        Mode = mode;
    }

    [RelayCommand]
    private void Select() => _owner.SetMode(Mode);
}

/// <summary>Preview for an image asset: decodes the baseImage source (.tif/.tiff/.png/.tga).</summary>
public sealed partial class ImagePreviewViewModel : ObservableObject, IPreviewContent
{
    private readonly AssetRecord _record;
    private readonly GameEnvironment _env;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>The shown bitmap; owned by this VM (disposed when replaced).</summary>
    [ObservableProperty]
    private Bitmap? _bitmap;

    [ObservableProperty]
    private string? _info;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private bool _isLoading;

    /// <summary>The error line, hidden while a newer load shows "Loading…" in its place.</summary>
    public bool ShowError => !IsLoading && !string.IsNullOrEmpty(Error);

    public ObservableCollection<ChannelModeOption> Modes { get; }

    private ImageChannelMode _mode = ImageChannelMode.Rgba;
    private readonly DelayedLoading _loading;

    public ImagePreviewViewModel(AssetRecord record, GameEnvironment env)
    {
        _record = record;
        _env = env;
        _loading = new DelayedLoading(v => IsLoading = v);
        Modes = new ObservableCollection<ChannelModeOption>
        {
            new(this, "RGB", "Colour", ImageChannelMode.Rgba),
            new(this, "R", "Red channel", ImageChannelMode.R),
            new(this, "G", "Green channel", ImageChannelMode.G),
            new(this, "B", "Blue channel", ImageChannelMode.B),
            new(this, "A", "Alpha channel", ImageChannelMode.A),
            new(this, "N", "Normal Z, rebuilt from the normal map's X and Y", ImageChannelMode.NormalZ),
        };
        Modes[0].IsActive = true;
    }

    public void SetMode(ImageChannelMode mode)
    {
        var changed = _mode != mode;
        _mode = mode;
        // Always re-assert: the two-way IsChecked binding unchecks an active pill on re-click.
        foreach (var m in Modes)
            m.IsActive = m.Mode == mode;
        if (changed)
            _ = RefreshAsync();
    }

    public bool DependsOn(string propertyKey) =>
        propertyKey.Equals("baseImage", StringComparison.OrdinalIgnoreCase)
        || (SchemaRegistry.Get(_record.Type)?.Find(propertyKey)?.FileKind == PropertyFileKind.Texture);

    public async Task RefreshAsync()
    {
        if (_disposed)
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        var raw = _record.Properties.GetValueOrDefault("baseImage", "");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Fail("No baseImage set.");
            return;
        }
        if (!_env.IsAvailable)
        {
            Fail("Image previews need the BO3 install.");
            return;
        }

        _loading.Begin();
        try
        {
            var mode = _mode;
            var path = await Task.Run(() => PreviewFileResolver.ResolveImage(_env, raw), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            if (path is null)
            {
                Fail($"Source file not found: {PreviewText.Path(raw)}");
                return;
            }

            var loaded = await ImagePreviewLoader.LoadAsync(path, mode, ImagePreviewLoader.PreviewSize, cts.Token);
            if (cts.IsCancellationRequested)
            {
                loaded?.Bitmap.Dispose();
                return;
            }
            if (loaded is null)
            {
                Fail($"Could not decode {System.IO.Path.GetFileName(path)}.");
                return;
            }
            SetBitmap(loaded.Bitmap);
            Error = null;
            var rel = path.StartsWith(_env.Bo3Root ?? "", StringComparison.OrdinalIgnoreCase) && _env.Bo3Root is not null
                ? path[_env.Bo3Root.Length..].TrimStart('\\', '/')
                : path;
            Info = $"{loaded.SourceWidth} × {loaded.SourceHeight}  ·  {rel}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (PreviewFailure.Contain(ex, _record, cts))
        {
            if (cts.IsCancellationRequested)
                return; // superseded: the newer load owns the pane
            SetBitmap(null);
            Info = null;
            Error = PreviewFailure.Text;
        }
        finally
        {
            if (_cts == cts)
                _loading.End();
        }
    }

    private void Fail(string error)
    {
        _loading.End();
        SetBitmap(null);
        Error = error;
        Info = null;
    }

    private void SetBitmap(Bitmap? bitmap)
    {
        var old = Bitmap;
        Bitmap = bitmap;
        old?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        _loading.End();
        SetBitmap(null);
    }
}

// ── Material ─────────────────────────────────────────────────────────────────

/// <summary>One texture slot on a material (colorMap, normalMap, …) with a decoded thumbnail.</summary>
public sealed partial class MaterialSlotViewModel : ObservableObject, IDisposable
{
    private readonly Action<string, string> _navigate;
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Cancelled when the slot is dropped: its thumbnail load outlives material refreshes that keep it.</summary>
    public CancellationToken Lifetime => _lifetime.Token;

    public string Key { get; }
    public string Label { get; }
    public string ImageAssetName { get; }

    /// <summary>The thumbnail; owned by this slot (disposed with it).</summary>
    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private string? _status;

    public MaterialSlotViewModel(string key, string label, string imageAssetName, bool canOpen, Action<string, string> navigate)
    {
        Key = key;
        Label = label;
        ImageAssetName = imageAssetName;
        CanOpen = canOpen;
        _navigate = navigate;
        if (!canOpen)
            _status = "not in any loaded GDT";
    }

    /// <summary>The image asset is loaded: the slot opens it. A missing one says so on the slot instead.</summary>
    public bool CanOpen { get; }

    public string OpenTip => CanOpen ? $"Open image asset {ImageAssetName}" : $"{ImageAssetName} isn't in any loaded GDT";

    [RelayCommand]
    private void Open()
    {
        if (CanOpen)
            _navigate("image", ImageAssetName);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        var old = Thumbnail;
        Thumbnail = null;
        old?.Dispose();
    }
}

/// <summary>
/// Preview for a material asset: a grid of its texture slots (schema AssetRef→image properties),
/// each resolved through the referenced image asset's baseImage source.
/// </summary>
public sealed partial class MaterialPreviewViewModel : ObservableObject, IPreviewContent
{
    // Show the shading-defining maps first, in APE's order.
    private static readonly string[] SlotOrder = { "colorMap", "normalMap", "specColorMap", "glossMap", "occlusionMap" };

    private readonly AssetRecord _record;
    private readonly GameEnvironment _env;
    private readonly Func<string, string, AssetRecord?> _resolve;
    private readonly Action<string, string> _navigate;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public ObservableCollection<MaterialSlotViewModel> Slots { get; } = new();

    [ObservableProperty]
    private string? _summary;

    /// <summary>One line in the shading ball's place when there is no ball to show (no slots, no install).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private bool _isLoading;

    /// <summary>The error line, hidden while a newer load shows "Loading…" in its place.</summary>
    public bool ShowError => !IsLoading && !string.IsNullOrEmpty(Error);

    /// <summary>OpenGL shading-ball scene (fallback when the ToolsGfx renderer cannot be used); null in mock mode.</summary>
    [ObservableProperty]
    private ModelScene? _scene;

    /// <summary>The material on a sphere through the ToolsGfx renderer (APE's shaders and lighting).</summary>
    public ToolsGfxPreviewState Gfx { get; } = new();

    /// <summary>True while either 3D preview (ToolsGfx or GL sphere) has something to show.</summary>
    public bool HasSphere => Gfx.IsActive || Scene is not null;

    /// <summary>
    /// No BO3 install: there is no ball and no thumbnail to show, so the pane is one line instead of an empty viewport
    /// over a row of blank slots (the slots' image assets are in the Inspector's Uses list either way).
    /// </summary>
    public bool IsOffline => !_env.IsAvailable;

    private bool _forceGl;
    private readonly DelayedLoading _loading;

    public MaterialPreviewViewModel(
        AssetRecord record,
        GameEnvironment env,
        Func<string, string, AssetRecord?> resolve,
        Action<string, string> navigate)
    {
        _record = record;
        _env = env;
        _resolve = resolve;
        _navigate = navigate;
        _loading = new DelayedLoading(v => IsLoading = v);
    }

    public bool DependsOn(string propertyKey)
    {
        // Any material field can change the ToolsGfx evaluation (constants, techset, state); the summary line shows these two.
        if (Gfx.IsActive || propertyKey.Equals("materialType", StringComparison.OrdinalIgnoreCase)
            || propertyKey.Equals("surfaceType", StringComparison.OrdinalIgnoreCase))
            return true;
        var def = SchemaRegistry.Get(_record.Type)?.Find(propertyKey);
        return def is { Kind: PropertyKind.AssetRef } && def.RefType.Equals("image", StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSceneChanged(ModelScene? value) => OnPropertyChanged(nameof(HasSphere));

    /// <summary>The ToolsGfx viewport cannot draw: show the OpenGL shading ball instead.</summary>
    public void FallBackToGl(string reason)
    {
        if (_disposed)
            return;
        _forceGl = true;
        Gfx.Fallback(reason);
        OnPropertyChanged(nameof(HasSphere));
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed)
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        var schema = SchemaRegistry.Get(_record.Type);
        var imageProps = schema?.Properties
            .Where(p => p.Kind == PropertyKind.AssetRef && p.RefType.Equals("image", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<PropertyDef>();

        var filled = imageProps
            .Select(p => (Def: p, Value: _record.Properties.GetValueOrDefault(p.Key, "")))
            .Where(t => !string.IsNullOrWhiteSpace(t.Value))
            .OrderBy(t =>
            {
                var i = Array.FindIndex(SlotOrder, k => k.Equals(t.Def.Key, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? SlotOrder.Length : i;
            })
            .ToList();

        var mtlType = _record.Properties.GetValueOrDefault("materialType", "");
        var surface = _record.Properties.GetValueOrDefault("surfaceType", "");
        Summary = string.Join("  ·  ", new[] { mtlType, surface }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var lookup = _env.IsAvailable ? ToolsGfxPreviewService.BeginLoad(_resolve) : null;
        SyncSlots(filled, lookup);
        if (filled.Count == 0)
        {
            _loading.End();
            Gfx.Clear();
            Scene = null;
            OnPropertyChanged(nameof(HasSphere));
            Error = "No texture slots are set on this material.";
            return;
        }
        if (lookup is null)
        {
            _loading.End();
            Error = "Material previews need the BO3 install.";
            return;
        }
        Error = null;

        _loading.Begin();
        try
        {
            if (!_forceGl)
            {
                var name = _record.Name;
                var options = new PreviewModelOptions { SkipBrokenMaterials = true };
                bool ok = await Gfx.TryLoadAsync(_env, _resolve,
                    data => ToolsGfxPreviewService.Reuse(data, "material|" + name, options,
                        () => PreviewModelLoader.PrepareMeshes(data, name, new[] { PreviewShapes.Sphere(name) }, options: options,
                            cancellationToken: cts.Token), cts.Token),
                    cts.Token);
                OnPropertyChanged(nameof(HasSphere));
                if (ok || cts.IsCancellationRequested)
                {
                    if (ok)
                        Scene = null;
                    return;
                }
            }
            var scene = await ModelSceneBuilder.BuildMaterialSphereAsync(_record.Name, _env, lookup, cts.Token);
            if (!cts.IsCancellationRequested)
            {
                scene.Subject = _record;
                Scene = scene;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (PreviewFailure.Contain(ex, _record, cts))
        {
            if (cts.IsCancellationRequested)
                return; // superseded: the newer load owns the pane
            Gfx.Clear();
            Scene = null;
            OnPropertyChanged(nameof(HasSphere));
            Error = PreviewFailure.Text;
        }
        finally
        {
            if (_cts == cts)
                _loading.End();
        }
    }

    /// <summary>
    /// Brings <see cref="Slots"/> to <paramref name="filled"/> in place: slots whose image reference is unchanged keep
    /// their thumbnail; only new or changed references are decoded.
    /// </summary>
    private void SyncSlots(List<(PropertyDef Def, string Value)> filled, ApexGdtLookup? lookup)
    {
        var kept = new Dictionary<(string, string), MaterialSlotViewModel>();
        foreach (var slot in Slots)
            kept.TryAdd((slot.Key, slot.ImageAssetName), slot);

        for (int i = 0; i < filled.Count; i++)
        {
            var (def, value) = filled[i];
            if (kept.Remove((def.Key, value), out var existing))
            {
                if (i < Slots.Count && ReferenceEquals(Slots[i], existing))
                    continue;
                Slots.Remove(existing);
                Slots.Insert(i, existing);
                continue;
            }
            var slot = new MaterialSlotViewModel(def.Key, def.Label, value, _resolve("image", value) is not null, _navigate);
            Slots.Insert(i, slot);
            if (slot.CanOpen)
                _ = LoadSlotAsync(slot, value, lookup);
        }
        while (Slots.Count > filled.Count)
            Slots.RemoveAt(Slots.Count - 1);
        foreach (var dropped in kept.Values)
            dropped.Dispose();
    }

    private async Task LoadSlotAsync(MaterialSlotViewModel slot, string imageAssetName, ApexGdtLookup? lookup)
    {
        if (lookup is null)
            return; // offline: the pane shows one line, not the slots

        var ct = slot.Lifetime;
        try
        {
            var (found, path) = await Task.Run(() =>
            {
                var raw = lookup.Find(imageAssetName, "image")?.Fields.GetValueOrDefault("baseImage", "");
                return (raw is not null, string.IsNullOrWhiteSpace(raw) ? null : PreviewFileResolver.ResolveImage(_env, raw));
            }, ct);
            if (ct.IsCancellationRequested)
                return;
            if (!found)
            {
                slot.Status = "image asset not found";
                return;
            }
            if (path is null)
            {
                slot.Status = "source file not found";
                return;
            }

            var loaded = await ImagePreviewLoader.LoadAsync(path, ImageChannelMode.Rgba, ImagePreviewLoader.ThumbnailSize, ct);
            if (ct.IsCancellationRequested)
            {
                loaded?.Bitmap.Dispose();
                return;
            }
            if (loaded is null)
            {
                slot.Status = "can't decode";
                return;
            }
            var old = slot.Thumbnail;
            slot.Thumbnail = loaded.Bitmap;
            old?.Dispose();
            slot.Status = $"{loaded.SourceWidth}×{loaded.SourceHeight}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (Services.CrashGuard.IsRecoverable(ex))
        {
            Services.CrashGuard.WriteLog(ex, $"texture slot {imageAssetName}");
            if (!ct.IsCancellationRequested)
                slot.Status = "can't load";
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        _loading.End();
        Gfx.Clear();
        Scene = null;
        foreach (var slot in Slots)
            slot.Dispose();
        Slots.Clear();
    }
}

// ── Inheritance ──────────────────────────────────────────────────────────────

/// <summary>A derived asset has only the properties it overrides; the rest are its parents'.</summary>
internal static class PreviewInheritance
{
    /// <summary>The value of <paramref name="key"/> on <paramref name="record"/> or the nearest ancestor that fills it; "" when none does.</summary>
    public static string Value(AssetRecord record, Func<string, string, AssetRecord?> resolve, string key)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var cur = record; cur is not null && seen.Add(cur.Name); cur = cur.Parent is { } p ? resolve(record.Type, p) : null)
            if (cur.Properties.GetValueOrDefault(key, "") is { } v && !string.IsNullOrWhiteSpace(v))
                return v;
        return "";
    }
}

// ── Model ────────────────────────────────────────────────────────────────────

/// <summary>
/// Preview for an xmodel asset: parses the exported model file referenced by <c>filename</c> and
/// builds a renderable scene (textures resolved through each part's material chain) for the GL
/// viewport. Only constructed when the live BO3 install is available.
/// </summary>
public sealed partial class ModelPreviewViewModel : ObservableObject, IPreviewContent
{
    private static readonly string[] ModelExtensions = { ".xmodel_bin", ".xmodel_export" };

    private readonly AssetRecord _record;
    private readonly GameEnvironment _env;
    private readonly Func<string, string, AssetRecord?> _resolve;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    [ObservableProperty]
    private ModelScene? _scene;

    [ObservableProperty]
    private string? _stats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private bool _isLoading;

    /// <summary>The error line, hidden while a newer load shows "Loading…" in its place.</summary>
    public bool ShowError => !IsLoading && !string.IsNullOrEmpty(Error);

    /// <summary>The ToolsGfx renderer (APE's frame) when usable; otherwise <see cref="Scene"/> drives the GL viewport.</summary>
    public ToolsGfxPreviewState Gfx { get; } = new();

    private bool _forceGl;
    private readonly DelayedLoading _loading;

    public ModelPreviewViewModel(AssetRecord record, GameEnvironment env, Func<string, string, AssetRecord?> resolve)
    {
        _record = record;
        _env = env;
        _resolve = resolve;
        _loading = new DelayedLoading(v => IsLoading = v);
    }

    public bool DependsOn(string propertyKey) =>
        propertyKey.Equals("filename", StringComparison.OrdinalIgnoreCase)
        || propertyKey.Equals("skinOverride", StringComparison.OrdinalIgnoreCase)
        || (Gfx.IsActive && propertyKey.EndsWith("Lod", StringComparison.OrdinalIgnoreCase))
        || (SchemaRegistry.Get(_record.Type)?.Find(propertyKey)?.FileKind == PropertyFileKind.Model);

    /// <summary>The ToolsGfx viewport cannot draw this model: switch to the OpenGL approximation.</summary>
    public void FallBackToGl(string reason)
    {
        if (_disposed)
            return;
        _forceGl = true;
        Gfx.Fallback(reason);
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed)
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        var raw = FindModelPath();
        if (string.IsNullOrWhiteSpace(raw))
        {
            Scene = null;
            Gfx.Clear();
            Error = "No model filename set.";
            Stats = null;
            _loading.End();
            return;
        }

        _loading.Begin();
        try
        {
            if (!_forceGl)
            {
                var name = _record.Name;
                if (await Gfx.TryLoadAsync(_env, _resolve,
                        data => ToolsGfxPreviewService.PrepareModel(data, name, new PreviewModelOptions { SkipBrokenMaterials = true }, cts.Token),
                        cts.Token))
                {
                    var m = Gfx.Model!;
                    Scene = null;
                    Error = null;
                    int bones = m.Lods[0].Bones.Count;
                    Stats = $"{m.TriangleCount(0):N0} tris  ·  {m.Lods.Count} LOD{(m.Lods.Count == 1 ? "" : "s")}  ·  {bones} bone{(bones == 1 ? "" : "s")}  ·  {m.Materials.Count} materials"
                            + (m.BrokenMaterialCount > 0 ? $" ({m.BrokenMaterialCount} missing)" : "")
                            + (ToolsGfxPreviewService.ConversionNote(m) is { } converted ? "  ·  " + converted : "");
                    return;
                }
                if (cts.IsCancellationRequested)
                    return;
            }

            var lookup = ToolsGfxPreviewService.BeginLoad(_resolve);
            var path = await Task.Run(() => _env.ModelExportDir is null
                ? null
                : PreviewFileResolver.ResolveRelative(_env, _env.ModelExportDir, raw, ModelExtensions)
                  ?? (_env.Bo3Root is null ? null : PreviewFileResolver.ResolveRelative(_env, _env.Bo3Root, raw, ModelExtensions)), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            if (path is null)
            {
                Scene = null;
                Error = $"Model file not found: {PreviewText.Path(raw)}";
                Stats = null;
                return;
            }

            var model = await Services.Preview.Formats.ModelReader.LoadAsync(path, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            if (model is null || model.Parts.Count == 0)
            {
                Scene = null;
                Error = $"Could not parse {System.IO.Path.GetFileName(path)}.";
                Stats = null;
                return;
            }

            var scene = await ModelSceneBuilder.BuildAsync(model, _env, lookup, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            scene.Subject = _record;
            Scene = scene;
            Error = null;
            Stats = $"{model.TriangleCount:N0} tris  ·  {model.Bones.Count} bones  ·  {model.MaterialNames.Count} materials  ·  {System.IO.Path.GetFileName(path)}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (PreviewFailure.Contain(ex, _record, cts))
        {
            if (cts.IsCancellationRequested)
                return; // superseded: the newer load owns the pane
            Gfx.Clear();
            Scene = null;
            Stats = null;
            Error = PreviewFailure.Text;
        }
        finally
        {
            if (_cts == cts)
                _loading.End();
        }
    }

    /// <summary>The record's model file value: first filled schema FileKind==Model property, else literal <c>filename</c>.</summary>
    private string FindModelPath()
    {
        var schema = SchemaRegistry.Get(_record.Type);
        if (schema is not null)
        {
            foreach (var prop in schema.Properties)
            {
                if (prop.FileKind != PropertyFileKind.Model)
                    continue;
                var value = PreviewInheritance.Value(_record, _resolve, prop.Key);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        return PreviewInheritance.Value(_record, _resolve, "filename");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        _loading.End();
        Gfx.Clear();
        Scene = null;
    }
}

// ── XAnim ────────────────────────────────────────────────────────────────────

/// <summary>
/// Preview for an xanim asset, the way APE's AssetViewer plays it (animation.md): the anim on its <c>previewModel</c>
/// (else the <c>model</c> file) through the ToolsGfx renderer, skinned on the GPU from APE's xanim cache (or the raw
/// export matched by bone name when the cache is missing), on APE's clock — real time, looping over the duration.
/// Falls back to the OpenGL viewport (raw export CPU-skinned on the exported skeleton model) when the ToolsGfx
/// renderer or the model's caches are unavailable. Only constructed in live mode.
/// </summary>
public sealed partial class AnimPreviewViewModel : ObservableObject, IPreviewContent
{
    private static readonly string[] AnimExtensions = { ".xanim_bin", ".xanim_export" };
    private static readonly string[] ModelExtensions = { ".xmodel_bin", ".xmodel_export" };

    // The transport readout while playing: reformatting it every display frame buys nothing.
    private static readonly TimeSpan LabelInterval = TimeSpan.FromMilliseconds(100);

    private readonly AssetRecord _record;
    private readonly GameEnvironment _env;
    private readonly Func<string, string, AssetRecord?> _resolve;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    // GL fallback: CPU skinning of the raw export.
    private AnimSkinner? _skinner;
    private DispatcherTimer? _timer;
    private bool _viewAttached;
    private bool _skinBusy;
    private int? _pendingFrame;
    private float _fps = 30f;

    // ToolsGfx playback runs on the view's display frames (TopLevel.RequestAnimationFrame).
    private TopLevel? _frameSource;
    private bool _frameRequested;
    private readonly System.Diagnostics.Stopwatch _labelClock = new();

    // Debug hooks for scripted screenshot verification (inert otherwise):
    //   APEX_PREVIEW_ANIM_T=<0..1>   open paused at this normalised time;   APEX_PREVIEW_ANIM_PLAY=1   start playing
    private static readonly float? AnimStartTime =
        float.TryParse(Environment.GetEnvironmentVariable("APEX_PREVIEW_ANIM_T"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var t) ? Math.Clamp(t, 0f, 1f) : null;
    private static readonly bool AnimAutoPlay = Environment.GetEnvironmentVariable("APEX_PREVIEW_ANIM_PLAY") == "1";

    // ToolsGfx: APE's clock over the clip.
    private bool _forceGl;
    private float _time;
    private float _duration;
    private int _clipFrames;
    private bool _syncingFrame;
    private readonly System.Diagnostics.Stopwatch _clock = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTransport))]
    private ModelScene? _scene;

    [ObservableProperty]
    private string? _stats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private bool _isLoading;

    /// <summary>The error line, hidden while a newer load shows "Loading…" in its place.</summary>
    public bool ShowError => !IsLoading && !string.IsNullOrEmpty(Error);

    private readonly DelayedLoading _loading;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _currentFrame;

    /// <summary>Slider maximum (frame count - 1; 0 while nothing is loaded).</summary>
    [ObservableProperty]
    private int _lastFrame;

    [ObservableProperty]
    private string? _transportLabel;

    /// <summary>The notetrack timeline under the transport: the clip's exported notetracks and GDT actions, and their sounds.</summary>
    public NotetrackTimeline Timeline { get; }

    /// <summary>The ToolsGfx renderer (APE's frame) when usable; otherwise <see cref="Scene"/> drives the GL viewport.</summary>
    public ToolsGfxPreviewState Gfx { get; } = new();

    /// <summary>The GPU skinning of <see cref="Gfx"/>'s model at the current time (bound to the ToolsGfx viewport).</summary>
    [ObservableProperty]
    private AnimPlaybackPose? _pose;

    // Viewmodel options, remembered for the session so the next anim opens the same way. Debug hooks for scripted
    // screenshots set the initial values: APEX_PREVIEW_ANIM_FP=1 (first person), APEX_PREVIEW_ANIM_FOV=<cg_fov>,
    // APEX_PREVIEW_ANIM_JOINTS=1, APEX_PREVIEW_ANIM_HANDS=<xmodel>, APEX_PREVIEW_ANIM_GUN=<xmodel|none>.
    private static bool _sessionFirstPerson = Environment.GetEnvironmentVariable("APEX_PREVIEW_ANIM_FP") == "1";
    private static bool _sessionShowJoints = Environment.GetEnvironmentVariable("APEX_PREVIEW_ANIM_JOINTS") == "1";
    private static double _sessionFov =
        double.TryParse(Environment.GetEnvironmentVariable("APEX_PREVIEW_ANIM_FOV"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var fov) ? Math.Clamp(fov, MinFov, MaxFov) : 65;
    private static readonly string? ScriptedHands = Environment.GetEnvironmentVariable("APEX_PREVIEW_ANIM_HANDS") is { Length: > 0 } h ? h : null;
    private static string? _sessionHands = ScriptedHands;

    /// <summary>The viewhands last chosen in an anim preview this session: the last place a preview looks for hands when
    /// neither the anim, its weapon nor its sibling anims name any.</summary>
    internal static string? SessionHands => _sessionHands;

    /// <summary>The first-person FOV last chosen in an anim preview this session.</summary>
    internal static double SessionFov => _sessionFov;

    /// <summary><c>APEX_PREVIEW_ANIM_PARITY=1</c>: evaluate exactly like APE (single model, useBones = 0 double bind offset,
    /// no attached gun) instead of the authored motion on the composed viewmodel. A verification switch, not a user option
    /// (RenderCheck / AnimCheck compare against APE with it).</summary>
    private static readonly bool ApeParity = Environment.GetEnvironmentVariable("APEX_PREVIEW_ANIM_PARITY") == "1";

    /// <summary>The game's <c>cg_fov</c> range (animation.md §12).</summary>
    public const double MinFov = 65, MaxFov = 120;

    /// <summary>Look through the rig's <c>tag_camera</c> / <c>tag_view</c> instead of orbiting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LooksThroughCamera), nameof(CanFrame))]
    private bool _firstPerson = _sessionFirstPerson;

    /// <summary>True when the loaded rig has a camera tag to look through (<see cref="FirstPerson"/> needs one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirstPersonTip), nameof(LooksThroughCamera), nameof(CanFrame))]
    private bool _hasViewTag;

    /// <summary>Frame is offered only while the camera orbits: looking through the rig's camera there is nothing to frame
    /// (the wheel changes the field of view instead).</summary>
    public bool CanFrame => !LooksThroughCamera;

    /// <summary>The First person toggle: on only while actually looking through the camera tag (a rig without one shows it
    /// off and disabled, and the preference comes back with the next rig that has one).</summary>
    public bool LooksThroughCamera
    {
        get => FirstPerson && HasViewTag;
        set => FirstPerson = value;
    }

    public string FirstPersonTip => HasViewTag
        ? "See the anim as the player does, through the rig's camera. The wheel changes the field of view"
        : "This rig has no camera tag (tag_camera or tag_view) to look through, so it isn't a first-person anim";

    /// <summary>First-person FOV as <c>cg_fov</c> (BO3 default 65; the PC setting goes to 120).</summary>
    [ObservableProperty]
    private double _viewmodelFov = _sessionFov;

    /// <summary>Draw the rig's joints over the frame (always drawn when nothing has geometry).</summary>
    [ObservableProperty]
    private bool _showJoints = _sessionShowJoints;

    private static bool _sessionLoop = true;

    /// <summary>Playback wraps at the end (APE's clock); off, it stops on the last frame. Remembered for the session.</summary>
    [ObservableProperty]
    private bool _loop = _sessionLoop;

    partial void OnLoopChanged(bool value) => _sessionLoop = value;

    private static readonly PropertyDef HandsDef = new("previewHands", "Hands", "", PropertyKind.AssetRef,
        "The viewhands the anim plays on. Empty: the weapon's handModel, else the hands its sibling anims preview on.") { RefType = "xmodel" };

    private static readonly PropertyDef GunDef = new("previewGun", "Gun", "", PropertyKind.AssetRef,
        "The gun in the hands. Empty: previewAttachModel, else the gun of the weapon that plays the anim. none: no gun.") { RefType = "xmodel" };

    /// <summary>The hands box (an xmodel field with suggestions): empty finds the hands; a name overrides them.</summary>
    public RefPropertyViewModel HandsField { get; } = new(HandsDef, ScriptedHands ?? "", null);

    /// <summary>The gun box: empty finds the gun; "none" draws none.</summary>
    public RefPropertyViewModel GunField { get; } =
        new(GunDef, Environment.GetEnvironmentVariable("APEX_PREVIEW_ANIM_GUN") is { Length: > 0 } g ? g : "", null);

    /// <summary>The line under the hands box: why the typed name can't be used, else (box empty) the hands drawn and
    /// where they came from. Its space is kept, so the box never moves.</summary>
    [ObservableProperty]
    private string? _handsNote;

    /// <summary>The line under the gun box, as <see cref="HandsNote"/>.</summary>
    [ObservableProperty]
    private string? _gunNote;

    /// <summary>What the composed viewmodel shows and where each part came from (the caption on the render).</summary>
    [ObservableProperty]
    private string? _viewmodelInfo;

    /// <summary>The stats line's tooltip: where the motion came from in a sentence, the file whole, the numbers.</summary>
    [ObservableProperty]
    private string? _statsTip;

    /// <summary>True while a viewport with a playable anim is shown (transport row visible).</summary>
    public bool HasTransport => Scene is not null || (Gfx.IsActive && Pose is not null);

    /// <summary>The hands / gun boxes: only for an anim without a <c>previewModel</c>, where the viewhands have to come
    /// from somewhere (with one, the GDT already says what to draw).</summary>
    public bool ShowViewmodelChoice => !ApeParity && !HasPreviewModel;

    private bool HasPreviewModel =>
        _record.Properties.GetValueOrDefault("previewModel", "").Trim() is { Length: > 0 } pm && _resolve("xmodel", pm) is not null;

    // The hands / gun the current composition was built with: a commit that changes neither reloads nothing.
    private (string? Hands, string? Gun) _appliedChoice;

    public AnimPreviewViewModel(AssetRecord record, GameEnvironment env, Func<string, string, AssetRecord?> resolve,
        NotetrackTimeline? timeline = null)
    {
        _record = record;
        _env = env;
        _resolve = resolve;
        Timeline = timeline ?? new NotetrackTimeline();
        _loading = new DelayedLoading(v => IsLoading = v);
        Gfx.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ToolsGfxPreviewState.IsActive))
                OnPropertyChanged(nameof(HasTransport));
        };
        HandsField.InitBaseline(HandsField.Value);
        GunField.InitBaseline(GunField.Value);
        HandsField.Edited += _ => ApplyViewmodel();
        GunField.Edited += _ => ApplyViewmodel();
        CheckViewmodelFields();
    }

    partial void OnPoseChanged(AnimPlaybackPose? value) => OnPropertyChanged(nameof(HasTransport));

    partial void OnFirstPersonChanged(bool value) => _sessionFirstPerson = value;

    partial void OnViewmodelFovChanged(double value) => _sessionFov = value;

    partial void OnShowJointsChanged(bool value)
    {
        _sessionShowJoints = value;
        if (Pose is AnimRigPose rig)
            rig.ShowJoints = value;
    }

    /// <summary>A hands or gun box committed (Enter, or leaving it): says what's wrong with the name, and re-composes the
    /// viewmodel when what it would draw changed. A name that isn't an xmodel is not drawn (the hands are found instead).</summary>
    private void ApplyViewmodel()
    {
        CheckViewmodelFields();
        var choice = CurrentChoice();
        if (choice.Hands is { } hands)
            _sessionHands = hands;
        if ((choice.Hands, choice.Gun) == _appliedChoice)
            return;
        // A newer load supersedes one still running.
        _ = RefreshAsync();
    }

    /// <summary>The boxes as a choice: a name that isn't an xmodel in the loaded GDTs counts as empty.</summary>
    private ViewmodelChoice CurrentChoice()
    {
        var hands = Normalize(HandsField.Value);
        var gun = Normalize(GunField.Value);
        if (hands is not null && _resolve("xmodel", hands) is null)
            hands = null;
        if (gun is not null && !gun.Equals("none", StringComparison.OrdinalIgnoreCase) && _resolve("xmodel", gun) is null)
            gun = null;
        return new ViewmodelChoice(hands, gun, _sessionHands);

        static string? Normalize(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    // What the last load drew in the hands and the gun, and where each came from (the notes under empty boxes).
    private string? _drawnHands, _drawnGun;

    /// <summary>The notes under the boxes: a typed name that can't be used says so (it is drawn without); an empty box
    /// says what is drawn in its place.</summary>
    private void CheckViewmodelFields()
    {
        HandsField.Problem = Unknown(HandsField.Value, allowNone: false);
        GunField.Problem = Unknown(GunField.Value, allowNone: true);
        HandsNote = HandsField.Problem ?? (string.IsNullOrWhiteSpace(HandsField.Value) ? _drawnHands ?? "Empty finds the hands: the weapon's handModel, or its sibling anims'" : null);
        GunNote = GunField.Problem ?? (string.IsNullOrWhiteSpace(GunField.Value) ? _drawnGun ?? "Empty finds the gun: previewAttachModel, or the weapon's gunModel" : null);

        string? Unknown(string? value, bool allowNone) =>
            value?.Trim() is not { Length: > 0 } name || allowNone && name.Equals("none", StringComparison.OrdinalIgnoreCase) || _resolve("xmodel", name) is not null
                ? null
                : $"{name} isn't an xmodel in the loaded GDTs";
    }

    /// <summary>The hands and gun the scene drew, for the notes under empty boxes.</summary>
    private void NoteDrawn(ViewmodelScene? scene)
    {
        _drawnHands = scene?.Hands is { } hands ? $"Drawing {hands}" + (scene.HandsVia is { } via and not "chosen" ? $", {via}" : "") : null;
        var gun = scene?.Description.Split(" · ").FirstOrDefault(n => n.StartsWith("gun ", StringComparison.Ordinal));
        _drawnGun = gun is null ? null : "Drawing " + gun[4..];
        CheckViewmodelFields();
    }

    public bool DependsOn(string propertyKey)
    {
        // The cache key (type, looping, useBones, node) picks which converted clip APE loads.
        foreach (var key in new[] { "filename", "previewModel", "model", "type", "looping", "useBones", "node", "previewAttachModel", "previewAlignParentTag" })
            if (propertyKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                return true;
        var kind = SchemaRegistry.Get(_record.Type)?.Find(propertyKey)?.FileKind;
        return kind is PropertyFileKind.Anim or PropertyFileKind.Model;
    }

    /// <summary>The ToolsGfx viewport cannot draw: switch to the OpenGL approximation.</summary>
    public void FallBackToGl(string reason)
    {
        if (_disposed)
            return;
        _forceGl = true;
        Gfx.Fallback(reason);
        Pose = null;
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_disposed)
            return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsPlaying = false;
        OnPropertyChanged(nameof(ShowViewmodelChoice));

        var animRaw = FindAnimPath();
        _loading.Begin();
        try
        {
            var animPath = string.IsNullOrWhiteSpace(animRaw)
                ? null
                : await Task.Run(() => ResolveFile(_env.XanimExportDir, animRaw, AnimExtensions), cts.Token);
            if (cts.IsCancellationRequested)
                return;

            if (!_forceGl && await TryLoadGfxAsync(animPath, cts))
                return;
            if (cts.IsCancellationRequested)
                return;
            Pose = null;

            if (string.IsNullOrWhiteSpace(animRaw))
            {
                ClearLoaded("No animation filename set.");
                return;
            }
            if (animPath is null)
            {
                ClearLoaded($"Animation file not found: {PreviewText.Path(animRaw)}");
                return;
            }

            var lookup = ToolsGfxPreviewService.BeginLoad(_resolve);
            var candidates = new[] { "previewModel", "model" }
                .Select(k => _record.Properties.GetValueOrDefault(k, ""))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();
            var modelPath = await Task.Run(() => ResolveSkeletonPath(candidates, lookup), cts.Token);
            if (cts.IsCancellationRequested)
                return;
            if (modelPath is null)
            {
                ClearLoaded(candidates.Count == 0
                    ? "Set previewModel (or model) to preview this animation."
                    : $"Model file not found: {PreviewText.Path(candidates[0])}");
                return;
            }

            var animTask = Services.Preview.Formats.AnimReader.LoadAsync(animPath, cts.Token);
            var modelTask = Services.Preview.Formats.ModelReader.LoadAsync(modelPath, cts.Token);
            await Task.WhenAll(animTask, modelTask);
            if (cts.IsCancellationRequested)
                return;

            var anim = animTask.Result;
            var model = modelTask.Result;
            if (anim is null || anim.Frames.Count == 0)
            {
                ClearLoaded($"Could not parse {System.IO.Path.GetFileName(animPath)}.");
                return;
            }
            if (model is null)
            {
                ClearLoaded($"Could not parse {System.IO.Path.GetFileName(modelPath)}.");
                return;
            }
            if (model.Parts.Count == 0)
            {
                ClearLoaded($"{System.IO.Path.GetFileName(modelPath)} is a skeleton without geometry: set previewModel to see the "
                            + "animation on viewhands.");
                return;
            }

            var scene = await ModelSceneBuilder.BuildAsync(model, _env, lookup, cts.Token);
            // Skinner construction allocates the double buffers — keep it off the UI thread too.
            var skinner = await Task.Run(() =>
            {
                var s = new AnimSkinner(model, anim);
                scene.UpdateVertices(s.SkinFrame(0));
                return s;
            }, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            _skinner = skinner;
            _fps = anim.FrameRate > 0 ? anim.FrameRate : 30f;
            LastFrame = Math.Max(anim.Frames.Count - 1, 0);
            LoadTimeline(anim.Notetracks);
            HasViewTag = false;
            CurrentFrame = 0;
            scene.Subject = _record;
            Scene = scene;
            Error = null;
            var file = System.IO.Path.GetFileName(animPath);
            Stats = $"export file{(anim.ReadProblem is { } problem ? $" ({problem})" : "")}  ·  {PreviewText.MiddleTrim(file, 34)}  ·  "
                    + $"{model.TriangleCount:N0} tris  ·  {skinner.MatchedParts}/{skinner.TotalAnimParts} parts matched";
            StatsTip = $"The approximate preview plays the export file on the model's own skeleton.\n{file}";
            UpdateTransportLabel();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (PreviewFailure.Contain(ex, _record, cts))
        {
            if (cts.IsCancellationRequested)
                return; // superseded: the newer load owns the pane
            Gfx.Clear();
            ClearLoaded(PreviewFailure.Text);
        }
        finally
        {
            if (_cts == cts)
                _loading.End();
        }
    }

    /// <summary>
    /// Shows the clip's notetracks on the timeline: its exported notetracks and the GDT entry's notetrack actions (a few
    /// hundred dictionary reads; sound aliases are looked up on a worker afterwards).
    /// </summary>
    private void LoadTimeline(IReadOnlyList<(string Name, int Frame)> exportNotes)
    {
        _exportNotes = exportNotes;
        // The clip opens on frame 0 (set right after); Load's task never faults, so nothing awaits it.
        _ = Timeline.Load(TimelineMarkers(exportNotes), LastFrame, Aliases(), currentFrame: 0);
    }

    private IEnumerable<NotetrackMarker> TimelineMarkers(IReadOnlyList<(string Name, int Frame)> exportNotes) =>
        GdtNotetracks.Export(exportNotes, LastFrame).Concat(GdtNotetracks.Read(_record.Properties, exportNotes, LastFrame));

    private SoundAliasIndex? Aliases() => _env.Bo3Root is { } root ? SoundAliasIndex.ForInstall(root) : null;

    // The loaded clip's exported notetracks (null while none is loaded): a GDT notetrack edit rebuilds the markers from them.
    private IReadOnlyList<(string Name, int Frame)>? _exportNotes;

    /// <summary>
    /// An edit to a GDT notetrack key (<c>&lt;id&gt;action</c>, <c>frame</c>, <c>actionparam1</c>, <c>useexistingnote</c>…)
    /// rebuilds the timeline's markers in place: a few hundred dictionary reads, the anim and its pose untouched.
    /// False for any other key.
    /// </summary>
    public bool TryReloadNotetracks(string propertyKey)
    {
        if (_disposed || !GdtNotetracks.IsNotetrackKey(propertyKey))
            return false;
        // No clip yet (one is loading): nothing to redraw, and nothing is lost. LoadTimeline builds the markers on the UI
        // thread when the load lands, from the record as it is then, which already holds this edit.
        if (_exportNotes is { } notes)
            _ = Timeline.Reload(TimelineMarkers(notes), Aliases());
        return true;
    }

    /// <summary>
    /// The ToolsGfx path: the preview model through APE's renderer and the clip evaluated like APE's model instance.
    /// False (with <see cref="ToolsGfxPreviewState.FallbackStatus"/> set) when anything it needs is unavailable.
    /// </summary>
    private async Task<bool> TryLoadGfxAsync(string? rawAnimPath, CancellationTokenSource cts)
    {
        var name = _record.Name;
        var fields = new Dictionary<string, string>(_record.Properties, StringComparer.Ordinal);
        AnimPreviewClip? clip = null;
        ViewmodelScene? scene = null;
        bool parity = ApeParity;
        var choice = CurrentChoice();
        _appliedChoice = (choice.Hands, choice.Gun);
        bool ok = await Gfx.TryLoadAsync(_env, _resolve, data =>
        {
            if (parity)
            {
                var model = AnimPreviewSource.PrepareModel(data, name, fields, cts.Token);
                clip = AnimPreviewSource.LoadClip(data, name, fields, rawAnimPath, cts.Token);
                return model;
            }
            scene = AnimPreviewSource.PrepareViewmodel(data, name, fields, choice, rawAnimPath, cts.Token);
            clip = scene.Clip;
            return scene.Primary;
        }, cts.Token);
        if (!ok || cts.IsCancellationRequested || clip is null || Gfx.Model is not { } model)
            return false;

        AnimPlaybackPose pose = scene is not null
            ? new AnimRigPose(model, scene.Attached, scene.Rig, clip.Clip) { ShowJoints = ShowJoints }
            : new AnimPreviewPose(model, clip.Clip);
        ViewmodelInfo = scene is null
            ? "Evaluated exactly like APE, on the preview model only (APEX_PREVIEW_ANIM_PARITY)"
            : scene.Description.Length > 0 ? scene.Description : null;
        NoteDrawn(scene);
        _skinner = null;
        Scene = null;
        _clipFrames = Math.Max(clip.Clip.FrameCount, 1);
        _fps = clip.Clip.Framerate > 0 ? clip.Clip.Framerate : 30f;
        _duration = clip.Clip.Duration;
        _time = 0f;
        LastFrame = Math.Max(_clipFrames - 1, 0);
        LoadTimeline(clip.Clip.Notetracks.Select(n => (n.Name, (int)n.Frame)).ToList());
        Pose = pose;
        // The renderer looks through the view tag only when the rig has one; otherwise first person does nothing.
        HasViewTag = ((IPreviewPose)pose).ViewTag is not null;
        SetFrameSilently(0);
        Error = pose.IsAnimated ? null : "The preview model has no skeleton to animate.";
        int tris = model.Lods.Count > 0 ? model.TriangleCount(0) : 0;
        if (scene is not null)
            tris += scene.Attached.Where(a => a.Lods.Count > 0).Sum(a => a.TriangleCount(0));
        // Frames and rate are on the transport; this line says where the motion came from first (the part worth reading),
        // its file trimmed in the middle (names in a family differ at the end), then what was drawn.
        var bones = $"{pose.MatchedBones}/{Math.Max(pose.BoneCount - 1, 0)} bones animated";
        Stats = string.Join("  ·  ", new[] { clip.Description, clip.File is { } f ? PreviewText.MiddleTrim(f, 34) : null, $"{tris:N0} tris", bones }
            .Where(p => p is not null));
        StatsTip = string.Join("\n", new[] { clip.Detail, clip.File, $"{tris:N0} triangles, {bones}" }.Where(p => p is not null));
        if (AnimStartTime is { } start)
        {
            _time = start * _duration;
            ApplyTime();
        }
        UpdateTransportLabel();
        if (ToolsGfxPreviewService.IsLogging)
            ToolsGfxPreviewService.Log($"{name} | anim on {model.Name}: {Stats}");
        if (AnimAutoPlay)
            Dispatcher.UIThread.Post(() => IsPlaying = true);
        return true;
    }

    private void ClearLoaded(string error)
    {
        _skinner = null;
        Scene = null;
        Pose = null;
        HasViewTag = false;
        Stats = null;
        StatsTip = null;
        Timeline.Clear();
        _exportNotes = null;
        LastFrame = 0;
        TransportLabel = null;
        Error = error;
    }

    /// <summary>
    /// The skeleton model file (worker thread): the first of <paramref name="values"/> (<c>previewModel</c>, then
    /// <c>model</c>: APE semantics) that resolves to a file on disk. Each value may be a model_export-relative path or a
    /// bare xmodel asset name (common in stock GDTs) — the latter is chased through the xmodel asset's own
    /// <c>filename</c>.
    /// </summary>
    private string? ResolveSkeletonPath(IReadOnlyList<string> values, IGdtLookup lookup)
    {
        foreach (var value in values)
        {
            var path = ResolveFile(_env.ModelExportDir, value, ModelExtensions);
            if (path is not null)
                return path;

            var assetFile = lookup.Find(value, "xmodel")?.Fields.GetValueOrDefault("filename", "");
            if (!string.IsNullOrWhiteSpace(assetFile))
            {
                path = ResolveFile(_env.ModelExportDir, assetFile, ModelExtensions);
                if (path is not null)
                    return path;
            }
        }
        return null;
    }

    /// <summary>File probing (blocking I/O): call off the UI thread.</summary>
    private string? ResolveFile(string? primaryRoot, string raw, string[] extensions)
    {
        if (primaryRoot is null)
            return null;
        return PreviewFileResolver.ResolveRelative(_env, primaryRoot, raw, extensions)
            ?? (_env.Bo3Root is null ? null : PreviewFileResolver.ResolveRelative(_env, _env.Bo3Root, raw, extensions));
    }

    /// <summary>The record's anim file value: first filled schema FileKind==Anim property, else literal <c>filename</c>.</summary>
    private string FindAnimPath()
    {
        var schema = SchemaRegistry.Get(_record.Type);
        if (schema is not null)
        {
            foreach (var prop in schema.Properties)
            {
                if (prop.FileKind != PropertyFileKind.Anim)
                    continue;
                var value = PreviewInheritance.Value(_record, _resolve, prop.Key);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        return PreviewInheritance.Value(_record, _resolve, "filename");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        _loading.End();
        IsPlaying = false;
        _timer?.Stop();
        _frameSource = null;
        _skinner = null;
        Pose = null;
        Scene = null;
        Gfx.Clear();
        Timeline.Dispose();
    }

    // ── Playback ────────────────────────────────────────────────────────────

    private bool GfxPlayback => Pose is not null && Gfx.IsActive;

    // The views showing this anim (the docked pane, the xanim editor's viewport and the popped-out window overlap for a
    // moment while it moves between them): the clock runs while any is attached, on the newest one's display frames.
    private readonly List<TopLevel?> _views = new();

    /// <param name="frameSource">The view's top level, whose display frames drive ToolsGfx playback.</param>
    public void ViewAttached(TopLevel? frameSource)
    {
        _views.Add(frameSource);
        _viewAttached = true;
        UseFrameSource(frameSource);
    }

    /// <summary>
    /// A view let go of the anim (pane collapsed, tab closed, moved elsewhere). Playback carries on in a view still
    /// showing it; with none left, the clock stops so nothing leaks, once the view taking over (the preview moving
    /// between the editor and its own window) has had its chance to attach.
    /// </summary>
    public void ViewDetached(TopLevel? frameSource = null)
    {
        var at = _views.LastIndexOf(frameSource);
        if (at >= 0)
            _views.RemoveAt(at);
        else if (_views.Count > 0)
            _views.RemoveAt(_views.Count - 1);
        if (_views.Count > 0)
        {
            UseFrameSource(_views[^1]);
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_views.Count > 0 || _disposed)
                return;
            _viewAttached = false;
            UseFrameSource(null);
            IsPlaying = false;
            Timeline.Silence();
        }, DispatcherPriority.Background);
    }

    // Each frame request belongs to the source it was made on: one from a source given up never ticks the clock.
    private int _frameGeneration;

    private void UseFrameSource(TopLevel? source)
    {
        if (ReferenceEquals(source, _frameSource))
            return;
        _frameSource = source;
        _frameGeneration++;
        _frameRequested = false;
        if (IsPlaying && GfxPlayback)
            RequestFrame();
    }

    /// <summary>Steps one frame (keyboard , . ← →, the step buttons), pausing first when playing; wraps at either end.</summary>
    public void StepFrame(int delta)
    {
        if (LastFrame <= 0)
            return;
        IsPlaying = false;
        var next = (int)Math.Round(CurrentFrame) + delta;
        CurrentFrame = ((next % (LastFrame + 1)) + LastFrame + 1) % (LastFrame + 1);
    }

    /// <summary>Pauses on the first or last frame (Home / End).</summary>
    public void GoToFrame(int frame)
    {
        if (LastFrame <= 0)
            return;
        IsPlaying = false;
        CurrentFrame = Math.Clamp(frame, 0, LastFrame);
    }

    [RelayCommand]
    private void StepBack() => StepFrame(-1);

    [RelayCommand]
    private void StepForward() => StepFrame(1);

    /// <summary>Space: play / pause.</summary>
    public void TogglePlay()
    {
        if (HasTransport)
            IsPlaying = !IsPlaying;
    }

    // Playback paused by a scrub, to resume when it ends.
    private bool _resumeAfterScrub;

    /// <summary>A press on the frame slider (or another scrub surface): playback pauses so the clock doesn't fight the
    /// pointer, and resumes on <see cref="EndScrub"/>.</summary>
    public void BeginScrub()
    {
        _resumeAfterScrub |= IsPlaying;
        IsPlaying = false;
    }

    /// <summary>The scrub ended (release, or lost capture): playback resumes if the scrub paused it.</summary>
    public void EndScrub()
    {
        if (!_resumeAfterScrub)
            return;
        _resumeAfterScrub = false;
        IsPlaying = true;
    }

    partial void OnIsPlayingChanged(bool value)
    {
        bool canPlay = GfxPlayback ? _duration > 0f : Scene is not null && LastFrame > 0;
        _timer?.Stop();
        if (value && canPlay)
        {
            if (!Loop && LastFrame > 0 && CurrentFrame >= LastFrame - 1e-3)
            {
                // Play from the end with no loop: start over, as a player does.
                if (GfxPlayback)
                {
                    _time = 0f;
                    ApplyTime();
                }
                else
                    CurrentFrame = 0;
            }
            Timeline.PlaybackStarted(CurrentFrame);
            _clock.Restart();
            _labelClock.Restart();
            // ToolsGfx: APE's wall clock, one pose per display frame. GL: one tick per anim frame.
            if (GfxPlayback && _frameSource is not null)
            {
                RequestFrame();
                return;
            }
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render);
                _timer.Tick += OnTick;
            }
            _timer.Interval = GfxPlayback ? TimeSpan.FromMilliseconds(15) : TimeSpan.FromSeconds(1.0 / Math.Max(_fps, 1f));
            _timer.Start();
        }
        else if (value)
        {
            IsPlaying = false;
        }
        else
        {
            Timeline.Silence();
            // Pausing lands on a whole frame, so what is drawn is the frame the readout and the notetracks name. Less than
            // half a frame, and no note plays for it.
            if (GfxPlayback && CurrentFrame != Math.Round(CurrentFrame))
            {
                _snapping = true;
                try
                {
                    CurrentFrame = Math.Round(CurrentFrame);
                }
                finally
                {
                    _snapping = false;
                }
            }
            else if (GfxPlayback)
                UpdateTransportLabel(); // the readout lags while playing: show where it stopped
        }
    }

    private void RequestFrame()
    {
        if (_frameRequested || _frameSource is null)
            return;
        _frameRequested = true;
        var generation = _frameGeneration;
        _frameSource.RequestAnimationFrame(_ =>
        {
            if (generation != _frameGeneration)
                return;
            _frameRequested = false;
            if (IsPlaying)
            {
                OnTick(null, EventArgs.Empty);
                if (IsPlaying && GfxPlayback)
                    RequestFrame();
            }
        });
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Defensive: if the view detached without our handler firing, self-stop instead of leaking.
        if (!_viewAttached || (Scene is null && !GfxPlayback))
        {
            IsPlaying = false;
            return;
        }
        if (GfxPlayback)
        {
            // Anim_PreviewUpdateTick: time = fmodf(time + elapsed, duration); t = clamp(time / duration).
            float elapsed = (float)_clock.Elapsed.TotalSeconds;
            _clock.Restart();
            if (!Loop && _time + elapsed >= _duration)
            {
                // Loop off: hold the last frame and stop, as a player does.
                _time = _duration;
                ApplyTime();
                IsPlaying = false;
                return;
            }
            _time = AnimClock.Advance(_time, elapsed, _duration);
            ApplyTime();
            return;
        }
        var next = (int)Math.Round(CurrentFrame) + 1;
        if (!Loop && next > LastFrame)
        {
            IsPlaying = false;
            return;
        }
        _ticking = true;
        try
        {
            CurrentFrame = next > LastFrame ? 0 : next;
        }
        finally
        {
            _ticking = false;
        }
    }

    // True while the GL playback timer advances the frame (the ToolsGfx clock sets it through SetFrameSilently).
    private bool _ticking;

    /// <summary>Pushes <see cref="_time"/> to the pose and mirrors it on the slider (frame f = (N-1)·t); while playing the
    /// readout follows at <see cref="LabelInterval"/>.</summary>
    private void ApplyTime()
    {
        if (Pose is null)
            return;
        float t = AnimClock.NormalizedTime(_time, _duration);
        Pose.NormalizedTime = t;
        SetFrameSilently(t * LastFrame);
        if (!IsPlaying || _labelClock.Elapsed >= LabelInterval)
        {
            _labelClock.Restart();
            UpdateTransportLabel();
        }
    }

    private void SetFrameSilently(double frame)
    {
        _syncingFrame = true;
        try
        {
            CurrentFrame = frame;
        }
        finally
        {
            _syncingFrame = false;
        }
    }

    // True while a user move is being put on a whole frame, and while a pause lands on one.
    private bool _rounding, _snapping;

    partial void OnCurrentFrameChanged(double value)
    {
        // A user move (slider, lanes, keys) lands on a whole frame: a scrub drawn at 12.4 and shown as 12 says two things.
        // Only the playback clock moves between frames.
        if (!_syncingFrame && !_ticking && !_rounding && value != Math.Round(value))
        {
            _rounding = true;
            try
            {
                CurrentFrame = Math.Round(value);
            }
            finally
            {
                _rounding = false;
            }
            return;
        }
        // Only the playback clock's own advance counts as playback (a move back is then the loop wrapping); a key, click
        // or drag while playing is a user move.
        if (!_snapping)
            Timeline.FrameChanged(value, fromClock: IsPlaying && (_syncingFrame || _ticking));
        if (GfxPlayback)
        {
            if (_syncingFrame)
                return;
            // Scrub / step: the slider's frame is the sampled frame position (N-1)·t.
            float t = LastFrame > 0 ? (float)Math.Clamp(value / LastFrame, 0, 1) : 0f;
            _time = t * _duration;
            Pose!.NormalizedTime = t;
            UpdateTransportLabel();
            return;
        }
        UpdateTransportLabel();
        RequestSkin((int)Math.Round(value));
    }

    /// <summary>
    /// The frame / time readout, padded to the clip's widest value so the monospaced label keeps one width while playing.
    /// One formula, playing or paused: the slider's frame, (N-1)·t to the nearest whole frame, the numbering the steps,
    /// the notetracks and the lanes use, so pausing never makes it jump.
    /// </summary>
    private void UpdateTransportLabel()
    {
        if (GfxPlayback)
        {
            int shown = (int)Math.Round(CurrentFrame);
            string total = _clipFrames.ToString(CultureInfo.InvariantCulture);
            string duration = _duration.ToString("0.00", CultureInfo.InvariantCulture);
            TransportLabel = $"{shown.ToString(CultureInfo.InvariantCulture).PadLeft(total.Length)} / {total}  ·  "
                             + $"{_time.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(duration.Length)}s / {duration}s  ·  {_fps:0.#} fps";
            return;
        }
        string last = (LastFrame + 1).ToString(CultureInfo.InvariantCulture);
        TransportLabel = $"{((int)Math.Round(CurrentFrame)).ToString(CultureInfo.InvariantCulture).PadLeft(last.Length)} / {last}  ·  {_fps:0.#} fps";
    }

    /// <summary>Coalesced skinning: at most one thread-pool skin in flight, latest requested frame wins.</summary>
    private void RequestSkin(int frame)
    {
        if (_skinner is null || Scene is null)
            return;
        _pendingFrame = frame;
        if (_skinBusy)
            return;
        _skinBusy = true;
        _ = SkinLoopAsync(_skinner, Scene);
    }

    private async Task SkinLoopAsync(AnimSkinner skinner, ModelScene scene)
    {
        try
        {
            while (_pendingFrame is { } frame)
            {
                _pendingFrame = null;
                var data = await Task.Run(() => skinner.SkinFrame(frame));
                if (!ReferenceEquals(Scene, scene) || _skinner != skinner)
                    return; // refreshed away mid-flight
                scene.UpdateVertices(data);
            }
        }
        finally
        {
            _skinBusy = false;
        }
    }
}
