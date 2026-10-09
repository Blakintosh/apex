using System.Numerics;
using System.Runtime.InteropServices;
using Apex.Render.Constants;
using Apex.Render.Data.Animation;

namespace Apex.Render.Assets;

/// <summary>
/// Per-frame skinning of a previewed model: the b11 bone buffer (<see cref="CodeObjectBonesConsts"/> bytes, the LOD's
/// xmesh bone order) for each LOD. A LOD without bones draws its bind pose with <c>hasBones = 0</c>, like APE before
/// an anim is bound.
/// </summary>
public interface IPreviewPose
{
    /// <summary>The model this pose was bound to (the renderer ignores the pose while it draws another one), or null
    /// for a pose that fits any model.</summary>
    PreparedPreviewModel? Model => null;

    /// <summary>True when <paramref name="lod"/> is skinned by this pose (its items get <see cref="Bones"/>).</summary>
    bool Animates(int lod);

    /// <summary>The bone buffer for <paramref name="lod"/> at the current time, or empty for the bind pose.</summary>
    ReadOnlyMemory<byte> Bones(int lod);

    /// <summary>The pose changed (time moved): the frame needs drawing again.</summary>
    event Action? Changed;

    /// <summary>Model-space box the camera frames the posed model on (APE: the bone boxes at t = 0), or null to use
    /// the model's bind bounds.</summary>
    (Vector3 Min, Vector3 Max)? FramingBounds => null;

    /// <summary>Further models drawn with <see cref="Model"/>, each with its own pose (<see cref="Model"/> set) on the
    /// same clock — e.g. the view gun and its attachments under viewhands.</summary>
    IReadOnlyList<IPreviewPose> Attached => Array.Empty<IPreviewPose>();

    /// <summary>Joint lines drawn over the frame at the current time, or null.</summary>
    PreviewOverlay? Overlay => null;

    /// <summary>The model-space transform (row vectors; rows X forward, Y left, Z up) a first-person camera looks from
    /// at the current time, or null.</summary>
    Matrix4x4? ViewTag => null;
}

/// <summary>Model-space line segments (<see cref="Segments"/>: pairs of points) and joint markers drawn over the
/// preview frame after post-processing.</summary>
public sealed record PreviewOverlay(Vector3[] Segments, Vector3[] Joints);

/// <summary>
/// A pose playing a clip on a clock: the normalised time (APE's <c>clamp(time / duration)</c>) that the buffers are
/// evaluated at, and <see cref="Changed"/> whenever it moves. UI thread only (the renderer reads the buffers while
/// drawing).
/// </summary>
public abstract class AnimPlaybackPose : IPreviewPose
{
    private float _time;

    protected AnimPlaybackPose(PreparedPreviewModel model, AnimClip clip)
    {
        Model = model;
        Clip = clip;
    }

    public AnimClip Clip { get; }

    public PreparedPreviewModel Model { get; }

    /// <summary>Skeleton bones the clip drives (matched by name).</summary>
    public abstract int MatchedBones { get; }

    /// <summary>Bones of the animated skeleton.</summary>
    public abstract int BoneCount { get; }

    /// <summary>True when something can be skinned.</summary>
    public abstract bool IsAnimated { get; }

    /// <summary>Normalised clip time (0..1; APE's <c>clamp(time / duration)</c>).</summary>
    public float NormalizedTime
    {
        get => _time;
        set
        {
            value = Math.Clamp(value, 0f, 1f);
            if (value == _time)
                return;
            _time = value;
            Invalidate();
            Changed?.Invoke();
        }
    }

    public event Action? Changed;

    /// <summary>Raises <see cref="Changed"/> (a display option of the pose changed).</summary>
    protected void RaiseChanged() => Changed?.Invoke();

    /// <summary>Something the clip reads besides the time changed (an <see cref="AnimBlendClip"/>'s weight): evaluate again.</summary>
    public void Refresh()
    {
        Invalidate();
        Changed?.Invoke();
    }

    /// <summary>The time moved: cached buffers are stale.</summary>
    protected abstract void Invalidate();

    public abstract bool Animates(int lod);

    public abstract ReadOnlyMemory<byte> Bones(int lod);

    public abstract (Vector3 Min, Vector3 Max)? FramingBounds { get; }

    /// <summary>The b11 floats of LOD <paramref name="lod"/> at the current time (verification).</summary>
    public float[] EvaluateFloats(int lod)
    {
        var bytes = Bones(lod);
        return MemoryMarshal.Cast<byte, float>(bytes.Span).ToArray();
    }

