using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using static Apex.Editor.Services.Extensions.Simulation.SimulatorNative;

namespace Apex.Editor.Services.Extensions.Simulation;

/// <summary>
/// Loads extensions' preview-simulator modules (docs/plugin-abi/apex_sim.h) when, and only when, a preview first asks
/// for one: never at startup, never on the save path. Per module, once a session: check the path again, hash the
/// file, ask the user unless they answered for that hash (<see cref="ISimulatorConsent"/>, remembered in
/// <see cref="SimulatorConsentStore"/>), then load exactly the file that was hashed, by full path, refusing one that
/// needs any DLL but Windows' own (<see cref="ModuleImage"/>); check its exports, ABI version and id. Every failure is
/// a plain sentence (a result's Message, and once a session a <see cref="Reported"/> diagnostic), never an exception.
///
/// A module runs in Apex's process: an access violation inside it can't be caught by .NET and ends Apex. To limit what
/// that costs, the session journal is flushed to disk before a module is loaded (DllMain is its first code) and before
/// every create (the call that sees new values first), so unsaved edits survive it as they would a power cut. Steps
/// aren't preceded by a flush: they see no new values, and one runs every frame. A loaded module is freed only when the
/// host is disposed (app exit), after every simulation it still has is destroyed: freeing code that may still be
/// running is worse than keeping it.
///
/// UI thread only: every call checks, and throws <see cref="InvalidOperationException"/> from any other thread
/// before touching a module (the ABI promises modules one thread).
/// </summary>
public sealed class SimulatorHost : IDisposable
{
    /// <summary>Failed steps in a row (across a module's simulations) after which the module is off until restart.</summary>
    public const int MaxFailuresInARow = 10;

    /// <summary>
    /// A step slower than this held Apex's window for three frames. Nothing can interrupt a call into a module (a hang
    /// hangs Apex), so this only counts and names it: a slow step counts with the failed ones.
    /// </summary>
    public const double SlowStepMs = 50;

    /// <summary>A create slower than this froze Apex's window for a second (it runs on every edit, after 150 ms).</summary>
    public const double SlowCreateMs = 1000;

    /// <summary>Far larger than a simulator should be: a bigger file isn't hashed or loaded.</summary>
    public const long MaxModuleBytes = 64L << 20;

    private readonly ISimulatorConsent _consent;
    private readonly SimulatorConsentStore _store;
    private readonly Action? _beforeNativeCall;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ExtensionDiagnostic> _diagnostics = new();
    private bool _disposed;

    /// <param name="beforeNativeCall">Run just before a module is loaded and before every create (the app flushes the
    /// session journal to disk here).</param>
    public SimulatorHost(ISimulatorConsent consent, SimulatorConsentStore store, Action? beforeNativeCall = null)
    {
        _consent = consent;
        _store = store;
        _beforeNativeCall = beforeNativeCall;
    }

    /// <summary>A module failed to load or was turned off: the diagnostic (said once per module and session) and the
    /// technical detail for a tooltip.</summary>
    public event Action<ExtensionDiagnostic, string>? Reported;

    /// <summary>An extension's module changed status (loaded, declined, failed, turned off): a preview showing it asks again.</summary>
    public event Action<string>? StatusChanged;

    public IReadOnlyList<ExtensionDiagnostic> Diagnostics => _diagnostics;

    private sealed class Entry
    {
        public SimulatorStatus Status = SimulatorStatus.NotLoaded;
        public string? Message;
        public Task<SimulatorModuleResult>? Pending;
        public NativeSimulatorModule? Module;

        /// <summary>Kept once loaded, even when a check after loading failed: never freed before the host is.</summary>
        public LibraryHandle? Handle;

        /// <summary>The hash last answered "Don't load" for, so asking again can forget it.</summary>
        public string? Declined;

        /// <summary>Who waits on the open under way; null when none is.</summary>
        public Waiters? Waiting;
    }

