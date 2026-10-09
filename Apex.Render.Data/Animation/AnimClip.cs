using System.Numerics;
using Apex.Render.Data.Assets;

namespace Apex.Render.Data.Animation;

/// <summary>GDT xanim <c>type</c>, in APE's table order (<c>XAnim_TypeFromString</c>, 0x1406341A0).</summary>
public enum XAnimType
{
    Absolute = 0,
    Relative = 1,
    Delta = 2,
    Delta3d = 3,
    Additive = 4,
    MpTorso = 5,
    MpLegs = 6,
    MpFullbody = 7,
}

public static class XAnimTypes
{
    private static readonly string[] Names = ["absolute", "relative", "delta", "delta3d", "additive", "mp_torso", "mp_legs", "mp_fullbody"];

    /// <summary>The GDT spelling (<c>"delta"</c>, …).</summary>
    public static string ToGdt(this XAnimType type) => Names[(int)type];

    /// <summary>Parses a GDT <c>type</c> value (case-sensitive like APE); null for unknown values.</summary>
    public static XAnimType? Parse(string? value)
    {
        int i = Array.IndexOf(Names, value?.Trim() ?? "");
        return i < 0 ? null : (XAnimType)i;
    }

    /// <summary>
    /// Types whose root motion (the cache's delta channel) moves the root in the preview:
    /// <c>Anim_EvaluateLocalToWorld</c> tests <c>((type - 2) &amp; ~4) == 0 &amp;&amp; type != 6</c>.
    /// </summary>
    public static bool HasRootMotion(this XAnimType type) => type is XAnimType.Delta or XAnimType.Delta3d or XAnimType.MpFullbody;
}

/// <summary>
/// An animation that can drive a model skeleton in APE's preview: frame count and rate (duration = frames / fps),
/// notetracks, and <see cref="Bind"/> to a skeleton, which matches bones by name.
/// </summary>
public abstract class AnimClip
{
    /// <summary>Frames per second.</summary>
    public abstract float Framerate { get; }

    /// <summary>Frame count N as APE holds it (<c>XAnim+0x4C</c>): looping anims exclude the closing copy of frame 0.</summary>
    public abstract int FrameCount { get; }

    public abstract IReadOnlyList<XAnimNote> Notetracks { get; }

    /// <summary><c>XAnim_GetDuration</c>: <c>(float)N / fps</c> seconds.</summary>
    public float Duration => Framerate > 0 ? (float)FrameCount / Framerate : 0f;

    /// <summary>Binds the clip to a skeleton (bone matching by name).</summary>
    public abstract AnimPose Bind(AnimSkeleton skeleton);
}

/// <summary>A clip bound to one skeleton: evaluates the b11 bone buffer (xmesh bone order) at a normalised time.</summary>
public abstract class AnimPose
{
    protected AnimPose(AnimSkeleton skeleton, AnimClip clip)
    {
        Skeleton = skeleton;
        Clip = clip;
    }

    public AnimSkeleton Skeleton { get; }
    public AnimClip Clip { get; }

    /// <summary>Model bones the clip drives (matched by name).</summary>
    public abstract int MatchedBones { get; }

    /// <summary>
    /// True when the clip has data for skeleton bone <paramref name="bone"/> (bones below <see cref="AnimSkeleton.RootCount"/>
    /// are the root); a bone it lacks only follows its parent. The game layers anims per bone this way: an anim that
    /// lacks a bone leaves it to the others (an ADS-up anim that keys only tag_torso moves the arms, not their pose).
    /// </summary>
    public virtual bool Drives(int bone) => true;

    /// <summary>
    /// Writes <see cref="AnimSkeleton.BoneCount"/> (at most 1024) bones of 16 floats — <c>objMatrixT</c> rows then
    /// <c>extra</c> — for normalised time <paramref name="t"/> (0..1 over the clip; see <see cref="AnimClock"/>).
    /// </summary>
    public void Evaluate(float t, Span<float> bones)
    {
        EvaluateWorld(t);
        WriteSkin(Skeleton, Root, World, Extra, bones);
    }

