@echo off
setlocal EnableExtensions
title WinExplorer installer

rem ---------------------------------------------------------------------------
rem  Double-click this file to install WinExplorer for the current user.
rem  No administrator rights are needed: every file and registry key belongs to
rem  the account that runs it.
rem
rem  Options can be passed through, for example:
rem      Install.cmd -NoStartup
rem      Install.cmd -Quiet -NoLaunch
rem      Install.cmd -Startup -ContextMenu
rem  See scripts\install-app.ps1 for the full list.
rem ---------------------------------------------------------------------------

set "SCRIPT=%~dp0scripts\install-app.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0install-app.ps1"

if not exist "%SCRIPT%" (
    echo.
    echo Could not find install-app.ps1 next to this file.
    echo Unpack the WinExplorer release zip and run Install.cmd from that folder.
    echo.
    pause
    exit /b 1
)

where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo.
    echo Windows PowerShell was not found. Windows 10 and 11 include it by default.
    echo.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Pause %*
set "CODE=%ERRORLEVEL%"

endlocal & exit /b %CODE%
