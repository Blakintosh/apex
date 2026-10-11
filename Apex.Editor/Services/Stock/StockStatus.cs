using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Apex.Editor.Services.Stock;

/// <summary>One feature's verdict from the helper's <c>status</c> (docs/status.md): may it be offered, and if not why and what fixes it.</summary>
public sealed record StockVerdict(bool Ok, string Reason, string Fix, IReadOnlyList<string> Warnings)
{
    public static StockVerdict Off(string reason, string fix = "") => new(false, reason, fix, Array.Empty<string>());

    /// <summary>"Reason. Fix." as one line for a note; empty when the verdict is ok.</summary>
    public string Note
    {
        get
        {
            if (Ok)
                return "";
            var reason = Reason.TrimEnd();
            return Fix.Length == 0 ? reason : reason + " " + Fix.TrimEnd('.') + ".";
        }
    }

    internal static StockVerdict Parse(JsonElement e) => new(
        StockDepNode.Bool(e, "ok"),
        StockDepNode.Str(e, "reason"),
        StockDepNode.Str(e, "fix"),
        StockDepNode.Strings(e, "warnings"));
}

/// <summary>A component of the install that wrote a registry entry (Ultrasound, ...), passed through by the helper.</summary>
public sealed record StockOtherComponent(string Id, string Version, string Path);

/// <summary>
/// What the install has and what each Stock Library feature may therefore do: the helper's <c>status</c> answer. Apex asks
/// the helper (it knows the registry, the disk and Dragon's capabilities) and only shows the verdicts.
/// </summary>
public sealed class StockStatus
{
    public static StockStatus Unknown(string reason, string fix = "") => new()
    {
        Known = false,
        Browse = StockVerdict.Off(reason, fix),
        Clone = StockVerdict.Off(reason, fix),
        Radiant = StockVerdict.Off(reason, fix),
        UseDirectly = StockVerdict.Off(reason, fix),
    };

    /// <summary>The helper answered (false: not asked yet, or it could not).</summary>
    public bool Known { get; init; }

    public StockVerdict Browse { get; init; } = StockVerdict.Off("");
    public StockVerdict Clone { get; init; } = StockVerdict.Off("");
    public StockVerdict Radiant { get; init; } = StockVerdict.Off("");
    public StockVerdict UseDirectly { get; init; } = StockVerdict.Off("");

    public string Install { get; init; } = "";

    // stock.db
    public bool IndexExists { get; init; }
    public long Assets { get; init; }
    public bool Indexing { get; init; }

    // the helper itself
    public string StocklibVersion { get; init; } = "";
    public int Protocol { get; init; }

    // Dragon
    public bool DragonPresent { get; init; }
    public string DragonVersion { get; init; } = "";
    public string DragonPath { get; init; } = "";
    public IReadOnlyList<string> DragonCapabilities { get; init; } = Array.Empty<string>();

    // Blackbird
    /// <summary>"blackbird.json" when Blackbird reported itself, else "settings" (it has not written an entry).</summary>
    public string BlackbirdSource { get; init; } = "";
    public string BlackbirdVersion { get; init; } = "";
    public bool BlackbirdSettingsFound { get; init; }

    // Radiant
    public bool RadiantPluginPresent { get; init; }
    public string RadiantPluginPath { get; init; } = "";

    public IReadOnlyList<StockOtherComponent> Others { get; init; } = Array.Empty<StockOtherComponent>();

    public static StockStatus Parse(JsonElement e)
    {
        JsonElement Obj(JsonElement p, string key) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        var verdicts = Obj(e, "verdicts");
        var components = Obj(e, "components");
        var stocklib = Obj(components, "stocklib");
        var dragon = Obj(components, "dragon");
        var blackbird = Obj(components, "blackbird");
        var entry = Obj(blackbird, "entry");
        var plugin = Obj(components, "radiant_plugin");
        var db = Obj(e, "stock_db");
        var others = components.ValueKind == JsonValueKind.Object && components.TryGetProperty("others", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(x => new StockOtherComponent(StockDepNode.Str(x, "id"), StockDepNode.Str(x, "version"), StockDepNode.Str(x, "path"))).Where(x => x.Id.Length > 0).ToList()
            : new List<StockOtherComponent>();
        return new StockStatus
        {
            Known = verdicts.ValueKind == JsonValueKind.Object,
            Browse = StockVerdict.Parse(Obj(verdicts, "browse")),
            Clone = StockVerdict.Parse(Obj(verdicts, "clone")),
            Radiant = StockVerdict.Parse(Obj(verdicts, "radiant")),
            UseDirectly = StockVerdict.Parse(Obj(verdicts, "use_directly")),
            Install = StockDepNode.Str(e, "install"),
            IndexExists = StockDepNode.Bool(db, "exists"),
            Assets = StockDepNode.Long(e, "assets"),
            Indexing = StockDepNode.Bool(e, "indexing"),
            StocklibVersion = StockDepNode.Str(stocklib, "version"),
            Protocol = StockDepNode.Int(stocklib, "protocol"),
            DragonPresent = StockDepNode.Bool(dragon, "present"),
            DragonVersion = StockDepNode.Str(dragon, "version"),
            DragonPath = StockDepNode.Str(dragon, "path"),
            DragonCapabilities = StockDepNode.Strings(dragon, "capabilities"),
            BlackbirdSource = StockDepNode.Str(blackbird, "source"),
            BlackbirdVersion = StockDepNode.Str(entry, "version"),
            BlackbirdSettingsFound = StockDepNode.Str(blackbird, "settings_path").Length > 0 && System.IO.File.Exists(StockDepNode.Str(blackbird, "settings_path")),
            RadiantPluginPresent = StockDepNode.Bool(plugin, "present"),
            RadiantPluginPath = StockDepNode.Str(plugin, "path"),
            Others = others,
        };
    }
}
