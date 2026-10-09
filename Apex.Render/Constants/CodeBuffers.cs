using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Apex.Render.Constants;

/// <summary>Fixed names/slots/sizes of the engine buffers (<c>Material_BuildDefaultConstantBuffer</c> rejects variants
/// that disagree: "slot/size mismatch ... Recompile").</summary>
public static class CodeBuffer
{
    public const string SceneName = "CodeSceneConstBuffer";
    public const string ObjectName = "CodeObjectConstBuffer";
    public const string BonesName = "CodeObjectBonesConstBuffer";
    public const string SsaoName = "CodeSSAOConstBuffer";
    public const string PostFxName = "CodePostFxConstBuffer";

    public const int SceneSlot = 9, ObjectSlot = 10, BonesSlot = 11, SsaoSlot = 12, PostFxSlot = 13;

    public static int SizeOf(string name) => name switch
    {
        SceneName => CodeSceneConsts.Size,
        ObjectName => CodeObjectConsts.Size,
        BonesName => CodeObjectBonesConsts.Size,
        SsaoName => CodeSsaoConsts.Size,
        PostFxName => CodePostFxConsts.Size,
        _ => -1,
    };

    public static int SlotOf(string name) => name switch
    {
        SceneName => SceneSlot,
        ObjectName => ObjectSlot,
        BonesName => BonesSlot,
        SsaoName => SsaoSlot,
        PostFxName => PostFxSlot,
        _ => -1,
    };
}

[InlineArray(7)]
public struct Vector4Array7 { private Vector4 _e0; }

[InlineArray(5)]
public struct Vector4Array5 { private Vector4 _e0; }

/// <summary><c>CodeObjectConsts</c> — cbuffer b10 (208 B); also the element of <c>gObjectInstanceData</c> t30.</summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct CodeObjectConsts
{
    public const int Size = 208;

    [FieldOffset(0)] public Matrix4x4 WorldMatrix;
    [FieldOffset(64)] public Vector4Array7 CustomFloat4s;
    [FieldOffset(176)] public int CustomInt0;
    [FieldOffset(180)] public int CustomInt1;
    [FieldOffset(184)] public int CustomInt2;
    [FieldOffset(188)] public int CustomInt3;
    [FieldOffset(192)] public uint HasBones;
    [FieldOffset(196)] public uint HasSiegeAnim;
    [FieldOffset(200)] public float SiegeAnimOffset;
    [FieldOffset(204)] public float SortDepth;

    public static CodeObjectConsts Identity => new() { WorldMatrix = Matrix4x4.Identity };

    /// <summary>
    /// cb10 as the post-FX / compute passes see it (compute/post copy at 0x1418ac730): identity world and
    /// <c>customInt4s = (0, postDesc+908, postDesc+909, useLUT ? 0 : 1)</c> — (0,0,0,1) in the preview.
    /// </summary>
    public static CodeObjectConsts ForPostFx(bool useLut = false, int linearDebug = 0, int passThroughCurve = 0)
    {
        var c = Identity;
        c.CustomInt1 = passThroughCurve;
        c.CustomInt2 = linearDebug;
        c.CustomInt3 = useLut ? 0 : 1;
        return c;
    }
}

/// <summary>One skinning matrix: <c>objMatrixT</c> is the 3×4 transpose of the row-vector affine bone matrix.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct CodeObjectBonesConst
{
    [FieldOffset(0)] public Vector4 Row0;
    [FieldOffset(16)] public Vector4 Row1;
    [FieldOffset(32)] public Vector4 Row2;
    [FieldOffset(48)] public Vector4 Extra;

    /// <summary>From a row-vector (<c>v' = v · M</c>) affine matrix: row i = column i of <paramref name="m"/>.</summary>
    public static CodeObjectBonesConst FromAffine(in Matrix4x4 m) => new()
    {
        Row0 = new Vector4(m.M11, m.M21, m.M31, m.M41),
        Row1 = new Vector4(m.M12, m.M22, m.M32, m.M42),
        Row2 = new Vector4(m.M13, m.M23, m.M33, m.M43),
    };
}

[InlineArray(CodeObjectBonesConsts.MaxBones)]
public struct BoneArray { private CodeObjectBonesConst _e0; }

/// <summary><c>CodeObjectBonesConstBuffer</c> b11: 1024 × 64 B = 65536 B.</summary>
[StructLayout(LayoutKind.Sequential, Size = Size)]
public struct CodeObjectBonesConsts
{
    public const int MaxBones = 1024;
    public const int Size = MaxBones * 64;

    public BoneArray Bones;
}

