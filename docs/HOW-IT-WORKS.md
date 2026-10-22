# How WinExplorer works

This is the technical companion to the README: what WinExplorer actually does to File
Explorer, which APIs it uses, and why the fallbacks are there. The engine is a hand-written
port of [w4po/ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility)'s watcher
(the COM interop is declared by hand because `tlbimp` type libraries are not available in a
cross-platform build).

Everything below refers to *Windows 11 22H2 and newer*, the first builds whose Explorer has
tabs. On older builds there is no `ShellTabWindowClass` child window and a tab cannot be
created; WinExplorer detects that and opens a window instead.

---

## 1. The moving parts

```
   desktop, Start, Win+E, other apps                  context menu / command line
                 │                                               │
                 │ Windows creates a new                         │  WinExplorer.exe --open "%1"
                 │ Explorer window                               │  (second process → named pipe)
                 ▼                                               ▼
   ┌──────────────────────────┐                   ┌─────────────────────────────┐
   │ ShellWindows              │                   │  ExplorerService (policy)   │
   │ WindowRegistered event    │──────────────────▶│  already open? → switch     │
   │ + EVENT_OBJECT_SHOW hook  │   fold request    │  otherwise → tab / window   │
   └──────────────────────────┘                   └──────────────┬──────────────┘
                                                                  │
                                                   ┌──────────────▼──────────────┐
                                                   │  ExplorerWatcher (engine)   │
                                                   │  WM_COMMAND + IWebBrowser2  │
                                                   └──────────────┬──────────────┘
                                                                  │
                                                        File Explorer (explorer.exe)
```

- **One process owns the engine.** A second `WinExplorer.exe` (started by the context menu,
  a shortcut or the command line) connects to the running helper over a named pipe and exits.
- **One dedicated, message-pumping STA thread** (`StaExecutor`) does all shell COM work:
  the `ShellWindows` collection, the event sinks, reading locations, `Navigate2`. Without a
  pump, Explorer's cross-apartment automation calls hang or return nothing.

## 2. Tracking tabs: `ShellWindows`

Every File Explorer tab announces itself in the shell's `ShellWindows` collection as an
`IWebBrowser2` object. The watcher subscribes to the collection's connection point
(`DShellWindowsEvents`), so **`WindowRegistered`** fires the instant a new tab or window
exists — no polling, no mouse hooks.

For each entry the watcher stores:

- the **frame** window: the entry's `HWND` property (`CabinetWClass`),
- the **tab** window: `IServiceProvider.QueryService(SID_STopLevelBrowser)` →
  `IShellBrowser.GetWindow()` (`ShellTabWindowClass`),
- the **location**: `LocationURL`, or `Document.Folder.Self.Path` for virtual folders
  (This PC, Recycle Bin, …) that report no URL,
- a `NavigateComplete2` / `OnQuit` event sink, which keeps the location fresh and records
  closed folders into the reopen history.

Already-seen entries are marked with a custom property (`PutProperty("WinExplorerSeen")`),
so enumerating the collection again never double-registers a tab. A 5-second health check
prunes dead entries, re-enumerates missed windows, and rebuilds everything when
`explorer.exe` itself restarts.

## 3. Folding a new window into a tab

Windows creates a window in every situation WinExplorer cares about: a folder opened from
the desktop, Start, the taskbar, Win+E, a file dialog, another application. The watcher
turns that window into a tab:

1. **Hide before paint.** An `EVENT_OBJECT_SHOW` win-event hook sees the new
   `CabinetWClass` window before it is drawn and makes it fully transparent (layered,
   alpha 0). If that fails, nothing breaks — the window just flashes once.
2. **`WindowRegistered` fires.** The watcher finds the unseen entry (polling the collection
   for up to 2.5 s), waits briefly for its location, and decides whether to fold. It folds
   only a *single-tab* window that is not the main window, when at least one other tab
   exists to fold into.
3. **Open as tab.** The normal open policy runs: if the folder is already shown in a
   tracked tab, that tab is selected; otherwise the main window gets a new tab which is
   navigated to the folder (section 4).
4. **Quit the surplus window** via its `IWebBrowser2.Quit()`. If anything failed, the
   hidden window is made visible again instead — nothing is ever lost or left invisible.

It deliberately leaves windows alone when:

- the app is paused or the single-window switch is off,
- the first 5 seconds after start (session restore),
- `Ctrl+Shift` is held at that moment (the explicit "I want a window" gesture),
- the location is unknown, Control Panel, or on the exclusion list,
- the window has more than one tab (the user assembled it),
- the folder appears in the closed-tabs history of the last 2 seconds — that is a tab the
  user just **dragged out** of a window, which must stay a window,
- there is no other window to fold into.

A safety net reveals any window that stayed hidden for more than ~4 seconds.

## 4. Creating and addressing tabs