    /// <summary>
    /// The asks sharing one open. The question is for them: once every ask that can stop waiting has (the previews
    /// asking closed), it is withdrawn. An ask that can't stop waiting (no token) keeps it until it is answered.
    /// </summary>
    private sealed class Waiters
    {
        public int Count;
        public bool Unbounded;
        public readonly CancellationTokenSource Withdraw = new();
        public readonly List<CancellationTokenRegistration> Registrations = new();
    }

    private static void Wait(Entry e, CancellationToken waiting)
    {
        if (e.Waiting is not { } w)
            return;
        if (!waiting.CanBeCanceled)
        {
            w.Unbounded = true;
            return;
        }
        w.Count++;
        w.Registrations.Add(waiting.Register(() =>
        {
            if (ReferenceEquals(e.Waiting, w) && --w.Count == 0 && !w.Unbounded)
                w.Withdraw.Cancel();
        }));
    }

    public SimulatorStatus StatusOf(ExtensionManifest extension)
    {
        VerifyThread();
        return extension.Simulator is null ? SimulatorStatus.None
            : _entries.TryGetValue(extension.Id, out var e) ? e.Status : SimulatorStatus.NotLoaded;
    }

    /// <summary>The plain sentence for the module's status (null while there's nothing to say).</summary>
    public string? MessageOf(ExtensionManifest extension)
    {
        VerifyThread();
        return _entries.TryGetValue(extension.Id, out var e) ? e.Message : null;
    }

    /// <summary>
    /// The extension's module, loading it on the first ask (which may ask the user). Asks made while one is under way
    /// share it. A later ask returns at once with what the first found. Never throws for anything the file or the
    /// module does.
    /// </summary>
    /// <param name="waiting">Cancelled when the asker stops waiting (its preview closed): when no one is left waiting,
    /// the question is withdrawn and nothing is remembered.</param>
    public Task<SimulatorModuleResult> GetModuleAsync(ExtensionManifest extension, CancellationToken waiting = default)
    {
        VerifyThread();
        if (_disposed || extension.Simulator is null)
            return Task.FromResult(new SimulatorModuleResult(null, SimulatorStatus.None, null));
        if (!_entries.TryGetValue(extension.Id, out var e))
            _entries[extension.Id] = e = new Entry();
        switch (e.Status)
        {
            case SimulatorStatus.Pending:
                Wait(e, waiting);
                return e.Pending!;
            case SimulatorStatus.Loaded:
                return Task.FromResult(new SimulatorModuleResult(e.Module, SimulatorStatus.Loaded, null));
            case SimulatorStatus.NotLoaded:
                e.Status = SimulatorStatus.Pending;
                e.Waiting = new Waiters();
                Wait(e, waiting);
                var open = OpenAsync(extension, e);
                if (!open.IsCompleted)
                    e.Pending = open;
                return open;
            default:
                return Task.FromResult(new SimulatorModuleResult(null, e.Status, e.Message));
        }
    }

    /// <summary>A simulation for <paramref name="request"/>: <see cref="GetModuleAsync"/>, then the module's create.</summary>
    public async Task<SimulationResult> CreateAsync(SimulatorRequest request, CancellationToken waiting = default)
    {
        var module = await GetModuleAsync(request.Extension, waiting);
        if (module.Module is null)
            return new SimulationResult(null, module.Status, module.Message);
        VerifyThread();
        if (_disposed)
            return new SimulationResult(null, SimulatorStatus.None, null);
        if (!module.Module.IsTurnedOff)
            _beforeNativeCall?.Invoke();
        return module.Module.Create(request.Values);
    }

    /// <summary>
    /// "Load module…" beside a preview that is off because the user declined, or because the file couldn't be used
    /// (fixed since?): forgets the answer for that build and asks again. A module that loaded stays as it is.
    /// </summary>
    public Task<SimulatorModuleResult> ReconsiderAsync(ExtensionManifest extension, CancellationToken waiting = default)
    {
        VerifyThread();
        if (_entries.TryGetValue(extension.Id, out var e) && e.Handle is null
            && e.Status is SimulatorStatus.Declined or SimulatorStatus.Failed)
        {
            if (e.Declined is { } sha)
                _store.Forget(extension.Id, sha);
            _entries.Remove(extension.Id);
        }
        return GetModuleAsync(extension, waiting);
    }

