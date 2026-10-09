using System;
using System.Collections.Generic;

namespace Apex.Editor.Services.Save;

/// <summary>
/// Which asset in a GDT a record came from: its name and body offset in the bytes of the file's current stamp.
/// The offset tells apart two assets with the same name in one file; the name finds the asset again after the file
/// changed underneath (offsets moved).
/// </summary>
public readonly record struct DiskRef(string Name, string? Parent, long BodyOffset);

/// <summary>
/// One asset's part in a save. An existing asset (<see cref="Disk"/> set) carries only what the session changed: its
/// header (name, parent) and <see cref="Set"/>, the keys whose value changed (null: removed). Everything else in its
/// body stays as the file has it, including changes other programs made. A new asset carries all its values.
/// </summary>
public sealed class AssetEdit
{
    public DiskRef? Disk { get; init; }
    public bool Delete { get; init; }

    /// <summary>The asset's name after the save.</summary>
    public required string Name { get; init; }

    /// <summary>gdf type (without <c>.gdf</c>) of a root asset.</summary>
    public string Type { get; init; } = "";

    /// <summary>Derivation parent; null for a root asset.</summary>
    public string? Parent { get; init; }

    /// <summary>Existing asset: changed keys and their new values (null: remove the key).</summary>
    public IReadOnlyDictionary<string, string?>? Set { get; init; }

    /// <summary>
    /// Existing asset: the values the session's changes were made against (the asset as Apex read it). A changed key
    /// whose value on disk no longer matches is a conflict. Null when unknown (the asset was never read into memory).
    /// </summary>
    public IReadOnlyDictionary<string, string>? Baseline { get; init; }

    /// <summary>New asset: every value, written in APE's key order.</summary>
    public IReadOnlyDictionary<string, string>? Full { get; init; }

    /// <summary>The caller's object for this asset (the app's record), carried through to the result.</summary>
    public object? Tag { get; init; }

    public bool IsNew => Disk is null && !Delete;
}

public enum AssetConflictKind
{
    /// <summary>A value the session changed was also changed on disk, to something else.</summary>
    ValuesChanged,
    /// <summary>The asset the session changed is no longer in the file.</summary>
    MissingOnDisk,
    /// <summary>The file now has several assets by this name, so Apex can't tell which one was edited.</summary>
    Ambiguous,
    /// <summary>A new or renamed asset's name is already used in the file.</summary>
    NameTaken,
    /// <summary>An asset deleted in the session was changed on disk since Apex read it.</summary>
    ChangedBeforeDelete,
}

/// <summary>A key in conflict: the value Apex read (<see cref="Was"/>), the file's now, and the session's.</summary>
public sealed record KeyConflict(string Key, string? Was, string? OnDisk, string? Mine);

public sealed record AssetConflict(AssetEdit Edit, AssetConflictKind Kind, IReadOnlyList<KeyConflict> Keys)
{
    public string Asset => Edit.Disk?.Name ?? Edit.Name;
}

/// <summary>APE's key order: case-insensitive, lowercasing ASCII (so <c>_</c> sorts before letters), as its GDTs show.</summary>
public sealed class ApeKeyComparer : IComparer<string>
{
    public static readonly ApeKeyComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var n = Math.Min(x.Length, y.Length);
        for (var i = 0; i < n; i++)
        {
            var a = Lower(x[i]);
            var b = Lower(y[i]);
            if (a != b)
                return a - b;
        }
        return x.Length - y.Length;
    }

    internal static char Lower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
}

/// <summary>
/// A <c>.gdtx</c>'s key order: APE's, except that a run of digits compares as a number, so a record list's keys
/// (wtKick1, wtKick2 … wtKick10) sit in row order. weapon-tech keeps them in file order, and APE's order would put
/// wtKick10 before wtKick2. APE never writes a <c>.gdtx</c>, so its order needn't hold there.
/// </summary>
public sealed class ExtensionKeyComparer : IComparer<string>
{
    public static readonly ExtensionKeyComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length)
                    return a.Length - b.Length;
                var c = a.SequenceCompareTo(b);
                if (c != 0)
                    return c;
                // Same number: the shorter spelling (1 before 01) first, so the order is still total.
                if (i - si != j - sj)
                    return (i - si) - (j - sj);
                continue;
            }
            var l = ApeKeyComparer.Lower(x[i]);
            var r = ApeKeyComparer.Lower(y[j]);
            if (l != r)
                return l - r;
            i++;
            j++;
        }
        return (x.Length - i) - (y.Length - j);
    }
}
