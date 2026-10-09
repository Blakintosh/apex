using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Preview;
using Apex.Editor.Services.Preview.ToolsGfx;

namespace Apex.Shots;

/// <summary>
/// First person's inferred viewhands on the real install (read-only: models are prepared from the install's caches, or
/// converted into Apex's own cache, never under the install). An xanim with no previewModel plays on its weapon's
/// handModel, else the hands its sibling anims preview on, else the last chosen; a candidate that doesn't fit the anim's
/// skeleton is skipped. Re-preparing a recently previewed anim is timed with the hands inferred and with them chosen.
/// </summary>
public partial class Program
{
    private static readonly string[] LiveAnimExtensions = { ".xanim_bin", ".xanim_export" };

    private static void RunLiveViewhands()
    {
        var env = new GameEnvironment();
        if (!env.IsAvailable)
        {
            Console.WriteLine("live viewhands: BO3 not found — skipped");
            return;
        }
        LiveViewhands(env, GdtLoader.LoadAll(env));
    }

    private static void LiveViewhands(GameEnvironment env, AssetDatabase db)
    {
        if (env.Bo3Root is not { } root)
            return;
        var install = Apex.Render.Data.ToolsGfxInstall.FromRoot(root);
        if (!install.IsAvailable)
        {
            Console.WriteLine("live viewhands: no ToolsGfx data — skipped");
            return;
        }
        var byKey = db.Assets.GroupBy(a => (a.Type.ToLowerInvariant(), a.Name.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.First());
        AssetRecord? Resolve(string type, string name) => byKey.GetValueOrDefault((type.ToLowerInvariant(), name.ToLowerInvariant()));
        var data = new Apex.Render.Data.ToolsGfxData(install, new ApexGdtLookup(Resolve));
        var sourceData = Path.Combine(root, "source_data");
        var ct = CancellationToken.None;

        var clock = Stopwatch.StartNew();
        // Anims with a skeleton and no previewModel: the ones whose hands have to come from somewhere.
        var bare = db.Assets.Where(a => a.Type.Equals("xanim", StringComparison.OrdinalIgnoreCase)
                                        && a.ScanProperties.GetValueOrDefault("model", "").Trim().Length > 0
                                        && a.ScanProperties.GetValueOrDefault("previewModel", "").Trim().Length == 0)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        WeaponViewmodelLookup.Find(sourceData, "", null, ct);
        Console.WriteLine($"info  live viewhands: {bare.Count:N0} xanims have a skeleton and no previewModel; the weapon index built in {clock.ElapsedMilliseconds:N0} ms");

        IReadOnlyDictionary<string, string> Fields(AssetRecord a) => new Dictionary<string, string>(a.ScanProperties, StringComparer.Ordinal);
        string? RawPath(AssetRecord a) => a.ScanProperties.GetValueOrDefault("filename", "").Trim() is { Length: > 0 } f && env.XanimExportDir is { } dir
            ? PreviewFileResolver.ResolveRelative(env, dir, f, LiveAnimExtensions)
            : null;
        string Skeleton(AssetRecord a) => a.ScanProperties.GetValueOrDefault("model", "").Trim();
        ViewmodelScene? Prepare(AssetRecord a, ViewmodelChoice choice)
        {
            try
            {
                return AnimPreviewSource.PrepareViewmodel(data, a.Name, Fields(a), choice, RawPath(a), ct);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                // An anim with neither a cache nor an export on this install: not one to check with.
                _ = ex;
                return null;
            }
        }

        // 1. A weapon's own anim: its handModel.
        // (Some weapons name a placeholder, e.g. tag_origin, which shares no joints with the anim and is skipped: the first
        // whose hands fit is the one checked.)
        (AssetRecord Anim, WeaponViewmodel Weapon, ViewmodelScene Scene)? own = null;
        var tries = 0;
        var triedWeapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in bare)
        {
            if (WeaponViewmodelLookup.Find(sourceData, a.Name, Skeleton(a), ct) is not { Hands: { } hands } w || !w.Via.Contains(" uses " + a.Name, StringComparison.OrdinalIgnoreCase)
                || Resolve("xmodel", hands) is null || !triedWeapons.Add(w.Weapon) || ++tries > 40 || Prepare(a, new ViewmodelChoice(null, null)) is not { } scene)
                continue;
            own = (a, w, scene);
            if (scene.Hands is not null)
                break;
        }
        // BO3's own weapons leave handModel empty or name a placeholder (tag_origin, viewmodel_hands_no_model): only a
        // weapon naming real viewhands can show this.
        if (own is { Scene.Hands: not null } o)
            Check($"live viewhands: an anim its weapon names plays on the weapon's handModel, and the caption says so ({o.Anim.Name}: '{o.Scene.Description}')",
                o.Scene.Hands.Equals(o.Weapon.Hands, StringComparison.OrdinalIgnoreCase) && o.Scene.HandsVia == $"handModel of {o.Weapon.Weapon}"
                && o.Scene.Description.StartsWith($"hands {o.Scene.Hands} (handModel of {o.Weapon.Weapon})", StringComparison.Ordinal));
        else
            Console.WriteLine($"live viewhands: no weapon in the install names viewhands that fit its anims ({triedWeapons.Count} weapons with a handModel tried) — handModel case skipped");
        Check($"live viewhands: a placeholder handModel (no joints in common) is passed over without a note ({own?.Anim.Name}: '{own?.Scene.Description}')",
            own is null || own.Value.Scene.Hands is not null || !own.Value.Scene.Description.Contains("skipped", StringComparison.Ordinal));

        // 2. An anim no weapon names, on a skeleton whose anims preview on hands: the hands most of them use.
        (AssetRecord Anim, SiblingHands Siblings, ViewmodelScene Scene)? sibling = null;
        foreach (var a in bare.Take(6000))
        {
            if (WeaponViewmodelLookup.Find(sourceData, a.Name, Skeleton(a), ct) is { Hands: not null }
                || WeaponViewmodelLookup.CommonHands(sourceData, Skeleton(a), ct) is not { } s || Resolve("xmodel", s.Model) is null
                || Prepare(a, new ViewmodelChoice(null, null)) is not { } scene)
                continue;
            sibling = (a, s, scene);
            if (scene.Hands is not null)
                break;
        }
        Check($"live viewhands: an anim no weapon names plays on the hands its sibling anims preview on ({sibling?.Anim.Name}: '{sibling?.Scene.Description}')",
            sibling is { } sb && (sb.Scene.Hands is { } h
                ? h.Equals(sb.Siblings.Model, StringComparison.OrdinalIgnoreCase) && sb.Scene.HandsVia!.StartsWith("previewModel of ", StringComparison.Ordinal)
                : sb.Scene.Description.Contains($"{sb.Siblings.Model} (previewModel of", StringComparison.Ordinal) && sb.Scene.Description.Contains("skipped", StringComparison.Ordinal)));

        // 3. A candidate built on another rig is skipped, and the caption says why: a gun offered as the last hands.
        if (own is { } first)
        {
            var orphan = bare.FirstOrDefault(a => WeaponViewmodelLookup.Find(sourceData, a.Name, Skeleton(a), ct) is null
                                                  && WeaponViewmodelLookup.CommonHands(sourceData, Skeleton(a), ct) is null);
            var misfit = orphan is null ? null : Prepare(orphan, new ViewmodelChoice(null, "none", first.Weapon.GunModel));
            Check($"live viewhands: hands that don't fit the anim's skeleton aren't drawn, and the caption says why ({orphan?.Name}: '{misfit?.Description}')",
                orphan is null || misfit is { Hands: null } && misfit.Description.Contains($"{first.Weapon.GunModel} (last chosen) skipped", StringComparison.Ordinal));
        }

        // 4. Re-opening: the hands come through the prepared-model cache, so finding them costs next to nothing.
        if (own is { } again)
        {
            static double Median(List<double> ms)
            {
                ms.Sort();
                return ms[ms.Count / 2];
            }
            var inferred = new List<double>();
            var chosen = new List<double>();
            for (var i = 0; i < 9; i++)
            {
                var sw = Stopwatch.StartNew();
                Prepare(again.Anim, new ViewmodelChoice(null, null));
                inferred.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                Prepare(again.Anim, new ViewmodelChoice(again.Weapon.Hands, null));
                chosen.Add(sw.Elapsed.TotalMilliseconds);
            }
            var (mi, mc) = (Median(inferred), Median(chosen));
            Console.WriteLine($"info  live viewhands: re-preparing {again.Anim.Name} (worker thread) median {mi:0.0} ms with the hands inferred, {mc:0.0} ms with them chosen");
            Check($"live viewhands: inferring the hands adds next to nothing to a re-open ({mi - mc:0.0} ms over choosing them; budget 5 ms of the 50 ms re-open)",
                mi - mc <= 5);
        }
    }
}
