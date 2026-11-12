namespace PerfMonNext.Core;

public readonly record struct LegendContext(bool SameMachine, bool SameCounter, bool SameInstance);

public static class ChartLegend
{
    public const int AutoHideAfter = 20;
    public const int MaxItems = 6;

    // The Legend checkbox is the force switch: when it is on, a compact capped legend
    // is shown even for large series counts (auto-hiding would fight the checkbox).
    public static bool ShouldShow(bool userWantsLegend, int seriesCount) =>
        userWantsLegend && seriesCount > 0;

    public static int Overflow(int seriesCount, bool userWantsLegend)
    {
        if (!userWantsLegend || seriesCount <= MaxItems)
            return 0;
        if (!ShouldShow(userWantsLegend, seriesCount))
            return 0;
        return seriesCount - MaxItems;
    }

    public static bool IncludeInLegend(int index, int seriesCount, bool userWantsLegend) =>
        ShouldShow(userWantsLegend, seriesCount) && index < MaxItems;

    public static LegendContext Context(IReadOnlyList<SeriesSnapshot> series)
    {
        if (series.Count == 0)
            return new LegendContext(true, true, true);

        var first = series[0];
        var sameMachine = series.All(item =>
            string.Equals(item.MachineDisplay, first.MachineDisplay, StringComparison.OrdinalIgnoreCase));
        var sameCounter = series.All(item =>
            WorkspaceEditor.NameEquals(item.Path.Category, first.Path.Category)
            && WorkspaceEditor.NameEquals(item.Path.Counter, first.Path.Counter));
        var sameInstance = series.All(item =>
            string.Equals(item.Path.Instance ?? "", first.Path.Instance ?? "", StringComparison.OrdinalIgnoreCase));
        return new LegendContext(sameMachine, sameCounter, sameInstance);
    }

    public static IReadOnlyList<string> Labels(IReadOnlyList<SeriesSnapshot> series, ChartGrouping grouping)
    {
        var context = Context(series);
        return series.Select(item => Label(item, context, grouping)).ToList();
    }

    public static string Label(SeriesSnapshot snapshot, LegendContext context, ChartGrouping grouping)
    {
        var instance = snapshot.Path.Instance ?? "";
        var hasInstance = instance.Length > 0;
        var counter = snapshot.Path.Counter;
        var machine = snapshot.MachineDisplay;

        if (context.SameCounter && !context.SameMachine)
            return context.SameInstance || !hasInstance ? machine : machine + " (" + instance + ")";
        if (context.SameCounter && context.SameMachine && hasInstance && !context.SameInstance)
            return instance;
        if (context.SameMachine && !context.SameCounter)
            return hasInstance ? counter + " (" + instance + ")" : counter;

        return grouping switch
        {
            ChartGrouping.ByMachine => hasInstance ? counter + " (" + instance + ")" : snapshot.Path.Category + "\\" + counter,
            ChartGrouping.ByCounter => hasInstance ? machine + " (" + instance + ")" : machine,
            _ => hasInstance ? machine + " · " + counter + " (" + instance + ")" : machine + " · " + counter
        };
    }
}

public static class ChartTicks
{
    public const float FontSize = 14;
    public const float BottomPad = 56;

    public static int CountForWidth(double widthPx)
    {
        const double pixelsPerLabel = 96;
        var n = (int)Math.Floor(Math.Max(widthPx, 160) / pixelsPerLabel);
        return Math.Clamp(n, 3, 10);
    }
}

public static class IndexRange
{
    public static IEnumerable<int> Inclusive(int left, int right)
    {
        var lo = Math.Min(left, right);
        var hi = Math.Max(left, right);
        for (var i = lo; i <= hi; i++)
            yield return i;
    }
}

public static class TokenFilter
{
    public static bool Matches(string text, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        return filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
