using System.Collections.Generic;
using System.Numerics;

namespace Apex.Editor.Services.Preview.Formats;

/// <summary>One animation frame: a pose (position + rotation) per part, indexed by part order.</summary>
public sealed class PreviewAnimFrame
{
    public Vector3[] Positions { get; }
    public Matrix4x4[] Rotations { get; }

    public PreviewAnimFrame(int partCount)
    {
        Positions = new Vector3[partCount];
        Rotations = new Matrix4x4[partCount];
        for (int i = 0; i < partCount; i++)
            Rotations[i] = Matrix4x4.Identity;
    }
}

/// <summary>A parsed XANIM (v3) ready for preview, left in BO3 space (Z-up, inches).</summary>
public sealed class PreviewAnim
{
    public List<string> PartNames { get; } = new();
    public int FrameCount { get; set; }
    public float FrameRate { get; set; } = 30f;
    public List<PreviewAnimFrame> Frames { get; } = new();
    public List<(string Name, int Frame)> Notetracks { get; } = new();

    /// <summary>What the reader could not read (lowercase, for the preview's stats line), or null when it read it all.</summary>
    public string? ReadProblem { get; set; }
}
