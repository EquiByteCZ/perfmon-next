using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class SeriesErrorTests
{
    [Fact]
    public void Missing_reading_surfaces_the_error_on_the_snapshot()
    {
        var workspace = WorkspaceFactory.CreateNew();
        var machine = workspace.Machines[0];
        var path = new CounterPath("Hyper-V Hypervisor Logical Processor", "% Total Run Time", "");
        workspace.Counters.Add(new CounterSelection
        {
            MachineId = machine.Id,
            Category = path.Category,
            Counter = path.Counter,
            Instance = ""
        });

        var store = new SeriesStore();
        var at = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        store.Apply(
            MachineReadResult.From(machine, MachineStatus.Online, null, at,
            [
                new CounterReading(path, null, SampleQuality.Missing, "This counter is multi-instance and was added without an instance. Select _Total or another instance.")
            ]),
            workspace);

        var snapshot = Assert.Single(store.Snapshot(workspace, null));
        Assert.Null(snapshot.Stats.Last);
        Assert.Equal(
            "This counter is multi-instance and was added without an instance. Select _Total or another instance.",
            snapshot.LastError);
    }

    [Fact]
    public void Successful_reading_clears_the_previous_error()
    {
        var workspace = WorkspaceFactory.CreateNew();
        var machine = workspace.Machines[0];
        var path = new CounterPath("Processor", "% Processor Time", "_Total");
        workspace.Counters.Add(new CounterSelection
        {
            MachineId = machine.Id,
            Category = path.Category,
            Counter = path.Counter,
            Instance = "_Total"
        });

        var store = new SeriesStore();
        var at = DateTimeOffset.Parse("2026-11-12T12:00:00Z");
        store.Apply(
            MachineReadResult.From(machine, MachineStatus.Online, null, at,
            [new CounterReading(path, null, SampleQuality.Missing, "Access denied.")]),
            workspace);
        store.Apply(
            MachineReadResult.From(machine, MachineStatus.Online, null, at.AddSeconds(1),
            [new CounterReading(path, 12.5, SampleQuality.Ok, null)]),
            workspace);

        var snapshot = Assert.Single(store.Snapshot(workspace, null));
        Assert.Equal(12.5, snapshot.Stats.Last);
        Assert.Null(snapshot.LastError);
    }
}
