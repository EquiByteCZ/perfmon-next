using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class ChartMathTests
{
    [Theory]
    [InlineData(5, 100, 0, 110)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(42, 42, 0, 46.2)]
    public void Non_negative_data_starts_at_zero(double low, double high, double min, double max)
    {
        var (a, b) = YRange.For(low, high);
        Assert.Equal(min, a);
        Assert.Equal(max, b, 6);
    }

    [Fact]
    public void Negative_data_is_allowed_only_when_present()
    {
        var (a, b) = YRange.For(-10, 30);
        Assert.True(a < -10 && b > 30);
        var (c, d) = YRange.For(-50, -5);
        Assert.Equal(0, d);
        Assert.True(c < -50);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(0.001, "0.001")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e-6, "1e-6")]
    [InlineData(1000, "1000")]
    [InlineData(1e7, "1e7")]
    [InlineData(0.25, "0.25")]
    public void Scale_never_displays_as_zero(double scale, string text)
    {
        Assert.Equal(text, ScaleFormat.Format(scale));
        Assert.True(ScaleFormat.TryParse(text, out var back));
        Assert.Equal(scale, back, 12);
    }

    [Theory]
    [InlineData("0,001", 0.001)]
    [InlineData("×0.01", 0.01)]
    [InlineData("x10", 10)]
    [InlineData("1E-05", 1e-5)]
    public void Scale_parses_common_inputs(string text, double expected)
    {
        Assert.True(ScaleFormat.TryParse(text, out var value));
        Assert.Equal(expected, value, 12);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("abc")]
    public void Scale_rejects_bad_input(string text) => Assert.False(ScaleFormat.TryParse(text, out _));

    [Fact]
    public void Suffix_is_empty_for_one() => Assert.Equal("", ScaleFormat.Suffix(1));

    [Fact]
    public void Suffix_shows_factor() => Assert.Equal(" ×0.001", ScaleFormat.Suffix(0.001));

    [Theory]
    [InlineData(5, 5000, 1000)]
    [InlineData(5, 40, 1)]
    [InlineData(0.5, 80, 100)]
    [InlineData(5000, 5000, 1)]
    [InlineData(0, 5000, 1)]
    public void Fit_factor_brings_small_series_to_the_largest_decade(double max, double reference, double factor) =>
        Assert.Equal(factor, ScaleFitter.FactorFor(max, reference), 9);

    [Fact]
    public void Fit_leaves_a_single_series_chart_at_one()
    {
        var plan = ScaleFitter.Fit([Snap("a", "Bytes/sec", 5_000_000)], ChartGrouping.ByCounter);
        Assert.Equal(1, Assert.Single(plan).Value);
    }

    [Fact]
    public void Fit_scales_only_the_smaller_series_in_a_mixed_chart()
    {
        var big = Snap("big", "Bytes/sec", 5_000_000);
        var small = Snap("small", "Bytes/sec", 3_000);
        var plan = ScaleFitter.Fit([big, small], ChartGrouping.Combined);
        Assert.Equal(1, plan[big.OverrideKey]);
        Assert.Equal(1000, plan[small.OverrideKey]);
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1234, "1.23K")]
    [InlineData(12_345_678, "12.3M")]
    [InlineData(2.5e9, "2.5G")]
    [InlineData(0, "0")]
    public void Compact_numbers(double value, string expected) =>
        Assert.Equal(expected, CompactFormat.Format(value).Replace(',', '.'));

    [Theory]
    [InlineData("Bytes/sec", "B/s")]
    [InlineData("Bytes Received/sec", "B/s")]
    [InlineData("% Processor Time", "%")]
    [InlineData("Available MBytes", "MB")]
    [InlineData("Interrupts/sec", "/s")]
    [InlineData("Processor Queue Length", "")]
    public void Unit_hints(string counter, string unit) => Assert.Equal(unit, CompactFormat.UnitFor(counter));

    [Fact]
    public void Nearest_index_by_binary_search()
    {
        double[] xs = [1, 2, 3, 10];
        Assert.Equal(0, ChartHover.Nearest(xs, -5));
        Assert.Equal(1, ChartHover.Nearest(xs, 2.4));
        Assert.Equal(2, ChartHover.Nearest(xs, 2.6));
        Assert.Equal(3, ChartHover.Nearest(xs, 99));
        Assert.Equal(-1, ChartHover.Nearest([], 1));
    }

    [Fact]
    public void Hover_shows_real_unscaled_values()
    {
        var snap = Snap("s", "Bytes/sec", 1000, 2000, 3000);
        var scaled = new SeriesSnapshot
        {
            Key = snap.Key, OverrideKey = snap.OverrideKey, MachineId = snap.MachineId, MachineDisplay = snap.MachineDisplay,
            Host = snap.Host, Path = snap.Path, SelectionId = null, Color = "#FF0000", Scale = 0.001, Visible = true,
            Samples = snap.Samples, Stats = snap.Stats
        };
        var frame = Assert.Single(ChartFrames.Build([scaled], ChartGrouping.ByCounter, true, null));
        Assert.Equal(2.0, frame.Series[0].Ys[1], 9);
        Assert.Contains("×0.001", frame.Series[0].Legend);
        var hover = ChartHover.At(frame, frame.Series[0].Xs[1]);
        Assert.NotNull(hover);
        var row = Assert.Single(hover!.Rows);
        Assert.Equal(2000, row.Value!.Value, 6);
        Assert.Equal("#FF0000", row.Color);
        Assert.StartsWith("2", row.Text);
        Assert.EndsWith("B/s", row.Text);
    }

    [Fact]
    public void Frame_y_axis_starts_at_zero_for_positive_counters()
    {
        var frame = Assert.Single(ChartFrames.Build([Snap("s", "Bytes/sec", 5, 7, 6)], ChartGrouping.ByCounter, true, null));
        Assert.Equal(0, frame.YMin);
        Assert.True(frame.YMax > 7);
    }

    [Fact]
    public void Legend_detail_uses_real_values()
    {
        var frame = Assert.Single(ChartFrames.Build([Snap("s", "Bytes/sec", 1000, 5_000_000, 2_000)], ChartGrouping.ByCounter, true, null));
        var detail = frame.Series[0].LegendDetail.Replace(',', '.');
        Assert.Contains("last 2K", detail);
        Assert.Contains("max 5M", detail);
    }

    private static SeriesSnapshot Snap(string machine, string counter, params double?[] values)
    {
        var start = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.FromHours(1));
        var path = new CounterPath("Hyper-V Virtual Switch", counter, "LabSwitch-" + machine);
        var samples = values.Select((value, index) => new SamplePoint(start.AddSeconds(index), value)).ToList();
        return new SeriesSnapshot
        {
            Key = SeriesIdentity.Key(machine, path),
            OverrideKey = SeriesIdentity.OverrideKey(machine, path),
            MachineId = machine,
            MachineDisplay = machine,
            Host = machine,
            Path = path,
            SelectionId = null,
            Color = "#4C9AFF",
            Scale = 1,
            Visible = true,
            Samples = samples,
            Stats = SeriesStats.FromSamples(samples, 1)
        };
    }
}
