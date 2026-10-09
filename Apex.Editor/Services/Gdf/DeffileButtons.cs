using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Gdf;

/// <summary>One button of a deffile button group: its label, the script function it calls and the text it passes.</summary>
public sealed record DeffileButton(string Label, string Callback, string Param);

/// <summary>
/// A deffile ButtonGroup as this asset's GenerateUI left it: where it sits (its section, before <see cref="AnchorKey"/>
/// or at the section's end when null), what it is called, whether it shows, and its buttons in deffile order.
/// <see cref="NextKey"/> is the next property in any section: a section of buttons alone goes before that one's.
/// </summary>
public sealed record DeffileButtonGroup(
    string Name, string Title, string? ToolTip, string Category, string? AnchorKey, string? NextKey, bool Visible, bool Enabled,
    IReadOnlyList<DeffileButton> Buttons);

/// <summary>
/// What a button's callback did: the values it wrote, the keys it cleared (ClearSpecified: back to what they inherit,
/// unless a later write gives them another value), the keys it read, the entry it scrolled to, and why it stopped early.
/// </summary>
public sealed record DeffileButtonRun(
    IReadOnlyDictionary<string, string> Written, IReadOnlySet<string> Cleared, IReadOnlySet<string> Read, string? ScrollTo,
    string? Error);

/// <summary>What a callback asks of the app: a message box (answered on the UI thread), and XModel info.</summary>
public interface IDeffileButtonServices
{
    /// <summary>
    /// APE's <c>MessageBox( text, "OK" | "YESNO" )</c>: "OK", "YES" or "NO". Called off the UI thread; may block, and may
    /// throw to stop the script (the question couldn't be put).
    /// </summary>
    string MessageBox(string text, string buttons);

    /// <summary>How long the script has spent waiting on the user (not counted against its time limit).</summary>
    TimeSpan Waited { get; }

    /// <summary>APE's <c>ShowXModelInfo( name )</c>.</summary>
    void ShowXModelInfo(string assetName);
}

/// <summary>
/// APE's deffile buttons (<c>Asset.AddEntry_ButtonGroup( id ).AddButton( label, icon, "void Callback( asset Asset, const
/// string&amp; param )", param )</c>). A click calls the callback against the asset's current values; the values it
/// writes come back as one edit. The callback never sees the record: it reads through <c>valueOf</c> and writes into
/// its own table, so it can run off the UI thread while a question it asked waits for an answer.
/// </summary>
public static class DeffileButtons
{
    /// <summary>The install the deffiles came from: <c>ReadTextFile</c> reads under it and nowhere else.</summary>
    public static string? InstallRoot { get; set; }

