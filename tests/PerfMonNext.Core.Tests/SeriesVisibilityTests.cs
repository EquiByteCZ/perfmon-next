using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class SeriesVisibilityTests
{
    [Fact]
    public void Hide_selected_touches_only_selection()
    {
        var plan = SeriesVisibility.Set(["a", "c"], false);
        Assert.Equal(2, plan.Count);
        Assert.False(plan["a"]);
        Assert.False(plan["c"]);
    }

    [Fact]
    public void Toggle_hides_when_any_selected_is_visible()
    {
        var plan = SeriesVisibility.Toggle([("a", true), ("b", false)]);
        Assert.False(plan["a"]);
        Assert.False(plan["b"]);
    }

    [Fact]
    public void Toggle_shows_when_all_selected_are_hidden()
    {
        var plan = SeriesVisibility.Toggle([("a", false), ("b", false)]);
        Assert.True(plan["a"]);
        Assert.True(plan["b"]);
    }

    [Fact]
    public void Show_only_hides_everything_else()
    {
        var plan = SeriesVisibility.ShowOnly(["a", "b", "c", "d"], ["b", "d"]);
        Assert.Equal(new Dictionary<string, bool> { ["a"] = false, ["b"] = true, ["c"] = false, ["d"] = true }, plan);
    }

    [Fact]
    public void Show_only_with_empty_selection_changes_nothing()
    {
        Assert.Empty(SeriesVisibility.ShowOnly(["a", "b"], []));
    }
}
