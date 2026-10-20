# How WinExplorer works

This is the technical companion to the README: what WinExplorer actually does to File
Explorer, which APIs it uses, and why the fallbacks are there.

Everything below refers to *Windows 11 22H2 and newer*, the first builds whose Explorer has
tabs. On older builds there is no `ShellTabWindowClass` child window and a tab cannot be
created; WinExplorer detects that and opens a window instead.

---

## 1. The moving parts

```
             mouse / keyboard                     other apps, desktop, Start
                     │                                        │
        ┌────────────▼────────────┐                ┌──────────▼─────────────┐
        │  MouseHook / KeyboardHook│                │  shell verb            │
        │  (low level, UI thread)  │                │  "open" (optional)     │
        └────────────┬────────────┘                └──────────┬─────────────┘
                     │                                        │  WinExplorer.exe --open "%1"
                     │  FolderClick                           │  (second process)
                     ▼                                        ▼
              ┌────────────────────────── ExplorerService (policy) ─────────────────────┐
              │  already open?  → switch          otherwise → new tab / new window      │
              └──────────────────────────────┬─────────────────────────────────────────┘
                                             │
                              ┌──────────────▼──────────────┐
                              │  TabController (mechanics)  │
                              │  WM_COMMAND + Shell.Application
                              └──────────────┬──────────────┘
                                             │
                                     File Explorer (explorer.exe)
```

- **One process owns the hooks.** A second `WinExplorer.exe` (started by the context menu, a
  shortcut or the command line) connects to the running helper over a named pipe and exits.
- **Two dedicated STA threads** (`StaExecutor`) do all shell COM work, so the UI thread never
  blocks on Explorer.

## 2. Creating a tab

Explorer has no documented API for "add a tab". It does, however, handle its own
accelerators through window messages, and the internal command ids are stable across builds
so far:

| Message | Sent to | Effect |
|---|---|---|
| `WM_COMMAND(0xA21B)` | the active `ShellTabWindowClass` window | same as **Ctrl+T** (new tab) |
| `WM_COMMAND(0xA221, n)` | the `CabinetWClass` window | same as **Ctrl+n** (select tab *n*) |
| `WM_COMMAND(0xA021)` | a `ShellTabWindowClass` window | same as **Ctrl+W** (close tab) |

So creating a tab is:

1. Snapshot the window's `ShellTabWindowClass` children.
2. `PostMessage(activeTab, WM_COMMAND, 0xA21B, 0)`.
3. Poll for a child window that was not in the snapshot (≤ 2.5 s). That handle *is* the new
   tab.
4. Navigate it (see below), verify, and select it.

If no tab appears, WinExplorer retries with a synchronous message, and finally opens a normal
window so the user's folder never disappears.

## 3. Navigating one specific tab

Explorer publishes every tab through the shell automation object: each entry of
`Shell.Application.Windows` has an `HWND` equal to the tab's `ShellTabWindowClass` handle
(on Windows 10 the entry belongs to the window instead). `ShellCom` matches an entry by HWND
and calls `Navigate2(path)`, then waits until the shell reports the expected location.

`Navigate2` accepts plain paths, `file:///` URLs and `shell:::{GUID}` namespace paths, which
is why This PC, Downloads and OneDrive also work.

Selection uses the `0xA221` command. Because the tab strip order is not the same as the
window's z-order, WinExplorer sends `Ctrl+1`, checks which tab became active, then `Ctrl+2`,
and so on until the target is in front — with a guard that never sends an index beyond the
current tab count (Explorer has a known crash when that happens while tabs are being created).

## 4. Detecting "this folder is already open"

The naive approach — compare the location strings — fails for:

- short (8.3) names: `C:\PROGRA~1` vs `C:\Program Files`
- junctions and symlinks
- redirected folders (`Documents` → OneDrive)
- namespace items (`shell:::{20D04FE0-...}` vs the localized display name)
- trailing separators, forward slashes, `%VARS%`, `file:///C:/My%20Folder`

`PathNormalizer` produces a canonical string first (fast path, covers 95 % of cases), and when
two strings still differ, `ShellPathComparer` compares them as shell item ids:

```
IShellFolder desktop = SHGetDesktopFolder()
pidl1 = desktop.ParseDisplayName(path1)
pidl2 = desktop.ParseDisplayName(path2)
desktop.CompareIDs(0, pidl1, pidl2) == 0   →  same folder
```

This is exactly how Explorer decides whether two paths are the same folder.

## 5. Intercepting double-clicks

