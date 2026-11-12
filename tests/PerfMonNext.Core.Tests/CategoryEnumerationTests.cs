using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class CategoryEnumerationTests
{
    [Fact]
    public void Multi_type_with_empty_instances_is_not_single_instance()
    {
        var set = CategoryEnumeration.Resolve(
            "Hyper-V Hypervisor Logical Processor",
            null,
            [
                new("CategoryType", CategoryInstanceKind.Multi, ["% Total Run Time"], [], Error: "ReadCategory returned no instances.")
            ]);

        Assert.True(set.MultiInstance);
        Assert.Empty(set.Instances);
        Assert.Equal(["% Total Run Time"], set.Counters.Select(counter => counter.Name));
        Assert.Equal(CategoryEnumeration.MissingInstancesMessage, set.EnumerationError);
    }

    [Fact]
    public void Multi_type_with_empty_instances_defaults_to_total_when_available()
    {
        var set = CategoryEnumeration.Resolve(
            "Hyper-V Hypervisor Logical Processor",
            null,
            [
                new("CategoryType", CategoryInstanceKind.Multi, ["% Total Run Time"], []),
                new("TotalProbe", CategoryInstanceKind.Multi, ["% Total Run Time"], [], TotalAvailable: true)
            ]);

        Assert.True(set.MultiInstance);
        Assert.Equal(["_Total"], set.Instances);
        Assert.Null(set.EnumerationError);
    }

    [Fact]
    public void Instance_names_from_any_source_mark_the_set_multi_instance()
    {
        var set = CategoryEnumeration.Resolve(
            "Hyper-V Hypervisor Logical Processor",
            null,
            [
                new("ReadCategory", CategoryInstanceKind.Unknown, ["% Total Run Time"], [""]),
                new("PdhEnumObjectItems", CategoryInstanceKind.Unknown, ["% Total Run Time", "% Guest Run Time"], ["_Total", "Hv LP 0"])
            ]);

        Assert.True(set.MultiInstance);
        Assert.Equal(["_Total", "Hv LP 0"], set.Instances);
        Assert.Equal(["% Guest Run Time", "% Total Run Time"], set.Counters.Select(counter => counter.Name));
        Assert.Null(set.EnumerationError);
    }

    [Fact]
    public void False_positive_total_probe_does_not_upgrade_a_single_instance_set()
    {
        var set = CategoryEnumeration.Resolve(
            "Memory",
            null,
            [
                new("CategoryType", CategoryInstanceKind.Single, ["Available MBytes"], []),
                new("TotalProbe", CategoryInstanceKind.Multi, ["Available MBytes"], [], TotalAvailable: true)
            ]);

        Assert.False(set.MultiInstance);
        Assert.Empty(set.Instances);
        Assert.Null(set.EnumerationError);
    }

    [Fact]
    public void Declared_single_instance_with_no_instance_names_stays_single()
    {
        var set = CategoryEnumeration.Resolve(
            "Memory",
            null,
            [
                new("CategoryType", CategoryInstanceKind.Single, ["Available MBytes"], []),
                new("ReadCategory", CategoryInstanceKind.Unknown, ["Available MBytes"], [""])
            ]);

        Assert.False(set.MultiInstance);
        Assert.Empty(set.Instances);
        Assert.Null(set.EnumerationError);
        Assert.Equal(["Available MBytes"], set.Counters.Select(counter => counter.Name));
    }

    [Fact]
    public void Instances_override_a_wrong_single_instance_type()
    {
        var set = CategoryEnumeration.Resolve(
            "Processor",
            null,
            [
                new("CategoryType", CategoryInstanceKind.Single, [], []),
                new("GetInstanceNames", CategoryInstanceKind.Unknown, ["% Processor Time"], ["_Total", "0"])
            ]);

        Assert.True(set.MultiInstance);
        Assert.Equal(["_Total", "0"], set.Instances);
        Assert.Null(set.EnumerationError);
    }

    [Fact]
    public void Default_instance_is_total_when_listed_otherwise_null_when_multi_and_empty()
    {
        var withTotal = new CounterSetInfo
        {
            Name = "Hyper-V Hypervisor Logical Processor",
            MultiInstance = true,
            Instances = ["Hv LP 0", "_Total"],
            Counters = [new CounterInfo { Name = "% Total Run Time" }]
        };
        Assert.Equal("_Total", WorkspaceEditor.DefaultInstance(withTotal));

        var emptyMulti = new CounterSetInfo
        {
            Name = "Hyper-V Hypervisor Logical Processor",
            MultiInstance = true,
            EnumerationError = CategoryEnumeration.MissingInstancesMessage,
            Counters = [new CounterInfo { Name = "% Total Run Time" }]
        };
        Assert.Null(WorkspaceEditor.DefaultInstance(emptyMulti));

        var memory = new CounterSetInfo
        {
            Name = "Memory",
            Counters = [new CounterInfo { Name = "Available MBytes" }]
        };
        Assert.Equal("", WorkspaceEditor.DefaultInstance(memory));
    }
}
