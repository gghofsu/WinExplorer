#Requires -Version 5.1
<#
    WinExplorer - per-user installer.

    Double-click Install.cmd (or run this script). Everything is installed into
    %LOCALAPPDATA%\Programs\WinExplorer and HKCU only, so Windows never asks for
    administrator rights.

    The installer
      * finds the WinExplorer payload: next to Install.cmd, in a publish/artifacts folder,
        from a fresh dotnet build, or by downloading the latest release from GitHub,
      * copies the app into the install folder, replacing an older version,
      * adds or removes the "start when I sign in" entry and the folder right-click menu,
      * creates Start Menu shortcuts and the uninstall entry that Windows' Settings app
        ("Apps & features" / "Installed apps") shows,
      * starts WinExplorer.

    Options (all optional):
      -Startup | -NoStartup          add/remove "start when I sign in" without asking
      -ContextMenu | -NoContextMenu  add/remove the folder right-click menu without asking
      -NoLaunch                      do not start WinExplorer after installing
      -Quiet                         no questions; use the defaults (startup + menu on)
      -NoDownload                    never download a release from GitHub; build instead
      -InstallSdk                    allow installing the .NET 8 SDK with winget, unattended
      -Destination <folder>          install somewhere other than %LOCALAPPDATA%\Programs\WinExplorer
      -Source <folder>               install from this folder instead of the payload found
                                     next to Install.cmd
      -Pause                         wait for Enter before the window closes
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\WinExplorer'),
    [string]$Source,
    [switch]$Startup,
    [switch]$NoStartup,
    [switch]$ContextMenu,
    [switch]$NoContextMenu,
    [switch]$NoLaunch,
    [switch]$Quiet,
    [switch]$NoDownload,
    [switch]$InstallSdk,
    [switch]$Pause,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Extra = @()
)

$ErrorActionPreference = 'Stop'
$AppName = 'WinExplorer'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinExplorer'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$ApprovedRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$script:TempPayloads = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------------------
# Small console helpers
# ---------------------------------------------------------------------------

$script:Interactive = $true
try {
    if (-not [Environment]::UserInteractive -or [Console]::IsInputRedirected) {
        $script:Interactive = $false
    }
}
catch {
    $script:Interactive = $false
}

function Write-Head([string]$Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
}

function Write-Info([string]$Text) {
    Write-Host "  $Text"
}

function Write-Ok([string]$Text) {
    Write-Host "  [ok] $Text" -ForegroundColor Green
}

function Write-Note([string]$Text) {
    Write-Host "  $Text" -ForegroundColor Yellow
}

function Ask-YesNo([string]$Question, [bool]$Default = $true) {
    # Silence, a piped call or a non-interactive host all mean "take the default".
    if ($Quiet -or -not $script:Interactive) {
        return $Default
    }

    $suffix = if ($Default) { '[Y/n]' } else { '[y/N]' }
    while ($true) {
        $answer = Read-Host "  $Question $suffix"
        if ($null -eq $answer -or $answer.Trim().Length -eq 0) {
            return $Default
        }

        switch ($answer.Trim().ToLowerInvariant()) {
            'y' { return $true }
            'yes' { return $true }
            'n' { return $false }
            'no' { return $false }
            default { Write-Note 'Please answer y or n.' }
        }
    }
}

function Stop-BeforeClose {
    if ($Pause -and $script:Interactive -and -not $Quiet) {
        Write-Host ''
        Read-Host '  Press Enter to close' | Out-Null
    }
}

# ---------------------------------------------------------------------------
# Location helpers
# ---------------------------------------------------------------------------

function Expand-Path([string]$Path) {
    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    return [IO.Path]::GetFullPath($expanded)
}

