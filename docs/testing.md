# Testing Apex

Apex's checks live in `Apex.Shots`, a headless harness that drives the real app (real windows, real input, real
screenshots) and prints one line per check: `PASS  …` or `FAIL  …`, then `ALL CHECKS PASSED` or `N CHECK(S) FAILED`.

The full run is a list of named **check groups** (`Apex.Shots/Suite.cs`), always run in the same order. A group can be
run on its own, in parallel with others, or timed. Nothing here changes what a check asserts.

## Which run, when

| Run | Command | What it is | Wall time |
|---|---|---|---|
| Fast tier | `.\Apex.Shots\run-suite.ps1 -Fast` | every check that needs no whole-install load, no real D3D and no timing, in 5 parallel shards | about 1.5 min |
| Full, functional | `.\Apex.Shots\run-suite.ps1 -NoTiming` | every group in 5 parallel shards; timing gates print their times but don't gate | about 1.5 min |
| Full | `.\Apex.Shots\run-suite.ps1` | the untimed groups in parallel shards, then the timing tier alone | about 5.5 min |
| Timing tier | `.\Apex.Shots\run-suite.ps1 -Timing` | the groups with timing gates, in one process, alone, on a quiet machine | about 5 min |
| One process | `dotnet run -c Release --project Apex.Shots` | the full run as it always was | about 8 min |

