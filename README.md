# WinExplorer

**Every folder opens as a tab in the File Explorer window you already have — and opening a
folder that is already open just switches to its tab instead of creating a duplicate.**

WinExplorer is a small Windows 11 tray app built on the mechanism of
[w4po/ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility): whenever Windows is
about to show a **new File Explorer window** — from the desktop, Start, the taskbar, Win+E,
a file dialog, another app — that window is caught before it is painted and its folder is
reopened as a **tab** of the window that is already there. One window, many folders, no
accidental copies of the same folder in three different tabs.

```
open D:\Projects\Website from the desktop  →  a tab in the Explorer window you already have
open D:\Projects\Website again             →  its existing tab is brought to the front
hold Ctrl+Shift while it opens             →  a real, separate window, because you asked
```

Double-clicking a folder *inside* an Explorer window keeps Explorer's own behaviour: it
navigates the current tab (Ctrl+double-click opens Explorer's own new tab, as always).

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
| **New windows become tabs** | Any folder that would open a new Explorer window — the desktop, Start, the taskbar, Win+E, a file dialog, another app, the context menu, the command line — lands as a tab in the window you already have. |
| **No accidental duplicates** | Opening a folder that is already shown in a tab switches to that tab — refreshed, and with its window brought forward — instead of creating a second copy. |
| **Invisible merge** | The surplus window is cloaked by DWM before it is ever painted (from a hook thread that does nothing else), so folding a window into a tab happens without anything flashing on screen. |
| **Switches are fresh** | Every switch to an already open tab refreshes it (F5) on the way, so the tab never shows the folder as it looked when it was last visited. |
| **New tabs come to the front** | A tab opened inside Explorer with Ctrl+T, a middle-click or "Open in new tab" is selected immediately; if its folder is already open — even when the folder only finishes loading afterwards — the duplicate tab closes and the existing tab is selected and refreshed. |
| **Home / This PC / Desktop / Downloads first** | These four folders are always placed at the left of the tab strip, in that order (Home, This PC, Desktop, Downloads), in front of every other tab — the way Explorer's own windows start at Home. This also applies when one of them is opened as a new tab inside the current window. |
| **Duplicates on demand** | Only an explicit request creates a second tab for the same folder: the duplicate shortcut, or `--new-tab` / `--new-window` on the command line. |
| **A window when you want one** | Hold **Ctrl+Shift** while a folder opens and the window stays a window; it is not folded back afterwards. |
| **Collect windows into one** | The tray menu (and `--single-window`) folds every open Explorer window into one tabbed window on demand. |
| **Duplicate / reopen shortcuts** | Global shortcuts to duplicate the current tab and to reopen the last closed folder, browser style. |
| **Win+E as a tab** *(optional)* | Turn Win+E into "open This PC (or Home) as a tab". |
| **One-click install and uninstall** | Double-click `Install.cmd` to install, `Uninstall.cmd` (or the entry Windows itself lists in *Settings → Apps*) to remove it. Startup is one checkbox — or one tray-menu click — either way. |
| **Per-user only** | Lives in `HKCU`, needs no administrator rights, touches no system files, does not inject code into Explorer. |
| **Honest fallbacks** | If Explorer refuses a tab (Windows 10, a Windows update) the folder opens in a window instead of vanishing. |

## How the rules work

The whole app is built around one decision table:

| The user does this | Already open? | Result |
|---|---|---|
| Open a folder that would make a new window (desktop, Start, Win+E, another app, …) | no | new tab in the existing window |
| Open a folder that would make a new window | yes | switch to the existing tab (refreshed), no duplicate |
| Double-click a folder *inside* Explorer | — | Explorer's own behaviour: navigates the current tab |
| Ctrl+T, middle-click or "Open in new tab" inside Explorer | no | new tab, brought to the front; a special folder moves left |
| Ctrl+T, middle-click or "Open in new tab" inside Explorer | yes | close the redundant new tab and switch to the existing tab (refreshed) |
| Duplicate shortcut (`Alt+Shift+D`) | any | new tab with the same folder |
| `--new-tab` / `--new-window` | any | always new tab / new window |
| **Ctrl+Shift** held while the folder opens | any | plain Windows behaviour: a real, separate window |
| Drag a tab out of a window | — | the dragged-out window stays a window |

"Already open" is decided from the locations the shell reports for every tab in every
File Explorer window. String comparison is the fast path; paths that differ textually
(`C:\PROGRA~1`, junctions, redirected folders, `shell:::{GUID}` items) are compared through
shell item IDs (PIDLs), which is how Windows itself decides whether two folders are the same.

## One window, many tabs

The mechanism is a port of w4po/ExplorerTabUtility's watcher, because it is the one that
holds up on Windows 11: WinExplorer does not try to prevent Explorer from creating a window.

- Every Explorer tab announces itself in the shell's `ShellWindows` collection, and the
  collection raises **`WindowRegistered`** the moment a new one appears. That event — not a
  mouse hook, not polling — is what drives the app.
- In parallel, a `EVENT_OBJECT_CREATE` hook cloaks a brand-new `CabinetWClass` window as
  soon as it is created; `EVENT_OBJECT_SHOW` retries the hide if the frame was not ready
  yet. This keeps a window that is about to become a tab from flashing on screen.
- When the registered window turns out to be a new single-tab window (and not just a new
  tab in an existing window), its folder is looked up among the tracked tabs. If a tab
  already shows it, that tab is selected; otherwise the main window gets a new tab
  (`Ctrl+T` via `WM_COMMAND`) which is navigated to the folder. Then the surplus window is
  quit. If anything goes wrong, the window is simply made visible again — nothing is ever
  left invisible and no folder is lost.
- New windows are folded one at a time, in arrival order, so two windows appearing together
  cannot race into two tabs for the same folder.

Home, This PC, Desktop and Downloads are special: they always sit at the left of the strip,
in that fixed order, ahead of normal folders. Explorer can only *append* tabs, so the app
moves folders, not tabs: the leading tabs shift one place to the right (at most four
navigations — the special group is never larger) and the special folder lands in the tab at
its position. Switching this off is a checkbox in the settings window (*Keep Home, This PC,
Desktop and Downloads at the left*).

Two gestures always beat the single-window rule and produce a separate window that
WinExplorer leaves alone from then on:

- hold **Ctrl+Shift** while the folder opens;
- **drag a tab out** of a window — the folder shows up in the closed-tabs history the moment
  the tab leaves its old window, which is how the new window is recognised as deliberate.

Windows that were already open when WinExplorer starts are tracked (so duplicate detection
sees their tabs) but never folded behind your back; **Collect windows into one** in the tray
menu (or `--single-window`) folds them on demand.

All COM work — the `ShellWindows` collection, the event sinks, reading locations, `Navigate2`
— runs on one dedicated STA thread that pumps messages while it waits; without that,
Explorer's automation answers with nothing at all. A shell call that never returns is
abandoned after a couple of seconds instead of freezing the app, and a health check
reconnects everything when Explorer itself restarts.

## Requirements

- Windows 11 **22H2 or newer** — tabs in File Explorer are required. On Windows 10 the app
  still runs (context menu, shortcuts, settings) but folders open in windows.
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or use the
  self-contained build, which needs nothing installed).
