using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Apex.Editor.Services.Preview.Formats;

namespace Apex.Editor.Services.Preview;

/// <summary>
/// CPU-skins a <see cref="PreviewModel"/> against a <see cref="PreviewAnim"/>. XANIM v3 frames carry
/// ABSOLUTE per-part world transforms, so the per-bone skin matrix is inverse(bindWorld) * frameWorld
/// (row-vector convention); bones the anim does not drive hold their bind pose (identity skin).
/// Output part order matches <see cref="ModelSceneBuilder.BuildAsync"/> (empty parts skipped).
/// </summary>
public sealed class AnimSkinner
{
    private readonly PreviewAnim _anim;
    private readonly List<PreviewMeshPart> _parts;
    private readonly Matrix4x4[] _invBind;
    private readonly int[] _boneToAnimPart;
    private readonly Matrix4x4[] _skin;

    // Two output sets, alternated per frame: the viewport may still be uploading the previous set. Two suffice because
    // the next skin only starts after the previous set was handed over on the UI thread, where the GL upload also runs.
    private readonly float[][][] _buffers;
    private int _bufferIndex;

    /// <summary>Anim parts that matched a skeleton bone by name.</summary>
    public int MatchedParts { get; }

    public int TotalAnimParts => _anim.PartNames.Count;

    public AnimSkinner(PreviewModel model, PreviewAnim anim)
    {
        _anim = anim;
        _parts = model.Parts.Where(p => p.Indices.Count > 0).ToList();

        _invBind = new Matrix4x4[model.Bones.Count];
        _boneToAnimPart = new int[model.Bones.Count];
        _skin = new Matrix4x4[model.Bones.Count];

        var partIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < anim.PartNames.Count; i++)
            partIndexByName[anim.PartNames[i]] = i;

        int matched = 0;
        for (int b = 0; b < model.Bones.Count; b++)
        {
            var bone = model.Bones[b];
            var bind = bone.WorldRotation;
            bind.M41 = bone.WorldPosition.X;
            bind.M42 = bone.WorldPosition.Y;
            bind.M43 = bone.WorldPosition.Z;
            _invBind[b] = Matrix4x4.Invert(bind, out var inv) ? inv : Matrix4x4.Identity;

            if (partIndexByName.TryGetValue(bone.Name, out var part))
            {
                _boneToAnimPart[b] = part;
                matched++;
            }
            else
            {
                _boneToAnimPart[b] = -1;
            }
        }
        MatchedParts = matched;

        _buffers = new float[2][][];
        for (int set = 0; set < 2; set++)
        {
            _buffers[set] = new float[_parts.Count][];
            for (int i = 0; i < _parts.Count; i++)
                _buffers[set][i] = new float[_parts[i].Vertices.Count * 8];
        }
    }

    /// <summary>
    /// Skins all parts for <paramref name="frameIndex"/> into one of two reused buffer sets and
    /// returns it (one interleaved pos/normal/uv array per scene part, in scene part order).
    /// </summary>
    public float[][] SkinFrame(int frameIndex)
    {
        var output = _buffers[_bufferIndex];
        _bufferIndex ^= 1;

        if (_anim.Frames.Count == 0)
            return output;
        var frame = _anim.Frames[Math.Clamp(frameIndex, 0, _anim.Frames.Count - 1)];

        for (int b = 0; b < _skin.Length; b++)
        {
            var part = _boneToAnimPart[b];
            if (part < 0 || part >= frame.Positions.Length)
            {
                _skin[b] = Matrix4x4.Identity;
                continue;
            }
            var world = frame.Rotations[part];
            world.M41 = frame.Positions[part].X;
            world.M42 = frame.Positions[part].Y;
            world.M43 = frame.Positions[part].Z;
            _skin[b] = _invBind[b] * world;
        }

        // Parts are independent (each vertex's result depends only on its own weights), so they skin in parallel.
        Parallel.For(0, _parts.Count, p => SkinPart(CollectionsMarshal.AsSpan(_parts[p].Vertices), output[p]));
        return output;
    }

    private void SkinPart(ReadOnlySpan<PreviewVertex> verts, float[] data)
    {
        for (int i = 0; i < verts.Length; i++)
        {
            ref readonly var v = ref verts[i];
            var w = v.Weights;
            float wsum = w.X + w.Y + w.Z + w.W;

            Vector3 pos, nrm;
            if (wsum <= 1e-6f)
            {
                pos = v.Position;
                nrm = v.Normal;
            }
            else
            {
                pos = Vector3.Zero;
                nrm = Vector3.Zero;
                if (w.X > 0f) Accumulate(ref pos, ref nrm, v, v.Bone0, w.X);
                if (w.Y > 0f) Accumulate(ref pos, ref nrm, v, v.Bone1, w.Y);
                if (w.Z > 0f) Accumulate(ref pos, ref nrm, v, v.Bone2, w.Z);
                if (w.W > 0f) Accumulate(ref pos, ref nrm, v, v.Bone3, w.W);
                pos /= wsum;
                var len = nrm.Length();
                nrm = len > 1e-6f ? nrm / len : v.Normal;
            }

            int o = i * 8;
            data[o] = pos.X;
            data[o + 1] = pos.Y;
            data[o + 2] = pos.Z;
            data[o + 3] = nrm.X;
            data[o + 4] = nrm.Y;
            data[o + 5] = nrm.Z;
            data[o + 6] = v.UV.X;
            data[o + 7] = v.UV.Y;
        }
    }

    private static readonly Matrix4x4 Identity = Matrix4x4.Identity;

    private void Accumulate(ref Vector3 pos, ref Vector3 nrm, in PreviewVertex v, int bone, float weight)
    {
        ref readonly var m = ref bone >= 0 && bone < _skin.Length ? ref _skin[bone] : ref Identity;
        pos += Vector3.Transform(v.Position, m) * weight;
        nrm += Vector3.TransformNormal(v.Normal, m) * weight;
    }
}