Explorer has no documented API for "add a tab". It does, however, handle its own
accelerators through window messages, and the internal command ids are stable across builds
so far:

| Message | Sent to | Effect |
|---|---|---|
| `WM_COMMAND(0xA21B)` | a `ShellTabWindowClass` window | same as **Ctrl+T** (new tab) |
| `WM_COMMAND(0xA221, n)` | the `CabinetWClass` window | same as **Ctrl+n** (select tab *n*, 1-based) |
| `WM_COMMAND(0xA021)` | a `ShellTabWindowClass` window | same as **Ctrl+W** (close tab) |

Creating a tab:

1. Snapshot the window's `ShellTabWindowClass` children.
2. `PostMessage(tab, WM_COMMAND, 0xA21B)`.
3. Poll for a child window that was not in the snapshot (≤ 2 s) — that handle *is* the new
   tab. Explorer appends it at the end of the strip and selects it.
4. Wait for its `ShellWindows` entry (the `WindowRegistered` handler usually has it
   already), then `Navigate2(path)` on that entry. Paths containing `#` are passed as a
   `Shell.Application.NameSpace(path)` folder object because the URL form mangles them.

Selecting a specific tab uses `0xA221`: the window's child z-order only reveals which tab
is *active*, so the watcher selects position 0, 1, 2, … and watches the active tab change
until the right handle is in front — exactly ETU's approach.

If Explorer refuses to create a tab, the folder opens in a normal window so it never
disappears. All open/fold operations are serialised by one semaphore: two folders arriving
together cannot race into duplicates.

## 5. Detecting "this folder is already open"

The naive approach — compare the location strings — fails for:

- short (8.3) names: `C:\PROGRA~1` vs `C:\Program Files`
- junctions and symlinks
- redirected folders (`Documents` → OneDrive)
- namespace items (`shell:::{20D04FE0-...}` vs the localized display name)
- trailing separators, forward slashes, `%VARS%`, `file:///C:/My%20Folder`

`PathNormalizer` produces a canonical string first (fast path, covers 95 % of cases), and
when two strings still differ, `ShellPathComparer` compares them as shell item ids:

```
IShellFolder desktop = SHGetDesktopFolder()
pidl1 = desktop.ParseDisplayName(path1)
pidl2 = desktop.ParseDisplayName(path2)
desktop.CompareIDs(0, pidl1, pidl2) == 0   →  same folder
```

This is exactly how Explorer decides whether two paths are the same folder. Tabs that are
currently being folded are never used as a dedupe target, and a matched tab is validated
with `IsWindow` before it is selected.

## 6. Keeping Home, This PC, Desktop, Downloads at the left

Explorer can only *append* tabs, so the watcher moves **folders**, not tabs. When a special
folder (rank 0–3: Home, This PC, Desktop, Downloads) opens as a new tab:

1. Count `k` = tracked specials in that window with a smaller rank (the target position)
   and `m` = all tracked specials. Specials always occupy positions `0..m-1` because this
   code is the only thing that places them.
2. Resolve the tab handles at positions `k..m` by selecting each position once and watching
   the active tab change; every position must map to a tracked entry with a known folder,
   otherwise the whole move is abandoned and the tab simply stays at the end (nothing is
   ever lost for the sake of ordering).
3. Shift the leading folders one tab to the right — at most four navigations, since the
   special group is never larger — with the first normal folder (if any) moving into the
   new tab at the end of the strip.
4. Navigate the tab at position `k` to the special folder and select it.

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
pipe at the already running helper. (With the single-window switch on this integration is a
nicety, not a necessity — a window the handler did not catch is folded in anyway.)

## 8. Threading and lifetime

| Thread | Owns |
|---|---|
| WPF UI thread | keyboard hook (needs a message pump), tray icon, settings window |
| `WinExplorer.Shell` (STA, pumping) | `ShellWindows`, event sinks, locations, `Navigate2`, PIDL comparison |
| Thread pool | fold workers, IPC connections, the open policy |

The `WindowRegistered` handler leaves the COM event thread immediately (the shell is still
inside its own `Register` call) and does its work on the pool, funnelling every COM touch
back to the STA thread. Open/fold operations are serialised by a semaphore.

## 9. Failure behaviour

Every step that depends on undocumented behaviour is verified:

- no new tab window appeared → a normal window opens instead
- the new tab never registered with the shell → the tab is closed again, a window opens
- a fold failed halfway → the hidden window is made visible again
- a special-folder placement could not resolve every leading tab → the tab stays at the end
- the shell stops answering (Explorer restart) → the health check rebuilds the COM objects

The log (`%APPDATA%\WinExplorer\WinExplorer.log`) contains one line per action, which makes
"why did that open a window?" answerable. `WinExplorer.exe --probe` dumps every window, tab
and location the engine can see.