    private async Task<SimulatorModuleResult> OpenAsync(ExtensionManifest extension, Entry e)
    {
        SimulatorModuleResult result;
        try
        {
            result = await OpenCoreAsync(extension, e);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // The boundary: nothing a module or its file does reaches the UI as an exception.
            result = Fail(extension, e, "couldn't be loaded", ex.ToString());
        }
        e.Status = result.Status;
        e.Message = result.Message;
        e.Pending = null;
        if (e.Waiting is { } waiting)
        {
            e.Waiting = null;
            foreach (var r in waiting.Registrations)
                r.Dispose();
            waiting.Withdraw.Dispose();
        }
        StatusChanged?.Invoke(extension.Id);
        return result;
    }

    private async Task<SimulatorModuleResult> OpenCoreAsync(ExtensionManifest extension, Entry e)
    {
        var module = extension.Simulator!;
        // Twice at most: a file that changes between the question and the load is asked about again, once.
        for (var attempt = 0; ; attempt++)
        {
            if (ExtensionLoader.CheckModulePath(module.Folder, module.Module) is { } problem)
                return Fail(extension, e, problem, module.FullPath);
            var (file, hashError) = await Task.Run(() => Hash(module.FullPath));
            VerifyThread();
            if (hashError is not null)
                return Fail(extension, e, hashError, module.FullPath);
            var sha = file.Sha;

            var answer = _store.AnswerFor(extension.Id, sha);
            if (answer is null)
            {
                var previous = _store.LastOtherBuild(extension.Id, sha);
                var request = new SimulatorConsentRequest(extension.Id, extension.Version, module.FullPath, sha,
                    Changed: attempt > 0 || previous is not null, file.Size, file.ModifiedUtc, previous);
                answer = await _consent.AskAsync(request, e.Waiting?.Withdraw.Token ?? CancellationToken.None);
                VerifyThread();
                if (_disposed)
                    return new SimulatorModuleResult(null, SimulatorStatus.None, null);
                if (answer is null)
                    return new SimulatorModuleResult(null, SimulatorStatus.NotLoaded, $"The {extension.Id} preview module isn't loaded yet.");
                _store.Remember(extension.Id, sha, answer.Value, module.FullPath, file.Size, file.ModifiedUtc);
            }
            if (answer == false)
            {
                e.Declined = sha;
                return new SimulatorModuleResult(null, SimulatorStatus.Declined,
                    $"The {extension.Id} preview module isn't loaded: you chose not to load it.");
            }

            // Load the file that was answered for: held open so nothing can change it, and hashed again while held.
            var (held, lockError) = await Task.Run(() => OpenHeld(module));
            VerifyThread();
            if (lockError is not null)
                return Fail(extension, e, lockError, module.FullPath);
            using (held)
            {
                if (_disposed)
                    return new SimulatorModuleResult(null, SimulatorStatus.None, null);
                if (!string.Equals(held!.Sha, sha, StringComparison.Ordinal))
                {
                    if (attempt == 0)
                        continue;
                    return Fail(extension, e, "kept changing while Apex was loading it", module.FullPath);
                }
                return Load(extension, e, sha, held.FinalPath);
            }
        }
    }

