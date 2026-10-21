#Requires -Version 5.1
<#
    WinExplorer - per-user uninstaller.

    Double-click Uninstall.cmd, use the Start Menu shortcut, or use
    Settings > Apps > Installed apps > WinExplorer > Uninstall (the Windows uninstaller).

    It stops WinExplorer and removes, per user only:
      * the startup entry (HKCU ...\CurrentVersion\Run),
      * the folder right-click menu entries (and restores the previous folder handler),
      * the Start Menu shortcuts and the "Apps & features" entry,
      * the application folder.

    Your settings and log (%APPDATA%\WinExplorer) are kept unless you ask for them to be
    deleted, so reinstalling keeps your configuration.

    Options (all optional):
      -Quiet          no questions; used by Settings > Apps (removes everything but settings)
      -KeepSettings   never delete %APPDATA%\WinExplorer
      -RemoveSettings delete %APPDATA%\WinExplorer without asking
      -Destination <folder>  uninstall from a folder other than the one recorded in the registry
      -Pause          wait for Enter before the window closes
#>
[CmdletBinding()]
param(
    [string]$Destination,
    [switch]$Quiet,
    [switch]$KeepSettings,
    [switch]$RemoveSettings,
    [switch]$Pause,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Extra = @()
)

$ErrorActionPreference = 'Stop'
$AppName = 'WinExplorer'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinExplorer'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$ApprovedRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$ClassesRoot = 'HKCU:\Software\Classes'

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

