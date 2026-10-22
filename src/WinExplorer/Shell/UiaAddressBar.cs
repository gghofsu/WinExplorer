using System.Windows.Automation;
using WinExplorer.Core;

namespace WinExplorer.Shell;

/// <summary>
/// Reads the folder a window shows through UI Automation: Explorer's address bar is an
/// <c>Edit</c>/<c>ComboBox</c> whose value is the full path.
/// </summary>
/// <remarks>
/// Third opinion, used when both automation objects stay silent. UIA talks to Explorer's
/// accessibility provider rather than its automation server, so it works in cases where
/// <c>Shell.Application</c> does not. Calls are not free, so results are cached briefly.
/// </remarks>
internal static class UiaAddressBar
{
    private static readonly Dictionary<nint, (long Tick, string Location)> Cache = new();
    private static readonly object Gate = new();

    /// <summary>The location shown by <paramref name="windowHwnd"/>, or null.</summary>
    public static string? TryRead(nint windowHwnd, int maxAgeMs = 1500)
    {
        if (windowHwnd == 0)
            return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(windowHwnd, out var cached) &&
                Environment.TickCount64 - cached.Tick < maxAgeMs)
            {
                return cached.Location.Length == 0 ? null : cached.Location;
            }
        }

        var location = Read(windowHwnd);
        lock (Gate)
        {
            Cache[windowHwnd] = (Environment.TickCount64, location ?? string.Empty);
            if (Cache.Count > 64)
            {
                foreach (var stale in Cache.Where(p => Environment.TickCount64 - p.Value.Tick > 30000)
                             .Select(p => p.Key).ToList())
                {
                    Cache.Remove(stale);
                }
            }
        }

        return location;
    }

    private static string? Read(nint windowHwnd)
    {
        try
        {
            var window = AutomationElement.FromHandle(windowHwnd);
            if (window is null)
                return null;

            // The address bar is an Edit (typing) or a ComboBox-style breadcrumb (browsing).
            foreach (var type in new[] { ControlType.Edit, ControlType.ComboBox, ControlType.ToolBar, ControlType.Text })
            {
                var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
                AutomationElementCollection found;
                try
                {
                    found = window.FindAll(TreeScope.Descendants, condition);
                }
                catch
                {
                    continue;
                }

                var count = Math.Min(found.Count, 24);
                for (var i = 0; i < count; i++)
                {
                    var location = FromElement(found[i]);
                    if (location is not null)
                        return location;
                }

                if (type == ControlType.Edit && found.Count > 0)
                    return null; // an edit was there but held no path: nothing better below
            }
        }
        catch
        {
            // UIA is best effort.
        }

        return null;
    }

    private static string? FromElement(AutomationElement element)
    {
        try
        {
            string? value = null;

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern valuePattern)
            {
                value = valuePattern.Current.Value;
            }

            if (string.IsNullOrWhiteSpace(value))
                value = element.Current.Name;

            return Accept(value);
        }
        catch
        {
            return null;
        }
    }

    private static string? Accept(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var candidate = text.Trim();
        var looksLikePath = candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
                            candidate.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
                            candidate.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
                            candidate.StartsWith("::", StringComparison.Ordinal) ||
                            (candidate.Length > 2 && char.IsLetter(candidate[0]) && candidate[1] == ':');

        if (!looksLikePath)
            return null;

        var normalized = PathNormalizer.Normalize(candidate);
        return normalized.Length == 0 ? null : normalized;
    }
}
