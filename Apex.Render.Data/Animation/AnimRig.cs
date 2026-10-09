using System.Numerics;
using Apex.Render.Data.Assets;

namespace Apex.Render.Data.Animation;

/// <summary>A model joining an <see cref="AnimRig"/>: its skeleton and, for root bones the rig does not have, the rig
/// bone they hang from (e.g. a view gun's <c>tag_weapon</c> under the hands' <c>tag_weapon_right</c>).</summary>
public sealed record AnimRigMember(AnimSkeleton Skeleton, string? ParentTag = null);

/// <summary>
/// Several models animated as one, the way the game's DObj composes a viewmodel (hands + view gun + attachments):
/// one merged skeleton in which bones of the same name are shared, driven once by a clip, and every member model skinned
/// from it by bone name. The merged skeleton starts as <c>primary</c> (the skeleton the anim was exported against) and
/// takes, in member order, each bone it does not have yet: under its parent of the same name, or — for a member root
/// that is not shared — under <see cref="AnimRigMember.ParentTag"/> with an identity offset. Bones without anim data
/// then follow their parent with their own bind offset, so a gun's bolt or mag moves only if the anim has it.
/// </summary>
public sealed class AnimRig
{
    private readonly Dictionary<string, int> _index;

    private AnimRig(AnimSkeleton skeleton, Dictionary<string, int> index, int added)
    {
        Skeleton = skeleton;
        _index = index;
        AddedBones = added;
    }

    /// <summary>The merged skeleton (bind the clip to it).</summary>
    public AnimSkeleton Skeleton { get; }

    /// <summary>Bones the members brought in beyond the primary skeleton.</summary>
    public int AddedBones { get; }

    /// <summary>Index of a bone in <see cref="Skeleton"/> (case-insensitive), or -1.</summary>
    public int IndexOf(string name) => _index.TryGetValue(name, out var i) ? i : -1;

    public static AnimRig Build(AnimSkeleton primary, IEnumerable<AnimRigMember> members)
    {
        var names = primary.BoneNames.ToList();
        var baseMats = primary.BaseMats.ToList();
        var children = primary.ChildBones.ToList();
        int roots = primary.RootCount;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
            index.TryAdd(names[i], i);
        int added = 0;

        foreach (var member in members)
        {
            var sk = member.Skeleton;
            for (int b = 0; b < sk.BoneCount; b++)
            {
                var name = sk.BoneNames[b];
                if (index.ContainsKey(name))
                    continue;
                int parent;
                Quaternion rot;
                Vector3 trans;
                if (b < sk.RootCount)
                {
                    // An unshared root hangs from the attach tag (or the rig root) with no offset.
                    parent = member.ParentTag is { } tag && index.TryGetValue(tag, out var t) ? t : 0;
                    rot = Quaternion.Identity;
                    trans = Vector3.Zero;
                }
                else
                {
                    var local = sk.ChildBones[b - sk.RootCount];
                    var parentName = local.Parent < 0 ? sk.BoneNames[0] : sk.BoneNames[sk.RootCount + local.Parent];
                    parent = index.TryGetValue(parentName, out var p) ? p : 0;
                    rot = local.Rotation;
                    trans = local.Translation;
                }
                var pb = baseMats[parent];
                var bm = Compose(new AnimMat(rot, trans, 2f), new AnimMat(pb.Rotation, pb.Translation, pb.TransWeight));
                index[name] = names.Count;
                names.Add(name);
                baseMats.Add(new XMeshBaseMat(bm.Quat, bm.Trans, bm.TransWeight));
                children.Add(new XMeshBone(name, parent < roots ? -1 : parent - roots, rot, trans));
                added++;
            }
        }

        var skeleton = new AnimSkeleton
        {
            BoneNames = names,
            BaseMats = baseMats,
            ChildBones = children,
            BoneBounds = primary.BoneBounds,
        };
        return new AnimRig(skeleton, index, added);
    }

