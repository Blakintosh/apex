# Launch-and-check for a built Apex.exe on a machine with the mod tools installed. Read-only: settings (a copy of
# yours), the session journal and logs go to a temp folder, and nothing is saved. Checks through UI Automation that the
# window opens, the install is found and loaded, an asset opens and the xanim preview appears; times each step.
# Usage: .\smoke.ps1 [-Exe artifacts\apex-0.2.1-win-x64\bin\Apex.exe] [-Asset vm_alien_blaster_fire] [-Shot out.png]
param(
    [string]$Exe,
    [string]$Asset = 'vm_alien_blaster_fire',
    [string]$Shot,
    [int]$TimeoutSec = 120
)
$ErrorActionPreference = 'Stop'
if (-not $Exe) {
    # This version's release, as release.ps1 leaves it unpacked.
    $version = ([xml](Get-Content (Join-Path $PSScriptRoot 'Apex.Editor\Apex.Editor.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $Exe = Join-Path $PSScriptRoot "artifacts\apex-$version-win-x64\bin\Apex.exe"
}
if (-not (Test-Path $Exe)) { throw "No Apex.exe at $Exe. Run release.ps1 first." }
$Exe = (Resolve-Path $Exe).Path
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

$temp = Join-Path ([IO.Path]::GetTempPath()) ("apex-smoke-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$psi = [Diagnostics.ProcessStartInfo]::new($Exe)
$psi.UseShellExecute = $false
# A copy of your settings, so an install found only through Locate… is found here too, and the real file is never written.
$settings = Join-Path $temp 'settings'
New-Item -ItemType Directory -Force $settings | Out-Null
$ui = Join-Path $env:APPDATA 'Apex\ui.json'
if (Test-Path $ui) { Copy-Item $ui $settings }
$psi.Environment['APEX_SETTINGS_DIR'] = $settings
$psi.Environment['APEX_SESSION_DIR'] = Join-Path $temp 'session'
$psi.Environment['APEX_LOG_DIR'] = Join-Path $temp 'logs'
$psi.Environment['APEX_STARTUP_OPEN'] = $Asset

$failed = 0
function Step([string]$label, [bool]$ok, [double]$ms) {
    '{0}  {1}{2}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $label, $(if ($ms -ge 0) { " ({0:N0} ms)" -f $ms } else { '' })
    if (-not $ok) { $script:failed++ }
}
function Find-Named($root, [scriptblock]$match) {
    # UI Automation fails now and then while the tree is changing under it (E_FAIL): that's a miss, and the caller retries.
    try { $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) }
    catch { return $null }
    foreach ($e in $all) { try { if (& $match $e.Current.Name) { return $e } } catch { } }
    $null
}
function Wait-For([scriptblock]$probe) {
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        $r = & $probe
        if ($r) { return $r }
        if ($proc.HasExited) { return $null }
        Start-Sleep -Milliseconds 50
    }
    $null
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = [Diagnostics.Process]::Start($psi)
try {
    $window = Wait-For {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0) { [Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle) }
    }
    Step 'window shown' ($null -ne $window) $(if ($window) { $sw.Elapsed.TotalMilliseconds } else { -1 })
    if (-not $window) { throw 'no window' }

    $loaded = Wait-For { Find-Named $window { param($n) $n -like 'Loaded * GDTs*' } }
    Step "install found and loaded ('$($loaded.Current.Name)')" ($null -ne $loaded) $(if ($loaded) { $sw.Elapsed.TotalMilliseconds } else { -1 })
    $missing = Find-Named $window { param($n) $n -eq 'Locate…' }
    Step 'no not-found state' ($null -eq $missing) -1

    $tab = Wait-For { Find-Named $window { param($n) $n -eq $Asset } }
    Step "asset opened ($Asset)" ($null -ne $tab) $(if ($tab) { $sw.Elapsed.TotalMilliseconds } else { -1 })

    # The anim preview's transport is up once the xanim has loaded into the preview.
    # (Its frame slider is hidden under the notetrack timeline, so look for Play.)
    $anim = Wait-For { Find-Named $window { param($n) $n -eq 'Play or pause animation' } }
    Step 'xanim preview shown' ($null -ne $anim) $(if ($anim) { $sw.Elapsed.TotalMilliseconds } else { -1 })

    if ($Shot) {
        Start-Sleep -Milliseconds 1500
        $r = $window.Current.BoundingRectangle
        $bmp = [Drawing.Bitmap]::new([int]$r.Width, [int]$r.Height)
        $g = [Drawing.Graphics]::FromImage($bmp)
        # PrintWindow with PW_RENDERFULLCONTENT: the window's own pixels (D3D included), even when it isn't in front.
        Add-Type -Namespace Smoke -Name User32 -MemberDefinition '[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);'
        $hdc = $g.GetHdc()
        [Smoke.User32]::PrintWindow($proc.MainWindowHandle, $hdc, 2) | Out-Null
        $g.ReleaseHdc($hdc)
        $bmp.Save($Shot)
        "wrote $Shot"
    }
    $logs = Get-ChildItem (Join-Path $temp 'logs') -ErrorAction SilentlyContinue
    Step 'no crash logs' (-not $logs) -1
}
finally {
    if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; if (-not $proc.WaitForExit(5000)) { $proc.Kill() } }
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failed -eq 0) { 'SMOKE PASSED' } else { "$failed SMOKE CHECK(S) FAILED"; exit 1 }
