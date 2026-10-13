<#
  Builds an Apex release, as gscode-installer's docs/release-contract.md describes:

    artifacts\apex-X.Y.Z-win-x64.zip   the bundle: bin\Apex.exe
    artifacts\apex-setup.exe           the installer, with that bundle inside

  .\release.ps1                      build both (refuses uncommitted changes; -AllowDirty for a test build)
  .\release.ps1 -Publish -NotesFile notes.md
                                     also create the GitHub release vX.Y.Z from them; fails if the tag exists

  Apex.exe is one self-contained file (Apex.Editor\Properties\PublishProfiles\FolderProfile.pubxml): nothing to
  install first. The unpacked bundle stays in artifacts\apex-X.Y.Z-win-x64\ for smoke.ps1.
  Needs the .NET 10 SDK and, to publish, the GitHub CLI.
#>
param(
    [string] $Installer = 'J:\Github\gscode-installer',
    [switch] $AllowDirty,
    [switch] $Publish,
    [string] $NotesFile
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = $PSScriptRoot
$id = 'apex'
$project = "$root\Apex.Editor\Apex.Editor.csproj"

if (-not $AllowDirty -and (git -C $root status --porcelain --untracked-files=no)) {
    throw 'Uncommitted changes. Commit them, or pass -AllowDirty for a test build.'
}
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"
if ($Publish) {
    if (-not $NotesFile -or -not (Test-Path $NotesFile)) { throw '-Publish needs -NotesFile <release notes .md>' }
    if (git -C $root ls-remote --tags origin "refs/tags/$tag") { throw "$tag already exists on GitHub" }
}

$name = "$id-$version-win-x64"
$artifacts = Join-Path $root 'artifacts'
$stage = Join-Path $artifacts $name
$zip = "$stage.zip"
$build = Join-Path $artifacts 'build'
Remove-Item $stage, $zip, $build -Recurse -Force -ErrorAction SilentlyContinue

# DebugType on the command line reaches every project, so their symbols go inside the exe too (crash logs keep
# lines). ContinuousIntegrationBuild maps the repo folder to /_/ in those symbols and in SourceLink's map, so no
# local path ships. Only Apex.exe ships; single-file leaves native symbols and XML docs beside it that nothing reads.
dotnet publish $project -r win-x64 -p:PublishProfile=FolderProfile -p:DebugType=embedded -p:ContinuousIntegrationBuild=true -o $build --nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
New-Item -ItemType Directory "$stage\bin" -Force | Out-Null
Copy-Item "$build\Apex.exe" "$stage\bin\Apex.exe"
Remove-Item $build -Recurse -Force

# Fail when a local path or personal folder is still in the exe: raw (ASCII and UTF-16) and inside its embedded
# portable PDBs (an "MPDB" marker, the inflated size, then deflate data). ContinuousIntegrationBuild maps the repo
# folder to /_/ in both, so the repo folder and the user profile must be gone.
function Test-LeakedPaths([string] $Exe, [string[]] $Forbidden) {
    $bytes = [IO.File]::ReadAllBytes($Exe)
    $blobs = [Collections.Generic.List[byte[]]]::new(); $blobs.Add($bytes)
    for ($i = 0; ($i = [Array]::IndexOf($bytes, [byte]0x4D, $i)) -ge 0; $i++) {
        if ($i + 8 -gt $bytes.Length -or $bytes[$i+1] -ne 0x50 -or $bytes[$i+2] -ne 0x44 -or $bytes[$i+3] -ne 0x42) { continue }
        $size = [BitConverter]::ToUInt32($bytes, $i + 4)
        if ($size -lt 64 -or $size -gt 512MB) { continue }
        try {
            $in = [IO.MemoryStream]::new($bytes, $i + 8, $bytes.Length - $i - 8)
            $ds = [IO.Compression.DeflateStream]::new($in, [IO.Compression.CompressionMode]::Decompress)
            $buf = [byte[]]::new($size); $n = 0
            while ($n -lt $size) { $r = $ds.Read($buf, $n, $size - $n); if ($r -le 0) { break }; $n += $r }
            if ($n -eq $size -and $buf[0] -eq 0x42 -and $buf[1] -eq 0x53) { $blobs.Add($buf) }
        } catch { }
    }
    $hits = foreach ($b in $blobs) {
        # one string per encoding: joined, a 180 MB exe is too big for one string
        foreach ($enc in [Text.Encoding]::ASCII, [Text.Encoding]::Unicode) {
            $text = $enc.GetString($b)
            $Forbidden | Where-Object { $text.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 }
        }
    }
    $hits | Select-Object -Unique
}
$leaked = Test-LeakedPaths "$stage\bin\Apex.exe" @($root, $root.Replace('\', '\\'), $env:USERPROFILE, $env:USERPROFILE.Replace('\', '\\'), 'C:\Users\', 'I:\SteamLibrary')
if ($leaked) { throw "Apex.exe still contains: $($leaked -join ', ')" }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

$setup = & "$Installer\scripts\build-setup.ps1" -Bundle $zip
foreach ($f in $zip, $setup) { '{0}  {1:N1} MB' -f $f, ((Get-Item $f).Length / 1MB) }

if ($Publish) {
    gh release create $tag $zip $setup --target (git -C $root rev-parse HEAD) --title "Apex $version" --notes-file $NotesFile
    if ($LASTEXITCODE) { throw "gh release create failed ($LASTEXITCODE)" }
}
