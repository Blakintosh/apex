using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

public partial class Program
{
    /// <summary>
    /// Line lists on the real install (read-only: every edit is undone, nothing is saved): the real deffiles map their
    /// Text entries, every list value in the corpus survives the editor untouched, and the interactions are timed.
    /// </summary>
    private static void LiveLineFields(Window window, MainViewModel vm, AssetDatabase db, List<PerfResult> results)
    {
        var bw = SchemaRegistry.Get("bulletweapon");
        var xm = SchemaRegistry.Get("xmodel");
        Check("lists (live): hideTags, materials and skinOverride are line lists; hideTags offers gunModel's bones",
            bw?.Find("hideTags") is { TextEditor: PropertyTextEditor.Lines, ModelKeys: ["gunModel", ..] }
            && xm?.Find("materials") is { TextEditor: PropertyTextEditor.Lines }
            && xm?.Find("skinOverride") is { TextEditor: PropertyTextEditor.Lines });

        // Every list value of the corpus, recomposed from its items in its own shape, comes back byte for byte (unless
        // it has blank lines between items, which an edit drops: counted; a value is never rewritten on open).
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int values = 0, identical = 0, innerBlank = 0;
        var mismatch = new List<string>();
        foreach (var a in db.Assets)
        {
            if (!(a.Type.Equals("xmodel", StringComparison.OrdinalIgnoreCase) || a.Type.EndsWith("weapon", StringComparison.OrdinalIgnoreCase))
                || SchemaRegistry.Get(a.Type) is not { } schema)
                continue;
            foreach (var key in new[] { "skinOverride", "materials", "hideTags" })
            {
                if (schema.Find(key) is not { TextEditor: PropertyTextEditor.Lines } def
                    || a.ScanProperties.GetValueOrDefault(key) is not { Length: > 0 } value)
                    continue;
                values++;
                var composed = LineList.Compose(LineList.Items(value), value, LineList.ShapeOf(def));
                if (composed == value)
                    identical++;
                else if (value.TrimEnd('\\', 'r', 'n').Contains(@"\r\n\r\n", StringComparison.Ordinal)
                         || value.StartsWith(@"\r\n", StringComparison.Ordinal))
                    innerBlank++;
                else if (mismatch.Count < 3)
                    mismatch.Add($"{a.Name}.{key}");
            }
        }
        Check($"lists (live): {identical:N0} of {values:N0} list values recompose byte for byte; {innerBlank} have blank lines before or between items "
              + $"(dropped only when edited){string.Concat(mismatch.Select(m => "; differs: " + m))} — {clock.ElapsedMilliseconds:N0} ms",
            values > 1000 && identical + innerBlank == values);

        // ── An xmodel overriding a surface with global_invisible: every surface of its model listed, nothing flagged ──
        Check("lists (live): global_invisible ships with the game, so it is no missing material",
            ShippedAssets.Contains("material", "global_invisible") && !ShippedAssets.Contains("material", "no_such_material_apex"));
        static bool Readable(AssetRecord a) => a.ScanProperties.GetValueOrDefault("filename", "") is { Length: > 0 } f
            && File.Exists(Path.Combine(FieldFiles.Root!, "model_export", Path.ChangeExtension(f.Replace(@"\\", @"\"), ".xmodel_bin")));
        var xmodels = db.Assets.Where(a => a.Type.Equals("xmodel", StringComparison.OrdinalIgnoreCase)
            && LineList.Items(a.ScanProperties.GetValueOrDefault("skinOverride", "")).Count >= 1).ToList();
        var model = xmodels.FirstOrDefault(a => a.ScanProperties["skinOverride"].Contains("global_invisible", StringComparison.OrdinalIgnoreCase)
                        && Readable(a))
                    ?? xmodels.FirstOrDefault(Readable);
        Check($"lists (live): an xmodel with a skinOverride list and a readable model ({model?.Name})", model is not null);
        if (model is null)
            return;
        var original = model.ScanProperties["skinOverride"];
        var opened = System.Diagnostics.Stopwatch.StartNew();
        vm.OpenAsset(model);
        Settle(window);
        var openMs = opened.Elapsed.TotalMilliseconds;
        var tab = vm.ActiveTab!;
        var skin = tab.AllSentinel.All.OfType<LinesPropertyViewModel>().First(p => p.Key == "skinOverride");
        WaitFor(() => skin.Items.Any(i => i.IsModelSurface));
        Settle(window);
        var modelSurfaces = FieldFiles.SurfaceMaterialsAsync(model.ScanProperties["filename"]).GetAwaiter().GetResult() ?? Array.Empty<string>();
        var listed = skin.Items.Where(i => i.IsModelSurface).Select(i => i.Surface).ToList();
        Check($"lists (live): {model.Name} lists every surface of its model ({listed.Count}: {string.Join(", ", listed.Take(3))}…), "
              + $"{skin.Items.Count(i => i.IsOverridden)} overridden; no Add field (opened in {openMs:0} ms)",
            listed.Count > 0 && modelSurfaces.All(m => listed.Contains(m, StringComparer.OrdinalIgnoreCase)) && !skin.CanAdd
            && !AddRowOf(FieldView(window, tab, skin).GetVisualDescendants().OfType<LineListEditor>().First()).IsEffectivelyVisible);
        Check($"lists (live): opened untouched and unflagged ({skin.Problem ?? "no ⚠"}; '{original}')",
            skin.RawValue == original && !tab.CanUndo && (skin.Problem is null || !original.Contains("global_invisible", StringComparison.OrdinalIgnoreCase)));
        // APEX_SHOTS_OUT: the list as it looks, in each theme.
        if (Environment.GetEnvironmentVariable("APEX_SHOTS_OUT") is { Length: > 0 } shotsOut)
        {
            FieldView(window, tab, skin).BringIntoView();
            foreach (var theme in new[] { ThemeChoice.Graphite, ThemeChoice.Slate, ThemeChoice.Light })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = AppTheme.VariantOf(theme);
                Settle(window);
                Capture(window, Path.Combine(shotsOut, $"53-skin-override-{theme.ToString().ToLowerInvariant()}.png"));
            }
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            Settle(window);
        }
        var list = FieldView(window, tab, skin).GetVisualDescendants().OfType<LineListEditor>().First();
        var setAt =skin.Items.ToList().FindIndex(i => i.IsModelSurface && i.IsOverridden);
        var row = ItemRow(list, Math.Max(0, setAt));
        Check($"lists (live): a surface of the model is its name, with one field for its replacement ({LineBoxes(row).Count} field)",
            LineBoxes(row).Count == 1 && row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("lsurface") && t.IsEffectivelyVisible));
        if (setAt >= 0)
        {
            var replacement = LineBoxes(ItemRow(list, setAt))[0];
            var before = skin.Items[setAt].Replacement!.RawValue;
            Click(window, replacement);
            results.Add(Time(window, "Line list: keystroke in an item", PerfBudgets.Frame, 3, 25,
                i => TypeChar(window, (char)('a' + i % 26)),
                before: i =>
                {
                    if (i % 10 == 0)
                        replacement.Text = before;
                }));
            results.Add(Time(window, "Line list: Enter commits an item (one edit)", PerfBudgets.Frame, 3, 15,
                _ => KeyStroke(window, K.Enter),
                before: i =>
                {
                    list = FieldView(window, tab, skin).GetVisualDescendants().OfType<LineListEditor>().First();
                    replacement = LineBoxes(ItemRow(list, setAt))[0];
                    replacement.Focus();
                    replacement.Text = before + "_" + i;
                    Settle(window);
                },
                after: _ => Settle(window)));
            Check($"lists (live): each Enter wrote its item ('{skin.Items[setAt].Replacement!.RawValue}')",
                skin.Items[setAt].Replacement!.RawValue == before + "_17" && skin.RawValue != original);
            while (tab.CanUndo)
                tab.UndoCommand.Execute(null);
            Settle(window);
            Check("lists (live): undone back to the original byte for byte", skin.RawValue == original && model.CountSessionChanges() == 0);
        }

        // ── A surface not overridden yet: its replacement suggests materials, and Enter adds its line after the others ──
        list = FieldView(window, tab, skin).GetVisualDescendants().OfType<LineListEditor>().First();
        var freeAt = skin.Items.ToList().FindIndex(i => i.IsModelSurface && !i.IsOverridden);
        if (freeAt < 0)
        {
            Console.WriteLine($"info  lists (live): every surface of {model.Name} is overridden — adding one not driven");
        }
        else
        {
            var free = skin.Items[freeAt];
            var addRepl = (RefField)LineBoxes(ItemRow(list, freeAt))[0];
            Click(window, addRepl);
            results.Add(Time(window, "Line list: keystroke in a material field (suggestions)", PerfBudgets.Frame, 3, 25,
                i => TypeChar(window, "mtl_wpn_"[i % 8]),
                before: i =>
                {
                    if (i % 8 == 0)
                        addRepl.Text = "";
                }));
            WaitFor(() => addRepl.IsSuggesting);
            Check($"lists (live): a skinOverride replacement suggests materials ({addRepl.Suggestions?.Items.Count} for '{addRepl.Text}')",
                addRepl.IsSuggesting && addRepl.Suggestions!.Items.Count > 0 && addRepl.Suggestions.Items.All(a => a.Type == "material"));
            Key(window, K.Escape);
            Key(window, K.Escape);
            Check("lists (live): typing alone wrote nothing", skin.RawValue == original && !tab.CanUndo);

            addRepl.Text = "global_invisible";
            Key(window, K.Enter);
            Settle(window);
            var added = LineList.Items(skin.RawValue);
            Check($"lists (live): Enter on {free.Surface}'s replacement adds its line last, unflagged ('{skin.RawValue}')",
                added.Count == LineList.Items(original).Count + 1
                && added[^1] == $"{free.Surface} global_invisible" && free.IsOverridden && !free.Replacement!.HasProblem
                && skin.Items.Count(i => i.IsModelSurface) == listed.Count);
            list = FieldView(window, tab, skin).GetVisualDescendants().OfType<LineListEditor>().First();
            var remove = ItemRow(list, freeAt).GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lremove"));
            Click(window, remove);
            Settle(window);
            Check($"lists (live): ✕ on it clears the override and keeps the surface listed ('{skin.RawValue}')",
                skin.RawValue == original && !free.IsOverridden && skin.Items.Contains(free) && free.Replacement!.RawValue.Length == 0);
            while (tab.CanUndo)
                tab.UndoCommand.Execute(null);
            Settle(window);
            Check("lists (live): undone back to the original byte for byte", skin.RawValue == original && model.CountSessionChanges() == 0);
        }

        // ── A model with more surfaces than the list shows at once: it scrolls, and ✕ stays clear of its scrollbar ──
        var many = xmodels.Where(Readable).Take(400)
            .FirstOrDefault(a => FieldFiles.SurfaceMaterialsAsync(a.ScanProperties["filename"]).GetAwaiter().GetResult() is { Length: > 9 });
        if (many is null)
        {
            Console.WriteLine("info  lists (live): no xmodel with more than 9 surfaces — the scrolled list not driven");
        }
        else
        {
            var manyOriginal = many.ScanProperties["skinOverride"];
            vm.OpenAsset(many);
            Settle(window);
            var mtab = vm.ActiveTab!;
            var mskin = mtab.AllSentinel.All.OfType<LinesPropertyViewModel>().First(p => p.Key == "skinOverride");
            WaitFor(() => mskin.Items.Any(i => i.IsModelSurface));
            Settle(window);
            var mlist = FieldView(window, mtab, mskin).GetVisualDescendants().OfType<LineListEditor>().First();
            FieldView(window, mtab, mskin).BringIntoView();
            Settle(window);
            var scroll = mlist.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ItemsScroll");
            var mfreeAt = mskin.Items.ToList().FindIndex(i => i.IsModelSurface && !i.IsOverridden);
            Check($"lists (live): {many.Name}'s {mskin.Items.Count} items scroll ({scroll.Viewport.Height:0} of {scroll.Extent.Height:0} px), "
                  + $"the items keeping {mlist.FindControl<ItemsControl>("ItemsList")!.Margin.Right:0} px clear of the scrollbar",
                scroll.Extent.Height > scroll.Viewport.Height && mlist.FindControl<ItemsControl>("ItemsList")!.Margin.Right >= 12);
            if (mfreeAt >= 0)
            {
                var mfree = mskin.Items[mfreeAt];
                var mbox = LineBoxes(ItemRow(mlist, mfreeAt))[0];
                Click(window, mbox);
                mbox.Text = "global_invisible";
                Key(window, K.Enter);
                Settle(window);
                ItemRow(mlist, mfreeAt).BringIntoView();
                Settle(window);
                Click(window, ItemRow(mlist, mfreeAt).GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lremove")));
                Settle(window);
                Check($"lists (live): in the scrolled list, ✕ clears {mfree.Surface}'s override ('{mskin.RawValue}')",
                    mskin.RawValue == manyOriginal && !mfree.IsOverridden);
            }
            while (mtab.CanUndo)
                mtab.UndoCommand.Execute(null);
            Settle(window);
            Check("lists (live): the scrolled list's edits undone byte for byte", mskin.RawValue == manyOriginal && many.CountSessionChanges() == 0);
        }

        // ── hideTags on a weapon whose gun model is readable: its items offer the bones ──
        var weapon = db.Assets.Where(a => a.Type.Equals("bulletweapon", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(a => LineList.Items(a.ScanProperties.GetValueOrDefault("hideTags", "")).Count >= 1
                && a.ScanProperties.GetValueOrDefault("gunModel", "") is { Length: > 0 } gun
                && FieldFiles.Resolve?.Invoke("xmodel", gun) is { } x
                && x.ScanProperties.GetValueOrDefault("filename", "") is { Length: > 0 } f
                && File.Exists(Path.Combine(FieldFiles.Root!, "model_export", f.Replace(@"\\", @"\").Replace(".xmodel_export", ".xmodel_bin"))));
        Check($"lists (live): a weapon with hideTags and a readable gun model ({weapon?.Name})", weapon is not null);
        if (weapon is null)
            return;
        vm.OpenAsset(weapon);
        Settle(window);
        var wtab = vm.ActiveTab!;
        var tags = wtab.AllSentinel.All.OfType<LinesPropertyViewModel>().First(p => p.Key == "hideTags");
        if (tags.IsRuleHidden)
        {
            Console.WriteLine($"info  lists (live): hideTags is hidden by {weapon.Name}'s rules — bone suggestions not driven");
            return;
        }
        var tagList = FieldView(window, wtab, tags).GetVisualDescendants().OfType<LineListEditor>().First();
        var suggest = AddRowOf(tagList).GetVisualDescendants().OfType<SuggestBox>().First();
        Click(window, suggest.Box);
        Key(window, K.Down, RawInputModifiers.Alt);
        WaitFor(() => suggest.IsOpen);
        var bones = (suggest.List.ItemsSource as IEnumerable<string>)?.ToList() ?? new();
        Check($"lists (live): Alt+↓ in a hideTags item lists {((BonePropertyViewModel)tags.AddItem.Main).Model}'s bones ({bones.Count}: {string.Join(", ", bones.Take(4))}…)",
            suggest.IsOpen && bones.Count > 0);
        Key(window, K.Escape);
        Check("lists (live): nothing written", tags.RawValue == weapon.ScanProperties["hideTags"] && !wtab.CanUndo);
    }
}
