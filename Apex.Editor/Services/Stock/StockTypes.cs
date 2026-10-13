using System;
using System.Collections.Generic;

namespace Apex.Editor.Services.Stock;

/// <summary>
/// The two spellings of an asset's type: the engine's (<c>stock.db</c>: XMODEL, ATTACHMENT_UNIQUE, XANIMPARTS) and the
/// GDT's (<c>xmodel</c>, <c>attachmentunique</c>, <c>xanim</c>, or one of the weapon deffiles). A <em>kind</em> is the
/// spelling both sides can be compared in: lower case, no underscores, every weapon deffile "weapon".
/// </summary>
public static class StockTypes
{
    /// <summary>Embedded LOD meshes: part of their model, never an asset anyone browses (about a third of the index).</summary>
    public const string HiddenEngineType = "XMODELMESH";

    /// <summary>The kind of an engine type: <c>XANIMPARTS</c> is "xanim", <c>ATTACHMENT_UNIQUE</c> "attachmentunique".</summary>
    public static string KindOfEngine(string engineType)
    {
        var kind = engineType.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return kind == "xanimparts" ? "xanim" : kind;
    }

    /// <summary>The kind of a GDT type: every weapon deffile is "weapon", the rest are their own names.</summary>
    public static string KindOfApex(string apexType)
    {
        var kind = apexType.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        return kind.EndsWith("weapon", StringComparison.Ordinal) ? "weapon" : kind;
    }

    /// <summary>
    /// The type a stock row is shown as. It is the kind (weapon, xmodel, xanim, material); once a record's fields are
    /// read the weapon deffile ("bulletweapon") takes its place, which is what the schema is looked up by.
    /// </summary>
    public static string DisplayType(string engineType) => KindOfEngine(engineType);

    /// <summary>
    /// A name as a GDT writes it. A stock material is listed under its techset folder ("mc/global_invisible"); the GDT, and
    /// every reference to it, leaves the folder off.
    /// </summary>
    public static string BareName(string engineType, string name)
    {
        if (!engineType.Equals("MATERIAL", StringComparison.OrdinalIgnoreCase))
            return name;
        var slash = name.IndexOf('/');
        return slash > 0 ? name[(slash + 1)..] : name;
    }

    /// <summary>The engine types a GDT type can be (the kind compared both ways), over the engine types the index holds.</summary>
    public static IEnumerable<string> EngineTypesOfKind(string kind, IEnumerable<string> engineTypes)
    {
        foreach (var engine in engineTypes)
            if (KindOfEngine(engine) == kind)
                yield return engine;
    }
}
