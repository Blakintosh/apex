using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using GdtEncoding = Apex.Render.Data.Gdt.GdtEncoding;

namespace Apex.Editor.Services;

public enum TokenKind { Name, Type, Gdt, Prop, Modified, Problems, Changed }

/// <summary>
/// One parsed search token, e.g. <c>type:weapon</c> or <c>prop:damage>=100</c>. <paramref name="Exact"/> (type: and gdt:
/// only) matches the whole type or GDT name instead of any part of it: set for a quoted value, and by the caller for a
/// value that names a type or GDT that exists, so <c>type:weapon</c> is the weapons and not the weapon camos too.
/// </summary>
public sealed record QueryToken(TokenKind Kind, string Key, string Op, string Value, string Raw, bool Exact = false);

/// <summary>
/// Parses and evaluates the asset browser query language:
///   bare words        → asset name contains (AND)
///   type:weapon       → asset type (multiple type: tokens OR together); the whole type when quoted or marked exact
///   gdt:zm_castle     → source GDT name (OR); the whole file name when quoted or marked exact
///   prop:key          → asset has a property with that key
///   prop:key=v        → property equals; also != > < >= <= ~ (contains)
///   prop:=v           → any property equals (every operator works without a key)
///   is:modified       → any property deviates from its schema default (alias is:off-default)
///   is:changed        → edited in this session
/// </summary>
public static class AssetQuery
{
    private static readonly string[] Ops = { ">=", "<=", "!=", "=", ">", "<", "~" };

    public static List<QueryToken> Parse(string? text)
    {
        var tokens = new List<QueryToken>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        foreach (var raw in Tokenize(text))
        {
            var colon = raw.IndexOf(':');
            if (colon > 0 && colon < raw.Length - 1)
            {
                var prefix = raw[..colon].ToLowerInvariant();
                var quoted = raw[(colon + 1)..];
                var rest = Unquote(quoted);
                var exact = rest.Length < quoted.Length;
                switch (prefix)
                {
                    case "type" or "t":
                        tokens.Add(new QueryToken(TokenKind.Type, "", "", rest.ToLowerInvariant(), raw, exact));
                        continue;
                    case "gdt" or "g":
                        tokens.Add(new QueryToken(TokenKind.Gdt, "", "", rest.ToLowerInvariant(), raw, exact));
                        continue;
                    case "prop" or "p":
                        tokens.Add(ParseProp(rest, raw));
                        continue;
                    case "is" when rest.Equals("modified", StringComparison.OrdinalIgnoreCase)
                                   || rest.Equals("off-default", StringComparison.OrdinalIgnoreCase)
                                   || rest.Equals("offdefault", StringComparison.OrdinalIgnoreCase):
                        tokens.Add(new QueryToken(TokenKind.Modified, "", "", "", raw));
                        continue;
                    case "is" when rest.Equals("changed", StringComparison.OrdinalIgnoreCase):
                        tokens.Add(new QueryToken(TokenKind.Changed, "", "", "", raw));
                        continue;
                    case "is" or "has" when rest.Equals("problems", StringComparison.OrdinalIgnoreCase):
                        tokens.Add(new QueryToken(TokenKind.Problems, "", "", "", raw));
                        continue;
                }
            }
            tokens.Add(new QueryToken(TokenKind.Name, "", "", Unquote(raw).ToLowerInvariant(), raw));
        }
        return tokens;
    }

    private static QueryToken ParseProp(string body, string raw)
    {
        // No key before the operator ("prop:=1", "prop:~mg42") tests every property: any property = value.
        foreach (var op in Ops)
        {
            var i = body.IndexOf(op, StringComparison.Ordinal);
            if (i >= 0)
                return new QueryToken(TokenKind.Prop, body[..i], op, Unquote(body[(i + op.Length)..]), raw);
        }
        return new QueryToken(TokenKind.Prop, body, "", "", raw);
    }

