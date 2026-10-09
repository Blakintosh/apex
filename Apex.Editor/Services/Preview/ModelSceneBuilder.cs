using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Preview.Formats;
using Apex.Render.Data.Gdt;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Apex.Editor.Services.Preview;

/// <summary>
/// Turns a parsed <see cref="PreviewModel"/> into a renderable <see cref="ModelScene"/>, resolving
/// each part's material chain (mesh material name → material asset → image asset → baseImage source
/// file) into raw RGBA textures. Any broken link in the chain simply yields an untextured part —
/// that is the normal case for many stock assets, so nothing is logged.
/// </summary>
public static class ModelSceneBuilder
{
    private const int MaxTextureDimension = 1024;
    private const long CacheBudgetBytes = 96L * 1024 * 1024;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> Cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<CacheEntry> Lru = new();
    private static long _cacheBytes;

    private sealed record CacheEntry(string Key, RawTexture? Texture, long Bytes);

    public static Task<ModelScene> BuildAsync(
        PreviewModel model,
        GameEnvironment env,
        IGdtLookup gdt,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            // Each distinct material's maps once, materials in parallel (their decodes are independent).
            var materialNames = model.Parts.Where(p => p.Indices.Count > 0).Select(p => p.MaterialName).Distinct().ToList();
            var resolved = new MaterialMaps[materialNames.Count];
            Parallel.For(0, materialNames.Count, new ParallelOptions { CancellationToken = ct },
                i => resolved[i] = ResolveTextures(materialNames[i], env, gdt, ct));
            var mapsByMaterial = new Dictionary<string, MaterialMaps>(StringComparer.Ordinal);
            for (int i = 0; i < materialNames.Count; i++)
                mapsByMaterial[materialNames[i]] = resolved[i];

            var parts = new List<ModelScenePart>(model.Parts.Count);
            foreach (var part in model.Parts)
            {
                ct.ThrowIfCancellationRequested();
                if (part.Indices.Count == 0)
                    continue;

                var vertexData = new float[part.Vertices.Count * 8];
                for (int i = 0; i < part.Vertices.Count; i++)
                {
                    var v = part.Vertices[i];
                    int o = i * 8;
                    vertexData[o] = v.Position.X;
                    vertexData[o + 1] = v.Position.Y;
                    vertexData[o + 2] = v.Position.Z;
                    vertexData[o + 3] = v.Normal.X;
                    vertexData[o + 4] = v.Normal.Y;
                    vertexData[o + 5] = v.Normal.Z;
                    vertexData[o + 6] = v.UV.X;
                    vertexData[o + 7] = v.UV.Y;
                }

                var indices = new uint[part.Indices.Count];
                for (int i = 0; i < part.Indices.Count; i++)
                    indices[i] = (uint)part.Indices[i];

                var maps = mapsByMaterial[part.MaterialName];

                parts.Add(new ModelScenePart
                {
                    MaterialName = part.MaterialName,
                    VertexData = vertexData,
                    Indices = indices,
                    ColorMap = maps.Color,
                    NormalMap = maps.Normal,
                    SpecColorMap = maps.Spec,
                    GlossMap = maps.Gloss,
                    OcclusionMap = maps.Occlusion,
                    GlossRangeMin = maps.GlossRangeMin,
                    GlossRangeMax = maps.GlossRangeMax,
                });
            }

            return new ModelScene
            {
                Parts = parts,
                BoundsMin = model.BoundsMin,
                BoundsMax = model.BoundsMax,
                TriangleCount = model.TriangleCount,
            };
        }, ct);
    }

    /// <summary>
    /// Builds a single-part lit UV-sphere scene textured directly from a material's own
    /// colorMap/normalMap/specColorMap (the material preview's shading ball).
    /// </summary>
    public static Task<ModelScene> BuildMaterialSphereAsync(
        string materialName,
        GameEnvironment env,
        IGdtLookup gdt,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var material = gdt.Find(materialName, "material");
            var maps = material is null ? NoMaps : ResolveMaterialTextures(material, env, gdt, ct);
            ct.ThrowIfCancellationRequested();

            const float radius = 40f; // BO3 inches — sized like a typical prop
            var (vertexData, indices) = BuildUvSphere(radius, 48, 32);

            return new ModelScene
            {
                Parts = new[]
                {
                    new ModelScenePart
                    {
                        MaterialName = materialName,
                        VertexData = vertexData,
                        Indices = indices,
                        ColorMap = maps.Color,
                        NormalMap = maps.Normal,
                        SpecColorMap = maps.Spec,
                        GlossMap = maps.Gloss,
                        OcclusionMap = maps.Occlusion,
                        GlossRangeMin = maps.GlossRangeMin,
                        GlossRangeMax = maps.GlossRangeMax,
                    },
                },
                BoundsMin = new System.Numerics.Vector3(-radius),
                BoundsMax = new System.Numerics.Vector3(radius),
                TriangleCount = indices.Length / 3,
            };
        }, ct);
    }

    /// <summary>Interleaved pos/normal/uv UV sphere, Z-up, poles on ±Z.</summary>
    private static (float[] VertexData, uint[] Indices) BuildUvSphere(float radius, int segments, int rings)
    {
        var vertexData = new float[(rings + 1) * (segments + 1) * 8];
        int o = 0;
        for (int r = 0; r <= rings; r++)
        {
            double theta = Math.PI * r / rings;
            double sinT = Math.Sin(theta), cosT = Math.Cos(theta);
            for (int s = 0; s <= segments; s++)
            {
                double phi = 2.0 * Math.PI * s / segments;
                float nx = (float)(sinT * Math.Cos(phi));
                float ny = (float)(sinT * Math.Sin(phi));
                float nz = (float)cosT;
                vertexData[o++] = nx * radius;
                vertexData[o++] = ny * radius;
                vertexData[o++] = nz * radius;
                vertexData[o++] = nx;
                vertexData[o++] = ny;
                vertexData[o++] = nz;
                vertexData[o++] = (float)s / segments;
                vertexData[o++] = (float)r / rings;
            }
        }

        var indices = new uint[rings * segments * 6];
        int k = 0;
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                uint a = (uint)(r * (segments + 1) + s);
                uint b = (uint)((r + 1) * (segments + 1) + s);
                indices[k++] = a;
                indices[k++] = b;
                indices[k++] = a + 1;
                indices[k++] = a + 1;
                indices[k++] = b;
                indices[k++] = b + 1;
            }
        }
        return (vertexData, indices);
    }

    /// <summary>Resolved shading maps of one material, plus its normalized gloss remap range.</summary>
    internal readonly record struct MaterialMaps(
        RawTexture? Color,
        RawTexture? Normal,
        RawTexture? Spec,
        RawTexture? Gloss,
        RawTexture? Occlusion,
        float GlossRangeMin,
        float GlossRangeMax);

    // BO3 authors gloss on a 0–17 scale (glossRangeMax defaults to 17); the shader wants [0,1].
    private const float GlossFullScale = 17f;

    private static readonly MaterialMaps NoMaps = new(null, null, null, null, null, 0f, 1f);

    private static readonly string[] SlotKeys = { "colorMap", "normalMap", "specColorMap", "cosinePowerMap", "occMap" };

    private static MaterialMaps ResolveTextures(
        string materialName,
        GameEnvironment env,
        IGdtLookup gdt,
        CancellationToken ct)
    {
        if (!env.IsAvailable || string.IsNullOrWhiteSpace(materialName))
            return NoMaps;

        // Mesh material names sometimes carry an mtl_ prefix the GDT asset name lacks (or vice versa).
        var material = gdt.Find(materialName, "material")
            ?? (materialName.StartsWith("mtl_", StringComparison.OrdinalIgnoreCase)
                ? gdt.Find(materialName[4..], "material")
                : gdt.Find("mtl_" + materialName, "material"));
        return material is null ? NoMaps : ResolveMaterialTextures(material, env, gdt, ct);
    }

    /// <summary>Resolves the shading maps of a material (shared by the model path and the sphere preview); the slots
    /// decode in parallel.</summary>
    private static MaterialMaps ResolveMaterialTextures(
        GdtEntry material,
        GameEnvironment env,
        IGdtLookup gdt,
        CancellationToken ct)
    {
        var maps = new RawTexture?[SlotKeys.Length];
        Parallel.For(0, SlotKeys.Length, new ParallelOptions { CancellationToken = ct },
            i => maps[i] = ResolveSlot(material, SlotKeys[i], env, gdt, ct));
        return new MaterialMaps(maps[0], maps[1], maps[2], maps[3], maps[4],
            ReadGlossEndpoint(material, "glossRangeMin", 0f),
            ReadGlossEndpoint(material, "glossRangeMax", GlossFullScale));

        static float ReadGlossEndpoint(GdtEntry material, string key, float fallback)
        {
            var raw = material.Fields.GetValueOrDefault(key, "");
            var value = float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
            return Math.Clamp(value / GlossFullScale, 0f, 1f);
        }
    }

    private static RawTexture? ResolveSlot(
        GdtEntry material,
        string slotKey,
        GameEnvironment env,
        IGdtLookup gdt,
        CancellationToken ct)
    {
        var imageAssetName = material.Fields.GetValueOrDefault(slotKey, "");
        if (string.IsNullOrWhiteSpace(imageAssetName))
            return null;

        var raw = gdt.Find(imageAssetName, "image")?.Fields.GetValueOrDefault("baseImage", "");
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var path = PreviewFileResolver.ResolveImage(env, raw);
        return path is null ? null : DecodeTexture(path, ct);
    }

    /// <summary>Decodes an image file to RGBA8 (longest side capped at 1024), through a bounded LRU.</summary>
    internal static RawTexture? DecodeTexture(string path, CancellationToken ct)
    {
        long mtime;
        try
        {
            mtime = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            return null;
        }

        var key = string.Concat(path.ToLowerInvariant(), "|", mtime.ToString());
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var node))
            {
                Lru.Remove(node);
                Lru.AddLast(node);
                return node.Value.Texture;
            }
        }

        ct.ThrowIfCancellationRequested();

        RawTexture? texture = null;
        try
        {
            using var image = ImagePreviewLoader.LoadScaled(path, MaxTextureDimension, out _, out _);
            var rgba = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(rgba);
            texture = new RawTexture { Width = image.Width, Height = image.Height, Rgba = rgba };
        }
        catch
        {
            // Undecodable (.exr etc.) — cache the miss so we don't retry every rebuild.
        }

        lock (CacheLock)
        {
            if (!Cache.ContainsKey(key))
            {
                long bytes = texture?.Rgba.LongLength ?? 0;
                var node = Lru.AddLast(new CacheEntry(key, texture, bytes));
                Cache[key] = node;
                _cacheBytes += bytes;
                while (_cacheBytes > CacheBudgetBytes && Lru.First is { } oldest && Lru.Count > 1)
                {
                    Lru.Remove(oldest);
                    Cache.Remove(oldest.Value.Key);
                    _cacheBytes -= oldest.Value.Bytes;
                }
            }
        }

        return texture;
    }
}
