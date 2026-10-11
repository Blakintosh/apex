using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Session;

namespace Apex.Editor.ViewModels;

/// <summary>
/// Change weapon class: a weapon's class (bulletweapon, projectileweapon…) is the deffile its root asset's GDT header
/// names, and its derived assets take it from there, so the root and every asset derived from it change together. The
/// values move as <see cref="WeaponClassRemap"/> plans them. Like Underive it is one undo step, journaled as <c>cls</c>,
/// and reaches the GDT through Save, which rewrites the root's <c>( "type.gdf" )</c>.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClassOpen), nameof(IsModalOpen))]
    private WeaponClassDialogViewModel? _classDialog;

    public bool IsClassOpen => ClassDialog is not null;

    /// <summary>Whether Change weapon class applies to <paramref name="rec"/>: a weapon whose class Apex knows.</summary>
    public static bool CanChangeWeaponClass(AssetRecord? rec) =>
        rec is not null && Services.Extensions.ExtensionManifest.IsWeapon(rec.Type)
        && !rec.Type.Equals(GdtLoader.UnknownType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the dialog for <paramref name="rec"/>'s family (its root and everything derived from it).</summary>
    public void OpenChangeWeaponClass(AssetRecord rec)
    {
        if (!IsLive(rec) || PlacementBlocked())
            return;
        if (!CanChangeWeaponClass(rec))
        {
            Alert(rec.Type.Equals(GdtLoader.UnknownType, StringComparison.OrdinalIgnoreCase)
                ? $"Can't change {rec.Name}'s class: Apex doesn't know it, because its parent isn't in any loaded GDT."
                : $"{rec.Name} is a {rec.Type}, not a weapon: only weapons have a class to change.");
            return;
        }
        if (RootOf(rec) is not { } root)
        {
            Alert($"Can't change {rec.Name}'s class: it comes from {rec.Parent}'s chain, and part of that chain isn't in any loaded GDT.");
            return;
        }
        var targets = WeaponClassRemap.Targets(root.Type);
        if (targets.Count == 0)
        {
            Alert($"There's no other weapon class to change {root.Name} to: the deffiles Apex loaded have no weapon type besides {root.Type}.");
            return;
        }
        ClassDialog = new WeaponClassDialogViewModel(rec, root, FamilyOf(root), targets,
            accept: d =>
            {
                ClassDialog = null;
                ChangeWeaponClass(d.Root, d.Target, d.Picks(), d.KeepOrphans);
            },
            cancel: () => ClassDialog = null);
    }

    /// <summary>
    /// Changes <paramref name="root"/>'s family to <paramref name="to"/>, its values as <see cref="WeaponClassRemap.Apply"/>
    /// makes them. One undo step. False when nothing changed (the dialog's reasons, or a save running).
    /// </summary>
    public bool ChangeWeaponClass(AssetRecord root, string to, IReadOnlyDictionary<RemapChoice, string>? picks = null, bool keepOrphans = false)
    {
        if (!IsLive(root) || root.Parent is not null || PlacementBlocked())
            return false;
        if (SchemaRegistry.Get(root.Type) is not { } fromSchema || SchemaRegistry.Get(to) is not { } toSchema
            || root.Type.Equals(to, StringComparison.OrdinalIgnoreCase))
            return false;
        var from = root.Type;
        var family = FamilyOf(root);
        var plan = WeaponClassRemap.Plan(family, fromSchema, toSchema);
        var values = WeaponClassRemap.Apply(plan, toSchema, picks, keepOrphans);

        var op = RetypeOp(root, family, to, values, keepOrphans, entry: null);
        JournalOp(op);
        MarkSessionEdited();
        AfterRetype(family);
        var others = family.Count - 1;
        var done = others == 0
            ? $"Changed {root.Name} to {to}. Ctrl+Z takes it back."
            : $"Changed {root.Name} and {others} derived asset{(others == 1 ? "" : "s")} to {to}. Ctrl+Z takes it back.";
        Status = done;
        PushPlacement(new PlacementStep
        {
            DoneText = done,
            UndoneText = $"{root.Name} is a {from} again",
            Gone = () => !IsLive(root) ? $"Nothing to take back: {root.Name} was deleted after its class changed."
                : !root.Type.Equals(to, StringComparison.OrdinalIgnoreCase) || !_sessionOps.Contains(op) ? $"Nothing to take back: {root.Name} isn't a {to}." : null,
            Undo = () =>
            {
                if (!IsLive(root) || !_sessionOps.Contains(op))
                    return;
                UndoRetypeOp(op, restoreValues: true);
                ForgetOps(new[] { op });
                AfterRetype(op.Family!);
            },
            Redo = () =>
            {
                // What the family holds now: values may have been edited since the undo.
                if (!IsLive(root) || root.Parent is not null || !root.Type.Equals(from, StringComparison.OrdinalIgnoreCase))
                    return;
                var now = FamilyOf(root);
                var again = WeaponClassRemap.Apply(WeaponClassRemap.Plan(now, fromSchema, toSchema), toSchema, picks, keepOrphans);
                op = RetypeOp(root, now, to, again, keepOrphans, entry: null);
                JournalOp(op);
                MarkSessionEdited();
                AfterRetype(now);
            },
        });
        return true;
    }

    /// <summary>The root of <paramref name="rec"/>'s derivation chain (itself when it has no parent); null when the chain breaks.</summary>
    private AssetRecord? RootOf(AssetRecord rec)
    {
        var seen = new HashSet<AssetRecord> { rec };
        var cur = rec;
        while (cur.Parent is { } p)
        {
            if (FindAsset(rec.Type, p) is not { } parent || !seen.Add(parent))
                return null;
            cur = parent;
        }
        return cur;
    }

    /// <summary>
    /// A replayed class change's values before its sets apply: the keys <paramref name="to"/> has no field for dropped
    /// (unless they were kept), the rest as the asset holds them. The sets bring back the picks.
    /// </summary>
    private static Dictionary<AssetRecord, Dictionary<string, string>>? ReplayedValues(IReadOnlyList<AssetRecord> family, string to, bool keep)
    {
        if (keep || SchemaRegistry.Get(to) is not { } schema)
            return null;
        var values = new Dictionary<AssetRecord, Dictionary<string, string>>();
        foreach (var rec in family)
            if (rec.Properties.Keys.Any(k => schema.Find(k) is null))
                values[rec] = rec.Properties.Where(kv => schema.Find(kv.Key) is not null)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return values;
    }

    /// <summary><paramref name="root"/> and every asset derived from it, at any depth, root first.</summary>
    private List<AssetRecord> FamilyOf(AssetRecord root)
    {
        var family = new List<AssetRecord> { root };
        var seen = new HashSet<AssetRecord> { root };
        for (var i = 0; i < family.Count; i++)
            if (_byParent.TryGetValue(family[i].Name, out var kids))
                foreach (var kid in kids.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
                    if (seen.Add(kid))
                        family.Add(kid);
        return family;
    }

    /// <summary>
    /// Gives the family <paramref name="to"/> and the values <paramref name="values"/> holds for those it changes (also
    /// used when a journal replays a class change, with no values: the replayed sets carry them).
    /// </summary>
    private SessionOp RetypeOp(AssetRecord root, IReadOnlyList<AssetRecord> family, string to,
        IReadOnlyDictionary<AssetRecord, Dictionary<string, string>>? values, bool keepOrphans, JournalEntry? entry)
    {
        var oldType = root.Type;
        var oldValues = new Dictionary<AssetRecord, Dictionary<string, string>>();
        foreach (var rec in family)
        {
            if (values?.GetValueOrDefault(rec) is { } now)
            {
                rec.CaptureBaseline();
                oldValues[rec] = new Dictionary<string, string>(rec.Properties, StringComparer.OrdinalIgnoreCase);
                var props = rec.Properties;
                props.Clear();
                foreach (var (key, value) in now)
                    props[key] = value;
                rec.ValuesTouched = true;
                MarkMaterialized(rec);
            }
            rec.Type = to;
        }
        InvalidateRefCatalog();
        return new SessionOp
        {
            Entry = entry ?? new JournalEntry
            {
                Op = "cls", Gdt = root.GdtName, Name = root.Name, Type = to, From = oldType, Keep = keepOrphans ? true : null,
            },
            Record = root,
            OldType = oldType,
            Family = family.ToList(),
            OldValues = null,
            FamilyValues = oldValues,
        };
    }

    /// <summary>Puts the family's class back; with <paramref name="restoreValues"/>, the values it had before too (Ctrl+Z).</summary>
    private void UndoRetypeOp(SessionOp op, bool restoreValues)
    {
        if (op.OldType is not { } oldType || op.Family is not { } family)
            return;
        foreach (var rec in family)
        {
            rec.Type = oldType;
            if (restoreValues && op.FamilyValues?.GetValueOrDefault(rec) is { } old)
            {
                var props = rec.Properties;
                props.Clear();
                foreach (var (key, value) in old)
                    props[key] = value;
            }
        }
        InvalidateRefCatalog();
    }

    private void AfterRetype(IReadOnlyList<AssetRecord> family)
    {
        foreach (var rec in family)
        {
            _leafCache[rec] = BrowserNode.ForAsset(rec, ToggleNode);
            UpdateProblemEntry(rec);
        }
        RecountSession(family);
        SumProblems();
        MarkRecordsChangedInTree(family.ToList());
        foreach (var rec in family)
            RebuildTab(rec);
        foreach (var gdt in GdtsOf(family))
            InvalidateSnapshot(gdt);
        RefreshPlacement(GdtsOf(family), family.Select(f => f.Name));
        NotifyUndoRedo();
    }
}
