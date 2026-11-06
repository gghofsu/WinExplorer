# Testing WinExplorer

The unit tests (`dotnet test`) cover the parts that do not need Explorer. Everything that
touches tabs has to be verified on a real Windows 11 machine; this is the checklist used
before a release.

## Automated

```powershell
dotnet test WinExplorer.sln -c Release
```

| Area | Covered by |
|---|---|
| Path normalisation (URLs, slashes, `%VARS%`, shell GUIDs) | `PathNormalizerTests` |
| Shortcut parsing / rendering / conflict checks | `HotkeySpecTests` |
| Command line parsing (context menu verbs, switches, errors) | `CommandLineTests` |
| Closed-tab history (order, capacity, de-duplication, desktop isolation) | `TabHistoryTests` |
| Per-desktop main windows, unknown desktop IDs, preferred handles and moved/closed windows | `ExplorerWindowRouterTests` |
| Forced new Explorer frames (including quoted, UNC and shell paths) | `ExplorerLaunchTests` |
| Activate → confirm selection → refresh ordering (including `--no-activate`) | `TabSwitcherTests` |
| Actual shell view refresh, wrong-tab protection and failed/unavailable COM services | `ShellViewRefresherTests` |
| Settings round-trip, corrupt files, sanitising | `SettingsStoreTests` |
| Folder name → path resolution (incl. `..` escapes, files, virtual folders) | `FolderResolutionTests` |

## Manual checklist (Windows 11 22H2+)

Preparation: start `WinExplorer.exe`, make sure the header says **Active**, and have
`%APPDATA%\WinExplorer\WinExplorer.log` open.

### Opening

- [ ] Double-click a folder in Explorer → a **new tab** appears in the same window and is
      focused; no new window flashes.
- [ ] Open four folders from the desktop → the tabs appear **in the order opened**, each new
      one added at the end of the strip.
- [ ] Open **Home** from Start with an Explorer window already open → it becomes a **tab** in
      that window (no second window), and it is the **leftmost** tab.
- [ ] Then open **This PC**, **Desktop** and **Downloads** (from Start / the desktop) → the
      strip reads Home, This PC, Desktop, Downloads, then the normal folders.
- [ ] Open a normal folder after them → it goes to the right of Downloads.
- [ ] Open *Downloads* first, then *Home* → Home still ends up leftmost.
- [ ] Settings → *Keep Home, This PC, Desktop and Downloads at the left* off → folders are
      simply appended again.
- [ ] Open a folder that is already open (desktop, Start, another app) → its tab is brought
      to the front **and refreshed (F5)**, **no second tab for it**, and no leftover window.
- [ ] Watch the strip while folders open: it must not hop through other tabs, and the tab you
      are looking at must not change its folder. Only the new tab may appear.
- [ ] Open the very first folder after starting WinExplorer with no window open → exactly one
      window, exactly one tab (never two windows of the same folder).