    /// <summary>
    /// Evaluates the model-space pose at normalised time <paramref name="t"/> into <see cref="Root"/> and
    /// <see cref="World"/> (<c>Anim_EvaluateLocalToWorld</c>) without building skinning matrices.
    /// </summary>
    public abstract void EvaluateWorld(float t);

    /// <summary>The root transform (bones below <see cref="AnimSkeleton.RootCount"/>) of the last <see cref="EvaluateWorld"/>.</summary>
    public AnimMat Root { get; protected set; } = AnimMat.Identity;

    /// <summary>Model-space transform per child bone (<see cref="AnimSkeleton.ChildBones"/> order) of the last
    /// <see cref="EvaluateWorld"/>.</summary>
    public abstract ReadOnlySpan<AnimMat> World { get; }

    /// <summary>The extra channel per child bone of the last <see cref="EvaluateWorld"/> (empty = zeros).</summary>
    public virtual ReadOnlySpan<Vector4> Extra => ReadOnlySpan<Vector4>.Empty;

    /// <summary>
    /// <c>Anim_BuildSkinMatrices</c> (0x140481700): bones below <see cref="AnimSkeleton.RootCount"/> take the root
    /// transform as is; every other bone i gets <c>inverse(base[i]) · world[i - rootCount]</c> and its extra channel.
    /// </summary>
    public static void WriteSkin(AnimSkeleton sk, in AnimMat root, ReadOnlySpan<AnimMat> world, ReadOnlySpan<Vector4> extra, Span<float> bones)
    {
        int count = Math.Min(sk.BoneCount, AnimBoneLayout.MaxBones);
        int roots = sk.RootCount;
        var inverseBase = sk.InverseBaseMats;
        for (int i = 0; i < count; i++)
        {
            var dst = bones.Slice(i * AnimBoneLayout.FloatsPerBone, AnimBoneLayout.FloatsPerBone);
            if (i < roots)
            {
                AnimMath.ToAxis(root, out var axis);
                AnimMath.StoreTransposed(axis, root.Trans.X, root.Trans.Y, root.Trans.Z, dst);
                dst[12] = dst[13] = dst[14] = dst[15] = 0f;
                continue;
            }
            int j = i - roots;
            ref readonly var inv = ref inverseBase[i];
            AnimMath.ToAxis(world[j], out var w);
            w.T0 = world[j].Trans.X;
            w.T1 = world[j].Trans.Y;
            w.T2 = world[j].Trans.Z;
            AnimMath.Multiply(inv, w, out var skin);
            AnimMath.StoreTransposed(skin, skin.T0, skin.T1, skin.T2, dst);
            var e = extra.IsEmpty ? Vector4.Zero : extra[j];
            dst[12] = e.X;
            dst[13] = e.Y;
            dst[14] = e.Z;
            dst[15] = e.W;
        }
    }

    /// <summary>
    /// The model-space box APE frames an animated preview on: every corner of every xmesh bone box, skinned by the
    /// pose at <paramref name="t"/> (APE uses t = 0, the pose when the anim is bound; this reproduces the captured
    /// cameras, animation.md §7). Null when the mesh has no bone boxes.
    /// </summary>
    public (Vector3 Min, Vector3 Max)? PosedBoneBounds(float t)
    {
        var boxes = Skeleton.BoneBounds;
        if (boxes.Count == 0)
            return null;
        var bones = new float[AnimBoneLayout.MaxBones * AnimBoneLayout.FloatsPerBone];
        Evaluate(t, bones);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var box in boxes)
        {
            int o = (int)Math.Min(box.BoneIndex, AnimBoneLayout.MaxBones - 1) * AnimBoneLayout.FloatsPerBone;
            for (int k = 0; k < 8; k++)
            {
                var p = new Vector3((k & 1) != 0 ? box.Max.X : box.Min.X, (k & 2) != 0 ? box.Max.Y : box.Min.Y, (k & 4) != 0 ? box.Max.Z : box.Min.Z);
                var q = new Vector3(
                    bones[o + 0] * p.X + bones[o + 1] * p.Y + bones[o + 2] * p.Z + bones[o + 3],
                    bones[o + 4] * p.X + bones[o + 5] * p.Y + bones[o + 6] * p.Z + bones[o + 7],
                    bones[o + 8] * p.X + bones[o + 9] * p.Y + bones[o + 10] * p.Z + bones[o + 11]);
                min = Vector3.Min(min, q);
                max = Vector3.Max(max, q);
            }
        }
        return (min, max);
    }

    /// <summary>APE's frame pair for a normalised time: <c>f = (N-1)·t</c>, <c>lerpTo = min(⌊f⌋+1, N-1)</c>.</summary>
    public static (int Frame, int LerpTo, float Frac) FramePair(int frameCount, float t)
    {
        if (frameCount <= 1)
            return (0, 0, 0f);
        int last = frameCount - 1;
        float f = (float)last * t;
        int frame = (int)f;
        int lerpTo = last >= frame + 1 ? frame + 1 : last;
        float frac = f - (float)frame;
        frame = Math.Clamp(frame, 0, last);
        return (frame, lerpTo, frac);
    }
}

