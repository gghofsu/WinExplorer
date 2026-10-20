# WinExplorer

**Every folder opens as a tab in the File Explorer window you already have — and opening a
folder that is already open just switches to its tab instead of creating a duplicate.**

WinExplorer is a small Windows 11 tray app that gives File Explorer the tab behaviour of a
browser (or of Total Commander / Finder): one window, many folders, no accidental copies of
the same folder in three different tabs.

```
double-click  D:\Projects\Website        →  a new tab in the window you are using
double-click  D:\Projects\Website  again  →  that tab is brought to the front (no duplicate)
right-click   → Open in new tab           →  a real duplicate, because you asked for it
right-click   → Open in new window        →  a separate window, because you asked for it
```

---

## Contents

- [Features](#features)
- [How the rules work](#how-the-rules-work)
- [Requirements](#requirements)
- [Install](#install)
- [Using it](#using-it)
- [Settings](#settings)
- [Command line](#command-line)
- [Shortcuts](#shortcuts)
- [Troubleshooting](#troubleshooting)
- [Known limits](#known-limits)
- [Uninstall](#uninstall)
- [Development](#development)
- [Project layout](#project-layout)
- [License](#license)

---

## Features

| | |
|---|---|
| **Folders open as tabs** | Double-clicking a folder inside File Explorer adds a tab to the window you are working in instead of spawning a window. |
| **No accidental duplicates** | Opening a folder that is already shown anywhere switches to the tab that shows it (and brings its window forward). |
| **Duplicates on demand** | Only an explicit request creates a second tab for the same folder: *Open in new tab*, *Open in new window* (right-click menu), the duplicate shortcut, or `--new-tab` / `--new-window` on the command line. |
| **Middle-click** | Middle-clicking a folder opens it in a background tab, exactly like the built-in behaviour — but it also de-duplicates. |
| **Window folding** *(optional)* | Windows opened from the desktop, Start, the taskbar or another app are folded into a tab too. Right after a right-click, and while Ctrl+Shift is held, windows are left alone so "Open in new window" keeps working. |
| **Duplicate / reopen shortcuts** | Global shortcuts to duplicate the current tab and to reopen the last closed folder, browser style. |
| **Win+E as a tab** *(optional)* | Turn Win+E into "open This PC (or Home) as a tab". |
| **Per-user only** | Lives in `HKCU`, needs no administrator rights, touches no system files, does not inject code into Explorer. |
| **Honest fallbacks** | If Explorer refuses a tab (Windows 10, a Windows update, a virtual folder) the folder opens in a window instead of vanishing. |

## How the rules work

The whole app is built around one decision table:

| The user does this | Already open? | Result |
|---|---|---|
| Double-click a folder in Explorer | no | new tab in the active window |
| Double-click a folder in Explorer | yes | switch to the existing tab, no duplicate |
| Middle-click a folder | no | new *background* tab |
| Middle-click a folder | yes | switch to the existing tab |
| Right-click → **Open in new tab** | any | always a new tab (duplicate allowed) |
| Right-click → **Open in new window** | any | always a new window (duplicate allowed) |
| Duplicate shortcut (`Alt+Shift+D`) | any | new tab with the same folder |
| `--new-tab` / `--new-window` | any | always new tab / new window |
| Ctrl / Alt / Shift / Win + double-click | any | plain Windows behaviour, WinExplorer steps aside |

"Already open" is decided by asking the shell for the location of every tab in every
File Explorer window. String comparison is the fast path; paths that differ textually
(`C:\PROGRA~1`, junctions, redirected folders, `shell:::{GUID}` items) are compared through
shell item IDs (PIDLs), which is how Windows itself decides whether two folders are the same.

`Ctrl / Alt / Shift / Win` are the universal escape hatch: with any modifier held the click
is passed straight to Explorer.

## Requirements

- Windows 11 **22H2 or newer** — tabs in File Explorer are required. On Windows 10 the app
  still runs (context menu, shortcuts, settings) but folders open in windows.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or use the
  self-contained build, which needs nothing installed).
- No administrator rights. Everything is stored in your own user profile.

## Install

### From a release

1. Download `WinExplorer-<version>-win-x64.zip`.
2. Unpack it somewhere permanent, e.g. `%LOCALAPPDATA%\Programs\WinExplorer`.
3. Run `WinExplorer.exe`.
4. In the settings window turn on:
   - **Add "Open in WinExplorer tab" to the folder right-click menu**
   - **Start WinExplorer when I sign in**

   Both are per-user and can be switched off again at any time.

### From source

```powershell
git clone https://github.com/gghofsu/WinExplorer
cd WinExplorer
dotnet publish src\WinExplorer\WinExplorer.csproj -c Release -r win-x64 --self-contained false -o .\publish
.\publish\WinExplorer.exe
```

Or use the helper scripts:

```powershell
.\scripts\build.ps1                  # build + run the unit tests
.\scripts\publish.ps1                # framework-dependent zip in .\artifacts
.\scripts\publish.ps1 -SelfContained  # no runtime needed, larger zip
.\scripts\install.ps1                # publish to %LOCALAPPDATA%\Programs\WinExplorer, register, start
.\scripts\install.ps1 -NoBuild       # only register an existing install
```

`install.ps1` copies the app into `%LOCALAPPDATA%\Programs\WinExplorer`, turns on the
context menu and the start-with-Windows entry, and launches it. Everything it writes is
per-user, so no elevation is involved.

### Installer

`installer\WinExplorer.iss` is a per-user [Inno Setup](https://jrsoftware.org/isdl.php) 6
script. Build the payload and the setup executable in one step:

```powershell
.\scripts\make-installer.ps1                 # self-contained payload (no .NET needed)
.\scripts\make-installer.ps1 -SelfContained:$false
```

The result is `installer\Output\WinExplorer-<version>-setup.exe`. The wizard offers
"start when I sign in" and "add the folder right-click menu" as tasks; uninstalling removes
both again and restores the previous folder handler. Like everything else it installs per
user into `%LOCALAPPDATA%\Programs\WinExplorer` and never asks for administrator rights.

## Using it

- **Start it once** (or let it start with Windows). It lives in the notification area.
- **Double-click folders** as usual. They open as tabs.
- **Right-click a folder** → *Open in WinExplorer tab* to be explicit, *Open in new tab* to
  force a duplicate, *Open in new window* to get a window.
- **Duplicate** the current tab with `Alt+Shift+D`, **reopen** the last closed folder with
  `Alt+Shift+T`, **pause/resume** WinExplorer with `Ctrl+Alt+P`.
- Closing the settings window keeps WinExplorer running in the tray (configurable). Use
  *Exit WinExplorer* in the tray menu to stop it completely.

## Settings

All settings are stored in `%APPDATA%\WinExplorer\settings.json` and applied immediately —
there is no Save button.

| Setting | Meaning |
|---|---|
| **Double-clicking a folder opens it as a tab** | The main switch on the click side. |
| **Middle-clicking a folder opens it in a background tab** | De-duplicating middle-click. |
| **When the folder is already open** | *Switch to the tab that already shows it* (default) or *Open another tab anyway*. |
| **Look for an already open copy in every Explorer window** | Off = only the target window is searched for duplicates. |
| **Open new tabs in** | The window you are using, the last window you used, or the window with the most tabs. |
| **Bring the new tab to the front** | Off = tabs are created in the background. |
| **Fold other new Explorer windows into tabs** | Catches folders opened from the desktop, Start, Win+E or other apps. |
| **Keep virtual folders as windows** | This PC, Recycle Bin, Control Panel, … stay windows. |
| **Use Win+E to open a tab** | Replaces the Win+E shortcut. |
| **Add "Open in WinExplorer tab"…** | Per-user context menu entries (`HKCU\Software\Classes\Directory\shell`). |
| **Use WinExplorer for every folder double-click in Windows** | Makes WinExplorer the default handler for folders (so double-clicking a folder on the desktop, in a file dialog or in another app is folded in as well). Restores the previous handler when switched off. |
| **Start WinExplorer when I sign in** | `HKCU\...\CurrentVersion\Run`. |
| **Close button keeps WinExplorer running in the notification area** | Tray behaviour. |
| **Show a notification when a tab is opened or switched** | Tray balloon tips. |
| **Folders that keep normal Windows behaviour** | An exclusion list, e.g. a folder where you always want a new window. |
| **Shortcuts** | Click a box and press the keys you want; Backspace clears it. |

## Command line

```
WinExplorer.exe                          Show the settings window (starts the helper)
WinExplorer.exe --open <folder>          Open as a tab; switch if already open
WinExplorer.exe --open <folder> --new-tab
                                         Always create a new tab (duplicate allowed)
WinExplorer.exe --open <folder> --new-window
                                         Always create a separate window
WinExplorer.exe --open <folder> --no-activate
                                         Open without stealing focus
WinExplorer.exe --duplicate-tab [--hwnd <handle>]
WinExplorer.exe --reopen-closed
WinExplorer.exe --pause | --resume | --toggle
WinExplorer.exe --settings               Show the window of the running instance
WinExplorer.exe --install-context-menu   Add the right-click entries (per user)
WinExplorer.exe --uninstall-context-menu Remove them and restore the old handler
WinExplorer.exe --install-startup | --uninstall-startup
WinExplorer.exe --log                    Open the log file
WinExplorer.exe --exit                   Stop the helper
WinExplorer.exe --version | --help
```

A bare path (`WinExplorer.exe "C:\Temp"`) is treated as `--open "C:\Temp"`.

The command line is also the integration point used by the context menu and by shortcuts:

```
Target: "C:\Path\WinExplorer.exe" --open "%1"
```

Because a running helper owns the hooks, every command is forwarded to it over a named pipe
(`WinExplorer.Ipc.v1`) — a second `WinExplorer.exe` process only sends the request and exits.
Console output is written to the console you started it from when there is one.

## Shortcuts

| Default | Action | Scope |
|---|---|---|
| `Alt+Shift+D` | Duplicate the active tab | File Explorer in front |
| `Alt+Shift+T` | Reopen the last closed folder | File Explorer in front |
| `Ctrl+Alt+P` | Pause / resume WinExplorer | Everywhere |

All three can be changed in the **Shortcuts** tab.

## Troubleshooting

- **A folder still opens in a window** — either WinExplorer is paused (check the tray icon
  and the header of the settings window), the folder is on the exclusion list, a modifier
  key was held, or the folder is a virtual location (search results, an archive, a phone).
  With **Fold other new Explorer windows into tabs** switched on, such windows are folded in
  a moment later.
- **"Open in new window" opens a tab instead** — *Fold other new Explorer windows into tabs*
  is on. Hold `Ctrl+Shift` while opening, or turn the option off.
- **Tabs do not work at all** — tabs need Windows 11 22H2+. On older builds folders fall
  back to windows and the log says so.
- **Nothing happens at all** — make sure only one WinExplorer is running (tray icon), then
  check `%APPDATA%\WinExplorer\WinExplorer.log` (tray menu → *Open log file*). The log
  records every tab command and every fallback with timestamps.
- **Explorer restarts** — WinExplorer re-attaches automatically; the log notes it.
- **Undoing the integration** — switch the two Windows-integration checkboxes off, or run
  `WinExplorer.exe --uninstall-context-menu`.

## Known limits

- File Explorer's tab commands (`WM_COMMAND 0xA21B` / `0xA221` / `0xA021`) are undocumented
  internals. Microsoft can change them in any update. WinExplorer verifies every step, falls
  back to a normal window and logs what happened instead of silently failing.
- Folders inside archives, search results, libraries and other virtual locations keep
  Explorer's own behaviour.
- Because the double-click interception runs in a low-level mouse hook, a click that turns
  out not to be a folder is replayed with synthesized input. This is invisible in practice
  but is the reason the app needs the hook to stay responsive.
- Windows may show the "Open with" style chooser the first time a verb is used, depending on
  your shell settings; choose WinExplorer and tick *Always*.

## Uninstall

1. Tray menu → **Exit WinExplorer**.
2. Settings → switch off **Start WinExplorer when I sign in** and both integration
   checkboxes (this removes the registry entries and restores your previous folder handler).
3. Delete the folder you unpacked WinExplorer into (or run the uninstaller when you used
   `WinExplorer-<version>-setup.exe`).
4. Optionally delete `%APPDATA%\WinExplorer`.

The command line equivalents are `WinExplorer.exe --uninstall-context-menu` and
`WinExplorer.exe --uninstall-startup`; they only remove keys under `HKCU` and restore the
folder handler that was in place before WinExplorer was installed.

No files outside your user profile are touched.

## Development

```
dotnet build WinExplorer.sln -c Release
dotnet test  WinExplorer.sln -c Release
```

The unit tests cover the platform-independent core: path normalisation, shortcut parsing,
command line parsing, closed-tab history and settings persistence. They run on Linux and
Windows; the app itself builds and runs on Windows only.

See [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) for the technical details (which Explorer
commands are used, how tabs are addressed, how duplicates are detected) and
[docs/TESTING.md](docs/TESTING.md) for the manual test checklist.

## Project layout

```
src/WinExplorer/
  Core/            settings, command line, shortcuts, path normalisation (no Win32)
  Interop/         P/Invoke declarations, MSAA + IShellFolder interfaces
  Shell/           Explorer windows/tabs, tab commands, shell automation, PIDL comparison
  Hooks/           low-level mouse + keyboard hooks, MSAA hit testing
  Services/        policy (open/switch/merge), watcher, IPC, registry, tray, logging
  UI/              WPF settings window
tests/             xUnit tests for the core
scripts/           build, publish, install and installer helpers
installer/         Inno Setup script (per-user setup executable)
docs/              architecture and manual test checklist
assets/            icon artwork (generated by tools/make-icon.py)
```

## License

MIT — see [LICENSE](LICENSE).
