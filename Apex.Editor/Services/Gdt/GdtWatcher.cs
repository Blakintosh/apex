using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.Editor.Services.Gdt;

/// <summary>One settled burst of on-disk changes: .gdt (and .gdtx) paths that now exist changed, and paths removed.</summary>
public sealed record GdtChangeBatch(IReadOnlyList<string> Changed, IReadOnlyList<string> Deleted);

/// <summary>
/// Read-only live watcher over the GDT roots. Wraps a <see cref="FileSystemWatcher"/> per root
/// (<c>*.gdt</c> and their <c>*.gdtx</c> sidecars, recursive) and coalesces every event into one debounced batch, raising
/// <see cref="Changed"/> on a background thread (the ThreadPool timer callback) once the burst has
/// settled — a 300-file sync is one batch, not 300 callbacks. Consumers marshal to the UI thread
/// themselves. Never writes anything, and degrades gracefully: a missing root is skipped, a buffer
/// overflow rescans that root against a snapshot of its files, and consumer exceptions never tear
/// down the watcher.
/// </summary>
public sealed class GdtWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<WatchedRoot> _roots = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<WatchedRoot> _overflowed = new();
    private readonly object _gate = new();
    private readonly object _flushGate = new();
    private readonly Timer _timer;
    private readonly int _debounceMs;
    private readonly int _maxDelayMs;
    private long _burstStart;
    private bool _disposed;

    /// <summary>Raised (background thread, one batch at a time, in order) when a burst has settled.</summary>
    public event Action<GdtChangeBatch>? Changed;

    /// <param name="debounceMs">Quiet time after the last event before a batch is raised.</param>
    /// <param name="maxDelayMs">Upper bound from a burst's first event, so a steady trickle still flushes.</param>
    public GdtWatcher(int debounceMs = 500, int maxDelayMs = 2000)
    {
        _debounceMs = debounceMs;
        _maxDelayMs = maxDelayMs;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Watches all present GDT roots of the environment.</summary>
    public void WatchAll(GameEnvironment env)
    {
        Watch(env.SourceDataDir);
        Watch(env.XanimExportDir);
        Watch(env.ModelExportDir);
    }

    /// <summary>Starts watching a single directory recursively. No-op if it doesn't exist / fails.</summary>
    public void Watch(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;

        try
        {
            var root = new WatchedRoot(dir);
            var w = new FileSystemWatcher(dir, "*.gdt")
            {
                Filters = { "*" + Models.ExtensionSidecar.Extension },
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                    | NotifyFilters.Size | NotifyFilters.CreationTime,
                InternalBufferSize = 64 * 1024,
            };
            w.Changed += OnChanged;
            w.Created += OnChanged;
            w.Renamed += OnRenamed;
            w.Deleted += OnDeleted;
            w.Error += (_, _) => OnOverflow(root);
            w.EnableRaisingEvents = true;

            lock (_gate)
            {
                if (_disposed)
                {
                    w.Dispose();
                    return;
                }
                _watchers.Add(w);
                _roots.Add(root);
            }
        }
        catch (Exception)
        {
            // Platform/permission failure — live updates simply won't fire.
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Schedule(e.FullPath);

    /// <summary>A consumer couldn't read <paramref name="path"/> yet (still being written): report it again in the next batch.</summary>
    public void Retry(string path) => Schedule(path);

    private void OnDeleted(object sender, FileSystemEventArgs e) => Schedule(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Schedule(e.OldFullPath);
        Schedule(e.FullPath);
    }

    /// <summary>
    /// Buffer overflow ("too many changes at once") or the directory going away: individual events were
    /// lost, so the next batch rescans this root instead of silently going stale.
    /// </summary>
    private void OnOverflow(WatchedRoot root)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _overflowed.Add(root);
            Arm();
        }
    }

    private void Schedule(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".gdt", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(Models.ExtensionSidecar.Extension, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
        {
            if (_disposed)
                return;
            _pending.Add(path);
            Arm();
        }
    }

    /// <summary>(Re)starts the debounce, capped at <see cref="_maxDelayMs"/> from the burst's first event. Caller holds _gate.</summary>
    private void Arm()
    {
        var now = Environment.TickCount64;
        if (_burstStart == 0)
            _burstStart = now;
        var due = Math.Min(_debounceMs, _maxDelayMs - (now - _burstStart));
        _timer.Change(Math.Max(0, due), Timeout.Infinite);
    }

    private void Flush()
    {
        // One flush at a time, so batches reach the consumer in the order their events arrived.
        lock (_flushGate)
        {
            HashSet<string> pending;
            List<WatchedRoot> overflowed;
            lock (_gate)
            {
                if (_disposed)
                    return;
                pending = new HashSet<string>(_pending, StringComparer.OrdinalIgnoreCase);
                _pending.Clear();
                overflowed = new List<WatchedRoot>(_overflowed);
                _overflowed.Clear();
                _burstStart = 0;
            }

            foreach (var root in overflowed)
                root.Rescan(pending);

            var changed = new List<string>();
            var deleted = new List<string>();
            foreach (var path in pending)
            {
                // Reconcile against the real final state: a delete+recreate burst nets to a change, and vice versa.
                var exists = File.Exists(path);
                (exists ? changed : deleted).Add(path);
                RootOf(path)?.Record(path, exists);
            }
            if (changed.Count == 0 && deleted.Count == 0)
                return;

            try
            {
                Changed?.Invoke(new GdtChangeBatch(changed, deleted));
            }
            catch (Exception)
            {
                // A misbehaving consumer must not kill the watcher.
            }
        }
    }

    private WatchedRoot? RootOf(string path)
    {
        lock (_gate)
        {
            foreach (var r in _roots)
                if (r.Contains(path))
                    return r;
        }
        return null;
    }

    public void Dispose()
    {
        List<FileSystemWatcher> watchers;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            watchers = new List<FileSystemWatcher>(_watchers);
            _watchers.Clear();
            _pending.Clear();
            _overflowed.Clear();
        }

        foreach (var w in watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); }
            catch (Exception) { /* ignore */ }
        }
        _timer.Dispose();
    }

    /// <summary>
    /// A watched root plus a snapshot of its .gdt files (path → last write time), taken in the
    /// background when watching starts and kept current as batches flush. An overflow diffs a fresh
    /// walk of the root against it to recover the changes whose events were dropped.
    /// </summary>
    private sealed class WatchedRoot
    {
        private readonly string _dir;
        private readonly string _prefix;
        private readonly Task<Dictionary<string, DateTime>> _initial;
        private Dictionary<string, DateTime>? _files;

        public WatchedRoot(string dir)
        {
            _dir = dir;
            _prefix = Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar;
            _initial = Task.Run(() => Snapshot(dir));
        }

        public bool Contains(string path) => path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);

        private Dictionary<string, DateTime> Files => _files ??= _initial.Result;

        /// <summary>Notes a flushed path's current state so a later rescan doesn't report it again. Flush thread only.</summary>
        public void Record(string path, bool exists)
        {
            if (!exists)
                Files.Remove(path);
            else if (LastWrite(path) is { } t)
                Files[path] = t;
        }

        /// <summary>Adds every new, modified or vanished file under the root to <paramref name="pending"/>. Flush thread only.</summary>
        public void Rescan(HashSet<string> pending)
        {
            var before = Files;
            var now = Snapshot(_dir);
            foreach (var (path, t) in now)
                if (!before.TryGetValue(path, out var old) || old != t)
                    pending.Add(path);
            foreach (var path in before.Keys)
                if (!now.ContainsKey(path))
                    pending.Add(path);
            _files = now;
        }

        private static Dictionary<string, DateTime> Snapshot(string dir)
        {
            var files = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in GdtCatalog.SafeEnumerate(dir, SearchOption.AllDirectories)
                         .Concat(GdtCatalog.SafeEnumerate(dir, SearchOption.AllDirectories, "*" + Models.ExtensionSidecar.Extension)))
                if (LastWrite(path) is { } t)
                    files[path] = t;
            return files;
        }

        private static DateTime? LastWrite(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }
}
