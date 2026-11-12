using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class CounterSetBuilderTests
{
    [Fact]
    public void Hyper_v_logical_processor_rows_fill_counters_and_instances()
    {
        var set = CounterSetBuilder.Build(
            "Hyper-V Hypervisor Logical Processor",
            null,
            [
                new("% Total Run Time", "_Total"),
                new("% Total Run Time", "0"),
                new("% Guest Run Time", "_Total"),
                new("% Guest Run Time", "0")
            ]);

        Assert.Equal(["% Guest Run Time", "% Total Run Time"], set.Counters.Select(counter => counter.Name));
        Assert.Equal(["_Total", "0"], set.Instances);
        Assert.True(set.MultiInstance);
    }
}
