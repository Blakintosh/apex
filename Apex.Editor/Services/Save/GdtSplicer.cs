using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Apex.Editor.Services.Gdt;
using GdtEncoding = Apex.Render.Data.Gdt.GdtEncoding;

namespace Apex.Editor.Services.Save;

/// <summary>The outcome of splicing a set of edits into a GDT's bytes.</summary>
public sealed class SpliceResult
{
    /// <summary>The new file, or null when there were conflicts or problems.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>True when <see cref="Bytes"/> differs from the original.</summary>
    public bool Changed { get; init; }

    public List<AssetConflict> Conflicts { get; } = new();

    /// <summary>Edits that can't be written at all (a value a GDT can't hold, a body Apex can't edit safely), in plain words.</summary>
    public List<string> Problems { get; } = new();

    /// <summary>The new file's layout (set with <see cref="Bytes"/>).</summary>
    public GdtLayout? Layout { get; init; }

    /// <summary>Every asset of the new file keyed by its body offset in the original (new assets are not in it).</summary>
    public Dictionary<long, GdtAssetSpan> Survivors { get; } = new();

    /// <summary>Where each written (changed or new) asset landed in the new file.</summary>
    public Dictionary<AssetEdit, GdtAssetSpan> Placed { get; } = new();
}

/// <summary>A splice produced a file that failed its own check. Always a bug in Apex; nothing is written.</summary>
public sealed class SpliceVerificationException(string message) : Exception(message);

/// <summary>
/// Splices edits into a GDT's bytes. Only the bytes of changed assets change: a changed value is replaced between its
/// quotes, a removed key loses its line, a new key gets a line in APE's order (see <see cref="InsertKeys"/>), a renamed
/// asset gets its new name between the header quotes, a deleted asset loses its block, and new assets are added at the
/// end of the file. Every other byte stays as it was. The result is checked before it is returned (see
/// <see cref="Verify"/>).
/// </summary>
public static class GdtSplicer
{
    /// <summary>The bytes of a new, empty GDT, as APE writes one.</summary>
    public static byte[] EmptyFile => "{\r\n}\r\n"u8.ToArray();

    /// <summary>True when <paramref name="file"/> holds nothing but its outer braces and whitespace: removing it loses nothing.</summary>
    public static bool IsEmptyShell(ReadOnlySpan<byte> file)
    {
        var seen = 0;
        foreach (var b in file)
        {
            if (b <= (byte)' ')
                continue;
            if (b != (seen == 0 ? (byte)'{' : (byte)'}') || ++seen > 2)
                return false;
        }
        return seen == 2;
    }

