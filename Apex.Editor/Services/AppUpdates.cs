using System.IO;
using System.Reflection;
using Gscode.Updates;

namespace Apex.Editor.Services;

/// <summary>What the shared updater (<see cref="Updater"/>) needs to know about Apex.</summary>
public static class AppUpdates
{
    /// <summary>The version in the csproj, without the build's commit hash.</summary>
    public static string CurrentVersion { get; } =
        typeof(AppUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppUpdates).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>
    /// Apex installs as bin\Apex.exe, or in place of the Asset Property Editor as bin\asseteditor_modtools.exe; either
    /// way the update goes to the exe that is running.
    /// </summary>
    public static UpdaterOptions Options { get; } = new()
    {
        ProductId = "apex",
        DisplayName = "Apex",
        Repository = "Blakintosh/apex",
        CurrentVersion = CurrentVersion,
        ExeBundlePath = "bin/Apex.exe",
        BundleFiles = ["bin/Apex.exe"],
        LogFile = Path.Combine(CrashGuard.LogDirectory, "updates.log"),
    };
}