/// <summary>
/// An xanim v11 cache (<see cref="XAnimData"/>) evaluated exactly like APE's preview (<c>Anim_EvaluateLocalToWorld</c>,
/// 0x140481070): per model child bone, the anim bone of the same name is sampled (quaternion slerp, translation lerp,
/// extra-channel lerp) or, when the anim lacks it, the bind rotation with zero translation offset is used; the anim
/// translation is an offset added to the model's bind translation; the result is concatenated onto the parent
/// (or the root transform), and the root takes the delta channel for root-motion types.
/// </summary>
public sealed class XAnimClip : AnimClip
{
    public XAnimClip(XAnimData data, XAnimType type)
    {
        Data = data;
        Type = type;
    }

    public XAnimData Data { get; }
    public XAnimType Type { get; }

    /// <summary>Root motion on (APE's preview slot flag, set whenever the slot plays an anim).</summary>
    public bool ApplyRootMotion { get; set; } = true;

    /// <summary>
    /// Add the model's bind local translation to every anim translation — APE (animation.md §4) does so whatever the
    /// GDT <c>useBones</c> says, so a <c>useBones = 0</c> anim, whose cache holds full local translations, gets the bind
    /// offset twice. False takes the cache translations as the full local translation: right for <c>useBones = 0</c>
    /// only (with <c>useBones = 1</c> the converter subtracted the bind, conversion.md §3.9, and APE's sum is right).
    /// </summary>
    public bool AddBindTranslation { get; init; } = true;

    public override float Framerate => Data.Framerate;
    public override int FrameCount => (int)Data.FrameCount;
    public override IReadOnlyList<XAnimNote> Notetracks => Data.Notetracks;

    public override AnimPose Bind(AnimSkeleton skeleton) => new Pose(skeleton, this);

    private sealed class Pose : AnimPose
    {
        private readonly XAnimClip _clip;
        private readonly XAnimBone?[] _map;
        private readonly Vector4[]?[] _extra;
        private readonly AnimMat[] _world;
        private readonly Vector4[] _extraOut;

        public Pose(AnimSkeleton skeleton, XAnimClip clip) : base(skeleton, clip)
        {
            _clip = clip;
            // Anim_ModelInstBindAnim: name → anim bone index (std::map, exact names), -1 when absent.
            var byName = new Dictionary<string, XAnimBone>(StringComparer.Ordinal);
            foreach (var b in clip.Data.Bones)
                byName.TryAdd(b.Name, b);
            int n = skeleton.ChildBones.Count;
            _map = new XAnimBone?[n];
            _extra = new Vector4[]?[n];
            for (int i = 0; i < n; i++)
            {
                if (!byName.TryGetValue(skeleton.ChildBones[i].Name, out var ab))
                    continue;
                _map[i] = ab;
                MatchedBones++;
                if (ab.Extra.AsSpan().ContainsAnyExcept((byte)0))
                    _extra[i] = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Vector4>(ab.Extra).ToArray();
            }
            _world = new AnimMat[n];
            _extraOut = new Vector4[n];
        }

        public override int MatchedBones { get; }

