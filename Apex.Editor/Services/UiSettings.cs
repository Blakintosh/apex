using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Apex.Editor.Services;

/// <summary>
/// Per-user workspace preferences: pane sizes and visibility, explorer grouping,
/// pinned and recent assets. Stored as JSON under %APPDATA%\Apex. These are conveniences, never
/// asset data — a missing or unreadable file silently yields defaults. Persistence is disabled when
/// APEX_FORCE_MOCK is set so the headless screenshot harness stays deterministic, and when
/// APEX_NO_PERSIST is set so the real-install perf gates neither read nor overwrite the user's file.
/// </summary>
public sealed class UiSettings
{
    public double ExplorerWidth { get; set; } = 248;
    public double RightColumnWidth { get; set; } = 352;
    /// <summary>The docked preview's height once the user has set one; null follows the column's width at 16:9.</summary>
    public double? PreviewPaneHeight { get; set; }
    public bool ExplorerVisible { get; set; } = true;
    public bool InspectorVisible { get; set; } = true;
    public string Grouping { get; set; } = "Gdt";
    /// <summary>The theme (<see cref="ThemeChoice"/>): System follows Windows.</summary>
    public string Theme { get; set; } = nameof(ThemeChoice.System);
    /// <summary>The preview's lighting state for every preview (APE: QSettings Preview/LightState).</summary>
    public string PreviewLightState { get; set; } = "Morning";
    /// <summary>The preview is popped out to its own window, and where that window was (null: not placed yet).</summary>
    public bool PreviewFloating { get; set; }
    public WindowPlacement? PreviewWindow { get; set; }
    public List<string> Pinned { get; set; } = new();
    /// <summary>The Black Ops III folder chosen with Locate… (null: detect it). Tried before detection on every launch.</summary>
    public string? Bo3Root { get; set; }
    public List<RecentEntry> Recent { get; set; } = new();
    /// <summary>The height of the dock under an asset's preview, per asset type (the xanim's notetracks dock).</summary>
    public Dictionary<string, double> DockHeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Asset types whose dock is folded to its transport row.</summary>
    public List<string> DocksCollapsed { get; set; } = new();
    /// <summary>When the automatic update check last ran; it runs at most once a day.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    public sealed class RecentEntry
    {
        public string Name { get; set; } = "";
        public DateTime OpenedUtc { get; set; }
    }

    /// <summary>A window's position (screen pixels) and size (DIPs) while not maximized, and whether it was maximized.</summary>
    public sealed class WindowPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>APEX_SETTINGS_DIR (harness): keep the file in this folder instead, and keep it even in mock or no-persist runs.</summary>
    private static string? OverrideDir =>
        Environment.GetEnvironmentVariable("APEX_SETTINGS_DIR") is { Length: > 0 } dir ? dir : null;

    public static bool IsPersistent => OverrideDir is not null
        || (Environment.GetEnvironmentVariable("APEX_FORCE_MOCK") != "1" && Environment.GetEnvironmentVariable("APEX_NO_PERSIST") != "1");

    /// <summary>Where per-user files live (this one); null in runs that may keep none.</summary>
    public static string? Folder => IsPersistent
        ? OverrideDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apex")
        : null;

    /// <summary>
    /// Per-user files that must not be supplied by anything unpacked into %AppData%\Apex (module consent): this machine's
    /// %LocalAppData%\Apex, or APEX_SETTINGS_DIR; null in runs that may keep none.
    /// </summary>
    public static string? LocalFolder => IsPersistent
        ? OverrideDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex")
        : null;

    private static string FilePath => Path.Combine(Folder ?? "", "ui.json");

    public static UiSettings Load()
    {
        if (!IsPersistent)
            return new UiSettings();
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Json) ?? new UiSettings();
        }
        catch
        {
            // A corrupt preferences file must never block startup.
        }
        return new UiSettings();
    }

    /// <summary>Writes the file now, on the calling thread (shutdown flush).</summary>
    public void Save() => Write(Serialize());

    /// <summary>A snapshot of the current values, taken on the thread that owns them.</summary>
    public string Serialize() => JsonSerializer.Serialize(this, Json);

    private static readonly object WriteLock = new();
    private static long _lastWritten;
    private static long _nextVersion;

    /// <summary>
    /// Writes a snapshot from <see cref="Serialize"/>; safe from any thread. Snapshots are numbered
    /// when taken, so a slow older write can never land on top of a newer one.
    /// </summary>
    public static void Write(string json) => Write(json, System.Threading.Interlocked.Increment(ref _nextVersion));

    /// <summary>Numbers a snapshot for a write that happens later, e.g. on the thread pool.</summary>
    public static Action WriteLater(string json)
    {
        var version = System.Threading.Interlocked.Increment(ref _nextVersion);
        return () => Write(json, version);
    }

    private static void Write(string json, long version)
    {
        if (!IsPersistent)
            return;
        lock (WriteLock)
        {
            if (version <= _lastWritten)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, json);
                _lastWritten = version;
            }
            catch
            {
                // Preferences are best-effort.
            }
        }
    }
}
