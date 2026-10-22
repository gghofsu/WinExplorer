@echo off
setlocal EnableExtensions
title WinExplorer uninstaller

rem ---------------------------------------------------------------------------
rem  Double-click this file to uninstall WinExplorer for the current user.
rem
rem  The same uninstaller appears in Settings > Apps > Installed apps, in the
rem  Start Menu and in the tray/settings window of the app itself.
rem
rem  Options can be passed through, for example:
rem      Uninstall.cmd -KeepSettings
rem      Uninstall.cmd -RemoveSettings
rem      Uninstall.cmd -Quiet
rem  See scripts\uninstall-app.ps1 for the full list.
rem ---------------------------------------------------------------------------

set "SCRIPT=%~dp0scripts\uninstall-app.ps1"
if not exist "%SCRIPT%" set "SCRIPT=%~dp0uninstall-app.ps1"

if not exist "%SCRIPT%" (
    echo.
    echo Could not find uninstall-app.ps1 next to this file.
    echo Use Settings ^> Apps ^> Installed apps ^> WinExplorer ^> Uninstall instead.
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
