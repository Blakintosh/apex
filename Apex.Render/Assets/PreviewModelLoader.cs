using System.Numerics;
using System.Runtime.ExceptionServices;
using Apex.Render.Constants;
using Apex.Render.Data;
using Apex.Render.Data.Assets;
using Apex.Render.Data.Hashing;
using Apex.Render.Data.Techsets;
using Apex.Render.Passes;
using Apex.Render.Resources;
using Avalonia.Threading;
using Vortice.DXGI;

namespace Apex.Render.Assets;

/// <summary>A model loaded for the preview: one GPU vertex/index buffer per LOD and its draw items.</summary>
public sealed class PreviewModel : IDisposable
{
    public required string Name { get; init; }
    public required IReadOnlyList<PreviewDrawItem> Items { get; init; }
    /// <summary>Per LOD: (surfaces, triangles) — diagnostics.</summary>
    public required IReadOnlyList<(int Surfaces, int Triangles)> Lods { get; init; }
    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }
    /// <summary>Per-LOD metric <c>G0·T0/Ti</c> (G0 = geometric mean of LOD0 triangle areas) that APE's LOD selection uses.</summary>
    public required float[] LodMetrics { get; init; }
    /// <summary>Union bounds over all LODs (APE's model bounds): centre and radius |max − min| / 2.</summary>
    public Vector3 BoundsCenter => (BoundsMin + BoundsMax) * 0.5f;
    public float BoundsRadius => (BoundsMax - BoundsMin).Length() * 0.5f;
    /// <summary>Items per LOD with every technique the material has (see <see cref="Select"/>).</summary>
    public required IReadOnlyList<IReadOnlyList<PreviewDrawItem>> LodItems { get; init; }
    /// <summary>Materials/images that could not be built (see <see cref="PreviewModelOptions.SkipBrokenMaterials"/>).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    internal List<IDisposable> Owned { get; } = new();

    /// <summary>
    /// The draws of one frame: <paramref name="mainLod"/> for the main-view stages and, per sun-shadow cascade, the
    /// LOD in <paramref name="shadowLods"/> (−1 = culled). See <see cref="XModelLod"/> for APE's selection.
    /// </summary>
    /// <param name="pose">Skinning for this frame (null = bind pose): each posed LOD's items carry its bone buffer and
    /// <c>hasBones = 1</c>. Like APE, a model playing an animation is drawn in the main view only — the anim captures
    /// show empty sun-shadow cascades while the same model un-animated casts (captures/anim/README.md).</param>
    public IReadOnlyList<IPreviewDrawItem> Select(int mainLod, ReadOnlySpan<int> shadowLods, IPreviewPose? pose = null)
    {
        // The items only depend on which LODs draw where and on each posed LOD's bone memory (read when drawn), so the
        // previous selection is returned again while that is unchanged.
        var key = _selectionScratch;
        key.Clear();
        for (int lod = 0; lod < LodItems.Count; lod++)
        {
            bool animated = pose?.Animates(lod) == true;
            int mask = 0;
            if (!animated)
                for (int c = 0; c < shadowLods.Length; c++)
                    if (shadowLods[c] == lod) mask |= 1 << c;
            bool main = lod == mainLod;
            if (!main && mask == 0)
                continue;
            key.Add((lod, main, mask, animated ? pose!.Bones(lod) : ReadOnlyMemory<byte>.Empty));
        }
        if (_selection != null && key.SequenceEqual(_selectionKey))
            return _selection;

        var list = new List<IPreviewDrawItem>();
        foreach (var (lod, main, mask, bones) in key)
            foreach (var item in LodItems[lod])
                list.Add(new LodDrawItem(item, main, mask, bones));
        (_selectionKey, _selectionScratch) = (key, _selectionKey);
        return _selection = list;
    }

    private List<(int Lod, bool Main, int Mask, ReadOnlyMemory<byte> Bones)> _selectionKey = new(), _selectionScratch = new();
    private IReadOnlyList<IPreviewDrawItem>? _selection;

    /// <summary>APE's per-frame LOD choice for this model (<see cref="XModelLod"/>): the main view from the camera
    /// origin and <paramref name="scene"/>'s projection, each sun cascade from its orthographic projection (1024²).</summary>
    public IReadOnlyList<IPreviewDrawItem> SelectForFrame(in CodeSceneConsts scene, IReadOnlyList<CodeSceneConsts>? shadowViews, Vector3 cameraOrigin,
        IPreviewPose? pose = null)
    {
        int main = XModelLod.MainLod(LodMetrics, BoundsCenter, BoundsRadius, cameraOrigin, scene.Transforms.CamToClp,
            (int)scene.RenderTargetWidth, (int)scene.RenderTargetHeight);
        Span<int> shadow = stackalloc int[shadowViews?.Count ?? 0];
        for (int i = 0; i < shadow.Length; i++)
        {
            var v = shadowViews![i];
            shadow[i] = XModelLod.ShadowLod(LodMetrics, BoundsRadius, v.Transforms.CamToClp, (int)v.RenderTargetWidth, (int)v.RenderTargetHeight);
        }
        if (LastSelection.Main != main || LastSelection.Shadow is not { } last || !shadow.SequenceEqual(last))
            LastSelection = (main, shadow.ToArray());
        return Select(main, shadow, pose);
    }

    /// <summary>The LODs <see cref="SelectForFrame"/> picked last (diagnostics).</summary>
    public (int Main, int[] Shadow) LastSelection { get; private set; }

    private sealed class LodDrawItem(PreviewDrawItem inner, bool main, int mask, ReadOnlyMemory<byte> bones) : IPreviewDrawItem
    {
        private readonly CodeObjectConsts _obj = WithBones(inner.ObjectConsts, !bones.IsEmpty);

        private static CodeObjectConsts WithBones(CodeObjectConsts o, bool skinned)
        {
            if (skinned)
                o.HasBones = 1;
            return o;
        }

        public MeshGeometry Mesh => inner.Mesh;
        public DrawRange Range => inner.Range;
        public CodeObjectConsts ObjectConsts => _obj;
        public ReadOnlyMemory<byte> Bones => bones.IsEmpty ? inner.Bones : bones;
        public int ShadowCascadeMask => mask;
        public MaterialPass? GetPass(PreviewTechnique technique)
            => technique == PreviewTechnique.ShadowDepth ? (mask != 0 ? inner.GetPass(technique) : null) : (main ? inner.GetPass(technique) : null);
    }

    public void Dispose()
    {
        foreach (var o in Owned)
            o.Dispose();
        Owned.Clear();
    }
}

