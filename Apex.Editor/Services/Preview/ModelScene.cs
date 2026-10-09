using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace Apex.Editor.Services.Preview;

/// <summary>Decoded RGBA8 pixels ready for GL upload (never an Avalonia bitmap).</summary>
public sealed class RawTexture
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; init; }
}

/// <summary>One renderable mesh part: interleaved vertices plus resolved textures (null slots are normal).</summary>
public sealed class ModelScenePart
{
    public string MaterialName { get; init; } = string.Empty;

    /// <summary>Interleaved position(3) + normal(3) + uv(2), 8 floats per vertex, BO3 space (Z-up, inches).</summary>
    public required float[] VertexData { get; init; }

    /// <summary>Triangle-list indices into <see cref="VertexData"/> (vertex granularity).</summary>
    public required uint[] Indices { get; init; }

    public RawTexture? ColorMap { get; init; }
    public RawTexture? NormalMap { get; init; }
    public RawTexture? SpecColorMap { get; init; }

    /// <summary>Dedicated gloss mask (GDT <c>cosinePowerMap</c>); r channel, remapped by the gloss range.</summary>
    public RawTexture? GlossMap { get; init; }

    /// <summary>Ambient occlusion (GDT <c>occMap</c>); r channel, applied to ambient light only.</summary>
    public RawTexture? OcclusionMap { get; init; }

    /// <summary>Gloss-map remap endpoints normalized to [0,1] (BO3 authors gloss on a 0–17 scale).</summary>
    public float GlossRangeMin { get; init; }
    public float GlossRangeMax { get; init; } = 1f;
}

/// <summary>
/// A GL-agnostic snapshot of a model ready for the preview viewport. Topology, textures and part
/// layout are immutable; vertex data may be swapped wholesale per part (anim playback) via
/// <see cref="UpdateVertices"/> — static scenes never pay for this (<see cref="Version"/> stays 0).
/// </summary>
public sealed class ModelScene
{
    public required IReadOnlyList<ModelScenePart> Parts { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public int TriangleCount { get; init; }

    /// <summary>What the scene previews (the asset record): the viewport keeps the user's camera while scenes of the same
    /// subject replace each other (a reload) and frames a new subject.</summary>
    public object? Subject { get; set; }

    private float[]?[]? _dynamicVertices;
    private int _version;

    /// <summary>Bumped once per <see cref="UpdateVertices"/> batch; the viewport re-uploads on change.</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Raised (on the updating thread) after a vertex batch lands; the viewport schedules a render.</summary>
    public event Action? VerticesUpdated;

    /// <summary>
    /// Swaps in new interleaved vertex data (same layout and vertex count as the part's static data)
    /// for each part, one array per part in part order. Null entries keep the previous data.
    /// </summary>
    public void UpdateVertices(IReadOnlyList<float[]?> perPart)
    {
        var slots = _dynamicVertices ??= new float[]?[Parts.Count];
        for (int i = 0; i < Parts.Count && i < perPart.Count; i++)
        {
            if (perPart[i] is { } data)
                Volatile.Write(ref slots[i], data);
        }
        Interlocked.Increment(ref _version);
        VerticesUpdated?.Invoke();
    }

    /// <summary>The vertex data the viewport should currently show for a part.</summary>
    public float[] CurrentVertexData(int partIndex)
    {
        var slots = _dynamicVertices;
        return (slots is null ? null : Volatile.Read(ref slots[partIndex])) ?? Parts[partIndex].VertexData;
    }
}
