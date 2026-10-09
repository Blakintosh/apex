using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Apex.Editor.Models;
using Apex.Editor.Services.Preview.ToolsGfx;
using Apex.Render.Data.Conversion.XAnim;

namespace Apex.Editor.Services;

/// <summary>Told when a background file check finished and a file field's ⚠ may have changed (UI thread).</summary>
public interface IFileProbeListener
{
    void FilesProbed();
}

/// <summary>
/// The disk side of the file, file-list and bone fields: where a field's files live under the install, what a picked
/// file is written as, whether a value names a file that exists, the files a FileCombo offers and the bones a
/// BoneCombo offers. Every disk touch runs on a worker thread and is cached, so a keystroke or a row being realized
/// never waits on the disk; callers get what is known now and hear about the rest through
/// <see cref="IFileProbeListener"/> or a task. Mock mode has no install (<see cref="Root"/> is null): nothing is
/// checked, listed or browsed.
/// </summary>
public static class FieldFiles
{
    /// <summary>The BO3 install the fields resolve under, or null (mock data).</summary>
    public static string? Root { get; set; }

    /// <summary>Apex's asset index, for a bone field's model (an xmodel asset name).</summary>
    public static Func<string, string, AssetRecord?>? Resolve { get; set; }

    /// <summary>Opens the file picker; Apex.Shots replaces it to drive a browse without a native dialog.</summary>
    public static Func<TopLevel, PickRequest, Task<string?>> PickFile { get; set; } = DefaultPickAsync;

    public static bool HasInstall => Root is not null;

    // ── Paths ────────────────────────────────────────────────────────────────

    /// <summary>The folder a field's values are relative to (the install for a texture), or null without an install.</summary>
    public static string? RootDir(PropertyDef def)
    {
        if (Root is null)
            return null;
        var relative = def.RelativeRoot.Trim().Replace('/', '\\').Trim('\\');
        return relative.Length == 0 ? Root : Path.Combine(Root, relative);
    }

    /// <summary>The field's folder as modders name it ("model_export", "share\raw\fx"); "the BO3 folder" for none.</summary>
    public static string RootName(PropertyDef def)
    {
        var relative = def.RelativeRoot.Trim().Replace('/', '\\').Trim('\\');
        return relative.Length == 0 ? "the BO3 folder" : relative;
    }

    /// <summary>A raw GDT path as a file path: escaped (\\) and forward separators become one backslash.</summary>
    internal static string Unescape(string raw) =>
        raw.Trim().Trim('"').Replace("\\\\", "\\").Replace('/', '\\').TrimStart('\\');

    /// <summary>A file path as GDTs spell it: backslashes escaped (model_export values read "weapons\\ar\\gun.xmodel_bin").</summary>
    internal static string Escape(string path) => path.Replace("\\", "\\\\");

    /// <summary>
    /// What a file picked for <paramref name="def"/> is written as: its path relative to the field's folder, in the GDT
    /// style. A file outside the folder is refused with one line (Error), since the converter only looks inside it —
    /// except an image, which APE stores as an absolute path (the install's material GDTs carry such values).
    /// </summary>
    public static (string? Value, string? Error) ValueFor(PropertyDef def, string picked)
    {
        var dir = RootDir(def);
        if (dir is null)
            return (null, "Browsing needs the BO3 install.");
        var full = Path.GetFullPath(picked);
        var relative = Path.GetRelativePath(dir, full);
        if (!Path.IsPathRooted(relative) && !relative.StartsWith("..", StringComparison.Ordinal))
            return (Escape(relative), null);
        if (def.FileKind == PropertyFileKind.Texture)
            return (Escape(full), null);
        return (null, $"{Path.GetFileName(full)} isn't inside {RootName(def)}. Pick a file in that folder.");
    }

    /// <summary>Where the picker opens: the folder of the field's current file when it has one, else the field's folder.</summary>
    public static string? StartDir(PropertyDef def, string raw)
    {
        var dir = RootDir(def);
        if (dir is null)
            return null;
        if (raw.Trim().Length > 0)
        {
            var value = Unescape(raw);
            var full = Path.IsPathRooted(value) ? value : Path.Combine(dir, value);
            var parent = Path.GetDirectoryName(full);
            if (parent is not null && Directory.Exists(parent))
                return parent;
        }
        return Directory.Exists(dir) ? dir : Root;
    }

