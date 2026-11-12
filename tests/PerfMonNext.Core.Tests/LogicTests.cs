using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class CounterPathTests
{
    [Theory]
    [InlineData(@"\Processor(_Total)\% Processor Time", null, "Processor", "_Total", "% Processor Time")]
    [InlineData(@"\\node01\Processor(_Total)\% Processor Time", "node01", "Processor", "_Total", "% Processor Time")]
    [InlineData(@"\Memory\Available MBytes", null, "Memory", "", "Available MBytes")]
    [InlineData(@"\PhysicalDisk(0 C:)\Disk Transfers/sec", null, "PhysicalDisk", "0 C:", "Disk Transfers/sec")]
    [InlineData(@"\Process(w3wp#2)\% Processor Time", null, "Process", "w3wp#2", "% Processor Time")]
    [InlineData(@"\Processor Information(_Total)\% Processor Utility", null, "Processor Information", "_Total", "% Processor Utility")]
    [InlineData(@"\\node02.contoso.local\Thread(winlogon/0)\Context Switches/sec", "node02.contoso.local", "Thread", "winlogon/0", "Context Switches/sec")]
    public void Parses_standard_paths(string text, string? machine, string category, string instance, string counter)
    {
        var path = CounterPathParser.Parse(text);
        Assert.Equal(machine, path.Machine);
        Assert.Equal(category, path.Category);
        Assert.Equal(instance, path.Instance);
        Assert.Equal(counter, path.Counter);
        Assert.Equal(text.Replace(@"\\node02.contoso.local", @"\\node02.contoso.local"), path.Format());
    }

    [Fact]
    public void Format_roundtrips_without_a_machine()
    {
        var path = new CounterPath("LogicalDisk", "Free Megabytes", "C:");
        Assert.Equal(@"\LogicalDisk(C:)\Free Megabytes", path.Format());
        Assert.Equal(path, CounterPathParser.Parse(path.Format()) with { Machine = null });
    }

    [Fact]
    public void Rejects_a_path_with_no_counter()
    {
        Assert.Throws<FormatException>(() => CounterPathParser.Parse(@"\Processor"));
    }

    [Theory]
    [InlineData("*", "chrome", true)]
    [InlineData("chrome*", "chrome#2", true)]
    [InlineData("chrome*", "Code", false)]
    [InlineData("_Total", "_total", true)]
    [InlineData("", "", true)]
    [InlineData("", "0", false)]
    public void Instance_patterns(string pattern, string instance, bool expected)
    {
        Assert.Equal(expected, InstanceMatcher.Matches(pattern, instance));
    }
}

