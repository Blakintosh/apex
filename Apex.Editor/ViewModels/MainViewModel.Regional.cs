using System;
using System.ComponentModel;
using System.Diagnostics;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>
/// A comma as Windows' decimal symbol breaks many of the mod tools (they read 0,5 wrongly), so a machine set that way
/// is told once at start. Apex itself is unaffected: it reads and writes numbers invariantly.
/// </summary>
public sealed partial class MainViewModel
{
    public const string DecimalCommaWarning =
        "Windows is set to use a comma as the decimal symbol. Many Black Ops III mod tools misread numbers that way.";

    private const string DecimalCommaDetail =
        "Set the decimal symbol to a full stop (.) in Region > Additional settings, then restart the mod tools. "
        + "Apex reads and writes numbers the same either way, but the game's tools do not, and values can come out wrong in assets and builds.";

    /// <summary>After the window opens: a banner when the decimal symbol is a comma, with a way to the setting.</summary>
    public void WarnIfDecimalComma()
    {
        if (!RegionalFormat.UsesDecimalComma())
            return;
        Alert(DecimalCommaWarning, isError: true, actionLabel: "Open Region settings", action: OpenRegionSettings, detail: DecimalCommaDetail);
    }

    private static void OpenRegionSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "intl.cpl") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // The banner has already said what to change; opening the panel is a convenience.
        }
    }
}