/// <summary><c>CodeSSAOConsts</c> / <c>CodeHemiAOConsts</c> — cbuffer b12 (160 B).</summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct CodeSsaoConsts
{
    public const int Size = 160;

    [FieldOffset(0)] public Vector2 InvSourceDimension;
    [FieldOffset(8)] public float ZClear;
    [FieldOffset(16)] public Vector4Array3 InvThicknessTable;
    [FieldOffset(64)] public Vector4Array3 SampleWeightTable;
    [FieldOffset(112)] public Vector2 InvSliceDimension;
    [FieldOffset(120)] public float RejectFadeoff;
    [FieldOffset(124)] public float Insensitivity;
    [FieldOffset(128)] public Vector2 InvLowResolution;
    [FieldOffset(136)] public Vector2 InvHighResolution;
    [FieldOffset(144)] public float NoiseFilterStrength;
    [FieldOffset(148)] public float StepSize;
    [FieldOffset(152)] public float BlurTolerance;
    [FieldOffset(156)] public float UpsampleTolerance;
}

/// <summary><c>CodePostFxConsts</c> — cbuffer b13 (704 B).</summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct CodePostFxConsts
{
    public const int Size = 704;

    [FieldOffset(0)] public Vector4 UnderwaterPostFxControl0;
    [FieldOffset(16)] public Vector4 UnderwaterPostFxControl1;
    [FieldOffset(32)] public Vector4 UnderwaterPostFxControl2;
    [FieldOffset(48)] public Vector4 UnderwaterPostFxControl3;
    [FieldOffset(64)] public Vector4 BloomEV;
    [FieldOffset(80)] public Vector4 BloomPWR;
    [FieldOffset(96)] public Vector4 BloomRGB1;
    [FieldOffset(112)] public Vector4 BloomLUM1;
    [FieldOffset(128)] public Vector4 BloomRGB2;
    [FieldOffset(144)] public Vector4 BloomLUM2;
    [FieldOffset(160)] public Vector4 BloomRGB3;
    [FieldOffset(176)] public Vector4 BloomLUM3;
    [FieldOffset(192)] public Vector4 BloomRGB4;
    [FieldOffset(208)] public Vector4 BloomLUM4;
    [FieldOffset(224)] public Vector4 BloomRGB5;
    [FieldOffset(240)] public Vector4 BloomLUM5;
    [FieldOffset(256)] public Vector4 BloomLIM;
    [FieldOffset(272)] public Vector4 BloomLIA;
    [FieldOffset(288)] public Vector4 BloomLIG;
    [FieldOffset(304)] public Vector4 BloomLOM;
    [FieldOffset(320)] public Vector4 BloomLOA;
    [FieldOffset(336)] public Vector4 BloomMxR;
    [FieldOffset(352)] public Vector4 BloomMxG;
    [FieldOffset(368)] public Vector4 BloomMxB;
    [FieldOffset(384)] public Vector4Array5 BloomMIP;
    [FieldOffset(464)] public Vector4 ColorLumaM;
    [FieldOffset(480)] public Vector4 ColorRangeM;
    [FieldOffset(496)] public Vector4 ColorRangeA;
    [FieldOffset(512)] public Vector4 ColorMtxSR;
    [FieldOffset(528)] public Vector4 ColorMtxSG;
    [FieldOffset(544)] public Vector4 ColorMtxSB;
    [FieldOffset(560)] public Vector4 ColorMtxMR;
    [FieldOffset(576)] public Vector4 ColorMtxMG;
    [FieldOffset(592)] public Vector4 ColorMtxMB;
    [FieldOffset(608)] public Vector4 ColorMtxHR;
    [FieldOffset(624)] public Vector4 ColorMtxHG;
    [FieldOffset(640)] public Vector4 ColorMtxHB;
    [FieldOffset(656)] public Vector4 ColorMixR;
    [FieldOffset(672)] public Vector4 ColorMixG;
    [FieldOffset(688)] public Vector4 ColorMixB;

    /// <summary>
    /// <c>Gfx_DefaultPostFxConsts</c> (0x140366be0): what the preview uploads (bloom/underwater off). Everything 0
    /// except the colour block below; identical in all four captured presets.
    /// </summary>
    public static CodePostFxConsts PreviewDefaults => new()
    {
        ColorLumaM = new Vector4(0.2126f, 0.7152f, 0.0722f, 0),
        ColorRangeM = new Vector4(-1, 1, 2, -2),
        ColorRangeA = new Vector4(1, 0, 0, 2),
        ColorMixR = new Vector4(1, 0, 0, 1),
        ColorMixG = new Vector4(0, 1, 0, 1),
        ColorMixB = new Vector4(0, 0, 0, 1),
    };
}
