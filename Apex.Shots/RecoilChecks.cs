using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Models;
using Apex.Editor.Services.Extensions;
using Apex.Editor.Services.Extensions.Simulation;
using Apex.Editor.Services.Preview.ToolsGfx;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using Apex.Render.Assets;
using Apex.Render.Data.Animation;
using Apex.Render.Data.Assets;
using Apex.Render.Presentation;
using Apex.Render.Scene;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The weapon recoil preview (step 4b): the kick's signs and matrices, the trigger / ADS / step driver on a scripted
/// simulation, the hip-to-ADS pose mix, resolving a weapon's viewmodel from its live values, the preview in the app on a
/// temp install with the fixture module (consent, every state's notice, real keys and clicks, edits remaking the
/// simulation, stale results, idle frames), and on the real install a frame with no recoil byte-identical to one drawn
/// without the feature. Nothing here writes outside scratch folders.
/// </summary>
public partial class Program
{
    private static void RunRecoilChecks(string outDir) => RecoilPart(() =>
    {
        RecoilLogicChecks();
        RecoilOverlayDrawChecks();
        RecoilAppPartChecks(outDir);
        RecoilRenderChecks();
    });

    /// <summary>The pure parts: signs, the driver, the pose mix, resolving, and what the overlay measures.</summary>
    private static void RecoilLogicChecks()
    {
        RecoilSignChecks();
        RecoilDriverChecks();
        RecoilBlendChecks();
        RecoilResolveChecks();
        RecoilTraceChecks();
        RecoilTraceDriverChecks();
    }

    /// <summary>The module question and notes, then the app on a temp install.</summary>
    private static void RecoilAppPartChecks(string outDir)
    {
        RecoilQuestionChecks();
        RecoilNoteLabelChecks();
        RecoilAppChecks(outDir);
        RecoilHardeningChecks(outDir);
    }

