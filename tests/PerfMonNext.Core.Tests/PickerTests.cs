using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class PickerTests
{
    [Fact]
    public void A_name_only_set_still_needs_its_counters_loaded()
    {
        var set = new CounterSetInfo { Name = "Hyper-V Hypervisor Logical Processor", DetailsLoaded = false, MultiInstance = true };
        Assert.True(CounterSetBuilder.NeedsDetailLoad(set));
        Assert.Empty(set.Counters);
        Assert.Empty(set.Instances);
    }

    [Fact]
    public void Counter_names_are_kept_when_the_only_instance_list_was_empty()
    {
        var set = CounterSetBuilder.Build(
            "Hyper-V Hypervisor Logical Processor",
            "Logical processors seen by the hypervisor.",
            [
                new("% Total Run Time", "_Total"),
                new("% Total Run Time", "0"),
                new("% Guest Run Time", "_Total"),
                new("% Guest Run Time", "0")
            ],
            multiHint: true);

        Assert.False(CounterSetBuilder.NeedsDetailLoad(set));
        Assert.Equal(["% Guest Run Time", "% Total Run Time"], set.Counters.Select(counter => counter.Name));
        Assert.Equal(["_Total", "0"], set.Instances);
        Assert.True(set.MultiInstance);
    }

    [Fact]
    public void WinRm_list_parses_counter_paths_and_instances()
    {
        const string json = """
            [{"name":"Hyper-V Hypervisor Logical Processor","help":"Hypervisor logical processors","counters":["\\Hyper-V Hypervisor Logical Processor(*)\\% Total Run Time"],"paths":["\\Hyper-V Hypervisor Logical Processor(_Total)\\% Total Run Time","\\Hyper-V Hypervisor Logical Processor(0)\\% Total Run Time"]}]
            """;
        var catalog = WinRmJson.ParseSets(json, "s2d2");
        var set = Assert.Single(catalog.Sets);
        Assert.Equal("% Total Run Time", Assert.Single(set.Counters).Name);
        Assert.Equal(["_Total", "0"], set.Instances);
        Assert.True(set.DetailsLoaded);
    }

    [Fact]
    public void WinRm_samples_and_remote_script_use_invoke_command()
    {
        var samples = WinRmJson.ParseSamples("""[{"path":"\\\\s2d2\\Processor(_Total)\\% Processor Time","value":12.5}]""");
        var sample = Assert.Single(samples);
        Assert.Equal("Processor", sample.Path.Category);
        Assert.Equal("_Total", sample.Path.Instance);
        Assert.Equal(12.5, sample.Value);

        var script = WinRmScripts.Sample("s2d2", [@"\Processor(_Total)\% Processor Time"], useCredential: true);
        Assert.Contains("Invoke-Command -ComputerName 's2d2'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Counter -ComputerName", script, StringComparison.Ordinal);
        Assert.Contains("$cred", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Pdh_failure_on_a_remote_machine_switches_to_winrm()
    {
        var machine = new MachineTarget { Id = "s2", Host = "s2d2", DisplayName = "s2d2" };
        var path = new CounterPath("Processor", "% Processor Time", "_Total");
        var pdh = new StubProbe(_ => MachineReadResult.From(machine, MachineStatus.AccessDenied, "Access is denied.", DateTimeOffset.Parse("2026-11-11T10:00:00Z")));
        var winrm = new StubProbe(_ => MachineReadResult.From(machine, MachineStatus.Online, null, DateTimeOffset.Parse("2026-11-11T10:00:01Z"),
            [new CounterReading(path, 4, SampleQuality.Ok, null)]));
        var router = new RoutingCounterProbe(pdh, winrm);

        var first = router.Read(machine, [path]);
        Assert.Equal(MachineStatus.Online, first.Status);
        Assert.Equal(ConnectionMode.WinRm, machine.Connection);
        Assert.Contains("WinRM", first.Message, StringComparison.Ordinal);

        var second = router.Read(machine, [path]);
        Assert.Equal(1, pdh.Calls);
        Assert.Equal(2, winrm.Calls);
        Assert.Equal(4, Assert.Single(second.Readings).Value);
    }

    [Fact]
    public void Bars_use_the_latest_finite_value_and_stats_include_duration()
    {
        var start = DateTimeOffset.Parse("2026-11-11T10:00:00Z");
        var samples = new List<SamplePoint>
        {
            new(start, 10),
            new(start.AddSeconds(30), 4),
            new(start.AddSeconds(60), null)
        };
        var stats = SeriesStats.FromSamples(samples, 1);
        Assert.Null(stats.Last);
        Assert.Equal(TimeSpan.FromSeconds(60), stats.Duration);
        Assert.Equal("1:00", ValueFormat.FormatDuration(stats.Duration));

        var snapshot = new SeriesSnapshot
        {
            Key = "k",
            OverrideKey = "o",
            MachineId = "s2",
            MachineDisplay = "s2d2",
            Host = "s2d2",
            Path = new CounterPath("Processor", "% Processor Time", "_Total"),
            SelectionId = null,
            Color = "#4C9AFF",
            Scale = 1,
            Visible = true,
            Samples = samples,
            Stats = stats
        };
        Assert.Equal(4, ChartFrames.LastFinite(snapshot));
        var frame = Assert.Single(ChartFrames.BuildBars([snapshot], ChartGrouping.ByCounter, true));
        Assert.True(frame.IsBar);
        Assert.Equal(4, Assert.Single(frame.Bars).Value);
    }

    private sealed class StubProbe : ICounterProbe
    {
        private readonly Func<MachineTarget, MachineReadResult> _handle;
        public int Calls { get; private set; }
        public StubProbe(Func<MachineTarget, MachineReadResult> handle) => _handle = handle;
        public MachineReadResult Read(MachineTarget machine, IReadOnlyList<CounterPath> paths)
        {
            Calls++;
            return _handle(machine);
        }
    }
}
