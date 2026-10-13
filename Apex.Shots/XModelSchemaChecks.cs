using System;
using System.Linq;
using Apex.Editor.Models;

namespace Apex.Shots;

/// <summary>
/// The mock xmodel schema's keys against the ones the linker reads (bb-linker src/conv/xmodel/plan.rs, build.rs). The
/// GDT lookup is case-sensitive, so each key is compared exactly.
/// </summary>
public partial class Program
{
    private static void RunXModelSchemaChecks(string outDir)
    {
        SchemaRegistry.ResetToMock();
        var schema = SchemaRegistry.Get("xmodel")!;
        var byKey = schema.Properties.ToDictionary(p => p.Key, StringComparer.Ordinal);
        PropertyDef? P(string k) => byKey.TryGetValue(k, out var v) ? v : null;

        Check("xmodel schema: keys are unique", byKey.Count == schema.Properties.Count);
        foreach (var old in new[] { "lodMedium", "lodLow", "lodBias", "bulletCollisionLOD" })
            Check($"xmodel schema: the old key {old} is gone", P(old) is null);

        string[] files = { "mediumLod", "lowLod", "lowestLod", "lod4File", "lod5File", "lod6File", "lod7File" };
        string[] rigid = { "forceMediumLodRigid", "forceLowLodRigid", "forceLowestLodRigid", "forceLod4Rigid", "forceLod5Rigid", "forceLod6Rigid", "forceLod7Rigid" };
        string[] auto = { "autogenMediumLod", "autogenLowLod", "autogenLowestLod", "autogenLod4", "autogenLod5", "autogenLod6", "autogenLod7" };
        Check("xmodel schema: filename and the 7 LOD file slots", P("filename") is not null && files.All(k => P(k)?.Kind == PropertyKind.Text));
        Check("xmodel schema: force-rigid and autogen toggles for every slot",
            rigid.Concat(auto).All(k => P(k)?.Kind == PropertyKind.Toggle));
        Check("xmodel schema: autogen percent for every slot (0-100)",
            auto.All(k => P(k + "Percent") is { Kind: PropertyKind.Number, Min: 0, Max: 100 }));

        Check("xmodel schema: customAutogenParams toggle", P("customAutogenParams")?.Kind == PropertyKind.Toggle);
        Check("xmodel schema: priority keys and defaults (32, 1/32, 1/16, 1/32)",
            P("lodPositionPriority")?.Default == "32" && P("lodNormalPriority")?.Default == "0.03125"
            && P("LodUvPriority")?.Default == "0.0625" && P("LodColorPriority")?.Default == "0.03125");
        Check("xmodel schema: bias keys", new[] { "LODBiasPercent", "LODBiasOffsetCP", "LODBiasOffsetMP" }.All(k => P(k)?.Kind == PropertyKind.Number));
        Check("xmodel schema: dropLOD and ShadowLOD list the 7 lower slots",
            new[] { "dropLOD", "ShadowLOD" }.All(k => P(k) is { Kind: PropertyKind.Choice } d
                && new[] { "Medium", "Low", "Lowest", "LOD4", "LOD5", "LOD6", "LOD7" }.All(c => d.Choices.Contains(c))));
        Check("xmodel schema: BulletCollisionLOD reaches LOD7 and Custom",
            P("BulletCollisionLOD") is { Kind: PropertyKind.Choice } b && new[] { "None", "High", "Lowest", "LOD7", "Custom" }.All(c => b.Choices.Contains(c)));
    }
}
