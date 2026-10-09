using System.Numerics;
using Apex.Render.Data.Assets;

namespace Apex.Render.Data.Animation;

/// <summary>
/// Two clips on one skeleton, mixed by <see cref="Weight"/>: <see cref="From"/> plays on the clock, <see cref="To"/> is
/// held at <see cref="ToTime"/>. The weapon recoil preview uses it for hip (the idle anim) to ADS (the ADS-up anim's
/// last frame). As the game layers anims, per bone and relative to the parent: a bone <see cref="To"/> keys is mixed
/// from <see cref="From"/>'s towards <see cref="To"/>'s, a bone it lacks keeps <see cref="From"/>'s (an ADS-up anim
/// that keys only tag_torso carries the idle arms to the eye). Weight 0 is exactly <see cref="From"/>'s pose.
/// </summary>
public sealed class AnimBlendClip : AnimClip
{
    public AnimBlendClip(AnimClip from, AnimClip to, float toTime = 1f)
    {
        From = from;
        To = to;
        ToTime = toTime;
    }

    public AnimClip From { get; }
    public AnimClip To { get; }
    public float ToTime { get; }

    /// <summary>0 = <see cref="From"/>, 1 = <see cref="To"/> layered on it. A pose reads it when it is next evaluated:
    /// whoever changes it re-evaluates the pose (<c>AnimPlaybackPose.Refresh</c>).</summary>
    public float Weight { get; set; }

    public override float Framerate => From.Framerate;
    public override int FrameCount => From.FrameCount;
    public override IReadOnlyList<XAnimNote> Notetracks => From.Notetracks;

    public override AnimPose Bind(AnimSkeleton skeleton) => new Pose(skeleton, this);

    private sealed class Pose : AnimPose
    {
        private readonly AnimBlendClip _clip;
        private readonly AnimPose _from;
        private readonly AnimPose _to;
        private readonly AnimMat[] _world;
        private float _weight;

        public Pose(AnimSkeleton skeleton, AnimBlendClip clip) : base(skeleton, clip)
        {
            _clip = clip;
            _from = clip.From.Bind(skeleton);
            _to = clip.To.Bind(skeleton);
            _world = new AnimMat[skeleton.ChildBones.Count];
        }

        public override int MatchedBones => _from.MatchedBones;

        public override bool Drives(int bone) => _from.Drives(bone) || _to.Drives(bone);

        public override ReadOnlySpan<AnimMat> World => _weight <= 0f ? _from.World : _world;

        public override ReadOnlySpan<Vector4> Extra => _from.Extra;

        public override void EvaluateWorld(float t)
        {
            _weight = Math.Clamp(_clip.Weight, 0f, 1f);
            _from.EvaluateWorld(t);
            if (_weight <= 0f)
            {
                Root = _from.Root;
                return;
            }
            _to.EvaluateWorld(_clip.ToTime);
            var sk = Skeleton;
            int roots = sk.RootCount;
            var a = _from.World;
            var b = _to.World;
            Root = _to.Drives(0) ? Mix(_from.Root, _to.Root, _weight) : _from.Root;
            for (int i = 0; i < _world.Length; i++)
            {
                int p = sk.ChildBones[i].Parent;
                var local = Local(a[i], p < 0 ? _from.Root : a[p]);
                if (_to.Drives(i + roots))
                    local = Mix(local, Local(b[i], p < 0 ? _to.Root : b[p]), _weight);
                _world[i] = AnimRig.Compose(local, p < 0 ? Root : _world[p]);
            }
        }

        /// <summary><paramref name="world"/> relative to <paramref name="parent"/>: what <see cref="AnimRig.Compose"/> undoes.</summary>
        private static AnimMat Local(in AnimMat world, in AnimMat parent)
        {
            var q = AnimMath.MultiplyParent(Quaternion.Conjugate(Unit(parent.Quat)), Unit(world.Quat));
            AnimMath.ToAxis(parent, out var m);
            var d = world.Trans - parent.Trans;
            var t = new Vector3(
                d.X * m.M00 + d.Y * m.M01 + d.Z * m.M02,
                d.X * m.M10 + d.Y * m.M11 + d.Z * m.M12,
                d.X * m.M20 + d.Y * m.M21 + d.Z * m.M22);
            return new AnimMat(q, t, 2f);
        }

        private static AnimMat Mix(in AnimMat a, in AnimMat b, float w)
        {
            var q = AnimMath.Slerp(Unit(a.Quat), Unit(b.Quat), w);
            float lenSq = ((q.X * q.X + q.Y * q.Y) + q.Z * q.Z) + q.W * q.W;
            return new AnimMat(q, AnimMath.Lerp(a.Trans, b.Trans, w), lenSq > 0.001f ? 2f / lenSq : 2f);
        }

        private static Quaternion Unit(Quaternion q) => q.LengthSquared() > 0f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }
}
