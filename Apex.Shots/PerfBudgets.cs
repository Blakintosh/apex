namespace Apex.Shots;

/// <summary>
/// The interaction budgets as the perf gates enforce them. Every threshold the gates check lives here and nowhere else;
/// when a budget changes, change it here in the same commit.
/// </summary>
internal static class PerfBudgets
{
    /// <summary>Keystroke, click, toggle, drag tick → visible response: one frame.</summary>
    public const double Frame = 16;

    /// <summary>Arrow through the asset list → selection moves, per step.</summary>
    public const double ArrowStep = 16;

    /// <summary>Open an asset → editor rows visible. The ceiling, not the target.</summary>
    public const double OpenAsset = 200;

    /// <summary>What open-asset aims for; reported beside the ceiling, not gated.</summary>
    public const double OpenAssetTarget = 100;

    /// <summary>
    /// Back to something already open or recently previewed: a tab switch (its editor exists, only its rows realize)
    /// and re-opening a recently previewed model (the prepared model is cached).
    /// </summary>
    public const double Reopen = 50;

    /// <summary>
    /// The longest the UI thread may be held by work that isn't a single keystroke: the Explorer's debounced filter
    /// rebuild and its clear. Anything that can take longer belongs off the UI thread.
    /// </summary>
    public const double UiChunk = 50;

    /// <summary>
    /// The median must meet the budget; the 95th percentile may run over it by this factor before the gate fails.
    /// The slack absorbs GC pauses and other processes on a shared machine without hiding a real regression, which moves
    /// the median.
    /// </summary>
    public const double P95Slack = 2.0;

    // ── Startup on the real install (launch → …), proposed from measurement; see docs/perf-optimization-spec.md ──

    /// <summary>Launch → the first GDT groups are on screen in the Explorer.</summary>
    public const double StartupFirstGroups = 1500;

    /// <summary>Launch → an asset can be opened with its real schema (deffiles parsed, its GDT indexed).</summary>
    public const double StartupFirstOpenable = 2000;
}
