using System.Runtime.InteropServices;

namespace Apex.Render.Data.Conversion;

/// <summary>
/// The C runtime math functions APE calls, taken from the same DLL APE imports them from: <c>MSVCR110.dll</c>
/// (the Visual C++ 2012 runtime every mod-tools install needs; APE's <c>acosf</c>/<c>log</c>/<c>exp</c>
/// imports go there). Transcendentals differ in the last bit between CRT versions, so converting byte-exactly
/// needs this exact implementation. Loaded at runtime from the system, never redistributed; when it cannot be
/// loaded the .NET implementations are used (results then may differ from APE by one ulp in rare cases).
/// </summary>
public static unsafe class CrtMath
{
    private static readonly delegate* unmanaged[Cdecl]<float, float> AcosF;
    private static readonly delegate* unmanaged[Cdecl]<double, double> LogD;
    private static readonly delegate* unmanaged[Cdecl]<double, double> ExpD;
    private static readonly delegate* unmanaged[Cdecl]<double, double, double> PowD;
    private static readonly delegate* unmanaged[Cdecl]<float, float, float> PowF;

    /// <summary>True when <c>MSVCR110.dll</c> was loaded (conversions are bit-exact with APE).</summary>
    public static bool IsExact { get; }

    static CrtMath()
    {
        if (!OperatingSystem.IsWindows() || !NativeLibrary.TryLoad("msvcr110.dll", typeof(CrtMath).Assembly, DllImportSearchPath.System32, out var h))
            return;
        if (NativeLibrary.TryGetExport(h, "acosf", out var p)) AcosF = (delegate* unmanaged[Cdecl]<float, float>)p;
        if (NativeLibrary.TryGetExport(h, "log", out p)) LogD = (delegate* unmanaged[Cdecl]<double, double>)p;
        if (NativeLibrary.TryGetExport(h, "exp", out p)) ExpD = (delegate* unmanaged[Cdecl]<double, double>)p;
        if (NativeLibrary.TryGetExport(h, "pow", out p)) PowD = (delegate* unmanaged[Cdecl]<double, double, double>)p;
        if (NativeLibrary.TryGetExport(h, "powf", out p)) PowF = (delegate* unmanaged[Cdecl]<float, float, float>)p;
        IsExact = AcosF != null && LogD != null && ExpD != null;
    }

    public static float Acos(float x) => AcosF != null ? AcosF(x) : MathF.Acos(x);
    public static double Log(double x) => LogD != null ? LogD(x) : Math.Log(x);
    public static double Exp(double x) => ExpD != null ? ExpD(x) : Math.Exp(x);
    public static double Pow(double x, double y) => PowD != null ? PowD(x, y) : Math.Pow(x, y);
    public static float PowF32(float x, float y) => PowF != null ? PowF(x, y) : MathF.Pow(x, y);
}
