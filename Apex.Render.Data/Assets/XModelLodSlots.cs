using System.Globalization;

namespace Apex.Render.Data.Assets;

/// <summary>
/// One LOD of an xmodel as the linker builds it (bb-linker <c>src/conv/xmodel/plan.rs::lod_list</c>): an authored LOD
/// reads its own file at 100%; an auto-generated one reads the previous LOD's file, decimated to <see cref="Percent"/>.
/// </summary>
/// <param name="Index">LOD index (0 = the high LOD).</param>
/// <param name="File">GDT file value (relative to <c>model_export</c>): for an auto LOD, the previous LOD's file.</param>
/// <param name="Percent">100 for an authored LOD, else the auto LOD percent (0-100).</param>
public readonly record struct XModelLodSlot(int Index, string File, int Percent, bool Autogen);

public static class XModelLodSlots
{
    /// <summary>File field, auto flag and auto percent of each slot, in LOD order (the case-sensitive GDT keys).</summary>
    private static readonly (string File, string Auto, string Pct)[] Slots =
    [
        ("filename", "", ""),
        ("mediumLod", "autogenMediumLod", "autogenMediumLodPercent"),
        ("lowLod", "autogenLowLod", "autogenLowLodPercent"),
        ("lowestLod", "autogenLowestLod", "autogenLowestLodPercent"),
        ("lod4File", "autogenLod4", "autogenLod4Percent"),
        ("lod5File", "autogenLod5", "autogenLod5Percent"),
        ("lod6File", "autogenLod6", "autogenLod6Percent"),
        ("lod7File", "autogenLod7", "autogenLod7Percent"),
    ];

    /// <summary>
    /// The LODs of an xmodel entry, up to the first slot that has neither a file nor auto-generate (the linker fails a
    /// model with a later LOD after such a gap; the preview just stops there). Never throws.
    /// </summary>
    public static IReadOnlyList<XModelLodSlot> Read(IReadOnlyDictionary<string, string> fields)
    {
        var list = new List<XModelLodSlot>();
        string previous = "";
        foreach (var (fileKey, autoKey, pctKey) in Slots)
        {
            bool auto = autoKey.Length > 0 && Int(fields, autoKey) != 0;
            string file;
            int percent;
            if (auto)
            {
                if (previous.Length == 0)
                    break;
                file = previous;
                percent = (int)Math.Clamp(Math.Round(Float(fields, pctKey)), 0, 100);
            }
            else
            {
                file = fields.TryGetValue(fileKey, out var v) ? v.Trim() : "";
                if (file.Length == 0)
                    break;
                percent = 100;
            }
            list.Add(new XModelLodSlot(list.Count, file, percent, auto));
            previous = file;
        }
        return list;
    }

    /// <summary>True when any LOD of <paramref name="fields"/> is auto-generated.</summary>
    public static bool HasAutogen(IReadOnlyDictionary<string, string> fields) => Read(fields).Any(s => s.Autogen);

    private static double Float(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static int Int(IReadOnlyDictionary<string, string> fields, string key) => (int)Float(fields, key);
}