    /// <summary>Splits a query string into raw tokens, honouring quoted values with spaces.</summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; current.Append(c); }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>"source_data/zm/zm_weapons.gdt" → "zm_weapons": the GDT's file name without its folder or extension.</summary>
    public static string ShortGdt(string gdtName)
    {
        var slash = gdtName.LastIndexOfAny(Slashes);
        var name = slash >= 0 ? gdtName[(slash + 1)..] : gdtName;
        return name.EndsWith(".gdt", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>"source_data/zm/zm_weapons.gdt" → "source_data/zm"; empty for a GDT at the root.</summary>
    public static string GdtFolder(string gdtName)
    {
        var slash = gdtName.LastIndexOfAny(Slashes);
        return slash > 0 ? gdtName[..slash] : "";
    }

    private static readonly char[] Slashes = { '/', '\\' };

    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    /// <summary>True when any token has to read property values (<c>prop:</c>/<c>is:modified</c>).</summary>
    public static bool TouchesProperties(IReadOnlyList<QueryToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].Kind is TokenKind.Prop or TokenKind.Modified)
                return true;
        return false;
    }

    /// <summary>
    /// Batched matcher for one GDT's asset list: opens a <see cref="GdtFileScan"/> scope so the
    /// property reads for <c>prop:</c>/<c>is:modified</c> tokens read the backing .gdt at most once
    /// (all assets in a single <see cref="GdtFile"/> share one source file) instead of once per asset.
    /// Behaviour is identical to calling <see cref="Matches"/> per record. The scope is per thread, so
    /// callers may run one MatchAll per GDT in parallel.
    /// </summary>
    /// <param name="extensions">An asset's extension values (its <c>.gdtx</c> blocks), which <c>prop:</c> also reads;
    /// null when none are installed or loaded.</param>
    public static List<AssetRecord> MatchAll(
        IReadOnlyList<AssetRecord> assets, IReadOnlyList<QueryToken> tokens,
        Func<AssetRecord, bool>? postGate = null, ExtensionValues? extensions = null)
    {
        var matcher = new Matcher(tokens, extensions);
        var result = new List<AssetRecord>();

        // No property-touching token → the cheap in-memory path; no file reads, no scope needed.
        using var scan = matcher.TouchesProperties ? GdtFileScan.Begin() : null;
        for (var i = 0; i < assets.Count; i++)
        {
            var a = assets[i];
            if (matcher.Matches(a) && (postGate is null || postGate(a)))
                result.Add(a);
        }
        return result;
    }

    /// <summary>
    /// Compiles <paramref name="tokens"/> once for a per-record loop. The predicate is not thread-safe;
    /// build one per scanning thread.
    /// </summary>
    public static Func<AssetRecord, bool> Compile(IReadOnlyList<QueryToken> tokens, ExtensionValues? extensions = null) =>
        new Matcher(tokens, extensions).Matches;

    /// <summary>An asset's extension blocks' values (one dictionary per extension); empty when it has none.</summary>
    public delegate IEnumerable<IReadOnlyDictionary<string, string>> ExtensionValues(AssetRecord asset);

    public static bool Matches(AssetRecord asset, IReadOnlyList<QueryToken> tokens) =>
        tokens.Count == 0 || new Matcher(tokens).Matches(asset);

    /// <summary>
    /// Transient property view for a record during a scan. When a <see cref="GdtFileScan"/> scope is
    /// active on this thread, reads route through its one-file-at-a-time cache (a whole file is read
    /// once and every asset in it reuses that buffer); otherwise it falls back to the record's own
    /// lazy <see cref="AssetRecord.ScanProperties"/>. Never retained on the record (laziness invariant).
    /// </summary>
    private static IReadOnlyDictionary<string, string> ScanProps(AssetRecord asset) =>
        GdtFileScan.Current?.ScanProperties(asset) ?? asset.ScanProperties;

