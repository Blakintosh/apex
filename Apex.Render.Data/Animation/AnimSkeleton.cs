using System.Numerics;
using Apex.Render.Data.Assets;

namespace Apex.Render.Data.Animation;

/// <summary>
/// A model skeleton as APE's model instance holds it (animation.md §3): every bone in xmesh order (the order the
/// vertices' blend indices and the b11 bone buffer use), the model-space bind pose (base mats) per bone, and the
/// parent-relative "child" bones — all bones minus the leading root bone(s) (<c>tag_origin</c>), whose parent index
/// is into the child list with -1 meaning "child of the root".
/// </summary>
public sealed class AnimSkeleton
{
    /// <summary>Bone names in xmesh order.</summary>
    public required IReadOnlyList<string> BoneNames { get; init; }

    /// <summary>Model-space bind pose per bone (xmesh order).</summary>
    public required IReadOnlyList<XMeshBaseMat> BaseMats { get; init; }

    /// <summary>Parent-relative child bones (xmesh <c>localBones</c>): bone <c>RootCount + i</c> of the model.</summary>
    public required IReadOnlyList<XMeshBone> ChildBones { get; init; }

    /// <summary>Per-bone bind-space boxes (xmesh <c>boneBounds</c>; may be empty).</summary>
    public IReadOnlyList<XMeshBoneBounds> BoneBounds { get; init; } = Array.Empty<XMeshBoneBounds>();

    /// <summary>Number of bones in the b11 buffer (= <see cref="BoneNames"/>.Count).</summary>
    public int BoneCount => BoneNames.Count;

    /// <summary>Leading bones not in <see cref="ChildBones"/> (1: <c>tag_origin</c>), driven by the root transform.</summary>
    public int RootCount => BoneNames.Count - ChildBones.Count;

    /// <summary>True when the model has anything to animate (more than the root bone).</summary>
    public bool IsAnimatable => ChildBones.Count > 0 && RootCount >= 1;

    /// <summary>The skeleton of an xmesh cache (null when it has no child bones or the lists disagree).</summary>
    public static AnimSkeleton? From(XMeshData mesh) => Create(mesh.Bones, mesh.LocalBones, mesh.BaseMats, mesh.BoneBounds);

    /// <summary>The skeleton of a GPU mesh built from an xmesh cache (null when it has none).</summary>
    public static AnimSkeleton? From(GpuMesh mesh) => Create(mesh.Bones, mesh.LocalBones, mesh.BaseMats, mesh.BoneBounds);

    private static AnimSkeleton? Create(IReadOnlyList<XMeshBone> bones, IReadOnlyList<XMeshBone> local, IReadOnlyList<XMeshBaseMat> baseMats,
        IReadOnlyList<XMeshBoneBounds> boneBounds)
    {
        if (local.Count == 0 || bones.Count <= local.Count || baseMats.Count != bones.Count)
            return null;
        return new AnimSkeleton
        {
            BoneNames = bones.Select(b => b.Name).ToArray(),
            BaseMats = baseMats,
            ChildBones = local,
            BoneBounds = boneBounds,
        };
    }

    /// <summary>The bind pose of child bone <paramref name="i"/> as a parent-relative <see cref="AnimMat"/>.</summary>
    public AnimMat BindLocal(int i) => new(ChildBones[i].Rotation, ChildBones[i].Translation, 2f);

    private AnimMatrix43[]? _inverseBaseMats;

    /// <summary><c>MatToInverseMatrix43</c> of every base mat, which skinning applies every frame; computed once.</summary>
    internal ReadOnlySpan<AnimMatrix43> InverseBaseMats => _inverseBaseMats ??= BaseMats.Select(b =>
    {
        AnimMath.ToInverseMatrix(new AnimMat(b.Rotation, b.Translation, b.TransWeight), out var inv);
        return inv;
    }).ToArray();
}

/// <summary>Floats per bone in the b11 layout (<c>float3x4 objMatrixT</c> + <c>float4 extra</c>).</summary>
public static class AnimBoneLayout
{
    public const int FloatsPerBone = 16;
    public const int MaxBones = 1024;
}