/// <summary>Options of <see cref="PreviewModelLoader.Load"/>.</summary>
public sealed record PreviewModelOptions
{
    /// <summary>Material prefix APE previews with (rigid models: <c>mcs</c> = <see cref="MaterialType.ModelSiege"/>).</summary>
    public MaterialType MaterialType { get; init; } = MaterialType.ModelSiege;
    /// <summary>World matrix → t30 <c>worldMatrix</c>.</summary>
    public Matrix4x4 World { get; init; } = Matrix4x4.Identity;
    /// <summary>t30 <c>sortDepth</c> of the main-view draws.</summary>
    public float SortDepth { get; init; }
    /// <summary>LOD drawn into each sun-shadow cascade (index = cascade); null = LOD 0 everywhere.</summary>
    public int[]? ShadowLodPerCascade { get; init; }
    /// <summary>Only these techniques are built (null = the stage the material belongs to, plus its shadow).</summary>
    public IReadOnlySet<PreviewTechnique>? Techniques { get; init; }
    /// <summary>Surfaces (by xmesh material name) not drawn, in addition to the xmodel's <c>skinOverride … nodraw</c>.</summary>
    public IReadOnlySet<string>? HiddenMaterials { get; init; }
    /// <summary>A material that cannot be built (missing from the GDTs, unknown techset, no shader, missing image) is
    /// reported in <see cref="PreviewModel.Warnings"/> and its surfaces are drawn with
    /// <see cref="PreviewModelLoader.DefaultMaterial"/>, as APE does, instead of failing the whole model (hidden when that
    /// cannot be built either).</summary>
    public bool SkipBrokenMaterials { get; init; }
}

