namespace WinExplorer.Core;

/// <summary>A folder that was open in a tab and is not any more.</summary>
public sealed record ClosedTab(string Path, DateTimeOffset ClosedAt, string? DisplayName = null,
    Guid? DesktopId = null);

/// <summary>
/// A bounded, de-duplicated stack of recently closed folders. Feeds the
/// "reopen the last closed tab" command. Folder identity includes the virtual desktop;
/// desktop-scoped queries leave other desktops' entries untouched. Thread safe.
/// </summary>
public sealed class TabHistory
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly LinkedList<ClosedTab> _items = new();

    public TabHistory(int capacity = 25) => _capacity = Math.Max(1, capacity);

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>Records a folder as closed (most recent first).</summary>
    public void RecordClosed(string? path, DateTimeOffset? at = null, Guid? desktopId = null)
    {
        var normalized = PathNormalizer.Normalize(path);
        if (string.IsNullOrEmpty(normalized))
            return;

        lock (_gate)
        {
            // Drop an older entry for the same folder on the same desktop so "reopen"
            // resolves to the newest record without clearing another desktop's history.
            for (var node = _items.First; node is not null; node = node.Next)
            {
                if (node.Value.DesktopId == desktopId &&
                    string.Equals(node.Value.Path, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _items.Remove(node);
                    break;
                }
            }

            _items.AddFirst(new ClosedTab(normalized, at ?? DateTimeOffset.Now,
                PathNormalizer.ToDisplayName(normalized), desktopId));

            while (_items.Count > _capacity)
                _items.RemoveLast();
        }
    }

    /// <summary>Removes and returns the most recently closed folder, optionally on one desktop.</summary>
    public ClosedTab? TakeMostRecent(Guid? desktopId = null)
    {
        lock (_gate)
        {
            var node = _items.First;
            while (node is not null && desktopId is not null && node.Value.DesktopId != desktopId)
                node = node.Next;
            if (node is null)
                return null;

            var item = node.Value;
            _items.Remove(node);
            return item;
        }
    }

    /// <summary>
    /// True when the folder was recorded as closed within the last
    /// <paramref name="maxAgeMs"/> milliseconds. Used to recognise a tab the user dragged
    /// out of a window: closing the tab and the new window appear within moments of each
    /// other, and that window must stay a window.
    /// </summary>
    public bool WasClosedRecently(string? path, int maxAgeMs, Guid? desktopId = null)
    {
        var normalized = PathNormalizer.Normalize(path);
        if (string.IsNullOrEmpty(normalized))
            return false;

        var cutoff = DateTimeOffset.Now.AddMilliseconds(-maxAgeMs);
        lock (_gate)
        {
            foreach (var item in _items)
            {
                if (item.ClosedAt < cutoff)
                    break;

                if ((desktopId is null || item.DesktopId == desktopId) &&
                    string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>Removes a folder from the history (e.g. it was reopened by the user).</summary>
    public void Forget(string? path, Guid? desktopId = null)
    {
        var normalized = PathNormalizer.Normalize(path);
        if (string.IsNullOrEmpty(normalized))
            return;

        lock (_gate)
        {
            for (var node = _items.First; node is not null; node = node.Next)
            {
                if ((desktopId is null || node.Value.DesktopId == desktopId) &&
                    string.Equals(node.Value.Path, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _items.Remove(node);
                    return;
                }
            }
        }
    }

    public IReadOnlyList<ClosedTab> Snapshot()
    {
        lock (_gate)
            return _items.ToArray();
    }

    public void Clear()
    {
        lock (_gate)
            _items.Clear();
    }
}
