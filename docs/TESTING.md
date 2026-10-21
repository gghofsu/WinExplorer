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
| Closed-tab history (order, capacity, de-duplication) | `TabHistoryTests` |
| Settings round-trip, corrupt files, sanitising | `SettingsStoreTests` |
| Folder name → path resolution (incl. `..` escapes, files, virtual folders) | `FolderResolutionTests` |

## Manual checklist (Windows 11 22H2+)

Preparation: start `WinExplorer.exe`, make sure the header says **Active**, and have
`%APPDATA%\WinExplorer\WinExplorer.log` open.

### Opening

- [ ] Double-click a folder in Explorer → a **new tab** appears in the same window and is
      focused; no new window flashes.
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

### Duplicates on demand

- [ ] Right-click a folder → *Open in new tab* → always a new tab, even when it is already
      open (with WinExplorer's own verb and with Explorer's built-in item).
- [ ] Right-click a folder → *Open in new window* → a separate window, even with window
      folding enabled.
- [ ] `Alt+Shift+D` in Explorer → duplicate tab.
- [ ] `WinExplorer.exe --open "C:\Temp" --new-tab` → duplicate tab.
- [ ] `WinExplorer.exe --open "C:\Temp" --new-window` → new window.

### Shortcuts and state

- [ ] `Alt+Shift+T` after closing a tab → the folder is reopened as a tab.
- [ ] `Ctrl+Alt+P` pauses (tray icon text changes, header says Paused); double-clicking a
      folder then behaves like plain Windows.
- [ ] `Ctrl+Alt+P` again resumes.
- [ ] Change a shortcut in the UI, press the new chord → it works; the old one no longer
      fires.
- [ ] Restart WinExplorer → settings and shortcuts survive.

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
