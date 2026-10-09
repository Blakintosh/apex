using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using Apex.Editor.Services.Preview.Formats;
using Apex.Render.Assets;
using Apex.Render.Data;
using Apex.Render.Data.Animation;
using Apex.Render.Data.Assets;
using Apex.Render.Data.Conversion.XModel;
using Apex.Render.Data.Hashing;

namespace Apex.Editor.Services.Preview.ToolsGfx;

/// <summary>Where an anim preview's clip came from.</summary>
public enum AnimClipOrigin
{
    /// <summary>APE's xanim cache for (anim, GDT model) — exactly what APE evaluates.</summary>
    Cache,

    /// <summary>The raw export (<c>xanim_bin</c>/<c>xanim_export</c>) mapped onto the model's bones by name.</summary>
    RawExport,
}

/// <summary>A clip ready for <see cref="AnimPreviewPose"/>: where its motion came from in a few words (the stats line
/// leads with it), the file it was read from, and what that means in a sentence (the line's tooltip).</summary>
public sealed record AnimPreviewClip(AnimClip Clip, AnimClipOrigin Origin, string Description, string? File = null, string? Detail = null);

/// <summary>What the user chose for a viewmodel preview (null = resolve from the GDTs).</summary>
/// <param name="Hands">Viewhands xmodel drawn when the xanim has no <c>previewModel</c>.</param>
/// <param name="Gun">View gun xmodel replacing the resolved one; <c>"none"</c> hides it.</param>
/// <param name="LastHands">The viewhands last chosen this session: the last place hands are looked for when neither
/// the anim, its weapon nor its sibling anims name any.</param>
public sealed record ViewmodelChoice(string? Hands, string? Gun, string? LastHands = null);

/// <summary>
/// A composed viewmodel preview: the primary model (viewhands, else the anim's own model — possibly bones only), the
/// models attached to it (view gun, its attachments), the rig joining them and the clip driving it.
/// </summary>
/// <param name="Hands">The viewhands drawn (null for none).</param>
/// <param name="HandsVia">Where they came from, in the caption's words ("handModel of wpn_ar_an94"); null for the
/// anim's own <c>previewModel</c>.</param>
public sealed record ViewmodelScene(PreparedPreviewModel Primary, IReadOnlyList<PreparedPreviewModel> Attached, AnimRig Rig,
    AnimPreviewClip Clip, string Description, string? Hands = null, string? HandsVia = null);

/// <summary>
/// Loads what APE's xanim preview shows (animation.md §2): the model is the xanim's <c>previewModel</c> xmodel asset,
/// else its <c>model</c> file (an xmodel_bin, drawn through its xmesh cache); the clip is APE's xanim cache converted
/// against the GDT <c>model</c> (<see cref="ModelCacheLocator.FindXAnim"/>), else the raw export matched onto the
/// model's bones by name.
/// </summary>
public static class AnimPreviewSource
{
    /// <summary>The model the anim plays on (throws <see cref="FileNotFoundException"/> when its caches are missing).</summary>
    public static PreparedPreviewModel PrepareModel(ToolsGfxData data, string animName, IReadOnlyDictionary<string, string> fields,
        CancellationToken ct)
    {
        var options = new PreviewModelOptions { SkipBrokenMaterials = true };
        var previewModel = fields.GetValueOrDefault("previewModel", "").Trim();
        if (previewModel.Length > 0 && data.Gdt.Find(previewModel, "xmodel") is not null)
            return ToolsGfxPreviewService.PrepareModel(data, previewModel, options, ct);

        var modelFile = fields.GetValueOrDefault("model", "").Trim();
        if (modelFile.Length == 0)
            throw new FileNotFoundException($"{animName}: set previewModel (or model) to preview this animation");
        var bin = data.Models.ResolveModelExportPath(modelFile);
        return PrepareModelFile(data, bin, options, () => LoadModelFile(data, bin), ct);
    }