public sealed class WorkspaceTests
{
    [Fact]
    public void Roundtrip_keeps_machines_counters_and_overrides()
    {
        var workspace = WorkspaceFactory.CreateNew();
        workspace.Name = "Cluster CPU";
        workspace.ChartGrouping = ChartGrouping.ByMachine;
        workspace.Machines.Add(new MachineTarget
        {
            Id = "n2",
            Host = "node02",
            DisplayName = "node02",
            Credential = new MachineCredential { Mode = CredentialMode.Alternate, UserName = @"CONTOSO\jaromir" }
        });
        workspace.ColorOverrides[".|Processor|% Processor Time|_Total"] = "#F07178";
        var loaded = WorkspaceStore.Deserialize(WorkspaceStore.Serialize(workspace)).Workspace;
        Assert.Equal("Cluster CPU", loaded.Name);
        Assert.Equal(ChartGrouping.ByMachine, loaded.ChartGrouping);
        Assert.Equal(@"CONTOSO\jaromir", loaded.Machines.Single(m => m.Id == "n2").Credential.UserName);
        Assert.Equal(CredentialMode.Alternate, loaded.Machines.Single(m => m.Id == "n2").Credential.Mode);
        Assert.Equal("#F07178", loaded.ColorOverrides[".|Processor|% Processor Time|_Total"]);
        Assert.Contains("byMachine", WorkspaceStore.Serialize(workspace), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_showCharts_defaults_to_on_and_table_only_roundtrips()
    {
        var legacy = WorkspaceStore.Deserialize("""{"version": 1, "name": "Legacy", "machines": [{"id": "local", "host": ".", "displayName": "Local"}]}""").Workspace;
        Assert.True(legacy.ChartsEnabled);
        Assert.Null(legacy.ShowCharts);

        var workspace = WorkspaceFactory.CreateNew();
        workspace.ChartsEnabled = false;
        var json = WorkspaceStore.Serialize(workspace);
        Assert.Contains("\"showCharts\": false", json, StringComparison.Ordinal);
        var loaded = WorkspaceStore.Deserialize(json).Workspace;
        Assert.False(loaded.ChartsEnabled);
    }

    [Fact]
    public void Password_in_json_is_ignored()
    {
        const string json = """
            {
              "version": 1,
              "name": "Secret",
              "machines": [
                {
                  "id": "n2",
                  "host": "node02",
                  "displayName": "node02",
                  "credential": { "mode": "alternate", "userName": "CONTOSO\\jaromir", "password": "do-not-keep" }
                }
              ]
            }
            """;
        var loaded = WorkspaceStore.Deserialize(json);
        Assert.Contains(loaded.Warnings, warning => warning.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"CONTOSO\jaromir", loaded.Workspace.Machines[0].Credential.UserName);
        Assert.DoesNotContain("do-not-keep", WorkspaceStore.Serialize(loaded.Workspace), StringComparison.Ordinal);
    }

    [Fact]
    public void Newer_version_is_rejected()
    {
        var ex = Assert.Throws<WorkspaceFormatException>(() => WorkspaceStore.Deserialize("""{"version": 9}"""));
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Star_selection_expands_to_every_machine_and_a_specific_one_does_not()
    {
        var workspace = WorkspaceFactory.CreateNew();
        workspace.Machines.Add(new MachineTarget { Id = "n2", Host = "node02", DisplayName = "node02" });
        workspace.Counters.Clear();
        workspace.Counters.Add(WorkspaceFactory.Selection("*", "Processor", "% Processor Time", "_Total"));
        workspace.Counters.Add(WorkspaceFactory.Selection("n2", "Memory", "Available MBytes", ""));
        var queries = SelectionExpander.Expand(workspace);
        Assert.Equal(2, queries.Count);
        Assert.Contains(queries.Single(q => q.Machine.Id == "local").Paths, path => path.Counter == "% Processor Time");
        Assert.DoesNotContain(queries.Single(q => q.Machine.Id == "local").Paths, path => path.Counter == "Available MBytes");
        Assert.Equal(2, queries.Single(q => q.Machine.Id == "n2").Paths.Count);
    }

    [Fact]
    public void Add_path_creates_a_machine_and_checking_star_clears_specific_instances()
    {
        var workspace = WorkspaceFactory.CreateNew();
        workspace.Counters.Clear();
        var added = WorkspaceEditor.AddCounterPath(workspace, @"\\node02\LogicalDisk(C:)\% Free Space", "*");
        Assert.Equal("node02", workspace.Machines.Single(m => m.Id == added.MachineId).Host);
        Assert.Equal("C:", added.Instance);
        var toggled = WorkspaceEditor.ToggleInstance(["C:", "D:"], "*", true);
        Assert.Equal(["*"], toggled);
        var cleared = WorkspaceEditor.ToggleInstance(["*"], "*", false);
        Assert.Empty(cleared);
    }

    [Fact]
    public void Local_aliases_share_a_series_key()
    {
        var path = new CounterPath("Memory", "Available MBytes", "");
        Assert.Equal(SeriesIdentity.Key("localhost", path), SeriesIdentity.Key(".", path));
        Assert.True(MachineHost.IsLocal("(local)"));
    }
}

public sealed class SeriesTests
{
    [Fact]
    public void Ring_drops_the_oldest_sample_and_stats_follow_the_window()
    {
        var ring = new SampleRing(3);
        var start = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
        ring.Append(new SamplePoint(start, 1));
        ring.Append(new SamplePoint(start.AddSeconds(1), 5));
        ring.Append(new SamplePoint(start.AddSeconds(2), 3));
        ring.Append(new SamplePoint(start.AddSeconds(3), 4));
        var copy = ring.Copy();
        Assert.Equal([5d, 3d, 4d], copy.Select(point => point.Value));
        var window = ring.Copy(start.AddSeconds(3));
        var stats = SeriesStats.FromSamples(window, 10);
        Assert.Equal(40, stats.Last);
        Assert.Equal(40, stats.Min);
        Assert.Equal(1, stats.Count);
    }

    [Fact]
    public void Gaps_do_not_move_min_and_the_latest_gap_clears_last()
    {
        var samples = new List<SamplePoint>
        {
            new(DateTimeOffset.Parse("2026-11-11T09:00:00Z"), 10),
            new(DateTimeOffset.Parse("2026-11-11T09:00:01Z"), null),
            new(DateTimeOffset.Parse("2026-11-11T09:00:02Z"), 4)
        };
        var stats = SeriesStats.FromSamples(samples, 1);
        Assert.Equal(4, stats.Last);
        Assert.Equal(4, stats.Min);
        Assert.Equal(10, stats.Max);
        Assert.Equal(7, stats.Average);
        samples.Add(new SamplePoint(DateTimeOffset.Parse("2026-11-11T09:00:03Z"), null));
        Assert.Null(SeriesStats.FromSamples(samples, 1).Last);
    }

    [Fact]
    public void Store_skips_warmup_gaps_a_down_machine_and_prunes()
    {
        var workspace = WorkspaceFactory.CreateNew();
        workspace.Counters.Clear();
        workspace.Counters.Add(WorkspaceFactory.Selection("*", "Processor", "% Processor Time", "_Total"));
        var store = new SeriesStore();
        var machine = workspace.Machines[0];
        var path = new CounterPath("Processor", "% Processor Time", "_Total");
        var t0 = DateTimeOffset.Parse("2026-11-11T09:00:00Z");
        store.Apply(MachineReadResult.From(machine, MachineStatus.Online, null, t0,
            [new CounterReading(path, null, SampleQuality.Warmup, null)]), workspace);
        Assert.Empty(store.Snapshot(workspace, null));

        store.Apply(MachineReadResult.From(machine, MachineStatus.Online, null, t0.AddSeconds(1),
            [new CounterReading(path, 12, SampleQuality.Ok, null)]), workspace);
        store.Apply(MachineReadResult.From(machine, MachineStatus.Offline, "RPC unavailable", t0.AddSeconds(2)), workspace);
        var snapshot = Assert.Single(store.Snapshot(workspace, null));
        Assert.Equal(12, snapshot.Stats.Max);
        Assert.Null(snapshot.Samples[^1].Value);
        Assert.Equal(2, snapshot.Samples.Count);

        workspace.Counters.Clear();
        Assert.Empty(store.Snapshot(workspace, null));
    }

    [Fact]
    public void Scale_override_changes_displayed_stats_but_not_raw_max()
    {
        var workspace = WorkspaceFactory.CreateNew();
        workspace.Counters.Clear();
        workspace.Counters.Add(WorkspaceFactory.Selection("local", "Memory", "Available MBytes", ""));
        var store = new SeriesStore();
        var path = new CounterPath("Memory", "Available MBytes", "");
        store.Apply(MachineReadResult.From(workspace.Machines[0], MachineStatus.Online, null, DateTimeOffset.Parse("2026-11-11T09:00:00Z"),
            [new CounterReading(path, 2048, SampleQuality.Ok, null)]), workspace);
        var key = SeriesIdentity.OverrideKey(".", path);
        workspace.ScaleOverrides[key] = 0.001;
        var snapshot = Assert.Single(store.Snapshot(workspace, null));
        Assert.Equal(2.048, snapshot.Stats.Last!.Value, 3);
        Assert.Equal(2048, snapshot.Stats.RawMaxAbs);
        Assert.Equal(1e-5, ScaleSuggester.Suggest(5_000_000), 8);
        Assert.Equal(1, ScaleSuggester.Suggest(80));
        Assert.Equal(0.1, ScaleSuggester.Suggest(250));
        Assert.Equal(1000, ScaleSuggester.Suggest(0.05));
    }
}
