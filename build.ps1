$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'Jellyfin.Plugin.AnimeSeasonCollections.csproj'
$dist = Join-Path $PSScriptRoot 'dist'
$publish = Join-Path $dist 'publish'
$plugin = Join-Path $dist 'plugin'
$zip = Join-Path $dist 'AnimeSeasonCollections_12.0.0.9.zip'
$assembly = 'Jellyfin.Plugin.AnimeSeasonCollections'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK was not found in PATH.'
}

Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publish -Force | Out-Null
New-Item -ItemType Directory -Path $plugin -Force | Out-Null

dotnet publish $project -c Release -o $publish
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE. No plugin ZIP was created."
}

# IMPORTANT: Jellyfin scans DLLs in a plugin directory. Native DLLs from
# NuGet runtime folders (for example runtimes/win-arm64/native/libSkiaSharp.dll)
# are not managed .NET assemblies and make Jellyfin disable the whole plugin.
# This plugin has no private runtime dependency: Jellyfin 12 already provides
# Jellyfin.Controller, Jellyfin.Model, Microsoft.Extensions.Logging and
# SkiaSharp. Therefore package ONLY our own managed assembly.
$mainDll = Join-Path $publish "$assembly.dll"
if (-not (Test-Path $mainDll)) {
    throw "Expected plugin DLL was not produced: $mainDll"
}
Copy-Item $mainDll $plugin

# Optional diagnostics/docs. These are harmless and useful when debugging.
foreach ($ext in @('pdb', 'xml')) {
    $candidate = Join-Path $publish "$assembly.$ext"
    if (Test-Path $candidate) {
        Copy-Item $candidate $plugin
    }
}

# Safety check: the staging directory must contain no runtime tree and no
# foreign DLLs. Fail the build instead of creating a broken ZIP.
if (Test-Path (Join-Path $plugin 'runtimes')) {
    throw 'Packaging error: runtimes directory unexpectedly exists in plugin staging.'
}
$foreignDlls = Get-ChildItem $plugin -Recurse -File -Filter '*.dll' | Where-Object {
    $_.Name -ne "$assembly.dll"
}
if ($foreignDlls) {
    $names = ($foreignDlls | ForEach-Object FullName) -join [Environment]::NewLine
    throw "Packaging error: foreign DLL(s) found:`n$names"
}

Compress-Archive -Path (Join-Path $plugin '*') -DestinationPath $zip -Force
Write-Host "Built: $zip"
Write-Host 'Packaged DLLs:'
Get-ChildItem $plugin -File -Filter '*.dll' | ForEach-Object { Write-Host "  $($_.Name)" }
