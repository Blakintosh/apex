using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using CallOfFile;

namespace Apex.Editor.Services.Preview.Formats;

/// <summary>
/// Loads XMODEL_EXPORT (v6/v7 text) and XMODEL_BIN (LZ4) files into a <see cref="PreviewModel"/>
/// using the vendored CallOfFile token reader. Data is left in BO3 space (Z-up, inches).
/// </summary>
public static class ModelReader
{
    private enum Section { None, Bones, Verts, Faces, Objects, Materials }

    /// <summary>A global (skinned) vertex from the NUMVERTS block.</summary>
    private struct GlobalVert
    {
        public Vector3 Position;
        public Vector4 Weights;
        public int Bone0, Bone1, Bone2, Bone3;
        public int WeightCount;
    }

    private struct Corner
    {
        public int VertIndex;
        public Vector3 Normal;
        public Vector2 UV;
    }

    public static Task<PreviewModel?> LoadAsync(string path, CancellationToken ct)
        => Task.Run(() => Load(path, ct), ct);

    private static PreviewModel? Load(string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        using var reader = ExportReaderSupport.OpenReader(path);
        if (reader is null)
            return null;

        var model = new PreviewModel();
        var verts = new List<GlobalVert>(1024);
        var partsByMaterial = new Dictionary<int, PartBuilder>();

        var section = Section.None;

        // Bone transform accumulation.
        int currentBone = -1;
        Vector3 boneRowX = Vector3.UnitX, boneRowY = Vector3.UnitY, boneRowZ = Vector3.UnitZ;

        // Face accumulation.
        int currentMaterial = 0;
        var curCorners = new List<Corner>(3);

        bool hasBounds = false;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);

        void FlushTriangle()
        {
            if (curCorners.Count < 3)
            {
                curCorners.Clear();
                return;
            }

            if (!partsByMaterial.TryGetValue(currentMaterial, out var builder))
            {
                builder = new PartBuilder();
                partsByMaterial[currentMaterial] = builder;
            }

            for (int i = 0; i < 3; i++)
            {
                var c = curCorners[i];
                var v = new PreviewVertex
                {
                    Normal = c.Normal,
                    UV = c.UV,
                };
                if (c.VertIndex >= 0 && c.VertIndex < verts.Count)
                {
                    var gv = verts[c.VertIndex];
                    v.Position = gv.Position;
                    v.Weights = gv.Weights;
                    v.Bone0 = gv.Bone0;
                    v.Bone1 = gv.Bone1;
                    v.Bone2 = gv.Bone2;
                    v.Bone3 = gv.Bone3;
                }
                builder.Add(c.VertIndex, v);
            }

            model.TriangleCount++;
            curCorners.Clear();
        }

