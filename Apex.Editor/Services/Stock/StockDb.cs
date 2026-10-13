using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Apex.Editor.Services.Stock;

/// <summary>One stock asset in a search result: its engine type and name, and the zones that define it (sorted, definitions of its own first).</summary>
public sealed record StockHit(string EngineType, string Name, IReadOnlyList<string> Zones)
{
    public string Kind => StockTypes.KindOfEngine(EngineType);

    /// <summary>The zone the row names: the first that defines the asset itself, else the first that holds it at all.</summary>
    public string Zone => Zones.Count > 0 ? Zones[0] : "";
}

/// <summary>What a stock search asks: the name words (all must be in the name), the kinds to keep (none: every kind) and the page.</summary>
public sealed record StockQuery(IReadOnlyList<string> Words, IReadOnlyList<string> Kinds, int Limit = 200, int Offset = 0)
{
    public string Key => string.Join(' ', Words) + "|" + string.Join(',', Kinds);
}

/// <summary>A search's answer: one page of hits, the hits of the kinds asked for, and every kind's count over the words alone (the facets).</summary>
public sealed record StockResult(IReadOnlyList<StockHit> Items, int Total, IReadOnlyDictionary<string, int> KindCounts);

/// <summary>What the index says about itself.</summary>
public sealed record StockMeta(string InstallPath, DateTimeOffset BuiltAt, int Zones, long Assets, string ReaderVersion);

/// <summary>
/// <c>stock.db</c> (docs/schema.md of the stock helper) opened read-only. Nothing here holds a transaction: every call opens
/// its own connection, runs its statements to the end and closes it, so the helper's refresh (which waits for open readers
/// for up to 30 s before it commits) is never kept waiting by Apex.
/// </summary>
public sealed class StockDb
{
    private readonly string _connection;
    private IReadOnlyList<string>? _engineTypes;
    private IReadOnlyList<string>? _materialFolders;
    private readonly ConcurrentDictionary<string, bool> _shipped = new(StringComparer.OrdinalIgnoreCase);

    public StockDb(string path)
    {
        Path = path;
        // A short wait for a lock: some reads run on the UI thread (shipped-name checks, a stock tab's header), and a
        // refresh that cannot rename the new index into place copies it in under a write lock for as long as the copy
        // takes. Failing fast lets those reads answer "not known" instead of freezing the window for the default 30 s.
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString();
    }

    public string Path { get; }

    /// <summary>The default database of an install: <c>%LOCALAPPDATA%\BO3ModTools\stock\&lt;install hash&gt;\stock.db</c>.</summary>
    public static string DefaultPath(string installPath)
    {
        var local = Environment.GetEnvironmentVariable("BO3MODTOOLS_STOCK_DIR");
        var root = local is { Length: > 0 }
            ? local
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BO3ModTools", "stock");
        return System.IO.Path.Combine(root, InstallHash(installPath), "stock.db");
    }

