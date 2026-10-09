using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Services.Extensions;
using Apex.Editor.Services.Extensions.Simulation;

namespace Apex.Shots;

/// <summary>
/// The preview-simulator host against what a hostile or careless extension can do: what a module pulls in besides
/// the file consent was given for, links swapped under the host, forged answers, text in the question, calls that hang
/// or change the floating-point state. Fixture modules only, copied to scratch folders.
/// </summary>
public partial class Program
{
    private static void RunSimulatorHardeningChecks()
    {
        SimulatorImportChecks();
        SimulatorLinkChecks();
        SimulatorConsentFileChecks();
        SimulatorQuestionTextChecks();
        SimulatorJournalChecks();
        SimulatorSlowCallChecks();
        SimulatorContractChecks();
        ManifestEdgeChecks();
        SimulatorWithdrawChecks();
        WeaponTechManifestCheck();
    }

    // ═══ The real extension, when it is on this machine ════════════════════

    private const string WeaponTechSim = @"J:\Github\weapon-tech-sim";

    /// <summary>
    /// weapon-tech's own manifest, laid out as its package installs it (weapon-tech\extension.json and the module), read
    /// by this loader with nothing to say, and its module passing the import rule. Read-only: both files are copied to
    /// scratch, the module is never loaded. Absent (another machine), this says so and checks nothing.
    /// </summary>
    private static void WeaponTechManifestCheck()
    {
        var manifest = Path.Combine(WeaponTechSim, "kit", "apex", ExtensionLoader.FileName);
        if (!File.Exists(manifest))
        {
            Console.WriteLine($"info  weapon-tech manifest: {manifest} isn't on this machine, so the real manifest wasn't checked");
            return;
        }
        var dir = NewScratch("weapon-tech-real");
        var folder = Path.Combine(dir, "weapon-tech");
        Directory.CreateDirectory(folder);
        File.Copy(manifest, Path.Combine(folder, ExtensionLoader.FileName));
        var module = Path.Combine(WeaponTechSim, "build", "sim", "weapon_tech_sim.dll");
        if (File.Exists(module))
            File.Copy(module, Path.Combine(folder, "weapon_tech_sim.dll"));
        else
            Console.WriteLine($"info  weapon-tech manifest: {module} isn't built, so the module's imports weren't checked");
        var (manifests, diagnostics) = ExtensionLoader.LoadAll(dir);
        Check($"weapon-tech manifest: the real kit\\apex\\extension.json loads with no diagnostics ({manifests.Count} manifest, {manifests.FirstOrDefault()?.Sections.Count} sections; {string.Join(" | ", diagnostics)})",
            manifests is [{ Id: "weapon-tech", Simulator: not null }] && diagnostics.Count == 0);
        if (File.Exists(module))
        {
            var problem = ModuleImage.Problem(File.ReadAllBytes(Path.Combine(folder, "weapon_tech_sim.dll")));
            Check($"weapon-tech manifest: the built weapon_tech_sim.dll needs only Windows ({problem ?? "no problem"})", problem is null);
        }
    }

    // ═══ A question with no one waiting for it is withdrawn ════════════════

    private static void SimulatorWithdrawChecks()
    {
        var (_, folder) = SimExtension("sim-withdraw");
        var m = ReadSim(folder)!;
        var store = Path.Combine(NewScratch("sim-withdraw-store"), SimulatorConsentStore.FileName);
        var consent = new ScriptedConsent { Held = new TaskCompletionSource<bool?>() };
        var host = NewHost(consent, store);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var a = host.GetModuleAsync(m, first.Token);
        Pump(50);
        var b = host.GetModuleAsync(m, second.Token);
        consent.Withdrawn.Register(() => consent.Held.TrySetResult(null));
        first.Cancel();
        var afterOne = consent.Withdrawn.IsCancellationRequested;
        second.Cancel();
        var result = Await(a);
        Check($"simulator withdraw: the question stays while any ask waits, and goes when the last stops waiting, remembering nothing ({afterOne}, {result.Status}, {Await(b).Status})",
            !afterOne && consent.Withdrawn.IsCancellationRequested && result.Status == SimulatorStatus.NotLoaded && !File.Exists(store)
            && host.StatusOf(m) == SimulatorStatus.NotLoaded && !IsLoaded(m.Simulator!.FullPath));

        // An ask that can't stop waiting (no token) keeps the question until it is answered.
        var held = new ScriptedConsent { Held = new TaskCompletionSource<bool?>() };
        var host2 = NewHost(held, null);
        using var third = new CancellationTokenSource();
        var c = host2.GetModuleAsync(m, third.Token);
        Pump(50);
        var d = host2.GetModuleAsync(m);
        third.Cancel();
        var kept = !held.Withdrawn.IsCancellationRequested;
        held.Held.SetResult(true);
        Check("simulator withdraw: an ask without a token keeps the question until it is answered",
            kept && Await(c).Status == SimulatorStatus.Loaded && Await(d).Status == SimulatorStatus.Loaded);
        host.Dispose();
        host2.Dispose();
    }

