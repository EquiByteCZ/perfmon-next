namespace PerfMonNext.Core;

public sealed class EncodedSeries
{
    public required string Key { get; init; }
    public required string Legend { get; init; }
    public required string Color { get; init; }
    public required double[] Xs { get; init; }
    public required double[] Ys { get; init; }
}

public sealed class ChartFrame
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required bool ShowLegend { get; init; }
    public double? XMin { get; init; }
    public double? XMax { get; init; }
    public double? YMin { get; init; }
    public double? YMax { get; init; }
    public required IReadOnlyList<EncodedSeries> Series { get; init; }
}

public static class ScaleSuggester
{
    public static double Suggest(double maxAbs)
    {
        if (!double.IsFinite(maxAbs) || maxAbs <= 0)
            return 1;

        var scale = 1d;
        for (var i = 0; i < 12 && maxAbs * scale > 100; i++)
            scale /= 10;
        for (var i = 0; i < 12 && maxAbs * scale < 10; i++)
            scale *= 10;
        return scale;
    }
}

public static class ChartFrames
{
    public static IReadOnlyList<ChartFrame> Build(
        IReadOnlyList<SeriesSnapshot> series,
        ChartGrouping grouping,
        bool showLegend,
        TimeSpan? window)
    {
        var visible = series.Where(item => item.Visible).ToList();
        if (visible.Count == 0)
            return [];

        IEnumerable<IGrouping<string, SeriesSnapshot>> groups = grouping switch
        {
            ChartGrouping.Combined => visible.GroupBy(_ => "All counters"),
            ChartGrouping.ByMachine => visible.GroupBy(item => item.MachineDisplay, StringComparer.OrdinalIgnoreCase),
            _ => visible.GroupBy(item => item.Path.Category + " · " + item.Path.Counter, StringComparer.OrdinalIgnoreCase)
        };

        var frames = new List<ChartFrame>();
        foreach (var group in groups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var encoded = group.Select(item => Encode(item, grouping)).Where(item => item.Xs.Length > 0).ToList();
            double? xMin = null, xMax = null, yMin = null, yMax = null;
            foreach (var item in encoded)
            {
                for (var i = 0; i < item.Xs.Length; i++)
                {
                    var x = item.Xs[i];
                    xMin = xMin is null ? x : Math.Min(xMin.Value, x);
                    xMax = xMax is null ? x : Math.Max(xMax.Value, x);
                    var y = item.Ys[i];
                    if (double.IsNaN(y))
                        continue;
                    yMin = yMin is null ? y : Math.Min(yMin.Value, y);
                    yMax = yMax is null ? y : Math.Max(yMax.Value, y);
                }
            }

            if (window is not null && xMax is double end)
                xMin = end - window.Value.TotalDays;

            if (xMin is double left && xMax is double right)
            {
                var pad = Math.Max((right - left) * 0.02, 1.0 / 86_400);
                xMax = right + pad;
                if (window is null)
                    xMin = left - pad;
            }

            if (yMin is double low && yMax is double high)
            {
                if (high <= low)
                {
                    low -= 1;
                    high += 1;
                }
                else
                {
                    var pad = (high - low) * 0.08;
                    low -= pad;
                    high += pad;
                }

                yMin = low;
                yMax = high;
            }

            var keyPrefix = grouping switch
            {
                ChartGrouping.Combined => "all",
                ChartGrouping.ByMachine => "machine",
                _ => "counter"
            };
            frames.Add(new ChartFrame
            {
                Key = keyPrefix + ":" + group.Key,
                Title = group.Key,
                ShowLegend = showLegend && encoded.Count is > 0 and <= 12,
                XMin = xMin,
                XMax = xMax,
                YMin = yMin,
                YMax = yMax,
                Series = encoded
            });
        }

        return frames;
    }

    public static EncodedSeries Encode(SeriesSnapshot snapshot, ChartGrouping grouping)
    {
        var xs = new double[snapshot.Samples.Count];
        var ys = new double[snapshot.Samples.Count];
        for (var i = 0; i < snapshot.Samples.Count; i++)
        {
            var sample = snapshot.Samples[i];
            xs[i] = sample.Timestamp.LocalDateTime.ToOADate();
            ys[i] = sample.Value is double value && double.IsFinite(value) ? value * snapshot.Scale : double.NaN;
        }

        return new EncodedSeries
        {
            Key = snapshot.Key,
            Legend = Legend(snapshot, grouping),
            Color = snapshot.Color,
            Xs = xs,
            Ys = ys
        };
    }

    public static string Legend(SeriesSnapshot snapshot, ChartGrouping grouping)
    {
        var instance = snapshot.Path.Instance;
        var hasInstance = !string.IsNullOrEmpty(instance);
        return grouping switch
        {
            ChartGrouping.ByMachine => hasInstance
                ? snapshot.Path.Counter + " (" + instance + ")"
                : snapshot.Path.Category + "\\" + snapshot.Path.Counter,
            ChartGrouping.ByCounter => hasInstance
                ? snapshot.MachineDisplay + " (" + instance + ")"
                : snapshot.MachineDisplay,
            _ => hasInstance
                ? snapshot.MachineDisplay + " · " + snapshot.Path.Counter + " (" + instance + ")"
                : snapshot.MachineDisplay + " · " + snapshot.Path.Counter
        };
    }
}