    /// <summary>FNV-1a 64 (16 hex digits) of the lower-cased install path without a trailing backslash, as the helper names the folder.</summary>
    public static string InstallHash(string installPath)
    {
        var text = installPath.Replace('/', '\\');
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal))
            text = text[4..];
        text = text.TrimEnd('\\').ToLowerInvariant();
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 0x100000001b3UL;
        }
        return hash.ToString("x16");
    }

    public bool Exists => File.Exists(Path);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connection);
        connection.Open();
        return connection;
    }

    /// <summary>The index's meta rows and zone count; null when there is no usable database yet.</summary>
    public StockMeta? Meta()
    {
        if (!Exists)
            return null;
        try
        {
            using var db = Open();
            var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT key, value FROM meta";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    meta[r.GetString(0)] = r.GetString(1);
            }
            var zones = Scalar(db, "SELECT COUNT(*) FROM zone");
            var assets = Scalar(db, "SELECT COUNT(*) FROM asset");
            return new StockMeta(meta.GetValueOrDefault("install_path", ""),
                DateTimeOffset.FromUnixTimeSeconds(long.TryParse(meta.GetValueOrDefault("built_at"), out var at) ? at : 0),
                (int)zones, assets, meta.GetValueOrDefault("reader_version", ""));
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            return null;
        }
    }

    private static long Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Every engine type the index holds.</summary>
    public IReadOnlyList<string> EngineTypes()
    {
        if (_engineTypes is { } known)
            return known;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT type FROM asset";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read())
            list.Add(r.GetString(0));
        return _engineTypes = list;
    }

    private HashSet<string>? _alwaysLoaded;

    /// <summary>The zones every map of their mode loads (the ones with an asset list: core_*, zm_levelcommon, mp_common, ...).</summary>
    public IReadOnlySet<string> AlwaysLoadedZones()
    {
        if (_alwaysLoaded is { } known)
            return known;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM zone WHERE has_assetlist = 1 AND lang IS NULL";
        using var r = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read())
            set.Add(r.GetString(0));
        return _alwaysLoaded = set;
    }

    /// <summary>The zones that define sound aliases and the bank languages there are (the sound view's filters); empty when the index has no sound tables yet.</summary>
    public (IReadOnlyList<string> Zones, IReadOnlyList<string> Langs) SoundFilters()
    {
        var zones = new List<string>();
        var langs = new List<string>();
        try
        {
            using var db = Open();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT z.name FROM sound_alias s JOIN zone z ON z.id = s.zone_id ORDER BY z.name COLLATE NOCASE";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    zones.Add(r.GetString(0));
            }
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT lang FROM sound_alias ORDER BY lang";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    langs.Add(r.GetString(0));
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // an index made before the sound tables: the helper's own answer says to index again
        }
        return (zones, langs);
    }

    /// <summary>How many assets of every kind the index holds (without the hidden LOD meshes).</summary>
    public int AssetCount()
    {
        // a full scan (~30 ms on the 460k-row index) that every search asked again; a refreshed index is a new StockDb
        if (_assetCount is { } known)
            return known;
        using var db = Open();
        return (_assetCount = (int)Scalar(db, $"SELECT COUNT(*) FROM asset WHERE type <> '{StockTypes.HiddenEngineType}'")).Value;
    }

    private int? _assetCount;

    // ── Search ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Assets whose name holds every word, best first (an exact name, then names that start with the first word, then the
    /// shorter, then by name), one page of them, with the count over all of them and each kind's count over the words alone.
    /// </summary>
    public StockResult Search(StockQuery query)
    {
        var words = query.Words.Where(w => w.Length > 0).ToList();
        using var db = Open();
        var (where, add) = NameFilter(words);

        // Facets: every kind's count over the words, whatever kind is picked.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"SELECT type, COUNT(*) FROM asset WHERE {where} GROUP BY type";
            add(cmd);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var kind = StockTypes.KindOfEngine(r.GetString(0));
                counts[kind] = counts.GetValueOrDefault(kind) + r.GetInt32(1);
            }
        }

        var engineTypes = EngineTypes();
        var picked = query.Kinds.Count == 0
            ? null
            : engineTypes.Where(t => query.Kinds.Contains(StockTypes.KindOfEngine(t))).ToList();
        var total = query.Kinds.Count == 0 ? counts.Values.Sum() : counts.Where(c => query.Kinds.Contains(c.Key)).Sum(c => c.Value);
        if (picked is { Count: 0 } || query.Limit <= 0)
            return new StockResult(Array.Empty<StockHit>(), total, counts);

        var typeFilter = picked is null ? "" : $" AND type IN ({string.Join(',', picked.Select((_, i) => "$t" + i))})";
        var first = words.Count > 0 ? words[0] : "";
        var rows = new List<(long Id, string Type, string Name)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = $"SELECT id, type, name FROM asset WHERE {where}{typeFilter} " +
                              "ORDER BY CASE WHEN name = $first THEN 0 WHEN name LIKE $prefix ESCAPE '\\' THEN 1 ELSE 2 END, length(name), name " +
                              "LIMIT $limit OFFSET $offset";
            add(cmd);
            cmd.Parameters.AddWithValue("$first", first);
            cmd.Parameters.AddWithValue("$prefix", Escape(first) + "%");
            cmd.Parameters.AddWithValue("$limit", query.Limit);
            cmd.Parameters.AddWithValue("$offset", query.Offset);
            if (picked is not null)
                for (var i = 0; i < picked.Count; i++)
                    cmd.Parameters.AddWithValue("$t" + i, picked[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        }
        return new StockResult(WithZones(db, rows), total, counts);
    }

    private static (string Where, Action<SqliteCommand> Add) NameFilter(IReadOnlyList<string> words)
    {
        var clauses = new List<string> { $"type <> '{StockTypes.HiddenEngineType}'" };
        for (var i = 0; i < words.Count; i++)
            clauses.Add($"name LIKE $w{i} ESCAPE '\\'");
        return (string.Join(" AND ", clauses), cmd =>
        {
            for (var i = 0; i < words.Count; i++)
                cmd.Parameters.AddWithValue("$w" + i, "%" + Escape(words[i]) + "%");
        });
    }

    /// <summary>A LIKE pattern for the text as written: underscore and percent are asset-name characters here, not wildcards.</summary>
    internal static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static List<StockHit> WithZones(SqliteConnection db, List<(long Id, string Type, string Name)> rows)
    {
        var zones = new Dictionary<long, List<(string Zone, bool Nested, bool Lang)>>();
        for (var from = 0; from < rows.Count; from += 400)
        {
            var chunk = rows.Skip(from).Take(400).ToList();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT d.asset_id, z.name, d.nested, z.lang IS NOT NULL FROM def d JOIN zone z ON z.id = d.zone_id " +
                              $"WHERE d.asset_id IN ({string.Join(',', chunk.Select((_, i) => "$a" + i))})";
            for (var i = 0; i < chunk.Count; i++)
                cmd.Parameters.AddWithValue("$a" + i, chunk[i].Id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                if (!zones.TryGetValue(id, out var list))
                    zones[id] = list = new List<(string, bool, bool)>();
                list.Add((r.GetString(1), r.GetInt32(2) != 0, r.GetInt32(3) != 0));
            }
        }
        return rows.Select(row => new StockHit(row.Type, row.Name, OrderedZones(zones.GetValueOrDefault(row.Id)))).ToList();
    }

    private static IReadOnlyList<string> OrderedZones(List<(string Zone, bool Nested, bool Lang)>? list) =>
        list is null
            ? Array.Empty<string>()
            : list.OrderBy(z => z.Lang).ThenBy(z => z.Nested).ThenBy(z => z.Zone, StringComparer.OrdinalIgnoreCase).Select(z => z.Zone).ToList();

    /// <summary>The zones that define one asset (the same order as in a search).</summary>
    public IReadOnlyList<string> ZonesOf(string engineType, string name)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT a.id FROM asset a WHERE a.type = $t AND a.name = $n";
        cmd.Parameters.AddWithValue("$t", engineType);
        cmd.Parameters.AddWithValue("$n", name);
        if (cmd.ExecuteScalar() is not long id)
            return Array.Empty<string>();
        return WithZones(db, new List<(long, string, string)> { (id, engineType, name) })[0].Zones;
    }

    /// <summary>
    /// The stock asset of a GDT type (or any of <paramref name="kinds"/> when the type is empty) and a name as a GDT writes it
    /// (a material without its techset folder); null when the index holds none. The first kind that has it wins.
    /// </summary>
    public StockHit? Find(string type, string name, IReadOnlyList<string> kinds)
    {
        if (name.Length == 0)
            return null;
        var wanted = type.Length > 0 ? new[] { StockTypes.KindOfApex(type) } : kinds;
        var engine = EngineTypes();
        using var db = Open();
        foreach (var kind in wanted)
            foreach (var engineType in StockTypes.EngineTypesOfKind(kind, engine))
            {
                var candidates = new List<string> { name };
                if (engineType == "MATERIAL")
                    candidates.AddRange(MaterialFolders(db).Select(f => f + name));
                foreach (var candidate in candidates)
                    if (ZonesOf(engineType, candidate) is { Count: > 0 } zones)
                        return new StockHit(engineType, candidate, zones);
            }
        return null;
    }

    /// <summary>The names among <paramref name="names"/> that the index also holds as an asset of <paramref name="kind"/> (a local asset hiding a stock one).</summary>
    public HashSet<string> NamesOfKind(string kind, IReadOnlyCollection<string> names)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var types = StockTypes.EngineTypesOfKind(kind, EngineTypes()).ToList();
        if (types.Count == 0 || names.Count == 0)
            return found;
        using var db = Open();
        foreach (var chunk in names.Chunk(150))
        {
            using var cmd = db.CreateCommand();
            var isMaterial = types.Contains("MATERIAL");
            var candidates = new List<string>();
            foreach (var n in chunk)
            {
                candidates.Add(n);
                if (isMaterial)
                    candidates.AddRange(MaterialFolders(db).Select(f => f + n));
            }
            var nameParams = string.Join(',', candidates.Select((_, i) => "$n" + i));
            var typeParams = string.Join(',', types.Select((_, i) => "$t" + i));
            cmd.CommandText = $"SELECT name FROM asset WHERE type IN ({typeParams}) AND name IN ({nameParams})";
            for (var i = 0; i < types.Count; i++)
                cmd.Parameters.AddWithValue("$t" + i, types[i]);
            for (var i = 0; i < candidates.Count; i++)
                cmd.Parameters.AddWithValue("$n" + i, candidates[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                found.Add(StockTypes.BareName(isMaterial ? "MATERIAL" : "", r.GetString(0)));
        }
        return found;
    }

    // ── Shipped-name checks ─────────────────────────────────────────────────

    /// <summary>
    /// True when the game ships a <paramref name="type"/> (a GDT type) named <paramref name="name"/> in a zone that every map
    /// loads (a zone with an asset list: core_*, the common zones): a reference to it links without any GDT.
    /// </summary>
    public bool Ships(string type, string name)
    {
        if (name.Length == 0)
            return false;
        var kind = KindOfRef(type);
        var key = kind + "\0" + name;
        if (_shipped.TryGetValue(key, out var known))
            return known;
        bool result;
        try
        {
            result = ShipsUncached(kind, name);
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            return false;
        }
        _shipped[key] = result;
        return result;
    }

    private static string KindOfRef(string type) => StockTypes.KindOfApex(type);

    private bool ShipsUncached(string kind, string name)
    {
        var types = StockTypes.EngineTypesOfKind(kind, EngineTypes()).ToList();
        if (types.Count == 0)
            return false;
        using var db = Open();
        var candidates = new List<string> { name };
        if (types.Contains("MATERIAL"))
            candidates.AddRange(MaterialFolders(db).Select(f => f + name));
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM asset a JOIN def d ON d.asset_id = a.id JOIN zone z ON z.id = d.zone_id " +
                          $"WHERE a.type IN ({string.Join(',', types.Select((_, i) => "$t" + i))}) " +
                          $"AND a.name IN ({string.Join(',', candidates.Select((_, i) => "$n" + i))}) AND z.has_assetlist = 1 LIMIT 1";
        for (var i = 0; i < types.Count; i++)
            cmd.Parameters.AddWithValue("$t" + i, types[i]);
        for (var i = 0; i < candidates.Count; i++)
            cmd.Parameters.AddWithValue("$n" + i, candidates[i]);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>The techset folders materials are listed under ("mc/", "mcs/", ...), read once.</summary>
    private IReadOnlyList<string> MaterialFolders(SqliteConnection db)
    {
        if (_materialFolders is { } known)
            return known;
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT substr(name, 1, instr(name, '/')) FROM asset WHERE type = 'MATERIAL' AND instr(name, '/') > 0";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read())
            list.Add(r.GetString(0));
        return _materialFolders = list;
    }
}