/// <summary>
/// A model read from the install and evaluated on the CPU (<see cref="PreviewModelLoader.Prepare"/>): LOD meshes from
/// the xmesh cache, every material evaluated through its techset with its images loaded. Holds no GPU objects, so it
/// can be built on a worker thread, kept across device loss, and turned into a <see cref="PreviewModel"/> any number of
/// times with <see cref="PreviewModelLoader.Create"/>.
/// </summary>
public sealed class PreparedPreviewModel
{
    public required string Name { get; init; }
    public required PreviewModelOptions Options { get; init; }
    public required IReadOnlyList<GpuMesh> Lods { get; init; }
    /// <summary>Surface material → replacement (xmodel <c>skinOverride</c>).</summary>
    public required IReadOnlyDictionary<string, string> Overrides { get; init; }
    /// <summary>Techniques per (overridden) surface material; null = neither the material nor the
    /// <see cref="PreviewModelLoader.DefaultMaterial"/> standing in for it could be built.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<PreviewTechnique, PreparedPass>?> Materials { get; init; }
    /// <summary>Materials that could not be built and are drawn with <see cref="PreviewModelLoader.DefaultMaterial"/>.</summary>
    public IReadOnlySet<string> DefaultedMaterials { get; init; } = new HashSet<string>();
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Union bounds over all LODs.</summary>
    public Vector3 BoundsMin => Lods.Count == 0 ? Vector3.Zero : Lods.Select(m => m.BoundsMin).Aggregate(Vector3.Min);
    public Vector3 BoundsMax => Lods.Count == 0 ? Vector3.Zero : Lods.Select(m => m.BoundsMax).Aggregate(Vector3.Max);
    public int TriangleCount(int lod) => Lods[lod].Surfaces.Sum(s => s.TriangleCount);
    public bool HasBones => Lods.Count > 0 && Lods[0].HasBones;
    /// <summary>Materials that could not be built (drawn with the default material or not at all).</summary>
    public int BrokenMaterialCount => Materials.Values.Count(m => m is null) + DefaultedMaterials.Count;
    /// <summary>Materials whose surfaces are not drawn at all.</summary>
    public int UndrawnMaterialCount => Materials.Values.Count(m => m is null);

    /// <summary>
    /// Drops the loaded image data (the bulk of a prepared model) once it is on the GPU; a later
    /// <see cref="PreviewModelLoader.Create"/> reads the images from the image cache again (on its thread).
    /// </summary>
    public void ReleaseImageData()
    {
        foreach (var material in Materials.Values)
            if (material is not null)
                foreach (var pass in material.Values)
                    foreach (var image in pass.Images)
                        image.Release();
    }
}

/// <summary>
/// Loads an xmodel from the install for the preview (data layer → GPU): LOD meshes from the xmesh cache (index
/// list 0 per surface), materials from the GDT evaluated through their techset for the preview prefix, and one
/// <see cref="PreviewDrawItem"/> per surface with the techniques APE draws it with. <see cref="Prepare"/> (CPU,
/// thread-safe) and <see cref="Create"/> (GPU) can run on different threads; <see cref="Load"/> does both.
/// </summary>
public sealed class PreviewModelLoader
{
    private readonly RenderContext _rc;
    private readonly ToolsGfxData _data;
    private readonly MaterialPassFactory _passes;
    private readonly IReadOnlyDictionary<string, string> _materialDefaults;

