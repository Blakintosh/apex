using System.Globalization;

namespace Apex.Editor.Services;

/// <summary>
/// Windows' regional number format. The Black Ops III mod tools read and write numbers with the system's decimal
/// symbol, so a comma there (0,5) breaks many of them. Apex itself always reads and writes numbers invariantly.
/// </summary>
public static class RegionalFormat
{
    /// <summary>The decimal symbol is a comma (the user's override counts, not just the region's default).</summary>
    public static bool UsesDecimalComma(CultureInfo? culture = null) =>
        (culture ?? CultureInfo.CurrentCulture).NumberFormat.NumberDecimalSeparator == ",";
}
