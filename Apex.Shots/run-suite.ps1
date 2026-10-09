# Runs Apex.Shots fast: the full run cut into shards that run in parallel, then the timing tier alone. See docs/testing.md.
#
#   .\Apex.Shots\run-suite.ps1                  # full: the untimed groups in parallel shards, then the timing groups alone
#   .\Apex.Shots\run-suite.ps1 -NoTiming        # every group in parallel shards, timing gates printed but not gated
#   .\Apex.Shots\run-suite.ps1 -Fast            # the fast tier (what to run per change)
#   .\Apex.Shots\run-suite.ps1 -Timing          # the timing tier alone (quiet machine)
#   .\Apex.Shots\run-suite.ps1 -SaveBaseline    # keep the result as this commit's baseline
#   .\Apex.Shots\run-suite.ps1 -Compare main    # say what changed against a saved baseline (commit, branch or file)
#
# Prints the harness's FAIL lines, one summary, and 'ALL CHECKS PASSED' or 'N CHECK(S) FAILED'; exits non-zero on any
# failed check, on a shard that crashed or didn't finish its groups, and on a partition that drops or repeats a group.
param(
    [int]$Shards = 0,
    [switch]$Fast,
    [switch]$Timing,
    [switch]$NoTiming,
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$SaveBaseline,
    [string]$Compare,
    [double]$MaxLoad = 20,
    [int]$QuietWaitSec = 0,
    [string]$Out
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$suiteTemp = Join-Path ([IO.Path]::GetTempPath()) 'apex-suite'
if (-not $Out) { $Out = Join-Path $suiteTemp ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID) }
New-Item -ItemType Directory -Force $Out | Out-Null
$exe = Join-Path $root "Apex.Shots\bin\$Configuration\net10.0\Apex.Shots.exe"
$wall = [Diagnostics.Stopwatch]::StartNew()
$tier = if ($Fast) { 'fast' } elseif ($Timing) { 'timing' } elseif ($NoTiming) { 'full-no-timing' } else { 'full' }

if (-not $NoBuild) {
    $build = & dotnet build (Join-Path $root 'Apex.Shots') -c $Configuration -v q -nologo 2>&1
    if ($LASTEXITCODE -ne 0) { $build | Select-String ' error ' | ForEach-Object { $_.Line }; 'BUILD FAILED'; exit 1 }
}
if (-not (Test-Path $exe)) { throw "No harness at $exe (build it, or drop -NoBuild)." }

# ── The machine ─────────────────────────────────────────────────────────────
$script:mine = @()
function Get-Others {
    # Harness runs that aren't this script's, and Apex instances doing something (one left open and idle costs nothing):
    # they share the CPU with ours.
    $apps = @(Get-Process -Name 'Apex' -ErrorAction SilentlyContinue | ForEach-Object { [pscustomobject]@{ P = $_; Cpu = $_.CPU } })
    if ($apps.Count) { Start-Sleep -Seconds 2 }
    @(Get-Process -Name 'Apex.Shots' -ErrorAction SilentlyContinue | Where-Object { $script:mine -notcontains $_.Id }) +
        @($apps | Where-Object { $_.P.Refresh(); $_.P.CPU - $_.Cpu -gt 0.1 } | ForEach-Object P)
}
function Measure-Load([int]$seconds) {
    # Percent of all cores busy, averaged over the window (Task Manager's "Utility" reads higher: it scales by clock).
    $n = [Math]::Max(1, [int]($seconds / 2))
    try { $samples = (Get-Counter '\Processor(_Total)\% Processor Time' -SampleInterval 2 -MaxSamples $n -ErrorAction Stop).CounterSamples.CookedValue }
    catch { $samples = 1..$n | ForEach-Object { (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average; Start-Sleep -Seconds 2 } }
    ($samples | Measure-Object -Average).Average
}
function Start-Harness([string[]]$harnessArgs, [string]$name) {
    $quoted = $harnessArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }
    $p = Start-Process -FilePath $exe -ArgumentList $quoted -WorkingDirectory $Out -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $Out "$name.log") -RedirectStandardError (Join-Path $Out "$name.err")
    $script:mine += $p.Id
    $p
}
function Get-Groups([string[]]$selection) {
    @(& $exe --list-groups @selection | Where-Object { $_ -like 'GROUP *' } | ForEach-Object { $_.Split(' ')[1] })
}
# Normalized check names: what a label reports in brackets and quotes (values, statuses), its numbers and temp paths
# vary from run to run, and with what ran before in the process; the words around them name the check.
function Normalize([string]$label) {
    $l = [regex]::Replace($label, '[A-Za-z]:\\[^\s''",;)]*?\\Temp\\[^\s''",;)]*', '<temp>')
    do { $was = $l; $l = [regex]::Replace($l, '\([^()]*\)', '(…)') } while ($l -ne $was)
    $l = [regex]::Replace($l, "'[^']*'", "'…'")
    [regex]::Replace($l, '\d+(?:[.,]\d+)*', '#')
}