    /// <param name="finalPath">The held file's own path, links resolved: a link retargeted since it was opened can't
    /// change what this loads.</param>
    private unsafe SimulatorModuleResult Load(ExtensionManifest extension, Entry e, string sha, string finalPath)
    {
        var path = extension.Simulator!.FullPath;
        _beforeNativeCall?.Invoke();
        var fp = FloatingPoint();
        var loaded = SimulatorNative.Load(finalPath, out var error);
        if (loaded is not null)
            e.Handle = loaded;
        if (FloatingPointChanged(fp))
            return Fail(extension, e, "changed Apex's floating-point settings while loading (Apex put them back)", path);
        if (loaded is not { } handle)
            return Fail(extension, e, Describe(error), $"{path}\nWin32 error {error}: {new System.ComponentModel.Win32Exception(error).Message}");

        var exports = new Exports();
        var addresses = new nint[ExportNames.Length];
        for (var i = 0; i < ExportNames.Length; i++)
            if (!NativeLibrary.TryGetExport(handle.DangerousGetHandle(), ExportNames[i], out addresses[i]))
                return Fail(extension, e, $"doesn't export {ExportNames[i]}", path);
        exports.AbiVersion = (delegate* unmanaged[Cdecl]<uint>)addresses[0];
        exports.Info = (delegate* unmanaged[Cdecl]<Info*, int>)addresses[1];
        exports.Create = (delegate* unmanaged[Cdecl]<Kv*, uint, byte*, uint, void*>)addresses[2];
        exports.Reset = (delegate* unmanaged[Cdecl]<void*, void>)addresses[3];
        exports.Step = (delegate* unmanaged[Cdecl]<void*, Input*, Output*, int>)addresses[4];
        exports.Destroy = (delegate* unmanaged[Cdecl]<void*, void>)addresses[5];

        var abi = exports.AbiVersion();
        if (FloatingPointChanged(fp))
            return Fail(extension, e, "changed Apex's floating-point settings in apex_sim_abi_version (Apex put them back)", path);
        if (abi != AbiVersion)
            return Fail(extension, e, $"is built for version {abi} of Apex's preview interface; this Apex knows version {AbiVersion}", path);

        var info = (Info*)Alloc(sizeof(Info));
        try
        {
            info->Size = (uint)sizeof(Info);
            var ok = exports.Info(info);
            if (FloatingPointChanged(fp))
                return Fail(extension, e, "changed Apex's floating-point settings in apex_sim_info (Apex put them back)", path);
            if (!CanaryIntact((byte*)info, sizeof(Info)))
                return Fail(extension, e, "wrote past the end of the space Apex gave it", path);
            if (ok != 0)
                return Fail(extension, e, $"reported a problem describing itself (apex_sim_info returned {ok})", path);
            if (info->Abi != abi)
                return Fail(extension, e, $"says it is built for version {abi} of Apex's preview interface, but apex_sim_info says version {info->Abi}", path);
            // Exactly as written: the header promises no trimming, so "weapon-tech " is not weapon-tech.
            var id = RawText(info->Id, IdCapacity);
            if (!string.Equals(id, extension.Id, StringComparison.Ordinal))
                return Fail(extension, e, id.Length == 0 ? "doesn't say which extension it belongs to"
                    : $"says it belongs to '{ExtensionLoader.Displayable(id)}', not {extension.Id}", path);
            e.Module = new NativeSimulatorModule(this, exports, extension.Id, Text(info->Version, VersionCapacity), path, sha);
            return new SimulatorModuleResult(e.Module, SimulatorStatus.Loaded, null);
        }
        finally
        {
            NativeMemory.Free(info);
        }
    }

    private SimulatorModuleResult Fail(ExtensionManifest extension, Entry e, string problem, string detail)
    {
        var file = extension.Simulator?.Module ?? "";
        Report(extension.Id, $"its preview module {file} {problem}, so the preview is off.", detail);
        return new SimulatorModuleResult(null, SimulatorStatus.Failed, $"The {extension.Id} preview is off: its module {file} {problem}.");
    }

    internal void ModuleTurnedOff(NativeSimulatorModule module, string why)
    {
        if (_entries.TryGetValue(module.ExtensionId, out var e))
        {
            e.Status = SimulatorStatus.TurnedOff;
            e.Message = module.OffNote;
        }
        Report(module.ExtensionId, $"Apex turned off its preview module until it restarts: {why}.", $"{module.Path}\nSHA-256 {module.Sha256}");
        StatusChanged?.Invoke(module.ExtensionId);
    }

    /// <summary>A call that returned but broke the header's rules (too slow, floating-point state): said once per kind.</summary>
    internal void ModuleFault(NativeSimulatorModule module, string what) =>
        Report(module.ExtensionId, $"its preview module {Path.GetFileName(module.Path)} {what}. Apex keeps using it; "
            + $"{MaxFailuresInARow} slow or failed steps in a row turn it off.", $"{module.Path}\nSHA-256 {module.Sha256}");