    /// <summary>An xmodel export drawn on its own (its xmesh through the GDT materials it names), reused while unchanged.</summary>
    private static PreparedPreviewModel PrepareModelFile(ToolsGfxData data, string bin, PreviewModelOptions options, Func<XMeshData> mesh,
        CancellationToken ct) =>
        ToolsGfxPreviewService.Reuse(data, "file|" + bin, options,
            () => PreviewModelLoader.PrepareMeshes(data, ModelCacheLocator.XModelName(bin), new[] { GpuMesh.FromXMesh(mesh()) }, options: options,
                cancellationToken: ct),
            ct, [bin]);

    /// <summary>
    /// The xmesh of an xanim's <c>model</c> file: its cache (APE's or converted by Apex) or, for a bones-only file such as
    /// a <c>*_skeleton.xmodel_bin</c> (no surfaces, so never cached), its skeleton converted in memory.
    /// </summary>
    public static XMeshData LoadModelFile(ToolsGfxData data, string xmodelBin)
    {
        if (data.Models.FindXMesh(xmodelBin, XModelLodParameters.Default) is { } loc)
            return loc.Payload is { } payload ? XMeshData.Parse(payload, loc.Path) : XMeshData.Load(loc.Path);
        if (!File.Exists(xmodelBin))
            throw new FileNotFoundException($"model file not found: {Path.GetFileName(xmodelBin)}");
        try
        {
            return XModelConverter.ConvertSkeleton(xmodelBin, XModelLodParameters.Default);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"could not read {Path.GetFileName(xmodelBin)}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A viewmodel preview beyond APE's (animation.md §2, report B/C): the anim evaluated once on a rig that merges its
    /// own skeleton (the GDT <c>model</c> file) with every drawn model, and each model skinned from it by bone name —
    /// viewhands (<c>previewModel</c> or <paramref name="choice"/>), the view gun (<c>previewAttachModel</c>, else the
    /// <c>gunModel</c> of the weapon playing this anim) and the gun's attached view models. The gun's joints (bolt, mag, …)
    /// animate because the rig carries the anim's tracks for them; a gun root the rig lacks hangs from the attach tag.
    /// The clip is the authored motion (<see cref="LoadAuthoredClip"/>), not APE's double bind offset.
    /// <paramref name="weapon"/> is the weapon the anim plays for when the caller already knows it (a weapon's own
    /// preview, from its current values); otherwise it is looked up (<see cref="WeaponViewmodelLookup"/>).
    /// </summary>
    public static ViewmodelScene PrepareViewmodel(ToolsGfxData data, string animName, IReadOnlyDictionary<string, string> fields, ViewmodelChoice choice,
        string? rawAnimPath, CancellationToken ct, WeaponViewmodel? weapon = null)
    {
        var options = new PreviewModelOptions { SkipBrokenMaterials = true };
        var notes = new List<string>();

        // The anim's own skeleton: the file it was exported/converted against.
        var modelFile = fields.GetValueOrDefault("model", "").Trim();
        XMeshData? animMesh = null;
        string? animBin = null;
        if (modelFile.Length > 0)
        {
            animBin = data.Models.ResolveModelExportPath(modelFile);
            animMesh = LoadModelFile(data, animBin);
        }

        var baseSkeleton = animMesh is not null ? AnimSkeleton.From(animMesh) : null;
        var sourceData = Path.Combine(data.Install.Root, "source_data");
        var forWeapon = weapon;
        weapon ??= WeaponViewmodelLookup.Find(sourceData, animName, modelFile, ct);

        // Primary: the viewhands, else the anim's own model itself (drawn if it has surfaces, joints otherwise).
        var (hands, handsModel, handsVia) = ChooseHands(data, fields, choice, weapon, sourceData, modelFile, baseSkeleton, options, notes, ct);
        PreparedPreviewModel primary;
        if (handsModel is not null)
        {
            primary = handsModel;
            notes.Insert(0, handsVia is null ? "hands " + hands : $"hands {hands} ({handsVia})");
        }
        else if (animMesh is not null)
        {
            primary = PrepareModelFile(data, animBin!, options, () => animMesh, ct);
            hands = "";
        }
        else
        {
            throw new FileNotFoundException($"{animName}: set previewModel (or model) to preview this animation");
        }
        ct.ThrowIfCancellationRequested();

        // The view gun: explicit choice, else previewAttachModel, else the weapon that plays this anim — skipping a gun
        // whose joints do not fit the anim's skeleton for one that does (the rig would pull its parts apart).
        var members = new List<(PreparedPreviewModel Model, string? Tag)>();
        string gunTag = fields.GetValueOrDefault("previewAlignParentTag", "").Trim();
        var candidates = new List<(string Model, string Via)>();
        if (choice.Gun?.Trim() is { Length: > 0 } chosen)
        {
            candidates.Add((chosen, "chosen"));
        }
        else
        {
            if (fields.GetValueOrDefault("previewAttachModel", "").Trim() is { Length: > 0 } attach)
                candidates.Add((attach, "previewAttachModel"));
            bool primaryHasGeometry = primary.Lods.Any(l => l.Surfaces.Count > 0);
            if (weapon is not null && (hands.Length > 0 || !primaryHasGeometry)
                && !candidates.Any(c => c.Model.Equals(weapon.GunModel, StringComparison.OrdinalIgnoreCase)))
                candidates.Add((weapon.GunModel, weapon.Via));
        }
        if (gunTag.Length == 0)
            gunTag = weapon?.GunTag ?? "tag_weapon";
        var gunChoice = ChooseGun(data, candidates, baseSkeleton, options, notes, ct);
        if (gunChoice is var (gun, gunModel, gunVia))
        {
            members.Add((gunModel, gunTag));
            notes.Add($"gun {gun} ({(forWeapon is not null && forWeapon.GunModel.Equals(gun, StringComparison.OrdinalIgnoreCase) ? forWeapon.Via : gunVia)})");
            // The weapon's attached view models, when this is its gun.
            if (weapon is not null && weapon.GunModel.Equals(gun, StringComparison.OrdinalIgnoreCase))
                foreach (var a in weapon.Attachments)
                    if (TryPrepare(data, a.Model, options, notes, ct) is { } am)
                    {
                        members.Add((am, a.Tag));
                        notes.Add($"{a.Model} at {a.Tag}");
                    }
        }
        ct.ThrowIfCancellationRequested();

        // Rig: the anim skeleton first (the tracks' own hierarchy), then every drawn model's bones by name.
        var rigMembers = new List<AnimRigMember>();
        foreach (var (m, tag) in members.Prepend((primary, (string?)null)))
            if (m.Lods.Count > 0 && AnimSkeleton.From(m.Lods[0]) is { } sk)
                rigMembers.Add(new AnimRigMember(sk, tag));
        if (baseSkeleton is null)
        {
            if (rigMembers.Count == 0)
                throw new InvalidDataException($"{animName}: no skeleton to play the animation on");
            baseSkeleton = rigMembers[0].Skeleton;
        }
        var rig = AnimRig.Build(baseSkeleton, rigMembers);
        var clip = LoadAuthoredClip(data, animName, fields, rawAnimPath, ct);
        return new ViewmodelScene(primary, members.Select(m => m.Model).ToList(), rig, clip, string.Join(" · ", notes),
            handsModel is null ? null : hands, handsModel is null ? null : handsVia);
    }

    /// <summary>Largest distance (inches) a viewhands joint may sit from the anim skeleton's joint of the same name,
    /// relative to the hands' root, for inferred hands to count as built on the anim's rig (arms of another build would
    /// be pulled out of shape).</summary>
    private const float HandsFitTolerance = 0.5f;

    /// <summary>
    /// The viewhands an anim plays on: its <c>previewModel</c>, else the ones the user chose, else — inferred, so a
    /// first-person anim never shows a floating gun — the <c>handModel</c> of the weapon that plays it, the
    /// <c>previewModel</c> most of its sibling anims (same skeleton file) name, or the hands last chosen this session.
    /// An inferred candidate is taken only when its joints fit the anim's skeleton (<see cref="AnimRig.BindMismatch"/>);
    /// a chosen one as it is. Returns the name, its prepared model (null for none) and where it came from (null for
    /// <c>previewModel</c>, which the GDT already says). Every model comes through the prepared-model cache.
    /// </summary>
    private static (string Hands, PreparedPreviewModel? Model, string? Via) ChooseHands(ToolsGfxData data, IReadOnlyDictionary<string, string> fields,
        ViewmodelChoice choice, WeaponViewmodel? weapon, string sourceData, string modelFile, AnimSkeleton? animSkeleton, PreviewModelOptions options,
        List<string> notes, CancellationToken ct)
    {
        var previewModel = fields.GetValueOrDefault("previewModel", "").Trim();
        if (previewModel.Length > 0 && data.Gdt.Find(previewModel, "xmodel") is not null)
            return (previewModel, ToolsGfxPreviewService.PrepareModel(data, previewModel, options, ct), null);
        if (choice.Hands?.Trim() is { Length: > 0 } chosen && data.Gdt.Find(chosen, "xmodel") is not null
            && TryPrepare(data, chosen, options, notes, ct) is { } chosenModel)
            return (chosen, chosenModel, "chosen");

        var candidates = new List<(string Model, string Via)>();
        if (weapon?.Hands is { Length: > 0 } handModel)
            candidates.Add((handModel, $"handModel of {weapon.Weapon}"));
        if (WeaponViewmodelLookup.CommonHands(sourceData, modelFile, ct) is { } siblings)
            candidates.Add((siblings.Model, siblings.Anims == 1 ? "previewModel of an anim on this skeleton" : $"previewModel of {siblings.Anims} anims on this skeleton"));
        if (choice.LastHands?.Trim() is { Length: > 0 } last)
            candidates.Add((last, "last chosen"));

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, via) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!tried.Add(name) || data.Gdt.Find(name, "xmodel") is null || TryPrepare(data, name, options, notes, ct) is not { } model)
                continue;
            if (animSkeleton is null || model.Lods.Count == 0)
                return (name, model, via);
            var off = AnimSkeleton.From(model.Lods[0]) is { } sk ? AnimRig.BindMismatch(animSkeleton, sk) : null;
            if (off is { } o && o.Distance <= HandsFitTolerance)
                return (name, model, via);
            // A model that shares no joints with the anim isn't hands at all (weapons often name tag_origin as a
            // placeholder): passed over without a word. Hands built for another rig are named, with how far off they are.
            if (off is { } far)
                notes.Add($"{name} ({via}) skipped: joints off the anim skeleton by up to {far.Distance:0.0} in ({far.Bone})");
        }
        return ("", null, null);
    }

    /// <summary>Largest distance (inches) a gun joint may sit from the anim skeleton's joint of the same name, relative to
    /// the gun root, for the gun to count as built on the anim's rig.</summary>
    private const float GunFitTolerance = 0.5f;

    /// <summary>
    /// The first of <paramref name="candidates"/> that can be drawn and whose joints fit <paramref name="animSkeleton"/>
    /// (<see cref="AnimRig.BindMismatch"/>), else the first that can be drawn at all; null for none (or "none"). A chosen
    /// gun is taken as it is.
    /// </summary>
    private static (string Gun, PreparedPreviewModel Model, string Via)? ChooseGun(ToolsGfxData data, List<(string Model, string Via)> candidates,
        AnimSkeleton? animSkeleton, PreviewModelOptions options, List<string> notes, CancellationToken ct)
    {
        var misfits = new List<(string Gun, PreparedPreviewModel Model, string Via, string Why)>();
        foreach (var (gun, via) in candidates)
        {
            if (gun.Equals("none", StringComparison.OrdinalIgnoreCase))
                return null;
            if (TryPrepare(data, gun, options, notes, ct) is not { } model)
                continue;
            var off = via == "chosen" || animSkeleton is null || model.Lods.Count == 0 || AnimSkeleton.From(model.Lods[0]) is not { } sk
                ? null
                : AnimRig.BindMismatch(animSkeleton, sk);
            if (off is not { } o || o.Distance <= GunFitTolerance)
            {
                foreach (var skipped in misfits)
                    notes.Add($"{skipped.Gun} ({skipped.Via}) skipped: {skipped.Why}");
                return (gun, model, via);
            }
            misfits.Add((gun, model, via, $"joints off the anim skeleton by up to {o.Distance:0.0} in ({o.Bone})"));
        }
        return misfits.Count > 0 ? (misfits[0].Gun, misfits[0].Model, $"{misfits[0].Via}; {misfits[0].Why}") : null;
    }

    private static PreparedPreviewModel? TryPrepare(ToolsGfxData data, string xmodel, PreviewModelOptions options, List<string> notes, CancellationToken ct)
    {
        try
        {
            return ToolsGfxPreviewService.PrepareModel(data, xmodel, options, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            // The info line is user-facing: say what is missing, keep the exception for the stats log.
            notes.Add($"{xmodel} could not be drawn");
            if (ToolsGfxPreviewService.IsLogging)
                ToolsGfxPreviewService.Log($"{xmodel} not drawn: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The anim as authored, for <see cref="PrepareViewmodel"/>. <c>useBones = 0</c> anims (most viewmodel anims) play
    /// their export directly: every part at its exported transform, parts matched to rig bones by name, bones without a
    /// part following their parent — so neither APE's second bind offset (animation.md §4) nor the converter's
    /// "relative to its own bind" fallback for bones whose parent has no part (conversion.md §3.7) can displace
    /// anything. <c>useBones = 1</c> and additive anims keep the cache with APE's evaluation, which is right for them
    /// (bind + offset). Without an export the cache is used with its translations taken as full local translations.
    /// </summary>
    public static AnimPreviewClip LoadAuthoredClip(ToolsGfxData data, string animName, IReadOnlyDictionary<string, string> fields, string? rawAnimPath,
        CancellationToken ct)
    {
        var type = XAnimTypes.Parse(fields.GetValueOrDefault("type", "relative").Trim()) ?? XAnimType.Relative;
        bool useBones = IsSet(fields.GetValueOrDefault("useBones", "0"));
        bool looping = IsSet(fields.GetValueOrDefault("looping", "0"));
        if (!useBones && type != XAnimType.Additive && rawAnimPath is not null && File.Exists(rawAnimPath)
            && AnimReader.Load(rawAnimPath, ct) is { Frames.Count: > 0 } raw)
        {
            var root = type.HasRootMotion() ? WorldSpaceRoot.Delta : type == XAnimType.Absolute ? WorldSpaceRoot.Absolute : WorldSpaceRoot.Relative;
            return new AnimPreviewClip(FromRaw(raw, looping, root), AnimClipOrigin.RawExport, "authored motion" + Problem(raw), Path.GetFileName(rawAnimPath),
                "Apex plays the export file as it was authored: every part at its exported transform, matched to the rig by bone name.");
        }
        var clip = LoadClip(data, animName, fields, rawAnimPath, ct);
        if (clip.Clip is XAnimClip x && !useBones && type != XAnimType.Additive)
            clip = clip with
            {
                Clip = new XAnimClip(x.Data, x.Type) { AddBindTranslation = false },
                Detail = clip.Detail + " Its translations are taken as full local positions, since there is no export file to read.",
            };
        return clip;
    }

    /// <summary>
    /// The clip: APE's cache when present, else the raw export (<paramref name="rawAnimPath"/>). Throws
    /// <see cref="FileNotFoundException"/> when neither is available.
    /// </summary>
    public static AnimPreviewClip LoadClip(ToolsGfxData data, string animName, IReadOnlyDictionary<string, string> fields, string? rawAnimPath,
        CancellationToken ct)
    {
        var typeText = fields.GetValueOrDefault("type", "relative").Trim();
        var type = XAnimTypes.Parse(typeText) ?? XAnimType.Relative;
        bool looping = IsSet(fields.GetValueOrDefault("looping", "0"));
        bool useBones = IsSet(fields.GetValueOrDefault("useBones", "0"));
        var node = fields.GetValueOrDefault("node", "");

        var animFile = fields.GetValueOrDefault("filename", "").Trim();
        var animBin = animFile.Length > 0 ? data.Models.ResolveXAnimExportPath(animFile) : null;
        var modelFile = fields.GetValueOrDefault("model", "").Trim();
        if (modelFile.Length > 0)
        {
            var modelBin = data.Models.ResolveModelExportPath(modelFile);
            var loc = data.Models.FindXAnim(animName, animBin, typeText, looping, useBones, node, ModelCacheLocator.XModelName(modelBin), modelBin);
            if (loc is not null)
            {
                var clip = new XAnimClip(XAnimData.Load(loc.Path), type);
                return loc.Match == CacheMatch.Exact
                    ? new AnimPreviewClip(clip, AnimClipOrigin.Cache, "xanim cache", Path.GetFileName(loc.Path),
                        "Apex plays the converted xanim cache, exactly as APE evaluates it.")
                    : new AnimPreviewClip(clip, AnimClipOrigin.Cache, "xanim cache, older than the export", Path.GetFileName(loc.Path),
                        "The export file changed after this cache was converted. Apex plays the cache, so this is the motion as last converted.");
            }
        }

        if (rawAnimPath is null || !File.Exists(rawAnimPath))
            throw new FileNotFoundException($"no xanim cache for {animName} and no export file to fall back to");
        var raw = AnimReader.Load(rawAnimPath, ct)
                  ?? throw new InvalidDataException($"could not parse {Path.GetFileName(rawAnimPath)}");
        return new AnimPreviewClip(FromRaw(raw, looping), AnimClipOrigin.RawExport, "export file" + Problem(raw), Path.GetFileName(rawAnimPath),
            "There is no converted xanim cache for this anim, so Apex plays its export file, matching parts to the model's bones by name. "
            + "The converted pose can differ slightly.");
    }

    private static string Problem(PreviewAnim raw) => raw.ReadProblem is { } p ? $" ({p})" : "";

    /// <summary>A raw export as a model-space clip (looping anims drop their closing copy of frame 0, like APE).</summary>
    public static WorldSpaceClip FromRaw(PreviewAnim anim, bool looping, WorldSpaceRoot root = WorldSpaceRoot.Absolute)
    {
        int count = anim.Frames.Count;
        if (looping && count > 1)
            count--;
        var frames = new List<WorldSpaceFrame>(count);
        for (int f = 0; f < count; f++)
        {
            var src = anim.Frames[f];
            var q = new Quaternion[src.Rotations.Length];
            for (int p = 0; p < q.Length; p++)
                q[p] = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(src.Rotations[p]));
            frames.Add(new WorldSpaceFrame(q, src.Positions.ToArray()));
        }
        var notes = anim.Notetracks.Select(n => new XAnimNote((uint)Math.Max(n.Frame, 0), n.Name)).ToList();
        return new WorldSpaceClip(anim.PartNames.ToList(), anim.FrameRate, frames, notes) { Root = root };
    }

    private static bool IsSet(string value) => value.Trim() is { Length: > 0 } v && v != "0";
}
