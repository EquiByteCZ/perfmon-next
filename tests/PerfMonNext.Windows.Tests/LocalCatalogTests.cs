using PerfMonNext.Core;
using PerfMonNext.Windows;
using Xunit;

namespace PerfMonNext.Windows.Tests;

public sealed class LocalCatalogTests
{
    [Fact]
    public void Processor_is_multi_instance_and_includes_total()
    {
        if (!OperatingSystem.IsWindows())
            return;

        WindowsCategorySampler.RefreshMachine(".");
        var set = WindowsCategorySampler.ReadSet(".", "Processor");
        Assert.True(set.MultiInstance);
        Assert.Contains("_Total", set.Instances, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(set.Counters, counter => counter.Name.Equals("% Processor Time", StringComparison.OrdinalIgnoreCase));
        Assert.Null(set.EnumerationError);
    }

    [Fact]
    public void Memory_stays_single_instance()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var set = WindowsCategorySampler.ReadSet(".", "Memory");
        Assert.False(set.MultiInstance);
        Assert.Empty(set.Instances);
        Assert.Contains(set.Counters, counter => counter.Name.Equals("Available MBytes", StringComparison.OrdinalIgnoreCase));
        Assert.Null(set.EnumerationError);
    }

    [Fact]
    public void Hyper_v_set_is_not_treated_as_single_instance_when_present()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var catalog = new WindowsCounterCatalog(new MemoryCredentialVault());
        var machine = new MachineTarget { Id = "local", Host = ".", DisplayName = "Local" };
        var loaded = catalog.Load(machine, CancellationToken.None);
        var set = loaded.Sets.FirstOrDefault(item =>
            item.Name.Equals("Hyper-V Hypervisor Logical Processor", StringComparison.OrdinalIgnoreCase));
        if (set is null)
            return;

        Assert.True(set.MultiInstance);
        Assert.False(string.IsNullOrEmpty(set.EnumerationError) && set.Instances.Count == 0);
        if (set.Instances.Count > 0)
            Assert.Contains("_Total", set.Instances, StringComparer.OrdinalIgnoreCase);
    }
}