        public override bool Drives(int bone) =>
            bone < Skeleton.RootCount
                ? _clip.ApplyRootMotion && _clip.Type.HasRootMotion() && _clip.Data.DeltaRotations.Length > 0
                : _map[bone - Skeleton.RootCount] is { Rotations.Length: > 0 };

        public override ReadOnlySpan<AnimMat> World => _world;

        public override ReadOnlySpan<Vector4> Extra => _extraOut;

        public override void EvaluateWorld(float t)
        {
            var data = _clip.Data;
            var sk = Skeleton;
            var (fi, lerpTo, frac) = FramePair(_clip.FrameCount, t);

            AnimMat root = AnimMat.Identity;
            var dq = data.DeltaRotations;
            if (_clip.ApplyRootMotion && _clip.Type.HasRootMotion() && dq.Length > 0)
            {
                int a = Math.Min(fi, dq.Length - 1), b = Math.Min(lerpTo, dq.Length - 1);
                root.Quat = AnimMath.Slerp(dq[a], dq[b], frac);
                root.Trans = AnimMath.Lerp(data.DeltaTranslations[a], data.DeltaTranslations[b], frac);
            }
            root.TransWeight = 2f;

            var children = sk.ChildBones;
            for (int i = 0; i < children.Count; i++)
            {
                var bone = children[i];
                AnimMat cur;
                bool animated = _map[i] is { Rotations.Length: > 0 };
                if (animated && _map[i] is { } ab)
                {
                    int a = Math.Min(fi, ab.Rotations.Length - 1), b = Math.Min(lerpTo, ab.Rotations.Length - 1);
                    cur.Quat = AnimMath.Slerp(ab.Rotations[a], ab.Rotations[b], frac);
                    cur.Trans = AnimMath.Lerp(ab.Translations[a], ab.Translations[b], frac);
                    _extraOut[i] = _extra[i] is { } ex ? AnimMath.Lerp(ex[a], ex[b], frac) : Vector4.Zero;
                }
                else
                {
                    cur.Quat = bone.Rotation;
                    cur.Trans = Vector3.Zero;
                    _extraOut[i] = Vector4.Zero;
                }
                cur.TransWeight = 2f;

                ref readonly var parent = ref (bone.Parent < 0 ? ref root : ref _world[bone.Parent]);
                cur.Quat = AnimMath.MultiplyParent(parent.Quat, cur.Quat);
                var q = cur.Quat;
                float lenSq = ((q.X * q.X + q.Y * q.Y) + q.Z * q.Z) + q.W * q.W;
                if (lenSq <= 0.001f)
                {
                    cur.Quat.W = 1f;
                    cur.TransWeight = 2f;
                }
                else
                {
                    cur.TransWeight = 2f / lenSq;
                }
                if (_clip.AddBindTranslation || !animated)
                    cur.Trans = new Vector3(bone.Translation.X + cur.Trans.X, bone.Translation.Y + cur.Trans.Y, bone.Translation.Z + cur.Trans.Z);
                AnimMath.ToAxis(parent, out var m);
                float x = cur.Trans.X, y = cur.Trans.Y, z = cur.Trans.Z;
                cur.Trans = new Vector3(
                    ((x * m.M00 + y * m.M10) + z * m.M20) + parent.Trans.X,
                    ((x * m.M01 + y * m.M11) + z * m.M21) + parent.Trans.Y,
                    ((x * m.M02 + y * m.M12) + z * m.M22) + parent.Trans.Z);
                _world[i] = cur;
            }
            Root = root;
        }
    }
}

/// <summary>One frame of a model-space animation: rotation and position per part.</summary>
public sealed record WorldSpaceFrame(Quaternion[] Rotations, Vector3[] Positions);

