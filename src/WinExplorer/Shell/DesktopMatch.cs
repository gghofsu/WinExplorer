namespace WinExplorer.Shell;

/// <summary>
/// Decides whether a window is a valid duplicate-check or merge candidate for a request
/// that is anchored to a specific virtual desktop.
/// </summary>
/// <remarks>
/// Two windows that belong to the same <c>CabinetWClass</c> frame are, by definition, on
/// the same desktop — that needs no COM at all, which matters because the live desktop
/// queries can fail or return nothing for a moment (a window being created or closing, a
/// wedged shell). A failed live query falls back to the desktop id captured when the tab
/// registered; when neither probe answers, the candidate is rejected (fail closed) rather
/// than trusted. Current-desktop scopes additionally require the live
/// <c>IsOnCurrentDesktop</c> probe to succeed — Windows will not move a window across
/// desktops by itself, but it can be wrong for one call, so ambiguity means "no".
/// </remarks>
internal static class DesktopMatch
{
    /// <param name="scope">The desktop the request is anchored to.</param>
    /// <param name="frame">The frame of the candidate window/tab.</param>
    /// <param name="sameWindowFrame">The frame the request originates from (0 when none).</param>
    /// <param name="liveDesktopId">Fresh desktop id from the shell (null when unknown).</param>
    /// <param name="cachedDesktopId">Desktop id captured when the candidate registered (null when never learned).</param>
    /// <param name="liveOnCurrentDesktop">Live "is on the current desktop" probe; only consulted for current-desktop scopes.</param>
    public static bool IsInScope(ExplorerDesktopScope scope, nint frame, nint sameWindowFrame,
        Guid? liveDesktopId, Guid? cachedDesktopId, bool liveOnCurrentDesktop)
    {
        // One frame is one desktop: no COM call can answer this better.
        if (frame != 0 && frame == sameWindowFrame)
            return true;

        if (scope.Id is not Guid id || id == Guid.Empty)
            return false;

        var effective = liveDesktopId ?? cachedDesktopId;
        if (effective is not Guid candidate || candidate != id)
            return false;

        if (scope.CurrentDesktopOnly && !liveOnCurrentDesktop)
            return false;

        return true;
    }
}
