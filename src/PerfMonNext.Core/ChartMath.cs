using System.Globalization;

namespace PerfMonNext.Core;

/// <summary>Y-axis range: starts at 0 for non-negative data, with headroom above the peak.</summary>
public static class YRange
{
    public const double Headroom = 0.10;

    public static (double Min, double Max) For(double low, double high)
    {
        if (!double.IsFinite(low) || !double.IsFinite(high))
            return (0, 1);
        if (low > high)
            (low, high) = (high, low);

        if (low >= 0)
        {
            // Non-negative data: never pad below zero.
            var top = high <= 0 ? 1 : high * (1 + Headroom);
            return (0, top);
        }

        if (high <= 0)
        {
            var bottom = low * (1 + Headroom);
            return (bottom, 0);
        }

        var pad = (high - low) * Headroom;
        return (low - pad, high + pad);
    }
}

/// <summary>Display and parsing of per-series scale factors.</summary>
public static class ScaleFormat
{
    public static bool IsIdentity(double scale) => !double.IsFinite(scale) || scale == 0 || Math.Abs(scale - 1) < 1e-12;

    public static string Format(double scale)
    {
        if (IsIdentity(scale))
            return "1";
        var exponent = Math.Log10(Math.Abs(scale));
        var rounded = Math.Round(exponent);
        if (Math.Abs(exponent - rounded) < 1e-9)
        {
            var e = (int)rounded;
            if (e is >= -4 and <= 4)
                return scale.ToString(e < 0 ? "0." + new string('#', -e) : "0", CultureInfo.InvariantCulture);
            return (scale < 0 ? "-" : "") + "1e" + e.ToString(CultureInfo.InvariantCulture);
        }

        return scale.ToString("G4", CultureInfo.InvariantCulture);
    }

    /// <summary>Legend suffix such as " ×0.001"; empty for a factor of 1.</summary>
    public static string Suffix(double scale) => IsIdentity(scale) ? "" : " ×" + Format(scale);

    public static bool TryParse(string? text, out double scale)
    {
        scale = 1;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim().TrimStart('×', 'x', 'X', '*').Trim();
        t = t.Contains(',') && !t.Contains('.')
            ? t.Replace(',', '.')
            : t.Replace(",", "", StringComparison.Ordinal);
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value == 0)
            return false;
        scale = value;
        return true;
    }
}

/// <summary>
/// Fit scales: in each chart that mixes series of very different size, magnify the smaller series by a
/// power of ten so they land in the same decade as the largest one. The largest series stays ×1 so the
/// axis keeps real units. A chart with one series, or series within 10× of each other, stays at ×1.
/// </summary>
public static class ScaleFitter
{
    public static IReadOnlyDictionary<string, double> Fit(IReadOnlyList<SeriesSnapshot> series, ChartGrouping grouping)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in series.Where(item => item.Visible).GroupBy(item => ChartFrames.GroupKey(item, grouping), StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            var reference = items.Select(item => item.Stats.RawMaxAbs ?? 0).DefaultIfEmpty(0).Max();
            foreach (var item in items)
            {
                var max = item.Stats.RawMaxAbs ?? 0;
                result[item.OverrideKey] = items.Count < 2 ? 1 : FactorFor(max, reference);
            }
        }

        return result;
    }

    public static double FactorFor(double max, double reference)
    {
        if (!double.IsFinite(max) || !double.IsFinite(reference) || max <= 0 || reference <= 0 || max >= reference)
            return 1;
        var decades = Math.Floor(Math.Log10(reference / max) + 1e-9);
        if (decades < 1)
            return 1;
        return Math.Pow(10, Math.Min(decades, 12));
    }
}

/// <summary>Compact numbers (1.23K, 4.5M) and a unit hint from the counter name.</summary>
public static class CompactFormat
{
    private static readonly (double Factor, string Suffix)[] Steps =
    [
        (1e12, "T"), (1e9, "G"), (1e6, "M"), (1e3, "K")
    ];

