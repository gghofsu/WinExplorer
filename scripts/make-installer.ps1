# Builds the self-contained payload and compiles installer\WinExplorer.iss with Inno Setup.
#
#   .\scripts\make-installer.ps1
#   .\scripts\make-installer.ps1 -Configuration Release -FrameworkDependent
#
# Requires Inno Setup 6 (https://jrsoftware.org/isdl.php). The script looks for ISCC.exe
# in the usual locations and on PATH, so an unattended "choco install innosetup" works too.
[CmdletBinding()]
param(
    [switch]$SelfContained = $true,
    [string]$OutputDirectory,
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\WinExplorer\WinExplorer.csproj'
$iss = Join-Path $root 'installer\WinExplorer.iss'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0'
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'publish'
}

$version = (Get-Content $project | Select-String -Pattern '<Version>(.*)</Version>').Matches[0].Groups[1].Value
$target = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
$publishDir = Join-Path $OutputDirectory $target

Write-Host "Publishing $version ($target)" -ForegroundColor Cyan
$selfContainedArg = $SelfContained.IsPresent.ToString().ToLowerInvariant()

# Start from an empty folder: a previous publish (or a release zip built by publish.ps1)
# may have left Install.cmd/scripts behind, and the setup is the installer in this case.
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish $project -c Release -r win-x64 `
    --self-contained $selfContainedArg `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir

if (-not $Iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    if ($candidates) {
        $Iscc = $candidates[0]
    }
    else {
        $command = Get-Command iscc -ErrorAction SilentlyContinue
        if ($command) { $Iscc = $command.Source }
    }
}

if (-not $Iscc) {
    throw 'ISCC.exe (Inno Setup 6) was not found. Install it from https://jrsoftware.org/isdl.php or pass -Iscc <path>.'
}

# The .iss script reads the version from the environment so it matches the assembly.
$env:WINEXPLORER_VERSION = $version

Write-Host "Compiling $iss" -ForegroundColor Cyan
$output = & $Iscc "/DAppVersion=$version" $iss 2>&1
$output | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE`n$($output -join [Environment]::NewLine)"
}

$setup = Join-Path $root "installer\Output\WinExplorer-$version-setup.exe"
Write-Host "Created $setup" -ForegroundColor Green
