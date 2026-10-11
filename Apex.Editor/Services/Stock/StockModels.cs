using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Apex.Editor.Services.Stock;

/// <summary>What a clone does with one dependency (the helper's <c>class</c>; docs/classify.md).</summary>
public enum StockClass { Copy, UseShipped, Rebuilt, Local, Embedded, Unsupported, Missing }

public sealed record StockNaming(bool KeepAllowed, bool MustRename, bool OverridesStock, string Why, string Proposed);

public sealed record StockRef(int Id, string How, string? Via);

public sealed record StockRename(string Type, string From, string To);

/// <summary>One node of a stock asset's dependency closure, classified against a target map or mod.</summary>
public sealed class StockDepNode
{
    public int Id { get; init; }
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public StockClass Class { get; init; }
    public string Reason { get; init; } = "";
    public string Zone { get; init; } = "";
    public IReadOnlyList<string> Zones { get; init; } = Array.Empty<string>();
    public StockNaming? Naming { get; init; }
    public IReadOnlyList<StockRef> Refs { get; init; } = Array.Empty<StockRef>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    /// <summary>The node's facts (image size and format, a model's LODs, ...), as the helper sent them; Undefined when it sent none.</summary>
    public JsonElement Facts { get; init; }

    public string Kind => StockTypes.KindOfEngine(Type);

    public static StockClass ParseClass(string? text) => text switch
    {
        "copy" => StockClass.Copy,
        "use_shipped" => StockClass.UseShipped,
        "rebuilt" => StockClass.Rebuilt,
        "local" => StockClass.Local,
        "embedded" => StockClass.Embedded,
        "unsupported" => StockClass.Unsupported,
        _ => StockClass.Missing,
    };