function Test-SamePath([string]$A, [string]$B) {
    return [string]::Equals($A.TrimEnd('\'), $B.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}

function Test-DesktopRuntime {
    try {
        $runtimes = & dotnet --list-runtimes 2>$null
        return [bool]($runtimes -match 'Microsoft\.WindowsDesktop\.App 8\.')
    }
    catch {
        return $false
    }
}

function Find-Payload([string]$Root, [string]$Explicit) {
    if ($Explicit) {
        $candidate = Expand-Path $Explicit
        if (-not (Test-Path (Join-Path $candidate 'WinExplorer.exe'))) {
            throw "WinExplorer.exe was not found in '$candidate'. Point -Source at the folder that contains it."
        }
        return $candidate
    }

    # An unpacked release folder has WinExplorer.exe right next to Install.cmd; a source
    # checkout has one of the publish folders, and a build output also works as-is.
    $candidates = @(
        $Root,
        (Join-Path $Root 'publish\self-contained'),
        (Join-Path $Root 'publish\framework-dependent'),
        (Join-Path $Root 'publish'),
        (Join-Path $Root 'artifacts\publish-win-x64'),
        (Join-Path $Root 'src\WinExplorer\bin\Release\net8.0-windows10.0.17763.0\win-x64\publish'),
        (Join-Path $Root 'src\WinExplorer\bin\Release\net8.0-windows10.0.17763.0\publish'),
        (Join-Path $Root 'src\WinExplorer\bin\Release\net8.0-windows10.0.17763.0\win-x64'),
        (Join-Path $Root 'src\WinExplorer\bin\Release\net8.0-windows10.0.17763.0'),
        (Join-Path $Root 'src\WinExplorer\bin\Release\net8.0-windows\win-x64\publish'),
        (Join-Path $Root 'src\WinExplorer\bin\Release\net8.0-windows\publish'),
        (Join-Path $Root 'src\WinExplorer\bin\Release\net8.0-windows\win-x64'),
        (Join-Path $Root 'src\WinExplorer\bin\Release\net8.0-windows')
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'WinExplorer.exe'))) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    # Source tree, but nothing has been published yet.
    return $null
}

function Resolve-DotNet {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    foreach ($candidate in @(
            (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe'),
            (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'))) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    return $null
}

function Test-DotNetSdk([string]$DotNet) {
    if (-not $DotNet) {
        return $false
    }

    try {
        # The runtime ships dotnet.exe too, so ask for an 8.x SDK explicitly.
        $sdks = & $DotNet --list-sdks 2>$null
        return [bool]($sdks -match '^8\.')
    }
    catch {
        return $false
    }
}

function Update-SessionPath {
    # A freshly installed SDK is not in the PATH of this process yet; re-read it from the
    # registry so the build below can find dotnet.exe.
    try {
        $machine = (Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' -Name 'Path' -ErrorAction SilentlyContinue).Path
        $user = (Get-ItemProperty -Path 'HKCU:\Environment' -Name 'Path' -ErrorAction SilentlyContinue).Path

        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($entry in @($env:PATH -split ';') + @($machine -split ';') + @($user -split ';')) {
            $trimmed = if ($entry) { $entry.Trim() } else { '' }
            if ($trimmed.Length -gt 0 -and -not $parts.Contains($trimmed)) {
                $parts.Add($trimmed)
            }
        }

        $env:PATH = ($parts -join ';')
    }
    catch {
        # best effort; Resolve-DotNet also looks in the default install folders
    }
}

function Build-Payload([string]$DotNet, [string]$Root) {
    $project = Join-Path $Root 'src\WinExplorer\WinExplorer.csproj'
    if (-not (Test-Path $project)) {
        return $null
    }

    # Build a self-contained app when the .NET 8 Desktop runtime is missing, so the result
    # runs without installing anything else.
    $selfContained = -not (Test-DesktopRuntime)
    $what = if ($selfContained) { 'self-contained' } else { 'framework-dependent' }

    $temp = Join-Path ([IO.Path]::GetTempPath()) ('WinExplorer-build-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $script:TempPayloads.Add($temp) | Out-Null

    Write-Info "Building WinExplorer from source ($what, this takes a minute)..."
    $selfContainedArg = if ($selfContained) { 'true' } else { 'false' }

    try {
        & $DotNet publish $project -c Release -r win-x64 --self-contained $selfContainedArg `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $temp | Out-Host
    }
    catch {
        Write-Note "The build failed: $($_.Exception.Message)"
        return $null
    }

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $temp 'WinExplorer.exe'))) {
        Write-Note "dotnet publish exited with code $LASTEXITCODE and produced no WinExplorer.exe."
        return $null
    }

    return $temp
}

function Get-PrebuiltPayload {
    # Last resort for people who downloaded the source code from GitHub instead of a release:
    # fetch the newest published build straight from the releases page.
    if ($NoDownload) {
        return $null
    }

    if ($script:Interactive -and -not $Quiet) {
        $question = 'Download the latest pre-built WinExplorer release from GitHub? (about 100 MB)'
        if (-not (Ask-YesNo $question $true)) {
            return $null
        }
    }

    Write-Info 'Looking up the latest WinExplorer release on GitHub...'

    try {
        [Net.ServicePointManager]::SecurityProtocol =
            [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

        $headers = @{ 'User-Agent' = 'WinExplorer-installer'; 'Accept' = 'application/vnd.github+json' }
        $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/gghofsu/WinExplorer/releases/latest' `
            -Headers $headers -TimeoutSec 30

        $assets = @($release.assets | Where-Object { $_.name -like '*.zip' })
        $asset = $assets | Where-Object { $_.name -like '*selfcontained*' } | Select-Object -First 1
        if (-not $asset) {
            $asset = $assets | Where-Object { $_.name -like '*win-x64*' } | Select-Object -First 1
        }
        if (-not $asset -and $assets.Count -gt 0) {
            $asset = $assets[0]
        }
        if (-not $asset) {
            Write-Note 'The latest release has no download.'
            return $null
        }

        Write-Info "Downloading $($asset.name)..."
        $work = Join-Path ([IO.Path]::GetTempPath()) ('WinExplorer-download-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $work -Force | Out-Null
        $script:TempPayloads.Add($work) | Out-Null

        $archive = Join-Path $work 'WinExplorer.zip'
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive -UseBasicParsing `
            -Headers @{ 'User-Agent' = 'WinExplorer-installer' } -TimeoutSec 600

        $extracted = Join-Path $work 'app'
        Expand-Archive -LiteralPath $archive -DestinationPath $extracted -Force

        $exe = Get-ChildItem -LiteralPath $extracted -Filter 'WinExplorer.exe' -Recurse -File |
            Select-Object -First 1
        if (-not $exe) {
            Write-Note 'The downloaded archive did not contain WinExplorer.exe.'
            return $null
        }

        return $exe.DirectoryName
    }
    catch {
        $status = $null
        try { $status = [int]$_.Exception.Response.StatusCode } catch { $status = $null }

        if ($status -eq 404) {
            Write-Note 'No public release has been published yet, so there is nothing to download.'
        }
        else {
            Write-Note "Could not download a release: $($_.Exception.Message)"
        }

        return $null
    }
}

function Install-DotNetSdk {
    # Building from source needs the SDK, not just the runtime. Offer winget (part of
    # Windows 11) so the user does not have to hunt for the installer.
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        return $false
    }

    if (-not $InstallSdk) {
        # Installing an SDK is not something to do behind the user's back.
        if ($Quiet -or -not $script:Interactive) {
            return $false
        }

        $question = 'Install the .NET 8 SDK (about 200 MB) so WinExplorer can be built?'
        if (-not (Ask-YesNo $question $true)) {
            return $false
        }
    }

    Write-Info 'Installing the .NET 8 SDK with winget...'
    try {
        & $winget.Source install --id Microsoft.DotNet.SDK.8 --exact --silent `
            --accept-package-agreements --accept-source-agreements --disable-interactivity | Out-Host
        Update-SessionPath
        return [bool](Resolve-DotNet)
    }
    catch {
        Write-Note "winget could not install the SDK: $($_.Exception.Message)"
        return $false
    }
}

function Show-NoPayloadHelp([string]$Root) {
    $source = Test-Path (Join-Path $Root 'WinExplorer.sln')

    Write-Host ''
    if ($source) {
        Write-Note 'This folder contains the WinExplorer source code, not a ready-to-run copy.'
    }
    else {
        Write-Note 'No WinExplorer.exe was found next to Install.cmd.'
    }

    Write-Host ''
    Write-Host '  Do one of these, then run Install.cmd again:'
    Write-Host ''
    Write-Host '  1. Download a release zip and unpack it, then run Install.cmd from that folder:'
    Write-Host '       https://github.com/gghofsu/WinExplorer/releases/latest' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  2. Install the .NET 8 SDK, so this script can build the app itself:'
    Write-Host '       winget install Microsoft.DotNet.SDK.8' -ForegroundColor Cyan
    Write-Host '       https://dotnet.microsoft.com/download/dotnet/8.0' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  3. Point the installer at an existing build:'
    Write-Host '       Install.cmd -Source C:\path\to\folder\with\WinExplorer.exe' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  4. Or download a build made by GitHub Actions (Actions > build > latest run >'
    Write-Host '     Artifacts > WinExplorer), unzip it and run Install.cmd from that folder.'
    Write-Host ''
}

function Get-AppVersion([string]$Payload, [string]$Root) {
    $exe = Join-Path $Payload 'WinExplorer.exe'
    if (Test-Path $exe) {
        try {
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
            if ($info.FileVersion) {
                return ($info.FileVersion -replace '\s*\+.*$', '').Trim()
            }
            if ($info.ProductVersion) {
                return ($info.ProductVersion -replace '\s*\+.*$', '').Trim()
            }
        }
        catch {
            # fall through to the project file
        }
    }

    $project = Join-Path $Root 'src\WinExplorer\WinExplorer.csproj'
    if (Test-Path $project) {
        $match = Select-String -Path $project -Pattern '<Version>(.*?)</Version>' | Select-Object -First 1
        if ($match -and $match.Matches.Count -gt 0) {
            return $match.Matches[0].Groups[1].Value.Trim()
        }
    }

    return '1.0.0'
}

function Copy-FileIfNeeded([string]$From, [string]$To) {
    if (-not (Test-Path $From)) {
        return $false
    }
    if (Test-Path $To) {
        $fromFull = [IO.Path]::GetFullPath($From)
        $toFull = [IO.Path]::GetFullPath($To)
        if (Test-SamePath $fromFull $toFull) {
            return $true
        }
    }
    Copy-Item -LiteralPath $From -Destination $To -Force
    return $true
}

function New-Shortcut([string]$Path, [string]$Target, [string]$Arguments, [string]$WorkingDirectory, [string]$IconPath, [string]$Description) {
    $wsh = New-Object -ComObject WScript.Shell
    $shortcut = $wsh.CreateShortcut($Path)
    $shortcut.TargetPath = $Target
    if ($Arguments) { $shortcut.Arguments = $Arguments }
    if ($WorkingDirectory) { $shortcut.WorkingDirectory = $WorkingDirectory }
    if ($IconPath) { $shortcut.IconLocation = "$IconPath,0" }
    if ($Description) { $shortcut.Description = $Description }
    $shortcut.Save()
}

function Remove-TempPayloads {
    foreach ($folder in $script:TempPayloads) {
        try {
            if ($folder -and (Test-Path $folder)) {
                Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
            # a stray temp folder is harmless
        }
    }
    $script:TempPayloads.Clear()
}

function Stop-RunningApp([string]$ExePath) {
    $running = @(Get-Process -Name $AppName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    Write-Info 'Stopping the running WinExplorer...'

    # Ask nicely first so the tray icon, hooks and IPC server shut down cleanly.
    if (Test-Path $ExePath) {
        try {
            $start = @{
                FilePath = $ExePath
                ArgumentList = '--exit'
                Wait = $true
                WindowStyle = 'Hidden'
            }
            Start-Process @start | Out-Null
        }
        catch {
            # ignored; the forced stop below handles it
        }
    }

    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 200
        if (-not (Get-Process -Name $AppName -ErrorAction SilentlyContinue)) {
            return
        }
    }

    Get-Process -Name $AppName -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# ---------------------------------------------------------------------------
# Install
# ---------------------------------------------------------------------------

$root = $null
$installDir = $null

try {
    if ($env:OS -ne 'Windows_NT') {
        throw 'WinExplorer is a Windows application and can only be installed on Windows.'
    }

    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'WinExplorer requires 64-bit Windows.'
    }

    if ($Startup -and $NoStartup) {
        throw 'Use either -Startup or -NoStartup, not both.'
    }

    if ($ContextMenu -and $NoContextMenu) {
        throw 'Use either -ContextMenu or -NoContextMenu, not both.'
    }

    foreach ($argument in $Extra) {
        switch ($argument.Trim().ToLowerInvariant()) {
            '/quiet' { $Quiet = $true }
            '/startup' { $Startup = $true }
            '/nostartup' { $NoStartup = $true }
            '/contextmenu' { $ContextMenu = $true }
            '/nocontextmenu' { $NoContextMenu = $true }
            '/nolaunch' { $NoLaunch = $true }
            '/nodownload' { $NoDownload = $true }
        }
    }

    # scripts\install-app.ps1 sits in the repository or in the unpacked release zip; in
    # both cases the payload is one level up. When the script is run from an installed copy
    # it sits next to WinExplorer.exe and that folder is the payload.
    $scriptDir = $PSScriptRoot
    $parentDir = Split-Path -Parent $scriptDir
    $root = if ($parentDir -and ((Split-Path -Leaf $scriptDir) -ieq 'scripts' -or
            (Test-Path (Join-Path $parentDir 'WinExplorer.sln')))) {
        $parentDir
    }
    else {
        $scriptDir
    }

    $installDir = Expand-Path $Destination

    # Find something to install: a pre-built copy, a build from source, or - for people who
    # downloaded the source code without an SDK - the latest release from GitHub.
    $payload = Find-Payload -Root $root -Explicit $Source

    if (-not $payload) {
        Write-Head 'WinExplorer installer'
        Write-Info 'No ready-to-run copy was found in this folder.'

        $dotnet = Resolve-DotNet

        # A source checkout with the SDK: build exactly what is in the folder.
        if (Test-DotNetSdk $dotnet) {
            $payload = Build-Payload -DotNet $dotnet -Root $root
        }

        # Otherwise grab a published build - much lighter than installing an SDK.
        if (-not $payload) {
            $payload = Get-PrebuiltPayload
        }

        # Still nothing: offer the SDK so the source in this folder can be compiled.
        if (-not $payload) {
            if (Install-DotNetSdk) {
                $dotnet = Resolve-DotNet
                if (Test-DotNetSdk $dotnet) {
                    $payload = Build-Payload -DotNet $dotnet -Root $root
                }
            }
        }
    }

    if (-not $payload) {
        Show-NoPayloadHelp $root
        throw 'Nothing to install yet.'
    }

    $version = Get-AppVersion -Payload $payload -Root $root

    # Copying a folder into itself would recurse forever.
    if (-not (Test-SamePath $payload $installDir) -and
        $installDir.StartsWith($payload.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The install folder cannot be inside the folder that holds the files to install.'
    }

    Write-Head "WinExplorer $version installer"
    Write-Info "Install folder: $installDir"
    if (-not (Test-SamePath $payload $installDir)) {
        Write-Info "Source:         $payload"
    }
    Write-Host ''

    $startupChoice = if ($Startup) { $true }
        elseif ($NoStartup) { $false }
        else { Ask-YesNo 'Start WinExplorer automatically when you sign in?' $true }

    $menuChoice = if ($ContextMenu) { $true }
        elseif ($NoContextMenu) { $false }
        else { Ask-YesNo 'Integrate with File Explorer (right-click menu and folders open as tabs)?' $true }

    Write-Host ''

    $existingExe = Join-Path $installDir 'WinExplorer.exe'
    Stop-RunningApp -ExePath $existingExe

    New-Item -ItemType Directory -Path $installDir -Force | Out-Null

    if (-not (Test-SamePath $payload $installDir)) {
        Write-Info 'Copying files...'

        $robocopy = Join-Path $env:SystemRoot 'System32\robocopy.exe'
        if (Test-Path $robocopy) {
            # Robocopy handles a few files that are still locked and long paths. The helper
            # files are excluded here and copied into the install folder explicitly below.
            & $robocopy $payload $installDir /E /XF Install.cmd Uninstall.cmd install-app.ps1 uninstall-app.ps1 `
                /XD scripts /R:3 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) {
                throw "Copying the application files failed (robocopy exit code $LASTEXITCODE)."
            }
        }
        else {
            Copy-Item -Path (Join-Path $payload '*') -Destination $installDir -Recurse -Force
        }
    }

    # Files that came out of a downloaded zip carry a mark of the web, which makes Windows
    # show a SmartScreen prompt for the app; remove it from our own install folder.
    try {
        Get-ChildItem -LiteralPath $installDir -Recurse -File |
            Unblock-File -ErrorAction SilentlyContinue
    }
    catch {
        # best effort
    }

    # The installer and the uninstaller travel with the app, so neither uninstalling nor
    # repairing ever needs the original zip again.
    $thisScript = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
    Copy-FileIfNeeded -From $thisScript -To (Join-Path $installDir 'install-app.ps1') | Out-Null
    foreach ($name in @('uninstall-app.ps1', 'Install.cmd', 'Uninstall.cmd')) {
        foreach ($candidate in @((Join-Path $root ('scripts\' + $name)), (Join-Path $root $name))) {
            if (Test-Path $candidate) {
                Copy-FileIfNeeded -From $candidate -To (Join-Path $installDir $name) | Out-Null
                break
            }
        }
    }

    $exe = Join-Path $installDir 'WinExplorer.exe'
    if (-not (Test-Path $exe)) {
        throw "WinExplorer.exe was not copied to $installDir."
    }

    Write-Ok 'Application files are in place.'

    # ---- Windows integration ------------------------------------------------

    $startInfo = @{
        FilePath = $exe
        Wait = $true
        WindowStyle = 'Hidden'
    }

    if ($menuChoice) {
        # --install-context-menu also makes WinExplorer the per-user handler for folder
        # double-clicks; --uninstall-context-menu restores the previous handler.
        Start-Process @startInfo -ArgumentList '--install-context-menu' | Out-Null
        Write-Ok 'File Explorer integration installed (right-click menu + folder double-clicks).'
    }
    else {
        Start-Process @startInfo -ArgumentList '--uninstall-context-menu' | Out-Null
        Write-Ok 'File Explorer integration not installed.'
    }

    if ($startupChoice) {
        Start-Process @startInfo -ArgumentList '--install-startup' | Out-Null
        Write-Ok 'WinExplorer will start when you sign in.'
    }
    else {
        Start-Process @startInfo -ArgumentList '--uninstall-startup' | Out-Null
        Write-Ok 'WinExplorer will not start automatically (turn it on in the settings window).'
    }

    # ---- Start Menu shortcuts ----------------------------------------------

    # Call PowerShell directly instead of going through cmd.exe: it avoids nested-quote
    # problems in shortcut targets and in the uninstall string Windows reads.
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $uninstallScript = Join-Path $installDir 'uninstall-app.ps1'
    $uninstallArguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $uninstallScript + '" -Pause'

    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WinExplorer'
    New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
    New-Shortcut -Path (Join-Path $startMenu 'WinExplorer.lnk') -Target $exe `
        -Arguments '' -WorkingDirectory $installDir -IconPath $exe `
        -Description 'Folders open as tabs in File Explorer'
    New-Shortcut -Path (Join-Path $startMenu 'Uninstall WinExplorer.lnk') `
        -Target $powershell -Arguments $uninstallArguments `
        -WorkingDirectory $installDir -IconPath $exe `
        -Description 'Remove WinExplorer from this computer'
    Write-Ok 'Start Menu shortcuts created.'

    # ---- Apps & features entry ---------------------------------------------

    $sizeKb = 0
    try {
        $bytes = (Get-ChildItem -LiteralPath $installDir -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum
        if ($bytes) {
            $sizeKb = [int][math]::Round(($bytes / 1024), 0)
        }
    }
    catch {
        $sizeKb = 0
    }

    $majorMinor = $version.Split('.')
    $major = if ($majorMinor.Count -ge 1) { [int]$majorMinor[0] } else { 1 }
    $minor = if ($majorMinor.Count -ge 2) { [int]$majorMinor[1] } else { 0 }

    if (-not (Test-Path $UninstallKey)) {
        New-Item -Path $UninstallKey -Force | Out-Null
    }

    $values = @{
        'DisplayName' = 'WinExplorer'
        'DisplayVersion' = $version
        'DisplayIcon' = $exe
        'Publisher' = 'WinExplorer'
        'URLInfoAbout' = 'https://github.com/gghofsu/WinExplorer'
        'HelpLink' = 'https://github.com/gghofsu/WinExplorer/issues'
        'InstallLocation' = $installDir
        'InstallDate' = (Get-Date -Format 'yyyyMMdd')
        'UninstallString' = '"' + $powershell + '" ' + $uninstallArguments
        'QuietUninstallString' = '"' + $powershell + '" -NoProfile -ExecutionPolicy Bypass -File "' +
            $uninstallScript + '" -Quiet -KeepSettings'
        'NoModify' = 1
        'NoRepair' = 1
        'Language' = 1033
    }

    if ($sizeKb -gt 0) {
        $values['EstimatedSize'] = $sizeKb
    }

    foreach ($name in $values.Keys) {
        Set-ItemProperty -Path $UninstallKey -Name $name -Value $values[$name]
    }
    Set-ItemProperty -Path $UninstallKey -Name 'VersionMajor' -Value $major -Type DWord
    Set-ItemProperty -Path $UninstallKey -Name 'VersionMinor' -Value $minor -Type DWord

    Write-Ok 'Added to Settings > Apps > Installed apps (Windows uninstaller).'

    # ---- Done ---------------------------------------------------------------

    Write-Host ''
    Write-Host "WinExplorer $version is installed." -ForegroundColor Green
    Write-Host ''
    Write-Info 'Uninstall any time by:'
    Write-Info '* Settings > Apps > Installed apps > WinExplorer > Uninstall'
    Write-Info '* Start Menu > WinExplorer > Uninstall WinExplorer'
    Write-Info '* double-click Uninstall.cmd in the install folder'
    Write-Info 'Startup and the File Explorer integration can be switched on or off in the'
    Write-Info 'settings window, from the tray menu, or with --toggle-startup and'
    Write-Info '--install-context-menu / --uninstall-context-menu.'

    if (-not $NoLaunch) {
        Write-Host ''
        Write-Info 'Starting WinExplorer...'
        Start-Process -FilePath $exe -WorkingDirectory $installDir
    }

    Remove-TempPayloads
    Stop-BeforeClose
    exit 0
}
catch {
    Write-Host ''
    Write-Host "Installation failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''

    $installedExe = if ($installDir) { Join-Path $installDir 'WinExplorer.exe' } else { $null }
    if ($root -and -not ($installedExe -and (Test-Path $installedExe))) {
        Show-NoPayloadHelp $root
    }

    Remove-TempPayloads
    Stop-BeforeClose
    exit 1
}
