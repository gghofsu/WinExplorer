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

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Created $zipPath" -ForegroundColor Green
