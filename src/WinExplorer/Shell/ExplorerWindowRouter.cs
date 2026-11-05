namespace WinExplorer.Shell;

/// <summary>A desktop captured before an open request waits for the serial open queue.</summary>
internal readonly record struct ExplorerDesktopScope(Guid? Id, bool CurrentDesktopOnly = false)
{
    public bool Contains(Guid? windowDesktopId, bool isOnCurrentDesktop = true) =>
        Id is Guid id && id != Guid.Empty && windowDesktopId == id &&
        (!CurrentDesktopOnly || isOnCurrentDesktop);
}

/// <summary>One live Explorer frame, in front-to-back z-order in a routing snapshot.</summary>
internal readonly record struct ExplorerWindowInfo(
    nint Handle, Guid? DesktopId, int TabCount, bool IsOnCurrentDesktop = true);

/// <summary>
/// Remembers a separate main window for each desktop. Called only on the shell STA.
/// Every choice is checked against a fresh snapshot, so closing or moving a window in
/// Task View cannot leave a cached destination pointing at another desktop.
/// </summary>
internal sealed class ExplorerWindowRouter
{
    private readonly Dictionary<Guid, nint> _mainWindows = new();

    public nint GetMainWindow(ExplorerDesktopScope scope, IReadOnlyList<ExplorerWindowInfo> windows,
        nint excludeFrame = 0, nint preferred = 0)
    {
        foreach (var id in _mainWindows.Keys.ToList())
        {
            if (!windows.Any(w => w.Handle == _mainWindows[id] && w.DesktopId == id))
                _mainWindows.Remove(id);
        }

        if (scope.Id is not Guid desktopId || desktopId == Guid.Empty)
            return 0;

        var candidates = windows
            .Where(w => w.Handle != 0 && w.Handle != excludeFrame &&
                        scope.Contains(w.DesktopId, w.IsOnCurrentDesktop))
            .ToList();

        if (preferred != 0 && candidates.Any(w => w.Handle == preferred))
        {
            _mainWindows[desktopId] = preferred;
            return preferred;
        }

        if (_mainWindows.TryGetValue(desktopId, out var main) &&
            candidates.Any(w => w.Handle == main))
            return main;

        // Prefer the window the user has been assembling. Break tab-count ties with
        // the backmost window, as before, but only within this desktop.
        main = candidates.AsEnumerable().Reverse()
            .OrderByDescending(w => w.TabCount)
            .Select(w => w.Handle)
            .FirstOrDefault();

        if (main != 0)
            _mainWindows[desktopId] = main;
        return main;
    }

    public void Clear() => _mainWindows.Clear();
}