    /// <param name="materialDefaults">Field defaults of the <c>material</c> GDF (deffiles/material.awi); the GDT only
    /// stores non-default values.</param>
    public PreviewModelLoader(RenderContext rc, ToolsGfxData data, MaterialPassFactory passes, IReadOnlyDictionary<string, string> materialDefaults)
    {
        _rc = rc;
        _data = data;
        _passes = passes;
        _materialDefaults = materialDefaults;
    }

    /// <summary>Material defaults from the install (<see cref="ToolsGfxData.MaterialDefaults"/>).</summary>
    public PreviewModelLoader(RenderContext rc, ToolsGfxData data, MaterialPassFactory passes)
        : this(rc, data, passes, data.MaterialDefaults)
    {
    }

    /// <summary>GDT xmodel LOD file fields in LOD order.</summary>
    private static readonly string[] LodFields = ["filename", "mediumLod", "lowLod", "lowestLod", "lod4File", "lod5File", "lod6File", "lod7File"];

    public PreviewModel Load(string xmodelName, PreviewModelOptions? options = null)
        => Create(Prepare(_data, xmodelName, options, _materialDefaults));

    /// <summary>
    /// Reads and evaluates an xmodel without touching the device (thread-safe given a thread-safe
    /// <see cref="ToolsGfxData.Gdt"/>). Throws <see cref="FileNotFoundException"/> when the xmodel or one of its xmesh
    /// caches is missing. LODs, materials and images are loaded in parallel (off the UI thread); the result does not
    /// depend on it.
    /// </summary>
    public static PreparedPreviewModel Prepare(ToolsGfxData data, string xmodelName, PreviewModelOptions? options = null,
        IReadOnlyDictionary<string, string>? materialDefaults = null, CancellationToken cancellationToken = default)
    {
        var entry = data.Gdt.Find(xmodelName, "xmodel") ?? throw new FileNotFoundException($"xmodel '{xmodelName}' not found in the GDTs");
        var lodParams = XModelLodParameters.FromGdt(entry.Fields);
        var files = new List<string>();
        foreach (var field in LodFields)
        {
            if (!entry.Fields.TryGetValue(field, out var file) || string.IsNullOrWhiteSpace(file))
                break;
            files.Add(file);
        }
        if (files.Count == 0)
            throw new InvalidDataException($"{xmodelName}: no LOD files");
        var lodMeshes = new GpuMesh[files.Count];
        ForEachInOrder(files.Count, cancellationToken, i =>
        {
            var bin = data.Models.ResolveModelExportPath(files[i]);
            var loc = data.Models.FindXMesh(bin, lodParams)
                ?? throw new FileNotFoundException($"{xmodelName}: no xmesh cache for {Path.GetFileName(files[i])} (open the model in APE once to convert it)");
            lodMeshes[i] = GpuMesh.FromXMesh(loc.Payload is { } payload ? XMeshData.Parse(payload, loc.Path) : XMeshData.Load(loc.Path));
        });
        return PrepareMeshes(data, xmodelName, lodMeshes, entry.Fields.GetValueOrDefault("skinOverride"), options, materialDefaults, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="body"/> for 0 … <paramref name="count"/> − 1, in parallel unless on the UI thread (the
    /// editor's GDT lookups marshal there, so it must not wait on workers). When bodies throw, rethrows the one with the
    /// lowest index — what a sequential loop would have thrown.
    /// </summary>
    internal static void ForEachInOrder(int count, CancellationToken cancellationToken, Action<int> body)
    {
        if (count <= 1 || Dispatcher.UIThread.CheckAccess())
        {
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                body(i);
            }
            return;
        }
        var errors = new Exception?[count];
        Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
        {
            try
            {
                body(i);
            }
            catch (Exception ex)
            {
                errors[i] = ex;
            }
        });
        foreach (var e in errors)
            if (e != null)
                ExceptionDispatchInfo.Throw(e);
    }

    /// <summary>
    /// <see cref="Prepare"/> for meshes that do not come from an xmodel (e.g. a material preview shape): each surface is
    /// drawn with the material its <see cref="GpuSurface.Material"/> names.
    /// </summary>
    /// <param name="skinOverride">xmodel <c>skinOverride</c> text ("&lt;surface material&gt; &lt;replacement&gt;" per line).</param>
    public static PreparedPreviewModel PrepareMeshes(ToolsGfxData data, string name, IReadOnlyList<GpuMesh> lods, string? skinOverride = null,
        PreviewModelOptions? options = null, IReadOnlyDictionary<string, string>? materialDefaults = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PreviewModelOptions();
        materialDefaults ??= data.MaterialDefaults;
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // GDT values are raw: lines are separated by the literal text \r\n (real line breaks accepted too).
        foreach (var line in (skinOverride ?? "").Replace(@"\r\n", "\n").Replace(@"\n", "\n").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
                overrides[parts[0]] = parts[1];
        }

        // Distinct materials in surface order, prepared in parallel and collected in that order.
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var surface in lods.SelectMany(m => m.Surfaces))
        {
            var material = overrides.GetValueOrDefault(surface.Material, surface.Material);
            if (material.Equals("nodraw", StringComparison.OrdinalIgnoreCase) || options.HiddenMaterials?.Contains(surface.Material) == true
                || !seen.Add(material))
                continue;
            names.Add(material);
        }
        var prepared = new (IReadOnlyDictionary<PreviewTechnique, PreparedPass>? Passes, string? Error)[names.Count];
        ForEachInOrder(names.Count, cancellationToken, i =>
        {
            try
            {
                prepared[i] = (PrepareMaterial(data, names[i], options, materialDefaults, cancellationToken), null);
            }
            catch (Exception ex) when (options.SkipBrokenMaterials && ex is not (OutOfMemoryException or OperationCanceledException))
            {
                prepared[i] = (null, $"{names[i]}: {ex.Message}");
            }
        });

        // A material that fails to load is drawn with the "$default" material instead (Material_GetCached), built once.
        IReadOnlyDictionary<PreviewTechnique, PreparedPass>? fallback = null;
        string? fallbackError = null;
        if (prepared.Any(p => p.Passes is null))
        {
            try
            {
                fallback = PrepareMaterial(data, DefaultMaterial, options, materialDefaults, cancellationToken);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                fallbackError = $"{DefaultMaterial}: {ex.Message}";
            }
        }

        var warnings = new List<string>();
        var materials = new Dictionary<string, IReadOnlyDictionary<PreviewTechnique, PreparedPass>?>(StringComparer.OrdinalIgnoreCase);
        var defaulted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
        {
            var (passes, error) = prepared[i];
            if (passes is not null)
                foreach (var p in passes.Values)
                    warnings.AddRange(p.Warnings);
            else
            {
                warnings.Add(error!);
                if (fallback is not null)
                {
                    passes = fallback;
                    defaulted.Add(names[i]);
                }
            }
            materials[names[i]] = passes;
        }
        if (fallback is not null)
            foreach (var p in fallback.Values)
                warnings.AddRange(p.Warnings);
        else if (fallbackError is not null)
            warnings.Add(fallbackError);
        return new PreparedPreviewModel
        {
            Name = name,
            Options = options,
            Lods = lods,
            Overrides = overrides,
            Materials = materials,
            DefaultedMaterials = defaulted,
            Warnings = warnings,
        };
    }

