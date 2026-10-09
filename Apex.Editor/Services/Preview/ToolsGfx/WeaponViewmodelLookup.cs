using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apex.Render.Data.Gdt;

namespace Apex.Editor.Services.Preview.ToolsGfx;

/// <summary>A model attached to the view gun (weapon <c>attachViewModelN</c> at <c>attachViewModelTagN</c>).</summary>
public sealed record ViewmodelAttachment(string Model, string Tag);

/// <summary>What a weapon puts on screen with its viewmodel anims: the view gun (<c>gunModel</c>) at <c>viewmodelTag</c>
/// and the models attached to it.</summary>
/// <param name="Weapon">Weapon asset name.</param>
/// <param name="GunModel">xmodel of the view gun.</param>
/// <param name="GunTag">Hands tag the gun hangs from when its root is not shared with the anim skeleton
/// (<c>viewmodelTag</c>; blank = the game's default, <c>tag_weapon</c>).</param>
/// <param name="Attachments">Attached view models, one per tag.</param>
/// <param name="Via">How the weapon was found (status line).</param>
/// <param name="Hands">The weapon's viewhands (<c>handModel</c>); null when it names none.</param>
public sealed record WeaponViewmodel(string Weapon, string GunModel, string GunTag, IReadOnlyList<ViewmodelAttachment> Attachments, string Via,
    string? Hands = null);

/// <summary>The viewhands most anims on one skeleton preview on (their <c>previewModel</c>), and how many do.</summary>
public sealed record SiblingHands(string Model, int Anims);

/// <summary>
/// Finds the weapon that plays an xanim as a viewmodel anim, to show its view gun with the anim. Weapons name their
/// viewmodel anims (<c>idleAnim</c>, <c>reloadAnim</c>, …); an anim no weapon names (an inspect, a variant) is matched
/// through its siblings — the anims exported against the same skeleton file. Built once per session from the GDTs under
/// <c>source_data</c> (only files holding weapon or xanim entries are parsed), so edits made since are not seen.
/// </summary>
public static class WeaponViewmodelLookup
{
    private static readonly object Gate = new();
    private static Task<Index>? _index;
    private static string? _root;