    internal static (IReadOnlyList<DeffileButtonGroup>, IReadOnlyDictionary<string, string>, IReadOnlySet<string>) Collect(AssetSchema schema, RecordingAsset host)
    {
        // Most types have no buttons: their runs pay nothing here.
        if (!host.Entries.Any(e => e.Kind == EntryKind.ButtonGroup && e.Buttons is { Count: > 0 }))
            return ([], new Dictionary<string, string>(), new HashSet<string>());
        var ordered = host.Entries.Where(e => !e.Dead).OrderBy(host.SortKey).ToList();
        List<DeffileButtonGroup>? groups = null;
        Dictionary<string, string>? unsaved = null;
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool IsProperty(RecordedEntry x) => x.Kind is not (EntryKind.Label or EntryKind.ButtonGroup) && schema.Find(x.PrimaryName) is not null;
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            foreach (var name in e.Names)
                if (name.Length > 0 && schema.Find(name) is null)
                {
                    (unsaved ??= new(StringComparer.OrdinalIgnoreCase)).TryAdd(name, GdfValue.AsString(e.Value));
                    if (e.Save && e.Kind is not (EntryKind.Label or EntryKind.ButtonGroup))
                        kept.Add(name);
                }
            if (e.Kind != EntryKind.ButtonGroup || e.Buttons is not { Count: > 0 } buttons)
                continue;
            // The property registered next in the same section: the group sits right above it. And the next in any
            // section, for a group whose section has no property of its own (bonuszmdata's "Add Skipto").
            string? anchor = null, next = null;
            for (var j = i + 1; j < ordered.Count && anchor is null; j++)
                if (IsProperty(ordered[j]))
                {
                    next ??= ordered[j].PrimaryName;
                    if (ordered[j].Category == e.Category)
                        anchor = ordered[j].PrimaryName;
                }
            (groups ??= new()).Add(new DeffileButtonGroup(e.PrimaryName, CleanTitle(e.Title), e.ToolTip is { Length: > 0 } tip ? tip : null,
                e.Category, anchor, next, e.Visible, e.Enabled,
                buttons.Select(b => new DeffileButton(b.Label, b.Callback, b.Param)).ToArray()));
        }
        return ((IReadOnlyList<DeffileButtonGroup>?)groups ?? [], (IReadOnlyDictionary<string, string>?)unsaved ?? new Dictionary<string, string>(), kept);
    }

    // "Edit:", " ", "\n\n" (a spacer APE lays out): the row's label, trimmed of the colon its own layout adds.
    private static string CleanTitle(string? title) => GdfRuntime.CleanTitle(title) ?? "";

    /// <summary>The script function a button calls: "void AddItem( asset Asset, const string&amp; params )" → AddItem.</summary>
    internal static string FunctionName(string callback)
    {
        var open = callback.IndexOf('(');
        var head = (open >= 0 ? callback[..open] : callback).Trim();
        var space = head.LastIndexOfAny([' ', '\t']);
        return space >= 0 ? head[(space + 1)..] : head;
    }

    /// <summary>How long a callback may run, not counting time a question waits on the user.</summary>
    public static TimeSpan TimeLimit { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest value a callback may write, in characters (as every string limit here is), and the largest file
    /// ReadTextFile reads: its size in bytes, which a file's character count never exceeds.
    /// </summary>
    public const int MaxText = GdfConcat.MaxChars;

    /// <param name="valueOf">A key's value as the asset holds it now (its own, its parent's, or the default).</param>
    /// <param name="inheritedOf">What a key holds when this asset doesn't specify it (its parent's value, or the default).</param>
    internal static DeffileButtonRun Run(GdfProgram program, string assetName, IReadOnlyDictionary<string, string> values,
        Func<string, string> valueOf, Func<string, string> inheritedOf, DeffileButton button, IDeffileButtonServices services)
    {
        var asset = new ButtonAsset(assetName, valueOf, inheritedOf);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = GdfInterpreter.Call(program, FunctionName(button.Callback), asset, new ButtonFunctions(services),
            new RecordingAsset(assetName, values), () => clock.Elapsed - services.Waited > TimeLimit, button.Param);
        return new DeffileButtonRun(asset.Written, asset.Cleared, asset.Reads, asset.ScrollTo, error);
    }

    // Copy and Paste (destructibledef) use a format of APE's own; nothing else reads it, so it stays in Apex.
    private static readonly Dictionary<string, string> Clipboard = new(StringComparer.Ordinal);

    /// <summary>
    /// APE's <c>ReadTextFile( path )</c>, read-only and inside the install: relative to AssetWorks (where APE runs from,
    /// so "scripts/assetpreviewer_lookuptable.csv"), else to the install root. Anything outside reads as empty.
    /// </summary>
    internal static List<object?> ReadInstallText(string path)
    {
        var lines = new List<object?>();
        if (InstallRoot is not { } root || path.Length == 0)
            return lines;
        // Ends in exactly one separator, a drive root (D:\) included, so the prefix test below can't match D:\x for D:\xy.
        var fullRoot = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(fullRoot))
            fullRoot += Path.DirectorySeparatorChar;
        foreach (var candidate in new[] { Path.Combine(fullRoot, "AssetWorks", path), Path.Combine(fullRoot, path) })
        {
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch { continue; }
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                continue;
            try
            {
                if (new FileInfo(full).Length > MaxText)
                    return lines;
                // Streamed: under the cap a file's lines are a few MB at most, never a second copy of it.
                foreach (var line in File.ReadLines(full))
                    lines.Add(line);
            }
            // All or nothing, as before: a read that fails partway returns no lines rather than some of them.
            catch (IOException) { lines.Clear(); }
            catch (UnauthorizedAccessException) { lines.Clear(); }
            return lines;
        }
        return lines;
    }

    /// <summary>The global host functions a callback may call.</summary>
    private sealed class ButtonFunctions(IDeffileButtonServices services) : IHostCallable
    {
        public object? Invoke(string method, IReadOnlyList<object?> a)
        {
            string Arg(int i) => i < a.Count ? GdfValue.AsString(a[i]) : "";
            switch (method)
            {
                case "MessageBox": return services.MessageBox(Arg(0), Arg(1));
                case "ReadTextFile": return ReadInstallText(Arg(0));
                case "GetClipboardData":
                    lock (Clipboard)
                        return Clipboard.GetValueOrDefault(Arg(0), "");
                case "SetClipboardData":
                    lock (Clipboard)
                        Clipboard[Arg(0)] = Arg(1);
                    return null;
                case "ShowXModelInfo":
                    services.ShowXModelInfo(Arg(0));
                    return null;
                default:
                    return null;
            }
        }
    }

    /// <summary>The <c>asset</c> a callback is handed: reads see its writes, then the asset's values.</summary>
    private sealed class ButtonAsset(string name, Func<string, string> valueOf, Func<string, string> inheritedOf) : IHostCallable
    {
        public readonly Dictionary<string, string> Written = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Cleared = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Reads = new(StringComparer.OrdinalIgnoreCase);
        public string? ScrollTo;

        public string Get(string key)
        {
            if (Written.TryGetValue(key, out var v))
                return v;
            Reads.Add(key);
            return Cleared.Contains(key) ? inheritedOf(key) : valueOf(key);
        }

        public void Set(string key, string value)
        {
            if (key.Length == 0)
                return;
            if (value.Length > MaxText)
                throw new InvalidOperationException($"the script wrote more than {MaxText / (1024 * 1024)}M characters to {key}");
            Written[key] = value;
        }

        /// <summary>ClearSpecified: the key goes back to what it inherits; a later write in the same run sets it again.</summary>
        public void Clear(string key)
        {
            if (key.Length == 0)
                return;
            Written.Remove(key);
            Cleared.Add(key);
        }

        public object? Invoke(string method, IReadOnlyList<object?> a)
        {
            var key = a.Count > 0 ? GdfValue.AsString(a[0]) : "";
            switch (method)
            {
                case "GetEntryValue": return Get(key);
                case "GetEntryBool": return GdfValue.Box(GdfValue.AsBool(Get(key)));
                case "GetEntryInt": return GdfValue.Box(GdfValue.AsLong(Get(key)));
                case "GetEntryFloat": return GdfValue.AsDouble(Get(key));
                case "GetEntryControl":
                case "GetEntryVariable":
                    return new KeyHandle(this, key);
                case "GetName": return name;
                case "ScrollToEntry":
                    ScrollTo = key;
                    return null;
                default:
                    return null;
            }
        }
    }

    /// <summary><c>GetEntryControl</c> / <c>GetEntryVariable</c> over one key; display calls (Show, Enable…) are no-ops.</summary>
    private sealed class KeyHandle(ButtonAsset asset, string key) : IHostCallable
    {
        public object? Invoke(string method, IReadOnlyList<object?> a)
        {
            var arg = a.Count > 0 ? a[0] : null;
            switch (method)
            {
                case "GetValue": return asset.Get(key);
                case "GetBool": return GdfValue.Box(GdfValue.AsBool(asset.Get(key)));
                case "GetInt":
                case "ToInt":
                    return GdfValue.Box(GdfValue.AsLong(asset.Get(key)));
                case "GetFloat": return GdfValue.AsDouble(asset.Get(key));
                case "SetValue": asset.Set(key, GdfValue.AsString(arg)); return this;
                case "SetInt": asset.Set(key, GdfValue.AsString(GdfValue.Box(GdfValue.AsLong(arg)))); return this;
                case "SetBool": asset.Set(key, GdfValue.AsBool(arg) ? "1" : "0"); return this;
                case "SetFloat": asset.Set(key, GdfValue.FormatDouble(GdfValue.AsDouble(arg))); return this;
                case "ClearSpecified": asset.Clear(key); return this;
                case "IsValid":
                case "GetShow":
                case "GetEnable":
                case "GetSave":
                    return GdfValue.Box(true);
                default:
                    return this; // ForceSpecified, UpdateSavedValue, Show, Enable, SetValid…
            }
        }
    }
}