- [ ] With four or more tabs open, open **Home** → it is the first tab, and the folders of all
      other tabs are unchanged (the old first tab's folder is now on the second tab).
- [ ] Double-click the **same** folder again → that tab is brought to the front, the tab
      count does not change.
- [ ] Open folder `A` in window 1, then double-click `A` inside a *different* Explorer
      window → the first window comes forward with tab `A` selected.
- [ ] Hold `Ctrl` and double-click a folder → Windows opens a normal new window (no tab).
- [ ] Double-click a **file** → it opens normally (the click is replayed).
- [ ] Double-click empty space in the file list → nothing unusual happens.
- [ ] Middle-click a folder → a background tab appears, focus stays where it was.
- [ ] Double-click a folder in the **navigation pane** → unchanged Windows behaviour.
- [ ] Double-click `This PC`, `Recycle Bin`, a library → Windows behaviour.
- [ ] Double-click a folder inside an archive (`zip:`) → Windows behaviour.
- [ ] Double-click a folder on the **desktop** → with the default-handler option on: tab in
      the existing window. Off: normal window (or folded in, if window folding is on).

### A window is never left invisible

- [ ] Open a folder that is on the exclusion list (Settings → excluded folders) → its window
      appears normally, stays visible, and is not folded in.
- [ ] Ctrl+Shift+double-click a folder, and right-click → *Open in new window* → separate
      windows, both visible, still there after ten seconds.
- [ ] Drag a tab out of an Explorer window → the detached window stays visible and is not
      pulled back in.
- [ ] With one window open, open a folder from another app while the shell is busy → either a
      tab appears and no window is left over, or the window stays visible (never transparent).

### New tabs and duplicates

- [ ] Ctrl+T with Home not already open → a new Home tab is selected immediately.
- [ ] Ctrl+T with Home already open → no duplicate remains; the existing Home tab is
      selected (and refreshed).
- [ ] Middle-click a folder not already open → a new tab is selected immediately.
- [ ] Middle-click or choose *Open in new tab* for a folder already open → no duplicate
      remains; the existing folder tab is selected and refreshed (including across Explorer
      windows on the same virtual desktop).
- [ ] Open the same folder four or more times in a row via Quick Access → *Open in new
      tab* → still no duplicate remains each time, and the other tabs never change; the
      existing tab is selected and refreshed on every open.
- [ ] Middle-click a folder that is already open **while the shell is busy** (several
      middle-clicks in a row, or on a slow/network folder) → still no duplicate remains;
      the existing tab is selected once the folder has loaded.
- [ ] Change a folder on disk in another window, then open a folder that is already shown
      in a tab → the switched-to tab **refreshes** and shows the change (it is never left
      showing the listing from when it was last visited).
- [ ] `Alt+Shift+D` in Explorer → duplicate tab, as an explicit duplicate request.
- [ ] `WinExplorer.exe --open "C:\Temp" --new-tab` → duplicate tab, as an explicit request.
- [ ] Right-click a folder → *Open in new window* → a separate window, even with window
      folding enabled.
- [ ] `WinExplorer.exe --open "C:\Temp" --new-window` → new window.

### Reopening a folder refreshes its files

- [ ] Open a folder with files, switch to a different tab, then create/delete a file in the
      original folder using another application or PowerShell. Open that original folder
      again from the desktop, another app, and `WinExplorer.exe --open <folder>` → its
      existing tab is selected and the listing reflects the change without pressing F5.
- [ ] Repeat while the destination Explorer window is **minimized** → it is restored,
      the correct tab is selected, and files appear without a manual refresh.
- [ ] Repeat while the folder's tab is **already selected**, including a folder whose view
      was initially empty → the listing still refreshes on every repeated open.
- [ ] Repeat with `--no-activate` → the listing refreshes without bringing Explorer forward.

### Separate virtual desktops (Win+Tab)

- [ ] Create Desktop 1 and Desktop 2. Open folder A on Desktop 1, then switch to Desktop 2
      and open folder B from the desktop/Start/another app → a new, visible Explorer window
      opens on Desktop 2; Desktop 1's window and tabs are untouched.
- [ ] Open folder C on Desktop 2 → C joins B as a tab in Desktop 2's window. Switch back to
      Desktop 1 and open folder D → D joins A, not B/C. Repeat several times in both directions.
- [ ] Open the **same folder A** on Desktop 2 → A opens locally there, not in Desktop 1's
      window. Open A again on each desktop → only that desktop's A tab is selected/refreshed.
- [ ] Ctrl+T / middle-click / *Open in new tab* for A on Desktop 2, including a slow folder
      whose path arrives late → duplicate detection stays within Desktop 2 and does not
      select or close Desktop 1's A tab.
- [ ] Repeat external opens using Win+E takeover, `--new-tab`, and `--hwnd` with the handle
      of Desktop 1's window while on Desktop 2 → Desktop 1 is never chosen as the destination.
- [ ] Make a deliberate extra window on each desktop (Ctrl+Shift). On Desktop 2 use the tray
      collection action or `--single-window` → only Desktop 2's windows are consolidated.
- [ ] Move an Explorer window using Task View → subsequent opens use its **new** desktop;
      no stale main-window handle pulls a request to the old desktop.
- [ ] Close A on Desktop 1, immediately open A from another app on Desktop 2 → it merges
      normally there (not misidentified as a dragged-out tab from Desktop 1). The reopen
      shortcut on each desktop restores only that desktop's last closed folder.
- [ ] Start a slow folder open, switch desktops before it completes → no activation pulls
      you back to the other desktop, and source/destination windows stay on their desktops.

### Shortcuts and state

- [ ] `Alt+Shift+T` after closing a tab → the folder is reopened as a tab.
- [ ] `Ctrl+Alt+P` pauses (tray icon text changes, header says Paused); double-clicking a
      folder then behaves like plain Windows.
- [ ] `Ctrl+Alt+P` again resumes.
- [ ] Change a shortcut in the UI, press the new chord → it works; the old one no longer
      fires.
- [ ] Restart WinExplorer → settings and shortcuts survive.

### One window, many tabs

- [ ] With the default settings, open two folders in Explorer, then a folder from the
      desktop → there is still **one** window, with three tabs.
- [ ] Open a second window from Start / the taskbar / Win+E → within ~2 s it disappears and
      its folder is a tab of the first window.
- [ ] Start WinExplorer while two Explorer windows are already open → after a few seconds
      both are one window (the *Also fold windows that are already open* switch).
- [ ] **Shift+double-click** a folder → a real second window appears and **stays** a window,
      even after a minute and after opening more folders.
- [ ] Right-click a folder → *Open in new window* → same: a second window that stays.
- [ ] Right-click a folder → *Open in new window* on the **desktop** → a second window that
      stays a window.
- [ ] Right-click a **file**, then open a folder from the desktop → the folder becomes a tab
      in the one window (a file right-click must not pin anything).
- [ ] Right-click a folder, pick *Properties*, then open a folder from Start → it becomes a
      tab in the one window.
- [ ] With one window showing folder A, open folder B from the desktop → B is a tab, **A is
      still showing A** (not Home), and A is the tab in front again.
- [ ] Open three folders in a row → three tabs with three correct folders, no *Home* tab,
      and the window never flickers through Home.
- [ ] Open a folder while Explorer is busy (a large folder still loading) → if Windows draws
      a window it disappears into a tab within a couple of seconds, and the next folder opens
      as a tab (the tab retry recovers instead of giving up).
- [ ] In that kept window, double-click another folder → the tab opens in that window (not in
      the first one).
- [ ] Tray menu → *Collect windows into one* → the extra windows are folded in at once, the
      pinned one stays.
- [ ] Settings → *Keep a single File Explorer window* off → new windows stay windows again.
- [ ] `WinExplorer.exe --single-window` → one window is left, no crash with a second copy
      running.
- [ ] `WinExplorer.exe --probe` prints every window with each tab's folder, and no tab says
      `(no folder reported)`.
- [ ] Leave the settings window open for ten minutes and check the log: no
      `a shell call did not finish in time` lines (they would mean the shell thread is stuck).
- [ ] `--diagnose` shows a `Shell` section with `shell automation: cached, answered, N window(s)`.
- [ ] The same with *Keep virtual folders as windows* on: a second window showing only
      *Recycle Bin* is **not** closed.

### Window folding

- [ ] Enable *Fold other new Explorer windows into tabs*; open a folder from Start → it is
      folded into a tab without a visible window flash.
- [ ] Enable it; use *Open in new window* from the context menu → stays a window.
- [ ] Hold `Ctrl+Shift` while opening a window → stays a window.
- [ ] Detach a tab (drag it out) with folding enabled → the detached window stays.
- [ ] Sign out/in with folding enabled → restored session windows are not eaten.

### Integration and hygiene

- [ ] Toggle *Add "Open in WinExplorer tab"…* off → the verb disappears from the context
      menu and the previous default folder handler is restored.
- [ ] Toggle *Start WinExplorer when I sign in* → check
      `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` contains/does not contain
      `WinExplorer`.
- [ ] `WinExplorer.exe --exit` stops the tray icon.

### Packaging

- [ ] `scripts\build.ps1` builds the solution and runs the tests without warnings that
      matter (`unused variable`-class noise is acceptable, `CS` errors are not).
- [ ] `scripts\publish.ps1` produces `artifacts\WinExplorer-<version>-win-x64.zip`, and the
      unzipped `WinExplorer.exe` starts on a machine without the .NET runtime when built with
      `-SelfContained`.
- [ ] `scripts\install.ps1` copies the app to
      `%LOCALAPPDATA%\Programs\WinExplorer`, adds the context menu and the Run entry, and
      starts the helper.
- [ ] `scripts\make-installer.ps1` (or `ISCC.exe installer\WinExplorer.iss`) produces
      `installer\Output\WinExplorer-<version>-setup.exe`.
- [ ] The release zip contains `Install.cmd`, `Uninstall.cmd`, `scripts\install-app.ps1`
      and `scripts\uninstall-app.ps1`.
- [ ] Install from the setup with both integration tasks ticked → context menu and startup
      entry exist; the app starts.
- [ ] Uninstall from *Apps & features* → both integration tasks are undone, the previous
      folder handler is back (double-clicking a folder opens a normal window) and
      `%LOCALAPPDATA%\Programs\WinExplorer` is gone.
- [ ] Install over an existing user install (no reboot, helper running) → the installer asks
      Explorer to close the app, updates the files and leaves `%APPDATA%\WinExplorer` alone.
- [ ] Start WinExplorer a second time → the settings window of the running instance appears
      (no second tray icon).

### Double-click installer (`Install.cmd`)

- [ ] Unpack an up-to-date release zip in a fresh folder and double-click `Install.cmd`.
      The console shows the detected payload and version, then both questions.
- [ ] Answer **yes** to both → `%LOCALAPPDATA%\Programs\WinExplorer` exists, the Run entry
      exists, the folder right-click menu appears, a Start Menu folder with *WinExplorer*
      and *Uninstall WinExplorer* exists, and the app is running.
- [ ] *Settings → Apps → Installed apps* lists **WinExplorer** with its version and icon.
- [ ] Run `Install.cmd -NoStartup -NoLaunch` over the existing install while the app runs →
      the running app is stopped and restarted only when `-NoLaunch` is absent, the startup
      entry is removed, and the settings in `%APPDATA%\WinExplorer` are untouched.
- [ ] `Install.cmd -Quiet` asks nothing and finishes with exit code 0
      (`echo %ERRORLEVEL%`).
- [ ] Run `Install.cmd` from GitHub's *Download ZIP* of the source (no `WinExplorer.exe`,
      no .NET SDK):
  - with a published release → it offers to download the latest build and installs it;
  - with no published release → it says no release exists, then offers to install the
    .NET 8 SDK with `winget` and builds;
  - with neither (`-NoDownload`, no winget) → it prints the three/four options and exits 1
    instead of failing silently.
