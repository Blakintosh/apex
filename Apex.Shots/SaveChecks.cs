using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Save;
using GdtEncoding = Apex.Render.Data.Gdt.GdtEncoding;

namespace Apex.Shots;

/// <summary>
/// The GDT save path, proven on temp copies (never the install): the splice writer, the per-file save sequence and
/// what can interfere with it. The fast set runs with every Shots run; <c>--save-corpus</c> runs the no-op round trip
/// and one-edit-per-file checks over every GDT in the install, copied to a temp folder.
/// </summary>
public partial class Program
{
    private static string? _saveRoot;

    /// <summary>
    /// The only folder the save path may write to in this process (<see cref="WriteGuard"/>). Set before anything
    /// else runs, so no check, screenshot flow or perf gate can write a GDT anywhere else.
    /// </summary>
    internal static string SaveRoot
    {
        get
        {
            if (_saveRoot is null)
            {
                _saveRoot = Path.Combine(Path.GetTempPath(), $"apex-shots-writes-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}");
                Directory.CreateDirectory(_saveRoot);
                Environment.SetEnvironmentVariable(WriteGuard.Variable, _saveRoot);
                Environment.SetEnvironmentVariable("APEX_BACKUP_DIR", Path.Combine(_saveRoot, "backups"));
            }
            return _saveRoot;
        }
    }

    private static string InstallRoot => @"I:\SteamLibrary\steamapps\common\Call of Duty Black Ops III";

    private static GdtSaveService NewSaveService(string label) =>
        new(new BackupStore(Path.Combine(SaveRoot, "backups", label)));

