using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Gdf;

/// <summary>
/// Loads BO3 deffiles (.awi AngelScript) into editing schemas by lexing,
/// parsing and interpreting <c>GenerateUI</c> against a recording host.
/// Pure; no UI dependencies. See docs/ingestion-design.md §2.
/// </summary>
public static class GdfSchemaLoader
{
    private static readonly Regex IncludeRegex =
        new(@"#include\s+""([^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// Parses every asset-type deffile under <paramref name="deffilesDir"/> and
    /// returns a case-insensitive map of asset type name → schema.
    /// </summary>
    public static IReadOnlyDictionary<string, AssetSchema> LoadAll(string deffilesDir, Action<string>? warn = null)
    {
        var result = new Dictionary<string, AssetSchema>(StringComparer.OrdinalIgnoreCase);
        // material.awi asks the techsetdefs which material types exist and which fields each one shows (re-indexed on
        // every load; dropped when the deffiles are gone).
        Techsetdefs.Configure(deffilesDir);
        if (!Directory.Exists(deffilesDir))
        {
            warn?.Invoke($"deffiles directory not found: {deffilesDir}");
            return result;
        }

        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(deffilesDir, "*.awi"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (stem.EndsWith("_old", StringComparison.OrdinalIgnoreCase) ||
                stem.EndsWith("_bkp", StringComparison.OrdinalIgnoreCase))
                continue;
            paths.Add(path);
        }

        // Each .awi is lexed/parsed/interpreted independently: BuildProgram allocates a fresh
        // GdfProgram, GdfInterpreter.Run a fresh RecordingAsset, and the interpreter/mapper share no
        // mutable static state (only immutable readonly lookup tables), so per-file work parallelizes
        // safely. Results are merged into a case-insensitive dictionary keyed by the unique file stem,
        // so the final map is deterministic regardless of completion order. The optional warn callback
        // may run on multiple threads, so serialize it behind a lock.
        object warnGate = new();
        Action<string>? safeWarn = warn is null
            ? null
            : msg => { lock (warnGate) warn(msg); };

        var parsed = new ConcurrentDictionary<string, AssetSchema>(StringComparer.OrdinalIgnoreCase);
        var files = new ConcurrentDictionary<string, Lazy<ParsedFile>>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(paths, path =>
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            try
            {
                var prog = BuildProgram(path, deffilesDir, files, safeWarn);
                if (!prog.Funcs.ContainsKey("GenerateUI"))
                {
                    safeWarn?.Invoke($"{stem}: no GenerateUI");
                    return;
                }

                var host = GdfInterpreter.Run(prog, "GenerateUI", msg => safeWarn?.Invoke($"{stem}: {msg}"));
                var schema = SchemaMapper.Build(stem, host);
                parsed[stem] = schema;
                // Keep the parsed program so GenerateUI can be re-run per asset for conditional
                // visibility/enable/options (see GdfRuntime).
                GdfRuntime.Register(stem, prog, host.Entries.Where(e => e.Placed && !e.Dead).SelectMany(e => e.Names));
            }
            catch (Exception ex)
            {
                safeWarn?.Invoke($"{stem}: failed to load ({ex.Message})");
            }
        });

        foreach (var (stem, schema) in parsed)
            result[stem] = schema;

        return result;
    }

    private static GdfProgram BuildProgram(string mainPath, string deffilesDir,
        ConcurrentDictionary<string, Lazy<ParsedFile>> files, Action<string>? warn)
    {
        var prog = new GdfProgram();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LoadFile(mainPath, deffilesDir, prog, seen, files, warn);
        return prog;
    }

    private static void LoadFile(string path, string deffilesDir, GdfProgram prog,
        HashSet<string> seen, ConcurrentDictionary<string, Lazy<ParsedFile>> files, Action<string>? warn)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return; }
        if (!seen.Add(full)) return;
        if (!File.Exists(full)) return;

        // Shared headers (gadget.h, asset_list_helper.h…) are included by many deffiles; each file is
        // read, lexed and parsed once per LoadAll and its declarations merged into every includer.
        var file = files.GetOrAdd(full, static f => new Lazy<ParsedFile>(() => ParsedFile.Parse(f))).Value;

        // Resolve includes first so their declarations are available.
        foreach (var incName in file.Includes)
            LoadFile(Path.Combine(deffilesDir, incName), deffilesDir, prog, seen, files, warn);

        file.MergeInto(prog);
        if (file.Error is { } error)
            warn?.Invoke($"parse error in {Path.GetFileName(full)}: {error}");
    }

    /// <summary>
    /// One source file's includes and top-level declarations. The AST is never mutated after parse,
    /// so the same declarations can be merged into several programs. A parse error keeps the
    /// declarations parsed before it, exactly as parsing straight into the program did.
    /// </summary>
    private sealed class ParsedFile
    {
        private readonly GdfProgram _decls = new();

        private ParsedFile(string[] includes) => Includes = includes;

        public string[] Includes { get; }
        public string? Error { get; private set; }

        public static ParsedFile Parse(string fullPath)
        {
            string src = File.ReadAllText(fullPath);
            var includes = new List<string>();
            foreach (Match m in IncludeRegex.Matches(src))
                includes.Add(m.Groups[1].Value);

            var file = new ParsedFile(includes.ToArray());
            try
            {
                GdfParser.ParseInto(GdfLexer.Tokenize(src), file._decls);
            }
            catch (Exception ex)
            {
                file.Error = ex.Message;
            }
            return file;
        }

        /// <summary>Same effect on <paramref name="prog"/> as parsing this file into it.</summary>
        public void MergeInto(GdfProgram prog)
        {
            foreach (var (name, fn) in _decls.Funcs)
                prog.Funcs[name] = fn;
            prog.Globals.AddRange(_decls.Globals);
        }
    }
}
