using System;
using System.Collections.Generic;

namespace Apex.Editor.Services.Stock;

/// <summary>
/// What makes an <see cref="Models.AssetRecord"/> a stock asset: the engine type and name it has in the game's fastfiles
/// and the zones that define it, plus what has been read about it since it was opened.
/// </summary>
public sealed class StockOrigin
{
    public StockOrigin(string engineType, string name, IReadOnlyList<string> zones)
    {
        EngineType = engineType;
        Name = name;
        Zones = zones;
    }

    public string EngineType { get; }

    /// <summary>The name in the zone (a material keeps its techset folder: "mc/global_invisible").</summary>
    public string Name { get; }

    public IReadOnlyList<string> Zones { get; }

    public string Zone => Zones.Count > 0 ? Zones[0] : "";

    public string Kind => StockTypes.KindOfEngine(EngineType);

    /// <summary>What <c>describe</c> read: the fields, the deffile they belong to, and what could not be read. Null until read.</summary>
    public StockDescription? Description { get; set; }

    /// <summary>The dependency closure behind the "Uses" list and the info line. Null until read.</summary>
    public StockDeps? Deps { get; set; }

    /// <summary>The dependency walk is running (the Uses list says so); a note when it won't run or failed.</summary>
    public bool DepsLoading { get; set; }

    public string DepsNote { get; set; } = "";

    /// <summary>Why reading it failed (shown in place of its fields); empty when it did not.</summary>
    public string Problem { get; set; } = "";

    public bool Loading { get; set; } = true;
}