    private sealed class Index
    {
        public Dictionary<string, WeaponViewmodel> ByAnim { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> AnimsBySkeleton { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per skeleton, the previewModel its anims name and how many name each.</summary>
        public Dictionary<string, Dictionary<string, int>> PreviewModelsBySkeleton { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="sourceData">The install's <c>source_data</c> folder.</param>
    /// <param name="animName">The xanim asset.</param>
    /// <param name="skeletonFile">Its GDT <c>model</c> value (the xmodel_bin it was exported against), if any.</param>
    public static WeaponViewmodel? Find(string sourceData, string animName, string? skeletonFile, CancellationToken ct)
    {
        var index = Get(sourceData, ct);
        if (index.ByAnim.TryGetValue(animName, out var direct))
            return direct with { Via = $"{direct.Weapon} uses {animName}" };
        if (string.IsNullOrWhiteSpace(skeletonFile) || !index.AnimsBySkeleton.TryGetValue(SkeletonKey(skeletonFile), out var siblings))
            return null;
        foreach (var sibling in siblings)
            if (index.ByAnim.TryGetValue(sibling, out var w))
                return w with { Via = $"{w.Weapon} uses {sibling} (same skeleton)" };
        return null;
    }

    /// <summary>
    /// The viewhands the anims exported against <paramref name="skeletonFile"/> preview on most often (their
    /// <c>previewModel</c>), or null when none names one. Ties go to the name that sorts first, so the answer is stable.
    /// </summary>
    public static SiblingHands? CommonHands(string sourceData, string? skeletonFile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(skeletonFile))
            return null;
        var index = Get(sourceData, ct);
        if (!index.PreviewModelsBySkeleton.TryGetValue(SkeletonKey(skeletonFile), out var counts) || counts.Count == 0)
            return null;
        var best = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase).First();
        return new SiblingHands(best.Key, best.Value);
    }

    private static Index Get(string sourceData, CancellationToken ct)
    {
        Task<Index> task;
        lock (Gate)
        {
            if (_index is null || !string.Equals(_root, sourceData, StringComparison.OrdinalIgnoreCase))
            {
                _root = sourceData;
                _index = Task.Run(() => Build(sourceData));
            }
            task = _index;
        }
        task.Wait(ct);
        return task.Result;
    }

    private static string SkeletonKey(string modelValue) => Path.GetFileName(modelValue.Trim().Replace('\\', '/'));

    private static Index Build(string sourceData)
    {
        var index = new Index();
        if (!Directory.Exists(sourceData))
            return index;
        var weaponGdf = Encoding.ASCII.GetBytes("weapon.gdf\"");
        var xanimGdf = Encoding.ASCII.GetBytes("\"xanim.gdf\"");
        foreach (var file in Directory.EnumerateFiles(sourceData, "*.gdt", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            bool weapons = bytes.AsSpan().IndexOf(weaponGdf) >= 0, anims = bytes.AsSpan().IndexOf(xanimGdf) >= 0;
            if (!weapons && !anims)
                continue;
            List<GdtEntry> entries;
            try { entries = GdtFile.Parse(GdtEncoding.GetString(bytes), file); }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or IndexOutOfRangeException) { continue; }
            foreach (var e in entries)
            {
                if (e.Gdf is null)
                    continue;
                if (e.Gdf.Equals("xanim.gdf", StringComparison.OrdinalIgnoreCase))
                {
                    var model = e.Fields.GetValueOrDefault("model", "");
                    if (model.Trim().Length == 0)
                        continue;
                    var key = SkeletonKey(model);
                    if (!index.AnimsBySkeleton.TryGetValue(key, out var list))
                        index.AnimsBySkeleton[key] = list = new List<string>();
                    list.Add(e.Name);
                    if (e.Fields.GetValueOrDefault("previewModel", "").Trim() is { Length: > 0 } preview)
                    {
                        if (!index.PreviewModelsBySkeleton.TryGetValue(key, out var counts))
                            index.PreviewModelsBySkeleton[key] = counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        counts[preview] = counts.GetValueOrDefault(preview) + 1;
                    }
                }
                else if (e.Gdf.Contains("weapon", StringComparison.OrdinalIgnoreCase)
                         && e.Fields.GetValueOrDefault("gunModel", "").Trim() is { Length: > 0 } gun)
                {
                    var vm = FromEntry(e, gun);
                    foreach (var (k, v) in e.Fields)
                        if (k.EndsWith("Anim", StringComparison.OrdinalIgnoreCase) && v.Trim() is { Length: > 0 } anim)
                            index.ByAnim.TryAdd(anim, vm);
                }
            }
        }
        return index;
    }

    private static WeaponViewmodel FromEntry(GdtEntry e, string gun) => FromValues(e.Name, gun, key => e.Fields.GetValueOrDefault(key, ""), "");

    /// <summary>A weapon's view gun, its tag, its attached view models and its viewhands from its values (<paramref name="value"/>: a
    /// key's value, "" when unset).</summary>
    public static WeaponViewmodel FromValues(string weapon, string gun, Func<string, string> value, string via)
    {
        var attachments = new List<ViewmodelAttachment>();
        for (int i = 1; i <= 16; i++)
        {
            var model = value($"attachViewModel{i}").Trim();
            var tag = value($"attachViewModelTag{i}").Trim();
            // One model per tag: later entries on the same tag are alternatives (e.g. the ADS variant of a scope).
            if (model.Length > 0 && tag.Length > 0 && !attachments.Any(a => a.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                attachments.Add(new ViewmodelAttachment(model, tag));
        }
        var gunTag = value("viewmodelTag").Trim();
        var hands = value("handModel").Trim();
        return new WeaponViewmodel(weapon, gun, gunTag.Length > 0 ? gunTag : "tag_weapon", attachments, via, hands.Length > 0 ? hands : null);
    }
}
