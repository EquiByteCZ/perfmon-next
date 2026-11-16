using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class ClearCountersTests
{
    [Fact]
    public void Clear_removes_every_counter_and_override_but_keeps_machines()
    {
        var workspace = WorkspaceFactory.CreateNew();
        workspace.Machines.Add(new MachineTarget { Id = "n2", Host = "node02", DisplayName = "node02" });
        workspace.Counters.Add(WorkspaceFactory.Selection("*", "Processor", "% Processor Time", "_Total"));
        workspace.Visibility["k"] = false;
        workspace.ScaleOverrides["k"] = 10;
        workspace.ColorOverrides["k"] = "#FF0000";
        var machines = workspace.Machines.Select(m => m.Id).ToList();
        var before = workspace.Counters.Count;

        var removed = WorkspaceEditor.ClearCounters(workspace);

        Assert.Equal(before, removed);
        Assert.Empty(workspace.Counters);
        Assert.Empty(workspace.Visibility);
        Assert.Empty(workspace.ScaleOverrides);
        Assert.Empty(workspace.ColorOverrides);
        Assert.Equal(machines, workspace.Machines.Select(m => m.Id).ToList());
    }

    [Fact]
    public void Clear_on_empty_workspace_removes_nothing()
    {
        var workspace = WorkspaceFactory.CreateNew();
        workspace.Counters.Clear();
        Assert.Equal(0, WorkspaceEditor.ClearCounters(workspace));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(80, true)]
    public void Confirmation_only_above_a_few_series(int count, bool expected) =>
        Assert.Equal(expected, WorkspaceEditor.ClearNeedsConfirmation(count));
}

public sealed class ChartPaletteTests
{
    public static TheoryData<string> Skins => new() { "Dark", "Light" };

    [Theory]
    [MemberData(nameof(Skins))]
    public void Text_is_readable_on_the_chart_background(string skin)
    {
        var p = ChartPalette.ForSkin(skin);
        Assert.True(ChartPalette.Contrast(p.TickLabel, p.Background) >= 7, "tick labels");
        Assert.True(ChartPalette.Contrast(p.AxisTitle, p.Background) >= 7, "axis title");
        Assert.True(ChartPalette.Contrast(p.LegendText, p.LegendBackground) >= 7, "legend text");
        Assert.True(ChartPalette.Contrast(p.OverflowText, p.LegendBackground) >= 4.5, "+N more");
        Assert.True(ChartPalette.Contrast(p.MajorTick, p.Background) >= 4.5, "major ticks");
        Assert.True(ChartPalette.Contrast(p.AxisLine, p.Background) >= 3, "axis line");
    }

    [Theory]
    [MemberData(nameof(Skins))]
    public void Grid_is_visible_but_quieter_than_labels(string skin)
    {
        var p = ChartPalette.ForSkin(skin);
        var grid = ChartPalette.Contrast(p.Grid, p.Background);
        Assert.True(grid >= 1.2, "grid visible");
        Assert.True(grid < ChartPalette.Contrast(p.TickLabel, p.Background), "grid quieter than text");
    }

    [Fact]
    public void Unknown_skin_falls_back_to_dark()
    {
        Assert.Same(ChartPalette.Dark, ChartPalette.ForSkin(null));
        Assert.Same(ChartPalette.Dark, ChartPalette.ForSkin("Solarized"));
        Assert.Same(ChartPalette.Light, ChartPalette.ForSkin("light"));
    }
}
