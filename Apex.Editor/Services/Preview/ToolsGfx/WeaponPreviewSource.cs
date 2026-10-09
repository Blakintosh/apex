using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Render.Data;
using Apex.Render.Data.Animation;

namespace Apex.Editor.Services.Preview.ToolsGfx;

/// <summary>
/// What a weapon's recoil preview draws, from the weapon's values as they are now: the view gun and its attachments
/// (<c>gunModel</c>, <c>viewmodelTag</c>, <c>attachViewModelN</c> / <c>attachViewModelTagN</c>), the viewhands
/// (<c>handModel</c>), the hip pose (<c>idleAnim</c>) and the ADS pose (<c>adsUpAnim</c>, its last frame), and the
/// timing (<c>fireTime</c>, <c>adsTransInTime</c>, <c>adsTransOutTime</c>, seconds).
/// </summary>
/// <param name="Notes">What the preview does without (no ADS anim), for the line under it.</param>
/// <param name="Key">Everything above as text: an edit that leaves it the same reloads nothing.</param>
public sealed record WeaponPreviewPlan(
    WeaponViewmodel Viewmodel,
    string? Hands,
    string IdleAnim,
    IReadOnlyDictionary<string, string> IdleFields,
    string? AdsAnim,
    IReadOnlyDictionary<string, string>? AdsFields,
    float FireTime,
    float AdsInTime,
    float AdsOutTime,
    IReadOnlyList<string> Notes,
    string Key);

/// <summary>A plan, or the one plain sentence saying why there is no gun to draw; the timing either way (Fire works without a gun).</summary>
public sealed record WeaponPreviewResolution(WeaponPreviewPlan? Plan, string? Problem, WeaponTiming Timing);

/// <summary>The weapon's <c>fireTime</c>, <c>adsTransInTime</c> and <c>adsTransOutTime</c>, in seconds.</summary>
public readonly record struct WeaponTiming(float FireTime, float AdsInTime, float AdsOutTime);

/// <summary>A loaded recoil preview: the viewmodel on the idle anim, and the ADS-up anim when it could be read.</summary>
public sealed record WeaponPreviewScene(ViewmodelScene Viewmodel, AnimClip? Ads, string? AdsNote);

/// <summary>
/// Resolves and loads a weapon's recoil preview. The values are read from the records as they are in the session (the
/// weapon's own, else the nearest ancestor's, else the deffile default), never from <see cref="WeaponViewmodelLookup"/>'s
/// index, which is built once and misses edits; anims are the xanim assets the weapon names.
/// </summary>
public static class WeaponPreviewSource
{
    private static readonly string[] AnimExtensions = { ".xanim_bin", ".xanim_export" };

    private static readonly string[] Keys =
    {
        "gunModel", "handModel", "viewmodelTag", "idleAnim", "adsUpAnim", "fireTime", "adsTransInTime", "adsTransOutTime",
    };

