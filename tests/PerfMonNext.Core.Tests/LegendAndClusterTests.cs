using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class LegendAndClusterTests
{
    [Fact]
    public void Unchecked_legend_is_off_on_every_frame()
    {
        var frames = ChartFrames.Build(
            [Snap("s2d1", "Processor", "% Processor Time", "_Total", 1),
             Snap("s2d2", "Processor", "% Processor Time", "_Total", 2)],
            ChartGrouping.ByCounter,
            showLegend: false,
            TimeSpan.FromMinutes(5));
        var cpu = Assert.Single(frames);
        Assert.False(cpu.ShowLegend);
        Assert.Equal(0, cpu.LegendOverflow);
        Assert.All(cpu.Series, series => Assert.Equal("", series.Legend));
    }

    [Fact]
    public void One_machine_same_counter_uses_instance_only()
    {
        var series = Enumerable.Range(0, 8)
            .Select(i => Snap("s2d1", "Hyper-V Hypervisor Virtual Processor", "% Total Run Time", "Hv VP " + i, i))
            .ToList();
        var labels = ChartLegend.Labels(series, ChartGrouping.ByCounter);
        Assert.Equal(["Hv VP 0", "Hv VP 1", "Hv VP 2", "Hv VP 3", "Hv VP 4", "Hv VP 5", "Hv VP 6", "Hv VP 7"], labels);
        var frame = Assert.Single(ChartFrames.Build(series, ChartGrouping.ByCounter, true, null));
        Assert.True(frame.ShowLegend);
        Assert.Equal(2, frame.LegendOverflow);
        Assert.Equal("Hv VP 0", frame.Series[0].Legend);
        Assert.Equal("Hv VP 5", frame.Series[5].Legend);
        Assert.Equal("", frame.Series[6].Legend);
    }

    [Fact]
    public void Two_machines_same_counter_keep_machine_and_drop_redundant_instance()
    {
        var labels = ChartLegend.Labels(
            [Snap("s2d1", "Processor", "% Processor Time", "_Total", 1),
             Snap("s2d2", "Processor", "% Processor Time", "_Total", 2)],
            ChartGrouping.ByCounter);
        Assert.Equal(["s2d1", "s2d2"], labels);
    }

    [Fact]
    public void Many_series_still_show_a_capped_legend_when_requested()
    {
        var series = Enumerable.Range(0, 80)
            .Select(i => Snap("s2d1", "Hyper-V Hypervisor Virtual Processor", "% Total Run Time", "Hv VP " + i, i))
            .ToList();
        var frame = Assert.Single(ChartFrames.Build(series, ChartGrouping.ByCounter, true, null));
        Assert.True(frame.ShowLegend);
        Assert.Equal(74, frame.LegendOverflow);
        Assert.Equal(6, frame.Series.Count(item => item.Legend.Length > 0));
    }

    [Fact]
    public void Tick_count_grows_with_width_and_stays_bounded()
    {
        Assert.Equal(3, ChartTicks.CountForWidth(100));
        Assert.InRange(ChartTicks.CountForWidth(800), 6, 10);
        Assert.Equal(10, ChartTicks.CountForWidth(4000));
    }

    [Fact]
    public void Instance_filter_matches_every_token()
    {
        Assert.True(TokenFilter.Matches("Hv VP 12", "hv vp"));
        Assert.True(TokenFilter.Matches("SalesVM", "sales"));
        Assert.False(TokenFilter.Matches("Hv LP 0", "Hv VP"));
        Assert.True(TokenFilter.Matches("Hv VP 12", ""));
    }

    [Fact]
    public void Cluster_name_expands_to_nodes_and_a_node_name_does_not()
    {
        var lookup = new ClusterLookup("s2dclus.contoso.local", ["s2d1", "s2d2.contoso.local", "s2d1"]);
        Assert.True(ClusterExpansion.IsClusterName("s2dclus", lookup));
        Assert.Equal(["s2d1", "s2d2.contoso.local"], ClusterExpansion.HostsToAdd("S2DCLUS", lookup));
        Assert.Equal(["s2d1"], ClusterExpansion.HostsToAdd("s2d1", lookup));
        Assert.Equal(["standalone"], ClusterExpansion.HostsToAdd("standalone", null));
    }

    [Fact]
    public void Same_host_treats_fqdn_and_short_name_as_equal_but_not_ip_octets()
    {
        Assert.True(MachineHost.SameHost("s2d1.contoso.local", "S2D1"));
        Assert.False(MachineHost.SameHost("10.0.0.5", "10.0.0.8"));
        Assert.True(MachineHost.SameHost("10.0.0.5", "10.0.0.5"));
    }

    [Fact]
    public void Index_range_is_inclusive_in_either_direction()
    {
        Assert.Equal([2, 3, 4], IndexRange.Inclusive(2, 4));
        Assert.Equal([2, 3, 4], IndexRange.Inclusive(4, 2));
        Assert.Equal([5], IndexRange.Inclusive(5, 5));
    }

    private static SeriesSnapshot Snap(string machine, string category, string counter, string instance, params double?[] values)
    {
        var start = new DateTimeOffset(2026, 11, 12, 13, 0, 0, TimeSpan.FromHours(1));
        var path = new CounterPath(category, counter, instance);
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
