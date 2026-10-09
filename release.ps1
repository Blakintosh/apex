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
# lines). Only Apex.exe ships; single-file leaves native symbols and XML docs beside it that nothing reads.
dotnet publish $project -r win-x64 -p:PublishProfile=FolderProfile -p:DebugType=embedded -o $build --nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
New-Item -ItemType Directory "$stage\bin" -Force | Out-Null
Copy-Item "$build\Apex.exe" "$stage\bin\Apex.exe"
Remove-Item $build -Recurse -Force
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

$setup = & "$Installer\scripts\build-setup.ps1" -Bundle $zip
foreach ($f in $zip, $setup) { '{0}  {1:N1} MB' -f $f, ((Get-Item $f).Length / 1MB) }

if ($Publish) {
    gh release create $tag $zip $setup --target (git -C $root rev-parse HEAD) --title "Apex $version" --notes-file $NotesFile
    if ($LASTEXITCODE) { throw "gh release create failed ($LASTEXITCODE)" }
}
