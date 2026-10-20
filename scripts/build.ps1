# Builds WinExplorer and runs the unit tests.
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoTest
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'WinExplorer.sln'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0'
}

Write-Host "Building $solution ($Configuration)" -ForegroundColor Cyan
dotnet build $solution -c $Configuration

if (-not $NoTest) {
    Write-Host 'Running tests' -ForegroundColor Cyan
    dotnet test $solution -c $Configuration --no-build
}

Write-Host 'Done.' -ForegroundColor Green
