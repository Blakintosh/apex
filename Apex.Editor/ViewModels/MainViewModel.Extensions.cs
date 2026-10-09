using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Editor.Models;
using Apex.Editor.Services.Extensions;

namespace Apex.Editor.ViewModels;

/// <summary>
/// What an extension adds to the window rather than to one form: its export (the palette and the button on its first
/// header) and turning it on or off for many assets at once (the palette, over the Explorer's or the table's selection).
/// Names and words come from the manifest; with nothing installed none of it is offered.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The Explorer's selected assets, read when the palette asks (the Explorer view sets it).</summary>
    public Func<IReadOnlyList<AssetRecord>>? ExplorerSelection { get; set; }

    /// <summary>The selection a bulk command acts on: the open table's checked rows, else the Explorer's.</summary>
    private IReadOnlyList<AssetRecord> BulkSelection() =>
        Table is { } table
            ? table.Rows.Where(r => r.IsSelected).Select(r => r.Record).ToList()
            : ExplorerSelection?.Invoke() ?? Array.Empty<AssetRecord>();

    /// <summary>Copies a tab's values as the extension's export writes them, and says what went.</summary>
    public async void CopyExtensionExport(AssetEditorViewModel tab, ExtensionManifest manifest)
    {
        if (tab.ExportText(manifest.Id) is not { } text || manifest.Export is not { } export)
            return;
        var values = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1;
        var header = export.Header.Replace("{asset}", tab.Name, StringComparison.Ordinal);
        try
        {
            if (Shell is not null)
                await Shell.CopyTextAsync(text);
        }
        catch (Exception)
        {
            // The shell's copy never throws; this handler is async void, so nothing may escape it regardless.
        }
        Status = values == 0
            ? $"Copied {header}: {tab.Name} has no {manifest.DisplayTitle} values to copy yet."
            : $"Copied {header}: {values} value{(values == 1 ? "" : "s")}, its own and the ones it inherits.";
    }

    /// <summary>The palette's extension commands: the active tab's exports, then turning extensions on or off for the selection.</summary>
    private IEnumerable<PaletteItemViewModel> ExtensionPaletteItems(string query)
    {
        if (ExtensionRegistry.Manifests.Count == 0)
            yield break;
        if (ActiveTab is { } tab)
            foreach (var manifest in tab.Exports)
                if (TextScore(manifest.Export!.Command, query) is not null)
                    yield return new PaletteItemViewModel(manifest.Export.Command, "", "⧉", () => CopyExtensionExport(tab, manifest))
                    {
                        Detail = $"{tab.Name}, its own and inherited values",
                    };

        // The selection is read only when the words could match: a palette keystroke never walks it otherwise.
        IReadOnlyList<AssetRecord>? selection = null;
        foreach (var manifest in ExtensionRegistry.Manifests)
        {
            if (manifest.EnabledBy is null)
                continue;
            foreach (var on in new[] { true, false })
            {
                var verb = on ? "Turn on" : "Turn off";
                if (TextScore($"{verb} {manifest.DisplayTitle} for the selected assets", query) is null)
                    continue;
                selection ??= BulkSelection();
                if (selection.Count == 0)
                    yield break;
                // Counted only for a selection a keystroke can afford to walk; a larger one is named by its size.
                var count = selection.Count <= CountedSelection ? Switching(selection, manifest, on, GdtLookup()).Count : -1;
                if (count == 0)
                    continue;
                var captured = selection;
                var title = count > 0
                    ? $"{verb} {manifest.DisplayTitle} for {count:N0} asset{(count == 1 ? "" : "s")}"
                    : $"{verb} {manifest.DisplayTitle} for the {selection.Count:N0} selected assets";
                yield return new PaletteItemViewModel(title, "", on ? "●" : "○", () => SetExtensionOn(captured, manifest, on))
                {
                    Detail = Table is not null ? "the table's selected rows" : "the Explorer's selection",
                };
            }
        }
    }

    /// <summary>The most selected assets the palette counts as it is typed into (each keystroke walks them).</summary>
    private const int CountedSelection = 2000;

    /// <summary>Each GDT by name, for a pass over many assets (<see cref="GdtOf"/> searches the list).</summary>
    private Func<AssetRecord, GdtFile?> GdtLookup()
    {
        var byName = new Dictionary<string, GdtFile>(StringComparer.Ordinal);
        foreach (var g in _db.Gdts)
            byName.TryAdd(g.Name, g);
        return r => byName.GetValueOrDefault(r.GdtName);
    }

    /// <summary>
    /// The assets of <paramref name="selection"/> whose switch for <paramref name="manifest"/> would change, parents before
    /// the assets based on them (so a child that only follows its parent isn't written once its parent is).
    /// </summary>
    private List<(AssetRecord Asset, ExtensionSchema Schema)> Switching(IReadOnlyList<AssetRecord> selection, ExtensionManifest manifest, bool on,
        Func<AssetRecord, GdtFile?> gdtOf)
    {
        var candidates = new List<(AssetRecord, ExtensionSchema, int)>();
        foreach (var asset in selection)
            if (ExtensionRegistry.For(asset.Type).FirstOrDefault(s => s.Manifest == manifest) is { EnabledBy: { } key } schema
                && VisibleWhen.Truthy(SwitchValue(asset, schema, key, gdtOf).Effective) != on)
                candidates.Add((asset, schema, Depth(asset)));
        return candidates.OrderBy(c => c.Item3).Select(c => (c.Item1, c.Item2)).ToList();
    }

    private int Depth(AssetRecord asset)
    {
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { asset.Name };
        for (var cur = asset; cur.Parent is { } p && seen.Add(p) && FindAsset(asset.Type, p) is { } parent; cur = parent)
            depth++;
        return depth;
    }

    /// <summary>A switch's value on an asset: its own (null: none), what it reads without it (inherited, else the default), and the one in effect.</summary>
    private (string? Own, string Unowned, string Effective) SwitchValue(AssetRecord asset, ExtensionSchema schema, string key,
        Func<AssetRecord, GdtFile?> gdtOf)
    {
        var own = gdtOf(asset)?.Extensions?.Get(asset.Name, schema.Id, key);
        string? inherited = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { asset.Name };
        for (var cur = asset; inherited is null && cur.Parent is { } p && seen.Add(p) && FindAsset(asset.Type, p) is { } parent; cur = parent)
            inherited = gdtOf(parent)?.Extensions?.Get(parent.Name, schema.Id, key);
        var unowned = inherited ?? schema.Fields.FirstOrDefault(f => f.Def.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Def.Default ?? "";
        return (own, unowned, own ?? unowned);
    }

    /// <summary>
    /// Turns an extension on or off for every asset of <paramref name="selection"/> it applies to, as one undo step. An
    /// asset already so (its own value, or what it inherits) is left alone; one that would read the wanted value from its
    /// parent or the default inherits it rather than holding a copy.
    /// </summary>
    public void SetExtensionOn(IReadOnlyList<AssetRecord> selection, ExtensionManifest manifest, bool on)
    {
        var token = new object();
        var edited = new List<AssetRecord>();
        var target = on ? "1" : "0";
        var gdtOf = GdtLookup();
        var switching = Switching(selection, manifest, on, gdtOf);
        foreach (var (asset, schema) in switching)
        {
            var key = schema.EnabledBy!;
            var (own, unowned, effective) = SwitchValue(asset, schema, key, gdtOf);
            if (VisibleWhen.Truthy(effective) == on || gdtOf(asset) is not { } gdt)
                continue;
            var write = VisibleWhen.Truthy(unowned) == on && own is not null ? null : target;
            ExtensionSidecar.Of(gdt).Set(asset.Name, schema.Id, key, write);
            var change = new PropertyChange(key, own, write) { Extension = new ExtensionTarget(GdtOf, schema.Id) };
            asset.History.RecordStep(new EditStep(new[] { change }, token));
            edited.Add(asset);
        }
        var verb = on ? "on" : "off";
        if (edited.Count == 0)
        {
            Status = $"{manifest.DisplayTitle} is already {verb} for every selected asset it applies to.";
            return;
        }
        // An asset that follows a parent switched with it changes without a write of its own: the count is what changed.
        var text = $"Turned {verb} {manifest.DisplayTitle} for {switching.Count:N0} asset{(switching.Count == 1 ? "" : "s")}";
        if (Table is { } table)
            table.RecordExternal(token, edited, text);
        else
            PushPlacement(new PlacementStep
            {
                DoneText = $"Redid: {text}",
                UndoneText = $"Undid: {text}",
                Undo = () => StepBatch(edited, token, undo: true),
                Redo = () => StepBatch(edited, token, undo: false),
            });
        OnRecordsEditedExternally(edited);
        OnNewValueEdit();
        var followers = switching.Count - edited.Count;
        Status = $"{text}{(followers > 0 ? $" ({followers:N0} by following {(followers == 1 ? "its parent" : "their parents")})" : "")}. "
            + $"{Commands.CommandCatalog.Get(Commands.CommandCatalog.Undo).GestureText} undoes it.";
    }

    /// <summary>Undoes or redoes one batch's step on each record whose newest step it still is (an edit made since wins).</summary>
    private void StepBatch(IReadOnlyList<AssetRecord> records, object token, bool undo)
    {
        var done = new List<AssetRecord>();
        foreach (var record in records)
        {
            var next = undo ? record.History.NextUndo : record.History.NextRedo;
            if (next?.Batch != token)
                continue;
            if (undo)
                record.History.Undo();
            else
                record.History.Redo();
            done.Add(record);
        }
        OnRecordsEditedExternally(done);
    }
}
