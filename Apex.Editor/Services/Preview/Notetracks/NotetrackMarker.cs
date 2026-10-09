using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Apex.Editor.Services.Preview.Notetracks;

/// <summary>Where a timeline marker comes from: the xanim file's own notetracks, or an action in the asset's GDT entry.</summary>
public enum NotetrackSource { Export, Gdt }

/// <summary>Whether a marker's sound can be heard in the preview.</summary>
public enum NotetrackSoundState
{
    /// <summary>Not a sound action.</summary>
    None,
    /// <summary>A sound action whose alias is still being looked up.</summary>
    Pending,
    /// <summary>The alias resolved to at least one raw file on disk.</summary>
    Playable,
    /// <summary>No alias of that name in share/raw/sound/aliases (stock sounds ship compiled in the game's banks).</summary>
    UnknownAlias,
    /// <summary>The alias exists but none of its files is under sound_assets.</summary>
    NoRawFile,
    /// <summary>Its raw files exist but none can be decoded here (only PCM or float WAV plays in the preview).</summary>
    Undecodable,
}

/// <summary>A sound variant ready to play: the file on disk and its linear volume range (0..1).</summary>
public sealed record SoundVariant(string Path, float VolumeMin, float VolumeMax);

/// <summary>
/// One marker on the notetrack timeline. <see cref="Label"/> is how APE names it (the export notetrack's name, or the
/// GDT category entry such as "Sound note 2"); <see cref="Action"/> and <see cref="Param"/> are the GDT action and its
/// first parameter (null for export notetracks).
/// </summary>
public sealed record NotetrackMarker(int Frame, string Label, NotetrackSource Source, string? Action = null, string? Param = null)
{
    public NotetrackSoundState SoundState { get; init; } = NotetrackSoundState.None;

    /// <summary>The GDT entry's note id (<c>customnote3</c>, <c>fx_startupnote0</c>…): its keys are this plus <c>action</c>,
    /// <c>frame</c>, <c>actionparam1</c>…. Null for an exported notetrack.</summary>
    public string? Id { get; init; }

    /// <summary>The playable variants (rows of the alias with a raw file), when <see cref="SoundState"/> is Playable.</summary>
    public IReadOnlyList<SoundVariant> Variants { get; init; } = Array.Empty<SoundVariant>();

    /// <summary>The alias a sound action plays (its first parameter), or null.</summary>
    public string? SoundAlias => SoundState != NotetrackSoundState.None ? Param : null;

    public bool IsSound => SoundState != NotetrackSoundState.None;

    /// <summary>
    /// The note's one name, wherever Apex shows it (lane, strip, tooltip, menu, undo): an entry note's action and first
    /// parameter ("Sound · wpn_fire", or just "Show Weapon"). An exported note reads the same way when the game knows
    /// its prefix (sndnt#wpn_raise is "Sound · wpn_raise", rmbnt#… a Rumble), and by its own name otherwise.
    /// </summary>
    public string Name => Source == NotetrackSource.Export
        ? ExportKind(Label) is { } kind ? $"{kind} · {Label[(Label.IndexOf('#') + 1)..]}" : Label
        : string.IsNullOrEmpty(Param) ? Action ?? Label
        : $"{Action} · {Param}";

    /// <summary>The action an exported note's prefix stands for in game (sndnt# a Sound, rmbnt# a Rumble), or null.</summary>
    public static string? ExportKind(string label) =>
        label.StartsWith("sndnt#", StringComparison.OrdinalIgnoreCase) ? "Sound"
        : label.StartsWith("rmbnt#", StringComparison.OrdinalIgnoreCase) ? "Rumble"
        : null;

    /// <summary>One line for the hover tip: the name first, then its frame and where it lives, and why a sound is silent.</summary>
    public string Describe()
    {
        var where = Source == NotetrackSource.Export ? "exported in the xanim" : Label;
        var why = SoundState switch
        {
            NotetrackSoundState.UnknownAlias => "  ·  silent: no alias of that name in share/raw/sound/aliases",
            NotetrackSoundState.NoRawFile => "  ·  silent: no raw file under sound_assets (it plays from the game's sound banks)",
            NotetrackSoundState.Undecodable => "  ·  silent: its raw file can't be decoded here (the preview plays PCM WAV)",
            NotetrackSoundState.Pending => "  ·  looking up the alias",
            _ => "",
        };
        return $"{Name}  ·  frame {Frame.ToString(CultureInfo.InvariantCulture)}  ·  {where}{why}";
    }

    /// <summary>A sound that can't be heard in the preview (no alias, no raw file, or a file it can't decode).</summary>
    public bool IsSilent => SoundState is NotetrackSoundState.UnknownAlias or NotetrackSoundState.NoRawFile or NotetrackSoundState.Undecodable;
}

/// <summary>
/// The GDT notetrack actions of an xanim entry, read the way deffiles/xanim.awi lays them out. AddNoteTrackCategory
/// creates, per prefix and index i, the id <c>prefix + i</c> with the keys <c>&lt;id&gt;action</c> (a combo; "None"
/// means unused), <c>&lt;id&gt;actionparam1</c> / <c>&lt;id&gt;actionparam2</c>, and for normal notes only
/// <c>&lt;id&gt;frame</c> and <c>&lt;id&gt;useexistingnote</c> (an exported notetrack whose frame it piggybacks on).
/// Startup notes run as the anim starts and shutdown notes as it ends, so they sit on the first and last frame.
/// </summary>
public static class GdtNotetracks
{
    private enum Kind { Normal, Startup, Shutdown }