    public static StockDepNode Parse(JsonElement e) => new()
    {
        Id = Int(e, "id"),
        Type = Str(e, "type"),
        Name = Str(e, "name"),
        Class = ParseClass(Str(e, "class")),
        Reason = Str(e, "reason"),
        Zone = Str(e, "zone"),
        Zones = Strings(e, "zones"),
        Naming = e.TryGetProperty("naming", out var n) && n.ValueKind == JsonValueKind.Object
            ? new StockNaming(Bool(n, "keep_allowed"), Bool(n, "must_rename"), Bool(n, "overrides_stock"), Str(n, "why"), Str(n, "proposed"))
            : null,
        Refs = e.TryGetProperty("refs", out var refs) && refs.ValueKind == JsonValueKind.Array
            ? refs.EnumerateArray().Select(r => new StockRef(Int(r, "id"), Str(r, "how"), r.TryGetProperty("via", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)).ToList()
            : Array.Empty<StockRef>(),
        Warnings = Strings(e, "warnings"),
        Options = Strings(e, "options"),
        Facts = e.TryGetProperty("facts", out var f) ? f.Clone() : default,
    };

    internal static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static int Int(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    internal static long Long(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : 0;

    internal static bool Bool(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    internal static IReadOnlyList<string> Strings(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList()
            : Array.Empty<string>();
}

/// <summary>The answer to <c>deps</c>: the closure of a stock asset with what a clone into the target does with each node.</summary>
public sealed class StockDeps
{
    public int Root { get; init; }
    public bool ResolvesByName { get; init; }
    public string Prefix { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string TargetMode { get; init; } = "";
    public IReadOnlyDictionary<string, int> Counts { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<StockRename> Renames { get; init; } = Array.Empty<StockRename>();
    public IReadOnlyList<StockDepNode> Nodes { get; init; } = Array.Empty<StockDepNode>();

    public StockDepNode RootNode => Nodes.First(n => n.Id == Root);

    public static StockDeps Parse(JsonElement e)
    {
        var target = e.TryGetProperty("target", out var t) ? t : default;
        return new StockDeps
        {
            Root = StockDepNode.Int(e, "root"),
            ResolvesByName = StockDepNode.Bool(e, "resolves_by_name"),
            Prefix = StockDepNode.Str(e, "prefix"),
            TargetName = StockDepNode.Str(target, "name"),
            TargetMode = StockDepNode.Str(target, "mode"),
            Counts = e.TryGetProperty("counts", out var c) && c.ValueKind == JsonValueKind.Object
                ? c.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32())
                : new Dictionary<string, int>(),
            Renames = e.TryGetProperty("renames", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => new StockRename(StockDepNode.Str(x, "type"), StockDepNode.Str(x, "from"), StockDepNode.Str(x, "to"))).ToList()
                : Array.Empty<StockRename>(),
            Nodes = e.GetProperty("nodes").EnumerateArray().Select(StockDepNode.Parse).ToList(),
        };
    }

    /// <summary>How many nodes have each class.</summary>
    public int Count(StockClass c) => Nodes.Count(n => n.Class == c);
}

public sealed record StockFile(string Path, string Kind, long Bytes);

public sealed record StockSkipped(string Type, string Name, string Why);

/// <summary>The answer to <c>extract</c>: what was (or with a dry run, would be) written.</summary>
public sealed class StockExtractResult
{
    public string Root { get; init; } = "";
    public string Name { get; init; } = "";
    public bool DryRun { get; init; }
    public string Gdt { get; init; } = "";
    public int Entries { get; init; }
    public IReadOnlyList<StockFile> Files { get; init; } = Array.Empty<StockFile>();
    public IReadOnlyList<StockRename> Renames { get; init; } = Array.Empty<StockRename>();
    public IReadOnlyList<string> Zone { get; init; } = Array.Empty<string>();
    public IReadOnlyList<StockSkipped> Skipped { get; init; } = Array.Empty<StockSkipped>();
    public IReadOnlyList<StockSkipped> Unresolved { get; init; } = Array.Empty<StockSkipped>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public string? Manifest { get; init; }

    public static StockExtractResult Parse(JsonElement e)
    {
        static IReadOnlyList<StockSkipped> Pairs(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Select(x => new StockSkipped(StockDepNode.Str(x, "type"), StockDepNode.Str(x, "name"), StockDepNode.Str(x, "why"))).ToList()
                : Array.Empty<StockSkipped>();
        return new StockExtractResult
        {
            Root = StockDepNode.Str(e, "root"),
            Name = StockDepNode.Str(e, "name"),
            DryRun = StockDepNode.Bool(e, "dry_run"),
            Gdt = StockDepNode.Str(e, "gdt"),
            Entries = e.TryGetProperty("entries", out var en) && en.ValueKind == JsonValueKind.Array ? en.GetArrayLength() : 0,
            Files = e.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => new StockFile(StockDepNode.Str(x, "path"), StockDepNode.Str(x, "kind"), StockDepNode.Long(x, "bytes"))).ToList()
                : Array.Empty<StockFile>(),
            Renames = e.TryGetProperty("renames", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => new StockRename(StockDepNode.Str(x, "type"), StockDepNode.Str(x, "from"), StockDepNode.Str(x, "to"))).ToList()
                : Array.Empty<StockRename>(),
            Zone = StockDepNode.Strings(e, "zone"),
            Skipped = Pairs(e, "skipped"),
            Unresolved = Pairs(e, "unresolved"),
            Notes = StockDepNode.Strings(e, "notes"),
            Manifest = e.TryGetProperty("manifest", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
        };
    }
}

/// <summary>What <c>undo</c> did.</summary>
public sealed record StockUndoResult(int Deleted, int Restored, IReadOnlyList<string> KeptChanged, IReadOnlyList<string> Missing)
{
    public static StockUndoResult Parse(JsonElement e) => new(
        e.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.Array ? d.GetArrayLength() : 0,
        e.TryGetProperty("restored", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : 0,
        StockDepNode.Strings(e, "kept_changed"), StockDepNode.Strings(e, "missing"));
}

/// <summary>An asset's GDT-shaped fields, read-only: what the helper's <c>describe</c> returns (nothing is written).</summary>
public sealed class StockDescription
{
    public string Type { get; init; } = "";
    public string Name { get; init; } = "";
    public string Zone { get; init; } = "";
    public IReadOnlyList<string> Zones { get; init; } = Array.Empty<string>();

    /// <summary>The deffile the fields belong to ("bulletweapon.gdf"); empty when unknown.</summary>
    public string Gdf { get; init; } = "";

    public string? Parent { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Fields { get; init; } = Array.Empty<KeyValuePair<string, string>>();
    public IReadOnlyList<string> Unrecoverable { get; init; } = Array.Empty<string>();
    public bool Supported { get; init; }
    public string? Note { get; init; }

    public static StockDescription Parse(JsonElement e) => new()
    {
        Type = StockDepNode.Str(e, "type"),
        Name = StockDepNode.Str(e, "name"),
        Zone = StockDepNode.Str(e, "zone"),
        Zones = StockDepNode.Strings(e, "zones"),
        Gdf = StockDepNode.Str(e, "gdf"),
        Parent = e.TryGetProperty("parent", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
        Fields = e.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(x => new KeyValuePair<string, string>(StockDepNode.Str(x, "key"), ValueText(x))).ToList()
            : Array.Empty<KeyValuePair<string, string>>(),
        Unrecoverable = e.TryGetProperty("unrecoverable", out var u) && u.ValueKind == JsonValueKind.Array
            ? u.EnumerateArray().Select(x => StockDepNode.Str(x, "key")).Where(k => k.Length > 0).ToList()
            : Array.Empty<string>(),
        Supported = !e.TryGetProperty("supported", out var s) || s.ValueKind != JsonValueKind.False,
        Note = e.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
    };

    private static string ValueText(JsonElement field) =>
        field.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
}

/// <summary>What a <c>cache</c> request wrote (the helper's answer, trimmed to what the UI and the checks report).</summary>
public sealed record StockCacheResult(bool Warm, double Seconds, long Bytes, int NewFiles, IReadOnlyList<string> Skipped)
{
    /// <summary>The manifest was there already: nothing was asked of the helper.</summary>
    public static StockCacheResult AlreadyThere { get; } = new(true, 0, 0, 0, []);

    public static StockCacheResult Parse(System.Text.Json.JsonElement e)
    {
        var skipped = new List<string>();
        if (e.TryGetProperty("skipped", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var item in s.EnumerateArray())
                skipped.Add(StockDepNode.Str(item, "type") + " " + StockDepNode.Str(item, "name") + ": " + StockDepNode.Str(item, "why"));
        var files = 0;
        if (e.TryGetProperty("files", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var item in f.EnumerateArray())
                if (item.TryGetProperty("new", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.True)
                    files++;
        return new StockCacheResult(e.TryGetProperty("warm", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.True,
            e.TryGetProperty("seconds", out var sec) && sec.TryGetDouble(out var d) ? d : 0, StockDepNode.Long(e, "bytes"), files, skipped);
    }
}