A `WH_MOUSE_LL` hook sees every mouse event before any window does.

- The **first** `WM_LBUTTONDOWN` is passed through — selection, hover and keyboard focus all
  behave normally.
- A second down inside the system double-click time/distance is treated as a double-click.
  WinExplorer blocks that down (and its up) so Explorer never starts its own navigation.
- If the click was not inside a File Explorer tab, or was in the navigation pane, the hook
  passes it through untouched. The check is a pure window-hierarchy test plus one MSAA query
  (`AccessibleObjectFromPoint` → `accRole` / `accName`), which is exactly what screen readers
  use; file rows report themselves as list items whose name is the display name.
- The folder path is then `current tab location + display name`. The resolve step happens on
  a worker thread, never inside the hook.
- If the row turned out **not** to be a folder (a file, a shortcut, a virtual item), the
  click is replayed with `SendInput`. Injected events carry `LLKHF_INJECTED`, so the hook
  ignores them and Windows opens the file exactly as it would have.

Middle-click uses the same resolution; the new tab is created without activating it, which
matches Windows' "open in background tab" behaviour.

## 6. Folding windows into tabs (optional)

Windows still creates a window in several situations that cannot be intercepted: a folder
double-clicked on the desktop, opened from Start, from another application, or with Win+E.
WinExplorer listens for `EVENT_OBJECT_SHOW` on `CabinetWClass` windows, waits for the location
to become available, then

1. hides the new window (layered alpha 0) so it never flashes,
2. runs the normal open policy (switch to an existing tab if the folder is already open, else
   create a tab,
3. closes the window it consumed.

It deliberately leaves windows alone when

- the option is off, WinExplorer is paused, or the first 8 seconds after start (session
  restore),
- `Ctrl+Shift` is held at that moment,
- the right mouse button was released within the last 1.2 s — that almost always means
  "Open in new window" from the context menu,
- the window has more than one tab (the user assembled it, or dragged a tab out),
- the folder was closed/ detached within the last 2 s (re-opening a tab also creates a window),
- the location is on the exclusion list, or a virtual folder with that option enabled.

## 7. The optional default-handler integration

Explorer invokes the **default verb** of the `Directory` class when a folder is opened from
anywhere. WinExplorer writes, under `HKCU` (per user, no elevation):

```
HKCU\Software\Classes\Directory\shell\WinExplorer.OpenTab          label, icon
HKCU\Software\Classes\Directory\shell\WinExplorer.OpenTab\command  "WinExplorer.exe" --open "%1"
HKCU\Software\Classes\Directory\shell\WinExplorer.OpenWindow\…     "…" --open "%1" --new-window
HKCU\Software\Classes\Directory\Background\shell\…                 "%V" (the current folder)
HKCU\Software\Classes\Drive\shell\WinExplorer.OpenTab              drive roots
```

Making WinExplorer the default handler sets the default value of
`HKCU\Software\Classes\Directory\shell` to `WinExplorer.OpenTab`; the previous value is backed
up in `HKCU\Software\WinExplorer\Backup` and restored when the option is switched off.

The plain `open` verb itself is **not** modified: registering a separate default verb avoids
the `DelegateExecute` / `DropTarget` COM handlers that make naive `open\command` rewrites fail
on Windows 11.

Because this path ends in `WinExplorer.exe --open …`, the request arrives through the named
pipe at the already running helper — a double-click costs one process start, not a new helper.

## 8. Threading and lifetime

| Thread | Owns |
|---|---|
| WPF UI thread | low-level hooks (they need a message pump), tray icon, settings window |
| `WinExplorer.Shell.Query` (STA) | every shell query: tab locations, PIDL comparison, MSAA-friendly COM |
| `WinExplorer.Shell.Navigate` (STA) | `Navigate2` calls, which can block while a folder loads |
| Thread pool | merge workers, IPC connections, click resolution |

Tab operations are serialised by a semaphore: creating two tabs at the same time is the
fastest way to confuse Explorer.

## 9. Failure behaviour

Every step that depends on undocumented behaviour is verified:

- no new tab window appeared → retry synchronously → new window
- tab created but `Navigate2` refused → tab is still selected and the failure is logged
- location never confirmed → accepted if the shell reports *any* location (short names and
  junctions are legitimate differences), logged otherwise
- window fold failed halfway → the hidden window is made visible again

The log (`%APPDATA%\WinExplorer\WinExplorer.log`) contains one line per action, which makes
"why did that open a window?" answerable.