    /// <summary>
    /// A query compiled for one batch. Per-token work (the lower-cased key bytes, the numeric operand)
    /// is done once, and tokens are evaluated cheapest first: name/is:changed/type/gdt from memory,
    /// then the property tokens that read the file — so <c>type:weapon prop:damage>100</c> never reads
    /// a non-weapon body. Every token is a pure AND (type:/gdt: are OR groups), so evaluation order
    /// cannot change the result. Records of one GDT share their type/GDT strings, so each OR group is
    /// re-evaluated only when the string instance changes.
    /// </summary>
    private sealed class Matcher
    {
        private readonly string[] _names;
        private readonly QueryToken[] _types;
        private readonly QueryToken[] _gdts;
        private readonly PropTest[] _props;
        private readonly bool _changed;
        private readonly ExtensionValues? _extensions;

        private string? _lastType;
        private bool _lastTypeHit;
        private string? _lastGdt;
        private bool _lastGdtHit;

        public Matcher(IReadOnlyList<QueryToken> tokens, ExtensionValues? extensions = null)
        {
            _extensions = extensions;
            var names = new List<string>();
            var types = new List<QueryToken>();
            var gdts = new List<QueryToken>();
            var props = new List<PropTest>();
            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                switch (t.Kind)
                {
                    case TokenKind.Name: names.Add(t.Value); break;
                    case TokenKind.Type: types.Add(t); break;
                    case TokenKind.Gdt: gdts.Add(t); break;
                    case TokenKind.Changed: _changed = true; break;
                    case TokenKind.Prop or TokenKind.Modified: props.Add(new PropTest(t)); break;
                    // Problems needs cross-asset knowledge (dangling refs); the caller post-filters.
                }
            }
            _names = names.ToArray();
            _types = types.ToArray();
            _gdts = gdts.ToArray();
            _props = props.ToArray();
        }

        public bool TouchesProperties => _props.Length > 0;

        public bool Matches(AssetRecord asset)
        {
            foreach (var n in _names)
                if (!asset.Name.Contains(n, StringComparison.OrdinalIgnoreCase))
                    return false;
            if (_changed && !asset.HasSessionEdits)
                return false;

            if (_types.Length > 0)
            {
                var type = asset.Type;
                if (!ReferenceEquals(type, _lastType))
                {
                    _lastType = type;
                    _lastTypeHit = AnyType(type, _types);
                }
                if (!_lastTypeHit)
                    return false;
            }
            if (_gdts.Length > 0)
            {
                var gdt = asset.GdtName;
                if (!ReferenceEquals(gdt, _lastGdt))
                {
                    _lastGdt = gdt;
                    _lastGdtHit = AnyGdt(gdt, _gdts);
                }
                if (!_lastGdtHit)
                    return false;
            }

            foreach (var p in _props)
                if (!(p.Token.Kind == TokenKind.Modified ? IsModified(asset) : MatchesProp(asset, p) || MatchesExtension(asset, p)))
                    return false;
            return true;
        }

        /// <summary><c>prop:</c> over the asset's extension values (weapon-tech's <c>wtKick1</c>…), as over its own.</summary>
        private bool MatchesExtension(AssetRecord asset, PropTest p)
        {
            if (_extensions is null)
                return false;
            foreach (var values in _extensions(asset))
                if (MatchesProp(values, p))
                    return true;
            return false;
        }