$problems = [Collections.Generic.List[string]]::new()
$final = [Collections.Generic.List[object]]::new()
$setupSeconds = [Collections.Generic.List[double]]::new()

# What runs where: the shards take the untimed groups (every group with -NoTiming, the fast ones with -Fast), the timing
# tier the groups with timing gates. Together: every group of the tier, each once.
[string[]]$shardSelection = @(switch ($tier) { 'fast' { '--fast' } 'full-no-timing' { } default { '--untimed' } })
$runShards = $tier -ne 'timing'
$runTiming = $tier -in 'full', 'timing'
$all = Get-Groups @(if ($Fast) { '--fast' })
$timingGroups = if ($runTiming) { Get-Groups @('--timing') } else { @() }
$shardGroups = if ($runShards) { Get-Groups $shardSelection } else { @() }

if ($runShards) {
    if ($Shards -le 0) { $Shards = [Math]::Max(2, [Math]::Min(6, [int][Math]::Floor([Environment]::ProcessorCount / 3))) }
    $Shards = [Math]::Min($Shards, $shardGroups.Count)
    $plan = @{}
    for ($i = 1; $i -le $Shards; $i++) { $plan[$i] = Get-Groups ($shardSelection + @('--shard', "$i/$Shards")) }
}

# The partition, from the harness's own split: every group of the tier in exactly one place.
$placed = @(@(if ($runShards) { $plan.Values | ForEach-Object { $_ } }) + @($timingGroups))
$expected = if ($tier -eq 'timing') { $timingGroups } else { $all }
$dupes = @($placed | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
$missing = @($expected | Where-Object { $placed -notcontains $_ })
if ($dupes.Count -or $missing.Count) { $problems.Add("partition: repeated [$($dupes -join ', ')], left out [$($missing -join ', ')]") }
else { "COVERAGE  $($expected.Count) groups: $(if ($runShards) { "$($shardGroups.Count) in $Shards parallel shards" })$(if ($runShards -and $runTiming) { ', ' })$(if ($runTiming) { "$($timingGroups.Count) in the timing tier" }), each exactly once" }

function Read-Run([string]$name, [string[]]$planned, [string]$tierName, $process, [double]$seconds) {
    $json = Join-Path $Out "$name.json"
    $log = Join-Path $Out "$name.log"
    if (-not (Test-Path $json)) {
        $problems.Add("$name left no results (exit $($process.ExitCode)): it crashed or was stopped; its log: $log")
        Get-Content $log -Tail 15 -ErrorAction SilentlyContinue | ForEach-Object { "      $_" }
        return
    }
    $r = Get-Content $json -Raw | ConvertFrom-Json
    $done = @($r.groups | Where-Object completed | ForEach-Object name)
    $notDone = @($planned | Where-Object { $done -notcontains $_ })
    if ($notDone.Count) { $problems.Add("$name did not finish [$($notDone -join ', ')]; its log: $log") }
    foreach ($c in $r.checks) { $final.Add([pscustomobject]@{ group = $c.group; label = $c.label; status = $c.status; tier = $tierName }) }
    $setup = ([double]$r.processStartToMainMs + [double]$r.sessionStartMs) / 1000
    $setupSeconds.Add($setup)
    "{0,-12} {1,6:N0} s  {2,4} checks  {3} failed  setup {4:N1} s  [{5}]" -f $name, $seconds, @($r.checks).Count, $r.failures, $setup, ($planned -join ' ')
}

if ($runShards) {
    $others = Get-Others
    if ($others.Count) { "WARN  $($others.Count) other Apex/Apex.Shots process(es) running ($(($others | ForEach-Object { "$($_.ProcessName) $($_.Id)" }) -join ', ')): the shards will be slower" }
    [string[]]$defer = @(if ($tier -ne 'full') { '--defer-gates' })
    "RUN  $Shards shards ($tier$(if ($defer) { ', timing gates printed, not gated' })); logs in $Out"
    $procs = for ($i = 1; $i -le $Shards; $i++) {
        $p = Start-Harness ($shardSelection + $defer + @('--shard', "$i/$Shards", '--results', (Join-Path $Out "shard-$i.json"), (Join-Path $Out "shots-$i"))) "shard-$i"
        [pscustomobject]@{ Index = $i; Process = $p; Started = $wall.Elapsed.TotalSeconds; Seconds = 0.0 }
    }
    while (@($procs | Where-Object { -not $_.Process.HasExited }).Count) {
        foreach ($s in $procs) { if ($s.Process.HasExited -and -not $s.Seconds) { $s.Seconds = $wall.Elapsed.TotalSeconds - $s.Started } }
        Start-Sleep -Milliseconds 250
    }
    foreach ($s in $procs) { if (-not $s.Seconds) { $s.Seconds = $wall.Elapsed.TotalSeconds - $s.Started } }
    foreach ($s in $procs) { Read-Run "shard-$($s.Index)" $plan[$s.Index] 'shard' $s.Process $s.Seconds }
}

# ── The timing tier: last, alone, in one process ────────────────────────────
$noisy = $false
if ($runTiming) {
    $deadline = (Get-Date).AddSeconds($QuietWaitSec)
    while ($true) {
        $others = Get-Others
        $load = Measure-Load 30
        $quiet = $others.Count -eq 0 -and $load -le $MaxLoad
        if ($quiet -or (Get-Date) -ge $deadline) { break }
        "WAIT  machine busy (load {0:N0}%, {1} other harness/app process(es)); waiting for it to be quiet" -f $load, $others.Count
        Start-Sleep -Seconds 15
    }
    if (-not $quiet) {
        $noisy = $true
        ''
        '!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!'
        "!!  NOISY: the machine is busy (load {0:N0}% over 30 s, limit {1}%; {2} other harness/app process(es))." -f $load, $MaxLoad, $others.Count
        '!!  Timing gates measured now say little either way: run -Timing again when it is quiet.'
        '!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!'
        ''
    }
    else { "TIMING  machine quiet (load {0:N0}% over 30 s); the timing tier runs alone" -f $load }
    $started = $wall.Elapsed.TotalSeconds
    $p = Start-Harness @('--timing', '--results', (Join-Path $Out 'timing.json'), (Join-Path $Out 'shots-timing')) 'timing'
    $p.WaitForExit()
    Read-Run 'timing' $timingGroups 'timing' $p ($wall.Elapsed.TotalSeconds - $started)
}

# ── Summary ────────────────────────────────────────────────────────────────
$fails = @($final | Where-Object status -eq 'fail')
$deferred = @($final | Where-Object status -eq 'deferred')
''
foreach ($f in $fails) { "FAIL  [$($f.group)$(if ($f.tier -eq 'timing') { ', timing tier' })] $($f.label)" }
foreach ($p in $problems) { "FAIL  [suite] $p" }
"SUMMARY  {0} checks: {1} passed, {2} failed{3}; {4:N0} s wall; setup per process {5:N1}-{6:N1} s{7}" -f $final.Count,
    @($final | Where-Object status -eq 'pass').Count, $fails.Count,
    $(if ($deferred.Count) { ", $($deferred.Count) timing gates printed, not gated" } else { '' }), $wall.Elapsed.TotalSeconds,
    ($setupSeconds | Measure-Object -Minimum).Minimum, ($setupSeconds | Measure-Object -Maximum).Maximum,
    $(if ($noisy) { '; timing tier NOISY' } else { '' })

# ── Results and baselines: one per commit and tier, under %TEMP%\apex-suite\baselines ──
$baselines = Join-Path $suiteTemp 'baselines'
$resultFile = Join-Path $Out 'result.json'
$head = (& git -C $root rev-parse HEAD 2>$null)
$dirty = [bool](& git -C $root status --porcelain --untracked-files=no 2>$null)
[pscustomobject]@{
    commit = $head; dirty = $dirty; tier = $tier; noisy = $noisy; when = (Get-Date).ToString('s')
    checks = @($final | ForEach-Object { [pscustomobject]@{ group = $_.group; label = $_.label; status = $_.status } })
} | ConvertTo-Json -Depth 5 | Set-Content $resultFile -Encoding utf8
"RESULT  $resultFile"
if ($SaveBaseline) {
    New-Item -ItemType Directory -Force $baselines | Out-Null
    $name = "$head$(if ($dirty) { '-dirty' })-$tier.json"
    Copy-Item $resultFile (Join-Path $baselines $name) -Force
    "BASELINE  saved $(Join-Path $baselines $name)"
}
if ($Compare) {
    $basePath = if (Test-Path $Compare) { $Compare } else {
        $sha = (& git -C $root rev-parse $Compare 2>$null)
        Get-ChildItem $baselines -Filter "$sha*-$tier.json" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
    }
    if (-not $basePath) { "COMPARE  no $tier baseline for '$Compare' (save one with -SaveBaseline on that commit)" }
    else {
        # As multisets of (group, normalized name): a check that runs twice must be there twice.
        $base = Get-Content $basePath -Raw | ConvertFrom-Json
        $was = @{}; $now = @{}
        foreach ($c in $base.checks) { $k = $c.group + '|' + (Normalize $c.label); if (-not $was[$k]) { $was[$k] = [Collections.Generic.List[object]]::new() }; $was[$k].Add($c) }
        foreach ($c in $final) { $k = $c.group + '|' + (Normalize $c.label); if (-not $now[$k]) { $now[$k] = [Collections.Generic.List[object]]::new() }; $now[$k].Add($c) }
        $n = @{ same = 0; regressed = 0; fixed = 0; still = 0; new = 0; gone = 0 }
        "COMPARE  against $basePath ($(@($base.checks).Count) checks there, $($final.Count) here)"
        foreach ($k in (@($was.Keys) + @($now.Keys) | Sort-Object -Unique)) {
            $a = @($was[$k] | Where-Object { $_ }); $b = @($now[$k] | Where-Object { $_ })
            for ($i = 0; $i -lt [Math]::Max($a.Count, $b.Count); $i++) {
                if ($i -ge $a.Count) { $n.new++; "  NEW       [$($b[$i].group)] $($b[$i].status.ToUpper()) $($b[$i].label)"; continue }
                if ($i -ge $b.Count) { $n.gone++; "  GONE      [$($a[$i].group)] $($a[$i].label)"; continue }
                switch ("$($a[$i].status)>$($b[$i].status)") {
                    'pass>fail' { $n.regressed++; "  REGRESSED [$($b[$i].group)] $($b[$i].label)" }
                    'fail>pass' { $n.fixed++; "  FIXED     [$($b[$i].group)] $($b[$i].label)" }
                    'fail>fail' { $n.still++; "  STILL     [$($b[$i].group)] $($b[$i].label)" }
                    default { $n.same++ }
                }
            }
        }
        "COMPARE  {0} same, {1} regressed, {2} fixed, {3} still failing, {4} new, {5} gone" -f $n.same, $n.regressed, $n.fixed, $n.still, $n.new, $n.gone
    }
}

if ($fails.Count -eq 0 -and $problems.Count -eq 0) { 'ALL CHECKS PASSED'; exit 0 }
"$($fails.Count + $problems.Count) CHECK(S) FAILED"
exit 1