    /// <summary>Per bone of <paramref name="sk"/>, its bone in <see cref="Skeleton"/> (every bone of a member is there).</summary>
    public int[] Map(AnimSkeleton sk)
    {
        var map = new int[sk.BoneCount];
        for (int b = 0; b < map.Length; b++)
            map[b] = IndexOf(sk.BoneNames[b]);
        return map;
    }

    /// <summary>
    /// How far <paramref name="member"/>'s bind joints sit from the joints of the same name in <paramref name="reference"/>
    /// (e.g. the anim's skeleton), both measured in their own frame of the member's root bone: the largest distance and
    /// its bone, or null when the reference lacks the member's root or shares no other bone with it. A member built on
    /// another rig (a gun from another game with the same joint names in other places) is far off: the rig would move
    /// each of its parts to the reference's joint and pull the model apart.
    /// </summary>
    public static (float Distance, string Bone)? BindMismatch(AnimSkeleton reference, AnimSkeleton member)
    {
        if (member.BoneCount == 0)
            return null;
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < reference.BoneCount; i++)
            names.TryAdd(reference.BoneNames[i], i);
        if (!names.TryGetValue(member.BoneNames[0], out int refRoot))
            return null;
        (float Distance, string Bone)? worst = null;
        for (int b = 1; b < member.BoneCount; b++)
        {
            if (!names.TryGetValue(member.BoneNames[b], out int r))
                continue;
            var d = (RootLocal(member.BaseMats, 0, b) - RootLocal(reference.BaseMats, refRoot, r)).Length();
            if (worst is null || d > worst.Value.Distance)
                worst = (d, member.BoneNames[b]);
        }
        return worst;
    }

    /// <summary>Bind position of bone <paramref name="bone"/> in the frame of bone <paramref name="root"/>.</summary>
    private static Vector3 RootLocal(IReadOnlyList<XMeshBaseMat> baseMats, int root, int bone)
    {
        var r = baseMats[root];
        var q = r.Rotation.LengthSquared() > 0f ? Quaternion.Normalize(r.Rotation) : Quaternion.Identity;
        return Vector3.Transform(baseMats[bone].Translation - r.Translation, Quaternion.Conjugate(q));
    }

    /// <summary>Model-space transform of rig bone <paramref name="bone"/> in an evaluated pose of <see cref="Skeleton"/>.</summary>
    public AnimMat WorldOf(AnimPose pose, int bone) =>
        bone < 0 || bone < Skeleton.RootCount ? pose.Root : pose.World[bone - Skeleton.RootCount];

    /// <summary>
    /// The b11 bone buffer of member skeleton <paramref name="sk"/> (<paramref name="map"/> from <see cref="Map"/>) for an
    /// evaluated pose of the rig: each bone takes the rig transform of its name, skinned against its own bind.
    /// </summary>
    public void WriteSkin(AnimPose pose, AnimSkeleton sk, int[] map, Span<float> bones)
    {
        var world = new AnimMat[sk.ChildBones.Count];
        for (int j = 0; j < world.Length; j++)
            world[j] = WorldOf(pose, map[sk.RootCount + j]);
        var root = WorldOf(pose, map[0]);
        AnimPose.WriteSkin(sk, root, world, ReadOnlySpan<Vector4>.Empty, bones);
    }

    /// <summary><paramref name="child"/> then <paramref name="parent"/> (row vectors), as the evaluation concatenates.</summary>
    public static AnimMat Compose(in AnimMat child, in AnimMat parent)
    {
        var q = AnimMath.MultiplyParent(parent.Quat, child.Quat);
        float lenSq = ((q.X * q.X + q.Y * q.Y) + q.Z * q.Z) + q.W * q.W;
        AnimMath.ToAxis(parent, out var m);
        var t = child.Trans;
        return new AnimMat(q, new Vector3(
            ((t.X * m.M00 + t.Y * m.M10) + t.Z * m.M20) + parent.Trans.X,
            ((t.X * m.M01 + t.Y * m.M11) + t.Z * m.M21) + parent.Trans.Y,
            ((t.X * m.M02 + t.Y * m.M12) + t.Z * m.M22) + parent.Trans.Z), lenSq > 0.001f ? 2f / lenSq : 2f);
    }
}
