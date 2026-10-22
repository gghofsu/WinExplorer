# Publishes WinExplorer and packs a zip for release.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SelfContained,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\WinExplorer\WinExplorer.csproj'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'artifacts'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0'
}

$version = (Get-Content $project | Select-String -Pattern '<Version>(.*)</Version>').Matches[0].Groups[1].Value
$publishDir = Join-Path $OutputDirectory "publish-$Runtime"
$zipPath = Join-Path $OutputDirectory "WinExplorer-$version-$Runtime.zip"

Write-Host "Publishing $version for $Runtime (self-contained: $($SelfContained.IsPresent))" -ForegroundColor Cyan

$publishArgs = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '-o', $publishDir,
    "/p:SelfContained=$($SelfContained.IsPresent.ToString().ToLower())",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true'
)

dotnet @publishArgs

# Ship the double-click installer and uninstaller with the app, so an unpacked release can
# be installed (and later removed) without the repository or the setup executable.
$extras = @(
    @{ From = 'Install.cmd'; To = 'Install.cmd' },
    @{ From = 'Uninstall.cmd'; To = 'Uninstall.cmd' },
    @{ From = 'scripts\install-app.ps1'; To = 'scripts\install-app.ps1' },
    @{ From = 'scripts\uninstall-app.ps1'; To = 'scripts\uninstall-app.ps1' }
)

foreach ($extra in $extras) {
    $from = Join-Path $root $extra.From
    $to = Join-Path $publishDir $extra.To
    if (Test-Path $from) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
        Copy-Item -Path $from -Destination $to -Force
    }
}

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Created $zipPath" -ForegroundColor Green