    public static string Format(double value)
    {
        if (!double.IsFinite(value))
            return "—";
        var abs = Math.Abs(value);
        foreach (var (factor, suffix) in Steps)
        {
            if (abs >= factor)
                return Trim(value / factor) + suffix;
        }

        if (abs == 0)
            return "0";
        return abs >= 1 ? Trim(value) : value.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private static string Trim(double value)
    {
        var abs = Math.Abs(value);
        var format = abs >= 100 ? "0" : abs >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.CurrentCulture);
    }

    public static string UnitFor(string counter)
    {
        if (string.IsNullOrEmpty(counter))
            return "";
        var c = counter.Trim();
        var perSec = c.EndsWith("/sec", StringComparison.OrdinalIgnoreCase) || c.EndsWith("/s", StringComparison.OrdinalIgnoreCase);
        if (c.StartsWith('%'))
            return "%";
        if (c.Contains("Bytes", StringComparison.OrdinalIgnoreCase) && !c.Contains("MBytes", StringComparison.OrdinalIgnoreCase)
            && !c.Contains("KBytes", StringComparison.OrdinalIgnoreCase))
            return perSec ? "B/s" : "B";
        if (c.Contains("MBytes", StringComparison.OrdinalIgnoreCase))
            return "MB";
        return perSec ? "/s" : "";
    }

    /// <summary>"12.3M B/s (12,345,678)" style text for tooltips; small numbers stay plain.</summary>
    public static string WithUnit(double value, string unit)
    {
        if (!double.IsFinite(value))
            return "—";
        var compact = Format(value);
        var spacer = unit.Length == 0 || unit == "%" ? "" : " ";
        var head = compact + spacer + unit;
        return Math.Abs(value) >= 10_000 ? head + " (" + ValueFormat.Format(value) + ")" : head;
    }
}

public sealed record HoverRow(string Label, string Color, double? Value, string Text);

public sealed record HoverInfo(double X, DateTime Time, IReadOnlyList<HoverRow> Rows, int Hidden);

/// <summary>Grafana-style hover: nearest point of each series at a given time.</summary>
public static class ChartHover
{
    public const int MaxRows = 12;

    public static int Nearest(IReadOnlyList<double> xs, double x)
    {
        if (xs.Count == 0)
            return -1;
        int lo = 0, hi = xs.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (xs[mid] < x)
                lo = mid + 1;
            else
                hi = mid;
        }

        if (lo > 0 && Math.Abs(xs[lo - 1] - x) <= Math.Abs(xs[lo] - x))
            return lo - 1;
        return lo;
    }

    public static HoverInfo? At(ChartFrame frame, double x)
    {
        var withData = frame.Series.Where(series => series.Xs.Length > 0).ToList();
        if (withData.Count == 0)
            return null;
        if (frame.XMin is double left && x < left - 1e-9)
            return null;
        if (frame.XMax is double right && x > right + 1e-9)
            return null;

        double? snapped = null;
        var rows = new List<HoverRow>();
        foreach (var series in withData)
        {
            var index = Nearest(series.Xs, x);
            if (index < 0)
                continue;
            if (snapped is null || Math.Abs(series.Xs[index] - x) < Math.Abs(snapped.Value - x))
                snapped = series.Xs[index];
            var shown = series.Ys[index];
            double? raw = double.IsNaN(shown) ? null : shown / (ScaleFormat.IsIdentity(series.Scale) ? 1 : series.Scale);
            var text = raw is double value ? CompactFormat.WithUnit(value, series.Unit) : "—";
            rows.Add(new HoverRow(series.Name + ScaleFormat.Suffix(series.Scale), series.Color, raw, text));
        }

        if (snapped is not double time)
            return null;
        var hidden = Math.Max(0, rows.Count - MaxRows);
        return new HoverInfo(time, DateTime.FromOADate(time), rows.Take(MaxRows).ToList(), hidden);
    }
}
