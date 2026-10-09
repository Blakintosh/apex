using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Styling;
using Apex.Editor.Models;
using Apex.Editor.Services.Extensions;
using Apex.Editor.Services.Extensions.Simulation;
using Apex.Editor.ViewModels;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// Preview-simulator modules (step 4a): the manifest's <c>simulator</c> member, the host (lazy load, consent, ABI and id
/// checks, every failure as a diagnostic, turning a faulting module off, the thread guard, unloading), exact step results
/// against the fixture module, and the consent prompt in the app with real keys. The modules are the committed builds
/// of Fixtures\simulator\sim_fixture.c, copied to scratch folders; nothing here writes outside them.
/// </summary>
public partial class Program
{
    private static string FixtureSimulator => Path.Combine(AppContext.BaseDirectory, "Fixtures", "simulator");

    private const string SimId = "sim-fixture";

    private static void RunSimulatorChecks(string outDir)
    {
        var saved = (Ext: Environment.GetEnvironmentVariable(ExtensionRegistry.DirVariable), Settings: Environment.GetEnvironmentVariable("APEX_SETTINGS_DIR"));
        try
        {
            SimulatorFixtureChecks();
            SimulatorManifestChecks();
            SimulatorConsentChecks();
            SimulatorFailureChecks();
            SimulatorStepChecks();
            RunSimulatorHardeningChecks();
            SimulatorAppChecks(outDir);
            SimulatorBannerCheck();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, saved.Ext);
            Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", saved.Settings);
            ExtensionRegistry.Clear();
            SchemaRegistry.ResetToMock();
        }
    }

    // ═══ The fixture is the build of the source beside it ═══════════════════

    /// <summary>
    /// The committed DLLs must be what build.ps1 makes from the committed source and header: a changed source or header
    /// without a rebuild fails here (never skips), so the checks below never test a stale module.
    /// </summary>
    private static void SimulatorFixtureChecks()
    {
        var built = Path.Combine(FixtureSimulator, "built");
        var recorded = File.ReadAllLines(Path.Combine(built, "hashes.txt"))
            .Select(l => l.Split("  ", 2)).ToDictionary(p => p[1], p => p[0], StringComparer.OrdinalIgnoreCase);
        static string TextHash(string path) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("\r\n", "\n"))));
        var header = RepoFile(Path.Combine("docs", "plugin-abi", "apex_sim.h"));
        var stale = new List<string>();
        foreach (var source in new[] { "sim_fixture.c", "sim_helper.c" })
            if (TextHash(Path.Combine(FixtureSimulator, source)) != recorded.GetValueOrDefault(source))
                stale.Add(source);
        if (header is null || TextHash(header) != recorded.GetValueOrDefault("apex_sim.h"))
            stale.Add("apex_sim.h");
        foreach (var dll in recorded.Keys.Where(k => k.EndsWith(".dll")))
            if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(built, dll)))) != recorded[dll])
                stale.Add(dll);
        Check($"simulator: the fixture modules are the build of sim_fixture.c and apex_sim.h ({(stale.Count == 0 ? "match" : "changed since the build: " + string.Join(", ", stale) + "; run Apex.Shots\\Fixtures\\simulator\\build.ps1")})",
            stale.Count == 0 && recorded.Count == 13);
    }

    /// <summary>
    /// A file of the repository: found above the harness's own folder (a build inside the repo), else above this source
    /// file's folder as it was compiled (a build into a folder outside the repo, -o).
    /// </summary>
    private static string? RepoFile(string relative, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) })
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, relative)))
                    return Path.Combine(dir.FullName, relative);
        return null;
    }

    // ═══ Fixture extensions ═══════════════════════════════════════════════

    private static string SimManifest(string module = "sim-fixture.dll", string simulatorJson = "") =>
        $$"""
        { "apexSchema": 1, "id": "{{SimId}}", "version": "1.0", "targets": ["weapon"], "enabledBy": "fxEnabled",
          "simulator": {{(simulatorJson.Length > 0 ? simulatorJson : $"{{ \"module\": \"{module.Replace("\\", "\\\\")}\" }}")}},
          "sections": [ { "title": "Fixture",
            "fields": [ { "key": "fxEnabled", "kind": "toggle", "default": 0 }, { "key": "fxGain", "kind": "number", "default": 1 },
                        { "key": "fxMode", "kind": "text" }, { "key": "fxNote", "kind": "text" } ],
            "records": [ { "key": "fxRow#", "columns": [ { "name": "a", "kind": "number" } ] } ] } ] }
        """;

    /// <summary>An extensions folder holding the fixture extension and <paramref name="dll"/> as its module.</summary>
    private static (string Dir, string Folder) SimExtension(string label, string dll = "sim-fixture.dll", string module = "sim-fixture.dll", string? manifest = null)
    {
        var dir = NewScratch(label);
        var folder = Path.Combine(dir, SimId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ExtensionLoader.FileName), manifest ?? SimManifest(module));
        var target = Path.GetFullPath(Path.Combine(folder, module));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(FixtureSimulator, "built", dll), target);
        return (dir, folder);
    }

    private static ExtensionManifest? ReadSim(string folder, List<ExtensionDiagnostic>? diagnostics = null) =>
        ExtensionLoader.Read(Path.Combine(folder, ExtensionLoader.FileName), diagnostics ?? new());

    /// <summary>Answers from a script, and records what it was asked.</summary>
    private sealed class ScriptedConsent(params bool?[] answers) : ISimulatorConsent
    {
        private readonly Queue<bool?> _answers = new(answers);
        public readonly List<SimulatorConsentRequest> Asked = new();
        public TaskCompletionSource<bool?>? Held;
        public System.Threading.CancellationToken Withdrawn;

        public Task<bool?> AskAsync(SimulatorConsentRequest request, System.Threading.CancellationToken withdrawn)
        {
            Asked.Add(request);
            Withdrawn = withdrawn;
            if (Held is not null)
                return Held.Task;
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : false);
        }
    }

    private static SimulatorHost NewHost(ScriptedConsent consent, string? store, Action? beforeFirst = null) =>
        new(consent, new SimulatorConsentStore(store), beforeFirst);

    private static T Await<T>(Task<T> task)
    {
        Pump(task);
        return task.IsCompleted ? task.Result : throw new TimeoutException("the host didn't answer within 10 s");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string name);

    private static bool IsLoaded(string path) => GetModuleHandleW(path) != 0;

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FixtureCounter();

    /// <summary>The fixture's own counters (not ABI): live simulations and steps that reached it.</summary>
    private static int FixtureCount(string path, string export) =>
        GetModuleHandleW(path) is var h and not 0 && NativeLibrary.TryGetExport(h, export, out var f)
            ? Marshal.GetDelegateForFunctionPointer<FixtureCounter>(f)()
            : -1;

    private static List<KeyValuePair<string, string>> Values(params (string Key, string Value)[] pairs) =>
        pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)).ToList();

    // ═══ Manifest ═════════════════════════════════════════════════════════

    private static void SimulatorManifestChecks()
    {
        var (_, folder) = SimExtension("sim-manifest");
        var diagnostics = new List<ExtensionDiagnostic>();
        var m = ReadSim(folder, diagnostics);
        Check($"simulator manifest: a module inside the folder is accepted, resolved and nothing else said ({m?.Simulator?.FullPath}; {string.Join("; ", diagnostics)})",
            m?.Simulator is { Module: "sim-fixture.dll" } s && s.FullPath == Path.Combine(folder, "sim-fixture.dll") && diagnostics.Count == 0
            && m.Sections.Count == 1 && !IsLoaded(s.FullPath));

        var refused = new (string Module, string Why)[]
        {
            (@"..\sim-fixture.dll", "'..'"), ("sub/../../x.dll", "'..'"), (@"C:\Windows\System32\kernel32.dll", "absolute"),
            (@"\\server\share\x.dll", "absolute"), (@"\x.dll", "absolute"), ("C:x.dll", "drive-relative"), ("x.dll:stream", "a stream"),
            ("x.exe", "not a .dll"), ("", "empty"), ("a/./b.dll", "'.'"), ("x.dll.", "trailing dot"), ("*.dll", "wildcard"),
            ("sub//x.dll", "empty part"), ("x.dll ", "trailing space"),
        };
        var wrong = new List<string>();
        foreach (var (module, why) in refused)
        {
            var d = new List<ExtensionDiagnostic>();
            var dir = NewScratch("sim-refused");
            Directory.CreateDirectory(Path.Combine(dir, SimId));
            File.WriteAllText(Path.Combine(dir, SimId, ExtensionLoader.FileName), SimManifest(module));
            var r = ReadSim(Path.Combine(dir, SimId), d);
            if (r is null || r.Simulator is not null || r.Sections.Count != 1 || !d.Any(x => x.Problem == ExtensionProblem.Skipped && x.Message.Contains("preview module was left out")))
                wrong.Add($"{module} ({why}): {(r is null ? "extension left out" : r.Simulator is null ? string.Join("; ", d) : "accepted")}");
        }
        Check($"simulator manifest: {refused.Length} paths that leave the folder or aren't a .dll are refused, the fields still load ({string.Join(" | ", wrong)})", wrong.Count == 0);

        // A junction inside the folder: a link could point anywhere, so it's refused when read and again when loading.
        var (_, linked) = SimExtension("sim-junction");
        var elsewhere = NewScratch("sim-junction-target");
        File.Copy(Path.Combine(FixtureSimulator, "built", "sim-fixture.dll"), Path.Combine(elsewhere, "sim-fixture.dll"));
        var junction = Path.Combine(linked, "bin");
        var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{elsewhere}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        made?.WaitForExit();
        File.WriteAllText(Path.Combine(linked, ExtensionLoader.FileName), SimManifest(@"bin\sim-fixture.dll"));
        var jd = new List<ExtensionDiagnostic>();
        var jm = ReadSim(linked, jd);
        Check($"simulator manifest: a module behind a junction is refused ({string.Join("; ", jd)})",
            Directory.Exists(junction) && jm is { Simulator: null } && jd.Any(d => d.Message.Contains("junction or symbolic link")));

        var (_, gone) = SimExtension("sim-missing");
        File.Delete(Path.Combine(gone, "sim-fixture.dll"));
        var md = new List<ExtensionDiagnostic>();
        var mm = ReadSim(gone, md);
        Check($"simulator manifest: a module that isn't there is a note, not a reason to leave anything out ({string.Join("; ", md)})",
            mm?.Simulator is not null && mm.Sections.Count == 1 && md.Count == 1 && md[0].Problem == ExtensionProblem.Note);

        var od = new List<ExtensionDiagnostic>();
        var (_, odd) = SimExtension("sim-odd", manifest: SimManifest(simulatorJson: "{ \"module\": \"sim-fixture.dll\", \"threads\": 4 }"));
        var om = ReadSim(odd, od);
        var nd = new List<ExtensionDiagnostic>();
        var (_, notObject) = SimExtension("sim-not-object", manifest: SimManifest(simulatorJson: "\"sim-fixture.dll\""));
        var nm = ReadSim(notObject, nd);
        Check($"simulator manifest: an unknown simulator member is a note; a simulator that isn't an object is left out ({string.Join("; ", od.Concat(nd))})",
            om?.Simulator is not null && od.Single().Problem == ExtensionProblem.Note
            && nm is { Simulator: null } && nd.Single().Problem == ExtensionProblem.Skipped);

        var withoutDir = NewScratch("sim-none");
        Directory.CreateDirectory(Path.Combine(withoutDir, "plain"));
        File.WriteAllText(Path.Combine(withoutDir, "plain", ExtensionLoader.FileName), Manifest("plain", "{ \"key\": \"plA\", \"kind\": \"text\" }"));
        Check("simulator manifest: an extension without one has none", ReadSim(Path.Combine(withoutDir, "plain")) is { Simulator: null });
    }

    // ═══ Consent ══════════════════════════════════════════════════════════

    private static void SimulatorConsentChecks()
    {
        var (_, folder) = SimExtension("sim-consent");
        var m = ReadSim(folder)!;
        var path = m.Simulator!.FullPath;
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        var store = Path.Combine(NewScratch("sim-consent-store"), SimulatorConsentStore.FileName);

        // Lazy: a host made and asked about status has touched nothing.
        var consent = new ScriptedConsent(true);
        var flushedWhileUnloaded = new List<bool>();
        var host = NewHost(consent, store, () => flushedWhileUnloaded.Add(IsLoaded(path)));
        Check("simulator: a host loads nothing and asks nothing until a module is asked for",
            host.StatusOf(m) == SimulatorStatus.NotLoaded && consent.Asked.Count == 0 && !IsLoaded(path) && !File.Exists(store));

        var first = Await(host.GetModuleAsync(m));
        Check($"simulator consent: the first ask asks, naming the extension, version, file and hash ({first.Status}; {consent.Asked.FirstOrDefault()})",
            first is { Status: SimulatorStatus.Loaded, Module: { } mod } && mod.ModuleVersion == "fixture-1" && mod.Sha256 == sha
            && consent.Asked.Count == 1
            && consent.Asked[0] == new SimulatorConsentRequest(SimId, "1.0", path, sha, Changed: false, new FileInfo(path).Length, File.GetLastWriteTimeUtc(path))
            && IsLoaded(path) && host.StatusOf(m) == SimulatorStatus.Loaded);
        Check("simulator consent: the journal hook runs once, before the module is in the process",
            flushedWhileUnloaded.SequenceEqual([false]));
        var again = Await(host.GetModuleAsync(m));
        Check("simulator consent: a later ask returns the loaded module without asking", ReferenceEquals(again.Module, first.Module) && consent.Asked.Count == 1);
        host.Dispose();
        Check("simulator: disposing the host unloads the module", !IsLoaded(path));

        var remembered = new ScriptedConsent();
        host = NewHost(remembered, store);
        Check("simulator consent: Load is remembered for that file (no question next session)",
            Await(host.GetModuleAsync(m)).Status == SimulatorStatus.Loaded && remembered.Asked.Count == 0);
        host.Dispose();

        // Don't load, remembered; Load module… asks again.
        var (_, declinedFolder) = SimExtension("sim-declined");
        var dm = ReadSim(declinedFolder)!;
        var dpath = dm.Simulator!.FullPath;
        var declinedStore = Path.Combine(NewScratch("sim-declined-store"), SimulatorConsentStore.FileName);
        var no = new ScriptedConsent(false);
        host = NewHost(no, declinedStore);
        var declined = Await(host.GetModuleAsync(dm));
        Check($"simulator consent: Don't load leaves it unloaded, with a calm reason ('{declined.Message}')",
            declined is { Status: SimulatorStatus.Declined, Module: null } && declined.Message == "The sim-fixture preview module isn't loaded: you chose not to load it."
            && !IsLoaded(dpath) && host.Diagnostics.Count == 0);
        host.Dispose();
        var later = new ScriptedConsent(true);
        host = NewHost(later, declinedStore);
        Check("simulator consent: Don't load is remembered too",
            Await(host.GetModuleAsync(dm)).Status == SimulatorStatus.Declined && later.Asked.Count == 0 && !IsLoaded(dpath));
        var reconsidered = Await(host.ReconsiderAsync(dm));
        Check("simulator consent: Load module… asks again, and Load then loads it",
            reconsidered.Status == SimulatorStatus.Loaded && later.Asked.Count == 1 && !later.Asked[0].Changed && IsLoaded(dpath));
        host.Dispose();

        // A changed file is asked about again, saying what changed: its hash, size and time against the build answered for.
        var oldSize = new FileInfo(path).Length;
        var oldTime = File.GetLastWriteTimeUtc(path);
        File.AppendAllText(path, "rebuilt");
        File.SetLastWriteTimeUtc(path, oldTime.AddHours(1));
        var changed = new ScriptedConsent(true);
        host = NewHost(changed, store);
        var changedLoad = Await(host.GetModuleAsync(m));
        var what = changed.Asked is [{ } asked] ? ModuleQuestion.ChangeOf(asked) : "";
        string Local(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Check($"simulator consent: a changed file (new hash) is asked about again, saying what changed ('{what}')",
            changedLoad.Status == SimulatorStatus.Loaded && changed.Asked is [{ Changed: true } q] && q.Sha256 != sha
            && q.Previous is { } p && p.Sha256 == sha && p.Size == oldSize && p.ModifiedUtc == oldTime
            && what == $"Changed since you answered on {p.AnsweredUtc.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}: "
                + $"SHA-256 {q.Sha256[..8]} (was {sha[..8]}), {((oldSize + 7) / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} KB (+7 bytes), "
                + $"modified {Local(oldTime.AddHours(1))} (was {Local(oldTime)}).");
        host.Dispose();

        // Missing and corrupt answer files: asked again, and the next answer writes a good file.
        File.Delete(store);
        var missing = new ScriptedConsent(true);
        host = NewHost(missing, store);
        Await(host.GetModuleAsync(m));
        host.Dispose();
        File.WriteAllText(store, "{ \"modules\": [ { \"id\": ");
        var corrupt = new ScriptedConsent(true);
        host = NewHost(corrupt, store);
        var afterCorrupt = Await(host.GetModuleAsync(m));
        host.Dispose();
        var reread = new SimulatorConsentStore(store);
        Check("simulator consent: a missing or unreadable answers file means asking again, and the answer is written cleanly",
            missing.Asked.Count == 1 && corrupt.Asked.Count == 1 && afterCorrupt.Status == SimulatorStatus.Loaded
            && reread.AnswerFor(SimId, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))) == true);

        // No answer (the question was replaced): nothing remembered, the next ask asks.
        var none = new ScriptedConsent(null, true);
        var noneStore = Path.Combine(NewScratch("sim-none-store"), SimulatorConsentStore.FileName);
        host = NewHost(none, noneStore);
        var unanswered = Await(host.GetModuleAsync(m));
        var answered = Await(host.GetModuleAsync(m));
        Check($"simulator consent: no answer remembers nothing and the next ask asks ({unanswered.Status} then {answered.Status})",
            unanswered.Status == SimulatorStatus.NotLoaded && answered.Status == SimulatorStatus.Loaded && none.Asked.Count == 2);
        host.Dispose();

        // Two asks while the question is open share it.
        var held = new ScriptedConsent { Held = new TaskCompletionSource<bool?>() };
        host = NewHost(held, Path.Combine(NewScratch("sim-held-store"), SimulatorConsentStore.FileName));
        var a = host.GetModuleAsync(m);
        Pump(50);
        var b = host.GetModuleAsync(m);
        var pendingStatus = host.StatusOf(m);
        held.Held.SetResult(true);
        Check("simulator consent: asks made while the question is open share it (one question)",
            pendingStatus == SimulatorStatus.Pending && Await(a).Module is { } am && ReferenceEquals(Await(b).Module, am) && held.Asked.Count == 1);
        host.Dispose();

        // The settings folder: APEX_SETTINGS_DIR decides where answers go; a run that keeps nothing keeps them in memory.
        var settings = NewScratch("sim-settings");
        Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", settings);
        var user = SimulatorConsentStore.ForUser();
        Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", null);
        Check($"simulator consent: APEX_SETTINGS_DIR decides where answers are kept ({user.FilePath}); mock runs keep none",
            user.FilePath == Path.Combine(settings, SimulatorConsentStore.FileName) && SimulatorConsentStore.ForUser().FilePath is null);
    }

    // ═══ Failures: each one a sentence, never an exception ═════════════════

    private static void SimulatorFailureChecks()
    {
        SimulationResult? Fails(string label, string dll, out SimulatorHost host, out ExtensionManifest m, out List<string> reported, Action<string>? before = null)
        {
            var (_, folder) = SimExtension(label, dll);
            before?.Invoke(folder);
            m = ReadSim(folder)!;
            var said = reported = new List<string>();
            host = NewHost(new ScriptedConsent(true), null);
            host.Reported += (d, _) => said.Add(d.ToString());
            try
            {
                return Await(host.CreateAsync(new SimulatorRequest(m, Values(("fxEnabled", "1")))));
            }
            catch (Exception ex)
            {
                Check($"simulator failures: {label} threw {ex.GetType().Name}: {ex.Message}", false);
                return null;
            }
        }

        var cases = new (string Label, string Dll, string Expect, Action<string>? Before)[]
        {
            ("missing", "sim-fixture.dll", "isn't there", f => File.Delete(Path.Combine(f, "sim-fixture.dll"))),
            ("x86", "sim-fixture-x86.dll", "isn't a 64-bit Windows DLL", null),
            ("not-a-dll", "sim-fixture.dll", "isn't a 64-bit Windows DLL", f => File.WriteAllText(Path.Combine(f, "sim-fixture.dll"), "not a module")),
            ("abi2", "sim-fixture-abi2.dll", "is built for version 2 of Apex's preview interface; this Apex knows version 1", null),
            ("wrong-id", "sim-fixture-wrongid.dll", "says it belongs to 'someone-else', not sim-fixture", null),
            ("no-step", "sim-fixture-nostep.dll", "doesn't export apex_sim_step", null),
            ("junction-at-load", "sim-fixture.dll", "junction or symbolic link", null),
        };
        foreach (var (label, dll, expect, before) in cases)
        {
            SimulationResult? r;
            SimulatorHost host;
            List<string> reported;
            ExtensionManifest m;
            if (label == "junction-at-load")
            {
                // Read while the folder was plain; by the time a preview asks, the module sits behind a junction.
                var (_, folder) = SimExtension("sim-late-junction", module: @"bin\sim-fixture.dll");
                m = ReadSim(folder)!;
                var target = NewScratch("sim-late-junction-target");
                File.Move(Path.Combine(folder, "bin", "sim-fixture.dll"), Path.Combine(target, "sim-fixture.dll"));
                Directory.Delete(Path.Combine(folder, "bin"));
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(folder, "bin")}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })?.WaitForExit();
                var said = reported = new List<string>();
                host = NewHost(new ScriptedConsent(true), null);
                host.Reported += (d, _) => said.Add(d.ToString());
                r = Await(host.CreateAsync(new SimulatorRequest(m, Values(("fxEnabled", "1")))));
            }
            else
                r = Fails("sim-fail-" + label, dll, out host, out m, out reported, before);
            Check($"simulator failures: {label} is a plain diagnostic, said once, and no simulation ('{r?.Message}'; reported '{string.Join(" | ", reported)}')",
                r is { Simulation: null, Status: SimulatorStatus.Failed } && r.Message!.Contains(expect) && reported.Count == 1 && reported[0].Contains(expect)
                && reported[0].StartsWith("sim-fixture: its preview module") && host.StatusOf(m) == SimulatorStatus.Failed
                && Await(host.GetModuleAsync(m)).Status == SimulatorStatus.Failed && reported.Count == 1);
            host.Dispose();
        }

        // Create refusing one weapon is the weapon's problem, not the module's.
        var (_, cf) = SimExtension("sim-create-fail");
        var cm = ReadSim(cf)!;
        var ch = NewHost(new ScriptedConsent(true), null);
        var said2 = new List<string>();
        ch.Reported += (d, _) => said2.Add(d.Message);
        var refusedCreate = Await(ch.CreateAsync(new SimulatorRequest(cm, Values(("fxMode", "create-fail"), ("fxNote", "no kick table")))));
        var fine = Await(ch.CreateAsync(new SimulatorRequest(cm, Values(("fxGain", "1")))));
        Check($"simulator failures: create returning NULL is that weapon's message, and the module keeps working ('{refusedCreate.Message}')",
            refusedCreate is { Simulation: null, Status: SimulatorStatus.Loaded, Message: "sim-fixture can't preview this weapon: no kick table" }
            && fine.Simulation is { IsAlive: true } && said2.Count == 0);

        // A failing step: each one a failed frame with its note; ten in a row turn the module off.
        var stepFail = Await(ch.CreateAsync(new SimulatorRequest(cm, Values(("fxMode", "step-fail"), ("fxNote", "spring blew up")))));
        var sim = stepFail.Simulation!;
        var path = cm.Simulator!.FullPath;
        var frames = Enumerable.Range(0, SimulatorHost.MaxFailuresInARow - 1).Select(_ => sim.Step(1 / 60f, 0, 0)).ToList();
        Check($"simulator failures: a failed step is a failed frame with the module's note ('{frames[0].Note}')",
            frames.All(f => !f.Ok && f.Note == "spring blew up" && f.Supported == SimulatorParts.None) && sim.IsAlive && said2.Count == 0);
        var good = fine.Simulation!.Step(1 / 60f, 0, 0);
        var resumed = Enumerable.Range(0, SimulatorHost.MaxFailuresInARow - 1).Select(_ => sim.Step(1 / 60f, 0, 0)).ToList();
        Check("simulator failures: a good step in between starts the count again", good.Ok && sim.IsAlive && resumed.All(f => !f.Ok));
        var stepsBefore = FixtureCount(path, "fixture_steps");
        var last = sim.Step(1 / 60f, 0, 0);
        var afterOff = fine.Simulation.Step(1 / 60f, 0, 0);
        Check($"simulator failures: {SimulatorHost.MaxFailuresInARow} failed steps in a row turn the module off for the session, said once ('{last.Note}'; '{string.Join(" | ", said2)}')",
            !last.Ok && !sim.IsAlive && !fine.Simulation.IsAlive && !afterOff.Ok && said2.Count == 1
            && said2[0].StartsWith($"Apex turned off its preview module until it restarts: it failed {SimulatorHost.MaxFailuresInARow} steps in a row (the last: spring blew up)")
            && last.Note == "Apex turned off the sim-fixture preview module until it restarts. Editing and saving work as usual."
            && FixtureCount(path, "fixture_steps") == stepsBefore + 1 && ch.StatusOf(cm) == SimulatorStatus.TurnedOff
            && Await(ch.CreateAsync(new SimulatorRequest(cm, Values()))).Status == SimulatorStatus.TurnedOff);
        ch.Dispose();

        foreach (var (mode, expect) in new[] { ("overrun", "wrote past the end of the output"), ("nan", null as string) })
        {
            var (_, f) = SimExtension("sim-" + mode);
            var mm = ReadSim(f)!;
            var h = NewHost(new ScriptedConsent(true), null);
            var said = new List<string>();
            h.Reported += (d, _) => said.Add(d.Message);
            var s = Await(h.CreateAsync(new SimulatorRequest(mm, Values(("fxMode", mode))))).Simulation!;
            var frame = s.Step(0.5f, 0, 0);
            if (expect is not null)
                Check($"simulator failures: a module writing past its output is turned off at once ('{string.Join(" | ", said)}')",
                    !frame.Ok && !s.IsAlive && said.Count == 1 && said[0].Contains(expect));
            else
                Check($"simulator failures: motion that isn't a number is a failed step, not a frame ('{frame.Note}')",
                    !frame.Ok && frame.Note == "The preview module returned motion that isn't a number." && s.IsAlive && said.Count == 0);
            h.Dispose();
        }
    }

    // ═══ Steps: exact, unsupported parts, leaks, threads, shutdown ═════════

    private static void SimulatorStepChecks()
    {
        var (_, folder) = SimExtension("sim-steps");
        var m = ReadSim(folder)!;
        var path = m.Simulator!.FullPath;
        var host = NewHost(new ScriptedConsent(true), null);
        var values = Values(("fxEnabled", "1"), ("fxGain", "2"), ("fxMode", ""), ("fxNote", "Rückstoß ✓"), ("fxRow1", "3"));
        var sim = Await(host.CreateAsync(new SimulatorRequest(m, values))).Simulation!;
        bool Is(SimulatorFrame f, Vector3 va, Vector3 vo, Vector3 ga, Vector3 go) =>
            f.Ok && f.Supported == SimulatorParts.All && f.ViewAngles == va && f.ViewOrigin == vo && f.GunAngles == ga && f.GunOrigin == go && f.Note == "";
        var s1 = sim.Step(0.5f, 0.25f, 3);
        var s2 = sim.Step(0.25f, 1f, 0);
        sim.Reset();
        var s3 = sim.Step(0.5f, 0, 1);
        Check($"simulator steps: outputs are exactly the fixture's functions of keys and inputs, in order ({s1}; {s2}; {s3})",
            Is(s1, new(1, 0.25f, 3), new(5, 1, 0.5f), new(4, 'f', 1), new(-2, -0.5f, 4))
            && Is(s2, new(1.5f, 1, 3), new(5, 2, 0.25f), new(4, 'f', 1), new(-2, -0.75f, 5))
            && Is(s3, new(1, 0, 1), new(5, 1, 0.5f), new(4, 'f', 1), new(-2, -0.5f, 2)));
        var clamped = sim.Step(0.5f, 7f, 0);
        var zero = sim.Step(0, 0, 0);
        Check("simulator steps: ads is clamped to 0..1 and a step of no time isn't sent",
            clamped.ViewAngles.Y == 1 && !zero.Ok && zero.Note == "A step needs a time above zero.");

        var partial = Await(host.CreateAsync(new SimulatorRequest(m, Values(("fxMode", "unsupported"), ("fxNote", "Rückstoß ✓ — only the view"))))).Simulation!;
        var p = partial.Step(0.5f, 0, 0);
        Check($"simulator steps: a module that computes only some parts says which, with its note in UTF-8 ('{p.Note}')",
            p.Ok && p.Supported == SimulatorParts.ViewAngles && p.ViewAngles.X == 0.5f && p.ViewOrigin == default && p.GunAngles == default
            && p.GunOrigin == default && p.Note == "Rückstoß ✓ — only the view");
        partial.Dispose();

        // 1,000 steps and 1,000 simulations made and closed: nothing kept (crude: handles and memory, and the module's own count).
        var proc = Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        proc.Refresh();
        var (handles, bytes) = (proc.HandleCount, proc.PrivateMemorySize64);
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
            sim.Step(1 / 60f, 0.5f, (uint)(i % 3 == 0 ? 1 : 0));
        var stepMicros = clock.Elapsed.TotalMicroseconds / 1000;
        for (var i = 0; i < 1000; i++)
        {
            using var s = Await(host.CreateAsync(new SimulatorRequest(m, values))).Simulation!;
            s.Step(1 / 60f, 0, 0);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        proc.Refresh();
        var (handlesAfter, bytesAfter) = (proc.HandleCount, proc.PrivateMemorySize64);
        Console.WriteLine($"info  simulator: a step takes {stepMicros:0.00} µs through the host (fixture module)");
        Check($"simulator steps: 1,000 steps and 1,000 simulations keep nothing (handles {handles} → {handlesAfter}, private memory {(bytesAfter - bytes) / 1024:+#,0;-#,0;0} KB, live in the module {FixtureCount(path, "fixture_live")})",
            handlesAfter - handles <= 8 && bytesAfter - bytes < 8 << 20 && FixtureCount(path, "fixture_live") == 1);

        // The thread guard: another thread is refused before anything reaches the module.
        var stepsBefore = FixtureCount(path, "fixture_steps");
        var fromPool = Task.Run(() => sim.Step(1 / 60f, 0, 0));
        Pump(fromPool);
        var hostFromPool = Task.Run(() => host.GetModuleAsync(m));
        Pump(hostFromPool);
        Check($"simulator steps: a call from another thread is refused and never reaches the module ({fromPool.Exception?.InnerException?.GetType().Name})",
            fromPool.Exception?.InnerException is InvalidOperationException && hostFromPool.Exception?.InnerException is InvalidOperationException
            && FixtureCount(path, "fixture_steps") == stepsBefore);

        // Shutdown: what the preview left alive is destroyed, then the module is freed; a late call is harmless.
        host.Dispose();
        var lateStep = sim.Step(1 / 60f, 0, 0);
        sim.Reset();
        sim.Dispose();
        Check("simulator steps: disposing the host destroys what is left, frees the module, and a late step is a failed frame, not a crash",
            !IsLoaded(path) && !sim.IsAlive && !lateStep.Ok);
    }

    // ═══ The app: lazy, enabledBy, the prompt with real keys ════════════════

    private static void SimulatorAppChecks(string outDir)
    {
        var (dir, folder) = SimExtension("sim-app");
        var path = Path.Combine(folder, "sim-fixture.dll");
        var settings = NewScratch("sim-app-settings");
        var answers = Path.Combine(settings, SimulatorConsentStore.FileName);
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, dir);
        Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", settings);
        var root = NewScratch("sim-app-session");
        var vm = new MainViewModel(root);
        var window = ShowJournalWindow(vm);
        try
        {
            Check("simulator app: starting Apex with a module installed loads nothing, asks nothing, writes no answers",
                !IsLoaded(path) && !vm.IsConfirmOpen && !File.Exists(answers) && ExtensionRegistry.Manifests.Single().Simulator is not null);

            vm.OpenByName("wpn_ar_havoc_zm");
            var tab = vm.ActiveTab!;
            Check("simulator app: an asset the extension is off for asks for no simulator", tab.SimulatorRequests().Count == 0 && !IsLoaded(path));
            PropertyItemViewModel Row(string key) => tab.AllSentinel.All.First(r => r.Key == key);
            Row("fxEnabled").RawValue = "1";
            Row("fxGain").RawValue = "2";
            ((RecordsPropertyViewModel)Row("fxRow#")).RawValue = RecordCodec.Join(["3", "4"]);
            var requests = tab.SimulatorRequests();
            var keys = string.Join(",", requests.SingleOrDefault()?.Values.Select(v => $"{v.Key}={v.Value}") ?? []);
            Check($"simulator app: switched on, it asks with every key's value in row order, a table as numbered keys ({keys})",
                keys == "fxEnabled=1,fxGain=2,fxMode=,fxNote=,fxRow1=3,fxRow2=4" && !IsLoaded(path));

            // The prompt: asked once, Don't load focused, Esc declines.
            var ask = vm.Simulators.CreateAsync(requests[0]);
            Pump(50);
            var focused = window.FocusManager?.GetFocusedElement() as Button;
            Check($"simulator app: the question names the extension, version and file, and says it runs with your permissions ('{vm.ConfirmTitle}')",
                vm.IsConfirmOpen && vm.ConfirmTitle == "Load sim-fixture's preview module?" && vm.ConfirmBody.StartsWith("It runs inside Apex with your permissions")
                && vm.ConfirmBody.EndsWith("\nExtension: sim-fixture\nVersion: 1.0\nFile: sim-fixture.dll") && !vm.ConfirmBody.Contains(path)
                && vm.ConfirmActionLabel == "Load" && vm.ConfirmCancelLabel == "Don't load" && !vm.ConfirmIsDestructive);
            Check($"simulator app: it opens on Don't load, so a reflexive Enter can't load code ({focused?.Name})",
                focused is { Name: "ConfirmCancelButton", Content: "Don't load" });
            Capture(window, Path.Combine(outDir, "68-simulator-consent.png"));
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Pump(100);
            Capture(window, Path.Combine(outDir, "69-simulator-consent-light.png"));
            Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Pump();
            Key(window, K.Escape);
            var declined = Await(ask);
            Check($"simulator app: Esc is Don't load: remembered, nothing loaded, the reason is calm ('{declined.Message}')",
                declined.Status == SimulatorStatus.Declined && !vm.IsConfirmOpen && !IsLoaded(path) && new SimulatorConsentStore(answers).AnswerFor(SimId, Sha(path)) == false);

            // Declined, everything else still works.
            var before = vm.SessionEditCount;
            Row("fxNote").RawValue = "declined";
            Check("simulator app: with the module declined, editing goes on as before", vm.SessionEditCount == before + 1 && Row("fxNote").RawValue == "declined");

            // Load module…: asked again; Enter on the default is Don't load, Tab then Enter is Load.
            var again = vm.Simulators.ReconsiderAsync(requests[0].Extension);
            Pump(50);
            Key(window, K.Enter);
            var enterResult = Await(again);
            Check($"simulator app: Load module… asks again, and Enter on the default is Don't load ({enterResult.Status})",
                enterResult.Status == SimulatorStatus.Declined && !IsLoaded(path));
            var third = vm.Simulators.ReconsiderAsync(requests[0].Extension);
            Pump(50);
            Key(window, K.Tab);
            var loadFocused = (window.FocusManager?.GetFocusedElement() as Button)?.Command == vm.AcceptConfirmCommand;
            // An edit still waiting for the journal's delay when the module first runs.
            Row("fxGain").RawValue = "3";
            bool Journaled() => Apex.Editor.Services.Session.SessionJournal.Read(JournalPath(root)).Entries
                .Any(e => e.Op == "xset" && e.Changes!.Any(c => c.Key == "fxGain" && c.New == "3"));
            var waiting = !Journaled();
            Key(window, K.Enter);
            var loaded = Await(third);
            Check($"simulator app: Tab reaches Load and Enter loads it, remembered ({loaded.Status})",
                loadFocused && loaded.Status == SimulatorStatus.Loaded && IsLoaded(path) && new SimulatorConsentStore(answers).AnswerFor(SimId, Sha(path)) == true);
            Check("simulator app: an edit still waiting for the journal was on disk before the module first ran", waiting && Journaled());

            var sim = Await(vm.Simulators.CreateAsync(tab.SimulatorRequests()[0])).Simulation;
            var frame = sim?.Step(0.5f, 0, 0);
            Check($"simulator app: the loaded module simulates the open weapon from its values ({frame})",
                frame is { Ok: true } f && f.ViewAngles.X == 1.5f && f.ViewOrigin.X == 6);

            Row("fxEnabled").RawValue = "0";
            Check("simulator app: switched off again, the asset asks for nothing", tab.SimulatorRequests().Count == 0);
        }
        catch (Exception ex)
        {
            Check($"simulator app: {ex}", false);
        }
        finally
        {
            window.Close();
            vm.Dispose();
            Check("simulator app: closing Apex frees the module", !IsLoaded(path));
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, NewScratch("no-extensions"));
            ExtensionRegistry.Clear();
        }
    }

    /// <summary>A module that can't be used says so where extension problems are said: the neutral banner, detail in its tooltip.</summary>
    private static void SimulatorBannerCheck()
    {
        var (dir, _) = SimExtension("sim-banner", "sim-fixture-x86.dll");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, dir);
        Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", NewScratch("sim-banner-settings"));
        var vm = new MainViewModel(NewScratch("sim-banner-session"));
        var window = ShowJournalWindow(vm);
        try
        {
            vm.OpenByName("wpn_ar_havoc_zm");
            var tab = vm.ActiveTab!;
            tab.AllSentinel.All.First(r => r.Key == "fxEnabled").RawValue = "1";
            var ask = vm.Simulators.CreateAsync(tab.SimulatorRequests()[0]);
            Pump(50);
            Key(window, K.Tab);
            Key(window, K.Enter);
            var result = Await(ask);
            Check($"simulator app: a module that can't load is one calm banner, the error in its tooltip ('{vm.AlertText}')",
                result.Status == SimulatorStatus.Failed && vm.IsAlertOpen && !vm.AlertIsError
                && vm.AlertText == "The sim-fixture extension: its preview module sim-fixture.dll isn't a 64-bit Windows DLL (Apex is 64-bit), so the preview is off. Editing and saving work as usual."
                && vm.AlertDetail?.Contains("Win32 error 193") == true);
        }
        catch (Exception ex)
        {
            Check($"simulator app: banner: {ex}", false);
        }
        finally
        {
            window.Close();
            vm.Dispose();
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, NewScratch("no-extensions"));
            ExtensionRegistry.Clear();
        }
    }
}