    private void Report(string id, string message, string detail)
    {
        var d = new ExtensionDiagnostic(id, ExtensionProblem.Skipped, message);
        _diagnostics.Add(d);
        Reported?.Invoke(d, detail);
    }

    /// <summary>The file as it was hashed: what the question names.</summary>
    private readonly record struct Hashed(string Sha, long Size, DateTime ModifiedUtc);

    /// <summary>
    /// The file's SHA-256 (lower-case hex), size and write time, or why it can't be used: unreadable, too large, or
    /// needing DLLs other than Windows' own (<see cref="ModuleImage"/>), so the user is never asked about a module that
    /// would be refused.
    /// </summary>
    private static (Hashed File, string? Error) Hash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            var (sha, error) = Examine(stream);
            return error is not null ? (default, error) : (new Hashed(sha!, stream.Length, File.GetLastWriteTimeUtc(stream.SafeFileHandle)), null);
        }
        catch (FileNotFoundException) { return (default, "isn't there"); }
        catch (DirectoryNotFoundException) { return (default, "isn't there"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (default, "couldn't be read (another program may have it, or access was refused)");
        }
    }

    /// <summary>Read whole (at most <see cref="MaxModuleBytes"/>): the bytes hashed are the bytes whose imports are checked.</summary>
    private static (string? Sha, string? Error) Examine(FileStream stream)
    {
        if (stream.Length > MaxModuleBytes)
            return (null, "is larger than 64 MB");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return ModuleImage.Problem(bytes) is { } problem ? (null, problem) : (Convert.ToHexStringLower(SHA256.HashData(bytes)), null);
    }

    /// <summary>The module file and its extension's folder, held until the load is done.</summary>
    private sealed class Held(FileStream file, Microsoft.Win32.SafeHandles.SafeFileHandle folder, string sha, string finalPath) : IDisposable
    {
        public string Sha { get; } = sha;
        public string FinalPath { get; } = finalPath;

        public void Dispose()
        {
            file.Dispose();
            folder.Dispose();
        }
    }

    /// <summary>
    /// The file opened so no one can write, rename or delete it until it is loaded, and its extension's folder so no one
    /// can rename or delete that; the file's hash while held, and where it really is. That must be inside the folder
    /// where the manifest says: a link that moved it is refused.
    /// </summary>
    private static (Held? Held, string? Error) OpenHeld(SimulatorModuleRef module)
    {
        var folder = SimulatorNative.HoldFolder(module.Folder);
        if (folder is null)
            return (null, "couldn't be opened (its extension's folder can't be read)");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(module.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            var (sha, error) = Examine(stream);
            var final = error is null ? SimulatorNative.FinalPath(stream.SafeFileHandle) : null;
            var folderFinal = error is null ? SimulatorNative.FinalPath(folder) : null;
            if (error is null && (final is null || folderFinal is null))
                error = "couldn't be located on disk";
            else if (error is null && !string.Equals(final, Path.Join(folderFinal, Path.GetRelativePath(module.Folder, module.FullPath)), StringComparison.OrdinalIgnoreCase))
                error = "isn't where its extension's folder says (a link points elsewhere)";
            if (error is not null)
            {
                stream.Dispose();
                folder.Dispose();
                return (null, error);
            }
            return (new Held(stream, folder, sha!, final!), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            folder.Dispose();
            return (null, ex is FileNotFoundException or DirectoryNotFoundException ? "isn't there"
                : "couldn't be read (another program may have it, or access was refused)");
        }
    }

    internal void VerifyThread()
    {
        if (Environment.CurrentManagedThreadId != _owner)
            throw new InvalidOperationException("Preview modules are used from the UI thread only.");
    }

    /// <summary>
    /// App exit: destroys every simulation still alive, then frees the modules. From another thread nothing is called
    /// into (the process is ending; the OS reclaims it).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Environment.CurrentManagedThreadId != _owner)
            return;
        foreach (var e in _entries.Values)
            e.Module?.DestroyAll();
        foreach (var e in _entries.Values)
            e.Handle?.Dispose();
        _entries.Clear();
    }
}