    /// <summary>The material APE draws a surface with when the surface's own material fails to load
    /// (<c>Material_GetCached</c> 0x140451290): <c>texture_assets\code.gdt</c>, techset <c>tools\default</c>.</summary>
    public const string DefaultMaterial = "$default";

    /// <summary>GPU buffers and material passes for a prepared model (on the thread that owns the device context).
    /// The passes' resources belong to this loader's <see cref="MaterialPassFactory"/>.</summary>
    public PreviewModel Create(PreparedPreviewModel prepared)
    {
        var options = prepared.Options;
        var xmodelName = prepared.Name;
        var lodMeshes = prepared.Lods;
        var owned = new List<IDisposable>();
        try
        {
            // One VB + IB per LOD, surfaces packed back to back; index list 0 only.
            var lodBuffers = new List<(MeshGeometry? Mesh, List<(GpuSurface Surface, DrawRange Range)> Surfaces)>();
            foreach (var mesh in lodMeshes)
            {
                var ranges = new List<(GpuSurface, DrawRange)>();
                int vertexBytes = 0, indexBytes = 0;
                foreach (var s in mesh.Surfaces)
                {
                    vertexBytes += s.VertexBytes.Length;
                    indexBytes += s.IndicesPerList * 2;
                }
                var vbytes = new byte[vertexBytes];
                var ibytes = new byte[indexBytes];
                int baseVertex = 0, startIndex = 0, vertexAt = 0, indexAt = 0;
                foreach (var s in mesh.Surfaces)
                {
                    s.VertexBytes.CopyTo(vbytes.AsSpan(vertexAt));
                    vertexAt += s.VertexBytes.Length;
                    var list0 = s.IndexBytes[..(s.IndicesPerList * 2)];
                    list0.CopyTo(ibytes.AsSpan(indexAt));
                    indexAt += list0.Length;
                    ranges.Add((s, new DrawRange(s.IndicesPerList, startIndex, baseVertex)));
                    baseVertex += s.Vertices.Length;
                    startIndex += s.IndicesPerList;
                }
                if (vbytes.Length == 0 || ibytes.Length == 0)
                {
                    lodBuffers.Add((null, new()));
                    continue;
                }
                var vb = GpuBuffer.CreateVertexRaw(_rc.Gfx, vbytes, Apex.Render.Data.Assets.GenericVertex.Stride, $"{xmodelName} VB");
                var ib = GpuBuffer.CreateIndexRaw(_rc.Gfx, ibytes, 2, $"{xmodelName} IB");
                owned.Add(vb);
                owned.Add(ib);
                lodBuffers.Add((new MeshGeometry { VertexBuffer = vb.Buffer, IndexBuffer = ib.Buffer, IndexFormat = Format.R16_UInt }, ranges));
            }

            var materialCache = new Dictionary<string, Dictionary<PreviewTechnique, MaterialPass>>(StringComparer.OrdinalIgnoreCase);
            var preparedCache = new Dictionary<IReadOnlyDictionary<PreviewTechnique, PreparedPass>, Dictionary<PreviewTechnique, MaterialPass>>(
                ReferenceEqualityComparer.Instance);
            Dictionary<PreviewTechnique, MaterialPass> PassesFor(string material)
            {
                if (materialCache.TryGetValue(material, out var p))
                    return p;
                p = new Dictionary<PreviewTechnique, MaterialPass>();
                if (prepared.Materials.GetValueOrDefault(material) is { } techniques)
                {
                    // Materials drawn with the default material share its passes.
                    if (preparedCache.TryGetValue(techniques, out var shared))
                        p = shared;
                    else
                    {
                        foreach (var (t, pass) in techniques)
                            p[t] = _passes.Create(pass);
                        preparedCache[techniques] = p;
                    }
                }
                materialCache[material] = p;
                return p;
            }

            var obj = CodeObjectConsts.Identity;
            obj.WorldMatrix = options.World;
            obj.SortDepth = options.SortDepth;
            var lodItems = new List<IReadOnlyList<PreviewDrawItem>>();
            for (int lod = 0; lod < lodBuffers.Count; lod++)
            {
                var list = new List<PreviewDrawItem>();
                if (lodBuffers[lod].Mesh is { } geometry)
                {
                    foreach (var (surface, range) in lodBuffers[lod].Surfaces)
                    {
                        var material = prepared.Overrides.GetValueOrDefault(surface.Material, surface.Material);
                        if (material.Equals("nodraw", StringComparison.OrdinalIgnoreCase) || options.HiddenMaterials?.Contains(surface.Material) == true)
                            continue;
                        var item = new PreviewDrawItem { Mesh = geometry, Range = range, ObjectConsts = obj };
                        foreach (var (t, pass) in PassesFor(material))
                            item.Passes[t] = pass;
                        if (item.Passes.Count > 0)
                            list.Add(item);
                    }
                }
                lodItems.Add(list);
            }
            var shadowLods = options.ShadowLodPerCascade ?? [0, 0, 0];
            var items = new List<PreviewDrawItem>();
            for (int lod = 0; lod < lodItems.Count; lod++)
            {
                int mask = 0;
                for (int c = 0; c < shadowLods.Length; c++)
                    if (Math.Min(shadowLods[c], lodItems.Count - 1) == lod) mask |= 1 << c;
                foreach (var it in lodItems[lod])
                {
                    var copy = new PreviewDrawItem { Mesh = it.Mesh, Range = it.Range, ObjectConsts = it.ObjectConsts, ShadowCascadeMask = mask };
                    foreach (var (t, pass) in it.Passes)
                        if (t == PreviewTechnique.ShadowDepth ? mask != 0 : lod == 0)
                            copy.Passes[t] = pass;
                    if (copy.Passes.Count > 0)
                        items.Add(copy);
                }
            }

            var model = new PreviewModel
            {
                Name = xmodelName,
                Items = items,
                Lods = lodMeshes.Select(m => (m.Surfaces.Count, m.Surfaces.Sum(s => s.TriangleCount))).ToList(),
                BoundsMin = prepared.BoundsMin,
                BoundsMax = prepared.BoundsMax,
                LodMetrics = XModelLod.Metrics(lodMeshes),
                LodItems = lodItems,
                Warnings = prepared.Warnings,
            };
            model.Owned.AddRange(owned);
            return model;
        }
        catch
        {
            foreach (var o in owned)
                o.Dispose();
            throw;
        }
    }

