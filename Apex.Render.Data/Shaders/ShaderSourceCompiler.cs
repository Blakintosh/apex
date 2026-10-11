using System.Buffers.Binary;
using System.Text;
using Apex.Render.Data.Hashing;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace Apex.Render.Data.Shaders;

/// <summary>A techset shader source failed to compile; the message carries the compiler's errors.</summary>
public sealed class ShaderCompileException : Exception
{
    public ShaderCompileException(ShaderVariantKey key, string message, IReadOnlyList<string> files) : base(message)
    {
        Key = key;
        Files = files;
    }

    public ShaderVariantKey Key { get; }

    /// <summary>The source and the includes it opened before failing (full paths): editing one may fix it.</summary>
    public IReadOnlyList<string> Files { get; }
}

/// <summary>
/// Compiles a techset shader source the way APE does on a cache miss (<c>ToolsGfx_CompileShaderCached</c>,
/// materials.md §1.2/1.3): D3DCompile flags 8 (<c>PACK_MATRIX_ROW_MAJOR</c>), the implicit macros <c>TOOLSGFX=1</c>,
/// <c>USING_HLSL=1</c>, <c>IS_*_SHADER</c> (1 for the stage, else 0), then the variant's own defines; entry point and
/// profile from the key. Includes resolve against the including file's folder, then the include root
/// (<c>share\raw\shaders_stable_toolsgfx</c>). Thread-safe.
/// </summary>
public static class ShaderSourceCompiler
{
    /// <summary>Bump when the compile recipe changes, so blobs from an older recipe are not reused.</summary>
    private const string Recipe = "apex-shader-compile 1";

    /// <summary>A compiled variant: DXBC, the sources it was built from (APE's <c>_deps</c> layout, with
    /// <see cref="ShaderDepsFile.CodeHash"/> naming the blob), and the compiler's warnings (null when none).</summary>
    public sealed record Result(byte[] Dxbc, ShaderDepsFile Deps, string? Warnings);

    /// <summary>Absolute path of <paramref name="source"/> (a techsetdef <c>source</c>) under the include root, or null.</summary>
    public static string? FindSource(string includeRoot, string source)
    {
        var rel = source.Replace('/', '\\').TrimStart('\\');
        if (rel.Length == 0 || Path.IsPathRooted(rel))
            return null;
        var path = Path.GetFullPath(Path.Combine(includeRoot, rel));
        return File.Exists(path) ? path : null;
    }

    /// <summary>Compiles <paramref name="key"/> from its source under <paramref name="includeRoot"/>.</summary>
    /// <exception cref="FileNotFoundException">The source is not there.</exception>
    /// <exception cref="ShaderCompileException">The compiler reported errors.</exception>
    public static Result Compile(string includeRoot, ShaderVariantKey key)
    {
        includeRoot = Path.GetFullPath(includeRoot);
        var rootPath = FindSource(includeRoot, key.Source)
                       ?? throw new FileNotFoundException($"Shader source {key.Source} is not under {includeRoot}.");
        var source = File.ReadAllBytes(rootPath);
        using var include = new IncludeHandler(includeRoot, Path.GetDirectoryName(rootPath)!);
        Blob? blob = null, errors = null;
        bool failed;
        try
        {
            failed = Compiler.Compile(source, Macros(key), include, key.EntryPoint, rootPath, key.Profile,
                ShaderFlags.PackMatrixRowMajor, EffectFlags.None, out blob, out errors).Failure;
        }
        catch (SharpGenException)
        {
            // Some Vortice overloads throw on a failed HRESULT; the error blob, if any, still holds the log.
            failed = true;
        }
        var log = errors is null ? null : Clean(errors.AsString(), includeRoot);
        errors?.Dispose();
        if (failed || blob is null)
        {
            blob?.Dispose();
            throw new ShaderCompileException(key,
                $"{key.Source} ({key.EntryPoint}/{key.Profile}) failed to compile:\n{(string.IsNullOrWhiteSpace(log) ? "no compiler output" : log.Trim())}",
                include.Dependencies.Select(d => Path.Combine(includeRoot, d.Path)).Prepend(rootPath).ToList());
        }
        var dxbc = blob.AsBytes();
        blob.Dispose();

        var root = new ShaderDependency(Relative(includeRoot, rootPath), Md4.Seeded(source));
        var includes = include.Dependencies;
        return new Result(dxbc, new ShaderDepsFile(root, includes, CodeHash(key, root, includes)), string.IsNullOrWhiteSpace(log) ? null : log.Trim());
    }

    /// <summary>
    /// The blob key of a compiled variant: a hash of the recipe, entry, profile, define key and every source file's
    /// signature, so an edit to the source or any include names a new blob and reverting an edit finds the old one.
    /// </summary>
    public static CacheHash CodeHash(ShaderVariantKey key, ShaderDependency root, IReadOnlyList<ShaderDependency> includes)
    {
        var h = Md4.Seeded(Encoding.ASCII.GetBytes($"{Recipe}|{key.EntryPoint}|{key.Profile}"));
        h = Md4.Chain(h, key.DefineKey.ToArray());
        foreach (var d in includes.Prepend(root))
        {
            h = Md4.Chain(h, Encoding.UTF8.GetBytes(d.Path.ToLowerInvariant()));
            h = Md4.Chain(h, d.Signature.ToArray());
        }
        return h;
    }

    /// <summary>True when every file a <c>_deps</c> file names still has the signature it records (paths relative to
    /// <paramref name="includeRoot"/>). A missing file is a mismatch.</summary>
    public static bool IsCurrent(ShaderDepsFile deps, string includeRoot)
    {
        foreach (var d in deps.Includes.Prepend(deps.Root))
        {
            var path = Path.Combine(includeRoot, d.Path);
            if (!File.Exists(path) || CacheKeys.FileSignature(path) != d.Signature)
                return false;
        }
        return true;
    }