Times are from a 16-thread machine with nothing else running; see [Measurements](#measurements). The single-process run
and every existing flag (`--save`, `--records`, `--extensions`, `--recoil`, `--perf`, `--perf-live`, …) behave as before.

`run-suite.ps1` builds Release first (`-NoBuild` skips it), writes each process's log under
`%TEMP%\apex-suite\<run>\`, prints the harness's FAIL lines and one `SUMMARY` line, and exits non-zero on any failed
check, on a shard that crashed or didn't finish its groups, and on a partition that drops or repeats a group.
`-Shards N` overrides the shard count (default: a third of the logical cores, between 2 and 6).

## The tiers

Every check belongs to exactly one tier, decided by what it needs:

- **Fast**: checks in a group marked `Tier.Fast` that aren't timing gates. Fast groups run on mock data or on a small temp
  install (deffiles and a few GDTs copied to `%TEMP%`), headless, with no D3D device.
- **Full only**: checks in a group marked `Tier.Full` that aren't timing gates. Those groups index the whole install
  (`live-timing`, `live-smoke`), render with real D3D11 (`recoil-render`), or exist to time things (`perf`, whose
  functional checks ride on its timed runs).
- **Timing**: every timing gate, wherever it is. A timing gate is a check whose verdict compares a measured duration
  with a budget (`PerfBudgets`, "within a frame", "under 1 ms"). A check that only reports a time in its label, or that
  checks a behaviour defined by time (a debounce, a probe timeout, a script's time limit), is not a gate.

| Group | Tier | Timing gates | Needs |
|---|---|---|---|
| main | fast | | mock data, screenshots of every surface |
| shell, ref, command, scroll, tabs, layout, search | fast | | mock data, real input |
| journal | fast | yes | mock data, session journal in temp |
| save | fast | | GDT save path on temp copies of 35 install GDTs |
| save-ui, placement, buttons, fields, lists, notetracks, install, anim-layout, extensions, records, hardening, simulator, editor-pass | fast | some (see `Gates` in Suite.cs) | a temp install |
| recoil, recoil-draw, recoil-app | fast | recoil-draw | pure, a test window, a temp install |
| live-timing | full | yes | the whole install (palette and search menu over 125k assets) |
| perf | full | yes | mock data, timed interactions |
| recoil-render | full | | real D3D11 and APE's renderer data (read-only) |
| live-smoke | full | | the whole install, read-only |

`--list-groups` prints the list with each group's tier, gates and cost.

How the tiers run:

- The fast tier and `-NoTiming` defer gates (`--defer-gates`): a gate checks what it asserts besides time and prints its
  time on a `TIME` line instead of a verdict. Those runs say nothing about performance.
- The full run gives the shards only the groups without gates (`--untimed`), and the timing tier (`--timing`) the groups
  with gates, functional checks and all. Together that is every group once; the driver checks the partition every run.
- The timing tier waits up to `-QuietWaitSec` for the machine to be quiet (no other Apex or Apex.Shots process, CPU
  under `-MaxLoad`, default 20%, over 30 s). If it isn't, it runs anyway, prints a NOISY banner and marks the result
  noisy: a failing gate then says little, so run `-Timing` again when the machine is quiet. (This machine idles at
  14-19% with nothing of ours running, which is why the limit isn't 10%.)

## Running pieces by hand

```
Apex.Shots.exe [shots-folder] [--group a,b] [--skip a,b] [--fast | --timing | --untimed] [--shard i/N]
               [--defer-gates] [--timings] [--results file.json] [--list-groups]
```

- `--group records,extensions` runs those groups, in suite order.
- `--shard i/N` runs the i-th of N shards of whatever else is selected. Shards are balanced by the cost table in
  `Suite.cs` (most costly first, each to the least-loaded shard), so every process computes the same split. A stale
  cost costs time, never coverage.
- `--timings` prints the 40 slowest checks (time since the check before it in its group), each group's total, the
  setup cost, and a fresh cost table; it writes the same as JSON to `%TEMP%`. Off, it costs nothing.
- `--results file.json` writes every check's group, label, verdict (pass, fail, deferred) and time.

## Adding a check

1. Put it in the group whose surface it checks (the `Run…Checks` method that group calls). A new surface gets a new
   group: add it to `Groups` in `Suite.cs` where it belongs in the run's order, with its tier and a cost.
2. Pick the tier by what it needs, not by how important it is: whole-install loads and real D3D are `Tier.Full`;
   everything else is `Tier.Fast`.
3. If its verdict compares a duration with a budget, use `Gate(label, ok, inBudget)` instead of `Check`, measure UI-thread
   time (`UiThreadMs`, or `Time` in PerfGates.cs) after a warm-up, and gate a median (with a p95 if you need the tail), never
   one sample. Then mark its group `Gates: true`. A `Gate` in a group without it fails, so it can't slip out of the
   timing tier.
4. Make the group pass alone: `Apex.Shots.exe --group <name>`. A check that passes only after another group ran is
   broken in a shard.
5. Write only under the run's temp folder (`NewScratch`, `SaveRoot`), never under the BO3 install, never to a fixed
   path in `%TEMP%`: other runs share the machine.

## Machine etiquette

- Run one suite at a time on the machine. Shards already use most of it; two suites at once make both slow and the
  timing gates meaningless.
- Before the timing tier, or any timing you report, check the machine is quiet: no other `Apex.Shots`, `Apex` or
  `dotnet test`/`dotnet run` process, CPU near its idle level for 30 s. `run-suite.ps1` checks this for the timing tier.
- Never run two harnesses in the same working folder with the default shots folder: they write the same PNGs.
  `run-suite.ps1` gives each process its own.

## Baselines

A full result is per commit. Take it once and keep it:

```
.\Apex.Shots\run-suite.ps1 -SaveBaseline          # on the base commit: saves %TEMP%\apex-suite\baselines\<sha>-full.json
.\Apex.Shots\run-suite.ps1 -Compare <base-sha>    # later: REGRESSED / FIXED / STILL failing / NEW / GONE against it
```

`-Compare` also takes a path, including a single-process `--results` file. It compares checks by group and name with
numbers and temp paths ignored, as multisets, so a check that runs twice must be there twice. Baselines are kept per
tier (`-Fast -SaveBaseline` saves a fast one).

## Measurements

Release build, 16 threads, 47 GB, Windows 11, 6-7 October 2026; CPU 14-19% over the 30 s before each run, with no
other harness running.

| Run | Wall |
|---|---|
| Single process, before this work (0599889) | 859 s, 882 s |
| Single process, after | 454 s |
| Full: 5 untimed shards (30-42 s) then the timing tier (288 s) | 361 s |
| Full, functional (`-NoTiming`, 5 shards) | 108 s |
| Fast tier (5 shards) | 97 s, 109 s |
| Timing tier alone (`-Timing`, plus its 30 s quiet check) | 304 s |

Nearly all of what the single process saved (405-428 s) was three waits that ran out instead of finishing (420 s): the live palette and
search timings each waited their full 3 minutes because they restored the user's own session journal, and the recoil
app's second start waited 60 s for a status the restored session had replaced. A process starts in about 0.1 s; the
setup that repeats is each temp-install group copying and parsing the deffiles (1-3 s each).

The heaviest groups alone: records 64 s, recoil-app 37 s, anim-layout 31 s, save 30 s, perf 30 s, journal 29 s,
extensions 29 s. The timing tier is most of the full run's wall time because it is serial by design; its groups'
own timed loops (the perf gates' 30-run medians, the journal's 420 keystrokes, the records' 40 opens) are about half of
it, and the rest is their setup and functional checks, which have to run with them.

Known flaky gates, 10 runs each of `--group anim-layout,records,live-timing` on a quiet machine:

| Gate | Before | After | What changed |
|---|---|---|---|
| palette timing (every letter within a frame) | 10 of 10 failed, slowest median 20-23 ms | 0 of 10, 9-12 ms | warmed until tiered up, UI-thread time |
| + Add at frame (p95 ≤ 32 ms) | 3 of 10 failed, p95 27-45 ms | 0 of 10, p95 17-21 ms | UI-thread time; heap collected first |
| a press on the lanes | 0 of 10 | 0 of 10 | UI-thread time |
| 24-row kick table adds less than a frame | 2 of 10 failed | 3 of 10 failed | nothing: see below |
| search menu timing | 0 of 10 | 0 of 10 | |

The palette failed every time in this run and passed in the full run only because the command checks before it had
already warmed its code. The kick-table gate compares two medians of 15 opens of about 150 ms; those medians wander
about 20 ms from run to run, more than the 16 ms it gates, while what 24 rows add is about nothing. Pairing each open
with the next one didn't help (the differences ranged -25 to +27 ms). It needs more samples (about 100 pairs, roughly
30 s more) or a different statistic; that is a decision about the gate, not its measurement, so it is unchanged.
