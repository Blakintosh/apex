using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apex.Editor.Services.Session;

/// <summary>
/// One record in the session journal. Structural operations (<c>gdt</c>, <c>add</c>, <c>ren</c>, <c>del</c>) replay
/// in order; a <c>set</c> is an asset's whole difference from its baseline at the time it was written, so the newest
/// one per asset wins and a torn or lost older one costs nothing. Assets are named by GDT and asset name as they
/// were when the record was written, which is what the replay sees at that point.
/// </summary>
public sealed class JournalEntry
{
    /// <summary>
    /// <c>head</c> (format version and environment), <c>gdt</c> (new GDT), <c>add</c> (new or duplicated asset with
    /// all its values), <c>ren</c> (rename), <c>del</c> (delete), <c>mov</c> (moved from GDT <c>gdt</c> to <c>to</c>),
    /// <c>flat</c> (Underive: <c>parent</c> was its parent, <c>props</c> every value it took), <c>set</c> (an asset's
    /// changed values), <c>xset</c> (an asset's changed extension values; <c>type</c> is the extension), <c>tabs</c>
    /// (open tabs), <c>bye</c> (Apex closed normally).
    /// </summary>
    [JsonPropertyName("op")] public string Op { get; set; } = "";

    [JsonPropertyName("gdt")] public string? Gdt { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }

    /// <summary><c>ren</c>: the new name. <c>mov</c>: the GDT it moved to.</summary>
    [JsonPropertyName("to")] public string? To { get; set; }

    /// <summary><c>add</c>: the asset's type and derivation parent. <c>xset</c>: the extension id.</summary>
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("parent")] public string? Parent { get; set; }

    /// <summary><c>add</c>: the asset it was duplicated from, when it was.</summary>
    [JsonPropertyName("from")] public string? From { get; set; }

    /// <summary><c>add</c>: every value the new asset started with.</summary>
    [JsonPropertyName("props")] public Dictionary<string, string>? Props { get; set; }

    /// <summary><c>add</c>, <c>mov</c>: the asset's extension values it started with (or took along), by extension id.</summary>
    [JsonPropertyName("xprops")] public Dictionary<string, Dictionary<string, string>>? ExtensionProps { get; set; }

    /// <summary><c>set</c>: each changed key with its baseline and current value (null: the key is absent).</summary>
    [JsonPropertyName("ch")] public List<JournalChange>? Changes { get; set; }

    [JsonPropertyName("tabs")] public List<JournalTab>? Tabs { get; set; }
    [JsonPropertyName("active")] public int? Active { get; set; }

    /// <summary><c>head</c>: format version and which install (or mock data) the session belongs to.</summary>
    [JsonPropertyName("v")] public int? Version { get; set; }
    [JsonPropertyName("env")] public string? Env { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, Options);

    public static JournalEntry? FromUtf8(System.ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<JournalEntry>(utf8, Options);
}

public sealed class JournalChange
{
    [JsonPropertyName("k")] public string Key { get; set; } = "";
    [JsonPropertyName("o")] public string? Old { get; set; }
    [JsonPropertyName("n")] public string? New { get; set; }
}

public sealed class JournalTab
{
    [JsonPropertyName("gdt")] public string Gdt { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("preview")] public bool Preview { get; set; }
}