    /// <summary>A line from every bone to its parent (joints closer than 0.001 in skipped) and a marker per joint, from
    /// an evaluated pose (<paramref name="pose"/> on <paramref name="sk"/>).</summary>
    protected static PreviewOverlay JointOverlay(AnimSkeleton sk, AnimPose pose)
    {
        var joints = new Vector3[sk.BoneCount];
        for (int b = 0; b < sk.BoneCount; b++)
            joints[b] = b < sk.RootCount ? pose.Root.Trans : pose.World[b - sk.RootCount].Trans;
        var segments = new List<Vector3>();
        for (int j = 0; j < sk.ChildBones.Count; j++)
        {
            int parent = sk.ChildBones[j].Parent;
            var a = joints[sk.RootCount + j];
            var b = parent < 0 ? joints[0] : joints[sk.RootCount + parent];
            if (Vector3.DistanceSquared(a, b) < 1e-6f)
                continue;
            segments.Add(b);
            segments.Add(a);
        }
        return new PreviewOverlay(segments.ToArray(), joints);
    }

    /// <summary>The first-person view tag of <paramref name="sk"/> (<c>tag_camera</c>, else <c>tag_view</c>), or -1.</summary>
    protected static int FindViewTag(AnimSkeleton sk)
    {
        int view = -1;
        for (int b = 0; b < sk.BoneCount; b++)
        {
            if (sk.BoneNames[b].Equals("tag_camera", StringComparison.OrdinalIgnoreCase))
                return b;
            if (view < 0 && sk.BoneNames[b].Equals("tag_view", StringComparison.OrdinalIgnoreCase))
                view = b;
        }
        return view;
    }

    /// <summary>Bone <paramref name="bone"/> of an evaluated pose as a row-vector matrix.</summary>
    protected static Matrix4x4 BoneMatrix(AnimPose pose, int bone)
    {
        var w = bone < pose.Skeleton.RootCount ? pose.Root : pose.World[bone - pose.Skeleton.RootCount];
        AnimMath.ToAxis(w, out var m);
        return new Matrix4x4(m.M00, m.M01, m.M02, 0, m.M10, m.M11, m.M12, 0, m.M20, m.M21, m.M22, 0, w.Trans.X, w.Trans.Y, w.Trans.Z, 1);
    }

    /// <summary>The corners of every bone box of <paramref name="skeleton"/> skinned by <paramref name="bones"/>.</summary>
    protected static void GrowByBoneBoxes(AnimSkeleton skeleton, ReadOnlySpan<float> bones, ref Vector3 min, ref Vector3 max)
    {
        foreach (var box in skeleton.BoneBounds)
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
    }
}

/// <summary>
/// An <see cref="AnimClip"/> playing on every LOD of a <see cref="PreparedPreviewModel"/>: one binding per LOD
/// skeleton (bones matched by name), evaluated lazily at <see cref="AnimPlaybackPose.NormalizedTime"/> into a 64 KiB
/// buffer per LOD — APE's single-model anim preview.
/// </summary>
public sealed class AnimPreviewPose : AnimPlaybackPose, IPreviewPose
{
    private readonly AnimPose?[] _poses;
    private readonly byte[]?[] _buffers;
    private readonly bool[] _valid;
    private readonly bool _hasGeometry;

    public AnimPreviewPose(PreparedPreviewModel model, AnimClip clip) : base(model, clip)
    {
        int n = model.Lods.Count;
        _poses = new AnimPose?[n];
        _buffers = new byte[]?[n];
        _valid = new bool[n];
        for (int lod = 0; lod < n; lod++)
        {
            if (AnimSkeleton.From(model.Lods[lod]) is { IsAnimatable: true } skeleton)
                _poses[lod] = clip.Bind(skeleton);
        }
        _hasGeometry = model.Lods.Any(l => l.Surfaces.Count > 0);
    }

    /// <summary>A model without surfaces (a bones-only skeleton) shows its joints instead.</summary>
    PreviewOverlay? IPreviewPose.Overlay
    {
        get
        {
            if (_hasGeometry || _poses.Length == 0 || _poses[0] is not { } pose)
                return null;
            pose.EvaluateWorld(NormalizedTime);
            return JointOverlay(pose.Skeleton, pose);
        }
    }