    /// <param name="sidecar">
    /// The file is a <c>.gdtx</c>: an asset name appears once per extension id, so a block is known by both, and a new
    /// block's header holds its extension id as is (a GDT's holds its type's <c>.gdf</c>).
    /// </param>
    public static SpliceResult Splice(byte[] original, IReadOnlyList<AssetEdit> edits, bool sidecar = false)
    {
        var layout = GdtLayout.Scan(original);
        var assets = layout.Assets;
        var problems = new List<string>();
        var conflicts = new List<AssetConflict>();

        foreach (var e in edits)
            Validate(e, problems);

        string Id(string name, string type) => sidecar ? name + "\0" + type : name;
        IComparer<string> order = sidecar ? ExtensionKeyComparer.Instance : ApeKeyComparer.Instance;
        string IdOf(GdtAssetSpan a) => Id(a.Name, a.IsDerived ? "" : a.TypeOrParent);

        var byOffset = new Dictionary<long, int>(assets.Count);
        var byName = new Dictionary<string, List<int>>(assets.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < assets.Count; i++)
        {
            byOffset[assets[i].BodyStart] = i;
            if (!byName.TryGetValue(IdOf(assets[i]), out var list))
                byName[IdOf(assets[i])] = list = new List<int>(1);
            list.Add(i);
        }

        // Which asset of this file each existing edit is about.
        var target = new Dictionary<int, AssetEdit>();
        foreach (var e in edits)
        {
            if (e.Disk is not { } disk)
                continue;
            int index;
            var id = Id(disk.Name, e.Type);
            if (byOffset.TryGetValue(disk.BodyOffset, out var at) && IdOf(assets[at]).Equals(id, StringComparison.OrdinalIgnoreCase))
                index = at;
            else if (byName.TryGetValue(id, out var named) && named.Count == 1)
                index = named[0];
            else
            {
                conflicts.Add(new AssetConflict(e, byName.ContainsKey(id) ? AssetConflictKind.Ambiguous : AssetConflictKind.MissingOnDisk,
                    Array.Empty<KeyConflict>()));
                continue;
            }
            if (!target.TryAdd(index, e))
                problems.Add($"{disk.Name} is part of this save twice.");
        }

        // Names after the save: a new or renamed asset may not take a name another asset in the file keeps.
        var finalNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Count(string name) => finalNames[name] = finalNames.GetValueOrDefault(name) + 1;
        for (var i = 0; i < assets.Count; i++)
        {
            if (target.TryGetValue(i, out var e))
            {
                if (!e.Delete)
                    Count(Id(e.Name, e.Type));
            }
            else
                Count(IdOf(assets[i]));
        }
        foreach (var e in edits)
            if (e.IsNew)
                Count(Id(e.Name, e.Type));
        foreach (var e in edits)
        {
            if (e.Delete)
                continue;
            var renamed = e.IsNew || (e.Disk is { } d && !d.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase));
            if (renamed && finalNames.GetValueOrDefault(Id(e.Name, e.Type)) > 1)
                conflicts.Add(new AssetConflict(e, AssetConflictKind.NameTaken, Array.Empty<KeyConflict>()));
        }

        var patches = new List<Patch>();
        foreach (var (index, e) in target.OrderBy(t => t.Key))
        {
            var a = assets[index];
            if (e.Delete)
            {
                if (e.Baseline is { } seen)
                {
                    var now = GdtParser.ParseBody(original.AsSpan(a.BodyStart, a.BodyLength));
                    if (!SameValues(now, seen))
                        conflicts.Add(new AssetConflict(e, AssetConflictKind.ChangedBeforeDelete, Differences(seen, now)));
                }
                patches.Add(new Patch(a.BlockStart, a.BlockEnd, Array.Empty<byte>()));
                continue;
            }
            SpliceHeader(original, a, e, patches, problems);
            SpliceBody(original, layout, a, e, patches, problems, conflicts, order, sidecar);
        }

        var added = edits.Where(e => e.IsNew).ToList();
        if (added.Count > 0)
        {
            if (layout.OuterClose < 0 || GdtLayout.LineStartIfClean(original, layout.OuterClose, out _) is not { } at)
                problems.Add("Apex can't add assets to this GDT: its closing brace doesn't sit on a line of its own.");
            else
            {
                var text = new StringBuilder();
                foreach (var e in added)
                    RenderAsset(text, e, layout, sidecar, order);
                patches.Add(new Patch(at, at, Bytes(text.ToString())));
            }
        }

        if (problems.Count > 0 || conflicts.Count > 0)
        {
            var failed = new SpliceResult();
            failed.Problems.AddRange(problems);
            failed.Conflicts.AddRange(conflicts);
            return failed;
        }

