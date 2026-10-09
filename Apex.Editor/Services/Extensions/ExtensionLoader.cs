using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Extensions;

/// <summary>
/// Reads <c>&lt;dir&gt;\&lt;id&gt;\extension.json</c> manifests. Tolerant by design: a manifest that can't be read, isn't
/// JSON or is for another <c>apexSchema</c> leaves out that extension alone; a bad field or section leaves out that
/// part; an unknown member is noted and ignored, so a manifest written for a newer Apex still loads what this one
/// knows. Never throws.
/// </summary>
public static partial class ExtensionLoader
{
    public const string FileName = "extension.json";

    /// <summary>The manifest format this Apex reads. Additions stay within a version; only a change that would make
    /// an older Apex misread a manifest bumps it.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Larger than any real manifest by far: a file this big is something else.</summary>
    private const int MaxBytes = 1 << 20;

    public const int MaxVersionLength = 32;

    private static readonly string[] Kinds = { "number", "toggle", "choice", "text", "assetRef", "anim" };

    // The id is written into the .gdtx as a block's type ( "weapon-tech" ); a key is a GDT key.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex KeyPattern();

    /// <summary>
    /// Manifest text as Apex shows it (a version, title, label, description, message): without control characters (a
    /// line break only where <paramref name="lines"/> allows one) and without format characters such as the bidi
    /// overrides, which can make text read as something it isn't. Values (defaults, choices, prefixes) are never
    /// passed through this: they stay raw.
    /// </summary>
    public static string Displayable(string text, bool lines = false)
    {
        if (!text.Any(c => Hidden(c, lines)))
            return text;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
            if (!Hidden(c, lines))
                sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// The first <paramref name="max"/> lines, then how many more: one manifest can make thousands of notes, and a
    /// tooltip holding all of them is unreadable and slow to lay out.
    /// </summary>
    public static string Listed(IReadOnlyList<string> lines, int max = 100, string separator = "\n") =>
        lines.Count <= max ? string.Join(separator, lines)
            : string.Join(separator, lines.Take(max)) + separator + $"…and {lines.Count - max:N0} more.";

    private static bool Hidden(char c, bool lines) =>
        char.IsControl(c) ? !(lines && c == '\n') : char.GetUnicodeCategory(c) == UnicodeCategory.Format;

    /// <summary>Every manifest under <paramref name="dir"/> (one folder per extension), in id order.</summary>
    public static (List<ExtensionManifest> Manifests, List<ExtensionDiagnostic> Diagnostics) LoadAll(string dir)
    {
        var manifests = new List<ExtensionManifest>();
        var diagnostics = new List<ExtensionDiagnostic>();
        List<string> folders;
        try
        {
            if (!Directory.Exists(dir))
                return (manifests, diagnostics);
            folders = Directory.EnumerateDirectories(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new(Path.GetFileName(dir), ExtensionProblem.Disabled, $"Apex couldn't list the extensions folder ({ex.Message})."));
            return (manifests, diagnostics);
        }

        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
                continue;
            bool link;
            try
            {
                link = IsLink(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                link = false; // Reading the manifest says what's wrong.
            }
            if (link)
            {
                // A link could point anywhere, and could be pointed elsewhere after the user was asked about its module.
                diagnostics.Add(new(Path.GetFileName(folder), ExtensionProblem.Disabled,
                    "its folder is a junction or symbolic link, which Apex doesn't follow for extensions, so it was left out. Copy the folder in instead."));
                continue;
            }
            if (Read(path, diagnostics) is not { } m)
                continue;
            if (manifests.Any(x => x.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new(m.Id, ExtensionProblem.Disabled,
                    $"{Path.GetFileName(folder)}\\{FileName} has the same id as another extension, so it was left out."));
                continue;
            }
            manifests.Add(m);
        }
        manifests.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return (manifests, diagnostics);
    }

    /// <summary>One manifest, or null (and why, in <paramref name="diagnostics"/>) when the extension is left out.</summary>
    public static ExtensionManifest? Read(string path, List<ExtensionDiagnostic> diagnostics)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? path;
        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > MaxBytes)
            {
                diagnostics.Add(new(folder, ExtensionProblem.Disabled, $"its {FileName} is over 1 MB, so it was left out."));
                return null;
            }
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new(folder, ExtensionProblem.Disabled, $"Apex couldn't read its {FileName} ({ex.Message})."));
            return null;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 16,
            });
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : "";
            diagnostics.Add(new(folder, ExtensionProblem.Disabled, $"its {FileName} isn't valid JSON{where}, so it was left out."));
            return null;
        }
        using (doc)
            return new Reader(folder, path, diagnostics).Manifest(doc.RootElement);
    }

    private sealed class Reader(string folder, string path, List<ExtensionDiagnostic> diagnostics)
    {
        private readonly string _folder = folder;
        private string _source = folder;
        private readonly List<string> _notes = new();

        private void Note(string message)
        {
            _notes.Add(message);
            diagnostics.Add(new(_source, ExtensionProblem.Note, message));
        }

        private void Skip(string message)
        {
            _notes.Add(message);
            diagnostics.Add(new(_source, ExtensionProblem.Skipped, message));
        }

        private ExtensionManifest? Disable(string message)
        {
            diagnostics.Add(new(_source, ExtensionProblem.Disabled, message + " The extension was left out."));
            return null;
        }

        public ExtensionManifest? Manifest(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                return Disable($"its {FileName} isn't a JSON object.");

            if (!root.TryGetProperty("apexSchema", out var schema))
                return Disable($"its {FileName} has no apexSchema (this Apex reads apexSchema {SchemaVersion}).");
            if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != SchemaVersion)
                return Disable($"it is written for apexSchema {schema.GetRawText()}; this Apex reads apexSchema {SchemaVersion}.");

            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                || idElement.GetString() is not { } id || !IdPattern().IsMatch(id))
                return Disable("its id is missing or isn't a plain name (letters, digits, '-', '_' or '.').");
            _source = id;
            Repeats(root, "its manifest");
            if (!id.Equals(_folder, StringComparison.OrdinalIgnoreCase))
                Note($"its folder is named {_folder}, not {id}.");

            var manifestVersion = "";
            if (!root.TryGetProperty("version", out var versionElement))
                Note("it has no version.");
            else if (versionElement.ValueKind == JsonValueKind.String)
            {
                // Shown in the question before its module loads: one short line, nothing that could pass for Apex's words.
                manifestVersion = Displayable(versionElement.GetString()!).Trim();
                if (manifestVersion.Length > MaxVersionLength)
                {
                    manifestVersion = manifestVersion[..MaxVersionLength];
                    Note($"its version is longer than {MaxVersionLength} characters, so only the first {MaxVersionLength} are shown.");
                }
            }
            else
                Note("its version isn't a string, so it was ignored.");

            var targets = new List<string>();
            if (root.TryGetProperty("targets", out var targetsElement) && targetsElement.ValueKind == JsonValueKind.Array)
                foreach (var t in targetsElement.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } type)
                        targets.Add(type);
                    else
                        Note($"a target ({t.GetRawText()}) isn't a type name, so it was ignored.");
            if (targets.Count == 0)
                return Disable("it targets no asset type.");

            string? enabledBy = null;
            if (root.TryGetProperty("enabledBy", out var enabledElement) && enabledElement.ValueKind != JsonValueKind.Null)
            {
                if (enabledElement.ValueKind != JsonValueKind.String || enabledElement.GetString() is not { } flag || !KeyPattern().IsMatch(flag))
                    return Disable($"its enabledBy ({enabledElement.GetRawText()}) isn't a key name.");
                enabledBy = flag;
            }

            if (!root.TryGetProperty("sections", out var sectionsElement) || sectionsElement.ValueKind != JsonValueKind.Array)
                return Disable("it has no sections list.");

            foreach (var member in root.EnumerateObject())
                if (member.Name is not ("apexSchema" or "id" or "version" or "targets" or "enabledBy" or "sections" or "simulator"
                    or "title" or "offNotice" or "notice" or "export" or "consumer"))
                    Note($"Apex doesn't know the manifest member '{member.Name}', so it was ignored.");

            SimulatorModuleRef? simulator = null;
            if (root.TryGetProperty("simulator", out var simulatorElement) && simulatorElement.ValueKind != JsonValueKind.Null)
                simulator = Simulator(simulatorElement);

            var title = Line(root, "title", "its title");
            var offNotice = Line(root, "offNotice", "its offNotice");
            if (offNotice is not null && enabledBy is null)
            {
                Note("its offNotice needs an enabledBy key to say it is off, so it was ignored.");
                offNotice = null;
            }
            var notice = Line(root, "notice", "its notice");
            ExtensionConsumer? consumer = null;
            if (root.TryGetProperty("consumer", out var consumerElement) && consumerElement.ValueKind != JsonValueKind.Null)
                consumer = Consumer(consumerElement);
            ExtensionExport? export = null;
            if (root.TryGetProperty("export", out var exportElement) && exportElement.ValueKind != JsonValueKind.Null)
                export = Export(exportElement);

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sections = new List<ExtensionSection>();
            foreach (var s in sectionsElement.EnumerateArray())
                if (Section(s, id, keys) is { } section)
                    sections.Add(section);

            if (enabledBy is not null && !keys.Contains(enabledBy))
                sections = WithSwitch(sections, id, enabledBy);

            foreach (var section in sections)
            {
                CheckReads(section.Rule, $"section {section.Title}'s visibleWhen", keys);
                foreach (var f in section.Fields)
                    CheckReads(f.Rule, $"{f.Def.Key}'s visibleWhen", keys);
                foreach (var r in section.Records)
                    CheckReads(r.Rule, $"{r.Def.Key}'s visibleWhen", keys);
            }

            return new ExtensionManifest
            {
                Id = id,
                Version = manifestVersion,
                Title = title ?? "",
                OffNotice = offNotice,
                Notice = notice,
                Export = export,
                Consumer = consumer,
                Path = path,
                Targets = targets,
                EnabledBy = enabledBy,
                Sections = sections,
                Notes = _notes,
                Simulator = simulator,
            };
        }

        /// <summary>One line of manifest text Apex shows (a title, a notice): a string, shown without control characters.</summary>
        private string? Line(JsonElement e, string name, string what)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
                return null;
            if (v.ValueKind == JsonValueKind.String && Displayable(v.GetString()!).Trim() is { Length: > 0 } text)
                return text;
            Note($"{what} isn't a line of text, so it was ignored.");
            return null;
        }

        /// <summary>
        /// The <c>export</c> object: <c>format</c> (only <c>ini-section</c>), <c>header</c> (<c>{asset}</c> is the asset's
        /// name) and <c>command</c> (what the palette and the button call it). Anything missing leaves the export out.
        /// </summary>

        private ExtensionConsumer? Consumer(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                Skip($"its consumer ({Short(e)}) isn't an object, so it was left out.");
                return null;
            }
            Repeats(e, "its consumer");
            foreach (var member in e.EnumerateObject())
                if (member.Name is not ("file" or "contains"))
                    Note($"Apex doesn't know the consumer member '{member.Name}', so it was ignored.");
            if (String(e, "file", "its consumer") is not { } file || ExtensionConsumerProbe.Resolve(System.IO.Path.GetTempPath(), file) is null)
            {
                Skip("its consumer's file isn't a path inside the install (relative, no .., no drive), so the consumer was left out.");
                return null;
            }
            if (String(e, "contains", "its consumer") is not { Length: > 0 } contains || contains.Length > ExtensionConsumerProbe.MaxTextLength
                || contains.Any(c => char.IsControl(c) || c > 126))
            {
                Skip("its consumer's contains isn't a short plain ASCII text to look for in that file, so the consumer was left out.");
                return null;
            }
            return new ExtensionConsumer(file, contains);
        }

        private ExtensionExport? Export(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                Skip($"its export ({Short(e)}) isn't an object, so it was left out.");
                return null;
            }
            Repeats(e, "its export");
            foreach (var member in e.EnumerateObject())
                if (member.Name is not ("format" or "header" or "command"))
                    Note($"Apex doesn't know the export member '{member.Name}', so it was ignored.");
            var format = String(e, "format", "its export");
            if (format != ExtensionExport.IniSection)
            {
                Skip($"its export's format ({format ?? "none"}) isn't {ExtensionExport.IniSection}, so the export was left out.");
                return null;
            }
            // The header is written into a file the extension's own tools read: raw, but on one line.
            if (String(e, "header", "its export") is not { } header || header.Any(char.IsControl) || header.Trim().Length == 0)
            {
                Skip("its export has no header (one line, such as [weapon:{asset}]), so the export was left out.");
                return null;
            }
            if (Line(e, "command", "its export's command") is not { } command)
            {
                Skip("its export has no command (what Apex calls it), so the export was left out.");
                return null;
            }
            return new ExtensionExport(format, header, command);
        }

        /// <summary>
        /// The <c>simulator</c> object. A bad path leaves out the module only (the fields still load); a module that isn't
        /// there yet is a note, since only a preview needs it and it says so then. A stat, never an open: nothing here
        /// reads or hashes the file.
        /// </summary>
        private SimulatorModuleRef? Simulator(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                Skip($"its simulator ({Short(e)}) isn't an object, so the preview module was left out.");
                return null;
            }
            Repeats(e, "its simulator");
            foreach (var member in e.EnumerateObject())
                if (member.Name != "module")
                    Note($"Apex doesn't know the simulator member '{member.Name}', so it was ignored.");
            if (!e.TryGetProperty("module", out var m) || m.ValueKind != JsonValueKind.String || m.GetString() is not { } module)
            {
                Skip("its simulator has no module path, so the preview module was left out.");
                return null;
            }
            var folder = Path.GetDirectoryName(path)!;
            if (CheckModulePath(folder, module) is { } problem)
            {
                Skip($"its simulator module {module} {problem}, so the preview module was left out.");
                return null;
            }
            var full = Path.GetFullPath(Path.Combine(folder, module));
            if (!File.Exists(full))
                Note($"its simulator module {module} isn't there; a preview that asks for it will say so.");
            return new SimulatorModuleRef(module, full, folder);
        }

        /// <summary>
        /// A manifest whose enabledBy key has no field of its own gets one: the switch has to be somewhere the user can
        /// reach it, and it leads the extension's first section.
        /// </summary>
        private static List<ExtensionSection> WithSwitch(List<ExtensionSection> sections, string id, string key)
        {
            var title = sections.Count > 0 ? sections[0].Title : id;
            var toggle = new ExtensionField
            {
                Def = new PropertyDef(key, "Enabled", title, PropertyKind.Toggle,
                    $"Turns {id} on for this asset and the assets based on it. Off hides its settings; their values are kept.")
                {
                    Default = "0",
                    Extension = id,
                },
            };
            if (sections.Count == 0)
                return [new ExtensionSection { Title = title, Fields = [toggle] }];
            var first = sections[0];
            sections[0] = new ExtensionSection
            {
                Title = first.Title, Rule = first.Rule, Fields = [toggle, .. first.Fields], Records = first.Records,
                CollapsedUnlessSet = first.CollapsedUnlessSet, Notice = first.Notice,
            };
            return sections;
        }

        private void CheckReads(VisibleWhen? rule, string what, HashSet<string> keys)
        {
            if (rule is null)
                return;
            foreach (var key in rule.Keys)
                if (!keys.Contains(key) && OwnerList(key) is null)
                    Note($"{what} reads {key}, which isn't one of its fields, so it reads as empty.");
        }

        // The record lists read so far, by stem: a flat key that is one of their numbered keys clashes with them. Sets,
        // not scans: a 1 MB manifest holds some 15,000 lists, and comparing each with every other took seconds.
        private readonly Dictionary<string, ExtensionRecordList> _lists = new(StringComparer.OrdinalIgnoreCase);

        // Every name some list's stem or flat field's key is a numbered key of ("wtA" for a list wtA1#, a field wtA12).
        private readonly HashSet<string> _listStemsNumbered = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _keysNumbered = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Each shorter name that <paramref name="name"/> is a numbered key of: wtA12 gives wtA1 and wtA.</summary>
        private static IEnumerable<string> Unnumbered(string name)
        {
            for (var i = name.Length - 1; i > 0 && char.IsAsciiDigit(name[i]); i--)
                yield return name[..i];
        }

        private ExtensionRecordList? OwnerList(string key)
        {
            foreach (var stem in Unnumbered(key))
                if (_lists.TryGetValue(stem, out var list) && list.NumberOf(key) is not null)
                    return list;
            return null;
        }

        /// <summary>JSON allows a member twice and Apex reads the last: say so rather than pick one silently.</summary>
        private void Repeats(JsonElement e, string where)
        {
            HashSet<string>? seen = null;
            foreach (var member in e.EnumerateObject())
                if (!(seen ??= new(StringComparer.Ordinal)).Add(member.Name))
                    Note($"{where} names '{member.Name}' more than once; Apex reads the last.");
        }

        private ExtensionSection? Section(JsonElement s, string id, HashSet<string> keys)
        {
            if (s.ValueKind != JsonValueKind.Object)
            {
                Skip($"a section ({Short(s)}) isn't an object, so it was left out.");
                return null;
            }
            if (!s.TryGetProperty("title", out var titleElement) || titleElement.ValueKind != JsonValueKind.String
                || titleElement.GetString() is not { } raw || Displayable(raw).Trim() is not { Length: > 0 } title)
            {
                Skip("a section has no title, so it was left out.");
                return null;
            }
            var rule = Rule(s, $"section {title}'s visibleWhen", "the section always shows");
            Repeats(s, $"section {title}");
            foreach (var member in s.EnumerateObject())
                if (member.Name is not ("title" or "visibleWhen" or "fields" or "records" or "collapsedUnlessSet" or "notice"))
                    Note($"section {title}: Apex doesn't know the member '{member.Name}', so it was ignored.");
            var collapsed = Bool(s, "collapsedUnlessSet", $"section {title}");
            var notice = Line(s, "notice", $"section {title}'s notice");

            var fields = new List<ExtensionField>();
            if (s.TryGetProperty("fields", out var fieldsElement))
            {
                if (fieldsElement.ValueKind != JsonValueKind.Array)
                    Skip($"section {title}'s fields isn't a list, so it has none.");
                else
                    foreach (var f in fieldsElement.EnumerateArray())
                        if (Field(f, id, title, keys) is { } field)
                            fields.Add(field);
            }
            var records = new List<ExtensionRecordList>();
            if (s.TryGetProperty("records", out var recordsElement))
            {
                if (recordsElement.ValueKind != JsonValueKind.Array)
                    Skip($"section {title}'s records isn't a list, so it has none.");
                else
                    foreach (var r in recordsElement.EnumerateArray())
                        if (RecordList(r, id, title, keys) is { } list)
                            records.Add(list);
            }
            foreach (var list in records)
                if (list.After is { } after && !fields.Any(f => f.Def.Key.Equals(after, StringComparison.OrdinalIgnoreCase)))
                    Note($"{list.Def.Key}'s after names {after}, which isn't a field of section {title}, so the table goes after its fields.");
            return new ExtensionSection
            {
                Title = title, Rule = rule, Fields = fields, Records = records, CollapsedUnlessSet = collapsed, Notice = notice,
            };
        }

        private static readonly string[] ColumnKinds = { "number", "toggle", "choice", "text", "anim" };

        /// <summary>A record list (<c>"key": "wtKick#"</c>): numbered keys, each holding one record of typed columns.</summary>
        private ExtensionRecordList? RecordList(JsonElement r, string id, string section, HashSet<string> keys)
        {
            if (r.ValueKind != JsonValueKind.Object)
            {
                Skip($"a record list in {section} ({Short(r)}) isn't an object, so it was left out.");
                return null;
            }
            if (!r.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String
                || keyElement.GetString() is not { } pattern || !pattern.EndsWith('#') || !KeyPattern().IsMatch(pattern[..^1]))
            {
                Skip($"a record list in {section} has no key, or one that isn't a key name ending in # (wtKick#), so it was left out.");
                return null;
            }
            var stem = pattern[..^1];
            // wtA# and wtA1# would both own wtA12.
            if (_lists.ContainsKey(stem) || Unnumbered(stem).Any(_lists.ContainsKey) || _listStemsNumbered.Contains(stem)
                || _keysNumbered.Contains(stem))
            {
                Skip($"{pattern}'s keys are also another field's or record list's, so it was left out.");
                return null;
            }
            Repeats(r, pattern);
            foreach (var member in r.EnumerateObject())
                if (member.Name is not ("key" or "label" or "description" or "max" or "columns" or "checks" or "unique" or "visibleWhen"
                    or "combine" or "after"))
                    Note($"{pattern}: Apex doesn't know the member '{member.Name}', so it was ignored.");
            var after = String(r, "after", pattern);

            int? max = null;
            if (r.TryGetProperty("max", out var maxElement) && maxElement.ValueKind != JsonValueKind.Null)
            {
                if (maxElement.ValueKind == JsonValueKind.Number && maxElement.TryGetInt32(out var m) && m > 0)
                    max = m;
                else
                    Note($"{pattern}'s max isn't a whole number above 0, so it has no row limit.");
            }

            var columns = new List<RecordColumn>();
            if (!r.TryGetProperty("columns", out var columnsElement) || columnsElement.ValueKind != JsonValueKind.Array)
            {
                Skip($"{pattern} has no columns list, so it was left out.");
                return null;
            }
            foreach (var c in columnsElement.EnumerateArray())
                if (Column(c, id, section, pattern, columns) is { } column)
                    columns.Add(column);
            if (columns.Count(c => !c.Named) == 0)
            {
                Skip($"{pattern} has no usable positional column, so it was left out.");
                return null;
            }

            var names = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var checks = new List<RecordCheck>();
            if (r.TryGetProperty("checks", out var checksElement))
            {
                if (checksElement.ValueKind != JsonValueKind.Array)
                    Note($"{pattern}'s checks isn't a list, so it has none.");
                else
                    foreach (var c in checksElement.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("when", out var when) || when.ValueKind != JsonValueKind.String
                            || !c.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String
                            || message.GetString() is not { Length: > 0 } text)
                        {
                            Skip($"a check of {pattern} ({Short(c)}) needs a when rule and a message, so it was left out.");
                            continue;
                        }
                        var rule = VisibleWhen.Parse(when.GetString()!, out var error);
                        if (rule is null)
                        {
                            Skip($"a check of {pattern} isn't valid ({error}), so it was left out.");
                            continue;
                        }
                        foreach (var read in rule.Keys.Where(k => !names.Contains(k)))
                            Note($"a check of {pattern} reads {read}, which isn't one of its columns, so it reads as empty.");
                        checks.Add(new RecordCheck(rule, Displayable(text, lines: true)));
                    }
            }

            var unique = new List<RecordColumn>();
            if (r.TryGetProperty("unique", out var uniqueElement))
            {
                if (uniqueElement.ValueKind != JsonValueKind.Array)
                    Note($"{pattern}'s unique isn't a list of column names, so it was ignored.");
                else
                    foreach (var u in uniqueElement.EnumerateArray())
                        if (u.ValueKind == JsonValueKind.String && columns.FirstOrDefault(c => c.Name.Equals(u.GetString(), StringComparison.OrdinalIgnoreCase)) is { } col)
                            unique.Add(col);
                        else
                            Note($"{pattern}'s unique names {Short(u)}, which isn't one of its columns, so it was ignored.");
            }

            var combines = new List<RecordCombine>();
            if (r.TryGetProperty("combine", out var combineElement))
            {
                if (combineElement.ValueKind != JsonValueKind.Array)
                    Note($"{pattern}'s combine isn't a list, so its columns show as they are.");
                else
                    foreach (var c in combineElement.EnumerateArray())
                        if (Combine(c, id, section, pattern, columns, combines) is { } combine)
                            combines.Add(combine);
            }

            var def = new PropertyDef(pattern, Label(r, pattern) ?? pattern, section, PropertyKind.Text, Description(r, pattern) ?? "")
            {
                Extension = id,
            };
            var list = new ExtensionRecordList
            {
                Def = def,
                Base = stem,
                MaxRows = max,
                Columns = columns,
                Checks = checks,
                Unique = unique,
                Combines = combines,
                Rule = Rule(r, $"{pattern}'s visibleWhen", "it always shows"),
                After = after,
            };
            _lists.Add(stem, list);
            _listStemsNumbered.UnionWith(Unnumbered(stem));
            return list;
        }

        /// <summary>
        /// A <c>combine</c> entry: <c>columns</c> (stored column names, none in another group), a <c>label</c>, a
        /// <c>description</c>, and <c>choices</c> of <c>{ "value": [one per column], "label": text }</c>. Anything wrong
        /// leaves the group out and its columns show as they are, so a mistake never hides a stored value.
        /// </summary>
        private RecordCombine? Combine(JsonElement c, string id, string section, string pattern, List<RecordColumn> columns, List<RecordCombine> taken)
        {
            if (c.ValueKind != JsonValueKind.Object)
            {
                Skip($"a combine of {pattern} ({Short(c)}) isn't an object, so its columns show as they are.");
                return null;
            }
            Repeats(c, $"a combine of {pattern}");
            foreach (var member in c.EnumerateObject())
                if (member.Name is not ("label" or "description" or "columns" or "choices"))
                    Note($"a combine of {pattern}: Apex doesn't know the member '{member.Name}', so it was ignored.");
            var indexes = new List<int>();
            if (c.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
                foreach (var n in cols.EnumerateArray())
                {
                    var at = n.ValueKind == JsonValueKind.String
                        ? columns.FindIndex(x => x.Name.Equals(n.GetString(), StringComparison.OrdinalIgnoreCase))
                        : -1;
                    if (at < 0 || indexes.Contains(at) || taken.Any(t => t.Columns.Contains(at)))
                    {
                        Skip($"a combine of {pattern} names {Short(n)}, which isn't one of its columns or is in another group, so its columns show as they are.");
                        return null;
                    }
                    if (columns[at].Hidden)
                    {
                        Skip($"a combine of {pattern} names {Short(n)}, a hidden column, so its columns show as they are.");
                        return null;
                    }
                    indexes.Add(at);
                }
            if (indexes.Count == 0)
            {
                Skip($"a combine of {pattern} names no columns, so it was left out.");
                return null;
            }
            var where = $"{pattern}'s combine of {string.Join(" and ", indexes.Select(i => columns[i].Name))}";
            if (Label(c, where) is not { } label)
            {
                Skip($"{where} has no label, so its columns show as they are.");
                return null;
            }
            var values = new List<string>();
            var labels = new List<string>();
            if (c.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Array
                        || v.GetArrayLength() != indexes.Count || Label(choice, where) is not { } choiceLabel
                        || v.EnumerateArray().Any(x => x.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)
                            || (x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()).Contains(',')))
                    {
                        Skip($"{where}: a choice ({Short(choice)}) needs a value with one entry per column (no commas) and a label, so its columns show as they are.");
                        return null;
                    }
                    var value = string.Join(",", v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()));
                    if (values.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        Note($"{where} lists the value [{value}] twice; the first label is used.");
                        continue;
                    }
                    values.Add(value);
                    labels.Add(choiceLabel);
                }
            if (values.Count == 0)
            {
                Skip($"{where} has no choices, so its columns show as they are.");
                return null;
            }
            // What empty columns mean, as one choice: shown as the dropdown's placeholder.
            var defaults = indexes.Select(i => columns[i].Def.Default).ToList();
            var def = new PropertyDef(string.Join("+", indexes.Select(i => columns[i].Name)), label, section, PropertyKind.Choice,
                Description(c, where) ?? "")
            {
                Extension = id,
                Choices = values.ToArray(),
                ChoiceLabels = labels.ToArray(),
                Default = defaults.All(d => d.Length > 0) ? string.Join(",", defaults) : "",
            };
            return new RecordCombine { Name = def.Key, Def = def, Columns = indexes };
        }

        private RecordColumn? Column(JsonElement c, string id, string section, string pattern, List<RecordColumn> columns)
        {
            if (c.ValueKind != JsonValueKind.Object)
            {
                Skip($"a column of {pattern} ({Short(c)}) isn't an object, so it was left out.");
                return null;
            }
            if (!c.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String
                || nameElement.GetString() is not { } name || !KeyPattern().IsMatch(name))
            {
                Skip($"a column of {pattern} has no name, or one that isn't a plain name, so it was left out.");
                return null;
            }
            var where = $"{pattern}.{name}";
            if (columns.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                Skip($"{where} is declared twice; the second one was left out.");
                return null;
            }
            if (!c.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String
                || Array.IndexOf(ColumnKinds, kindElement.GetString()) < 0)
            {
                Skip($"{where}'s kind isn't one of {string.Join(", ", ColumnKinds)}, so it was left out.");
                return null;
            }
            var kind = kindElement.GetString()!;
            Repeats(c, where);
            foreach (var member in c.EnumerateObject())
                if (member.Name is not ("name" or "kind" or "label" or "description" or "default" or "min" or "max" or "step"
                    or "integer" or "choices" or "prefix" or "named" or "optional" or "hidden"))
                    Note($"{where}: Apex doesn't know the member '{member.Name}', so it was ignored.");

            var def = new PropertyDef(name, Label(c, where) ?? name, section, kind switch
            {
                "number" => PropertyKind.Number,
                "toggle" => PropertyKind.Toggle,
                "choice" => PropertyKind.Choice,
                "anim" => PropertyKind.AssetRef,
                _ => PropertyKind.Text,
            }, Description(c, where) ?? "")
            {
                Extension = id,
            };
            if (Default(c, where, kind) is { } value)
                def = def with { Default = value };
            switch (kind)
            {
                case "number":
                    def = Number(c, where, def);
                    break;
                case "toggle" when def.Default.Length > 0 && def.Default is not ("0" or "1"):
                    Note($"{where}'s default ({def.Default}) isn't 0 or 1, so it has none.");
                    def = def with { Default = "" };
                    break;
                case "choice":
                    def = Choices(c, where, def);
                    break;
                case "anim":
                    def = def with { RefType = "xanim" };
                    break;
            }

            var prefix = String(c, "prefix", where) ?? "";
            if (prefix.Contains(','))
            {
                Note($"{where}'s prefix holds a comma, which would split the field, so it has none.");
                prefix = "";
            }
            var named = Bool(c, "named", where);
            if (named && prefix.Length == 0)
            {
                Skip($"{where} is named but has no prefix to be found by, so it was left out.");
                return null;
            }
            var optional = Bool(c, "optional", where);
            if (!named && !optional && columns.Any(x => x is { Optional: true, Named: false }))
                Note($"{where} isn't optional but follows an optional column, so it is optional too.");
            // A hidden column is written only as its default, so without one it would be an empty field nobody can fill.
            var hidden = Bool(c, "hidden", where);
            if (hidden && def.Default.Length == 0)
            {
                Note($"{where} is hidden but has no default to write, so it shows.");
                hidden = false;
            }
            return new RecordColumn
            {
                Name = name,
                Def = def,
                Prefix = prefix,
                Named = named,
                Optional = optional || named || columns.Any(x => x is { Optional: true, Named: false }),
                Hidden = hidden,
            };
        }

        private bool Bool(JsonElement e, string name, string where)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
                return false;
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return v.ValueKind == JsonValueKind.True;
            Note($"{where}: {name} isn't true or false, so it was ignored.");
            return false;
        }

        private ExtensionField? Field(JsonElement f, string id, string section, HashSet<string> keys)
        {
            if (f.ValueKind != JsonValueKind.Object)
            {
                Skip($"a field in {section} ({Short(f)}) isn't an object, so it was left out.");
                return null;
            }
            if (!f.TryGetProperty("key", out var keyElement) || keyElement.ValueKind != JsonValueKind.String
                || keyElement.GetString() is not { } key || !KeyPattern().IsMatch(key))
            {
                Skip($"a field in {section} has no key, or one that isn't a GDT key name, so it was left out.");
                return null;
            }
            if (!keys.Add(key))
            {
                Skip($"{key} is declared twice; the second one was left out.");
                return null;
            }
            if (OwnerList(key) is { } owner)
            {
                keys.Remove(key);
                Skip($"{key} is one of {owner.Def.Key}'s numbered keys, so its field was left out.");
                return null;
            }
            if (!f.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String
                || Array.IndexOf(Kinds, kindElement.GetString()) < 0)
            {
                keys.Remove(key);
                Skip($"{key}'s kind isn't one of {string.Join(", ", Kinds)}, so it was left out.");
                return null;
            }
            var kind = kindElement.GetString()!;

            Repeats(f, key);
            foreach (var member in f.EnumerateObject())
                if (member.Name is not ("key" or "kind" or "label" or "description" or "default" or "min" or "max" or "step"
                    or "integer" or "choices" or "refType" or "visibleWhen" or "parts"))
                    Note($"{key}: Apex doesn't know the member '{member.Name}', so it was ignored.");

            List<PropertyDef>? parts = null;
            if (f.TryGetProperty("parts", out var partsElement) && partsElement.ValueKind != JsonValueKind.Null)
            {
                if (kind != "text")
                    Note($"{key}: parts apply to text fields only, so they were ignored.");
                else
                    parts = Parts(partsElement, id, section, key);
            }

            var def = new PropertyDef(key, Label(f, key) ?? key, section, kind switch
            {
                "number" => PropertyKind.Number,
                "toggle" => PropertyKind.Toggle,
                "choice" => PropertyKind.Choice,
                "assetRef" or "anim" => PropertyKind.AssetRef,
                _ => PropertyKind.Text,
            }, Description(f, key) ?? "")
            {
                Extension = id,
            };

            var value = Default(f, key, kind);
            if (value is not null)
                def = def with { Default = value };

            switch (kind)
            {
                case "number":
                    def = Number(f, key, def);
                    break;
                case "toggle":
                    if (def.Default is not ("0" or "1"))
                    {
                        if (value is not null)
                            Note($"{key}'s default ({value}) isn't 0 or 1, so it is 0.");
                        def = def with { Default = "0" };
                    }
                    break;
                case "choice":
                    def = Choices(f, key, def);
                    break;
                case "assetRef":
                    if (String(f, "refType", key) is not { Length: > 0 } refType)
                    {
                        keys.Remove(key);
                        Skip($"{key} is an assetRef with no refType (the asset type it names), so it was left out.");
                        return null;
                    }
                    def = def with { RefType = refType };
                    break;
                case "anim":
                    def = def with { RefType = "xanim" };
                    break;
            }
            if (kind != "number")
                foreach (var numeric in new[] { "min", "max", "step", "integer" })
                    if (f.TryGetProperty(numeric, out _))
                        Note($"{key}: {numeric} applies to number fields only, so it was ignored.");
            if (kind != "choice" && f.TryGetProperty("choices", out _))
                Note($"{key}: choices apply to choice fields only, so they were ignored.");
            if (kind != "assetRef" && f.TryGetProperty("refType", out _))
                Note($"{key}: refType applies to assetRef fields only, so it was ignored.");

            _keysNumbered.UnionWith(Unnumbered(key));
            return new ExtensionField { Def = def, Rule = Rule(f, $"{key}'s visibleWhen", "it always shows"), Parts = parts };
        }

        private static readonly string[] PartKinds = { "number", "choice", "toggle", "text" };

        /// <summary>
        /// A text field's <c>parts</c>: what each comma-separated value of it is, in order. One bad part leaves out all of
        /// them (the field stays one text box): a part missing from the middle would put every later value under the
        /// wrong name.
        /// </summary>
        private List<PropertyDef>? Parts(JsonElement e, string id, string section, string key)
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() == 0)
            {
                Skip($"{key}'s parts isn't a list of parts, so it is edited as one value.");
                return null;
            }
            var parts = new List<PropertyDef>();
            foreach (var p in e.EnumerateArray())
            {
                var where = $"{key} part {parts.Count + 1}";
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String
                    || n.GetString() is not { } name || !KeyPattern().IsMatch(name)
                    || parts.Any(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    Skip($"{where} has no name, one that isn't a plain name, or one another part has, so {key} is edited as one value.");
                    return null;
                }
                where = $"{key}.{name}";
                if (!p.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String || Array.IndexOf(PartKinds, k.GetString()) < 0)
                {
                    Skip($"{where}'s kind isn't one of {string.Join(", ", PartKinds)}, so {key} is edited as one value.");
                    return null;
                }
                var kind = k.GetString()!;
                Repeats(p, where);
                foreach (var member in p.EnumerateObject())
                    if (member.Name is not ("name" or "kind" or "label" or "description" or "default" or "min" or "max" or "step"
                        or "integer" or "choices"))
                        Note($"{where}: Apex doesn't know the member '{member.Name}', so it was ignored.");
                var def = new PropertyDef(name, Label(p, where) ?? name, section, kind switch
                {
                    "number" => PropertyKind.Number,
                    "toggle" => PropertyKind.Toggle,
                    "choice" => PropertyKind.Choice,
                    _ => PropertyKind.Text,
                }, Description(p, where) ?? "")
                {
                    Extension = id,
                };
                if (Default(p, where, kind) is { } value)
                {
                    if (value.Contains(','))
                        Note($"{where}'s default holds a comma, which would split the value, so it has none.");
                    else
                        def = def with { Default = value };
                }
                switch (kind)
                {
                    case "number":
                        def = Number(p, where, def);
                        break;
                    case "toggle" when def.Default.Length > 0 && def.Default is not ("0" or "1"):
                        Note($"{where}'s default ({def.Default}) isn't 0 or 1, so it has none.");
                        def = def with { Default = "" };
                        break;
                    case "choice":
                        def = Choices(p, where, def);
                        break;
                }
                parts.Add(def);
            }
            return parts;
        }

        private PropertyDef Number(JsonElement f, string key, PropertyDef def)
        {
            var min = Double(f, "min", key);
            var max = Double(f, "max", key);
            var step = Double(f, "step", key);
            var integer = f.TryGetProperty("integer", out var i) && i.ValueKind == JsonValueKind.True;
            if (f.TryGetProperty("integer", out var i2) && i2.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                Note($"{key}: integer isn't true or false, so it was ignored.");
            if (min is { } lo && max is { } hi)
            {
                if (hi > lo)
                    def = def with { Min = lo, Max = hi };
                else
                    Note($"{key}'s max isn't above its min, so it has no range.");
            }
            else if (min is not null || max is not null)
                Note($"{key} has only one of min and max; a range needs both, so it has none.");
            if (step is <= 0)
            {
                Note($"{key}'s step isn't above 0, so it was ignored.");
                step = null;
            }
            // As the deffile importer steps a float without one: a hundredth of the range's order of magnitude.
            var fallback = integer ? 1 : def.HasRange ? Math.Clamp(Math.Pow(10, Math.Floor(Math.Log10(def.Max - def.Min)) - 2), 0.001, 1) : 0.1;
            def = def with { Step = step ?? fallback, IsInteger = integer };
            if (def.Default.Length > 0 && !double.TryParse(def.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                Note($"{key}'s default ({def.Default}) isn't a number.");
            return def;
        }

        /// <summary>
        /// A choice's <c>choices</c>: values as written (strings or numbers), or <c>{ "value": v, "label": text }</c> objects
        /// whose label the dropdown shows while the value is what is stored. A list with any label labels every value (a
        /// plain one is its own label).
        /// </summary>
        private PropertyDef Choices(JsonElement f, string key, PropertyDef def)
        {
            if (!f.TryGetProperty("choices", out var c) || c.ValueKind != JsonValueKind.Array)
            {
                Note($"{key} is a choice with no choices list, so any value is accepted.");
                return def with { Choices = Array.Empty<string>() };
            }
            var values = new List<string>();
            var labels = new List<string>();
            var labelled = false;
            foreach (var item in c.EnumerateArray())
            {
                string? value = null, label = null;
                if (item.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    value = item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText();
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var v)
                         && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    value = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
                    label = Label(item, key);
                    labelled = true;
                    foreach (var member in item.EnumerateObject())
                        if (member.Name is not ("value" or "label"))
                            Note($"{key}: Apex doesn't know the choice member '{member.Name}', so it was ignored.");
                }
                if (value is null)
                {
                    Note($"{key}: a choice ({Short(item)}) isn't a string, a number or a {{ value, label }}, so it was ignored.");
                    continue;
                }
                if (values.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    Note($"{key} lists the choice {value} twice; the first is used.");
                    continue;
                }
                values.Add(value);
                labels.Add(label ?? value);
            }
            return def with { Choices = values.ToArray(), ChoiceLabels = labelled ? labels.ToArray() : null };
        }

        /// <summary>The default as the GDT would hold it: numbers keep the manifest's spelling, true/false are 1/0.</summary>
        private string? Default(JsonElement f, string key, string kind)
        {
            if (!f.TryGetProperty("default", out var d) || d.ValueKind == JsonValueKind.Null)
                return null;
            switch (d.ValueKind)
            {
                case JsonValueKind.String:
                    return d.GetString();
                case JsonValueKind.Number when d.TryGetDouble(out var n) && double.IsFinite(n):
                    return d.GetRawText();
                case JsonValueKind.Number:
                    Note($"{key}'s default ({Short(d)}) is too large to be a number, so it was ignored.");
                    return null;
                case JsonValueKind.True or JsonValueKind.False when kind == "toggle":
                    return d.ValueKind == JsonValueKind.True ? "1" : "0";
                default:
                    Note($"{key}'s default ({Short(d)}) isn't a value Apex can write, so it was ignored.");
                    return null;
            }
        }

        private VisibleWhen? Rule(JsonElement e, string what, string consequence)
        {
            if (!e.TryGetProperty("visibleWhen", out var v) || v.ValueKind == JsonValueKind.Null)
                return null;
            if (v.ValueKind != JsonValueKind.String)
            {
                Skip($"{what} isn't a string, so {consequence}.");
                return null;
            }
            var rule = VisibleWhen.Parse(v.GetString()!, out var error);
            if (rule is null)
                Skip($"{what} isn't valid ({error}), so {consequence}.");
            return rule;
        }

        /// <summary>Shown, never written: made <see cref="Displayable"/> (an emptied label falls back to the key).</summary>
        private string? Label(JsonElement e, string key) =>
            String(e, "label", key) is { } s && Displayable(s).Trim() is { Length: > 0 } label ? label : null;

        private string? Description(JsonElement e, string key) =>
            String(e, "description", key) is { } s ? Displayable(s, lines: true) : null;

        private string? String(JsonElement e, string name, string key)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
                return null;
            if (v.ValueKind == JsonValueKind.String)
                return v.GetString();
            Note($"{key}'s {name} isn't a string, so it was ignored.");
            return null;
        }

        private double? Double(JsonElement e, string name, string key)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
                return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d))
                return d;
            Note($"{key}'s {name} isn't a number, so it was ignored.");
            return null;
        }

        private static string Short(JsonElement e)
        {
            var raw = e.GetRawText();
            return raw.Length <= 40 ? raw : raw[..37] + "...";
        }
    }

    /// <summary>
    /// Why <paramref name="module"/> can't name a module inside <paramref name="folder"/>, or null when it can: a relative
    /// path ending in <c>.dll</c> that stays in the folder, with no <c>..</c>, drive, stream (<c>:</c>) or wildcard, and no
    /// junction or symbolic link on the way, the folder itself included (a link could point anywhere, so the folder would
    /// no longer say what loads).
    /// Checked when the manifest is read and again just before loading.
    /// </summary>
    public static string? CheckModulePath(string folder, string module)
    {
        if (module.Length == 0 || module.Length > 260)
            return "isn't a usable path";
        if (module.IndexOfAny(['*', '?', '"', '<', '>', '|', ':']) >= 0 || module.Any(c => Hidden(c, lines: false))
            || module.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return "has characters a file path can't hold";
        if (Path.IsPathRooted(module) || module[0] is '\\' or '/')
            return "must be a path inside the extension's folder, not an absolute one";
        var parts = module.Split('\\', '/');
        if (parts.Any(p => p.Length == 0 || p.Trim('.').Length == 0 || p.EndsWith('.') || p.EndsWith(' ')))
            return "must stay inside the extension's folder (no '..' or empty parts)";
        if (!module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return "must be a .dll";
        string root, full;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            full = Path.GetFullPath(Path.Combine(root, module));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "isn't a usable path";
        }
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "must stay inside the extension's folder";
        // Every part from the extension's folder down that exists, the file included: none may be a reparse point.
        for (var p = full; p.Length >= root.Length; p = Path.GetDirectoryName(p)!)
        {
            try
            {
                if (IsLink(p))
                    return "goes through a junction or symbolic link, which Apex doesn't follow for modules";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "can't be checked";
            }
            if (p.Length == root.Length)
                break;
        }
        return null;
    }

    private static bool IsLink(string path) =>
        (File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
}