/// <summary>
/// An animation given as model-space part transforms per frame — what raw <c>.xanim_export</c>/<c>.xanim_bin</c>
/// files hold — for previews without an xanim cache. Bound to a skeleton by part name: matched bones take the
/// interpolated part transform (slerp/lerp at APE's frame pair); unmatched bones follow their parent with their bind
/// offset; the root bone stays at the origin unless a part of that name exists. Skinning then matches the cache path
/// (<c>inverse(base) · world</c>). The xmodel's GDT <c>scale</c> is not applied to part positions.
/// </summary>
public sealed class WorldSpaceClip : AnimClip
{
    public WorldSpaceClip(IReadOnlyList<string> partNames, float framerate, IReadOnlyList<WorldSpaceFrame> frames, IReadOnlyList<XAnimNote> notetracks)
    {
        PartNames = partNames;
        _framerate = framerate > 0 ? framerate : 30f;
        Frames = frames;
        Notetracks = notetracks;
    }

    private readonly float _framerate;

    public IReadOnlyList<string> PartNames { get; }
    public IReadOnlyList<WorldSpaceFrame> Frames { get; }

    /// <summary>Root-part handling (default: parts as exported).</summary>
    public WorldSpaceRoot Root { get; init; }
    public override float Framerate => _framerate;
    public override int FrameCount => Frames.Count;
    public override IReadOnlyList<XAnimNote> Notetracks { get; }

    public override AnimPose Bind(AnimSkeleton skeleton) => new Pose(skeleton, this);

    private sealed class Pose : AnimPose
    {
        private readonly WorldSpaceClip _clip;
        private readonly int[] _part;   // per bone (xmesh order): part index or -1
        private readonly AnimMat[] _world;
        private readonly AnimMat[] _bindLocal;

        public Pose(AnimSkeleton skeleton, WorldSpaceClip clip) : base(skeleton, clip)
        {
            _clip = clip;
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < clip.PartNames.Count; i++)
                byName.TryAdd(clip.PartNames[i], i);
            _part = new int[skeleton.BoneCount];
            for (int i = 0; i < _part.Length; i++)
            {
                _part[i] = byName.TryGetValue(skeleton.BoneNames[i], out var p) ? p : -1;
                if (_part[i] >= 0 && i >= skeleton.RootCount)
                    MatchedBones++;
            }
            _world = new AnimMat[skeleton.ChildBones.Count];
            _bindLocal = new AnimMat[skeleton.ChildBones.Count];
            for (int i = 0; i < _bindLocal.Length; i++)
                _bindLocal[i] = skeleton.BindLocal(i);
        }

        public override int MatchedBones { get; }

        public override bool Drives(int bone) => _clip.FrameCount > 0 && _part[bone] >= 0;

        public override ReadOnlySpan<AnimMat> World => _world;

        private AnimMat Sample(int part, int a, int b, float frac)
        {
            var fa = _clip.Frames[a];
            var fb = _clip.Frames[b];
            var q = AnimMath.Slerp(fa.Rotations[part], fb.Rotations[part], frac);
            float lenSq = ((q.X * q.X + q.Y * q.Y) + q.Z * q.Z) + q.W * q.W;
            return new AnimMat(q, AnimMath.Lerp(fa.Positions[part], fb.Positions[part], frac), lenSq > 0.001f ? 2f / lenSq : 2f);
        }

        public override void EvaluateWorld(float t)
        {
            var sk = Skeleton;
            var (fi, lerpTo, frac) = FramePair(_clip.FrameCount, t);
            int roots = sk.RootCount;
            AnimMat root = _clip.FrameCount > 0 && _part[0] >= 0 ? Sample(_part[0], fi, lerpTo, frac) : AnimMat.Identity;

            // Relative / delta anims: parts re-expressed against the root part (this frame's, or frame 0's for root motion).
            bool rebase = _clip.Root != WorldSpaceRoot.Absolute && _clip.FrameCount > 0 && _part[0] >= 0;
            var toRoot = AnimMat.Identity;
            if (rebase)
            {
                toRoot = Inverse(_clip.Root == WorldSpaceRoot.Relative ? root : Sample(_part[0], 0, 0, 0f));
                root = _clip.Root == WorldSpaceRoot.Relative ? AnimMat.Identity : Concat(root, toRoot);
            }

            for (int i = 0; i < _world.Length; i++)
            {
                int part = _part[i + roots];
                if (part >= 0 && _clip.FrameCount > 0)
                {
                    var w = Sample(part, fi, lerpTo, frac);
                    _world[i] = rebase ? Concat(w, toRoot) : w;
                    continue;
                }
                var parent = sk.ChildBones[i].Parent < 0 ? root : _world[sk.ChildBones[i].Parent];
                var local = _bindLocal[i];
                var q = AnimMath.MultiplyParent(parent.Quat, local.Quat);
                float lenSq = ((q.X * q.X + q.Y * q.Y) + q.Z * q.Z) + q.W * q.W;
                AnimMath.ToAxis(parent, out var m);
                var lt = local.Trans;
                _world[i] = new AnimMat(q, new Vector3(
                    ((lt.X * m.M00 + lt.Y * m.M10) + lt.Z * m.M20) + parent.Trans.X,
                    ((lt.X * m.M01 + lt.Y * m.M11) + lt.Z * m.M21) + parent.Trans.Y,
                    ((lt.X * m.M02 + lt.Y * m.M12) + lt.Z * m.M22) + parent.Trans.Z), lenSq > 0.001f ? 2f / lenSq : 2f);
            }
            Root = root;
        }