    // ═══ Manifests at the edges: repeated members, non-finite numbers, size ═══

    private static void ManifestEdgeChecks()
    {
        var dir = NewScratch("manifest-edges");
        Directory.CreateDirectory(Path.Combine(dir, "second"));
        File.WriteAllText(Path.Combine(dir, "second", ExtensionLoader.FileName), """
            { "apexSchema": 1, "id": "first", "id": "second", "version": "1", "targets": ["weapon"],
              "sections": [ { "title": "T", "fields": [
                { "key": "edgeHuge", "kind": "number", "default": 1e400 },
                { "key": "edgeTwice", "kind": "number", "default": 1, "default": 2 } ] } ] }
            """);
        var (manifests, diagnostics) = ExtensionLoader.LoadAll(dir);
        var m = manifests.Single();
        var huge = m.Sections[0].Fields.Single(f => f.Def.Key == "edgeHuge").Def;
        var said = string.Join("; ", diagnostics.Select(d => d.Message));
        Check($"manifest edges: a member named twice is said (the last is read), not silently last-wins ('{m.Id}'; {said})",
            m.Id == "second" && diagnostics.Count(d => d.Message.Contains("'id' more than once")) == 1
            && diagnostics.Count(d => d.Message.Contains("'default' more than once")) == 1
            && m.Sections[0].Fields.Single(f => f.Def.Key == "edgeTwice").Def.Default == "2");
        Check($"manifest edges: a default too large to be a number (1e400) is refused, not read as infinity ('{huge.Default}')",
            huge.Default == "" && diagnostics.Any(d => d.Message.Contains("edgeHuge") && d.Message.Contains("default")));

        // A megabyte of record lists: read in linear time, not by comparing every list with every other.
        var lists = new System.Text.StringBuilder();
        var count = 0;
        while (lists.Length < 900_000)
            lists.Append(count > 0 ? "," : "").Append($$"""{"key":"r{{count++}}x#","columns":[{"name":"c","kind":"text"}]}""");
        var bigDir = NewScratch("manifest-big");
        Directory.CreateDirectory(Path.Combine(bigDir, "recs"));
        File.WriteAllText(Path.Combine(bigDir, "recs", ExtensionLoader.FileName),
            $$"""{ "apexSchema": 1, "id": "recs", "version": "1", "targets": ["weapon"], "sections": [ { "title": "T", "records": [{{lists}}] } ] }""");
        var clock = Stopwatch.StartNew();
        var big = ExtensionLoader.LoadAll(bigDir).Manifests.Single();
        var ms = clock.ElapsedMilliseconds;
        Gate($"manifest edges: {count:#,0} record lists (a 1 MB manifest) load in {ms} ms, under a second",
            big.Sections[0].Records.Count == count, ms < 1000);

        // A flood of notes: the section tooltip and the banner list a hundred and say how many more.
        var unknown = string.Join(",", Enumerable.Range(0, 300).Select(i => $$"""{ "key": "flood{{i}}", "kind": "text", "zz": 1 }"""));
        var bad = string.Join(",", Enumerable.Range(0, 300).Select(i => $$"""{ "key": "bad{{i}}", "kind": "nope" }"""));
        var floodDir = NewScratch("manifest-flood");
        Directory.CreateDirectory(Path.Combine(floodDir, "flood"));
        File.WriteAllText(Path.Combine(floodDir, "flood", ExtensionLoader.FileName),
            $$"""{ "apexSchema": 1, "id": "flood", "version": "1", "targets": ["weapon"], "sections": [ { "title": "Flood", "fields": [{{unknown}},{{bad}}] } ] }""");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, floodDir);
        var vm = new Apex.Editor.ViewModels.MainViewModel(NewScratch("manifest-flood-session"));
        var window = ShowJournalWindow(vm);
        try
        {
            vm.OpenByName("wpn_ar_havoc_zm");
            var tip = vm.ActiveTab!.RailItems.First(c => c.Extension == "flood").ExtensionTip ?? "";
            var detail = vm.AlertDetail ?? "";
            Check($"manifest edges: 600 notes show as a hundred and how many more in the section tooltip, 300 left-out fields as forty in the banner ({tip.Split('\n').Length} and {detail.Split('\n').Length} lines)",
                tip.Split('\n').Length < 110 && tip.TrimEnd().EndsWith("…and 500 more.")
                && detail.Split('\n').Length == 41 && detail.TrimEnd().EndsWith("…and 260 more."));
        }
        finally
        {
            window.Close();
            vm.Dispose();
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, NewScratch("no-extensions"));
            ExtensionRegistry.Clear();
        }
    }

    [System.Runtime.InteropServices.DllImport("ucrtbase.dll", ExactSpelling = true, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private static extern uint _control87(uint value, uint mask);

    // ═══ The header's contract, as the host holds modules to it ════════════

    private static void SimulatorContractChecks()
    {
        foreach (var (dll, expect) in new[]
                 {
                     ("sim-fixture-infoabi.dll", "says it is built for version 1 of Apex's preview interface, but apex_sim_info says version 2"),
                     ("sim-fixture-idspace.dll", "says it belongs to 'sim-fixture ', not sim-fixture"),
                 })
        {
            var (_, folder) = SimExtension("sim-contract-" + dll[12..^4], dll);
            var m = ReadSim(folder)!;
            var host = NewHost(new ScriptedConsent(true), null);
            var r = Await(host.GetModuleAsync(m));
            Check($"simulator contract: {dll} is refused ('{r.Message}')", r.Status == SimulatorStatus.Failed && r.Message!.Contains(expect));
            host.Dispose();
        }

        // The floating-point state is the renderer's too (its output is bit-exact with APE's): a module that leaves
        // flush-to-zero on is put back and counted.
        var (_, fpFolder) = SimExtension("sim-contract-fpu");
        var fm = ReadSim(fpFolder)!;
        var fpHost = NewHost(new ScriptedConsent(true), null);
        var sim = Await(fpHost.CreateAsync(new SimulatorRequest(fm, Values(("fxMode", "fpu"))))).Simulation!;
        var before = _control87(0, 0);
        var frame = sim.Step(1 / 60f, 0, 0);
        var after = _control87(0, 0);
        Check($"simulator contract: a step that changes the floating-point control state is a failed step, and the state is put back ({before:x8} -> {after:x8}; '{frame.Note}')",
            before == after && !frame.Ok && frame.Note.Contains("floating-point") && sim.IsAlive);
        fpHost.Dispose();

        // A create that writes past err but returns a simulation: the module is off, and what it made is destroyed.
        var (_, overFolder) = SimExtension("sim-contract-create-overrun");
        var om = ReadSim(overFolder)!;
        var overHost = NewHost(new ScriptedConsent(true), null);
        var liveBefore = -1;
        var over = Await(overHost.CreateAsync(new SimulatorRequest(om, Values(("fxGain", "1")))));
        liveBefore = FixtureCount(om.Simulator!.FullPath, "fixture_live");
        over.Simulation?.Dispose();
        var overrun = Await(overHost.CreateAsync(new SimulatorRequest(om, Values(("fxMode", "create-overrun")))));
        Check($"simulator contract: a create that writes past its message space but returns a simulation is turned off and that simulation destroyed ({overrun.Status}, live {FixtureCount(om.Simulator.FullPath, "fixture_live")})",
            liveBefore == 1 && overrun is { Simulation: null, Status: SimulatorStatus.TurnedOff } && FixtureCount(om.Simulator.FullPath, "fixture_live") == 0);
        overHost.Dispose();
    }

    // ═══ Slow calls: no watchdog, but a module that stalls the UI is counted and named ═══

    private static void SimulatorSlowCallChecks()
    {
        var (_, folder) = SimExtension("sim-slow");
        var m = ReadSim(folder)!;
        var host = NewHost(new ScriptedConsent(true), null);
        var said = new List<(string Message, string Detail)>();
        host.Reported += (d, detail) => said.Add((d.Message, detail));
        var slow = Await(host.CreateAsync(new SimulatorRequest(m, Values(("fxMode", "slow-step"))))).Simulation!;
        var first = slow.Step(1 / 60f, 0, 0);
        Check($"simulator slow calls: a step over 50 ms still draws, and is named once in a calm diagnostic ('{string.Join(" | ", said.Select(s => s.Message))}')",
            first.Ok && slow.IsAlive && said.Count == 1 && said[0].Message.Contains("took") && said[0].Message.Contains("ms for one step")
            && !said[0].Message.Contains('!') && said[0].Detail.Contains("sim-fixture.dll"));
        var frames = Enumerable.Range(0, SimulatorHost.MaxFailuresInARow - 1).Select(_ => slow.Step(1 / 60f, 0, 0)).ToList();
        Check($"simulator slow calls: {SimulatorHost.MaxFailuresInARow} slow steps in a row count as failures and turn the module off ('{string.Join(" | ", said.Select(s => s.Message))}')",
            !slow.IsAlive && !frames[^1].Ok && said.Count == 2 && said[1].Message.Contains("too slow") && host.StatusOf(m) == SimulatorStatus.TurnedOff);
        host.Dispose();

        var (_, createFolder) = SimExtension("sim-slow-create");
        var cm = ReadSim(createFolder)!;
        var createHost = NewHost(new ScriptedConsent(true), null);
        var createSaid = new List<string>();
        createHost.Reported += (d, _) => createSaid.Add(d.Message);
        var made = Await(createHost.CreateAsync(new SimulatorRequest(cm, Values(("fxMode", "slow-create"))))).Simulation;
        Check($"simulator slow calls: a create over a second is named once and counted ('{string.Join(" | ", createSaid)}')",
            made is { IsAlive: true } && createSaid.Count == 1 && createSaid[0].Contains("ms to start a simulation"));
        createHost.Dispose();
    }

    // ═══ A crash in create loses nothing ═══════════════════════════════════

    /// <summary>
    /// A value that makes create crash must already be in the journal: create runs 150 ms after an edit, the journal
    /// writes 250 ms after one, so the host flushes before every create, not only before the first load.
    /// </summary>
    private static void SimulatorJournalChecks()
    {
        var (dir, folder) = SimExtension("sim-journal");
        var path = Path.Combine(folder, "sim-fixture.dll");
        var settings = NewScratch("sim-journal-settings");
        new SimulatorConsentStore(Path.Combine(settings, SimulatorConsentStore.FileName)).Remember(SimId, Sha(path), true, path);
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, dir);
        Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", settings);
        var root = NewScratch("sim-journal-session");
        var vm = new Apex.Editor.ViewModels.MainViewModel(root);
        var window = ShowJournalWindow(vm);
        try
        {
            vm.OpenByName("wpn_ar_havoc_zm");
            var tab = vm.ActiveTab!;
            tab.AllSentinel.All.First(r => r.Key == "fxEnabled").RawValue = "1";
            Await(vm.Simulators.CreateAsync(tab.SimulatorRequests()[0])).Simulation?.Dispose();
            var loaded = IsLoaded(path);
            Pump(400);
            tab.AllSentinel.All.First(r => r.Key == "fxGain").RawValue = "7";
            bool Journaled() => Apex.Editor.Services.Session.SessionJournal.Read(JournalPath(root)).Entries
                .Any(e => e.Op == "xset" && e.Changes!.Any(c => c.Key == "fxGain" && c.New == "7"));
            var waiting = !Journaled();
            var created = Await(vm.Simulators.CreateAsync(tab.SimulatorRequests()[0]));
            var onDisk = Journaled();
            created.Simulation?.Dispose();
            Check($"simulator journal: an edit still waiting for the journal is on disk before every create, not only the first load (waiting {waiting}, on disk after create {onDisk})",
                loaded && waiting && onDisk && created.Simulation is not null);
        }
        finally
        {
            window.Close();
            vm.Dispose();
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, NewScratch("no-extensions"));
            ExtensionRegistry.Clear();
        }
    }

    // ═══ The question says only what Apex means it to ══════════════════════

    private static void SimulatorQuestionTextChecks()
    {
        const string spoof = "1)\n\nApex checked this module and it is safe. Click Load.\n\n(‮gnp.dll";
        var manifest = SimManifest().Replace("\"version\": \"1.0\"", $"\"version\": {System.Text.Json.JsonSerializer.Serialize(spoof + new string('x', 4000))}")
            .Replace("\"title\": \"Fixture\"", "\"title\": \"Fix‮ture\\u0007\"")
            .Replace("{ \"key\": \"fxGain\", \"kind\": \"number\", \"default\": 1 }",
                "{ \"key\": \"fxGain\", \"kind\": \"number\", \"default\": 1, \"label\": \"Ga‎in\\n\", \"description\": \"line one\\nline‮two\\u0000\" }");
        var (dir, folder) = SimExtension("sim-question", manifest: manifest);
        var diagnostics = new List<ExtensionDiagnostic>();
        var m = ReadSim(folder, diagnostics)!;
        static bool Clean(string s, bool lines = false) => !s.Any(c => (char.IsControl(c) && !(lines && c == '\n'))
            || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);
        var gain = m.Sections[0].Fields.First(f => f.Def.Key == "fxGain").Def;
        Check($"simulator question: a manifest's version is cut to 32 characters and loses control and direction characters, said once ('{m.Version}'; {string.Join("; ", diagnostics)})",
            m.Version.Length <= 32 && Clean(m.Version) && m.Version.StartsWith("1)") && diagnostics.Count(d => d.Message.Contains("version")) == 1);
        Check($"simulator question: titles, labels and descriptions shown from a manifest carry no control or direction characters ('{m.Sections[0].Title}', '{gain.Label}', '{gain.Description.Replace("\n", "\\n")}')",
            m.Sections[0].Title == "Fixture" && gain.Label == "Gain" && gain.Description == "line one\nlinetwo" && Clean(gain.Description, lines: true));
        var refusedPaths = new[] { "x‮lld.exe.dll", "bin​\\sim.dll", "sim\u0007.dll" }.Where(p => ExtensionLoader.CheckModulePath(folder, p) is null).ToList();
        Check($"simulator question: a module path with control, format or direction characters is refused ({refusedPaths.Count} accepted)", refusedPaths.Count == 0);

        // In the app: the version on a line of its own, nothing else from the manifest in the question.
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, dir);
        Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", NewScratch("sim-question-settings"));
        var vm = new Apex.Editor.ViewModels.MainViewModel(NewScratch("sim-question-session"));
        var window = ShowJournalWindow(vm);
        try
        {
            vm.OpenByName("wpn_ar_havoc_zm");
            var tab = vm.ActiveTab!;
            tab.AllSentinel.All.First(r => r.Key == "fxEnabled").RawValue = "1";
            var ask = vm.Simulators.CreateAsync(tab.SimulatorRequests()[0]);
            Pump(50);
            var lines = vm.ConfirmBody.Split('\n');
            Check($"simulator question: the version is on a line of its own and can't add lines to the question ({lines.Length} lines: '{vm.ConfirmBody.Replace("\n", "⏎")}')",
                vm.IsConfirmOpen && lines.Length == 5 && lines[3] == $"Version: {m.Version}" && Clean(vm.ConfirmBody, lines: true)
                && vm.ConfirmTitle == "Load sim-fixture's preview module?");
            Key(window, Avalonia.Input.Key.Escape);
            Await(ask);
        }
        finally
        {
            window.Close();
            vm.Dispose();
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, NewScratch("no-extensions"));
            ExtensionRegistry.Clear();
        }
    }

    // ═══ The answers file: written by Apex, for this user, outside %AppData%\Apex ═══

    private static void SimulatorConsentFileChecks()
    {
        var (_, folder) = SimExtension("sim-forged");
        var m = ReadSim(folder)!;
        var path = m.Simulator!.FullPath;
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        var store = Path.Combine(NewScratch("sim-forged-store"), SimulatorConsentStore.FileName);
        // What an extension archive unpacked over the settings folder could carry: answers Apex never wrote.
        File.WriteAllText(store, $$"""
            { "modules": [ { "id": "{{SimId}}", "sha256": "{{sha}}", "load": true, "file": "{{path.Replace("\\", "\\\\")}}", "answeredUtc": "2026-01-01T00:00:00Z" } ] }
            """);
        var consent = new ScriptedConsent(false);
        var host = NewHost(consent, store);
        var forged = Await(host.GetModuleAsync(m));
        host.Dispose();
        Check($"simulator consent file: answers Apex didn't write are no answers, so the user is asked ({forged.Status}, asked {consent.Asked.Count})",
            forged.Status == SimulatorStatus.Declined && consent.Asked.Count == 1 && !IsLoaded(path));
        var bytes = File.ReadAllBytes(store);
        Check("simulator consent file: Apex keeps answers protected for this Windows user (not readable text), and reads its own back",
            !System.Text.Encoding.UTF8.GetString(bytes).Contains(SimId) && !System.Text.Encoding.Unicode.GetString(bytes).Contains(SimId)
            && new SimulatorConsentStore(store).AnswerFor(SimId, sha) == false);

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Settings: Environment.GetEnvironmentVariable("APEX_SETTINGS_DIR"),
            Persist: Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        string? user;
        try
        {
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
            Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", null);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", null);
            user = SimulatorConsentStore.ForUser().FilePath;
        }
        finally
        {
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", saved.Settings);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
        }
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex", SimulatorConsentStore.FileName);
        Check($"simulator consent file: the user's answers live in %LocalAppData%\\Apex, never under %AppData%\\Apex where extensions are unpacked ({user})",
            user == local && !user.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase));
    }

    private static void Junction(string link, string target, bool replace = false) =>
        Process.Start(new ProcessStartInfo("cmd.exe", (replace ? $"/c rmdir \"{link}\" && " : "/c ") + $"mklink /J \"{link}\" \"{target}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })?.WaitForExit();

    // ═══ Links: what loads is the file that was hashed ═════════════════════

    private static void SimulatorLinkChecks()
    {
        // An extension folder that is itself a junction is left out when read: a link could point anywhere.
        var real = Path.Combine(NewScratch("sim-link-real"), SimId);
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, ExtensionLoader.FileName), SimManifest());
        File.Copy(Path.Combine(FixtureSimulator, "built", "sim-fixture.dll"), Path.Combine(real, "sim-fixture.dll"));
        var linkedDir = NewScratch("sim-link-folder");
        Junction(Path.Combine(linkedDir, SimId), real);
        var (manifests, diagnostics) = ExtensionLoader.LoadAll(linkedDir);
        Check($"simulator links: an extension folder that is a junction is left out, said once ({string.Join("; ", diagnostics)})",
            Directory.Exists(Path.Combine(linkedDir, SimId)) && manifests.Count == 0
            && diagnostics.Count == 1 && diagnostics[0].Problem == ExtensionProblem.Disabled && diagnostics[0].Message.Contains("junction or symbolic link"));
        Check("simulator links: and a module whose extension folder is a link is refused at load as well",
            ExtensionLoader.CheckModulePath(Path.Combine(linkedDir, SimId), "sim-fixture.dll") is { } why && why.Contains("junction"));

        // A link above the extension folder retargeted between the question and LoadLibrary (the journal flush is the
        // window): the file that loads is the one held and hashed, not whatever the path names by then.
        var good = NewScratch("sim-swap-good");
        var evil = NewScratch("sim-swap-evil");
        foreach (var (root, dll) in new[] { (good, "sim-fixture.dll"), (evil, "sim-fixture-wrongid.dll") })
        {
            Directory.CreateDirectory(Path.Combine(root, SimId));
            File.WriteAllText(Path.Combine(root, SimId, ExtensionLoader.FileName), SimManifest());
            File.Copy(Path.Combine(FixtureSimulator, "built", dll), Path.Combine(root, SimId, "sim-fixture.dll"));
        }
        var parent = Path.Combine(NewScratch("sim-swap"), "extensions");
        Junction(parent, good);
        var m = ExtensionLoader.LoadAll(parent).Manifests.Single();
        var swapped = false;
        var consent = new ScriptedConsent(true);
        var host = NewHost(consent, null, () =>
        {
            Junction(parent, evil, replace: true);
            swapped = File.ReadAllBytes(Path.Combine(parent, SimId, "sim-fixture.dll")).SequenceEqual(File.ReadAllBytes(Path.Combine(evil, SimId, "sim-fixture.dll")));
        });
        var r = Await(host.GetModuleAsync(m));
        var goodPath = Path.Combine(good, SimId, "sim-fixture.dll");
        Check($"simulator links: retargeting a junction above the module after it was hashed still loads the hashed file ({r.Status}: '{r.Message}'; swapped {swapped})",
            swapped && r is { Status: SimulatorStatus.Loaded, Module: { } loaded } && consent.Asked.Count == 1 && loaded.Sha256 == consent.Asked[0].Sha256
            && IsLoaded(goodPath) && !IsLoaded(Path.Combine(evil, SimId, "sim-fixture.dll")));
        host.Dispose();
    }

    // ═══ Imports: a module is one self-contained file ══════════════════════

    private static void SimulatorImportChecks()
    {
        var helper = Path.Combine(FixtureSimulator, "built", "sim-helper.dll");
        foreach (var (dll, how) in new[] { ("sim-fixture-sibling.dll", "imports"), ("sim-fixture-delay.dll", "delay-loads") })
        {
            var (_, folder) = SimExtension("sim-import-" + how, dll);
            var beside = Path.Combine(folder, "sim-helper.dll");
            File.Copy(helper, beside);
            var m = ReadSim(folder)!;
            var consent = new ScriptedConsent(true);
            var host = NewHost(consent, null);
            var said = new List<string>();
            host.Reported += (d, _) => said.Add(d.Message);
            var r = Await(host.GetModuleAsync(m));
            Check($"simulator imports: a module that {how} a DLL from its own folder is refused before it is asked about or loaded ('{r.Message}')",
                r.Status == SimulatorStatus.Failed && r.Message!.Contains("needs sim-helper.dll, which isn't part of Windows")
                && consent.Asked.Count == 0 && !IsLoaded(m.Simulator!.FullPath) && !IsLoaded(beside) && said.Count == 1);
            host.Dispose();
        }

        var system = Environment.SystemDirectory;
        var allowed = new[] { "kernel32.dll", "KERNEL32.dll", "ntdll.dll", "dbghelp.dll", "api-ms-win-crt-runtime-l1-1-0.dll", "ext-ms-win-ntuser-window-l1-1-0.dll" };
        var refused = new[] { "sim-helper.dll", @"..\kernel32.dll", Path.Combine(system, "kernel32.dll"), "kernel32.dll‮", "", "." };
        var wrong = allowed.Where(n => !ModuleImage.IsWindows(n)).Concat(refused.Where(ModuleImage.IsWindows)).ToList();
        Check($"simulator imports: Windows supplies API sets, KnownDLLs and System32's files by bare name, nothing else ({string.Join(", ", wrong)})",
            wrong.Count == 0 && File.Exists(Path.Combine(system, "dbghelp.dll")));

        var built = Directory.GetFiles(Path.Combine(FixtureSimulator, "built"), "sim-fixture*.dll")
            .Where(f => !f.Contains("sibling") && !f.Contains("delay") && !f.Contains("x86")).ToList();
        var problems = built.Select(f => (File: Path.GetFileName(f), Problem: ModuleImage.Problem(File.ReadAllBytes(f)))).Where(p => p.Problem is not null).ToList();
        Check($"simulator imports: a module that needs only Windows passes ({built.Count} fixtures; {string.Join("; ", problems)})",
            built.Count >= 6 && problems.Count == 0
            && ModuleImage.Imports(File.ReadAllBytes(Path.Combine(FixtureSimulator, "built", "sim-fixture-delay.dll")))!.Contains("sim-helper.dll"));

        // Bytes that only look like a module: refused, never an exception.
        var torn = File.ReadAllBytes(Path.Combine(FixtureSimulator, "built", "sim-fixture-sibling.dll"))[..0x200];
        var garbage = (byte[])File.ReadAllBytes(Path.Combine(FixtureSimulator, "built", "sim-fixture-sibling.dll")).Clone();
        new Random(7).NextBytes(garbage.AsSpan(0x200));
        Check($"simulator imports: a truncated or damaged module is refused with a sentence ('{ModuleImage.Problem(torn)}', '{ModuleImage.Problem(garbage)}')",
            ModuleImage.Problem(torn) is { } t && ModuleImage.Problem(garbage) is { } g && t.Length > 0 && g.Length > 0);
    }
}