    /// <summary>Runs a part of the recoil checks (the suite runs them as groups of their own), putting back what they change.</summary>
    private static void RecoilPart(Action run)
    {
        var saved = (Ext: Environment.GetEnvironmentVariable(ExtensionRegistry.DirVariable), Settings: Environment.GetEnvironmentVariable("APEX_SETTINGS_DIR"));
        try
        {
            run();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, saved.Ext);
            Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", saved.Settings);
            ExtensionRegistry.Clear();
            SchemaRegistry.ResetToMock();
        }
    }

    // ═══ Signs: negative pitch up, positive yaw left, origins along view axes ════

    private static void RecoilSignChecks()
    {
        static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-5f;
        var up = PreviewKick.Transform(new Vector3(-5, 0, 0), default);
        var left = PreviewKick.Transform(new Vector3(0, 5, 0), default);
        var muzzle = new Vector3(30, 0, 0);
        Check($"recoil signs: pitch -5 turns the muzzle (+X) up and yaw +5 turns it left (+Y) ({Vector3.Transform(muzzle, up)}, {Vector3.Transform(muzzle, left)})",
            Vector3.Transform(muzzle, up).Z > 2.5f && Math.Abs(Vector3.Transform(muzzle, up).Y) < 1e-4f
            && Vector3.Transform(muzzle, left).Y > 2.5f && Math.Abs(Vector3.Transform(muzzle, left).Z) < 1e-4f);
        var roll = PreviewKick.Transform(new Vector3(0, 0, 10), default);
        Check("recoil signs: roll turns about forward only, and the transform is rigid",
            Near(Vector3.TransformNormal(Vector3.UnitX, roll), Vector3.UnitX)
            && Math.Abs(Vector3.TransformNormal(Vector3.UnitY, roll).Length() - 1) < 1e-5f
            && Math.Abs(Matrix4x4.Identity.GetDeterminant() - roll.GetDeterminant()) < 1e-5f);
        Check("recoil signs: an origin is along the view axes (x forward, y left, z up)",
            PreviewKick.Transform(default, new Vector3(-1, 2, 3)).Translation == new Vector3(-1, 2, 3));

        // The camera: a view kick turns it with the viewmodel, so the gun holds still on screen and the world moves.
        var camera = new PreviewCamera(new Vector3(0, 0, 0), 0, 0);
        var kicked = PreviewSceneRenderer.Kicked(camera, new PreviewKick(new Vector3(-5, 0, 0), default, default, default).View);
        kicked.GetAxes(out var fwd, out var right, out var camUp);
        var gunInView = Vector3.Transform(muzzle, new PreviewKick(new Vector3(-5, 0, 0), default, default, default).View);
        Check($"recoil signs: a view kick of pitch -5 looks up (APE pitch {kicked.PitchDegrees:0.00}) and the gun stays where it was in the view",
            Math.Abs(kicked.PitchDegrees + 5) < 1e-3f && fwd.Z > 0
            && Math.Abs(Vector3.Dot(gunInView - kicked.Position, camUp)) < 1e-4f && Math.Abs(Vector3.Dot(gunInView - kicked.Position, right)) < 1e-4f);
        var ahead = new Vector3(1000, 0, 0);
        Check("recoil signs: so a point ahead in the world drops on screen (below the kicked camera's centre)",
            Vector3.Dot(ahead - kicked.Position, camUp) < 0);
        var gunOnly = new PreviewKick(default, default, new Vector3(-5, 0, 0), default);
        Check("recoil signs: a gun kick leaves the camera alone and lifts the muzzle on screen",
            !gunOnly.MovesView && Vector3.Transform(muzzle, gunOnly.Gun * gunOnly.View).Z > 2.5f);
        Check("recoil signs: an all-zero kick is no kick (the renderer draws the plain frame)",
            new PreviewKick().IsZero && !new PreviewKick(default, default, default, new Vector3(0, 0, 1e-6f)).IsZero);

        var supported = new SimulatorFrame(true, SimulatorParts.ViewAngles | SimulatorParts.GunOrigin, new Vector3(-1, 2, 3), new Vector3(4, 5, 6),
            new Vector3(7, 8, 9), new Vector3(10, 11, 12), "");
        var drawn = WeaponPreviewViewModel.KickOf(supported);
        Check($"recoil signs: only the parts the module computed are drawn ({drawn})",
            drawn.ViewAngles == new Vector3(-1, 2, 3) && drawn.ViewOrigin == default && drawn.GunAngles == default && drawn.GunOrigin == new Vector3(10, 11, 12));
        Check($"recoil signs: a module's note is shown as it is; without one the parts left out are named ('{WeaponPreviewViewModel.NoteOf(supported)}')",
            WeaponPreviewViewModel.NoteOf(supported) == "Not simulated: view origin, gun angles."
            && WeaponPreviewViewModel.NoteOf(supported with { Note = "Kick return isn't simulated" }) == "Kick return isn't simulated"
            && WeaponPreviewViewModel.NoteOf(supported with { Supported = SimulatorParts.All }) is null
            && WeaponPreviewViewModel.NoteOf(default) is null && WeaponPreviewViewModel.NoteOf(SimulatorFrame.Failed("it failed")) == "it failed");
    }

    // ═══ The driver: cadence, taps, ADS, steps, rest ═════════════════════════

    /// <summary>A scripted simulation: by default a value that jumps on each round and decays (a spring at rest).</summary>
    private sealed class ScriptedSimulation : ISimulation
    {
        public readonly List<(float Dt, float Ads, uint Shots)> Steps = new();
        public Func<float, float, uint, SimulatorFrame>? Output;
        public int Resets;
        private float _v;

        public string ExtensionId => "scripted";
        public bool IsAlive { get; set; } = true;
        public void Reset() => Resets++;
        public void Dispose() => IsAlive = false;

        public SimulatorFrame Step(float dt, float ads, uint shots)
        {
            Steps.Add((dt, ads, shots));
            if (Output is { } f)
                return f(dt, ads, shots);
            _v = (_v + shots) * MathF.Exp(-dt * 25f);
            return new SimulatorFrame(true, SimulatorParts.All, new Vector3(-_v, 0, 0), default, default, default, "");
        }
    }

    private static void RecoilDriverChecks()
    {
        const float frame = 1 / 64f;
        int[] ShotSteps(ScriptedSimulation s) => s.Steps.Select((x, i) => (x, i)).Where(p => p.x.Shots > 0).Select(p => p.i).ToArray();

        var sim = new ScriptedSimulation();
        var d = new RecoilDriver { Simulation = sim, FireTime = 0.125f };
        Check("recoil driver: at rest nothing asks for frames", !d.NeedsFrames);
        d.PressTrigger();
        for (var i = 0; i < 64; i++)
            d.Step(frame);
        Check($"recoil driver: held for a second at an eighth of a second a round, it fires 8, the first at once, one every 8 frames ({string.Join(",", ShotSteps(sim))})",
            ShotSteps(sim).SequenceEqual(new[] { 0, 8, 16, 24, 32, 40, 48, 56 }) && sim.Steps.All(s => s.Shots <= 1) && d.Rounds == 8);
        d.ReleaseTrigger();
        var settle = 0;
        while (d.NeedsFrames && settle < 64 * 20)
        {
            d.Step(frame);
            settle++;
        }
        Check($"recoil driver: let go, it keeps stepping until the motion rests, then stops ({settle} frames, last {d.Frame.ViewAngles.X:0.000000})",
            !d.NeedsFrames && settle > 16 && settle < 64 * 3 && Math.Abs(d.Frame.ViewAngles.X) < 0.01f);

        sim = new ScriptedSimulation();
        d = new RecoilDriver { Simulation = sim, FireTime = 0.125f };
        d.PressTrigger();
        d.ReleaseTrigger();
        for (var i = 0; i < 32; i++)
            d.Step(frame);
        Check("recoil driver: a tap shorter than a round fires exactly one", ShotSteps(sim).SequenceEqual(new[] { 0 }) && d.Rounds == 1);
        d.PressTrigger();
        d.ReleaseTrigger();
        d.Step(frame);
        Check("recoil driver: a tap after the fire time fires at once", ShotSteps(sim).SequenceEqual(new[] { 0, 32 }));
        d.PressTrigger();
        d.ReleaseTrigger();
        for (var i = 0; i < 12; i++)
            d.Step(frame);
        Check($"recoil driver: a tap sooner than the fire time waits for it, never faster than the weapon ({string.Join(",", ShotSteps(sim))})",
            ShotSteps(sim).SequenceEqual(new[] { 0, 32, 40 }));

        sim = new ScriptedSimulation();
        d = new RecoilDriver { Simulation = sim, FireTime = 0.125f };
        d.PressTrigger();
        d.Step(1f);
        d.Step(0.0002f);
        d.Step(0.0002f);
        var sentAfterTiny = sim.Steps.Count;
        d.Step(0.0002f);
        Check($"recoil driver: a long frame is one step of {RecoilDriver.MaxStep} s, and tiny frames add up before one is sent ({sim.Steps[0].Dt}, {sim.Steps.Last().Dt:0.0000})",
            sim.Steps[0].Dt == RecoilDriver.MaxStep && sentAfterTiny == 1 && sim.Steps.Count == 2 && Math.Abs(sim.Steps[1].Dt - 0.0006f) < 1e-6f);

        sim = new ScriptedSimulation();
        d = new RecoilDriver { Simulation = sim, AdsInTime = 0.25f, AdsOutTime = 0f };
        d.AimDownSights = true;
        Check("recoil driver: switching to ADS asks for frames", d.NeedsFrames);
        for (var i = 0; i < 8; i++)
            d.Step(frame);
        var half = d.Ads;
        for (var i = 0; i < 8; i++)
            d.Step(frame);
        Check($"recoil driver: ADS moves over the weapon's adsTransInTime and the simulation is given it ({half}, {d.Ads}, sent {sim.Steps[7].Ads})",
            half == 0.5f && d.Ads == 1f && sim.Steps[7].Ads == 0.5f);
        d.AimDownSights = false;
        d.Step(frame);
        Check("recoil driver: a transition time of 0 is at once", d.Ads == 0f);

        var counter = 0f;
        sim = new ScriptedSimulation();
        sim.Output =(dt, ads, shots) => new SimulatorFrame(true, SimulatorParts.All, new Vector3(counter += 1, 0, 0), default, default, default, "");
        d = new RecoilDriver { Simulation = sim, FireTime = 0.125f };
        d.PressTrigger();
        d.ReleaseTrigger();
        var restless = 0;
        while (d.NeedsFrames && restless < 64 * 60)
        {
            d.Step(frame);
            restless++;
        }
        Check($"recoil driver: motion that never rests stops {RecoilDriver.MaxQuietSeconds} s after the last input ({restless * frame:0.00} s)",
            Math.Abs(restless * frame - RecoilDriver.MaxQuietSeconds) <= 2 * frame);

        sim = new ScriptedSimulation { Output = (_, _, _) => SimulatorFrame.Failed("no") };
        d = new RecoilDriver { Simulation = sim };
        d.PressTrigger();
        d.ReleaseTrigger();
        var failing = 0;
        while (d.NeedsFrames && failing < 640)
        {
            d.Step(frame);
            failing++;
        }
        Check($"recoil driver: failed steps don't keep it stepping ({failing} frames)", failing <= RecoilDriver.SettleSeconds / frame + 2);

        sim = new ScriptedSimulation();
        d = new RecoilDriver { Simulation = sim };
        d.PressTrigger();
        d.Step(frame);
        d.Reset();
        Check("recoil driver: Reset resets the simulation, lets go of the trigger and rests",
            sim.Resets == 1 && !d.IsTriggerHeld && d.Rounds == 0 && d.Frame == default && !d.NeedsFrames);
        var none = new RecoilDriver();
        none.PressTrigger();
        Check("recoil driver: without a simulation the trigger does nothing", !none.IsTriggerHeld && !none.NeedsFrames);
    }

    // ═══ Hip to ADS: the ADS-up anim layered on the idle per bone ══════════

    private static void RecoilBlendChecks()
    {
        static XMeshBaseMat Base(Vector3 t) => new(Quaternion.Identity, t, 2f);
        var skeleton = new AnimSkeleton
        {
            BoneNames = new[] { "tag_view", "tag_torso", "j_arm" },
            BaseMats = new[] { Base(default), Base(default), Base(new Vector3(1, 0, 0)) },
            ChildBones = new[]
            {
                new XMeshBone("tag_torso", -1, Quaternion.Identity, default),
                new XMeshBone("j_arm", 0, Quaternion.Identity, new Vector3(1, 0, 0)),
            },
        };
        WorldSpaceClip Clip(string[] parts, params Vector3[][] frames) =>
            new(parts, 30, frames.Select(f => new WorldSpaceFrame(f.Select(_ => Quaternion.Identity).ToArray(), f)).ToArray(), Array.Empty<XAnimNote>());
        var idle = Clip(new[] { "tag_torso", "j_arm" }, new[] { Vector3.Zero, new Vector3(10, -5, -5) }, new[] { Vector3.Zero, new Vector3(10, -5, -5) });
        var adsUp = Clip(new[] { "tag_torso" }, new[] { Vector3.Zero }, new[] { new Vector3(-8, 4, 0) });
        var blend = new AnimBlendClip(idle, adsUp);
        var mixed = blend.Bind(skeleton);
        var alone = idle.Bind(skeleton);
        alone.EvaluateWorld(0);
        mixed.EvaluateWorld(0);
        Check("recoil pose: at hip the pose is the idle anim's, bit for bit",
            mixed.World.ToArray().Zip(alone.World.ToArray()).All(p => p.First.Quat == p.Second.Quat && p.First.Trans == p.Second.Trans));
        blend.Weight = 1;
        mixed.EvaluateWorld(0);
        var torso = mixed.World[0].Trans;
        var arm = mixed.World[1].Trans;
        Check($"recoil pose: at ADS a bone the ADS-up anim keys takes its last frame, and the arms keep their idle pose on it (torso {torso}, arm {arm})",
            Vector3.Distance(torso, new Vector3(-8, 4, 0)) < 1e-4f && Vector3.Distance(arm, new Vector3(2, -1, -5)) < 1e-4f);
        blend.Weight = 0.5f;
        mixed.EvaluateWorld(0);
        Check($"recoil pose: halfway is halfway ({mixed.World[1].Trans})", Vector3.Distance(mixed.World[1].Trans, new Vector3(6, -3, -5)) < 1e-4f);
        var full = Clip(new[] { "tag_torso", "j_arm" }, new[] { new Vector3(1, 2, 3), new Vector3(4, 5, 6) });
        var whole = new AnimBlendClip(idle, full, 0f) { Weight = 1 }.Bind(skeleton);
        whole.EvaluateWorld(0);
        Check("recoil pose: an ADS anim that keys every bone is that anim at ADS",
            Vector3.Distance(whole.World[0].Trans, new Vector3(1, 2, 3)) < 1e-4f && Vector3.Distance(whole.World[1].Trans, new Vector3(4, 5, 6)) < 1e-4f);
    }

    // ═══ Resolving the viewmodel from the weapon's values as they are now ═════

    private static void RecoilResolveChecks()
    {
        var records = new Dictionary<(string, string), AssetRecord>();
        AssetRecord Add(string type, string name, string? parent = null, params (string Key, string Value)[] values)
        {
            var r = new AssetRecord { Name = name, Type = type, GdtName = "recoil.gdt", Parent = parent };
            foreach (var (k, v) in values)
                r.Properties[k] = v;
            records[(type.ToLowerInvariant(), name.ToLowerInvariant())] = r;
            return r;
        }
        AssetRecord? Resolve(string type, string name) =>
            records.GetValueOrDefault((type.ToLowerInvariant(), name.ToLowerInvariant()))
            ?? (type.EndsWith("weapon", StringComparison.OrdinalIgnoreCase) ? records.Values.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && r.Type.EndsWith("weapon")) : null);

        Add("xmodel", "gun_a");
        Add("xmodel", "gun_b");
        Add("xanim", "idle_a", null, ("filename", "idle_a.xanim_export"), ("model", "rig.xmodel_export"));
        Add("xanim", "idle_b");
        var ads = Add("xanim", "ads_a", null, ("type", "relative"));
        var parent = Add("bulletweapon", "w_parent", null, ("gunModel", "gun_a"), ("idleAnim", "idle_a"), ("adsUpAnim", "ads_a"), ("fireTime", "0.08"),
            ("adsTransInTime", "0.3"), ("attachViewModel1", "att"), ("attachViewModelTag1", "tag_att"), ("attachViewModel2", "att_alt"), ("attachViewModelTag2", "tag_att"));
        var child = Add("bulletweapon", "w_child", "w_parent", ("idleAnim", "idle_b"), ("viewmodelTag", "tag_gun"));

        var r = WeaponPreviewSource.Resolve(child, Resolve);
        var p = r.Plan;
        Check($"recoil resolve: a derived weapon reads its own idleAnim and inherits the gun, ADS anim, times and attachments ({p?.Viewmodel.GunModel}, {p?.IdleAnim}, {p?.AdsAnim}, {p?.FireTime})",
            p is { IdleAnim: "idle_b", AdsAnim: "ads_a", FireTime: 0.08f, AdsInTime: 0.3f, AdsOutTime: 0f, Hands: null }
            && p.Viewmodel is { GunModel: "gun_a", GunTag: "tag_gun" } && p.Viewmodel.Attachments.Single() == new ViewmodelAttachment("att", "tag_att"));

        parent.Properties["gunModel"] = "gun_b";
        var edited = WeaponPreviewSource.Resolve(child, Resolve).Plan;
        Check("recoil resolve: an edit to the parent in this session is seen at once (no index to go stale)",
            edited?.Viewmodel.GunModel == "gun_b" && edited.Key != p!.Key);
        ads.Properties["type"] = "additive";
        Check("recoil resolve: an edit to the ADS anim's own values changes the plan", WeaponPreviewSource.Resolve(child, Resolve).Plan!.Key != edited!.Key);
        Check("recoil resolve: an edit to a key the preview doesn't read changes nothing",
            WeaponPreviewSource.Resolve(child, Resolve).Plan!.Key == WeaponPreviewSource.Resolve(child, Resolve).Plan!.Key && !WeaponPreviewSource.Reads("damage")
            && WeaponPreviewSource.Reads("attachViewModelTag3") && WeaponPreviewSource.Reads("ADSUPANIM"));

        string? Problem(params (string Key, string Value)[] values) =>
            WeaponPreviewSource.Resolve(Add("bulletweapon", "w_probe", null, values), Resolve).Problem;
        Check($"recoil resolve: each missing piece is one plain sentence and no gun to draw ('{Problem(("idleAnim", "idle_a"))}')",
            Problem(("idleAnim", "idle_a")) == "No gun to draw: set gunModel to see it."
            && Problem(("gunModel", "gun_x"), ("idleAnim", "idle_a")) == "No gun to draw: gunModel is gun_x, which isn't in a loaded GDT."
            && Problem(("gunModel", "gun_a")) == "No gun to draw: set idleAnim, the anim the gun is held in at hip."
            && Problem(("gunModel", "gun_a"), ("idleAnim", "idle_x")) == "No gun to draw: idleAnim is idle_x, which isn't in a loaded GDT.");
        var timing = WeaponPreviewSource.Resolve(Add("bulletweapon", "w_probe", null, ("fireTime", "0.2"), ("adsTransInTime", "0.3")), Resolve).Timing;
        Check($"recoil resolve: with no gun the timing is still the weapon's, so Fire keeps its cadence ({timing})",
            timing == new WeaponTiming(0.2f, 0.3f, 0f));
        var noAds = WeaponPreviewSource.Resolve(Add("bulletweapon", "w_probe", null, ("gunModel", "gun_a"), ("idleAnim", "idle_a"), ("fireTime", "0")), Resolve).Plan;
        var badAds = WeaponPreviewSource.Resolve(Add("bulletweapon", "w_probe", null, ("gunModel", "gun_a"), ("idleAnim", "idle_a"), ("adsUpAnim", "ads_x")), Resolve).Plan;
        Check($"recoil resolve: without an ADS anim it still previews, says ADS keeps the hip pose, and a fire time of 0 reads as 0.1 s ('{noAds?.Notes.FirstOrDefault()}')",
            noAds is { AdsAnim: null, FireTime: 0.1f } && noAds.Notes.Single() == "No adsUpAnim: ADS keeps the hip pose."
            && badAds is { AdsAnim: null } && badAds.Notes.Single() == "adsUpAnim ads_x isn't in a loaded GDT: ADS keeps the hip pose.");
    }

    // ═══ The app: a temp install, the fixture module, real input ═══════════════

    private const string RecoilGdt = "{\r\n"
        + "\t\"apex_recoil_gun\" ( \"xmodel.gdf\" )\r\n\t{\r\n\t\t\"filename\" \"apex_recoil_gun.xmodel_export\"\r\n\t}\r\n"
        + "\t\"apex_recoil_idle\" ( \"xanim.gdf\" )\r\n\t{\r\n\t\t\"filename\" \"apex_recoil_idle.xanim_export\"\r\n\t\t\"model\" \"apex_rig.xmodel_export\"\r\n\t\t\"type\" \"relative\"\r\n\t}\r\n"
        + "\t\"apex_recoil_ads\" ( \"xanim.gdf\" )\r\n\t{\r\n\t\t\"filename\" \"apex_recoil_ads.xanim_export\"\r\n\t\t\"model\" \"apex_rig.xmodel_export\"\r\n\t\t\"type\" \"relative\"\r\n\t}\r\n"
        + "\t\"apex_recoil_base\" ( \"bulletweapon.gdf\" )\r\n\t{\r\n\t\t\"gunModel\" \"apex_recoil_gun\"\r\n\t\t\"idleAnim\" \"apex_recoil_idle\"\r\n"
        + "\t\t\"adsUpAnim\" \"apex_recoil_ads\"\r\n\t\t\"fireTime\" \"0.125\"\r\n\t\t\"adsTransInTime\" \"0.25\"\r\n\t}\r\n"
        + "\t\"apex_recoil_child\" [ \"apex_recoil_base\" ]\r\n\t{\r\n\t\t\"displayName\" \"Recoil child\"\r\n\t}\r\n"
        + "\t\"apex_recoil_plain\" ( \"bulletweapon.gdf\" )\r\n\t{\r\n\t\t\"gunModel\" \"apex_recoil_gun\"\r\n\t\t\"idleAnim\" \"apex_recoil_idle\"\r\n\t}\r\n"
        + "\t\"apex_recoil_noidle\" ( \"bulletweapon.gdf\" )\r\n\t{\r\n\t\t\"gunModel\" \"apex_recoil_gun\"\r\n\t}\r\n"
        + "}\r\n";

    /// <summary>A temp install with deffiles and the recoil GDT; the extension (fixture module) and answers in scratch.</summary>
    private static (string Install, string Ext, string Settings, string Dll)? RecoilInstall(string label, string dll = "sim-fixture.dll")
    {
        if (!Directory.Exists(Path.Combine(InstallRoot, "deffiles")))
            return null;
        var install = NewScratch(label + "-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        Directory.CreateDirectory(Path.Combine(install, "source_data"));
        File.WriteAllText(Path.Combine(install, "source_data", "apex_recoil.gdt"), RecoilGdt);
        // apex_recoil_noidle has the extension on in its own block: its preview must say why it can't draw and never ask.
        File.WriteAllText(Path.Combine(install, "source_data", "apex_recoil.gdtx"),
            "{\r\n\t\"apex_recoil_noidle\" ( \"sim-fixture\" )\r\n\t{\r\n\t\t\"fxEnabled\" \"1\"\r\n\t}\r\n}\r\n");
        var (ext, folder) = SimExtension(label + "-ext", dll);
        return (install, ext, NewScratch(label + "-settings"), Path.Combine(folder, "sim-fixture.dll"));
    }

    private static (MainViewModel Vm, MainWindow Window)? StartRecoilApp((string Install, string Ext, string Settings, string Dll) setup)
    {
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", setup.Install);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, setup.Ext);
        Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", setup.Settings);
        ExtensionRegistry.Clear();
        var vm = new MainViewModel(Path.Combine(setup.Install, "session"));
        var window = ShowJournalWindow(vm);
        // A second start on the same install restores the first one's session, which replaces "Loaded…" in the same step.
        WaitUntil(() => vm.Status.StartsWith("Loaded") || vm.Status.StartsWith("Restored"), 60_000);
        return (vm, window);
    }

    private static void StopRecoilApp(MainViewModel? vm, Window? window, (string? Mock, string? Root, string? Persist) saved)
    {
        window?.Close();
        vm?.Dispose();
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, NewScratch("no-extensions"));
        ExtensionRegistry.Clear();
        SchemaRegistry.ResetToMock();
    }

    private static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name && c.IsEffectivelyVisible);

    /// <summary>Display frames: the headless render timer, with the dispatcher run between them.</summary>
    private static void Frames(int count, int ms = 16)
    {
        for (var i = 0; i < count; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Pump(ms);
        }
    }

    private static void RecoilAppChecks(string outDir)
    {
        if (RecoilInstall("recoil-app") is not { } setup)
        {
            Console.WriteLine("recoil app: BO3 deffiles not found — skipped");
            return;
        }
        var saved = (Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        MainViewModel? vm = null;
        MainWindow? window = null;
        try
        {
            (vm, window) = StartRecoilApp(setup)!.Value;
            Check($"recoil app: Apex loaded the temp install live ('{vm.Status}')", vm.Status.StartsWith("Loaded") && !vm.IsMockData);

            // A weapon with no extension module on has no preview of its own, exactly as before.
            vm.OpenByName("apex_recoil_plain");
            Pump(300);
            Check($"recoil app: a weapon its extension is off for has no preview of its own (subjects: {string.Join(", ", vm.PreviewSubjects.Select(s => s.Label))})",
                vm.ActiveTab!.PreviewPane is null && vm.PreviewSubjects.All(s => s.Label != "This asset") && !IsLoaded(setup.Dll) && !vm.IsConfirmOpen);

            // On, but no gun to draw: one sentence on the viewport, and the module is asked for in place all the same
            // (the overlay carries the preview).
            vm.OpenByName("apex_recoil_noidle");
            Pump(300);
            var noIdle = vm.ActiveTab!.PreviewPane?.Content as WeaponPreviewViewModel;
            Check($"recoil app: a weapon with no idleAnim says there's no gun to draw and asks for the module in its preview, not in a dialog ('{noIdle?.Error}')",
                noIdle is { ShowError: true, HasPlan: false, CanFire: false, Question: not null } && noIdle.Error == "No gun to draw: set idleAnim, the anim the gun is held in at hip."
                && !vm.IsConfirmOpen && !IsLoaded(setup.Dll));

            vm.OpenByName("apex_recoil_base");
            var tab = vm.ActiveTab!;
            PropertyItemViewModel Row(string key) => tab.AllSentinel.All.First(r => r.Key == key);
            Pump(300);
            Check("recoil app: the extension off, the weapon has no preview and nothing is asked", tab.PreviewPane is null && !vm.IsConfirmOpen);
            // The keyboard is in the Explorer's search when the question appears: it stays there.
            var search = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "SearchBox");
            search.Focus();
            Pump();
            Row("fxEnabled").RawValue = "1";
            Row("fxGain").RawValue = "-2";
            Pump(400);
            var recoil = tab.PreviewPane?.Content as WeaponPreviewViewModel;
            Check($"recoil app: switched on, the weapon's preview is the recoil preview and the module's one question is in it ('{recoil?.Question?.Title}')",
                recoil is not null && vm.CurrentPreview == tab.PreviewPane && vm.PreviewSubjects.First().Label == "This asset"
                && !vm.IsConfirmOpen && recoil.Question is { Title: "Load sim-fixture's preview module?", FileName: "sim-fixture.dll", Change: null }
                && ReferenceEquals(recoil.Question, noIdle!.Question) && recoil.Notice is null
                && window.GetVisualDescendants().OfType<ScrollViewer>().Any(s => s.Name == "ModuleQuestionPanel" && s.IsEffectivelyVisible));
            Check($"recoil app: the question appearing leaves the keyboard where it was ({(window.FocusManager?.GetFocusedElement() as Control)?.Name})",
                ReferenceEquals(window.FocusManager?.GetFocusedElement(), search));
            Check($"recoil app: without APE's renderer data the viewmodel says why, and the controls still show ('{recoil?.Error}')",
                recoil is { HasPlan: true, Error: WeaponPreviewViewModel.CantDraw } && recoil.ErrorDetail?.Length > 0);
            Capture(window, Path.Combine(outDir, "94-recoil-question.png"));
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Pump(100);
            Capture(window, Path.Combine(outDir, "95-recoil-question-light.png"));
            Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Pump();
            // A click into the preview puts the keyboard there; Esc is the default answer, Don't load.
            RecoilRoot(window, recoil!).Focus();
            Pump();
            Key(window, K.Escape);
            Pump(100);
            Check($"recoil app: Don't load: the preview says so plainly and offers Load module…, and Fire is off ('{recoil!.Notice}')",
                recoil.Notice == "The sim-fixture preview module isn't loaded: you chose not to load it." && recoil.CanLoadModule && !recoil.CanFire
                && Named<Button>(window, "LoadModuleButton").IsVisible && !Named<Button>(window, "FireButton").IsEnabled);
            Capture(window, Path.Combine(outDir, "90-recoil-declined.png"));
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Pump(100);
            Capture(window, Path.Combine(outDir, "91-recoil-declined-light.png"));
            Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Pump();

            // Load module… with a real click; an edit while the question is open makes a newer ask, and only it lands.
            Click(window, Named<Button>(window, "LoadModuleButton"), MouseButton.Left);
            WaitUntil(() => recoil.Question is not null, 3000);
            Pump(50);
            Check($"recoil app: Load module… asks again in place, and the keyboard (in the preview) lands on Don't load ({(window.FocusManager?.GetFocusedElement() as Control)?.Name})",
                recoil.Question is not null && !vm.IsConfirmOpen && (window.FocusManager?.GetFocusedElement() as Control)?.Name == "DontLoadButton");
            Row("fxGain").RawValue = "-3";
            Pump(400);
            Key(window, K.Tab);
            var loadFocused = (window.FocusManager?.GetFocusedElement() as Control)?.Name;
            Key(window, K.Enter);
            WaitUntil(() => recoil.CanFire, 5000);
            Pump(100);
            // Two previews wait on the module: this weapon's (one simulation, for the latest values) and apex_recoil_noidle's.
            Check($"recoil app: Tab reaches Load ({loadFocused}) and Enter loads: the module simulates the weapon, one simulation alive per preview (alive {FixtureCount(setup.Dll, "fixture_live")})",
                loadFocused == "LoadButton" && recoil.CanFire && recoil.Notice is null && !recoil.CanLoadModule && recoil.Question is null
                && FixtureCount(setup.Dll, "fixture_live") == 2 && noIdle!.CanFire);
            Check($"recoil app: answered from the keyboard, the question goes and the keyboard is back in the preview ({(window.FocusManager?.GetFocusedElement() as Control)?.GetType().Name})",
                window.FocusManager?.GetFocusedElement() is Control { } f && (ReferenceEquals(f, RecoilRoot(window, recoil)) || f.GetVisualAncestors().Contains(RecoilRoot(window, recoil))));

            RecoilPaneSizeCheck(window, recoil);
            RecoilNoGunCheck(vm, window, outDir);

            // Idle: no frames burn, and the overlay draws nothing.
            Frames(30);
            var steppedBefore = recoil.FramesStepped;
            var drawnBefore = Named<Apex.Editor.Controls.RecoilOverlay>(window, "RecoilOverlay").Renders;
            Frames(5);
            Check($"recoil app: at rest the preview asks for no frames and the overlay draws nothing ({Named<Apex.Editor.Controls.RecoilOverlay>(window, "RecoilOverlay").Renders - drawnBefore} draws)",
                !recoil.IsTicking && recoil.FramesStepped == steppedBefore && Named<Apex.Editor.Controls.RecoilOverlay>(window, "RecoilOverlay").Renders == drawnBefore);

            // Space held on the preview fires at the weapon's fire time; its release lets go.
            var root = window.GetVisualDescendants().OfType<DockPanel>().First(p => p.DataContext == recoil && p.Classes.Contains("animroot"));
            root.Focus();
            Pump();
            window.KeyPress(K.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Pump();
            var clock = Stopwatch.StartNew();
            Frames(20, 25);
            window.KeyPress(K.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Frames(4, 25);
            var held = recoil.Driver.IsTriggerHeld;
            var heldFor = clock.Elapsed.TotalSeconds;
            window.KeyRelease(K.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Pump();
            var rounds = recoil.Driver.Rounds;
            var expected = (int)(heldFor / 0.125) + 1;
            Check($"recoil app: Space held fires at fireTime 0.125 s ({rounds} rounds in {heldFor:0.00} s; a repeat isn't a new press) and its release lets go",
                held && !recoil.Driver.IsTriggerHeld && rounds >= Math.Max(2, expected - 2) && rounds <= expected + 1 && recoil.ShotsText == $"{rounds}");
            Check($"recoil app: the burst is on the overlay, a dot a round, and the readout says how far it climbed ({recoil.Trace.DotCount} dots, climb {recoil.ClimbText})",
                recoil.Trace.DotCount == rounds && recoil.ClimbText == WeaponPreviewViewModel.Degrees(recoil.Trace.Climb) && recoil.Trace.Climb > 0
                && recoil.SettleText == WeaponPreviewViewModel.NoValue && recoil.MeasureNote is null);
            var frame = recoil.Driver.Frame;
            var kick = recoil.Kick;
            Check($"recoil app: the viewport gets exactly the module's frame (pitch {frame.ViewAngles.X} from fxGain -3, rounds {frame.ViewAngles.Z})",
                frame.Ok && kick == WeaponPreviewViewModel.KickOf(frame) && frame.ViewAngles.X < 0 && frame.ViewAngles.Z == rounds);
            Check("recoil app: with the fixture module a negative pitch turns the view up (forward gains +Z)",
                kick is { } k && Vector3.TransformNormal(Vector3.UnitX, k.View).Z > 0);

            // A on the preview is ADS, R resets; the ADS segment is checked, Hip isn't.
            Key(window, K.A);
            Frames(2);
            var hip = Named<Button>(window, "HipToggle");
            var ads = Named<Button>(window, "AdsToggle");
            static bool Shown(Button segment) => segment.Classes.Contains("checked");
            Check($"recoil app: A switches to ADS (the ADS segment shows checked, Hip doesn't) and the module is given the fraction ({recoil.Driver.Ads:0.00})",
                recoil.IsAds && Shown(ads) && !Shown(hip) && recoil.Driver.Ads > 0);
            Key(window, K.R);
            // The fixture's roll is the rounds since its reset; ADS is still moving in, so it may have stepped since.
            Check($"recoil app: R resets the module to a fresh burst and clears the overlay and readout ({recoil.Driver.Rounds}, {recoil.Kick?.ViewAngles}, '{recoil.ShotsText}', '{recoil.ClimbText}')",
                recoil.Driver.Rounds == 0 && (recoil.Kick?.ViewAngles.Z ?? 0) == 0 && recoil.ShotsText == "0" && recoil.ClimbText == WeaponPreviewViewModel.NoValue
                && recoil.Trace is { DotCount: 0, TrailCount: 0, HasBurst: false });
            Click(window, hip, MouseButton.Left);
            Check("recoil app: a click on Hip goes back to hip, and the pair shows it", !recoil.IsAds && Shown(hip) && !Shown(ads));
            Click(window, hip, MouseButton.Left);
            Check("recoil app: a second click on the checked Hip keeps it checked (one segment always shows checked)", !recoil.IsAds && Shown(hip) && !Shown(ads));
            Click(window, ads, MouseButton.Left);
            Click(window, ads, MouseButton.Left);
            Check("recoil app: two clicks on ADS leave ADS checked, Hip not", recoil.IsAds && Shown(ads) && !Shown(hip));
            Click(window, hip, MouseButton.Left);

            // The Fire button: held by the mouse it fires; Enter on it is one round.
            var fire = Named<Button>(window, "FireButton");
            var at = fire.TranslatePoint(new Point(fire.Bounds.Width / 2, fire.Bounds.Height / 2), window)!.Value;
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Left);
            Pump();
            var pressedHolds = recoil.Driver.IsTriggerHeld;
            Frames(10, 25);
            window.MouseUp(at, MouseButton.Left);
            Pump(50);
            var mouseRounds = recoil.Driver.Rounds;
            Check($"recoil app: the Fire button held by the mouse is the trigger ({mouseRounds} rounds), and its click isn't one more",
                pressedHolds && !recoil.Driver.IsTriggerHeld && mouseRounds >= 2);
            System.Threading.Thread.Sleep(200);
            fire.Focus();
            Pump();
            Key(window, K.R);
            Key(window, K.Enter);
            Frames(3);
            Check($"recoil app: Enter on the focused Fire button fires one round: one dot, one shot ({recoil.Driver.Rounds}, {recoil.Trace.DotCount}, '{recoil.ShotsText}')",
                recoil.Driver.Rounds == 1 && !recoil.Driver.IsTriggerHeld && recoil.Trace.DotCount == 1 && recoil.ShotsText == "1");
            Capture(window, Path.Combine(outDir, "92-recoil-firing.png"));
            Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            Pump(100);
            Capture(window, Path.Combine(outDir, "93-recoil-firing-light.png"));
            Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Pump();
            RecoilAdsReadoutCheck(window, recoil, outDir);

            // An edit remakes the simulation from the new values (they are a copy), after the edits settle.
            var simBefore = recoil.Driver.Simulation;
            Row("fxGain").RawValue = "-5";
            Row("fxGain").RawValue = "-6";
            Pump(400);
            recoil.FireOnce();
            Frames(2);
            Check($"recoil app: edits remake the simulation once they settle, from the latest values (gun pitch {recoil.Driver.Frame.GunAngles.X} = 2 × fxGain, alive {FixtureCount(setup.Dll, "fixture_live")})",
                !ReferenceEquals(simBefore, recoil.Driver.Simulation) && simBefore?.IsAlive == false
                && recoil.Driver.Frame.GunAngles.X == -12 && FixtureCount(setup.Dll, "fixture_live") == 2);

            // What the module doesn't simulate is said beside the preview, and not drawn.
            Row("fxMode").RawValue = "unsupported";
            Row("fxNote").RawValue = "Only the view kick here";
            Pump(400);
            recoil.FireOnce();
            Frames(2);
            Check($"recoil app: the module's note is shown and only the parts it computed are drawn ('{recoil.Notice}', {recoil.Kick})",
                recoil.Notice == "Only the view kick here" && recoil.Kick is { ViewOrigin: var vo, GunAngles: var ga, GunOrigin: var go } && vo == default && ga == default && go == default);
            RecoilNoViewKickCheck(window, recoil, outDir);

            Row("fxMode").RawValue = "create-fail";
            Row("fxNote").RawValue = "no kick sets";
            Pump(400);
            Check($"recoil app: a weapon the module can't simulate says why, without Load module… ('{recoil.Notice}')",
                recoil.Notice == "sim-fixture can't preview this weapon: no kick sets" && !recoil.CanLoadModule && !recoil.CanFire);

            Row("fxMode").RawValue = "step-fail";
            Row("fxNote").RawValue = "";
            Pump(400);
            recoil.PressTrigger();
            Frames(14);
            recoil.ReleaseTrigger();
            Pump(100);
            Check($"recoil app: a module that keeps failing is turned off and the preview says so ('{recoil.Notice}')",
                !recoil.CanFire && recoil.Notice == "Apex turned off the sim-fixture preview module until it restarts. Editing and saving work as usual.");

            // Off again: the preview goes, and its simulation with it.
            Row("fxEnabled").RawValue = "0";
            Pump(400);
            Check($"recoil app: switched off, the weapon has no preview of its own again, and its simulation is gone (subjects: {string.Join(", ", vm.PreviewSubjects.Select(s => s.Label))})",
                tab.PreviewPane is null && vm.PreviewSubjects.All(s => s.Label != "This asset") && FixtureCount(setup.Dll, "fixture_live") == 0);
            Check("recoil pane: with no recoil preview the Inspector is back under the Preview", InspectorShown(window));
            vm.UndoActiveCommand.Execute(null);
            Pump(400);
            Check("recoil app: Ctrl+Z switching it back on brings the preview back", tab.PreviewPane?.Content is WeaponPreviewViewModel);

            // A derived weapon follows its parent's switch and resolves the parent's gun and anims.
            vm.OpenByName("apex_recoil_child");
            Pump(400);
            var childRecoil = vm.ActiveTab!.PreviewPane?.Content as WeaponPreviewViewModel;
            Check($"recoil app: a derived weapon inherits the switch and its parent's viewmodel (fire time {childRecoil?.Driver.FireTime})",
                childRecoil is { HasPlan: true } && childRecoil.Driver.FireTime == 0.125f);
        }
        catch (Exception ex)
        {
            Check($"recoil app: {ex}", false);
        }
        finally
        {
            StopRecoilApp(vm, window, saved);
        }
        RecoilChangedModuleCheck(setup, outDir);

        // A module that can't be used: the preview offers to try again.
        if (RecoilInstall("recoil-failed", "sim-fixture-x86.dll") is not { } broken)
            return;
        try
        {
            (vm, window) = StartRecoilApp(broken)!.Value;
            vm.OpenByName("apex_recoil_base");
            vm.ActiveTab!.AllSentinel.All.First(r => r.Key == "fxEnabled").RawValue = "1";
            Pump(400);
            var recoil = vm.ActiveTab.PreviewPane?.Content as WeaponPreviewViewModel;
            AnswerQuestion(window, recoil!, load: true);
            Pump(300);
            Check($"recoil app: a module that can't load says so beside the preview and offers Load module… ('{recoil?.Notice}')",
                recoil is { CanFire: false, CanLoadModule: true } && recoil.Notice!.StartsWith("The sim-fixture preview is off: its module sim-fixture.dll isn't a 64-bit Windows DLL"));
        }
        catch (Exception ex)
        {
            Check($"recoil app: failed module: {ex}", false);
        }
        finally
        {
            StopRecoilApp(vm, window, saved);
        }
    }

    // ═══ The renderer: no recoil is the plain frame, byte for byte ═══════════

    private static void RecoilRenderChecks()
    {
        // Real D3D11 and APE's data, read-only: skipped without the install or a device (the harness on a machine without BO3).
        var install = Apex.Render.Data.ToolsGfxInstall.FromRoot(InstallRoot);
        if (!install.IsAvailable || !File.Exists(Path.Combine(InstallRoot, "source_data", "ar_an94.gdt")))
        {
            Console.WriteLine("recoil render: BO3 install not found — skipped");
            return;
        }
        Apex.Render.Device.GfxDevice gfx;
        try
        {
            gfx = Apex.Render.Device.GfxDevice.CreateDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"recoil render: no D3D11 device ({ex.Message}) — skipped");
            return;
        }
        var gdts = NewScratch("recoil-render-gdt");
        File.Copy(Path.Combine(InstallRoot, "source_data", "ar_an94.gdt"), Path.Combine(gdts, "ar_an94.gdt"));
        var data = new Apex.Render.Data.ToolsGfxData(install, new Apex.Render.Data.Gdt.GdtIndex(gdts));
        var env = PreviewEnvironment.Load(data);
        var model = PreviewModelLoader.Prepare(data, "wpn_t7_loot_ar_an94_view", new PreviewModelOptions { SkipBrokenMaterials = true });
        const int size = 256;
        using var texture = gfx.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
        {
            Width = size, Height = size, ArraySize = 1, MipLevels = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource,
        });
        using var rtv = gfx.Device.CreateRenderTargetView(texture);
        var renderer = new PreviewSceneRenderer(env) { LightState = env.Lighting.States[0].State };
        renderer.OnDeviceCreated(gfx);
        renderer.Model = model;
        var center = (model.BoundsMin + model.BoundsMax) * 0.5f;
        renderer.Camera = new PreviewCamera(center - new Vector3(60, 0, -10), 10, 0);

        byte[] Draw()
        {
            renderer.Render(new D3DFrame(gfx, texture, rtv, Vortice.DXGI.Format.B8G8R8A8_UNorm, size, size, 1, TimeSpan.Zero));
            return Apex.Render.Offscreen.Readback.Read(gfx, texture).Data;
        }
        // The model's GPU copy and the sky arrive asynchronously: draw until two frames in a row agree.
        var previous = Draw();
        var settled = false;
        for (var i = 0; i < 200 && !settled; i++)
        {
            Pump(20);
            var next = Draw();
            settled = renderer.Stats.Triangles > 0 && next.AsSpan().SequenceEqual(previous) && env.IsSkyLoaded(renderer.LightState);
            previous = next;
        }
        if (!settled)
        {
            Check("recoil render: the model drew and the frame settled", false);
            return;
        }
        double MedianCpu()
        {
            var times = new List<double>();
            for (var i = 0; i < 31; i++)
            {
                renderer.Render(new D3DFrame(gfx, texture, rtv, Vortice.DXGI.Format.B8G8R8A8_UNorm, size, size, 1, TimeSpan.Zero));
                times.Add(renderer.Stats.CpuMilliseconds);
            }
            times.Sort();
            return times[15];
        }
        var plain = Draw();
        var cpuPlain = MedianCpu();
        renderer.Kick = new PreviewKick();
        var zero = Draw();
        renderer.Kick = new PreviewKick(new Vector3(-2, 1, 0), new Vector3(-1, 0, 0), new Vector3(-5, 0, 1), new Vector3(-0.5f, 0, 0));
        var kicked = Draw();
        var cpuKicked = MedianCpu();
        renderer.Kick = null;
        var after = Draw();
        Check("recoil render: a frame with no recoil (no kick, or a kick of zeros) is byte-identical to the plain frame, before and after a kick",
            zero.AsSpan().SequenceEqual(plain) && after.AsSpan().SequenceEqual(plain));
        Check("recoil render: a kick changes the frame", !kicked.AsSpan().SequenceEqual(plain));

        // The spray overlay is UI over the viewport: drawing it (a burst's dots, path and reticle) between two frames
        // changes neither, with or without a kick.
        var spray = new SprayTrace();
        for (var i = 0; i < 40; i++)
            spray.Add(1 / 60f, i % 6 == 0 ? 1u : 0u, new SimulatorFrame(true, SimulatorParts.All, new Vector3(-0.1f * i, 0.03f * i, 0), default, default, default, ""));
        var (overlayWindow, overlay) = OverlayWindow(spray, size);
        try
        {
            renderer.Kick = null;
            var overlayDrawn = overlayWindow.CaptureRenderedFrame();
            var plainWithOverlay = Draw();
            renderer.Kick = new PreviewKick(new Vector3(-2, 1, 0), new Vector3(-1, 0, 0), new Vector3(-5, 0, 1), new Vector3(-0.5f, 0, 0));
            overlayWindow.CaptureRenderedFrame();
            var kickedWithOverlay = Draw();
            Check($"recoil render: the frame is byte-identical with the spray overlay drawn over it or not, plain and kicked ({overlay.Renders} overlay draws, {spray.DotCount} dots)",
                overlayDrawn is not null && overlay.Renders > 0 && spray.DotCount == 7
                && plainWithOverlay.AsSpan().SequenceEqual(plain) && kickedWithOverlay.AsSpan().SequenceEqual(kicked));
            renderer.Kick = null;
        }
        finally
        {
            overlayWindow.Close();
        }
        Console.WriteLine($"info  recoil render: frame CPU median of 31, {cpuPlain:0.00} ms plain, {cpuKicked:0.00} ms kicked ({gfx.AdapterName})");
        renderer.OnDeviceDestroyed();
        gfx.Dispose();
    }
}
