using System.Text;
using Apex.Render.Data.Assets;
using Apex.Render.Data.Hashing;

namespace Apex.Render.Data.Conversion.XModel;

/// <summary>
/// Converts an <c>.xmodel_bin</c> into the payloads APE caches under <c>ToolsGfx\xmeshes\v39</c> and
/// <c>ToolsGfx\xbins\v1</c> (asset_caches.md §6.2/§6.4, conversion.md §1). Pure: reads the source files, writes
/// nothing; thread-safe.
/// </summary>
public static class XModelConverter
{
    /// <summary>
    /// Builds the xmesh v39 payload of one LOD.
    /// </summary>
    /// <param name="xmodelBinPath">The LOD's <c>.xmodel_bin</c>.</param>
    /// <param name="parameters">GDT scale and autogen priorities (<see cref="XModelLodParameters.FromGdt"/>).</param>
    /// <param name="percent">100 for an authored LOD; below 100 an autogen LOD simplified from <paramref name="lod0Path"/>.</param>
    /// <param name="lod0Path">For autogen LODs: the LOD0 file whose face count sets the simplification target.</param>
    public static byte[] ConvertXMesh(string xmodelBinPath, XModelLodParameters parameters, int percent = 100, string? lod0Path = null)
    {
        if (!xmodelBinPath.EndsWith(".xmodel_bin", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported xmodel file format '{xmodelBinPath}'.");
        var src = XModelSource.Load(xmodelBinPath);
        if (percent < 100)
        {
            var lod0 = lod0Path is null ? src : XModelSource.Load(lod0Path);
            float frac = Math.Clamp(percent * 0.0099999998f, 0f, 1.0f);
            int target = (int)(lod0.Faces.Count * frac);
            if (target <= 16)
                target = 16;
            throw new NotSupportedException($"Autogen LOD simplification ({percent}%, target {target} faces) is not implemented.");
        }
        var mesh = XMeshConverter.Convert(src, parameters.Scale);
        return XMeshWriter.Write(mesh);
    }

    /// <summary>
    /// The skeleton of an <c>.xmodel_bin</c> as an xmesh without surfaces (bones, local bones and base mats computed
    /// exactly as <see cref="ConvertXMesh"/> does). For bones-only models such as the <c>*_skeleton.xmodel_bin</c> an
    /// xanim is exported against, which APE cannot convert ("no surfaces") and so never caches. Not written anywhere.
    /// </summary>
    public static XMeshData ConvertSkeleton(string xmodelBinPath, XModelLodParameters parameters)
    {
        var src = XModelSource.Load(xmodelBinPath, bonesOnly: true);
        var mesh = XMeshConverter.ConvertSkeleton(src, parameters.Scale);
        return XMeshData.Parse(XMeshWriter.Write(mesh), xmodelBinPath);
    }

    /// <summary>Cache file name of an xmesh: <c>&lt;xmodelName&gt;\pct&lt;P&gt;_&lt;hash&gt;.lz4</c> relative to <c>xmeshes\v39</c>.</summary>
    public static string XMeshCacheRelativePath(string xmodelBinPath, XModelLodParameters parameters, int percent = 100, string? autogenSourcePath = null)
    {
        var sig = CacheKeys.FileSignature(xmodelBinPath);
        var autoSig = autogenSourcePath is not null && File.Exists(autogenSourcePath) ? CacheKeys.FileSignature(autogenSourcePath) : default;
        var hash = CacheKeys.XMeshHash(sig, parameters, autogenSourcePath, autoSig);
        return Path.Combine(Path.GetFileNameWithoutExtension(xmodelBinPath).ToLowerInvariant(), $"pct{percent}_{hash.ToFileName()}.lz4");
    }

    /// <summary>
    /// The xbin v1 payload (0x14067E970): the xmodel_bin's material names, lower-cased, 128 bytes each.
    /// </summary>
    public static byte[] ConvertXBin(string xmodelBinPath)
    {
        var src = XModelSource.Load(xmodelBinPath);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((uint)src.Materials.Count);
        foreach (var m in src.Materials)
            w.Write(m, 0, 128);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Cache file name of an xbin: <c>&lt;xmodelName&gt;\&lt;Sig(xmodel_bin)&gt;.lz4</c> relative to <c>xbins\v1</c>.</summary>
    public static string XBinCacheRelativePath(string xmodelBinPath) =>
        Path.Combine(Path.GetFileNameWithoutExtension(xmodelBinPath).ToLowerInvariant(), CacheKeys.FileSignature(xmodelBinPath).ToFileName() + ".lz4");

    /// <summary>Material names of an xbin payload.</summary>
    public static IReadOnlyList<string> ReadXBinMaterials(byte[] payload)
    {
        int n = BitConverter.ToInt32(payload, 0);
        var list = new List<string>(n);
        for (int i = 0; i < n; i++)
        {
            var span = payload.AsSpan(4 + 128 * i, 128);
            int z = span.IndexOf((byte)0);
            list.Add(Encoding.Latin1.GetString(z < 0 ? span : span[..z]));
        }
        return list;
    }
}
