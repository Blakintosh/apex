namespace Apex.Render.Data.Conversion.XModel;

/// <summary>The material names an xmodel_bin's surfaces use (what an xmodel's skinOverride replaces).</summary>
public static class XModelMaterials
{
    /// <summary>The model's material names, sorted and distinct. Reads the whole file: call off the UI thread.</summary>
    public static string[] Load(string xmodelBinPath) =>
        XModelSource.Load(xmodelBinPath).Materials
            .Select(XModelSource.NameOf)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
