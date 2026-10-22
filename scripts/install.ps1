# Publishes WinExplorer into %LOCALAPPDATA%\Programs\WinExplorer and registers the
# per-user integrations. No administrator rights are required.
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\WinExplorer'),
    [switch]$StartWithWindows = $true,
    [switch]$ContextMenu = $true,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\WinExplorer\WinExplorer.csproj'

if (-not $NoBuild) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The .NET SDK was not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0'
    }

    Write-Host "Publishing to $Destination" -ForegroundColor Cyan
    dotnet publish $project -c Release -r win-x64 --self-contained false -o $Destination
}

$exe = Join-Path $Destination 'WinExplorer.exe'
if (-not (Test-Path $exe)) {
    throw "WinExplorer.exe was not found in $Destination"
}

if ($ContextMenu) {
    Write-Host 'Registering the folder context menu' -ForegroundColor Cyan
    & $exe --install-context-menu | Out-Null
}

if ($StartWithWindows) {
    Write-Host 'Registering the startup entry' -ForegroundColor Cyan
    & $exe --install-startup | Out-Null
}

Write-Host 'Starting WinExplorer' -ForegroundColor Cyan
Start-Process -FilePath $exe

Write-Host "Installed to $Destination" -ForegroundColor Green
Write-Host 'This is the developer helper: it does not create the Start Menu shortcuts or the'
Write-Host 'entry in Settings > Apps. For a normal installation use Install.cmd instead.'
Write-Host "Uninstall with: `"$Destination\Uninstall.cmd`" (or Settings > Apps > WinExplorer)."