        try
        {
            TokenData? td;
            while ((td = reader.RequestNextToken()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                var name = td.Token.Name;

                switch (name)
                {
                    case "NUMBONES":
                        section = Section.Bones;
                        break;

                    case "BONE":
                        if (td is TokenDataBoneInfo bi)
                        {
                            // Bone declaration line: BONE <index> <parent> "name".
                            while (model.Bones.Count <= bi.BoneIndex)
                                model.Bones.Add(new PreviewBone());
                            model.Bones[bi.BoneIndex] = new PreviewBone
                            {
                                Name = bi.Name,
                                ParentIndex = bi.BoneParentIndex,
                            };
                        }
                        else if (section == Section.Verts && td is TokenDataBoneWeight bw && verts.Count > 0)
                        {
                            // Vertex skin weight: BONE <boneIndex> <weight>.
                            var gv = verts[^1];
                            AddWeight(ref gv, bw.BoneIndex, bw.BoneWeight);
                            verts[^1] = gv;
                        }
                        else if (section == Section.Bones && td is TokenDataUInt bIdx)
                        {
                            // Bone transform block header: BONE <index>.
                            currentBone = (int)bIdx.Value;
                            boneRowX = Vector3.UnitX;
                            boneRowY = Vector3.UnitY;
                            boneRowZ = Vector3.UnitZ;
                        }
                        break;

                    case "OFFSET":
                        if (td is TokenDataVector3 off)
                        {
                            if (section == Section.Bones && currentBone >= 0 && currentBone < model.Bones.Count)
                                model.Bones[currentBone].WorldPosition = off.Value;
                            else if (section == Section.Verts && verts.Count > 0)
                            {
                                var gv = verts[^1];
                                gv.Position = off.Value;
                                verts[^1] = gv;
                                if (off.Value.X < min.X) min.X = off.Value.X;
                                if (off.Value.Y < min.Y) min.Y = off.Value.Y;
                                if (off.Value.Z < min.Z) min.Z = off.Value.Z;
                                if (off.Value.X > max.X) max.X = off.Value.X;
                                if (off.Value.Y > max.Y) max.Y = off.Value.Y;
                                if (off.Value.Z > max.Z) max.Z = off.Value.Z;
                                hasBounds = true;
                            }
                        }
                        break;

                    case "X":
                        if (section == Section.Bones && td is TokenDataVector3 rx) boneRowX = rx.Value;
                        break;
                    case "Y":
                        if (section == Section.Bones && td is TokenDataVector3 ry) boneRowY = ry.Value;
                        break;
                    case "Z":
                        if (section == Section.Bones && td is TokenDataVector3 rz)
                        {
                            boneRowZ = rz.Value;
                            if (currentBone >= 0 && currentBone < model.Bones.Count)
                                model.Bones[currentBone].WorldRotation = RowsToMatrix(boneRowX, boneRowY, boneRowZ);
                        }
                        break;

                    case "NUMVERTS":
                    case "NUMVERTS32":
                        section = Section.Verts;
                        break;

                    case "VERT":
                    case "VERT32":
                        if (section == Section.Verts)
                        {
                            verts.Add(new GlobalVert());
                        }
                        else if (section == Section.Faces && td is TokenDataUInt vref)
                        {
                            curCorners.Add(new Corner { VertIndex = (int)vref.Value });
                        }
                        break;

                    case "NUMFACES":
                        section = Section.Faces;
                        break;

                    case "TRI":
                    case "TRI16":
                        FlushTriangle();
                        if (td is TokenDataTri tri)
                            currentMaterial = tri.MaterialIndex;
                        break;

                    case "NORMAL":
                        if (section == Section.Faces && curCorners.Count > 0 && td is TokenDataVector3 nrm)
                        {
                            var c = curCorners[^1];
                            c.Normal = nrm.Value;
                            curCorners[^1] = c;
                        }
                        break;

                    case "UV":
                        if (section == Section.Faces && curCorners.Count > 0 && td is TokenDataUVSet uv && uv.UVs.Count > 0)
                        {
                            var c = curCorners[^1];
                            c.UV = uv.UVs[0];
                            curCorners[^1] = c;
                        }
                        break;

                    case "NUMOBJECTS":
                        FlushTriangle();
                        section = Section.Objects;
                        break;

                    case "NUMMATERIALS":
                        section = Section.Materials;
                        break;

                    case "MATERIAL":
                        if (td is TokenDataUIntStringX3 mat)
                            model.MaterialNames.Add(mat.StringValue1);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Lenient: keep whatever was parsed before a malformed/truncated token
            // (e.g. a _BIN using an unsupported TRI16 form in CallOfFile).
        }

        FlushTriangle();

        // Resolve material names and materialize parts in material order.
        var matIndices = new List<int>(partsByMaterial.Keys);
        matIndices.Sort();
        foreach (var matIndex in matIndices)
        {
            var builder = partsByMaterial[matIndex];
            var part = builder.Part;
            part.MaterialName = matIndex >= 0 && matIndex < model.MaterialNames.Count
                ? model.MaterialNames[matIndex]
                : $"material_{matIndex}";
            model.Parts.Add(part);
        }

        if (hasBounds)
        {
            model.BoundsMin = min;
            model.BoundsMax = max;
        }
        else
        {
            model.BoundsMin = Vector3.Zero;
            model.BoundsMax = Vector3.Zero;
        }

        return model;
    }

    private static void AddWeight(ref GlobalVert gv, int bone, float weight)
    {
        switch (gv.WeightCount)
        {
            case 0: gv.Bone0 = bone; gv.Weights.X = weight; break;
            case 1: gv.Bone1 = bone; gv.Weights.Y = weight; break;
            case 2: gv.Bone2 = bone; gv.Weights.Z = weight; break;
            case 3: gv.Bone3 = bone; gv.Weights.W = weight; break;
            default: return; // ignore >4 influences
        }
        gv.WeightCount++;
    }

    private static Matrix4x4 RowsToMatrix(Vector3 x, Vector3 y, Vector3 z) => new(
        x.X, x.Y, x.Z, 0f,
        y.X, y.Y, y.Z, 0f,
        z.X, z.Y, z.Z, 0f,
        0f, 0f, 0f, 1f);

    /// <summary>Accumulates deduplicated corners for a single material into a mesh part.</summary>
    private sealed class PartBuilder
    {
        public PreviewMeshPart Part { get; } = new();
        private readonly Dictionary<VertexKey, int> _lookup = new();

        public void Add(int vertIndex, in PreviewVertex v)
        {
            var key = new VertexKey(vertIndex, v.Normal, v.UV);
            if (!_lookup.TryGetValue(key, out var idx))
            {
                idx = Part.Vertices.Count;
                Part.Vertices.Add(v);
                _lookup[key] = idx;
            }
            Part.Indices.Add(idx);
        }
    }

    private readonly struct VertexKey : IEquatable<VertexKey>
    {
        private readonly int _vert;
        private readonly float _nx, _ny, _nz, _u, _v;

        public VertexKey(int vert, Vector3 n, Vector2 uv)
        {
            _vert = vert;
            _nx = n.X; _ny = n.Y; _nz = n.Z;
            _u = uv.X; _v = uv.Y;
        }

        public bool Equals(VertexKey o) =>
            _vert == o._vert && _nx == o._nx && _ny == o._ny && _nz == o._nz && _u == o._u && _v == o._v;

        public override bool Equals(object? obj) => obj is VertexKey o && Equals(o);

        public override int GetHashCode() => HashCode.Combine(_vert, _nx, _ny, _nz, _u, _v);
    }
}