    // ── Pick ─────────────────────────────────────────────────────────────────

    /// <summary>What the picker is asked for: a title, the folder it opens in, and the file types it offers.</summary>
    public sealed record PickRequest(string Title, string? StartDir, IReadOnlyList<FilePickerFileType> Types);

    public static PickRequest RequestFor(PropertyDef def, string raw) =>
        new($"Choose {def.Label}", StartDir(def, raw), FileTypes(def));

    private static async Task<string?> DefaultPickAsync(TopLevel top, PickRequest request)
    {
        var storage = top.StorageProvider;
        var options = new FilePickerOpenOptions
        {
            Title = request.Title,
            AllowMultiple = false,
            FileTypeFilter = request.Types,
        };
        if (request.StartDir is not null)
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(request.StartDir);
        var files = await storage.OpenFilePickerAsync(options);
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private static readonly Regex Patterns = new(@"\(([^)]*\*[^)]*)\)", RegexOptions.Compiled);

    /// <summary>
    /// The picker's file types: the deffile's filter when it names one ("Collision Map Files (*.map)"), else the kind's
    /// own (xmodel and xanim exports, images); always ending with all files, since APE accepts any.
    /// </summary>
    public static IReadOnlyList<FilePickerFileType> FileTypes(PropertyDef def)
    {
        var types = new List<FilePickerFileType>();
        foreach (var part in def.FileFilter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Patterns.Match(part);
            if (!match.Success)
                continue;
            var globs = match.Groups[1].Value.Split(new[] { ';', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (globs.Length > 0 && !globs.All(g => g == "*.*"))
                types.Add(new FilePickerFileType(part[..match.Index].Trim() is { Length: > 0 } name ? name : part) { Patterns = globs });
        }
        if (types.Count == 0)
        {
            switch (def.FileKind)
            {
                case PropertyFileKind.Model:
                    types.Add(new FilePickerFileType("Models") { Patterns = new[] { "*.xmodel_bin", "*.xmodel_export" } });
                    break;
                case PropertyFileKind.Anim:
                    types.Add(new FilePickerFileType("Animations") { Patterns = new[] { "*.xanim_bin", "*.xanim_export" } });
                    break;
                case PropertyFileKind.Texture:
                    types.Add(new FilePickerFileType("Images") { Patterns = new[] { "*.tif", "*.tiff", "*.png", "*.tga", "*.exr", "*.dds" } });
                    break;
            }
        }
        types.Add(new FilePickerFileType("All files") { Patterns = new[] { "*" } });
        return types;
    }

    // ── Missing files (the ⚠ beside a file field) ────────────────────────────

    /// <summary>How long a check is trusted before the next validation re-checks it in the background.</summary>
    private const long ProbeFreshMs = 5000;

    private readonly record struct Probe(bool Exists, long CheckedAt);

    private static readonly ConcurrentDictionary<string, Probe> Probes = new(StringComparer.OrdinalIgnoreCase);
    // Every distinct file value a session validates is remembered; past this many the cache starts over (the next
    // validations simply check again) rather than growing for the life of the app.
    private const int ProbeLimit = 20_000;
    // A finished check posts one refresh; checks landing before it runs ride along with it.
    private static int _notifyPosted;
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<WeakReference<IFileProbeListener>> Listeners = new();

    /// <summary>Hears about finished checks for as long as it lives (held weakly: a closed tab needs no unsubscribe).</summary>
    public static void Listen(IFileProbeListener listener)
    {
        lock (Listeners)
        {
            Listeners.RemoveAll(w => !w.TryGetTarget(out _));
            Listeners.Add(new WeakReference<IFileProbeListener>(listener));
        }
    }

    /// <summary>
    /// The ⚠ line for a file field whose value names no file on disk, from what is known now (never touches the disk);
    /// null when the file exists, isn't known yet, or the value isn't a file path. A value with no extension is a name
    /// the game resolves (an fx or reticle name), not a path, so it is never flagged. An unknown or stale value is
    /// checked in the background, and listeners hear when it lands.
    /// </summary>
    public static string? MissingProblem(PropertyDef def, string raw)
    {
        if (def.FileKind == PropertyFileKind.None || RootDir(def) is not { } dir || raw.Trim().Length == 0)
            return null;
        var value = Unescape(raw);
        if (value.Length == 0 || Path.GetExtension(value).Length < 2 || value.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || value.IndexOfAny(Wildcards) >= 0)
            return null;
        var key = (int)def.FileKind + "|" + dir + "|" + value;
        var known = Probes.TryGetValue(key, out var probe);
        if (!known || Environment.TickCount64 - probe.CheckedAt > ProbeFreshMs)
            Queue(key, def.FileKind, dir, value);
        return known && !probe.Exists ? $"No file at ‘{Shown(def, value)}’" : null;
    }

    private static string Shown(PropertyDef def, string value) =>
        Path.IsPathRooted(value) || def.RelativeRoot.Trim().Length == 0 ? value : Path.Combine(RootName(def), value);

    private static void Queue(string key, PropertyFileKind kind, string dir, string value)
    {
        if (!Pending.TryAdd(key, 0))
            return;
        var root = Root;
        Task.Run(() =>
        {
            bool exists;
            try
            {
                exists = Exists(kind, root, dir, value);
            }
            catch (Exception)
            {
                exists = true; // unreadable: say nothing rather than a wrong ⚠
            }
            var changed = !Probes.TryGetValue(key, out var before) || before.Exists != exists;
            if (Probes.Count >= ProbeLimit)
                Probes.Clear();
            Probes[key] = new Probe(exists, Environment.TickCount64);
            Pending.TryRemove(key, out _);
            if (changed && Interlocked.Exchange(ref _notifyPosted, 1) == 0)
                Dispatcher.UIThread.Post(Notify, DispatcherPriority.Background);
        });
    }

    /// <summary>Blocking: the value as given under the field's folder, then under the install (and, for images, the
    /// texture_assets and model_export folders APE also looks in).</summary>
    private static bool Exists(PropertyFileKind kind, string? root, string dir, string value)
    {
        if (Path.IsPathRooted(value))
            return Present(value);
        if (Present(Path.Combine(dir, value)))
            return true;
        if (root is null)
            return false;
        if (Present(Path.Combine(root, value)))
            return true;
        return kind == PropertyFileKind.Texture
            && (Present(Path.Combine(root, "texture_assets", value)) || Present(Path.Combine(root, "model_export", value)));
    }

    // A folder-valued path ("fx.v2") is there too; only nothing at all is missing.
    private static bool Present(string path) => File.Exists(path) || Directory.Exists(path);

    private static readonly char[] Wildcards = { '*', '?' };

    private static void Notify()
    {
        Volatile.Write(ref _notifyPosted, 0);
        IFileProbeListener[] live;
        lock (Listeners)
        {
            live = Listeners.Select(w => w.TryGetTarget(out var l) ? l : null).OfType<IFileProbeListener>().ToArray();
        }
        foreach (var listener in live)
            listener.FilesProbed();
    }

    /// <summary>Forgets every check (a new install root, or Apex.Shots between scenarios).</summary>
    public static void ResetProbes()
    {
        Probes.Clear();
        ListCache.Clear();
    }

    /// <summary>For Apex.Shots: true once no check is in flight.</summary>
    public static bool ProbesIdle => Pending.IsEmpty;

    // ── File lists (FileCombo) ───────────────────────────────────────────────

    private sealed record Listing(DateTime Stamp, string[] Files);

    private static readonly ConcurrentDictionary<string, Listing> ListCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The file names a FileCombo offers: the files in its folder matching its file types (all of them when it names
    /// none), sorted. Listed on a worker thread and cached until the folder changes; empty without an install or folder.
    /// </summary>
    public static Task<string[]> ListFilesAsync(PropertyDef def)
    {
        if (RootDir(def) is not { } dir)
            return Task.FromResult(Array.Empty<string>());
        var globs = FileTypes(def).SelectMany(t => t.Patterns ?? Array.Empty<string>()).Where(g => g != "*").ToArray();
        return Task.Run(() =>
        {
            if (!Directory.Exists(dir))
                return Array.Empty<string>();
            var stamp = Directory.GetLastWriteTimeUtc(dir);
            var cacheKey = dir + "|" + string.Join(";", globs);
            if (ListCache.TryGetValue(cacheKey, out var cached) && cached.Stamp == stamp)
                return cached.Files;
            var files = Directory.EnumerateFiles(dir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => globs.Length == 0 || globs.Any(g => Glob(name, g)))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ListCache[cacheKey] = new Listing(stamp, files);
            return files;
        });
    }

    private static bool Glob(string name, string glob) =>
        glob.StartsWith("*.", StringComparison.Ordinal)
            ? name.EndsWith(glob[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, glob, StringComparison.OrdinalIgnoreCase);

    // ── Bones (BoneCombo) ────────────────────────────────────────────────────

    private sealed record Skeleton(DateTime Stamp, string[] Bones);

    private static readonly ConcurrentDictionary<string, Skeleton> Skeletons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ModelExtensions = { ".xmodel_bin", ".xmodel_export" };

    /// <summary>
    /// The bone (tag) names of the model <paramref name="model"/> names — an xmodel asset (its LOD0 <c>filename</c>) or
    /// a model_export path — sorted; null when it names nothing Apex can read. The xmodel_bin's skeleton is read on a
    /// worker thread and cached per file until it changes. Call on the UI thread.
    /// </summary>
    public static Task<string[]?> BonesAsync(string model)
    {
        if (Root is null || model.Trim().Length == 0)
            return Task.FromResult<string[]?>(null);
        var modelExport = Path.Combine(Root, "model_export");
        var root = Root;
        // The lookup is readied on the UI thread, as every preview load does; its Find is safe on a worker.
        var lookup = Resolve is { } resolve ? ToolsGfxPreviewService.BeginLoad(resolve) : null;
        return Task.Run(() =>
        {
            var path = ModelFile(model, modelExport, root);
            if (path is null && lookup?.Find(model.Trim(), "xmodel")?.Fields.GetValueOrDefault("filename", "") is { Length: > 0 } file)
                path = ModelFile(file, modelExport, root);
            return path is null ? null : BonesOf(path);
        });
    }

    // ── Surface materials (skinOverride) ─────────────────────────────────────

    private sealed record SurfaceList(DateTime Stamp, string[] Names);

    private static readonly ConcurrentDictionary<string, SurfaceList> SurfaceMaterials = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The material names the surfaces of <paramref name="modelFile"/> use (an xmodel's own <c>filename</c>), sorted;
    /// null when it names nothing Apex can read. The xmodel_bin is read on a worker thread and cached per file until it
    /// changes. Call on the UI thread.
    /// </summary>
    public static Task<string[]?> SurfaceMaterialsAsync(string modelFile)
    {
        if (Root is null || modelFile.Trim().Length == 0)
            return Task.FromResult<string[]?>(null);
        var modelExport = Path.Combine(Root, "model_export");
        var root = Root;
        return Task.Run(() =>
        {
            if (ModelFile(modelFile, modelExport, root) is not { } path)
                return null;
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (SurfaceMaterials.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                    return cached.Names;
                var names = Apex.Render.Data.Conversion.XModel.XModelMaterials.Load(path);
                SurfaceMaterials[path] = new SurfaceList(stamp, names);
                return names;
            }
            catch (Exception)
            {
                return null; // an unreadable model offers nothing; the surface stays free text
            }
        });
    }

    /// <summary>The xmodel_bin a model value names (an export's sibling bin), or null.</summary>
    private static string? ModelFile(string raw, string modelExport, string root)
    {
        var value = Unescape(raw);
        var ext = Path.GetExtension(value);
        if (!ModelExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return null;
        var bin = Path.ChangeExtension(value, ".xmodel_bin");
        foreach (var candidate in Path.IsPathRooted(bin) ? new[] { bin } : new[] { Path.Combine(modelExport, bin), Path.Combine(root, bin) })
            if (File.Exists(candidate))
                return candidate;
        return null;
    }

    private static string[]? BonesOf(string path)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (Skeletons.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                return cached.Bones;
            var bones = XModelSkeleton.Load(path).Bones
                .Select(b => b.Name)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Skeletons[path] = new Skeleton(stamp, bones);
            return bones;
        }
        catch (Exception)
        {
            return null; // an unreadable or malformed model offers no bones; the field stays a plain text field
        }
    }
}