        private static bool AnyType(string type, QueryToken[] tokens)
        {
            foreach (var t in tokens)
                if (t.Exact ? type.Equals(t.Value, StringComparison.OrdinalIgnoreCase) : type.Contains(t.Value, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>An exact gdt: names the file (<c>zm_weapons</c>) or its whole path (<c>source_data/zm_weapons.gdt</c>).</summary>
        private static bool AnyGdt(string gdt, QueryToken[] tokens)
        {
            foreach (var t in tokens)
            {
                if (!t.Exact ? gdt.Contains(t.Value, StringComparison.OrdinalIgnoreCase)
                    : ShortGdt(gdt).Equals(t.Value, StringComparison.OrdinalIgnoreCase) || gdt.Equals(t.Value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    /// <summary>A <c>prop:</c> token with its per-query constants precomputed.</summary>
    private sealed class PropTest
    {
        public readonly QueryToken Token;

        /// <summary>Lower-cased ASCII key bytes for the raw-body scan; null for a non-ASCII key.</summary>
        public readonly byte[]? KeyLower;

        public readonly bool HasNumber;
        public readonly double Number;

        public PropTest(QueryToken t)
        {
            Token = t;
            if (t.Kind != TokenKind.Prop)
                return;
            if (IsAsciiKey(t.Key))
            {
                KeyLower = new byte[t.Key.Length];
                for (var i = 0; i < t.Key.Length; i++)
                {
                    var c = t.Key[i];
                    KeyLower[i] = (byte)(c is >= 'A' and <= 'Z' ? c + 32 : c);
                }
            }
            HasNumber = double.TryParse(t.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out Number);
        }
    }

    private static bool MatchesProp(AssetRecord asset, PropTest p)
    {
        // Fast path: during a GdtFileScan, evaluate directly over the asset's raw body bytes (read
        // once per file, cached) and decode a value only when its key matches — this skips building
        // the full ~180-entry dictionary and decoding every value, the crux of the sub-second scan.
        var scan = GdtFileScan.Current;
        if (scan is not null && p.KeyLower is not null)
        {
            var body = scan.GetBody(asset, out var hasBody);
            if (hasBody)
                return MatchesPropRaw(body, p);
        }

        // ScanProps: a bulk filter over ~95k assets must not permanently materialize the corpus.
        return MatchesProp(ScanProps(asset), p);
    }

    private static bool MatchesProp(IReadOnlyDictionary<string, string> values, PropTest p)
    {
        // Key can itself be a partial match ("prop:damage" hits damage, minDamage, meleeDamage…).
        var t = p.Token;
        var candidates = values.Where(kv => kv.Key.Contains(t.Key, StringComparison.OrdinalIgnoreCase));
        if (t.Op.Length == 0)
            return candidates.Any();

        foreach (var (_, value) in candidates)
        {
            if (MatchesValue(value, p))
                return true;
        }
        return false;
    }

    private static bool MatchesPropRaw(ReadOnlySpan<byte> body, PropTest p)
    {
        ReadOnlySpan<byte> needle = p.KeyLower;

        // Reused scratch: lowercase each raw key into it so the substring test is a single vectorized
        // ReadOnlySpan.IndexOf rather than a naive char-by-char case-insensitive scan.
        Span<byte> keyLower = stackalloc byte[256];

        var reader = new GdtPropertyReader(body);
        while (reader.MoveNext())
        {
            var key = reader.KeyRaw;
            bool keyMatch;
            if (key.Length <= keyLower.Length)
            {
                for (var i = 0; i < key.Length; i++)
                {
                    var h = key[i];
                    keyLower[i] = (byte)(h is >= (byte)'A' and <= (byte)'Z' ? h + 32 : h);
                }
                keyMatch = keyLower[..key.Length].IndexOf(needle) >= 0;
            }
            else
            {
                keyMatch = GdtParser.AsciiContainsIgnoreCase(key, needle);
            }

            if (!keyMatch)
                continue;
            if (p.Token.Op.Length == 0)
                return true; // existence check — a matching key is enough, never decode the value
            if (MatchesRawValue(reader.ValueRaw, p))
                return true;
        }
        return false;
    }

    /// <summary>
    /// <see cref="MatchesValue(ReadOnlySpan{char}, PropTest)"/> over a raw value: plain values are
    /// decoded into a stack buffer (the same chars <see cref="GdtParser.Decode"/> produces, with
    /// no string allocated); long values go through <see cref="GdtParser.Decode"/>.
    /// </summary>
    private static bool MatchesRawValue(ReadOnlySpan<byte> raw, PropTest p)
    {
        if (raw.Length > 256)
            return MatchesValue(GdtParser.Decode(raw), p);
        Span<char> chars = stackalloc char[raw.Length];
        var n = GdtEncoding.GetChars(raw, chars);
        return MatchesValue(chars[..n], p);
    }

    private static bool MatchesValue(ReadOnlySpan<char> value, PropTest p)
    {
        var t = p.Token;
        switch (t.Op)
        {
            case "=": return value.Equals(t.Value, StringComparison.OrdinalIgnoreCase);
            case "!=": return !value.Equals(t.Value, StringComparison.OrdinalIgnoreCase);
            case "~": return value.Contains(t.Value, StringComparison.OrdinalIgnoreCase);
        }
        if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var l) || !p.HasNumber)
            return false;
        var r = p.Number;
        return t.Op switch
        {
            ">" => l > r,
            "<" => l < r,
            ">=" => l >= r,
            "<=" => l <= r,
            _ => false,
        };
    }

    private static bool IsAsciiKey(string key)
    {
        foreach (var c in key)
            if (c > 127)
                return false;
        return true;
    }

    public static bool IsModified(AssetRecord asset)
    {
        var schema = SchemaRegistry.Get(asset.Type);
        if (schema is null)
            return false;

        // Fast path: raw-body scan (value decoded only for schema-known keys); no dictionary built.
        var scan = GdtFileScan.Current;
        if (scan is not null)
        {
            var body = scan.GetBody(asset, out var hasBody);
            if (hasBody)
            {
                var defaults = SchemaDefaults.For(schema);
                var reader = new GdtPropertyReader(body);
                while (reader.MoveNext())
                    if (defaults.IsOffDefault(reader.KeyRaw, reader.ValueRaw))
                        return true;
                return false;
            }
        }

        // ScanProps: is:modified filters the whole corpus and must not retain it materialized.
        foreach (var (key, value) in ScanProps(asset))
        {
            var def = schema.Find(key);
            if (def is not null && !ValuesEqual(value, def.Default))
                return true;
        }
        return false;
    }

    public static bool ValuesEqual(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return true;
        return double.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out var da)
            && double.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var db)
            && Math.Abs(da - db) < 1e-9;
    }

    /// <summary>
    /// A schema's defaults keyed for the raw is:modified scan: looked up by a stack-decoded key span
    /// (no string per property) with each default's numeric parse done once. Same answers as
    /// <see cref="AssetSchema.Find"/> + <see cref="ValuesEqual"/>. Built once per schema instance.
    /// </summary>
    private sealed class SchemaDefaults
    {
        private static readonly ConditionalWeakTable<AssetSchema, SchemaDefaults> Cache = new();

        private readonly Dictionary<string, DefaultValue>.AlternateLookup<ReadOnlySpan<char>> _byKey;

        private SchemaDefaults(AssetSchema schema)
        {
            var map = schema.Properties.ToDictionary(
                p => p.Key, p => new DefaultValue(p.Default), StringComparer.OrdinalIgnoreCase);
            _byKey = map.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        public static SchemaDefaults For(AssetSchema schema) =>
            Cache.GetValue(schema, static s => new SchemaDefaults(s));

        public bool IsOffDefault(ReadOnlySpan<byte> keyRaw, ReadOnlySpan<byte> valueRaw)
        {
            DefaultValue? def;
            if (keyRaw.Length <= 128)
            {
                Span<char> key = stackalloc char[keyRaw.Length];
                var n = GdtEncoding.GetChars(keyRaw, key);
                if (!_byKey.TryGetValue(key[..n], out def))
                    return false;
            }
            else if (!_byKey.Dictionary.TryGetValue(GdtParser.Decode(keyRaw), out def))
            {
                return false;
            }

            if (valueRaw.Length > 256)
                return !def.Matches(GdtParser.Decode(valueRaw));
            Span<char> value = stackalloc char[valueRaw.Length];
            var m = GdtEncoding.GetChars(valueRaw, value);
            return !def.Matches(value[..m]);
        }
    }

    private sealed class DefaultValue
    {
        private readonly string _text;
        private readonly bool _isNumber;
        private readonly double _number;

        public DefaultValue(string text)
        {
            _text = text;
            _isNumber = double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _number);
        }

        /// <summary><see cref="ValuesEqual"/>(<paramref name="value"/>, default).</summary>
        public bool Matches(ReadOnlySpan<char> value) =>
            value.Equals(_text, StringComparison.OrdinalIgnoreCase)
            || (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                && _isNumber
                && Math.Abs(d - _number) < 1e-9);
    }
}