- [ ] `Install.cmd -NoDownload` in a source checkout with the .NET 8 SDK → builds and installs.
- [ ] `Install.cmd -Source <folder with WinExplorer.exe>` → installs from that folder.
- [ ] After installing from a downloaded release, `WinExplorer.exe` starts without a
      SmartScreen prompt (the mark of the web is stripped from the install folder).
- [ ] After the install, delete the original zip → `Uninstall.cmd` still works.

### Uninstaller (`Uninstall.cmd` and Windows)

- [ ] Tray menu → *Start with Windows* toggles the Run entry (checkmark follows); while the
      app is running, the settings window checkbox shows the same state.
- [ ] `WinExplorer.exe --toggle-startup` flips it from a console, and
      `--install-startup` / `--uninstall-startup` set it explicitly.
- [ ] Double-click the Start Menu **Uninstall WinExplorer** shortcut → confirmation, then the
      app stops, the context menu disappears, the previous folder handler is restored, the
      startup entry is gone, both shortcuts are gone and the app folder is deleted.
- [ ] *Settings → Apps → Installed apps → WinExplorer → Uninstall* does the same (this uses
      the registry `UninstallString`; no console window stays open).
- [ ] Answer **yes** to the settings question → `%APPDATA%\WinExplorer` is gone. Answer
      **no** → settings survive, and reinstalling keeps them.
