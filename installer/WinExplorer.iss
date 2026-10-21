; WinExplorer - per-user Inno Setup script.
;
; Build the payload first (from the repository root):
;   scripts\publish.ps1 -SelfContained
;
; Then compile this script with Inno Setup 6:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\WinExplorer.iss
;
; The result is installer\Output\WinExplorer-<version>-setup.exe.
; The installer writes only into %LOCALAPPDATA%\Programs and HKCU, so it never asks
; for administrator rights.

#define AppName "WinExplorer"
#define AppPublisher "WinExplorer"
#define AppUrl "https://github.com/gghofsu/WinExplorer"
#define AppExe "WinExplorer.exe"

; Keep the version in one place: src\WinExplorer\WinExplorer.csproj (<Version>).
; scripts\make-installer.ps1 passes it as /DAppVersion=<version>; when compiling by hand
; either set the WINEXPLORER_VERSION environment variable or accept the default.
#ifndef AppVersion
  #define AppVersion GetEnv("WINEXPLORER_VERSION")
  #if AppVersion == ''
    #define AppVersion "1.0.0"
  #endif
#endif

; Prefer the self-contained payload, fall back to the framework-dependent one.
#if FileExists(AddBackslash(SourcePath) + "..\publish\self-contained\WinExplorer.exe")
#define PayloadDir "..\publish\self-contained"
#elif FileExists(AddBackslash(SourcePath) + "..\publish\framework-dependent\WinExplorer.exe")
#define PayloadDir "..\publish\framework-dependent"
#else
#define PayloadDir "..\publish\self-contained"
#endif

[Setup]
AppId={{9C2F5B47-2C2E-4E36-9A3D-6F1B8A1E5D74}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=Output
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\src\WinExplorer\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; Registered under HKCU, so Windows Settings > Apps lists it for the current user and its
; uninstaller needs no administrator rights either.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in"; GroupDescription: "Windows integration:"; Flags: checkedonce
Name: "contextmenu"; Description: "Integrate with File Explorer (right-click menu and folders open as tabs)"; GroupDescription: "Windows integration:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; These switches only touch HKCU and exit immediately. The "not <task>" entries make the
; checkboxes real add/remove options when Setup is run over an existing installation.
Filename: "{app}\{#AppExe}"; Parameters: "--install-startup"; Flags: runhidden waituntilterminated; Tasks: startup
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-startup"; Flags: runhidden waituntilterminated; Tasks: not startup
Filename: "{app}\{#AppExe}"; Parameters: "--install-context-menu"; Flags: runhidden waituntilterminated; Tasks: contextmenu
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-context-menu"; Flags: runhidden waituntilterminated; Tasks: not contextmenu
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Undo the registry work *before* the files disappear.
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-context-menu"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveContextMenu"
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-startup"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStartup"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;

  { Windows 11 22H2 is the first build with Explorer tabs. Older builds still run,
    but folders open as windows, so be honest about it. }
  if not IsWin64 then
  begin
    MsgBox('WinExplorer requires 64-bit Windows.', mbError, MB_OK);
    Result := False;
  end
  else if (GetWindowsVersion < $0A000000) then
    MsgBox('WinExplorer is designed for Windows 11. It will install, but folder ' +
           'tabs are only available on Windows 11 22H2 or newer.', mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
  Prompt: string;
begin
  { Settings live outside {app}, so they survive an uninstall unless the user says
    otherwise. Ask once, after the program files are gone. }
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\WinExplorer');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      Prompt := 'Do you also want to delete your WinExplorer settings and log?';
      Prompt := Prompt + #13#10 + #13#10 + DataDir;
      if MsgBox(Prompt, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
