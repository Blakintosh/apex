using Apex.Render.Constants;
using Apex.Render.Targets;

namespace Apex.Render.Passes;

/// <summary>
/// <c>CodeSSAOConsts</c> (b12) for the HemiAO dispatches, as <c>Gfx_RenderSSAO_HemiAO</c> (0x1404a3250) builds them —
/// Microsoft MiniEngine's SSAO. Nothing depends on the camera: the <c>TanHalfFovH</c> argument is a constant 1.0, and
/// the tolerances are fixed constants (blur −3, upsample −5, noise filter −3, as log10). Reproduces all captured cb12
/// bytes bit-exact.
/// </summary>
public static class HemiAoConstants
{
    // SampleThickness² as stored in the exe (.rdata 0x1408FAF3C..68).
    private static readonly uint[] ThicknessSquaredBits =
    [
        0x3f75c28f, 0x3f570a3d, 0x3f23d70a, 0x3eb851ea, 0x3f6b851e, 0x3f4ccccc,
        0x3f199999, 0x3ea3d708, 0x3f2e147a, 0x3ef5c28e, 0x3e4cccc8, 0x3e8f5c28,
    ];

    private const float TanHalfFovH = 1f;
    private const float RejectionFalloff = 0.4f;
    private const float Accentuation = 1.25f;
    private const float BlurToleranceLog10 = -3f, UpsampleToleranceLog10 = -5f, NoiseFilterToleranceLog10 = -3f;

    /// <summary>b12 of a render dispatch over a <paramref name="width"/>×<paramref name="height"/> source
    /// (slice size for the interleaved atlases).</summary>
    public static CodeSsaoConsts Render(int width, int height)
    {
        var c = new CodeSsaoConsts();
        Span<float> th = stackalloc float[12];
        for (int i = 0; i < 12; i++)
            th[i] = MathF.Sqrt(BitConverter.UInt32BitsToSingle(ThicknessSquaredBits[i]));

        // MiniEngine doubles the thickness multiplier for array size 1; a Texture2D reports 0, so it never does.
        float thicknessMultiplier = TanHalfFovH * 2f * 10f / width;
        float inverseRangeFactor = 1f / thicknessMultiplier;
        for (int i = 0; i < 12; i++)
            SetTable(ref c.InvThicknessTable, i, inverseRangeFactor / th[i]);

        Span<float> w = stackalloc float[12];
        w[0] = 0; w[1] = th[1] * 4f; w[2] = 0; w[3] = th[3] * 4f; w[4] = th[4] * 4f; w[5] = 0;
        w[6] = th[6] * 8f; w[7] = 0; w[8] = th[8] * 4f; w[9] = 0; w[10] = 0; w[11] = th[11] * 4f;
        float sum = 0f;
        for (int i = 0; i < 12; i++)
            sum += w[i];
        for (int i = 0; i < 12; i++)
            SetTable(ref c.SampleWeightTable, i, w[i] / sum);

        c.InvSliceDimension = new System.Numerics.Vector2(1f / width, 1f / height);
        c.RejectFadeoff = RejectionFalloff;
        c.Insensitivity = Accentuation;
        return c;
    }

    /// <summary>b12 of the blur-and-upsample dispatch: <paramref name="low"/> = DS4x, <paramref name="high"/> = DS2x,
    /// <paramref name="destinationWidth"/> = AoResult width.</summary>
    public static CodeSsaoConsts BlurUpsample((int W, int H) low, (int W, int H) high, int destinationWidth)
    {
        float dest = destinationWidth;
        float blur = 1f - MathF.Pow(10f, BlurToleranceLog10) * dest / low.W;
        blur *= blur;
        float upsample = MathF.Pow(10f, UpsampleToleranceLog10);
        return new CodeSsaoConsts
        {
            InvLowResolution = new System.Numerics.Vector2(1f / low.W, 1f / low.H),
            InvHighResolution = new System.Numerics.Vector2(1f / high.W, 1f / high.H),
            NoiseFilterStrength = 1f / (MathF.Pow(10f, NoiseFilterToleranceLog10) + upsample),
            StepSize = dest / low.W,
            BlurTolerance = blur,
            UpsampleTolerance = upsample,
        };
    }

    /// <summary>The four b12 blocks of the preview chain (dispatches 2–5) for a window size.</summary>
    public static CodeSsaoConsts[] ForWindow(int width, int height)
    {
        (int W, int H) Size(int id) => RenderTargetTable.Get(id).SizeFor(width, height);
        var ds4a = Size(RenderTargetId.HemiAoDepth4xAtlas);
        var ds4 = Size(RenderTargetId.HemiAoDepth4x);
        var ds2a = Size(RenderTargetId.HemiAoDepth2xAtlas);
        var ds2 = Size(RenderTargetId.HemiAoDepth2x);
        var result = Size(RenderTargetId.Ssao);
        return
        [
            Render(ds4a.W, ds4a.H),
            Render(ds4.W, ds4.H),
            Render(ds2a.W, ds2a.H),
            BlurUpsample(ds4, ds2, result.W),
        ];
    }

    private static void SetTable(ref Vector4Array3 table, int i, float v)
    {
        var e = table[i / 4];
        switch (i % 4)
        {
            case 0: e.X = v; break;
            case 1: e.Y = v; break;
            case 2: e.Z = v; break;
            default: e.W = v; break;
        }
        table[i / 4] = e;
    }
}