    // (prefix, count, kind, APE's category title) exactly as xanim.awi's AddNoteTrackCategory calls.
    private static readonly (string Prefix, int Count, Kind Kind, string Title)[] Categories =
    {
        ("customnote", 50, Kind.Normal, "Note"),
        ("startupnote", 10, Kind.Startup, "Startup note"),
        ("shutdownnote", 10, Kind.Shutdown, "Shutdown note"),
        ("fx_customnote", 20, Kind.Normal, "FX note"),
        ("fx_startupnote", 10, Kind.Startup, "FX startup note"),
        ("fx_shutdownnote", 10, Kind.Shutdown, "FX shutdown note"),
        ("sound_customnote", 20, Kind.Normal, "Sound note"),
        ("sound_startupnote", 10, Kind.Startup, "Sound startup note"),
        ("sound_shutdownnote", 10, Kind.Shutdown, "Sound shutdown note"),
    };

    /// <summary>
    /// True for the keys <see cref="Read"/> uses: <c>&lt;prefix&gt;&lt;i&gt;</c> followed by <c>action</c>, <c>frame</c>,
    /// <c>useexistingnote</c> or <c>actionparam1</c> / <c>actionparam2</c>.
    /// </summary>
    public static bool IsNotetrackKey(string key)
    {
        foreach (var (prefix, count, _, _) in Categories)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var i = prefix.Length;
            var digits = i;
            while (digits < key.Length && char.IsAsciiDigit(key[digits]))
                digits++;
            if (digits == i || !int.TryParse(key.AsSpan(i, digits - i), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n >= count)
                continue;
            if (key.AsSpan(digits) is "action" or "frame" or "useexistingnote" or "actionparam1" or "actionparam2")
                return true;
        }
        return false;
    }

    /// <summary>How many notes of <paramref name="prefix"/> an entry holds (xanim.awi's MAX_* for it); 0 for an unknown prefix.</summary>
    public static int Capacity(string prefix)
    {
        foreach (var (p, count, _, _) in Categories)
            if (p == prefix)
                return count;
        return 0;
    }

    /// <summary>The prefix of a note id (<c>fx_customnote3</c> → <c>fx_customnote</c>), or null for anything else.</summary>
    public static string? PrefixOf(string id)
    {
        var end = id.Length;
        while (end > 0 && char.IsAsciiDigit(id[end - 1]))
            end--;
        if (end == id.Length || end == 0)
            return null;
        var prefix = id[..end];
        return Capacity(prefix) > 0 ? prefix : null;
    }

    /// <summary>True for a note placed by its own frame (a custom note); startup and shutdown notes sit on the clip's ends.</summary>
    public static bool HasFrame(string id) =>
        PrefixOf(id) is { } prefix && Array.Find(Categories, c => c.Prefix == prefix).Kind == Kind.Normal;

    /// <summary>The actions that start a sound (xanim.awi gives each a "Sound Alias" first parameter).</summary>
    public static bool IsSoundAction(string action) =>
        action is "Sound" or "2D Sound" or "Vox" or "Start looping sound";

    /// <summary>
    /// The used actions of an xanim entry (any whose action is set and not "None"), placed on the clip's frames.
    /// <paramref name="exportNotes"/> resolves <c>useexistingnote</c>; <paramref name="lastFrame"/> places shutdown notes.
    /// </summary>
    public static List<NotetrackMarker> Read(IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<(string Name, int Frame)> exportNotes, int lastFrame)
    {
        var markers = new List<NotetrackMarker>();
        foreach (var (prefix, count, kind, title) in Categories)
        {
            for (var i = 0; i < count; i++)
            {
                var id = prefix + i.ToString(CultureInfo.InvariantCulture);
                var action = fields.GetValueOrDefault(id + "action", "").Trim();
                if (action.Length == 0 || action == "None")
                    continue;
                var frame = kind switch
                {
                    Kind.Startup => 0,
                    Kind.Shutdown => lastFrame,
                    _ => NormalFrame(fields, id, exportNotes),
                };
                var param = fields.GetValueOrDefault(id + "actionparam1", "").Trim();
                // A custom note keeps its own frame even past the clip's end (the dock draws it at the edge and flags it);
                // startup and shutdown notes sit on the ends by definition.
                markers.Add(new NotetrackMarker(kind == Kind.Normal ? Math.Max(frame, 0) : Math.Clamp(frame, 0, Math.Max(lastFrame, 0)),
                    $"{title} {(i + 1).ToString(CultureInfo.InvariantCulture)}", NotetrackSource.Gdt, action,
                    param.Length > 0 ? param : null)
                {
                    SoundState = IsSoundAction(action) && param.Length > 0 ? NotetrackSoundState.Pending : NotetrackSoundState.None,
                    Id = id,
                });
            }
        }
        return markers;
    }

    private static int NormalFrame(IReadOnlyDictionary<string, string> fields, string id, IReadOnlyList<(string Name, int Frame)> exportNotes)
    {
        // useexistingnote names an exported notetrack; its frame wins (xanim.awi disables <id>frame while it is set).
        var linked = fields.GetValueOrDefault(id + "useexistingnote", "").Trim();
        if (linked.Length > 0)
            foreach (var (name, frame) in exportNotes)
                if (name.Equals(linked, StringComparison.OrdinalIgnoreCase))
                    return frame;
        // AddEntry_Int(<id>frame, 1, 1, 10000): unset reads as the default, 1.
        return int.TryParse(fields.GetValueOrDefault(id + "frame", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 1;
    }

    /// <summary>Export notetracks as markers (frames as the reader gives them, relative to the clip's first frame).</summary>
    public static IEnumerable<NotetrackMarker> Export(IEnumerable<(string Name, int Frame)> notes, int lastFrame) =>
        notes.Select(n => new NotetrackMarker(Math.Clamp(n.Frame, 0, Math.Max(lastFrame, 0)), n.Name, NotetrackSource.Export));
}
