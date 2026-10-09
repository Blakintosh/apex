using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;

namespace Apex.Render.Data.Gdt;

/// <summary>One GDT asset entry.</summary>
/// <param name="Name">Asset name.</param>
/// <param name="Gdf">Type file (<c>material.gdf</c>), or null for a derived entry.</param>
/// <param name="Parent">Parent asset of a derived entry (<c>"name" [ "parent" ]</c>), else null.</param>
/// <param name="Fields">Field values exactly as written in the file (no escape processing).</param>
/// <param name="SourceFile">The .gdt the entry came from.</param>
public sealed record GdtEntry(
    string Name,
    string? Gdf,
    string? Parent,
    IReadOnlyDictionary<string, string> Fields,
    string SourceFile)
{
    /// <summary>True when <paramref name="gdf"/> (<c>material.gdf</c>) names GDF type <paramref name="type"/> (<c>material</c>).</summary>
    public static bool IsType(string gdf, string type) =>
        Path.GetFileNameWithoutExtension(gdf.Trim()).Equals(type, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Minimal reader for GDT text files (<c>{ "name" ( "type.gdf" ) { "key" "value" ... } ... }</c>).
/// Enough for the data layer's own lookups (SSI assets, verification); the editor has its own parser.
/// </summary>
public static class GdtFile
{
    /// <summary>Parses GDT text into entries (in file order).</summary>
    public static List<GdtEntry> Parse(string text, string sourceFile)
    {
        var entries = new List<GdtEntry>();
        // Keys, type files and short values repeat throughout a file: share one string per distinct value.
        var pool = new Dictionary<string, string>(StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();
        int i = 0;
        SkipWs();
        if (i < text.Length && text[i] == '{') i++;
        while (true)
        {
            SkipWs();
            if (i >= text.Length || text[i] == '}') break;
            if (text[i] != '"') { i++; continue; }
            var name = ReadString(pooled: false);
            SkipWs();
            string? gdf = null, parent = null;
            if (i < text.Length && text[i] == '(')
            {
                i++; SkipWs();
                gdf = ReadString();
                SkipWs();
                if (i < text.Length && text[i] == ')') i++;
            }
            else if (i < text.Length && text[i] == '[')
            {
                i++; SkipWs();
                parent = ReadString(pooled: false);
                SkipWs();
                if (i < text.Length && text[i] == ']') i++;
            }
            SkipWs();
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (i < text.Length && text[i] == '{')
            {
                i++;
                while (true)
                {
                    SkipWs();
                    if (i >= text.Length) break;
                    if (text[i] == '}') { i++; break; }
                    if (text[i] != '"') { i++; continue; }
                    var key = ReadString();
                    SkipWs();
                    var value = i < text.Length && text[i] == '"' ? ReadString() : string.Empty;
                    fields[key] = value;
                }
            }
            entries.Add(new GdtEntry(name, gdf, parent, fields, sourceFile));
        }
        return entries;

        void SkipWs()
        {
            while (i < text.Length)
            {
                if (char.IsWhiteSpace(text[i])) { i++; continue; }
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                break;
            }
        }

        // Values are raw, as APE reads them: a backslash is a literal character (paths mix `\` and `\\`, and
        // skinOverride separates its lines with the literal text `\r\n`), and a value ends at the next quote.
        string ReadString(bool pooled = true)
        {
            i++; // opening quote
            int start = i;
            int stop = text.AsSpan(start).IndexOf('"');
            var s = stop < 0 ? text.AsSpan(start) : text.AsSpan(start, stop);
            i = stop < 0 ? text.Length + 1 : start + stop + 1;
            return pooled && s.Length <= 32 ? Pooled(s) : s.ToString();
        }

        string Pooled(ReadOnlySpan<char> s)
        {
            if (!pool.TryGetValue(s, out var v))
            {
                v = s.ToString();
                pool[v] = v;
            }
            return v;
        }
    }

    /// <summary>Parses a .gdt file, in APE's code page (see <see cref="GdtEncoding"/>).</summary>
    public static List<GdtEntry> Load(string path) => Parse(GdtEncoding.GetString(File.ReadAllBytes(path)), path);
}

/// <summary>
/// Asset lookup by name — what the data layer resolves xmodels, materials, images and SSIs through. <see cref="GdtIndex"/>
/// reads the install's GDTs; a host (the editor) can supply its own index so unsaved edits are previewed.
/// </summary>
public interface IGdtLookup
{
    /// <summary>The asset named <paramref name="name"/> with its parent chain merged, or null.</summary>
    GdtEntry? Find(string name);

    /// <summary>
    /// The asset named <paramref name="name"/> of GDF type <paramref name="type"/> (<c>material</c>, <c>xmodel</c>,
    /// <c>image</c>, …), or null when missing or of another type. Entries without a known GDF are accepted.
    /// </summary>
    GdtEntry? Find(string name, string type)
    {
        var e = Find(name);
        return e is null || e.Gdf is null || GdtEntry.IsType(e.Gdf, type) ? e : null;
    }
}

/// <summary>
/// Name index over GDT files (<see cref="ForInstall"/>: <c>source_data</c> and <c>model_export</c> of the install,
/// recursively — APE reads both; e.g. <c>model_export	7_skybox.gdt</c> holds the SSI skybox xmodels). Built lazily;
/// derived entries are resolved by merging parent fields. Later files win for duplicate names.
/// </summary>
public sealed class GdtIndex : IGdtLookup
{
    private readonly IReadOnlyList<string> _roots;
    private readonly object _lock = new();
    private Dictionary<string, GdtEntry>? _entries;
    private Dictionary<string, List<GdtEntry>>? _all;

    // Derived entries merged with their parent chain; the index never changes once built.
    private readonly ConcurrentDictionary<GdtEntry, GdtEntry> _merged = new(ReferenceEqualityComparer.Instance);

    public GdtIndex(params string[] roots) => _roots = roots;

    /// <summary>Indexes <c>source_data</c> and <c>model_export</c> of an install (the GDT roots APE loads).</summary>
    public static GdtIndex ForInstall(ToolsGfxInstall install) => new(Path.Combine(install.Root, "source_data"), install.ModelExportDir);

    /// <summary>
    /// The other GDT roots of an install that APE sees through the GDT database (<c>gdtdb\gdt.db</c> indexes them too):
    /// <c>texture_assets</c> (e.g. <c>images.gdt</c> defines the shared material-library images such as
    /// <c>skin_pore_detail</c>, the skin techsets' detail map), <c>art_assets</c> and <c>xanim_export</c>. Meant as the
    /// fallback layer behind <see cref="ForInstall"/> or a host index (<see cref="LayeredGdtLookup"/>).
    /// </summary>
    public static GdtIndex ForInstallExtras(ToolsGfxInstall install) => new(Path.Combine(install.Root, "texture_assets"),
        Path.Combine(install.Root, "art_assets"), Path.Combine(install.Root, "xanim_export"));

    /// <summary>Looks up an asset by name (the last definition wins); derived entries come back with their
    /// parent's fields merged in.</summary>
    public GdtEntry? Find(string name)
    {
        Ensure();
        return _entries!.TryGetValue(name, out var e) ? Merge(e) : null;
    }

    /// <summary>Like <see cref="Find(string)"/> but only among definitions of GDF type <paramref name="type"/>, so
    /// e.g. a material and an image sharing a name both stay reachable.</summary>
    public GdtEntry? Find(string name, string type)
    {
        Ensure();
        if (!_all!.TryGetValue(name, out var list))
            return null;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var merged = Merge(list[i]);
            if (merged.Gdf is null || GdtEntry.IsType(merged.Gdf, type))
                return merged;
        }
        return null;
    }

    private GdtEntry Merge(GdtEntry e) => e.Parent is null ? e : _merged.GetOrAdd(e, MergeChain);

    private GdtEntry MergeChain(GdtEntry e)
    {
        var map = _entries!;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var chain = new Stack<GdtEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var cur = e; cur is not null && seen.Add(cur.Name); cur = cur.Parent is null ? null : map.GetValueOrDefault(cur.Parent))
            chain.Push(cur);
        string? gdf = null;
        while (chain.Count > 0)
        {
            var c = chain.Pop();
            gdf ??= c.Gdf;
            foreach (var (k, v) in c.Fields) fields[k] = v;
        }
        return e with { Gdf = gdf, Fields = fields };
    }

    private void Ensure()
    {
        lock (_lock)
        {
            if (_entries is not null)
                return;
            var files = _roots.Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(root, "*.gdt", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".gdt", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase))
                .ToArray();
            // Parse in parallel, then index in file order so later files still win.
            var parsed = new List<GdtEntry>?[files.Length];
            var failed = new ExceptionDispatchInfo?[files.Length];
            Parallel.For(0, files.Length, k =>
            {
                try { parsed[k] = GdtFile.Load(files[k]); }
                catch (Exception ex) { failed[k] = ExceptionDispatchInfo.Capture(ex); }
            });
            var map = new Dictionary<string, GdtEntry>(StringComparer.OrdinalIgnoreCase);
            var all = new Dictionary<string, List<GdtEntry>>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k < files.Length; k++)
            {
                if (failed[k] is { } error)
                {
                    if (error.SourceException is IOException)
                        continue;
                    error.Throw();
                }
                foreach (var e in parsed[k]!)
                {
                    map[e.Name] = e;
                    if (!all.TryGetValue(e.Name, out var same))
                        all[e.Name] = same = new List<GdtEntry>(1);
                    same.Add(e);
                }
            }
            _all = all;
            _entries = map;
        }
    }
}

/// <summary>Looks an asset up in <paramref name="primary"/> first and in <paramref name="fallback"/> when it is missing
/// there (e.g. the editor's asset index backed by the install's remaining GDT roots, see
/// <see cref="GdtIndex.ForInstallExtras"/>).</summary>
public sealed class LayeredGdtLookup(IGdtLookup primary, IGdtLookup fallback) : IGdtLookup
{
    public IGdtLookup Primary { get; } = primary;
    public IGdtLookup Fallback { get; } = fallback;

    public GdtEntry? Find(string name) => Primary.Find(name) ?? Fallback.Find(name);

    public GdtEntry? Find(string name, string type) => Primary.Find(name, type) ?? Fallback.Find(name, type);
}