    /// <summary>The preview stage a techset draws in: SSS forward (+ its depth prepass) for subsurface-scattering materials,
    /// the deferred or forward decal stage for decals, else deferred (gbuffer) if it has a gbuffer technique, else OIT when it has one, else lit forward, else emissive
    /// (unlit); always plus "build shadowmap depth" when present.</summary>
    private static Dictionary<PreviewTechnique, PreparedPass> PrepareMaterial(ToolsGfxData data, string material, PreviewModelOptions options,
        IReadOnlyDictionary<string, string> materialDefaults, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = data.Gdt.Find(material, "material") ?? throw new FileNotFoundException($"material '{material}' not found in the GDTs");
        var fields = new Dictionary<string, string>(materialDefaults, StringComparer.Ordinal);
        foreach (var (k, v) in entry.Fields)
            fields[k] = v;
        var techset = data.Techsets.Load(entry.Fields.GetValueOrDefault("materialType") ?? "", options.MaterialType);
        var values = MaterialEvaluator.Evaluate(techset, fields);

        var want = options.Techniques;
        var result = new Dictionary<PreviewTechnique, PreparedPass>();
        void Add(PreviewTechnique t, ToolsGfxTechnique slot, PreviewTechnique? requestedAs = null)
        {
            if (want != null && !want.Contains(requestedAs ?? t))
                return;
            if (techset.GetTechnique(slot) is { } technique)
                result[t] = MaterialPassFactory.Prepare(data, values, technique, cancellationToken);
        }
        // Render class 3, lit-forward opaque (techsetdef renderFlags "lit forward opaque"): its "forward opaque" state does not
        // write depth, so APE's Prepass stage ("draws scene lists 0, 3, 4") lays the surface's depth down first. Without it
        // a closed surface such as a pistol slide draws its far inner wall over its near wall.
        void AddLitPrepass()
        {
            if ((techset.Globals.RenderFlags & MaterialRenderFlags.IsOpaque) != 0)
                Add(PreviewTechnique.LitPrepass, ToolsGfxTechnique.DepthPrepass);
        }
        // Render class 4 (Scene_RenderClassFromFlags: isOpaque, neither isGbuffer nor isEmissive, isSubSurfaceScattering)
        // is drawn by RENDER_STAGE_LIT_FORWARD_SSS_OPAQUE: its "depth prepass" before the G-buffer and its "lit" after
        // deferred lighting (skin_*: techsetdef renderFlags "lit forward sss").
        var flags = techset.Globals.RenderFlags;
        bool sss = (flags & (MaterialRenderFlags.IsOpaque | MaterialRenderFlags.IsGbuffer | MaterialRenderFlags.IsEmissive
                             | MaterialRenderFlags.IsSubSurfaceScattering)) == (MaterialRenderFlags.IsOpaque | MaterialRenderFlags.IsSubSurfaceScattering)
                   && techset.GetTechnique(ToolsGfxTechnique.Lit) != null;
        // Decals (not isOpaque, isDecal) are render class 6 with isGbuffer, else 7. APE draws class 6 in its own stage
        // after the opaque G-buffer ("GBuffer Decal", which reads a copy of the normal/gloss target) and class 7 after
        // deferred lighting, each with its class technique: "gbuffer" for isGbuffer, else "lit".
        bool decal = (flags & (MaterialRenderFlags.IsOpaque | MaterialRenderFlags.IsDecal)) == MaterialRenderFlags.IsDecal;
        if (sss && (want is null || want.Contains(PreviewTechnique.Sss)))
        {
            Add(PreviewTechnique.Sss, ToolsGfxTechnique.Lit);
            Add(PreviewTechnique.SssPrepass, ToolsGfxTechnique.DepthPrepass, PreviewTechnique.Sss);
        }
        else if (want is null && decal)
        {
            if ((flags & MaterialRenderFlags.IsGbuffer) != 0) Add(PreviewTechnique.GbufferDecal, ToolsGfxTechnique.Gbuffer);
            else Add(PreviewTechnique.LitDecal, ToolsGfxTechnique.Lit);
        }
        else if (want is null)
        {
            if (techset.GetTechnique(ToolsGfxTechnique.Gbuffer) != null) Add(PreviewTechnique.Gbuffer, ToolsGfxTechnique.Gbuffer);
            else if (techset.GetTechnique(ToolsGfxTechnique.Oit) != null) Add(PreviewTechnique.Oit, ToolsGfxTechnique.Oit);
            else if (techset.GetTechnique(ToolsGfxTechnique.Lit) != null)
            {
                Add(PreviewTechnique.Lit, ToolsGfxTechnique.Lit);
                AddLitPrepass();
            }
            else Add(PreviewTechnique.Emissive, ToolsGfxTechnique.Unlit);
        }
        else
        {
            Add(PreviewTechnique.Gbuffer, ToolsGfxTechnique.Gbuffer);
            Add(PreviewTechnique.Oit, ToolsGfxTechnique.Oit);
            Add(PreviewTechnique.Lit, ToolsGfxTechnique.Lit);
            if (techset.GetTechnique(ToolsGfxTechnique.Gbuffer) == null)
                AddLitPrepass();
            Add(PreviewTechnique.Emissive, techset.GetTechnique(ToolsGfxTechnique.Lit) != null ? ToolsGfxTechnique.Lit : ToolsGfxTechnique.Unlit);
        }
        Add(PreviewTechnique.ShadowDepth, ToolsGfxTechnique.BuildShadowmapDepth);
        // APE fails a material whose image cannot be loaded (Material_ResolveTextures), so it is drawn with the default
        // material too.
        if (options.SkipBrokenMaterials && result.Values.SelectMany(p => p.MissingImages).FirstOrDefault() is { } image)
            throw new FileNotFoundException($"failed to load image '{image}'");
        return result;
    }
}
