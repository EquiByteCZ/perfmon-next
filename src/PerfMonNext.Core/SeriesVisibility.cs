namespace PerfMonNext.Core;

/// <summary>Plans bulk visibility changes for series rows, keyed by series override key.</summary>
public static class SeriesVisibility
{
    /// <summary>Sets every selected key to <paramref name="visible"/>; other keys are untouched.</summary>
    public static IReadOnlyDictionary<string, bool> Set(IEnumerable<string> selected, bool visible)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in selected)
        {
            if (!string.IsNullOrEmpty(key))
                result[key] = visible;
        }

        return result;
    }

    /// <summary>
    /// Space-bar toggle: if any selected series is visible, hide them all; otherwise show them all.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> Toggle(IEnumerable<(string Key, bool Visible)> selected)
    {
        var items = selected.Where(item => !string.IsNullOrEmpty(item.Key)).ToList();
        var target = !items.Any(item => item.Visible);
        return Set(items.Select(item => item.Key), target);
    }

    /// <summary>Shows the selected keys and hides every other key in <paramref name="all"/>.</summary>
    public static IReadOnlyDictionary<string, bool> ShowOnly(IEnumerable<string> all, IEnumerable<string> selected)
    {
        var keep = new HashSet<string>(selected.Where(key => !string.IsNullOrEmpty(key)), StringComparer.Ordinal);
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (keep.Count == 0)
            return result;
        foreach (var key in all)
        {
            if (!string.IsNullOrEmpty(key))
                result[key] = keep.Contains(key);
        }

        foreach (var key in keep)
            result[key] = true;
        return result;
    }
}
