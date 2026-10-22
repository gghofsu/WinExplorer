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
| **One window, many tabs** | While a File Explorer window is open, every folder becomes a tab in it — double-click, Start, the taskbar, Win+E, another app, and even windows that were already open. Only *Shift+double-click* or right-click → *Open in new window* creates a second window, and that window is never folded back. |
| **Duplicate / reopen shortcuts** | Global shortcuts to duplicate the current tab and to reopen the last closed folder, browser style. |
| **Win+E as a tab** *(optional)* | Turn Win+E into "open This PC (or Home) as a tab". |
| **One-click install and uninstall** | Double-click `Install.cmd` to install, `Uninstall.cmd` (or the entry Windows itself lists in *Settings → Apps*) to remove it. Startup is one checkbox — or one tray-menu click — either way. |
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
| Shift / Ctrl / Alt / Win + double-click | any | plain Windows behaviour: a real, separate window |
| Any of those windows, later | — | stays a window; it is never folded into a tab |

"Already open" is decided by asking the shell for the location of every tab in every
File Explorer window. String comparison is the fast path; paths that differ textually
(`C:\PROGRA~1`, junctions, redirected folders, `shell:::{GUID}` items) are compared through
shell item IDs (PIDLs), which is how Windows itself decides whether two folders are the same.

`Ctrl / Alt / Shift / Win` are the universal escape hatch: with any modifier held the click
is passed straight to Explorer, so the folder opens in its own window.

## One window, many tabs

The default is strict: **one File Explorer window**. Whatever makes a folder appear — a
double-click, the desktop, Start, the taskbar, Win+E, a shortcut in another app, a
`WinExplorer.exe --open` call — it lands as a tab in the window you already have. Windows
that appear anyway (a second window from an app, a window you opened before starting
WinExplorer, a *This PC* window with virtual folders allowed) are folded in within a second
or two: their tabs move over and the empty window is closed.

Two gestures always beat that rule and produce a separate window that WinExplorer leaves
alone from then on:

- **Shift+double-click** a folder (holding Ctrl, Alt or Win works too),
- right-click a folder → **Open in new window** — on the desktop or in any Explorer window.
  The folder that was right-clicked is remembered, so the window that shows up with exactly
  that folder is the deliberate one. A right-click on a *file*, on empty space or in another
  app does not stop anything.

The folder of a tab is read from the shell automation. The `ShellWindows` object is asked
first (through its typed interface); `Shell.Application` and the window's own address bar
(MSAA/UI Automation) are the fallbacks, because which engine answers depends on the Windows
build. Some builds report one entry *per window* instead of one per tab, and such an entry describes
the tab that is **showing**. Everything that navigates therefore makes the intended tab the
visible one first, verifies that it really is, and checks afterwards that the folder actually
arrived - if it did not, the new tab is closed again instead of being left on *Home*. Reading
the folder of a background tab only ever happens in a window you are not using. That call runs on
a dedicated STA thread that dispatches messages while it waits — without that, Explorer's
automation server answers with nothing at all, and no window could ever be compared with
another. When the automation server still refuses to say what a tab shows, the folder is read
straight off the tab's own address bar, and a shell call that never returns is abandoned after
a couple of seconds instead of freezing the app.

The pass that does this runs on its own timer (about once a second), independent of the tab
bookkeeping, and it repairs itself: if Explorer refuses to create a tab for a moment — which
happens while a folder is loading — WinExplorer retries, and it keeps trying in later passes
instead of giving up. If a tab cannot be created at all and the window has a single tab, the
folder is shown in that tab instead of in a new window. **Collect windows into one** in the tray menu performs a pass by hand,
even while the automatic switches are off.

Switched off: **Keep a single File Explorer window** in the settings window. Then the two
older switches apply again: **Also fold windows that are already open** and **Fold other new
Explorer windows into tabs**.

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
- **Double-click folders** as usual. They open as tabs.
- **Right-click a folder** → *Open in WinExplorer tab* to be explicit, *Open in new tab* to
  force a duplicate, *Open in new window* to get a window.
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
| **Double-clicking a folder opens it as a tab** | The main switch on the click side. |
| **Middle-clicking a folder opens it in a background tab** | De-duplicating middle-click. |
| **When the folder is already open** | *Switch to the tab that already shows it* (default) or *Open another tab anyway*. |
| **Look for an already open copy in every Explorer window** | Off = only the target window is searched for duplicates. |
| **Open new tabs in** | The window you are using, the last window you used, or the window with the most tabs. |
| **Bring the new tab to the front** | Off = tabs are created in the background. |
| **Keep a single File Explorer window** | The main switch: every folder becomes a tab in the one window, and extra windows are folded in. Shift+double-click / *Open in new window* still make real windows. |
| **Also fold windows that are already open** | Collects the windows that were open before WinExplorer started (or that you deliberately kept). |
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
  and the header of the settings window), the folder is on the exclusion list, a modifier
  key was held, or the folder is a virtual location (search results, an archive, a phone).
  With **Keep a single File Explorer window** on, such a window is folded in a moment later.
- **"Open in new window" opens a tab instead** — there is a window *and* a folder open
  somewhere, so the new window is folded in. Use `Shift+double-click` or turn **Keep a single
  File Explorer window** off.
- **Two windows remain and never merge** — one of them was opened with `Shift+double-click`
  or *Open in new window*, so it is pinned on purpose. Close it (or drag its tabs where you
  want them) and the next folder opens in the remaining window. The log says why a window was
  kept (`kept as a window: ...`); `--diagnose` shows the last lines.
- **A window appears and then turns into a tab** — that is the fold-in working: Windows had
  already drawn the window. `--single-window` or the tray item does the same thing by hand.
- **Nothing is folded and the log says "no folder could be read"** — the shell is not
  telling the app which folder a tab shows. Run `--probe`: it lists every window and tab
  *with* the folder the app managed to read, and for each tab what the three readers see
  (`MSAA`, `UIA`, `IESW` — the address bar, UI Automation, and the `ShellWindows` object).
  Whichever line has a path is the engine that works on that machine.
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
  Explorer's own behaviour.
- Because the double-click interception runs in a low-level mouse hook, a click that turns
  out not to be a folder is replayed with synthesized input. This is invisible in practice
  but is the reason the app needs the hook to stay responsive.
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
  Interop/         P/Invoke declarations, MSAA + IShellFolder interfaces
  Shell/           Explorer windows/tabs, tab commands, shell automation, PIDL comparison
  Hooks/           low-level mouse + keyboard hooks, MSAA hit testing
  Services/        policy (open/switch/merge), watcher, IPC, registry, tray, logging
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
