using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Apex.Editor.Services.Stock;

/// <summary>What a stock sound list is asked for (the helper's <c>sounds</c>; docs/protocol.md).</summary>
/// <param name="Group">One item per alias name and language (true), or one per definition (false).</param>
public sealed record StockSoundQuery(string Query = "", string? Zone = null, string? Lang = null, string? Status = null, bool Group = true, int Limit = 200, int Offset = 0);

/// <summary>One definition of a sound alias in one zone's bank (the helper's ungrouped item, and a grouped item's best one).</summary>
public sealed class StockSoundDef
{
    public string Name { get; init; } = "";
    public string Zone { get; init; } = "";
    public string Bank { get; init; } = "";
    public string Lang { get; init; } = "all";
    public string Hash { get; init; } = "";
    public int Variants { get; init; }

    /// <summary>"loaded", "streamed", "primed" or "unknown".</summary>
    public string Storage { get; init; } = "unknown";
    public bool Looping { get; init; }
    public string File { get; init; } = "";

    /// <summary>"ok", "no_file", "no_audio" or "unreadable": whether the reader found the audio.</summary>
    public string Status { get; init; } = "";
    public string BankFile { get; init; } = "";
    public int DurationMs { get; init; }
    public int Channels { get; init; }
    public int Rate { get; init; }

    /// <summary>"flac", "pcm16" or "other": how the bank stores the audio.</summary>
    public string Format { get; init; } = "";
    public long Size { get; init; }
    public bool Secondary { get; init; }
    public bool Stop { get; init; }
    public bool Duck { get; init; }
    public bool Facial { get; init; }

    public bool HasAudio => Status == "ok";

    public static StockSoundDef Parse(JsonElement e) => new()
    {
        Name = StockDepNode.Str(e, "name"),
        Zone = StockDepNode.Str(e, "zone"),
        Bank = StockDepNode.Str(e, "bank"),
        Lang = Or(StockDepNode.Str(e, "lang"), "all"),
        Hash = StockDepNode.Str(e, "hash"),
        Variants = StockDepNode.Int(e, "variants"),
        Storage = Or(StockDepNode.Str(e, "storage"), "unknown"),
        Looping = StockDepNode.Bool(e, "looping"),
        File = StockDepNode.Str(e, "file"),
        Status = StockDepNode.Str(e, "status"),
        BankFile = StockDepNode.Str(e, "bank_file"),
        DurationMs = StockDepNode.Int(e, "duration_ms"),
        Channels = StockDepNode.Int(e, "channels"),
        Rate = StockDepNode.Int(e, "rate"),
        Format = StockDepNode.Str(e, "format"),
        Size = StockDepNode.Long(e, "size"),
        Secondary = StockDepNode.Bool(e, "secondary"),
        Stop = StockDepNode.Bool(e, "stop"),
        Duck = StockDepNode.Bool(e, "duck"),
        Facial = StockDepNode.Bool(e, "facial"),
    };

    private static string Or(string text, string fallback) => text.Length > 0 ? text : fallback;
}

/// <summary>One row of the stock sound list: an alias name in a language (grouped), or one definition (ungrouped).</summary>
public sealed class StockSoundItem
{
    public string Name { get; init; } = "";
    public string Lang { get; init; } = "all";
    public string Hash { get; init; } = "";
    public int Definitions { get; init; } = 1;
    public IReadOnlyList<string> Zones { get; init; } = Array.Empty<string>();
    public int Variants { get; init; }
    public bool Looping { get; init; }
    public string Storage { get; init; } = "unknown";
    public int DurationMs { get; init; }
    public int Channels { get; init; }
    public int Rate { get; init; }
    public bool HasAudio { get; init; }

    public string Zone => Zones.Count > 0 ? Zones[0] : "";

    public static StockSoundItem Parse(JsonElement e)
    {
        var zones = StockDepNode.Strings(e, "zones");
        if (zones.Count == 0 && StockDepNode.Str(e, "zone") is { Length: > 0 } one)
            zones = new[] { one };
        var hasAudio = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("has_audio", out var h)
            ? h.ValueKind == JsonValueKind.True
            : StockDepNode.Str(e, "status") == "ok";
        return new StockSoundItem
        {
            Name = StockDepNode.Str(e, "name"),
            Lang = StockDepNode.Str(e, "lang") is { Length: > 0 } l ? l : "all",
            Hash = StockDepNode.Str(e, "hash"),
            Definitions = Math.Max(1, StockDepNode.Int(e, "definitions")),
            Zones = zones,
            Variants = StockDepNode.Int(e, "variants"),
            Looping = StockDepNode.Bool(e, "looping"),
            Storage = StockDepNode.Str(e, "storage") is { Length: > 0 } s ? s : "unknown",
            DurationMs = StockDepNode.Int(e, "duration_ms"),
            Channels = StockDepNode.Int(e, "channels"),
            Rate = StockDepNode.Int(e, "rate"),
            HasAudio = hasAudio,
        };
    }
}

/// <summary>A page of the sound list: <see cref="Total"/> counts every match, before limit and offset.</summary>
public sealed record StockSoundPage(int Total, IReadOnlyList<StockSoundItem> Items)
{
    public static StockSoundPage Parse(JsonElement e) => new(
        StockDepNode.Int(e, "total"),
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(StockSoundItem.Parse).ToList()
            : Array.Empty<StockSoundItem>());
}

/// <summary>Every definition of one alias name (the helper's <c>sound</c>).</summary>
public sealed record StockSoundDefs(string Name, IReadOnlyList<StockSoundDef> Definitions)
{
    public static StockSoundDefs Parse(JsonElement e) => new(
        StockDepNode.Str(e, "name"),
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("definitions", out var defs) && defs.ValueKind == JsonValueKind.Array
            ? defs.EnumerateArray().Select(StockSoundDef.Parse).ToList()
            : Array.Empty<StockSoundDef>());
}

/// <summary>What <c>sound_export</c> wrote.</summary>
public sealed record StockSoundExport(string Path, string Format, string Zone, string Bank, string File, int Variant, int Variants, long Bytes, int DurationMs, int Channels, int Rate)
{
    public static StockSoundExport Parse(JsonElement e) => new(
        StockDepNode.Str(e, "path"), StockDepNode.Str(e, "format"), StockDepNode.Str(e, "zone"), StockDepNode.Str(e, "bank"), StockDepNode.Str(e, "file"),
        StockDepNode.Int(e, "variant"), StockDepNode.Int(e, "variants"), StockDepNode.Long(e, "bytes"),
        StockDepNode.Int(e, "duration_ms"), StockDepNode.Int(e, "channels"), StockDepNode.Int(e, "rate"));
}
