<#
Builds the preview-simulator test modules from sim_fixture.c with MSVC, into built\, and records their hashes in
built\hashes.txt. The DLLs are committed (a few KB each, no C runtime) so the checks run on a machine without Visual
Studio; the checks fail, never skip, when the source or header no longer matches what built them. Run this after
changing sim_fixture.c or docs\plugin-abi\apex_sim.h.

  sim-fixture.dll          the module the checks drive
  sim-fixture-abi2.dll     reports ABI version 2
  sim-fixture-infoabi.dll  apex_sim_abi_version says 1, apex_sim_info says 2
  sim-fixture-wrongid.dll  says it belongs to another extension
  sim-fixture-idspace.dll  says "sim-fixture " (a trailing space): not the id
  sim-fixture-nostep.dll   doesn't export apex_sim_step
  sim-fixture-x86.dll      a 32-bit build (Apex is 64-bit)
  sim-helper.dll           a DLL the next two import from their own folder
  sim-fixture-sibling.dll  imports sim-helper.dll
  sim-fixture-delay.dll    delay-loads sim-helper.dll

Finds Visual Studio through vswhere, as weapon-tech's build.ps1 does. Set VCVARS to a vcvars64.bat to skip the search.
/Brepro makes the output depend on the input only, so a rebuild of unchanged source gives the same bytes.
#>
$ErrorActionPreference = 'Stop'

function Find-VcVars {
    if ($env:VCVARS -and (Test-Path $env:VCVARS)) { return $env:VCVARS }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $root = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($root) {
            $p = Join-Path $root 'VC\Auxiliary\Build\vcvars64.bat'
            if (Test-Path $p) { return $p }
        }
    }
    throw 'vcvars64.bat not found: install Visual Studio with the "Desktop development with C++" workload, or set VCVARS.'
}

$vcvars64 = Find-VcVars
$vcvars32 = Join-Path (Split-Path $vcvars64) 'vcvars32.bat'
$env:PATH += ';' + (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer')
$here = $PSScriptRoot
$out = Join-Path $here 'built'
$obj = Join-Path ([IO.Path]::GetTempPath()) ('apex-sim-fixture-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $out, $obj | Out-Null

$cl = 'cl /nologo /O1 /GS- /W4 /WX /Brepro /LD'
$link = '/link /NODEFAULTLIB /NOENTRY /Brepro kernel32.lib'
$helperLib = "`"$obj\sim-helper.lib`""
$variants = @(
    @{ Name = 'sim-helper';          Vars = $vcvars64; Source = 'sim_helper.c'; Defines = ''; Libs = "/IMPLIB:$helperLib" },
    @{ Name = 'sim-fixture';         Vars = $vcvars64; Defines = '' },
    @{ Name = 'sim-fixture-abi2';    Vars = $vcvars64; Defines = '/DFIX_ABI=2' },
    @{ Name = 'sim-fixture-infoabi'; Vars = $vcvars64; Defines = '/DFIX_INFO_ABI=2' },
    @{ Name = 'sim-fixture-wrongid'; Vars = $vcvars64; Defines = '/DFIX_ID=\"someone-else\"' },
    @{ Name = 'sim-fixture-idspace'; Vars = $vcvars64; Defines = '"/DFIX_ID=\"sim-fixture \""' },
    @{ Name = 'sim-fixture-nostep';  Vars = $vcvars64; Defines = '/DFIX_NO_STEP' },
    @{ Name = 'sim-fixture-x86';     Vars = $vcvars32; Defines = '' },
    @{ Name = 'sim-fixture-sibling'; Vars = $vcvars64; Defines = '/DFIX_HELPER'; Libs = $helperLib },
    @{ Name = 'sim-fixture-delay';   Vars = $vcvars64; Defines = '/DFIX_HELPER /DFIX_DELAY'; Libs = "$helperLib /DELAYLOAD:sim-helper.dll" }
)
foreach ($v in $variants) {
    $dll = Join-Path $out ($v.Name + '.dll')
    $source = if ($v.Source) { $v.Source } else { 'sim_fixture.c' }
    $cmd = "`"$($v.Vars)`" >nul && cd /d `"$here`" && $cl $source $($v.Defines) /Fo`"$obj\\`" /Fe:`"$dll`" $link $($v.Libs) /PDBALTPATH:none"
    cmd.exe /c $cmd
    if ($LASTEXITCODE -ne 0) { throw "build of $($v.Name) failed ($LASTEXITCODE)" }
    Remove-Item (Join-Path $out ($v.Name + '.lib')), (Join-Path $out ($v.Name + '.exp')) -ErrorAction SilentlyContinue
}
Remove-Item -Recurse -Force $obj

# Sources hashed with LF line ends, so a checkout's line-end setting doesn't matter.
function Text-Hash($path) {
    $text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    return ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))).ToLowerInvariant()
}
$lines = @(
    "$(Text-Hash (Join-Path $here 'sim_fixture.c'))  sim_fixture.c",
    "$(Text-Hash (Join-Path $here 'sim_helper.c'))  sim_helper.c",
    "$(Text-Hash (Join-Path $here '..\..\..\docs\plugin-abi\apex_sim.h'))  apex_sim.h"
)
foreach ($v in $variants) {
    $dll = Join-Path $out ($v.Name + '.dll')
    $lines += "$((Get-FileHash $dll -Algorithm SHA256).Hash.ToLowerInvariant())  $($v.Name).dll"
}
[IO.File]::WriteAllLines((Join-Path $out 'hashes.txt'), $lines)
Write-Host "built $($variants.Count) modules in $out"
