using System.Numerics;
using Apex.Render.Data.Assets;

namespace Apex.Render.Assets;

/// <summary>
/// APE's automatic xmodel LOD selection (ToolsGfx scene submission; mode switch at 0x140564EC0). The GDT
/// <c>*LodDist</c> values are not used: each LOD gets a size metric at load, and a view picks the coarsest LOD whose
/// projected metric stays within one texel (orthographic shadow views) or within the camera distance (main view).
/// Reproduces the barrel's captured LODs (main 0, cascades 1 / 4 / 4).
/// </summary>
public static class XModelLod
{
    /// <summary><c>lod[i] = G0 · T0 / Ti</c>: G0 = geometric mean of LOD0 triangle areas (each ≥ 1e-4), T = triangles.</summary>
    public static float[] Metrics(IReadOnlyList<GpuMesh> lods)
    {
        double acc = 0;
        long n = 0;
        foreach (var s in lods[0].Surfaces)
        {
            for (int t = 0; t < s.TriangleCount; t++)
            {
                var a = s.Vertices[s.Indices[t * 3]].Position;
                var b = s.Vertices[s.Indices[t * 3 + 1]].Position;
                var c = s.Vertices[s.Indices[t * 3 + 2]].Position;
                float area = Vector3.Cross(c - a, b - a).Length() * 0.5f;
                if (area < 0.000099999997f)
                    area = 0.000099999997f;
                acc += Math.Log(area);
                n++;
            }
        }
        float g0 = n == 0 ? 0f : (float)Math.Exp(acc / n);
        int t0 = lods[0].Surfaces.Sum(s => s.TriangleCount);
        return lods.Select(m => g0 * t0 / Math.Max(1, m.Surfaces.Sum(s => s.TriangleCount))).ToArray();
    }

    /// <summary><c>X = CamToClp._11 · CamToClp._22 · (W &gt;&gt; 1) · (H &gt;&gt; 1)</c> for the view's render-target size.</summary>
    public static float ViewScale(in Matrix4x4 camToClp, int width, int height)
        => camToClp.M11 * camToClp.M22 * (width >> 1) * (height >> 1);

    /// <summary>Orthographic (sun-shadow) views: −1 when culled (<c>radius·P &lt; 1</c>), otherwise the number of
    /// leading LODs with <c>√(scale·lod[i])·P ≤ 1</c>, clamped to the last LOD. <c>P = √X</c>.</summary>
    public static int ShadowLod(IReadOnlyList<float> metrics, float radius, in Matrix4x4 camToClp, int width, int height, float scale = 1f)
    {
        float p = MathF.Sqrt(ViewScale(camToClp, width, height));
        if (radius * p < 1f)
            return -1;
        int lod = 0;
        while (lod < metrics.Count && MathF.Sqrt(scale * metrics[lod]) * p <= 1f)
            lod++;
        return Math.Min(lod, metrics.Count - 1);
    }

    /// <summary>Perspective (main) view: <c>d = |camPos − centre| − radius</c>; LOD 0 when d ≤ 0, otherwise the number
    /// of leading LODs with <c>√(scale·lod[i])·Q ≤ d</c>, clamped. <c>Q = √(X/4 + 1/π)</c>.</summary>
    public static int MainLod(IReadOnlyList<float> metrics, Vector3 center, float radius, Vector3 cameraPosition, in Matrix4x4 camToClp,
        int width, int height, float scale = 1f)
    {
        float d = Vector3.Distance(cameraPosition, center) - radius;
        if (d <= 0f)
            return 0;
        float q = MathF.Sqrt(ViewScale(camToClp, width, height) / 4f + 1f / MathF.PI);
        int lod = 0;
        while (lod < metrics.Count && MathF.Sqrt(scale * metrics[lod]) * q <= d)
            lod++;
        return Math.Min(lod, metrics.Count - 1);
    }
}