    /// <summary>
    /// <paramref name="deps"/> with every file's current signature and the blob key those give, or null when a file is
    /// gone. The files' contents decide what they include, so a blob of that key, if one exists, is this variant's
    /// build of the sources as they are now (an edit that was reverted finds its earlier build).
    /// </summary>
    public static ShaderDepsFile? Resign(ShaderVariantKey key, ShaderDepsFile deps, string includeRoot)
    {
        ShaderDependency? Sign(ShaderDependency d)
        {
            var path = Path.Combine(includeRoot, d.Path);
            return File.Exists(path) ? d with { Signature = CacheKeys.FileSignature(path) } : null;
        }
        if (Sign(deps.Root) is not { } root)
            return null;
        var includes = new List<ShaderDependency>(deps.Includes.Count);
        foreach (var d in deps.Includes)
        {
            if (Sign(d) is not { } signed)
                return null;
            includes.Add(signed);
        }
        return new ShaderDepsFile(root, includes, CodeHash(key, root, includes));
    }

    /// <summary>True when every file a <c>_deps</c> file names exists under <paramref name="includeRoot"/>, i.e. the
    /// variant could be rebuilt here. Some of APE's builds of shipped sources (<c>lit_diamonds.hlsl</c>) include core
    /// headers that are not shipped; those builds stay authoritative.</summary>
    public static bool HasAllSources(ShaderDepsFile deps, string includeRoot) =>
        deps.Includes.Prepend(deps.Root).All(d => File.Exists(Path.Combine(includeRoot, d.Path)));

    /// <summary>APE's compile macro list (<c>ToolsGfx_BuildCompileMacros</c> 0x14059D5A0).</summary>
    public static ShaderMacro[] Macros(ShaderVariantKey key)
    {
        string Is(ShaderStage s) => key.Stage == s ? "1" : "0";
        var list = new List<ShaderMacro>
        {
            new("TOOLSGFX", "1"),
            new("USING_HLSL", "1"),
            new("IS_VERTEX_SHADER", Is(ShaderStage.Vertex)),
            new("IS_PIXEL_SHADER", Is(ShaderStage.Pixel)),
            new("IS_GEOMETRY_SHADER", Is(ShaderStage.Geometry)),
            new("IS_COMPUTE_SHADER", Is(ShaderStage.Compute)),
            new("IS_HULL_SHADER", Is(ShaderStage.Hull)),
            new("IS_DOMAIN_SHADER", Is(ShaderStage.Domain)),
        };
        foreach (var d in key.Defines)
            list.Add(new ShaderMacro(d.Name, d.Value));
        return list.ToArray();
    }

    /// <summary>The <c>_deps</c> bytes of <paramref name="deps"/> (<see cref="ShaderDepsFile.Parse"/> reads them back).</summary>
    public static byte[] Serialize(ShaderDepsFile deps)
    {
        var all = deps.Includes;
        var buf = new byte[272 + 4 + 272 * all.Count + 16];
        int o = 0;
        void Entry(ShaderDependency d)
        {
            var name = Encoding.ASCII.GetBytes(d.Path);
            if (name.Length > 255)
                throw new InvalidDataException($"Shader dependency path is longer than 255 characters: {d.Path}");
            name.CopyTo(buf, o);
            d.Signature.CopyTo(buf.AsSpan(o + 256, 16));
            o += 272;
        }
        Entry(deps.Root);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(o), all.Count);
        o += 4;
        foreach (var d in all)
            Entry(d);
        deps.CodeHash.CopyTo(buf.AsSpan(o, 16));
        return buf;
    }

    private static string Relative(string includeRoot, string path) =>
        Path.GetRelativePath(includeRoot, path).Replace('/', '\\');

    // The compiler prints absolute paths; the include root is noise in a preview warning.
    private static string Clean(string log, string includeRoot) =>
        log.Replace(includeRoot + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase).TrimEnd('\0');

    /// <summary>Resolves <c>#include</c>s and records every file opened, in first-open order.</summary>
    private sealed class IncludeHandler : CallbackBase, Include
    {
        private readonly string _root;
        private readonly Stack<string> _dirs = new();
        private readonly Dictionary<Stream, string> _open = new();
        private readonly List<ShaderDependency> _deps = new();
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public IncludeHandler(string root, string sourceDir)
        {
            _root = root;
            _dirs.Push(sourceDir);
        }

        public IReadOnlyList<ShaderDependency> Dependencies => _deps;

        public Stream Open(IncludeType type, string fileName, Stream? parentStream)
        {
            var parentDir = parentStream is not null && _open.TryGetValue(parentStream, out var p) ? Path.GetDirectoryName(p)! : _dirs.Peek();
            var rel = fileName.Replace('/', '\\');
            var path = new[] { Path.Combine(parentDir, rel), Path.Combine(_root, rel) }
                .Select(Path.GetFullPath)
                .FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException($"#include \"{fileName}\" not found (looked in {Relative(_root, parentDir)} and the include root).");
            var bytes = File.ReadAllBytes(path);
            if (_seen.Add(path))
                _deps.Add(new ShaderDependency(Relative(_root, path), Md4.Seeded(bytes)));
            var stream = new MemoryStream(bytes, writable: false);
            _open[stream] = path;
            return stream;
        }

        public void Close(Stream stream)
        {
            _open.Remove(stream);
            stream.Dispose();
        }
    }
}