    /// <summary>The keys the preview reads (an edit to any other core key can't change it).</summary>
    public static bool Reads(string key) =>
        Keys.Contains(key, StringComparer.OrdinalIgnoreCase)
        || key.StartsWith("attachViewModel", StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="key"/> as the game sees it for <paramref name="weapon"/> (UI thread: reads live records).</summary>
    public static string EffectiveValue(AssetRecord weapon, string key, Func<string, string, AssetRecord?> resolve)
    {
        if (weapon.Properties.TryGetValue(key, out var own))
            return own;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { weapon.Name };
        for (var cur = weapon; cur.Parent is { } p && seen.Add(p) && resolve(weapon.Type, p) is { } parent; cur = parent)
            if (parent.ScanProperties.TryGetValue(key, out var inherited))
                return inherited;
        return SchemaRegistry.Get(weapon.Type)?.Find(key)?.Default ?? "";
    }

    /// <summary>The weapon's plan, or why there is none. A few dozen dictionary reads: UI thread.</summary>
    public static WeaponPreviewResolution Resolve(AssetRecord weapon, Func<string, string, AssetRecord?> resolve)
    {
        string Value(string key) => EffectiveValue(weapon, key, resolve).Trim();

        var timing = new WeaponTiming(Seconds(Value("fireTime"), 0.1f), Seconds(Value("adsTransInTime"), 0f), Seconds(Value("adsTransOutTime"), 0f));
        var gun = Value("gunModel");
        if (gun.Length == 0)
            return new(null, "No gun to draw: set gunModel to see it.", timing);
        if (resolve("xmodel", gun) is null)
            return new(null, $"No gun to draw: gunModel is {gun}, which isn't in a loaded GDT.", timing);
        var idle = Value("idleAnim");
        if (idle.Length == 0)
            return new(null, "No gun to draw: set idleAnim, the anim the gun is held in at hip.", timing);
        if (resolve("xanim", idle) is not { } idleRecord)
            return new(null, $"No gun to draw: idleAnim is {idle}, which isn't in a loaded GDT.", timing);

        var notes = new List<string>();
        var ads = Value("adsUpAnim");
        IReadOnlyDictionary<string, string>? adsFields = null;
        if (ads.Length == 0)
            notes.Add("No adsUpAnim: ADS keeps the hip pose.");
        else if (resolve("xanim", ads) is { } adsRecord)
            adsFields = Fields(adsRecord);
        else
            notes.Add($"adsUpAnim {ads} isn't in a loaded GDT: ADS keeps the hip pose.");

        var viewmodel = WeaponViewmodelLookup.FromValues(weapon.Name, gun, Value, "gunModel");
        var hands = Value("handModel");
        var idleFields = Fields(idleRecord);
        var plan = new WeaponPreviewPlan(viewmodel, hands.Length > 0 ? hands : null, idle, idleFields,
            adsFields is null ? null : ads, adsFields, timing.FireTime, timing.AdsInTime, timing.AdsOutTime, notes, "");
        return new(plan with { Key = KeyOf(plan) }, null, timing);
    }

    private static Dictionary<string, string> Fields(AssetRecord record) =>
        new(record.ScanProperties, StringComparer.OrdinalIgnoreCase);

    /// <summary>A positive number of seconds, else <paramref name="fallback"/>.</summary>
    private static float Seconds(string value, float fallback) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && float.IsFinite(s) && s > 0f ? s : fallback;

    private static string KeyOf(WeaponPreviewPlan p)
    {
        var sb = new StringBuilder();
        sb.Append(p.Viewmodel.Weapon).Append('|').Append(p.Viewmodel.GunModel).Append('|').Append(p.Viewmodel.GunTag).Append('|').Append(p.Hands);
        foreach (var a in p.Viewmodel.Attachments)
            sb.Append('|').Append(a.Model).Append('@').Append(a.Tag);
        sb.Append('|').Append(p.IdleAnim).Append('|').Append(p.AdsAnim);
        foreach (var fields in new[] { p.IdleFields, p.AdsFields })
            if (fields is not null)
                foreach (var (k, v) in fields.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                    sb.Append('|').Append(k).Append('=').Append(v);
        sb.Append('|').Append(string.Join(",", p.Notes));
        sb.Append(CultureInfo.InvariantCulture, $"|{p.FireTime}|{p.AdsInTime}|{p.AdsOutTime}");
        return sb.ToString();
    }

    /// <summary>
    /// Loads the plan (worker thread): the viewmodel composed on the idle anim, exactly as an xanim preview of it with
    /// this weapon's gun (<see cref="AnimPreviewSource.PrepareViewmodel"/>), and the ADS-up anim's clip. Throws like
    /// <see cref="AnimPreviewSource.PrepareViewmodel"/> when the idle anim or the models can't be loaded; an ADS anim that
    /// can't be read is a note, and ADS then keeps the hip pose. The hands are found as an xanim preview finds them: the
    /// weapon's <c>handModel</c>, else its idle anim's siblings', else <paramref name="lastHands"/>, each only if it fits.
    /// </summary>
    public static WeaponPreviewScene Prepare(ToolsGfxData data, GameEnvironment env, WeaponPreviewPlan plan, string? lastHands, CancellationToken ct)
    {
        var scene = AnimPreviewSource.PrepareViewmodel(data, plan.IdleAnim, plan.IdleFields, new ViewmodelChoice(null, plan.Viewmodel.GunModel, lastHands),
            RawAnimPath(env, plan.IdleFields), ct, plan.Viewmodel);
        if (plan.AdsAnim is not { } adsName || plan.AdsFields is not { } adsFields)
            return new WeaponPreviewScene(scene, null, null);
        try
        {
            var ads = AnimPreviewSource.LoadAuthoredClip(data, adsName, adsFields, RawAnimPath(env, adsFields), ct);
            return new WeaponPreviewScene(scene, ads.Clip, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return new WeaponPreviewScene(scene, null, $"adsUpAnim {adsName} couldn't be read, so ADS keeps the hip pose.");
        }
    }

    /// <summary>The anim's export file (<c>filename</c>) on disk, or null (file probing: worker thread).</summary>
    private static string? RawAnimPath(GameEnvironment env, IReadOnlyDictionary<string, string> fields)
    {
        var raw = fields.GetValueOrDefault("filename", "").Trim();
        if (raw.Length == 0 || env.XanimExportDir is not { } root)
            return null;
        return PreviewFileResolver.ResolveRelative(env, root, raw, AnimExtensions)
            ?? (env.Bo3Root is null ? null : PreviewFileResolver.ResolveRelative(env, env.Bo3Root, raw, AnimExtensions));
    }
}