    Matrix4x4? IPreviewPose.ViewTag
    {
        get
        {
            if (_poses.Length == 0 || _poses[0] is not { } pose || FindViewTag(pose.Skeleton) is var tag && tag < 0)
                return null;
            pose.EvaluateWorld(NormalizedTime);
            return BoneMatrix(pose, tag);
        }
    }

    /// <summary>Model bones the clip drives on LOD 0.</summary>
    public override int MatchedBones => _poses.Length > 0 ? _poses[0]?.MatchedBones ?? 0 : 0;

    /// <summary>Bones of LOD 0's skeleton.</summary>
    public override int BoneCount => _poses.Length > 0 ? _poses[0]?.Skeleton.BoneCount ?? 0 : 0;

    /// <summary>True when at least one LOD can be skinned.</summary>
    public override bool IsAnimated => _poses.Any(p => p is not null);

    /// <summary>APE's anim framing: LOD 0's bone boxes skinned by the pose at t = 0.</summary>
    public override (Vector3 Min, Vector3 Max)? FramingBounds =>
        _framing ??= _poses.Length > 0 && _poses[0] is { } p ? p.PosedBoneBounds(0f) : null;

    private (Vector3 Min, Vector3 Max)? _framing;

    protected override void Invalidate() => Array.Clear(_valid);

    public override bool Animates(int lod) => (uint)lod < (uint)_poses.Length && _poses[lod] is not null;

    public override ReadOnlyMemory<byte> Bones(int lod)
    {
        if ((uint)lod >= (uint)_poses.Length || _poses[lod] is not { } pose)
            return ReadOnlyMemory<byte>.Empty;
        var buffer = _buffers[lod] ??= new byte[CodeObjectBonesConsts.Size];
        if (!_valid[lod])
        {
            pose.Evaluate(NormalizedTime, MemoryMarshal.Cast<byte, float>(buffer.AsSpan()));
            _valid[lod] = true;
        }
        return buffer;
    }
}

/// <summary>
/// A clip driving several models as one (<see cref="AnimRig"/>): the clip is evaluated once per time on the merged
/// skeleton and every LOD of <see cref="AnimPlaybackPose.Model"/> and of each attached model is skinned from it by bone
/// name. Draws a joint overlay when <see cref="ShowJoints"/> is set or no model has anything to draw, and exposes the
/// first-person view tag (<c>tag_camera</c>, else <c>tag_view</c>).
/// </summary>
public sealed class AnimRigPose : AnimPlaybackPose, IPreviewPose
{
    private readonly AnimRig _rig;
    private readonly AnimPose _pose;
    private readonly Member _primary;
    private readonly Member[] _attached;
    private readonly int _viewTag;
    private bool _evaluated;
    private bool _showJoints;

    public AnimRigPose(PreparedPreviewModel model, IReadOnlyList<PreparedPreviewModel> attached, AnimRig rig, AnimClip clip) : base(model, clip)
    {
        _rig = rig;
        _pose = clip.Bind(rig.Skeleton);
        _primary = new Member(this, model);
        _attached = attached.Select(m => new Member(this, m)).ToArray();
        _viewTag = FindViewTag(rig.Skeleton);
        HasGeometry = model.Lods.Any(l => l.Surfaces.Count > 0) || attached.Any(m => m.Lods.Any(l => l.Surfaces.Count > 0));
    }

    public AnimRig Rig => _rig;

    public override int MatchedBones => _pose.MatchedBones;

    public override int BoneCount => _rig.Skeleton.BoneCount;

    public override bool IsAnimated => _rig.Skeleton.IsAnimatable;

    /// <summary>True when at least one model has surfaces (else only the joint overlay shows).</summary>
    public bool HasGeometry { get; }

    /// <summary>Draw the rig's joints over the frame (always drawn when there is no geometry).</summary>
    public bool ShowJoints
    {
        get => _showJoints;
        set
        {
            if (_showJoints == value)
                return;
            _showJoints = value;
            RaiseChanged();
        }
    }

    public IReadOnlyList<IPreviewPose> Attached => _attached;

    protected override void Invalidate()
    {
        _evaluated = false;
        _primary.Invalidate();
        foreach (var m in _attached)
            m.Invalidate();
    }

    private AnimPose Evaluated()
    {
        if (!_evaluated)
        {
            _pose.EvaluateWorld(NormalizedTime);
            _evaluated = true;
        }
        return _pose;
    }

    public override bool Animates(int lod) => _primary.Animates(lod);