- [ ] `Uninstall.cmd -Quiet -RemoveSettings` runs without a prompt and removes everything
      (exit code 0).
- [ ] Running `Uninstall.cmd` twice does not fail; the second run says WinExplorer is not
      installed.
- [ ] Restart Explorer (`explorer.exe` kill) → WinExplorer keeps running and tabs work again
      afterwards.

### Recovery and diagnostics

- [ ] Start WinExplorer, then start it again → the existing window comes forward, no second
      tray icon, no error dialog.
- [ ] Freeze the helper (Task Manager → *Suspend* on WinExplorer.exe) and start the app
      again → the dialog offers to restart; answering **Yes** stops the frozen copy, starts
      a fresh one and the settings window opens. The log records the takeover.
- [ ] Same with *Suspend*: tray menu → *Restart WinExplorer* recovers it.
- [ ] `WinExplorer.exe --restart` from a console recovers a suspended copy.
- [ ] `WinExplorer.exe --exit` while the helper is suspended returns within a few seconds
      (it must not hang behind a dialog).
- [ ] Running `Install.cmd` or `Uninstall.cmd` while the helper is suspended completes; the
      installer force-stops the copy after a few seconds instead of waiting forever.
- [ ] `WinExplorer.exe --diagnose` writes `%APPDATA%\WinExplorer\WinExplorer-diagnose.txt`,
      opens it, and the report shows the mutex owner, the pipe answer (or the reason it did
      not answer), the running processes and the log tail.
- [ ] Kill the helper's process while it holds the mutex → starting the app again works
      (takeover path, "no other WinExplorer process is alive").
- [ ] Simulate a helper failure: rename `%APPDATA%\WinExplorer` to a read-only folder and
      start the app → it reports that it could not start and does not stay running hidden
      (the next start is clean).

### Edge cases

- [ ] A folder whose name contains `#`, `%`, `&`, or non-ASCII characters.
- [ ] A very deep path (> 260 characters) with long paths enabled.
- [ ] A junction pointing at another folder → opening either path switches to the same tab.
- [ ] Open 15 tabs, then switch to tab 12 with `WinExplorer` (duplicate test) → the right tab
      is selected and Explorer does not crash.
- [ ] Pause, exit WinExplorer, click a folder with the context verb → the app starts and
      opens the folder.