        var bytes = Apply(original, patches);
        var result = new SpliceResult { Bytes = bytes, Changed = !bytes.AsSpan().SequenceEqual(original), Layout = GdtLayout.Scan(bytes) };
        Verify(original, layout, result, target, added);
        return result;
    }

    // ── Header ──────────────────────────────────────────────────────────────

    private static void SpliceHeader(byte[] file, GdtAssetSpan a, AssetEdit e, List<Patch> patches, List<string> problems)
    {
        if (!string.Equals(e.Name, a.Name, StringComparison.Ordinal))
            patches.Add(new Patch(a.NameStart, a.NameEnd, Bytes(e.Name)));
        if ((e.Parent is not null) != a.IsDerived)
        {
            // Underive (or its undo): the header's ( "type.gdf" ) and [ "parent" ] swap, written as APE writes them.
            if (e.Parent is null && e.Type.Length == 0)
            {
                problems.Add($"{a.Name}: Apex doesn't know its type, so it can't write it as a root asset.");
                return;
            }
            var open = SkipBlanks(file, a.NameEnd + 1, a.BodyStart);
            var close = a.IsDerived ? (byte)']' : (byte)')';
            var end = a.TypeStart >= 0 ? a.TypeEnd + 1 : open + 1;
            while (end < a.BodyStart && file[end] != close)
                end++;
            if (open >= a.BodyStart || file[open] != (a.IsDerived ? (byte)'[' : (byte)'(') || end >= a.BodyStart)
            {
                problems.Add($"{a.Name}: its header isn't in a form Apex can edit safely. Edit it in a text editor or APE.");
                return;
            }
            var header = e.Parent is { } p ? $"[ \"{p}\" ]" : $"( \"{e.Type}.gdf\" )";
            patches.Add(new Patch(open, end + 1, Bytes(header)));
            return;
        }
        if (a.IsDerived && !string.Equals(e.Parent, a.TypeOrParent, StringComparison.Ordinal))
        {
            if (a.TypeStart < 0)
                problems.Add($"{a.Name}: its parent isn't quoted in the GDT, so Apex can't change it.");
            else
                patches.Add(new Patch(a.TypeStart, a.TypeEnd, Bytes(e.Parent!)));
        }
        else if (!a.IsDerived && e.Type.Length > 0 && !e.Type.Equals(a.TypeOrParent, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{a.Name}: Apex can't change an asset's type.");
    }

    // ── Body ────────────────────────────────────────────────────────────────

    private static void SpliceBody(byte[] file, GdtLayout layout, GdtAssetSpan a, AssetEdit e, List<Patch> patches,
        List<string> problems, List<AssetConflict> conflicts, IComparer<string> order, bool sidecar)
    {
        if (e.Set is not { Count: > 0 } set)
            return;
        var props = GdtLayout.ScanProps(file, a, out var regular);
        if (!regular || !a.Closed)
        {
            // Reading stops at such text, so a write can't know what follows it: refused rather than guessed at.
            problems.Add(sidecar
                ? $"{a.Name}'s {a.TypeOrParent} values in the .gdtx hold text that isn't a \"key\" \"value\" pair or a // comment, so Apex can't change them safely. Edit that block in a text editor."
                : $"{a.Name}: its text in the GDT isn't in a form Apex can edit safely. Edit it in a text editor or APE.");
            return;
        }

        // What the loader reads for each key: the last occurrence wins.
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in props)
            current[p.Key] = p.Value;

        var keyConflicts = new List<KeyConflict>();
        foreach (var (key, value) in set)
        {
            var onDisk = current.TryGetValue(key, out var d) ? d : null;
            if (e.Baseline is { } baseline)
            {
                var was = baseline.TryGetValue(key, out var b) ? b : null;
                if (onDisk != was && onDisk != value)
                    keyConflicts.Add(new KeyConflict(key, was, onDisk, value));
            }
        }
        if (keyConflicts.Count > 0)
            conflicts.Add(new AssetConflict(e, AssetConflictKind.ValuesChanged, keyConflicts));

        if (order is ExtensionKeyComparer && NeedsSorting(file, props, set, order))
        {
            SortBody(layout, props, current, set, patches, order);
            return;
        }

        var removed = new HashSet<int>();
        var inserts = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in set)
        {
            var any = false;
            for (var i = 0; i < props.Count; i++)
            {
                var p = props[i];
                if (!p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    continue;
                any = true;
                if (value is null)
                {
                    if (CommentInside(file, p))
                    {
                        problems.Add($"{a.Name}: Apex can't remove {p.Key} without the comment between it and its value. Move the comment off that line in a text editor.");
                        continue;
                    }
                    removed.Add(i);
                    patches.Add(p.LineHasOnlyThis
                        ? new Patch(p.LineStart, p.LineEnd, Array.Empty<byte>())
                        : new Patch(p.KeyQuote, SkipBlanks(file, p.ValueEnd + 1, a.BodyEnd), Array.Empty<byte>()));
                }
                else if (!string.Equals(p.Value, value, StringComparison.Ordinal))
                    patches.Add(new Patch(p.ValueStart, p.ValueEnd, Bytes(value)));
            }
            if (!any && value is not null)
                inserts.Add(new(key, value));
        }
        if (inserts.Count > 0)
            InsertKeys(file, layout, a, props, removed, inserts, patches, order);
    }

    /// <summary>
    /// A <c>.gdtx</c> block whose numbered keys an edit touches but whose lines aren't in key order (written by hand,
    /// or by an older Apex): splicing would leave wtKick10 before wtKick2, and weapon-tech reads numbered keys in file
    /// order. Only a block whose every key has a line of its own, each key once, the lines one after another, and whose
    /// other tables and keys are already in order, is rewritten; anything odder is spliced as it is (a rewrite keeps one
    /// line per key, so a key written twice would silently lose a line, a comment or blank line between keys would go,
    /// and a table the edit didn't touch would move).
    /// </summary>
    private static bool NeedsSorting(byte[] file, List<GdtPropSpan> props, IReadOnlyDictionary<string, string?> set, IComparer<string> order)
    {
        if (props.Count < 2 || !set.Keys.Any(k => k.Length > 0 && char.IsAsciiDigit(k[^1])) || props.Any(p => !p.LineHasOnlyThis))
            return false;
        if (props.Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != props.Count)
            return false;
        for (var i = 1; i < props.Count; i++)
            if (props[i].LineStart != props[i - 1].LineEnd)
                return false;
        if (props.Any(p => CommentInside(file, p)))
            return false;
        // Lines of tables the edit doesn't touch never move against each other: a table nobody edited stays as written.
        var touched = set.Keys.Select(Stem).Where(s => s is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = props.Where(p => Stem(p.Key) is not { } s || !touched.Contains(s)).ToList();
        for (var i = 1; i < others.Count; i++)
            if (order.Compare(others[i - 1].Key, others[i].Key) > 0)
                return false;
        for (var i = 1; i < props.Count; i++)
            if (order.Compare(props[i - 1].Key, props[i].Key) > 0)
                return true;
        return false;
    }

    /// <summary>A numbered key's stem (wtKick for wtKick12, wtKick01), or null for a key not ending in digits.</summary>
    private static string? Stem(string key)
    {
        var end = key.Length;
        while (end > 0 && char.IsAsciiDigit(key[end - 1]))
            end--;
        return end < key.Length && end > 0 ? key[..end] : null;
    }

    /// <summary>A <c>//</c> comment sits between the key and its value, so the pair's bytes hold text that isn't the pair's.</summary>
    private static bool CommentInside(byte[] file, GdtPropSpan p)
    {
        // Between the key's closing quote and the value's opening one there is only whitespace and comments.
        var pair = file.AsSpan(p.KeyQuote + 1, p.ValueStart - 1 - (p.KeyQuote + 1));
        var close = pair.IndexOf(GdtParser.Quote);
        return close >= 0 && pair[(close + 1)..].Contains(GdtParser.Slash);
    }

    /// <summary>The block's lines, edited, in key order, in the place and indentation of the lines they replace.</summary>
    private static void SortBody(GdtLayout layout, List<GdtPropSpan> props, Dictionary<string, string> current,
        IReadOnlyDictionary<string, string?> set, List<Patch> patches, IComparer<string> order)
    {
        var values = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in set)
            if (value is null)
                values.Remove(key);
            else
                values[key] = value;
        // Each key keeps its file spelling; a new one is spelled as the edit has it.
        var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in props)
            spelling.TryAdd(p.Key, p.Key);
        var eol = props[0].Eol.Length > 0 ? props[0].Eol : layout.Eol;
        var sb = new StringBuilder();
        foreach (var key in values.Keys.Select(k => spelling.GetValueOrDefault(k, k)).OrderBy(k => k, order))
            sb.Append(props[0].Indent).Append(Line(key, values[key])).Append(eol);
        patches.Add(new Patch(props[0].LineStart, props[^1].LineEnd, Bytes(sb.ToString())));
    }

    /// <summary>
    /// New keys go where APE would put them. APE writes an asset's keys sorted case-insensitively (every APE-written
    /// asset in the corpus is), so in a sorted body a new key goes in its sorted place. A body that isn't sorted wasn't
    /// written by APE; there the new keys go after the last one, rather than reordering anything. A <c>.gdtx</c> is
    /// sorted by <see cref="ExtensionKeyComparer"/> instead.
    /// </summary>
    private static void InsertKeys(byte[] file, GdtLayout layout, GdtAssetSpan a, List<GdtPropSpan> props, HashSet<int> removed,
        List<KeyValuePair<string, string>> inserts, List<Patch> patches, IComparer<string> order)
    {
        inserts.Sort((x, y) => order.Compare(x.Key, y.Key));
        var kept = Enumerable.Range(0, props.Count).Where(i => !removed.Contains(i)).Select(i => props[i]).ToList();

        if (kept.Count == 0)
        {
            // Empty body: the lines go right after the opening brace's line.
            var eol = layout.Eol;
            var sb = new StringBuilder();
            var at = GdtLayout.EndOfLineAfter(file, a.BodyStart);
            if (at == a.BodyStart)
                sb.Append(eol);
            foreach (var (k, v) in inserts)
                sb.Append(layout.PropIndent).Append(Line(k, v)).Append(eol);
            if (at == a.BodyStart)
                sb.Append(layout.CloseIndent);
            patches.Add(new Patch(at, at, Bytes(sb.ToString())));
            return;
        }

        var sorted = true;
        for (var i = 1; i < kept.Count && sorted; i++)
            sorted = order.Compare(kept[i - 1].Key, kept[i].Key) <= 0;

        var last = kept[^1];
        foreach (var (k, v) in inserts)
        {
            var before = sorted ? kept.FirstOrDefault(p => order.Compare(p.Key, k) > 0) : default;
            if (before.Key is not null && before.LineHasOnlyThis)
                patches.Add(new Patch(before.LineStart, before.LineStart, Bytes(before.Indent + Line(k, v) + before.Eol)));
            else if (before.Key is not null)
                patches.Add(new Patch(before.KeyQuote, before.KeyQuote, Bytes(Line(k, v) + " ")));
            else if (last.Eol.Length > 0)
                patches.Add(new Patch(last.LineEnd, last.LineEnd,
                    Bytes((last.LineHasOnlyThis ? last.Indent : layout.PropIndent) + Line(k, v) + last.Eol)));
            else
                patches.Add(new Patch(last.ValueEnd + 1, last.ValueEnd + 1, Bytes(layout.Eol + layout.PropIndent + Line(k, v))));
        }
    }

    private static string Line(string key, string value) => $"\"{key}\" \"{value}\"";

    private static int SkipBlanks(byte[] s, int from, int limit)
    {
        while (from < limit && s[from] is (byte)' ' or (byte)'\t')
            from++;
        return from;
    }

    // ── New assets ──────────────────────────────────────────────────────────

    /// <summary>A new asset in the file's own indentation and line ending, keys in APE's order.</summary>
    private static void RenderAsset(StringBuilder sb, AssetEdit e, GdtLayout layout, bool sidecar, IComparer<string> order)
    {
        var eol = layout.Eol;
        sb.Append(layout.AssetIndent).Append('"').Append(e.Name).Append('"');
        if (e.Parent is not null)
            sb.Append(" [ \"").Append(e.Parent).Append("\" ]");
        else
            sb.Append(" ( \"").Append(e.Type).Append(sidecar ? "\" )" : ".gdf\" )");
        sb.Append(eol).Append(layout.BraceIndent).Append('{').Append(eol);
        if (e.Full is { } full)
            foreach (var key in full.Keys.OrderBy(k => k, order))
                sb.Append(layout.PropIndent).Append(Line(key, full[key])).Append(eol);
        sb.Append(layout.CloseIndent).Append('}').Append(eol);
    }

    // ── Values a GDT can hold ───────────────────────────────────────────────

    /// <summary>
    /// A GDT string runs from one double quote to the next with no escapes, so a value can't hold a double quote or a
    /// line break (APE's own files never have one). U+FFFD means Apex couldn't read the original bytes, so writing it back
    /// would change them. A character the file's code page has no byte for (see <see cref="GdtEncoding"/>) would reach
    /// APE as something else. All of these are refused rather than silently altered.
    /// </summary>
    public static string? Problem(string what, string text, bool allowEmpty)
    {
        if (!allowEmpty && text.Length == 0)
            return $"{what} is empty.";
        foreach (var c in text)
        {
            if (c == '"')
                return $"{what} contains a double quote, which a GDT can't hold.";
            if (c is '\r' or '\n')
                return $"{what} contains a line break, which a GDT can't hold.";
            if (c == '\0')
                return $"{what} contains a null character.";
            if (c == '�')
                return $"{what} contains characters Apex couldn't read from the file, so it can't write them back exactly.";
        }
        if (!GdtEncoding.CanHold(text, out var bad))
            return $"{what} contains “{bad}”, which a GDT can't hold ({GdtEncoding.File.WebName}, as APE writes them).";
        return null;
    }

    private static void Validate(AssetEdit e, List<string> problems)
    {
        void Check(string what, string text, bool allowEmpty)
        {
            if (Problem(what, text, allowEmpty) is { } p)
                problems.Add(p);
        }
        if (e.Delete)
            return;
        Check($"The name of “{e.Name}”", e.Name, allowEmpty: false);
        if (e.Parent is { } parent)
            Check($"{e.Name}'s parent", parent, allowEmpty: false);
        else if (e.IsNew)
            Check($"{e.Name}'s type", e.Type, allowEmpty: false);
        IEnumerable<KeyValuePair<string, string?>> values = e.Set ?? (IEnumerable<KeyValuePair<string, string?>>?)e.Full?.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)) ?? Array.Empty<KeyValuePair<string, string?>>();
        foreach (var (key, value) in values)
        {
            Check($"A key of {e.Name}", key, allowEmpty: false);
            if (value is not null)
                Check($"{e.Name} · {key}", value, allowEmpty: true);
        }
    }

    // ── Applying and checking ───────────────────────────────────────────────

    private readonly record struct Patch(int Start, int End, byte[] Text);

    /// <summary>
    /// Text in the file's code page. Text it can't hold was already reported by <see cref="Validate"/>, which fails the
    /// whole splice, so its patch is never written.
    /// </summary>
    private static byte[] Bytes(string text) => GdtEncoding.CanHold(text, out _) ? GdtEncoding.GetBytes(text) : Array.Empty<byte>();

    private static byte[] Apply(byte[] original, List<Patch> patches)
    {
        // Insertions sort before a removal starting at the same byte; otherwise patches keep the order they were made in.
        var ordered = patches.Select((p, i) => (p, i))
            .OrderBy(t => t.p.Start).ThenBy(t => t.p.End > t.p.Start ? 1 : 0).ThenBy(t => t.i)
            .Select(t => t.p).ToList();
        var size = original.Length;
        foreach (var p in ordered)
            size += p.Text.Length - (p.End - p.Start);
        var output = new byte[size];
        var src = 0;
        var dst = 0;
        foreach (var p in ordered)
        {
            if (p.Start < src)
                throw new SpliceVerificationException("Two changes overlap in the file.");
            original.AsSpan(src, p.Start - src).CopyTo(output.AsSpan(dst));
            dst += p.Start - src;
            p.Text.CopyTo(output.AsSpan(dst));
            dst += p.Text.Length;
            src = p.End;
        }
        original.AsSpan(src).CopyTo(output.AsSpan(dst));
        return output;
    }

    /// <summary>
    /// Proves the new file says what was asked and nothing else: the same assets in the same order (deleted ones gone,
    /// new ones at the end), every untouched asset byte-identical, every written asset reading back as the original
    /// with exactly its changes applied (or, if new, as its values), and every byte between assets unchanged.
    /// </summary>
    private static void Verify(byte[] original, GdtLayout before, SpliceResult result, Dictionary<int, AssetEdit> target, List<AssetEdit> added)
    {
        var bytes = result.Bytes!;
        var after = result.Layout!;
        var expected = new List<(int Old, AssetEdit? Edit)>();
        for (var i = 0; i < before.Assets.Count; i++)
        {
            target.TryGetValue(i, out var e);
            if (e is { Delete: true })
                continue;
            expected.Add((i, e));
        }
        foreach (var e in added)
            expected.Add((-1, e));
        if (expected.Count != after.Assets.Count)
            throw new SpliceVerificationException($"The new file has {after.Assets.Count} assets where {expected.Count} were expected.");

        for (var j = 0; j < expected.Count; j++)
        {
            var (old, e) = expected[j];
            var now = after.Assets[j];
            if (old >= 0)
            {
                var was = before.Assets[old];
                result.Survivors[was.BodyStart] = now;
                if (e is null)
                {
                    if (!original.AsSpan(was.BlockStart, was.BlockEnd - was.BlockStart).SequenceEqual(bytes.AsSpan(now.BlockStart, now.BlockEnd - now.BlockStart)))
                        throw new SpliceVerificationException($"{was.Name} changed although it wasn't edited.");
                    continue;
                }
                var derived = e.Parent is not null;
                var header = derived ? now.TypeOrParent == e.Parent
                    : was.IsDerived ? now.TypeOrParent.Equals(e.Type, StringComparison.OrdinalIgnoreCase)
                    : now.TypeOrParent == was.TypeOrParent;
                Expect(now.Name == e.Name && now.IsDerived == derived && header && now.Closed, $"{e.Name}'s header didn't come out as expected.");
                var want = GdtParser.ParseBody(original.AsSpan(was.BodyStart, was.BodyLength));
                foreach (var (k, v) in e.Set ?? new Dictionary<string, string?>())
                {
                    if (v is null) want.Remove(k);
                    else want[k] = v;
                }
                Expect(SameValues(GdtParser.ParseBody(bytes.AsSpan(now.BodyStart, now.BodyLength)), want), $"{e.Name} doesn't read back with its changes.");
                result.Placed[e] = now;
            }
            else
            {
                Expect(now.Name == e!.Name && now.IsDerived == (e.Parent is not null) && now.Closed
                       && (e.Parent is not null ? now.TypeOrParent == e.Parent : now.TypeOrParent.Equals(e.Type, StringComparison.OrdinalIgnoreCase)),
                    $"New asset {e.Name}'s header didn't come out as expected.");
                var want = new Dictionary<string, string>(e.Full ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                Expect(SameValues(GdtParser.ParseBody(bytes.AsSpan(now.BodyStart, now.BodyLength)), want), $"New asset {e.Name} doesn't read back with its values.");
                result.Placed[e] = now;
            }
        }

        Expect(Frame(original, before).SequenceEqual(Frame(bytes, after)), "Text between assets changed.");

        static void Expect(bool ok, string what)
        {
            if (!ok)
                throw new SpliceVerificationException(what);
        }
    }

    /// <summary>The file with every asset block cut out: what must never change.</summary>
    private static byte[] Frame(byte[] file, GdtLayout layout)
    {
        var frame = new List<byte>();
        var at = 0;
        foreach (var a in layout.Assets)
        {
            frame.AddRange(file.AsSpan(at, a.BlockStart - at).ToArray());
            at = a.BlockEnd;
        }
        frame.AddRange(file.AsSpan(at).ToArray());
        return frame.ToArray();
    }

    public static bool SameValues(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count)
            return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var w) || !string.Equals(v, w, StringComparison.Ordinal))
                return false;
        return true;
    }

    private static List<KeyConflict> Differences(IReadOnlyDictionary<string, string> was, IReadOnlyDictionary<string, string> now)
    {
        var list = new List<KeyConflict>();
        foreach (var (k, v) in was)
            if (!now.TryGetValue(k, out var n) || n != v)
                list.Add(new KeyConflict(k, v, now.GetValueOrDefault(k), null));
        foreach (var (k, n) in now)
            if (!was.ContainsKey(k))
                list.Add(new KeyConflict(k, null, n, null));
        return list;
    }
}