function Ask-YesNo([string]$Question, [bool]$Default = $false) {
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

function Expand-Path([string]$Path) {
    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    return [IO.Path]::GetFullPath($expanded)
}

function Remove-RegistryValue([string]$Key, [string]$Name) {
    try {
        if (Test-Path $Key) {
            Remove-ItemProperty -Path $Key -Name $Name -Force -ErrorAction SilentlyContinue
        }
    }
    catch {
        # best effort
    }
}

function Stop-RunningApp([string]$ExePath) {
    $running = @(Get-Process -Name $AppName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) {
        return
    }

    Write-Info 'Stopping WinExplorer...'

    if (Test-Path $ExePath) {
        try {
            # No -Wait: a wedged instance must not block the installer behind a dialog.
            Start-Process -FilePath $ExePath -ArgumentList '--exit' -WindowStyle Hidden | Out-Null
        }
        catch {
            # ignored; the forced stop below handles it
        }
    }

    for ($i = 0; $i -lt 25; $i++) {
        Start-Sleep -Milliseconds 200
        if (-not (Get-Process -Name $AppName -ErrorAction SilentlyContinue)) {
            return
        }
    }

    Get-Process -Name $AppName -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Remove-ContextMenu {
    # The app knows how to restore the previous folder handler; use it when it is present.
    $verbRoot = Join-Path $ClassesRoot 'Directory\shell'
    $driveVerb = Join-Path $ClassesRoot 'Drive\shell\WinExplorer.OpenTab'
    $backgroundVerb = Join-Path $ClassesRoot 'Directory\Background\shell\WinExplorer.OpenTabHere'
    $backupKey = 'HKCU:\Software\WinExplorer\Backup'

    foreach ($key in @(
            (Join-Path $ClassesRoot 'Directory\shell\WinExplorer.OpenTab'),
            (Join-Path $ClassesRoot 'Directory\shell\WinExplorer.OpenWindow'),
            $backgroundVerb,
            $driveVerb)) {
        try {
            if (Test-Path $key) {
                Remove-Item -Path $key -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
            # best effort
        }
    }

    # Restore the double-click handler only when it still points at us. The default value of
    # a registry key has no name, so use the .NET API instead of the provider's '(default)'.
    try {
        $shellKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\Directory\shell', $true)
        if ($shellKey) {
            try {
                $current = [string]$shellKey.GetValue($null)
                if ($current -eq 'WinExplorer.OpenTab') {
                    $stored = $null
                    $existed = $false

                    $backup = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\WinExplorer\Backup')
                    if ($backup) {
                        try {
                            if ($null -ne $backup.GetValue('DirectoryShellDefault')) {
                                $stored = [string]$backup.GetValue('DirectoryShellDefault')
                                $existed = ([int]$backup.GetValue('DirectoryShellDefaultExisted', 0) -eq 1)
                            }
                        }
                        finally {
                            $backup.Dispose()
                        }
                    }

                    if ($existed -and $stored) {
                        $shellKey.SetValue($null, $stored)
                    }
                    else {
                        $shellKey.DeleteValue('', $false)
                    }
                }
            }
            finally {
                $shellKey.Dispose()
            }
        }
    }
    catch {
        # best effort
    }

    foreach ($key in @($backupKey, 'HKCU:\Software\WinExplorer')) {
        try {
            if (Test-Path $key) {
                Remove-Item -Path $key -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
            # best effort
        }
    }
}

function Remove-Shortcuts {
    $folders = @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\WinExplorer'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\WinExplorer')
    )

    foreach ($folder in $folders) {
        try {
            if (Test-Path $folder) {
                Remove-Item -Path $folder -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        catch {
            # best effort
        }
    }

    # A desktop shortcut is optional and only ever created for the current user.
    $desktop = [Environment]::GetFolderPath('Desktop')
    if ($desktop) {
        foreach ($name in @('WinExplorer.lnk', 'Uninstall WinExplorer.lnk')) {
            $path = Join-Path $desktop $name
            try {
                if (Test-Path $path) {
                    Remove-Item -Path $path -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
                # best effort
            }
        }
    }
}

function Test-SafeInstallFolder([string]$Folder) {
    # Never hand a root folder ("C:\") or an unrelated directory to Remove-Item, even when
    # a damaged registry value points there.
    $trimmed = $Folder.TrimEnd('\')
    if ($trimmed.Length -eq 0) {
        return $false
    }
    if ($trimmed -match '^[A-Za-z]:$') {
        return $false
    }

    $leaf = Split-Path -Leaf $trimmed
    if (-not $leaf) {
        return $false
    }

    # Only delete something that is recognisably ours: the default folder name or a folder
    # that actually contains the application.
    if ($leaf -ine $AppName -and -not (Test-Path (Join-Path $trimmed 'WinExplorer.exe'))) {
        return $false
    }

    return $true
}

function Remove-InstallFolder([string]$Folder) {
    if (-not (Test-Path $Folder)) {
        return $true
    }

    # The running uninstaller and its .cmd wrapper live inside this folder. Windows usually
    # allows the delete anyway; when it does not, hand the job to a detached cmd that runs
    # after this process has exited.
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try {
            Remove-Item -Path $Folder -Recurse -Force -ErrorAction Stop
            return $true
        }
        catch {
            Start-Sleep -Milliseconds 400
        }
    }

    try {
        # A tiny batch file runs after this process has exited (the two-second ping is the
        # delay) and deletes the folder, including the uninstaller that is locking it now.
        $cleanup = Join-Path ([IO.Path]::GetTempPath()) ('WinExplorer-cleanup-' + [Guid]::NewGuid().ToString('N') + '.cmd')
        Set-Content -Path $cleanup -Encoding ASCII -Value @(
            '@echo off',
            'ping -n 3 127.0.0.1 >nul',
            'rmdir /s /q "' + $Folder + '"',
            'del "%~f0"'
        )
        Start-Process -FilePath $cleanup -WindowStyle Hidden | Out-Null
        Write-Info 'The folder is locked by the uninstaller itself; it will be removed in a moment.'
    }
    catch {
        Write-Note "Could not delete $Folder. Remove it manually."
        return $false
    }

    return $true
}

# ---------------------------------------------------------------------------
# Uninstall
# ---------------------------------------------------------------------------

try {
    if ($env:OS -ne 'Windows_NT') {
        throw 'WinExplorer is a Windows application.'
    }

    foreach ($argument in $Extra) {
        switch ($argument.Trim().ToLowerInvariant()) {
            '/quiet' { $Quiet = $true }
            '/keepsettings' { $KeepSettings = $true }
            '/removesettings' { $RemoveSettings = $true }
        }
    }

    $dataFolder = Join-Path $env:APPDATA $AppName

    if (-not $Destination) {
        $recorded = $null
        try {
            if (Test-Path $UninstallKey) {
                $recorded = (Get-ItemProperty -Path $UninstallKey -Name 'InstallLocation' -ErrorAction SilentlyContinue).InstallLocation
            }
        }
        catch {
            $recorded = $null
        }

        if ($recorded) {
            $Destination = $recorded
        }
        else {
            $Destination = Join-Path $env:LOCALAPPDATA 'Programs\WinExplorer'
        }
    }

    $installDir = Expand-Path $Destination
    $exe = Join-Path $installDir 'WinExplorer.exe'
    $installed = (Test-Path $installDir) -or (Test-Path $UninstallKey)

    Write-Head 'WinExplorer uninstaller'

    if (-not $installed) {
        Write-Info 'WinExplorer does not appear to be installed for this user.'
        Write-Info "Checked: $installDir"
        Stop-BeforeClose
        exit 0
    }

    Write-Info "Install folder: $installDir"
    Write-Info 'This removes the app, the startup entry, the folder right-click menu, the'
    Write-Info 'Start Menu shortcuts and the entry in Settings > Apps.'
    Write-Info "Settings and logs in $dataFolder can be kept."
    Write-Host ''

    if (-not $Quiet) {
        if (-not (Ask-YesNo 'Uninstall WinExplorer now?' $false)) {
            Write-Info 'Uninstall cancelled. Nothing was changed.'
            Stop-BeforeClose
            exit 0
        }
    }

    if (-not $KeepSettings -and -not $RemoveSettings) {
        $RemoveSettings = Ask-YesNo 'Also delete your WinExplorer settings and log?' $false
    }

    Write-Host ''

    # 1. Stop the app so the files can be replaced.
    Stop-RunningApp -ExePath $exe

    # 2. Undo the Windows integration. The app itself restores the previous folder handler;
    #    the direct registry edits are the fallback for a damaged or missing installation.
    if (Test-Path $exe) {
        foreach ($verb in @('--uninstall-context-menu', '--uninstall-startup')) {
            try {
                $start = @{
                    FilePath = $exe
                    ArgumentList = $verb
                    Wait = $true
                    WindowStyle = 'Hidden'
                }
                Start-Process @start | Out-Null
            }
            catch {
                # the direct edits below still run
            }
        }
    }

    Remove-ContextMenu
    Write-Ok 'Folder right-click menu removed and the previous handler restored.'

    Remove-RegistryValue -Key $RunKey -Name $AppName
    Remove-RegistryValue -Key $ApprovedRunKey -Name $AppName
    Write-Ok 'Startup entry removed.'

    Remove-Shortcuts
    Write-Ok 'Start Menu shortcuts removed.'

    try {
        if (Test-Path $UninstallKey) {
            Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Ok 'Removed from Settings > Apps > Installed apps.'
    }
    catch {
        Write-Note 'Could not remove the "Apps & features" entry; Windows will hide it after the folder is gone.'
    }

    # 3. Delete the program files.
    if (Test-SafeInstallFolder $installDir) {
        $removed = Remove-InstallFolder -Folder $installDir
        if ($removed -and -not (Test-Path $installDir)) {
            Write-Ok 'Application files removed.'
        }
    }
    else {
        Write-Note "Skipped deleting '$installDir' because it does not look like a WinExplorer folder."
        Write-Note 'Remove that folder by hand if it is left over.'
    }

    # 4. Settings and logs.
    if ($RemoveSettings -and -not $KeepSettings) {
        try {
            if (Test-Path $dataFolder) {
                Remove-Item -Path $dataFolder -Recurse -Force -ErrorAction SilentlyContinue
                Write-Ok 'Settings and log deleted.'
            }
        }
        catch {
            Write-Note "Could not delete $dataFolder. Remove it manually."
        }
    }
    else {
        Write-Info "Your settings and log were kept in $dataFolder."
    }

    Write-Host ''
    Write-Host 'WinExplorer has been uninstalled.' -ForegroundColor Green
    Write-Host ''

    Stop-BeforeClose
    exit 0
}
catch {
    Write-Host ''
    Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    Stop-BeforeClose
    exit 1
}
