using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class ChartSizingTests
{
    [Fact]
    public void Default_fills_viewport_for_one_chart_and_is_fixed_for_several()
    {
        Assert.Equal(600, ChartSizing.Default(1, 600));
        Assert.Equal(ChartSizing.DefaultSingleMinHeight, ChartSizing.Default(1, 50));
        Assert.Equal(ChartSizing.DefaultMultiHeight, ChartSizing.Default(3, 900));
    }

    [Theory]
    [InlineData(260, 100, 360)]
    [InlineData(260, -500, ChartSizing.MinHeight)]
    [InlineData(2300, 500, ChartSizing.MaxHeight)]
    [InlineData(260, 12.6, 273)]
    public void Drag_adds_delta_and_clamps(double current, double delta, double expected) =>
        Assert.Equal(expected, ChartSizing.Drag(current, delta));

    [Fact]
    public void Chart_heights_round_trip_through_a_saved_workspace_and_are_sanitized()
    {
        var workspace = WorkspaceFactory.CreateNew();
        workspace.ChartHeights["Processor\\% Processor Time"] = 480;
        workspace.ChartHeights["tiny"] = 10;

        var loaded = WorkspaceStore.Deserialize(WorkspaceStore.Serialize(workspace)).Workspace;

        Assert.Equal(480, loaded.ChartHeights["processor\\% processor time"]);
        Assert.Equal(ChartSizing.MinHeight, loaded.ChartHeights["tiny"]);
    }

    [Fact]
    public void Old_workspace_without_chart_heights_loads_with_none()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(WorkspaceStore.Serialize(WorkspaceFactory.CreateNew()))!.AsObject();
        var key = node.Select(p => p.Key).Single(k => k.Equals("ChartHeights", StringComparison.OrdinalIgnoreCase));
        node.Remove(key);
        var json = node.ToJsonString();
        var loaded = WorkspaceStore.Deserialize(json).Workspace;
        Assert.Empty(loaded.ChartHeights);
    }
}