    private static string NewScratch(string label)
    {
        var dir = Path.Combine(SaveRoot, label + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static GdtFile LoadGdt(string path) => GdtLoader.IndexToGdtFile(new GdtSource(path, Path.GetFileName(path)));

    /// <summary>Plans, saves and commits one GDT's session changes, as the app does.</summary>
    private static GdtSaveResult SaveGdt(GdtSaveService service, GdtFile gdt, IEnumerable<AssetRecord>? deleted = null,
        bool isNew = false, string? root = null)
    {
        var problems = new List<string>();
        var requests = GdtSavePlanner.Plan(new[] { gdt }, gdt.Assets, deleted ?? Array.Empty<AssetRecord>(),
            isNew ? new[] { gdt } : Array.Empty<GdtFile>(), root, problems);
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join("; ", problems));
        var result = service.Save(requests);
        foreach (var f in result.Files)
            GdtSavePlanner.Commit((GdtFile)f.Request.Tag!, f);
        return result;
    }

    private static void Edit(AssetRecord r, string key, string? value)
    {
        r.CaptureBaseline();
        EditHistory.Set(r, key, value);
    }

    // ═══ Entry points ═══════════════════════════════════════════════════════

    private static void RunSaveChecks()
    {
        _ = SaveRoot;
        Console.WriteLine($"save checks: writes allowed only under {SaveRoot}");
        GuardChecks();
        GoldenSpliceChecks();
        RealFileChecks(CorpusSubset(), label: "subset");
        FuzzChecks(iterations: 400, seed: 1234);
        InterferenceChecks();
        BackupChecks();
        ExtensionChecks();
        GdtxReadChecks();
        GdtxCommentSaveChecks();
        Check($"save checks: nothing written outside {SaveRoot} (no .apex-tmp left in the temp roots)",
            !Directory.EnumerateFiles(SaveRoot, "*" + GdtSaveService.TempSuffix, SearchOption.AllDirectories).Any());
    }

    /// <summary>--save-corpus: every GDT in the install, copied to temp, round-tripped and edited once.</summary>
    private static int RunSaveCorpus()
    {
        _ = SaveRoot;
        var files = InstallGdts();
        RealFileChecks(files, label: "corpus");
        FuzzChecks(iterations: 3000, seed: 99);
        try { Directory.Delete(SaveRoot, recursive: true); } catch (IOException) { }
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Every .gdt under the install's GDT roots (read only), relative to the install.</summary>
    private static List<string> InstallGdts()
    {
        var list = new List<string>();
        foreach (var sub in new[] { "source_data", "xanim_export", "model_export", "usermaps", "mods", "share" })
        {
            var dir = Path.Combine(InstallRoot, sub);
            if (Directory.Exists(dir))
                list.AddRange(Directory.EnumerateFiles(dir, "*.gdt", new EnumerationOptions
                    { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }));
        }
        return list.Select(p => Path.GetRelativePath(InstallRoot, p)).ToList();
    }

    /// <summary>
    /// The fast set: the largest GDTs and every oddity the corpus survey found (LF-only, no trailing newline, space
    /// indentation, duplicate keys, cp1252 bytes, empty stubs, a usermap GDT, animation and model sidecars).
    /// </summary>
    private static List<string> CorpusSubset()
    {
        var wanted = new[]
        {
            @"source_data\ww2_paris_assets.gdt", @"source_data\black_ops_3_fx.gdt", @"model_export\motd\motd.gdt",
            @"xanim_export\t7_ai_zombie.gdt", @"source_data\iw9_sn_india_ab.gdt", @"source_data\rex_psierra41.gdt",
            @"source_data\lockonly_stats.gdt", @"source_data\tbd_s2\ar_as44_s2.gdt", @"source_data\tbd_s2\ar_wimmer_s2.gdt",
            @"source_data\tbd_s2\lmg_vmg1927_s2.gdt", @"source_data\tbd_s2\smg_austen_s2.gdt",
            @"source_data\blak_custom\lmg_dp28\blak_wpn_lmg_dp28_anims.gdt", @"source_data\blak_iw8\pistol_mike\iw8_wpn_pistol_mike_anims.gdt",
            @"source_data\blak_iw8\ar_scharlie\iw8_wpn_ar_scharlie_camo.gdt", @"source_data\zm_ai_parasite.gdt",
            @"source_data\skye_t8_hitchcock_m9.gdt", @"source_data\50cal_camo.gdt", @"source_data\1887_stat.gdt",
            @"source_data\ar_ak47_h1.gdt", @"source_data\blak_karelia\zm_karelia_weapons.gdt", @"share\raw\gdts\t7_zombie_player_character.gdt",
        };
        var list = wanted.Where(w => File.Exists(Path.Combine(InstallRoot, w))).ToList();
        // Plus whatever usermap GDT exists, and a few more sizes.
        list.AddRange(InstallGdts().Where(p => p.StartsWith("usermaps", StringComparison.OrdinalIgnoreCase)).Take(2));
        list.AddRange(InstallGdts().Where(p => p.StartsWith("source_data", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p).Where((_, i) => i % 97 == 0));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ═══ The write guard ════════════════════════════════════════════════════

    private static void GuardChecks()
    {
        Check("guard: the install is outside the allowed write root",
            Throws<WriteOutsideRootException>(() => WriteGuard.Check(Path.Combine(InstallRoot, "source_data", "x.gdt"))));
        var outside = Path.Combine(Path.GetTempPath(), "apex-guard-outside-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "g.gdt");
        File.WriteAllBytes(file, "{\r\n\t\"a\" ( \"x.gdf\" )\r\n\t{\r\n\t\t\"k\" \"1\"\r\n\t}\r\n}\r\n"u8.ToArray());
        var gdt = LoadGdt(file);
        Edit(gdt.Assets[0], "k", "2");
        var threw = Throws<WriteOutsideRootException>(() => SaveGdt(NewSaveService("guard"), gdt));
        Check("guard: a save outside the root throws before touching anything",
            threw && File.ReadAllText(file).Contains("\"k\" \"1\"") && !File.Exists(file + GdtSaveService.TempSuffix)
            && !Directory.Exists(Path.Combine(SaveRoot, "backups", "guard")));
        Directory.Delete(outside, true);
    }

    private static bool Throws<T>(Action a) where T : Exception
    {
        try { a(); return false; }
        catch (T) { return true; }
    }

    // ═══ Golden splices: exact bytes for every kind of edit ═════════════════

    private const string GoldenBase =
        "{\r\n" +
        "\t\"base_gun\" ( \"bulletweapon.gdf\" )\r\n\t{\r\n\t\t\"ammoName\" \"base\"\r\n\t\t\"clipSize\" \"30\"\r\n\t\t\"damage\" \"40\"\r\n\t}\r\n" +
        "\t\"base_gun_up\" [ \"base_gun\" ]\r\n\t{\r\n\t\t\"damage\" \"80\"\r\n\t}\r\n" +
        "\t\"other\" ( \"xmodel.gdf\" )\r\n\t{\r\n\t\t\"filename\" \"a\\\\b\\c.xmodel_bin\"\r\n\t\t\"type\" \"rigid\"\r\n\t\t\"weapon\" \"base_gun\"\r\n\t}\r\n" +
        "}\r\n";

    private static void GoldenSpliceChecks()
    {
        var dir = NewScratch("golden");
        var service = NewSaveService("golden");

        (GdtFile Gdt, string Path) Fresh(string name, string text)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllBytes(p, GdtEncoding.GetBytes(text));
            return (LoadGdt(p), p);
        }
        AssetRecord A(GdtFile g, string name) => g.Assets.First(a => a.Name == name);
        string Text(string p) => GdtEncoding.File.GetString(File.ReadAllBytes(p));

        // A value: only the bytes between its quotes change.
        var (g1, p1) = Fresh("value.gdt", GoldenBase);
        Edit(A(g1, "base_gun"), "damage", "45");
        var r1 = SaveGdt(service, g1);
        Check($"golden: a value edit changes only that value ({r1.Files[0].Status})",
            Text(p1) == GoldenBase.Replace("\"damage\" \"40\"", "\"damage\" \"45\""));

        // An override on a derived asset lands in APE's (sorted) key order; removing one takes its line.
        var (g2, p2) = Fresh("override.gdt", GoldenBase);
        Edit(A(g2, "base_gun_up"), "clipSize", "60");
        Edit(A(g2, "base_gun_up"), "zzLast", "1");
        Edit(A(g2, "base_gun_up"), "_first", "x");
        SaveGdt(service, g2);
        Check("golden: new keys go in APE's key order (_ before letters, case-insensitive)",
            Text(p2) == GoldenBase.Replace("\t\"base_gun_up\" [ \"base_gun\" ]\r\n\t{\r\n\t\t\"damage\" \"80\"\r\n",
                "\t\"base_gun_up\" [ \"base_gun\" ]\r\n\t{\r\n\t\t\"_first\" \"x\"\r\n\t\t\"clipSize\" \"60\"\r\n\t\t\"damage\" \"80\"\r\n\t\t\"zzLast\" \"1\"\r\n"));
        Edit(A(g2, "base_gun_up"), "damage", null);
        Edit(A(g2, "base_gun_up"), "_first", null);
        Edit(A(g2, "base_gun_up"), "clipSize", null);
        Edit(A(g2, "base_gun_up"), "zzLast", null);
        SaveGdt(service, g2);
        Check("golden: removing every override leaves an empty body in APE's shape",
            Text(p2) == GoldenBase.Replace("\t\"base_gun_up\" [ \"base_gun\" ]\r\n\t{\r\n\t\t\"damage\" \"80\"\r\n\t}\r\n", "\t\"base_gun_up\" [ \"base_gun\" ]\r\n\t{\r\n\t}\r\n"));
        Edit(A(g2, "base_gun_up"), "damage", "81");
        SaveGdt(service, g2);
        Check("golden: an override added to an empty body gets its own line",
            Text(p2) == GoldenBase.Replace("\"damage\" \"80\"", "\"damage\" \"81\""));

        // Rename with its references, as the in-memory refactor does: the deriver's parent and a value that names it.
        var (g3, p3) = Fresh("rename.gdt", GoldenBase);
        var gun = A(g3, "base_gun");
        gun.Name = "hero_gun";
        A(g3, "base_gun_up").Parent = "hero_gun";
        Edit(A(g3, "other"), "weapon", "hero_gun");
        SaveGdt(service, g3);
        Check("golden: rename rewrites the name, the deriver's parent and the reference, nothing else",
            Text(p3) == GoldenBase.Replace("\"base_gun\" (", "\"hero_gun\" (").Replace("[ \"base_gun\" ]", "[ \"hero_gun\" ]")
                .Replace("\"weapon\" \"base_gun\"", "\"weapon\" \"hero_gun\""));
        Check("golden: after a save the records point at the new offsets",
            g3.Assets.All(a => a.Disk is { } d && d.Name == a.Name
                && GdtParser.ParseProperties(p3, d.BodyOffset, (a.Source as GdtPropertySource)?.Length ?? 0).Count == a.Properties.Count));

        // Delete: the asset's block goes, the rest stays.
        var (g4, p4) = Fresh("delete.gdt", GoldenBase);
        var other = A(g4, "other");
        _ = other.Properties;
        other.CaptureBaseline();
        g4.Assets.Remove(other);
        SaveGdt(service, g4, deleted: new[] { other });
        Check("golden: delete removes exactly the asset's block",
            Text(p4) == GoldenBase.Replace("\t\"other\" ( \"xmodel.gdf\" )\r\n\t{\r\n\t\t\"filename\" \"a\\\\b\\c.xmodel_bin\"\r\n\t\t\"type\" \"rigid\"\r\n\t\t\"weapon\" \"base_gun\"\r\n\t}\r\n", ""));

        // New asset (keys in APE's order), duplicate of a derived asset, at the end of the file.
        var (g5, p5) = Fresh("new.gdt", GoldenBase);
        var fresh = new AssetRecord { Name = "new_gun", Type = "bulletweapon", GdtName = g5.Name };
        fresh.Properties["b"] = "2";
        fresh.Properties["A"] = "1";
        fresh.Properties["_c"] = "3";
        g5.Assets.Add(fresh);
        var copy = new AssetRecord { Name = "base_gun_up_copy", Type = "bulletweapon", GdtName = g5.Name, Parent = "base_gun" };
        foreach (var (k, v) in A(g5, "base_gun_up").Properties)
            copy.Properties[k] = v;
        g5.Assets.Add(copy);
        SaveGdt(service, g5);
        Check("golden: new and duplicated assets are appended in the file's form, keys in APE's order",
            Text(p5) == GoldenBase[..^3]
                + "\t\"new_gun\" ( \"bulletweapon.gdf\" )\r\n\t{\r\n\t\t\"_c\" \"3\"\r\n\t\t\"A\" \"1\"\r\n\t\t\"b\" \"2\"\r\n\t}\r\n"
                + "\t\"base_gun_up_copy\" [ \"base_gun\" ]\r\n\t{\r\n\t\t\"damage\" \"80\"\r\n\t}\r\n}\r\n");
        Check("golden: new assets are bound to the file after the save",
            fresh.Disk is not null && copy.Disk is not null && !fresh.HasSessionEdits);

        // New GDT: APE's empty file plus the assets.
        var newGdt = new GdtFile { Name = "brand_new.gdt" };
        var inNew = new AssetRecord { Name = "n1", Type = "xmodel", GdtName = newGdt.Name };
        inNew.Properties["filename"] = "x.xmodel_bin";
        newGdt.Assets.Add(inNew);
        var newRoot = NewScratch("golden-root");
        var r6 = SaveGdt(service, newGdt, isNew: true, root: newRoot);
        var p6 = Path.Combine(newRoot, "source_data", "brand_new.gdt");
        Check($"golden: a new GDT is created under source_data ({r6.Files[0].Status})",
            File.Exists(p6) && Text(p6) == "{\r\n\t\"n1\" ( \"xmodel.gdf\" )\r\n\t{\r\n\t\t\"filename\" \"x.xmodel_bin\"\r\n\t}\r\n}\r\n"
            && newGdt.Stamp is not null && newGdt.FullPath == p6);
        var emptyGdt = new GdtFile { Name = "empty_new.gdt" };
        SaveGdt(service, emptyGdt, isNew: true, root: newRoot);
        Check("golden: an empty new GDT is written as APE writes one",
            Text(Path.Combine(newRoot, "source_data", "empty_new.gdt")) == "{\r\n}\r\n");
        var again = new GdtFile { Name = "brand_new.gdt" };
        var r7 = SaveGdt(service, again, isNew: true, root: newRoot);
        Check($"golden: a new GDT never overwrites a file that exists ({r7.Files[0].Status})",
            r7.Files[0].Status == GdtSaveStatus.Conflict && Text(p6).Contains("n1"));

        // LF file without a trailing newline and with space indentation: new text follows it.
        const string odd = "{\n    \"a\" ( \"x.gdf\" )\n    {\n        \"k\" \"1\"\n    }\n}";
        var (g8, p8) = Fresh("odd.gdt", odd);
        Edit(A(g8, "a"), "j", "2");
        var n8 = new AssetRecord { Name = "b", Type = "x", GdtName = g8.Name };
        n8.Properties["k"] = "3";
        g8.Assets.Add(n8);
        SaveGdt(service, g8);
        Check("golden: an LF file with spaces and no final newline keeps all three",
            Text(p8) == "{\n    \"a\" ( \"x.gdf\" )\n    {\n        \"j\" \"2\"\n        \"k\" \"1\"\n    }\n    \"b\" ( \"x.gdf\" )\n    {\n        \"k\" \"3\"\n    }\n}");

        // Values a GDT can't hold are refused, not altered.
        var (g9, p9) = Fresh("invalid.gdt", GoldenBase);
        Edit(A(g9, "other"), "type", "say \"hi\"");
        var r9 = SaveGdt(service, g9);
        Check($"golden: a value with a double quote is refused and nothing is written ('{r9.Files[0].Message}')",
            r9.Files[0].Status == GdtSaveStatus.Invalid && Text(p9) == GoldenBase);
        Edit(A(g9, "other"), "type", "two\r\nlines");
        Check("golden: a value with a line break is refused", SaveGdt(service, g9).Files[0].Status == GdtSaveStatus.Invalid && Text(p9) == GoldenBase);
        if (!GdtEncoding.CanHold("中文", out _))
        {
            Edit(A(g9, "other"), "type", "Benét 中文");
            var r9b = SaveGdt(service, g9);
            Check($"golden: a character APE's code page can't hold is refused and nothing is written ('{r9b.Files[0].Message}')",
                r9b.Files[0].Status == GdtSaveStatus.Invalid && Text(p9) == GoldenBase);
        }
        Edit(A(g9, "other"), "type", "Benét-Mercié \\\\server\\share\\ {braces} ");
        SaveGdt(service, g9);
        Check("golden: accents, backslashes and braces are written raw, in APE's code page",
            Text(p9) == GoldenBase.Replace("\"type\" \"rigid\"", "\"type\" \"Benét-Mercié \\\\server\\share\\ {braces} \""));
        Check("golden: an accent is one ANSI byte, as APE writes it", GdtEncoding.File.CodePage != 1252 ||
            File.ReadAllBytes(p9).AsSpan().IndexOf(new byte[] { (byte)'B', (byte)'e', (byte)'n', 0xE9, (byte)'t' }) >= 0);

        // A file APE wrote with an accent reads as that accent, and an edit beside it leaves its byte alone.
        if (GdtEncoding.File.CodePage == 1252)
        {
            var ansi = GoldenBase.Replace("\t\t\"weapon\" \"base_gun\"", "\t\t\"weapon\" \"base_gun\"\r\n\t\t\"displayName\" \"Benét\"");
            var pa = Path.Combine(dir, "ansi.gdt");
            File.WriteAllBytes(pa, GdtEncoding.GetBytes(ansi));
            var ga = LoadGdt(pa);
            Check("golden: an ANSI accent reads as itself", A(ga, "other").Properties.TryGetValue("displayName", out var shown) && shown == "Benét");
            Edit(A(ga, "other"), "type", "animated");
            SaveGdt(service, ga);
            Check("golden: an edit beside an ANSI accent keeps its byte",
                Text(pa) == ansi.Replace("\"type\" \"rigid\"", "\"type\" \"animated\"")
                && File.ReadAllBytes(pa).AsSpan().IndexOf(new byte[] { (byte)'n', 0xE9, (byte)'t' }) >= 0);
        }

        // Duplicate keys: the loader reads the last; an edit sets every occurrence so any reader sees it.
        const string dup = "{\r\n\t\"a\" ( \"x.gdf\" )\r\n\t{\r\n\t\t\"k\" \"1\"\r\n\t\t\"m\" \"0\"\r\n\t\t\"K\" \"2\"\r\n\t}\r\n}\r\n";
        var (g10, p10) = Fresh("dup.gdt", dup);
        Edit(A(g10, "a"), "k", "3");
        SaveGdt(service, g10);
        Check("golden: an edited duplicate key is set in every occurrence", Text(p10) == dup.Replace("\"1\"", "\"3\"").Replace("\"2\"", "\"3\""));
    }

    // ═══ Real GDTs: identity, no-op round trip, one edit per file ═══════════

    private static void RealFileChecks(List<string> relative, string label)
    {
        var copyRoot = NewScratch("real-" + label);
        var sw = Stopwatch.StartNew();
        long bytes = 0;
        foreach (var rel in relative)
        {
            var dst = Path.Combine(copyRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(Path.Combine(InstallRoot, rel), dst);
            bytes += new FileInfo(dst).Length;
        }
        Console.WriteLine($"info  save {label}: copied {relative.Count:N0} GDTs ({bytes / 1048576.0:N0} MB) to temp in {sw.ElapsedMilliseconds:N0} ms");

        var layoutBad = new ConcurrentBag<string>();
        var noopBad = new ConcurrentBag<string>();
        var editBad = new ConcurrentBag<string>();
        var edited = 0;
        var noEditable = 0;
        long biggest = 0;
        string? biggestName = null;
        double biggestMs = 0;
        var service = NewSaveService("real-" + label);
        sw.Restart();
        Parallel.ForEach(relative, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, rel =>
        {
            var path = Path.Combine(copyRoot, rel);
            var original = File.ReadAllBytes(Path.Combine(InstallRoot, rel));
            try
            {
                // 1. The writer's layout is the loader's index: same assets, same body offsets.
                var index = GdtIndexer.Index(original);
                var layout = GdtLayout.Scan(original);
                if (index.Count != layout.Assets.Count || index.Zip(layout.Assets).Any(p =>
                        p.First.Name != p.Second.Name || p.First.TypeOrParent != p.Second.TypeOrParent || p.First.IsDerived != p.Second.IsDerived
                        || p.First.BodyOffset != p.Second.BodyStart || p.First.BodyLength != p.Second.BodyLength))
                    layoutBad.Add(rel);

                // 2. No-op: every record read into memory, nothing changed → nothing planned; then the full write
                //    sequence forced with no edits → byte-identical.
                var gdt = LoadGdt(path);
                foreach (var a in gdt.Assets)
                    a.CaptureBaseline();
                var problems = new List<string>();
                var planned = GdtSavePlanner.Plan(new[] { gdt }, gdt.Assets, Array.Empty<AssetRecord>(), Array.Empty<GdtFile>(), null, problems);
                var forced = new GdtSaveService(new BackupStore(Path.Combine(SaveRoot, "backups", "noop-" + label))) { WriteEvenIfUnchanged = true };
                var noop = forced.Save(new[] { new GdtSaveRequest { Path = path, Expected = gdt.Stamp, Edits = Array.Empty<AssetEdit>() } });
                if (planned.Count != 0 || noop.Files[0].Status != GdtSaveStatus.Saved || !File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
                    noopBad.Add($"{rel} (planned {planned.Count}, {noop.Files[0].Status} {noop.Files[0].Message})");
                gdt.Stamp = noop.Files[0].Stamp;

                // 3. One value in one asset: the new file is the old one with exactly those bytes replaced.
                var target = PickEditable(gdt, original, out var key, out var valueStart, out var valueEnd);
                if (target is null)
                {
                    Interlocked.Increment(ref noEditable);
                    return;
                }
                var old = target.Properties[key];
                var value = old.Length > 0 && char.IsDigit(old[^1]) ? old + "7" : old + "_apex";
                Edit(target, key, value);
                var t0 = Stopwatch.GetTimestamp();
                var saved = SaveGdt(service, gdt);
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                var expected = new byte[original.Length - (valueEnd - valueStart) + GdtEncoding.File.GetByteCount(value)];
                original.AsSpan(0, valueStart).CopyTo(expected);
                GdtEncoding.GetBytes(value).CopyTo(expected, valueStart);
                original.AsSpan(valueEnd).CopyTo(expected.AsSpan(valueStart + GdtEncoding.File.GetByteCount(value)));
                var now = File.ReadAllBytes(path);
                var reread = GdtIndexer.Index(now);
                var ok = saved.Files[0].Status == GdtSaveStatus.Saved && now.AsSpan().SequenceEqual(expected)
                    && reread.Count == index.Count
                    && gdt.Assets.All(a => a.Disk is { } d && reread.Any(e => e.BodyOffset == d.BodyOffset && e.Name == d.Name))
                    && GdtSavePlanner.EditFor(target) is null && !target.HasSessionEdits;
                // Re-parse equals the model: the edited asset from disk, and a fresh read through every rebound source.
                var e1 = reread.First(e => e.BodyOffset == target.Disk!.Value.BodyOffset);
                ok &= GdtSplicer.SameValues(GdtParser.ParseProperties(now, e1.BodyOffset, e1.BodyLength), target.Properties);
                ok &= gdt.Assets.Take(5).Concat(gdt.Assets.TakeLast(5)).All(a => a.Source is null
                    || GdtSplicer.SameValues(a.Source.Materialize(), GdtParser.ParseProperties(now, a.Disk!.Value.BodyOffset, ((GdtPropertySource)a.Source).Length)));
                if (!ok)
                    editBad.Add($"{rel} ({saved.Files[0].Status} {saved.Files[0].Message} {saved.Files[0].Detail})");
                Interlocked.Increment(ref edited);
                lock (editBad)
                {
                    if (original.Length > biggest)
                    {
                        biggest = original.Length;
                        biggestName = rel;
                        biggestMs = ms;
                    }
                }
            }
            catch (Exception ex)
            {
                editBad.Add($"{rel}: {ex.GetType().Name} {ex.Message}");
            }
        });
        Check($"save {label}: writer layout matches the loader's index in {relative.Count - layoutBad.Count:N0}/{relative.Count:N0} GDTs {string.Join(", ", layoutBad.Take(3))}",
            layoutBad.IsEmpty);
        Check($"save {label}: no-op round trip byte-identical in {relative.Count - noopBad.Count:N0}/{relative.Count:N0} GDTs {string.Join(", ", noopBad.Take(3))}",
            noopBad.IsEmpty);
        Check($"save {label}: one edit per GDT changes only that value's bytes in {edited - editBad.Count:N0}/{edited:N0} GDTs "
              + $"({noEditable} have no asset with a value) {string.Join(", ", editBad.Take(3))}", editBad.IsEmpty);
        Console.WriteLine($"info  save {label}: {relative.Count:N0} GDTs in {sw.ElapsedMilliseconds:N0} ms; one edit in the largest ({biggestName}, {biggest / 1048576.0:N1} MB) saved in {biggestMs:N0} ms");

        RealStructuralChecks(copyRoot, relative);
        foreach (var folder in new[] { copyRoot, Path.Combine(SaveRoot, "backups", "real-" + label), Path.Combine(SaveRoot, "backups", "noop-" + label) })
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>An asset (middle of the file) with a key that occurs once in its body, and that value's byte range.</summary>
    private static AssetRecord? PickEditable(GdtFile gdt, byte[] bytes, out string key, out int valueStart, out int valueEnd)
    {
        key = "";
        valueStart = valueEnd = 0;
        var layout = GdtLayout.Scan(bytes);
        var order = Enumerable.Range(0, gdt.Assets.Count).OrderBy(i => Math.Abs(i - gdt.Assets.Count / 2));
        foreach (var i in order)
        {
            var span = layout.Assets[i];
            var props = GdtLayout.ScanProps(bytes, span, out var regular);
            if (!regular || !span.Closed || props.Count == 0)
                continue;
            foreach (var p in props.Skip(props.Count / 2).Concat(props.Take(props.Count / 2)))
            {
                if (props.Count(q => q.Key.Equals(p.Key, StringComparison.OrdinalIgnoreCase)) != 1 || p.Value.Contains('\uFFFD'))
                    continue;
                var rec = gdt.Assets[i];
                if (rec.Disk?.BodyOffset != span.BodyStart)
                    return null;
                key = p.Key;
                valueStart = p.ValueStart;
                valueEnd = p.ValueEnd;
                return rec;
            }
        }
        return null;
    }

    /// <summary>
    /// On a real GDT with derived assets: rename a root with its derivers and references, duplicate, delete, add
    /// root and derived assets, add and remove overrides. Every untouched asset's block must stay byte-identical.
    /// </summary>
    private static void RealStructuralChecks(string copyRoot, List<string> relative)
    {
        string? pick = null;
        foreach (var rel in relative.OrderBy(r => new FileInfo(Path.Combine(copyRoot, r)).Length))
        {
            var idx = GdtIndexer.IndexFile(Path.Combine(copyRoot, rel));
            if (idx.Count >= 6 && idx.Any(e => e.IsDerived && idx.Any(r => !r.IsDerived && r.Name.Equals(e.TypeOrParent, StringComparison.OrdinalIgnoreCase))))
            {
                pick = rel;
                break;
            }
        }
        if (pick is null)
        {
            Console.WriteLine("info  save structural: no real GDT with a root and its deriver in the set; golden checks cover it");
            return;
        }
        var path = Path.Combine(copyRoot, pick);
        var before = File.ReadAllBytes(path);
        var gdt = LoadGdt(path);
        var deriver = gdt.Assets.First(a => a.Parent is not null && gdt.Assets.Any(r => r.Parent is null && r.Name.Equals(a.Parent, StringComparison.OrdinalIgnoreCase)));
        var root = gdt.Assets.First(r => r.Parent is null && r.Name.Equals(deriver.Parent, StringComparison.OrdinalIgnoreCase));
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var oldRoot = root.Name;
        root.Name = oldRoot + "_renamed";
        touched.Add(oldRoot);
        foreach (var d in gdt.Assets.Where(a => string.Equals(a.Parent, oldRoot, StringComparison.OrdinalIgnoreCase)))
        {
            d.Parent = root.Name;
            touched.Add(d.Disk!.Value.Name);
        }
        foreach (var a in gdt.Assets.Where(a => a != root))
        {
            var refs = a.Properties.Where(kv => kv.Value.Equals(oldRoot, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
            foreach (var k in refs)
            {
                Edit(a, k, root.Name);
                touched.Add(a.Disk!.Value.Name);
            }
        }
        Edit(deriver, "apexOverride", "1");
        var firstKey = deriver.Properties.Keys.FirstOrDefault(k => k != "apexOverride");
        if (firstKey is not null)
            Edit(deriver, firstKey, null);
        var doomed = gdt.Assets.Last(a => !touched.Contains(a.Disk!.Value.Name) && a != root && a != deriver
                                          && !gdt.Assets.Any(d => string.Equals(d.Parent, a.Name, StringComparison.OrdinalIgnoreCase)));
        _ = doomed.Properties;
        doomed.CaptureBaseline();
        gdt.Assets.Remove(doomed);
        touched.Add(doomed.Name);
        var dup = new AssetRecord { Name = deriver.Name + "_copy", Type = deriver.Type, GdtName = gdt.Name, Parent = deriver.Parent };
        foreach (var (k, v) in deriver.Properties)
            dup.Properties[k] = v;
        gdt.Assets.Add(dup);
        var created = new AssetRecord { Name = "apex_new_root", Type = root.Type, GdtName = gdt.Name };
        foreach (var (k, v) in root.Properties)
            created.Properties[k] = v;
        gdt.Assets.Add(created);

        var result = SaveGdt(NewSaveService("structural"), gdt, deleted: new[] { doomed });
        var after = File.ReadAllBytes(path);
        var oldLayout = GdtLayout.Scan(before);
        var newLayout = GdtLayout.Scan(after);
        string Block(byte[] b, GdtAssetSpan s) => Encoding.Latin1.GetString(b, s.BlockStart, s.BlockEnd - s.BlockStart);
        var newBlocks = newLayout.Assets.Select(s => Block(after, s)).ToHashSet();
        var untouchedSame = oldLayout.Assets.Where(s => !touched.Contains(s.Name)).All(s => newBlocks.Contains(Block(before, s)));
        var names = newLayout.Assets.Select(a => a.Name).ToList();
        var expectedNames = oldLayout.Assets.Where(s => !s.Name.Equals(doomed.Name, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Name.Equals(oldRoot, StringComparison.OrdinalIgnoreCase) ? root.Name : s.Name)
            .Concat(new[] { dup.Name, created.Name }).ToList();
        var model = gdt.Assets.All(a =>
        {
            var s = newLayout.Assets.First(n => n.BodyStart == a.Disk!.Value.BodyOffset);
            return s.Name == a.Name && (a.Parent is null ? !s.IsDerived : s.TypeOrParent == a.Parent)
                   && GdtSplicer.SameValues(GdtParser.ParseProperties(after, s.BodyStart, s.BodyLength), a.Properties);
        });
        Check($"save structural ({pick}): rename+references, override add/remove, delete, duplicate, new → {result.Files[0].Status}; "
              + $"re-parse equals the model, {oldLayout.Assets.Count - touched.Count} untouched assets byte-identical",
            result.Files[0].Status == GdtSaveStatus.Saved && untouchedSame && names.SequenceEqual(expectedNames) && model);
    }

    // ═══ Fuzz ═══════════════════════════════════════════════════════════════

    private sealed class FuzzAsset
    {
        public required string Name;
        public string? Parent;
        public string Type = "fuzz";
        public List<(string K, string V)> Props = new();
    }

    private static readonly string[] FuzzKeys =
        { "damage", "Damage", "clipSize", "_under", "fx_path", "a", "b", "zz", "ammoName", "type", "filename", "mixedCASE", "x1", "x10", "x2" };

    private static string FuzzValue(Random rng)
    {
        switch (rng.Next(12))
        {
            case 0: return "";
            case 1: return "\\\\server\\share\\a\\b.tif";
            case 2: return "Benét-Mercié ©";
            case 3: return new string('v', rng.Next(1000, 40000));
            case 4: return "{ } [ ] ( ) '";
            case 5: return "\t tab and  spaces ";
            case 6: return rng.Next(-1000, 1000).ToString();
            case 7: return (rng.NextDouble() * 100).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            default:
                var chars = "abcXYZ019_-./\\ :;,%$#@!é".ToCharArray();
                return new string(Enumerable.Range(0, rng.Next(1, 30)).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
        }
    }

    private static string RenderFuzz(List<FuzzAsset> assets, Random rng, out string eol, out bool trailing)
    {
        eol = rng.Next(4) == 0 ? "\n" : "\r\n";
        trailing = rng.Next(5) != 0;
        var spaces = rng.Next(4) == 0;
        var ai = spaces ? "    " : "\t";
        var pi = spaces ? "        " : "\t\t";
        var tight = rng.Next(5) == 0;
        var sb = new StringBuilder("{").Append(eol);
        foreach (var a in assets)
        {
            sb.Append(ai).Append('"').Append(a.Name).Append('"');
            if (a.Parent is not null)
                sb.Append(tight ? $" [\"{a.Parent}\"]" : $" [ \"{a.Parent}\" ]");
            else
                sb.Append(tight ? $" (\"{a.Type}.gdf\")" : $" ( \"{a.Type}.gdf\" )");
            sb.Append(eol).Append(ai).Append('{').Append(eol);
            foreach (var (k, v) in a.Props)
                sb.Append(pi).Append('"').Append(k).Append("\" \"").Append(v).Append('"').Append(eol);
            sb.Append(ai).Append('}').Append(eol);
        }
        sb.Append('}');
        if (trailing)
            sb.Append(eol);
        return sb.ToString();
    }

    private static void FuzzChecks(int iterations, int seed)
    {
        var dir = NewScratch("fuzz");
        var service = NewSaveService("fuzz");
        var rng = new Random(seed);
        var failures = new List<string>();
        var saves = 0;
        var refusals = 0;
        var ops = 0;
        for (var it = 0; it < iterations && failures.Count < 5; it++)
        {
            // A GDT in random conventions: sorted or unsorted keys, duplicate keys, empty bodies, derived assets.
            var assets = new List<FuzzAsset>();
            var count = rng.Next(0, 8);
            for (var i = 0; i < count; i++)
            {
                var a = new FuzzAsset { Name = $"asset_{i}_{rng.Next(1000)}" };
                if (i > 0 && rng.Next(3) == 0)
                    a.Parent = assets[rng.Next(i)].Name;
                var n = rng.Next(0, 7);
                for (var k = 0; k < n; k++)
                    a.Props.Add((FuzzKeys[rng.Next(FuzzKeys.Length)], FuzzValue(rng)));
                if (rng.Next(2) == 0)
                    a.Props.Sort((x, y) => ApeKeyComparer.Instance.Compare(x.K, y.K));
                assets.Add(a);
            }
            var path = Path.Combine(dir, $"f{it}.gdt");
            var text = RenderFuzz(assets, rng, out var eol, out var trailing);
            File.WriteAllBytes(path, GdtEncoding.GetBytes(text));
            var gdt = LoadGdt(path);

            // The model the file must read back as: name → (parent, values), in file order.
            var expected = assets.Select(a => (a.Name, a.Parent, Values: a.Props.Aggregate(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), (d, p) => { d[p.K] = p.V; return d; }))).ToList();
            var deleted = new List<AssetRecord>();

            for (var round = 0; round < 3; round++)
            {
                var before = File.ReadAllBytes(path);
                var touched = new HashSet<AssetRecord>();
                var invalid = false;
                var nOps = rng.Next(1, 6);
                for (var o = 0; o < nOps; o++)
                {
                    ops++;
                    var live = gdt.Assets;
                    var kind = live.Count == 0 ? 5 : rng.Next(8);
                    var rec = live.Count > 0 ? live[rng.Next(live.Count)] : null;
                    var ei = rec is null ? -1 : expected.FindIndex(x => x.Name == rec.Name);
                    switch (kind)
                    {
                        case 0: case 1: case 2: // set a value (existing or new key)
                        {
                            var key = rng.Next(3) == 0 ? $"new_{rng.Next(50)}" : FuzzKeys[rng.Next(FuzzKeys.Length)];
                            var v = rng.Next(40) == 0 ? "bad \" quote" : rng.Next(40) == 0 ? "bad\nbreak" : FuzzValue(rng);
                            Edit(rec!, key, v);
                            expected[ei].Values[key] = v;
                            touched.Add(rec!);
                            break;
                        }
                        case 3: // remove a key
                        {
                            if (rec!.Properties.Count == 0) break;
                            var key = rec.Properties.Keys.ElementAt(rng.Next(rec.Properties.Count));
                            Edit(rec, key, null);
                            expected[ei].Values.Remove(key);
                            touched.Add(rec);
                            break;
                        }
                        case 4: // rename (derivers follow, as the refactor does)
                        {
                            var old = rec!.Name;
                            var name = $"ren_{it}_{round}_{o}";
                            rec.Name = name;
                            expected[ei] = (name, expected[ei].Parent, expected[ei].Values);
                            touched.Add(rec);
                            foreach (var d in live.Where(a => a.Parent == old))
                            {
                                d.Parent = name;
                                var di = expected.FindIndex(x => x.Name == d.Name);
                                expected[di] = (expected[di].Name, name, expected[di].Values);
                                touched.Add(d);
                            }
                            break;
                        }
                        case 5: // new asset (root or derived)
                        {
                            var fresh = new AssetRecord { Name = $"new_{it}_{round}_{o}", Type = "fuzz", GdtName = gdt.Name,
                                Parent = live.Count > 0 && rng.Next(2) == 0 ? live[rng.Next(live.Count)].Name : null };
                            var n = rng.Next(0, 5);
                            for (var k = 0; k < n; k++)
                                fresh.Properties[FuzzKeys[rng.Next(FuzzKeys.Length)]] = FuzzValue(rng);
                            live.Add(fresh);
                            expected.Add((fresh.Name, fresh.Parent, new Dictionary<string, string>(fresh.Properties, StringComparer.OrdinalIgnoreCase)));
                            touched.Add(fresh);
                            break;
                        }
                        case 6: // delete (only an asset nothing derives from, as Apex's delete requires)
                        {
                            if (live.Any(a => a.Parent == rec!.Name)) break;
                            _ = rec!.Properties;
                            rec.CaptureBaseline();
                            live.Remove(rec);
                            if (rec.Disk is not null) deleted.Add(rec);
                            expected.RemoveAt(ei);
                            touched.Add(rec);
                            break;
                        }
                        default: // no-op edit: the same value again
                        {
                            if (rec!.Properties.Count == 0) break;
                            var (k, v) = rec.Properties.First();
                            Edit(rec, k, v);
                            break;
                        }
                    }
                }

                invalid = gdt.Assets.Any(r => r.IsMaterialized && r.Properties.Values.Any(v => v.Contains('"') || v.Contains('\n')));
                var touchedOffsets = touched.Where(r => r.Disk is not null).Select(r => r.Disk!.Value.BodyOffset).ToHashSet();
                GdtSaveResult result;
                try { result = SaveGdt(service, gdt, deleted); }
                catch (Exception ex) { failures.Add($"f{it} r{round}: {ex.GetType().Name} {ex.Message}"); break; }
                var file = result.Files.FirstOrDefault();
                var after = File.ReadAllBytes(path);
                if (invalid)
                {
                    refusals++;
                    if (file?.Status != GdtSaveStatus.Invalid || !after.AsSpan().SequenceEqual(before))
                        failures.Add($"f{it} r{round}: an invalid value was not refused cleanly ({file?.Status})");
                    break; // the session holds a value it can't save; start a new file
                }
                if (file is not null && file.Status is not (GdtSaveStatus.Saved or GdtSaveStatus.Unchanged))
                {
                    failures.Add($"f{it} r{round}: {file.Status} {file.Message} {string.Join("; ", file.Problems)}");
                    break;
                }
                saves++;
                deleted.Clear();

                // Reads back as the model, in order.
                var layout = GdtLayout.Scan(after);
                var names = layout.Assets.Select(a => a.Name).ToList();
                if (!names.SequenceEqual(expected.Select(x => x.Name)))
                    failures.Add($"f{it} r{round}: asset order {string.Join(",", names)} vs {string.Join(",", expected.Select(x => x.Name))}");
                else
                    for (var i = 0; i < names.Count; i++)
                    {
                        var s = layout.Assets[i];
                        var values = GdtParser.ParseProperties(after, s.BodyStart, s.BodyLength);
                        if (!GdtSplicer.SameValues(values, expected[i].Values) || (expected[i].Parent is { } p ? s.TypeOrParent != p : s.IsDerived))
                            failures.Add($"f{it} r{round}: {names[i]} doesn't read back as the model");
                    }
                // The file's conventions survive.
                var t = Encoding.UTF8.GetString(after);
                var crlf = t.Split("\r\n").Length - 1;
                var lf = t.Count(c => c == '\n') - crlf;
                if ((eol == "\n" && crlf > 0) || (eol == "\r\n" && lf > 0) || t.EndsWith('\n') != trailing || t.StartsWith('\uFEFF'))
                    failures.Add($"f{it} r{round}: conventions changed (eol {eol.Length}, trailing {trailing})");
                // Untouched assets are byte-identical; every record reads the same through its rebound source.
                var oldLayout = GdtLayout.Scan(before);
                var newBlocks = layout.Assets.Select(s => Convert.ToBase64String(after, s.BlockStart, s.BlockEnd - s.BlockStart)).ToHashSet();
                foreach (var s in oldLayout.Assets)
                    if (!touchedOffsets.Contains(s.BodyStart)
                        && !newBlocks.Contains(Convert.ToBase64String(before, s.BlockStart, s.BlockEnd - s.BlockStart)))
                        failures.Add($"f{it} r{round}: untouched {s.Name} changed");
                foreach (var r in gdt.Assets)
                {
                    var fromDisk = r.Source is GdtPropertySource src ? GdtParser.ParseProperties(src.Path, src.Offset, src.Length) : new Dictionary<string, string>();
                    var want = expected.First(x => x.Name == r.Name).Values;
                    if (!GdtSplicer.SameValues(fromDisk, want) || !GdtSplicer.SameValues(r.Properties, want) || r.HasSessionEdits)
                    {
                        failures.Add($"f{it} r{round}: {r.Name} isn't rebound to the saved file");
                        break;
                    }
                }
            }
        }
        Check($"save fuzz: {iterations} random GDTs × up to 3 saves ({saves} saves, {ops} edits, {refusals} invalid values refused) "
              + $"read back as the model with conventions kept {string.Join(" | ", failures.Take(3))}", failures.Count == 0);
    }

    // ═══ Interference and failure ═══════════════════════════════════════════

    private const string SmallGdt =
        "{\r\n\t\"alpha\" ( \"xmodel.gdf\" )\r\n\t{\r\n\t\t\"filename\" \"a.xmodel_bin\"\r\n\t\t\"type\" \"rigid\"\r\n\t}\r\n"
        + "\t\"beta\" ( \"xmodel.gdf\" )\r\n\t{\r\n\t\t\"filename\" \"b.xmodel_bin\"\r\n\t\t\"type\" \"rigid\"\r\n\t}\r\n}\r\n";

    private static void InterferenceChecks()
    {
        var dir = NewScratch("interfere");
        var n = 0;
        (GdtFile Gdt, string Path) Setup(string text = SmallGdt)
        {
            var p = Path.Combine(dir, $"i{n++}.gdt");
            File.WriteAllBytes(p, GdtEncoding.GetBytes(text));
            var g = LoadGdt(p);
            Edit(g.Assets[0], "type", "animated");
            return (g, p);
        }
        bool Untouched(string p, string text = SmallGdt) =>
            File.ReadAllText(p) == text && !File.Exists(p + GdtSaveService.TempSuffix);

        // Another program has it open for writing.
        {
            var (g, p) = Setup();
            GdtSaveFileResult r;
            using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                r = SaveGdt(NewSaveService("i-locked"), g).Files[0];
            Check($"interfere: open for writing elsewhere → '{r.Message}', file untouched",
                r.Status == GdtSaveStatus.Locked && r.Message.Contains("open in another program") && Untouched(p) && GdtSavePlanner.EditFor(g.Assets[0]) is not null);
            var retry = SaveGdt(NewSaveService("i-locked"), g).Files[0];
            Check("interfere: saving again once it's closed works", retry.Status == GdtSaveStatus.Saved && File.ReadAllText(p).Contains("animated"));
        }

        // Changed on disk between load and save: a conflict, never overwritten.
        {
            var (g, p) = Setup();
            var external = SmallGdt.Replace("\"type\" \"rigid\"\r\n\t}\r\n\t\"beta\"", "\"type\" \"skinned\"\r\n\t}\r\n\t\"beta\"");
            File.WriteAllText(p, external);
            var r = SaveGdt(NewSaveService("i-conflict"), g).Files[0];
            var c = r.Conflicts.FirstOrDefault();
            Check($"interfere: changed on disk since load → conflict listing alpha · type (yours animated, disk skinned), file not overwritten",
                r.Status == GdtSaveStatus.Conflict && r.ChangedOnDisk && c is { Kind: AssetConflictKind.ValuesChanged }
                && c.Asset == "alpha" && c.Keys.Single() is { Key: "type", Was: "rigid", OnDisk: "skinned", Mine: "animated" }
                && Untouched(p, external) && GdtSavePlanner.EditFor(g.Assets[0]) is not null);
            // The user keeps theirs: save again against the file as it is now.
            g.Stamp = r.Stamp;
            var theirs = new GdtSaveService(new BackupStore(Path.Combine(SaveRoot, "backups", "i-conflict")));
            var edits = GdtSavePlanner.EditFor(g.Assets[0])!;
            var keep = new AssetEdit { Disk = edits.Disk, Name = edits.Name, Type = edits.Type, Set = edits.Set, Baseline = null, Tag = g.Assets[0] };
            var r2 = theirs.Save(new[] { new GdtSaveRequest { Path = p, Expected = r.Stamp, Edits = new[] { keep }, Tag = g } }).Files[0];
            Check("interfere: keeping yours after a conflict writes your value over the disk's",
                r2.Status == GdtSaveStatus.Saved && File.ReadAllText(p) == SmallGdt.Replace("\"type\" \"rigid\"\r\n\t}\r\n\t\"beta\"", "\"type\" \"animated\"\r\n\t}\r\n\t\"beta\""));

            // A change elsewhere in the file doesn't overlap: still a conflict (stamp), with no asset listed, and
            // saving against the new stamp merges without losing the other program's change.
            var (g2, p2) = Setup();
            var elsewhere = SmallGdt.Replace("b.xmodel_bin", "b2.xmodel_bin");
            File.WriteAllText(p2, elsewhere);
            var r3 = SaveGdt(NewSaveService("i-conflict"), g2).Files[0];
            g2.Stamp = r3.Stamp;
            var r4 = SaveGdt(NewSaveService("i-conflict"), g2).Files[0];
            Check("interfere: a change elsewhere in the file is a conflict with nothing overlapping; saving then keeps both",
                r3.Status == GdtSaveStatus.Conflict && r3.Conflicts.Count == 0 && r4.Status == GdtSaveStatus.Saved
                && File.ReadAllText(p2) == elsewhere.Replace("\"type\" \"rigid\"\r\n\t}\r\n\t\"beta\"", "\"type\" \"animated\"\r\n\t}\r\n\t\"beta\""));
        }

        // Changed between preflight and lock: caught by the re-check under the lock.
        {
            var (g, p) = Setup();
            var svc = NewSaveService("i-race");
            var sneaky = SmallGdt.Replace("a.xmodel_bin", "sneaky.xmodel_bin");
            svc.Hooks.AfterPreflight = path => File.WriteAllText(path, sneaky);
            var r = SaveGdt(svc, g).Files[0];
            Check("interfere: a write between preflight and lock is caught under the lock (conflict, not overwritten)",
                r.Status == GdtSaveStatus.Conflict && r.ChangedOnDisk && Untouched(p, sneaky));
        }

        // A handle held during the swap (antivirus, linker): retried, then reported; or retried until it lets go.
        {
            var (g, p) = Setup();
            var svc = NewSaveService("i-swap");
            FileStream? held = null;
            svc.Hooks.BeforeSwap = path => held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sw = Stopwatch.StartNew();
            var r = SaveGdt(svc, g).Files[0];
            var took = sw.ElapsedMilliseconds;
            held?.Dispose();
            Check($"interfere: a handle held through the swap → retried for {took} ms, then '{r.Message}', original intact",
                r.Status == GdtSaveStatus.Locked && took >= 900 && Untouched(p) && !r.Replaced);

            var (g2, p2) = Setup();
            var svc2 = NewSaveService("i-swap");
            svc2.Hooks.BeforeSwap = path =>
            {
                var h = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                Task.Delay(250).ContinueWith(_ => h.Dispose());
            };
            var r2 = SaveGdt(svc2, g2).Files[0];
            Check("interfere: a handle released within the retry window → saved", r2.Status == GdtSaveStatus.Saved && File.ReadAllText(p2).Contains("animated"));
        }

        // Killed between stage and swap: original intact, temp file left, cleaned up by the next save.
        {
            var (g, p) = Setup();
            var svc = NewSaveService("i-kill");
            svc.Hooks.BeforeSwap = _ => throw new SimulatedCrash();
            var crashed = Throws<SimulatedCrash>(() => SaveGdt(svc, g));
            var tempLeft = File.Exists(p + GdtSaveService.TempSuffix);
            var intact = File.ReadAllText(p) == SmallGdt;
            var r = SaveGdt(NewSaveService("i-kill"), g).Files[0];
            Check("interfere: a crash between stage and swap leaves the original intact; the next save removes the temp file and saves",
                crashed && tempLeft && intact && r.Status == GdtSaveStatus.Saved && !File.Exists(p + GdtSaveService.TempSuffix)
                && File.ReadAllText(p).Contains("animated"));
        }

        // Read-only.
        {
            var (g, p) = Setup();
            File.SetAttributes(p, FileAttributes.ReadOnly);
            var r = SaveGdt(NewSaveService("i-ro"), g).Files[0];
            File.SetAttributes(p, FileAttributes.Normal);
            Check($"interfere: read-only file → '{r.Message}', no write, no backup",
                r.Status == GdtSaveStatus.ReadOnly && Untouched(p) && r.BackupPath is null);
        }

        // Something writes in the swap window: verification catches it, the backup is kept.
        {
            var (g, p) = Setup();
            var svc = NewSaveService("i-verify");
            svc.Hooks.AfterSwap = path => File.WriteAllText(path, "{\r\n}\r\n");
            var r = SaveGdt(svc, g).Files[0];
            Check($"interfere: a write in the swap window fails verification ('{r.Message}') and keeps the backup",
                r.Status == GdtSaveStatus.Failed && r.Replaced && r.BackupPath is { } b && File.ReadAllText(b) == SmallGdt
                && GdtSavePlanner.EditFor(g.Assets[0]) is not null);
        }

        // Several files, one can't be locked: nothing is written.
        {
            var (g1, p1) = Setup();
            var (g2, p2) = Setup();
            var problems = new List<string>();
            var requests = GdtSavePlanner.Plan(new[] { g1, g2 }, g1.Assets.Concat(g2.Assets), Array.Empty<AssetRecord>(), Array.Empty<GdtFile>(), null, problems);
            GdtSaveResult r;
            using (new FileStream(p2, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                r = NewSaveService("i-multi").Save(requests);
            Check($"interfere: two GDTs, one locked → neither written ({string.Join(", ", r.Files.Select(f => f.Status))})",
                r.Files.Count == 2 && r.Files[0].Status == GdtSaveStatus.NotWritten && r.Files[1].Status == GdtSaveStatus.Locked
                && Untouched(p1) && Untouched(p2));
        }

        // Deleted on disk since load.
        {
            var (g, p) = Setup();
            File.Delete(p);
            var r = SaveGdt(NewSaveService("i-gone"), g).Files[0];
            Check($"interfere: file deleted since load → '{r.Message}', not recreated", r.Status == GdtSaveStatus.Conflict && !File.Exists(p));
        }
    }

    private sealed class SimulatedCrash : Exception;

    private static void BackupChecks()
    {
        var dir = NewScratch("backups");
        var p = Path.Combine(dir, "b.gdt");
        File.WriteAllText(p, SmallGdt);
        var g = LoadGdt(p);
        var store = new BackupStore(Path.Combine(SaveRoot, "backups", "bounded"), keep: 10);
        var svc = new GdtSaveService(store);
        var versions = new List<string>();
        for (var i = 0; i < 13; i++)
        {
            versions.Add(File.ReadAllText(p));
            Edit(g.Assets[1], "filename", $"v{i}.xmodel_bin");
            SaveGdt(svc, g);
        }
        var list = store.List(p);
        Check($"backups: newest 10 kept per GDT ({list.Count}), newest is the version before the last save",
            list.Count == 10 && File.ReadAllText(list[0]) == versions[^1] && File.ReadAllText(list[^1]) == versions[^10]);

        // Restoring the previous version goes through the same save sequence (and is itself backed up).
        var current = File.ReadAllBytes(p);
        var restore = svc.Save(new[] { new GdtSaveRequest { Path = p, Expected = g.Stamp, Replacement = File.ReadAllBytes(list[0]) } }).Files[0];
        Check("backups: restoring the previous version writes it back and backs up the current one",
            restore.Status == GdtSaveStatus.Saved && File.ReadAllText(p) == versions[^1]
            && store.List(p) is { } after && File.ReadAllBytes(after[0]).AsSpan().SequenceEqual(current));
    }
}