- No administrator rights. Everything is stored in your own user profile.

## Install

WinExplorer installs per user: no administrator rights are needed and nothing outside your
own profile is changed.

### The simple way: double-click `Install.cmd`

1. Download a release zip:
   - `WinExplorer-<version>-win-x64-selfcontained.zip` — needs nothing else installed, or
   - `WinExplorer-<version>-win-x64.zip` — smaller, needs the
     [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
     If no release has been published yet, take a build from **Actions → build → latest
     run → Artifacts → WinExplorer** instead (it has the same zip layout), or run
     `Install.cmd` from the source code and let it build.
2. Unpack it anywhere.
3. Double-click **`Install.cmd`** in the unpacked folder.
4. Answer the two questions (Enter accepts the default):
   - **Start WinExplorer automatically when you sign in?** — `Y` adds the per-user startup
     entry, `n` leaves it out.
   - **Integrate with File Explorer (right-click menu and folders open as tabs)?** — `Y`
     registers the right-click entries and makes WinExplorer the per-user handler for
     folder double-clicks. Both can be switched off again separately in the settings
     window after installing.

That is the whole installation. The script copies the app to
`%LOCALAPPDATA%\Programs\WinExplorer`, creates Start Menu shortcuts, registers it in
**Settings → Apps → Installed apps** so the normal Windows uninstaller can remove it, and
starts WinExplorer. `Install.cmd` and `Uninstall.cmd` are copied along with the app, so you
can repair or uninstall later without keeping the original zip.

Run from a source checkout (GitHub's *Download ZIP* has no compiled program in it),
`Install.cmd` picks the program up by itself:

1. an existing build in the folder (`publish\`, `artifacts\`, `bin\`) — used as-is;
2. otherwise, with the .NET 8 SDK installed, the app is built from the sources;
3. otherwise the latest pre-built release is downloaded from GitHub (about 100 MB,
   `-NoDownload` turns this off);
4. otherwise the installer offers to install the .NET 8 SDK with `winget` and then builds.

If none of that is possible it prints exactly what to download or install. `-Source <folder>`
always works when you already have a folder containing `WinExplorer.exe`.

#### Installer options

Anything after `Install.cmd` is passed on to `scripts\install-app.ps1`:

| Command | Result |
|---|---|
| `Install.cmd -NoStartup` | install without the startup entry |
| `Install.cmd -Startup -ContextMenu` | add both integration points, without asking |
| `Install.cmd -Quiet -NoLaunch` | unattended install with the defaults |
| `Install.cmd -Destination D:\Apps\WinExplorer` | install into a different folder |
| `Install.cmd -Source <folder>` | install from another folder that holds `WinExplorer.exe` |
| `Install.cmd -NoDownload` | never download a release; build from source instead |
| `Install.cmd -InstallSdk` | allow `winget` to install the .NET 8 SDK unattended, then build |

`-Quiet` never asks; startup and the File Explorer integration are both enabled unless you
add `-NoStartup` or `-NoContextMenu`.

### Setup wizard

`WinExplorer-<version>-setup.exe` (built by CI and attached to every release) is an
optional wizard made with [Inno Setup](https://jrsoftware.org/isdl.php) 6. It asks the same
two questions as checkboxes and registers its own per-user uninstaller in
**Settings → Apps**. To build it from source:

```powershell
.\scripts\make-installer.ps1                 # self-contained payload (no .NET needed)
.\scripts\make-installer.ps1 -SelfContained:$false
```

The result is `installer\Output\WinExplorer-<version>-setup.exe`.

### From source or manually

```powershell
git clone https://github.com/gghofsu/WinExplorer
cd WinExplorer
.\Install.cmd                                # build, install, register, start
```

Or do it by hand:

```powershell
dotnet publish src\WinExplorer\WinExplorer.csproj -c Release -r win-x64 --self-contained false -o .\publish
.\publish\WinExplorer.exe
```

Helper scripts:

```powershell
.\scripts\build.ps1                  # build + run the unit tests
.\scripts\publish.ps1                # framework-dependent zip in .\artifacts
.\scripts\publish.ps1 -SelfContained # no runtime needed, larger zip
.\scripts\install.ps1                # publish to %LOCALAPPDATA%\Programs\WinExplorer, register, start
.\scripts\install.ps1 -NoBuild       # only register an existing install
```

`install.ps1` is the developer helper (it publishes first); `Install.cmd` +
`scripts\install-app.ps1` is the end-user installer described above. Both write only
per-user state.

### Start with Windows

The startup entry can be added or removed at any time, without reinstalling:

- **Settings window** → *Start WinExplorer when I sign in*;
- **Tray menu** → *Start with Windows* (checkmark shows the current state);
- **Command line**: `WinExplorer.exe --install-startup`, `--uninstall-startup` or
  `--toggle-startup`.

## Using it

- **Start it once** (or let it start with Windows). It lives in the notification area.
- **Open folders** as usual — from the desktop, Start, the taskbar, Win+E, anywhere. Each
  one appears as a tab in the Explorer window you already have (or switches to its tab).
- **Hold Ctrl+Shift** while a folder opens to get a separate window.
- **Right-click a folder** → *Open in WinExplorer tab* to send it to the tabbed window
  explicitly (useful while the single-window switch is off).
- **Duplicate** the current tab with `Alt+Shift+D`, **reopen** the last closed folder with
  `Alt+Shift+T`, **pause/resume** WinExplorer with `Ctrl+Alt+P`.
- Closing the settings window keeps WinExplorer running in the tray (configurable). Use
  *Exit WinExplorer* in the tray menu to stop it completely.
- Tray menu → **Start with Windows** adds or removes the startup entry with one click.
- Tray menu → **Restart WinExplorer** replaces the running copy, which is the way out of a
  hung instance.

## Settings

All settings are stored in `%APPDATA%\WinExplorer\settings.json` and applied immediately —
there is no Save button.

| Setting | Meaning |
|---|---|
| **Keep a single File Explorer window** | The main switch: every new Explorer window is reopened as a tab of the window you already have. Hold Ctrl+Shift while a folder opens to get a real window. |
| **When the folder is already open** | *Switch to the tab that already shows it* (default) or *Open another tab anyway*. |
| **Keep Home, This PC, Desktop and Downloads at the left** | The fixed order described above. |
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
WinExplorer.exe --install-startup | --uninstall-startup | --toggle-startup
WinExplorer.exe --single-window         Fold every Explorer window into one tabbed window
WinExplorer.exe --restart                Restart the helper (recovers a hung copy)
WinExplorer.exe --diagnose               Write a diagnostics report and open it
WinExplorer.exe --probe                  List every window, tab and folder the app can see
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
  and the header of the settings window), the folder is on the exclusion list, Ctrl+Shift
  was held, or the location is not a folder at all (Control Panel, search results, an
  archive, a phone). There was also no other Explorer window to fold into — the very first
  window always stays a window.
- **Two windows remain and never merge** — the second one was opened with Ctrl+Shift or by
  dragging a tab out, so it is deliberate; WinExplorer folds *new* windows, not ones you
  made on purpose. Tray menu → **Collect windows into one** (or `--single-window`) merges
  everything on demand.
- **A window flashes before it becomes a tab** — Windows painted it before the hook could
  hide it (heavy load, or the window appeared during the first seconds after startup, when
  WinExplorer deliberately keeps its hands off restored session windows).
- **Dedupe misses a folder** — run `--probe`: it lists every window and tab with the
  location the shell reports for it. A tab whose line shows no location cannot be matched;
  the log records what happened instead.
- **Nothing is folded at all** — look for `Explorer did not create a tab` in the log. Windows
  11 22H2+ is required; on older builds the app cannot create tabs and every folder falls
  back to a window (which is also what the tab lookups need). `--restart` reloads the helper
  after an Explorer update.
- **Tabs do not work at all** — tabs need Windows 11 22H2+. On older builds folders fall
  back to windows and the log says so.
- **`Install.cmd` says "No WinExplorer.exe was found"** — you are in the source code ZIP,
  which has no compiled program. Let the installer download a build (answer *yes* to the
  download question), install the .NET 8 SDK so it can build one, or run it from an unpacked
  release zip / CI artifact.
- **"WinExplorer is already running but is not responding"** — a copy is wedged (a hook or
  the tray icon failed, or the window never appeared). Answer **Yes** to the restart prompt:
  the stuck copy is stopped and a fresh one takes over with your settings. The same thing is
  available as tray menu → *Restart WinExplorer* and as `WinExplorer.exe --restart`.
- **Something is off and the log is not enough** — run `WinExplorer.exe --diagnose`. It
  writes `%APPDATA%\WinExplorer\WinExplorer-diagnose.txt` and opens it: version and paths,
  whether the single-instance mutex is held, whether a running copy answers on the IPC pipe
  (and how fast), every WinExplorer process with its start time, the active settings and the
  last 80 log lines.
- **Nothing happens at all** — make sure only one WinExplorer is running (tray icon), then
  check `%APPDATA%\WinExplorer\WinExplorer.log` (tray menu → *Open log file*). The log
  records every tab command and every fallback with timestamps, and now also each startup
  stage, so the last line tells you where startup stopped.
- **Explorer restarts** — WinExplorer re-attaches automatically; the log notes it.
- **Undoing the integration** — switch the two Windows-integration checkboxes off, or run
  `WinExplorer.exe --uninstall-context-menu`.

## Known limits

- File Explorer's tab commands (`WM_COMMAND 0xA21B` / `0xA221` / `0xA021`) are undocumented
  internals. Microsoft can change them in any update. WinExplorer verifies every step, falls
  back to a normal window and logs what happened instead of silently failing.
- Folders inside archives, search results, libraries and other virtual locations keep
  Explorer's own behaviour; Control Panel windows are never folded.
- Double-clicking a folder inside an Explorer window is Explorer's own navigation (the
  current tab navigates). WinExplorer only converts folders that would open a *new window*.
- Windows may show the "Open with" style chooser the first time a verb is used, depending on
  your shell settings; choose WinExplorer and tick *Always*.

## Uninstall

WinExplorer registers itself with Windows, so the ordinary Windows uninstaller works:

- **Settings → Apps → Installed apps → WinExplorer → Uninstall**, or
- **Start Menu → WinExplorer → Uninstall WinExplorer**, or
- double-click **`Uninstall.cmd`** in `%LOCALAPPDATA%\Programs\WinExplorer`.

The uninstaller stops WinExplorer and removes the startup entry, the folder right-click
menu, the default folder handler (restoring the one that was there before), the Start Menu
shortcuts, the entry in **Installed apps** and the application folder. It then asks whether
to delete `%APPDATA%\WinExplorer` (settings and log) and keeps it by default, so a later
reinstall keeps your configuration.

Unattended uninstall:

```powershell
Uninstall.cmd -Quiet -KeepSettings     # remove the app, keep settings
Uninstall.cmd -Quiet -RemoveSettings   # remove everything
```

If you installed with `WinExplorer-<version>-setup.exe`, its uninstaller does the same and
is listed in **Settings → Apps** too.

The individual integration points can also be removed from the command line:
`WinExplorer.exe --uninstall-context-menu` and `WinExplorer.exe --uninstall-startup`. They
only touch `HKCU` and restore the folder handler that was in place before WinExplorer was
installed.

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
Install.cmd        double-click installer (runs scripts\install-app.ps1)
Uninstall.cmd      double-click uninstaller (also registered in Settings > Apps)
src/WinExplorer/
  Core/            settings, command line, shortcuts, path normalisation (no Win32)
  Interop/         P/Invoke declarations, IWebBrowser2/ShellWindows + IShellFolder interfaces
  Shell/           the watcher engine, Explorer windows/tabs, PIDL comparison
  Hooks/           low-level keyboard hook (shortcuts, Win+E)
  Services/        policy (open/switch), IPC, registry, tray, logging
  UI/              WPF settings window
tests/             xUnit tests for the core
scripts/           build, publish, install (install-app.ps1), uninstall
                   (uninstall-app.ps1) and installer helpers
installer/         Inno Setup script (per-user setup executable)
docs/              architecture and manual test checklist
assets/            icon artwork (generated by tools/make-icon.py)
```

## License

MIT — see [LICENSE](LICENSE).