        /// <summary><paramref name="child"/> then <paramref name="parent"/> (row vectors: child · parent).</summary>
        private static AnimMat Concat(in AnimMat child, in AnimMat parent)
        {
            var q = AnimMath.MultiplyParent(parent.Quat, child.Quat);
            float lenSq = ((q.X * q.X + q.Y * q.Y) + q.Z * q.Z) + q.W * q.W;
            AnimMath.ToAxis(parent, out var m);
            var lt = child.Trans;
            return new AnimMat(q, new Vector3(
                ((lt.X * m.M00 + lt.Y * m.M10) + lt.Z * m.M20) + parent.Trans.X,
                ((lt.X * m.M01 + lt.Y * m.M11) + lt.Z * m.M21) + parent.Trans.Y,
                ((lt.X * m.M02 + lt.Y * m.M12) + lt.Z * m.M22) + parent.Trans.Z), lenSq > 0.001f ? 2f / lenSq : 2f);
        }

        private static AnimMat Inverse(in AnimMat a)
        {
            var q = Quaternion.Conjugate(Quaternion.Normalize(a.Quat));
            return new AnimMat(q, Vector3.Transform(-a.Trans, q), 2f);
        }
    }
}

/// <summary>How a <see cref="WorldSpaceClip"/> places the parts when the export has a part for the model's root
/// bone (usually <c>tag_origin</c>).</summary>
public enum WorldSpaceRoot
{
    /// <summary>Parts as exported; the root bone follows its part.</summary>
    Absolute,

    /// <summary>Parts relative to the root part of the same frame, root at the origin (relative anims: the converter
    /// drops the root).</summary>
    Relative,

    /// <summary>Parts relative to the root part of frame 0; the root carries the motion since then (delta anims).</summary>
    Delta,
}

/// <summary>
/// APE's preview clock (animation.md §6): wall-clock seconds at rate 1, wrapped with <c>fmodf</c> over the longest
/// playing anim (always loops, regardless of the GDT <c>looping</c> flag); each anim samples at
/// <c>t = clamp(time / duration, 0, 1)</c>; step buttons move ±1/30 s; the overlay shows frame <c>(int)(N·t)</c>.
/// </summary>
public static class AnimClock
{
    /// <summary>The ± step of APE's frame buttons (seconds, independent of the anim's rate).</summary>
    public const float StepSeconds = 0.033333f;

    /// <summary><c>Anim_PreviewSlotSetTime</c>: <c>clamp(time / duration, 0, 1)</c>.</summary>
    public static float NormalizedTime(float time, float duration) =>
        duration > 0f ? Math.Clamp(time / duration, 0f, 1f) : 0f;

    /// <summary><c>Anim_PreviewUpdateTick</c>: <c>fmodf(time + elapsed, loopLength)</c>.</summary>
    public static float Advance(float time, float elapsedSeconds, float loopLength) =>
        loopLength > 0f ? (time + elapsedSeconds) % loopLength : 0f;

    /// <summary>The frame number APE's overlay prints ("Frame: %i").</summary>
    public static int DisplayFrame(int frameCount, float t) => (int)((float)frameCount * t);
}