    public override ReadOnlyMemory<byte> Bones(int lod) => _primary.Bones(lod);

    /// <summary>APE's rule on the whole composition: every model's posed bone boxes at t = 0; the joints when no model
    /// has bone boxes.</summary>
    public override (Vector3 Min, Vector3 Max)? FramingBounds => _framing ??= ComputeFraming();

    private (Vector3 Min, Vector3 Max)? _framing;

    private (Vector3 Min, Vector3 Max)? ComputeFraming()
    {
        float keep = NormalizedTime;
        _pose.EvaluateWorld(0f);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var bones = new float[AnimBoneLayout.MaxBones * AnimBoneLayout.FloatsPerBone];
        foreach (var m in _attached.Prepend(_primary))
        {
            if (m.Lod0 is not { } sk || sk.BoneBounds.Count == 0)
                continue;
            _rig.WriteSkin(_pose, sk, m.Map0!, bones);
            GrowByBoneBoxes(sk, bones, ref min, ref max);
        }
        if (min.X > max.X)
        {
            foreach (var p in JointPositions())
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            if (min.X > max.X)
                return null;
            var pad = new Vector3(Math.Max((max - min).Length() * 0.05f, 1f));
            min -= pad;
            max += pad;
        }
        _evaluated = false;
        _pose.EvaluateWorld(keep);
        _evaluated = true;
        return (min, max);
    }

    private IEnumerable<Vector3> JointPositions()
    {
        var sk = _rig.Skeleton;
        for (int b = 0; b < sk.BoneCount; b++)
            yield return _rig.WorldOf(_pose, b).Trans;
    }

    public PreviewOverlay? Overlay
    {
        get
        {
            if (!ShowJoints && HasGeometry)
                return null;
            return JointOverlay(_rig.Skeleton, Evaluated());
        }
    }

    public Matrix4x4? ViewTag
    {
        get
        {
            return _viewTag < 0 ? null : BoneMatrix(Evaluated(), _viewTag);
        }
    }

    // Explicit members so the interface sees the rig's composition (the base class does not declare them).
    IReadOnlyList<IPreviewPose> IPreviewPose.Attached => Attached;
    PreviewOverlay? IPreviewPose.Overlay => Overlay;
    Matrix4x4? IPreviewPose.ViewTag => ViewTag;

    /// <summary>One model of the rig: its LOD skeletons mapped onto the rig, a buffer per LOD.</summary>
    private sealed class Member : IPreviewPose
    {
        private readonly AnimRigPose _owner;
        private readonly AnimSkeleton?[] _skeletons;
        private readonly int[]?[] _maps;
        private readonly byte[]?[] _buffers;
        private readonly bool[] _valid;

        public Member(AnimRigPose owner, PreparedPreviewModel model)
        {
            _owner = owner;
            Model = model;
            int n = model.Lods.Count;
            _skeletons = new AnimSkeleton?[n];
            _maps = new int[]?[n];
            _buffers = new byte[]?[n];
            _valid = new bool[n];
            for (int lod = 0; lod < n; lod++)
            {
                if (AnimSkeleton.From(model.Lods[lod]) is not { } sk)
                    continue;
                var map = owner._rig.Map(sk);
                if (map.All(i => i < 0))
                    continue;
                _skeletons[lod] = sk;
                _maps[lod] = map;
            }
        }

        public PreparedPreviewModel Model { get; }

        public AnimSkeleton? Lod0 => _skeletons.Length > 0 ? _skeletons[0] : null;

        public int[]? Map0 => _maps.Length > 0 ? _maps[0] : null;

        // Attached models redraw with the owner (the renderer listens to the owner's Changed).
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public void Invalidate() => Array.Clear(_valid);

        public bool Animates(int lod) => (uint)lod < (uint)_skeletons.Length && _skeletons[lod] is not null;

        public ReadOnlyMemory<byte> Bones(int lod)
        {
            if ((uint)lod >= (uint)_skeletons.Length || _skeletons[lod] is not { } sk)
                return ReadOnlyMemory<byte>.Empty;
            var buffer = _buffers[lod] ??= new byte[CodeObjectBonesConsts.Size];
            if (!_valid[lod])
            {
                _owner._rig.WriteSkin(_owner.Evaluated(), sk, _maps[lod]!, MemoryMarshal.Cast<byte, float>(buffer.AsSpan()));
                _valid[lod] = true;
            }
            return buffer;
        }
    }
}
