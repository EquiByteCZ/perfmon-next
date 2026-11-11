using System.Diagnostics;
using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class SamplingTests
{
    [Fact]
    public void One_offline_machine_does_not_drop_the_other()
    {
        var workspace = WorkspaceFactory.CreateNew();
        workspace.Machines.Add(new MachineTarget { Id = "n2", Host = "node02", DisplayName = "node02" });
        workspace.Counters.Clear();
        workspace.Counters.Add(WorkspaceFactory.Selection("*", "Processor", "% Processor Time", "_Total"));
        var poller = new ScriptedPoller(machine =>
        {
            if (machine.Id == "n2")
                return MachineReadResult.From(machine, MachineStatus.Offline, "RPC server unavailable", DateTimeOffset.Parse("2026-11-11T09:00:00Z"));
            var path = new CounterPath("Processor", "% Processor Time", "_Total");
            return MachineReadResult.From(machine, MachineStatus.Online, null, DateTimeOffset.Parse("2026-11-11T09:00:00Z"),
                [new CounterReading(path, 15, SampleQuality.Ok, null)]);
        });
        var reported = new List<MachineReadResult>();
        new SamplingEngine(poller).Collect(workspace, new Progress<MachineReadResult>(reported.Add));
        Assert.Equal(2, reported.Count);
        Assert.Contains(reported, result => result.MachineId == "local" && result.Status == MachineStatus.Online);
        Assert.Contains(reported, result => result.MachineId == "n2" && result.Status == MachineStatus.Offline);
    }

    [Fact]
    public void A_throwing_poller_becomes_an_error_result()
    {
        var workspace = WorkspaceFactory.CreateNew();
        var poller = new ScriptedPoller(_ => throw new InvalidOperationException("registry busy"));
        var reported = new List<MachineReadResult>();
        new SamplingEngine(poller).Collect(workspace, new Progress<MachineReadResult>(reported.Add));
        var result = Assert.Single(reported);
        Assert.Equal(MachineStatus.Error, result.Status);
        Assert.Contains("registry busy", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_flight_read_does_not_start_a_second_one_and_then_returns_the_value()
    {
        var flight = new PollingFlight<int>();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var now = DateTimeOffset.Parse("2026-11-11T09:00:00Z");
        var calls = 0;
        var first = flight.Poll(() =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait();
            return 7;
        }, TimeSpan.FromSeconds(5), now);

        Assert.Equal(PollStatus.Busy, first.Status);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        var stuck = flight.Poll(() => 1, TimeSpan.FromSeconds(5), now.AddSeconds(9));
        Assert.Equal(PollStatus.Stuck, stuck.Status);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.Set();

        PollResult<int> done = default;
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(2))
        {
            done = flight.Poll(() => 9, TimeSpan.FromSeconds(5), now.AddSeconds(10));
            if (done.Status == PollStatus.Value)
                break;
            await Task.Delay(10);
        }

        Assert.Equal(PollStatus.Value, done.Status);
        Assert.Equal(7, done.Value);
    }

    [Fact]
    public async Task Guard_maps_a_fault_and_abandon_drops_a_stale_result()
    {
        var probe = new ScriptedProbe(_ => throw new InvalidOperationException("denied"));
        var guard = new GuardedCounterPoller(probe);
        var machine = new MachineTarget { Id = "local", Host = ".", DisplayName = "Local" };
        var now = DateTimeOffset.Parse("2026-11-11T09:00:00Z");
        MachineReadResult? failed = null;
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(2))
        {
            var poll = guard.Poll(machine, [], TimeSpan.FromSeconds(5), now);
            if (poll.Status == MachineStatus.Error)
            {
                failed = poll;
                break;
            }
            await Task.Delay(10);
        }

        Assert.NotNull(failed);
        Assert.Contains("denied", failed!.Message, StringComparison.Ordinal);

        var blocking = new ManualResetEventSlim(false);
        var slow = new GuardedCounterPoller(new ScriptedProbe(_ =>
        {
            blocking.Wait();
            return MachineReadResult.From(machine, MachineStatus.Online, null, now, []);
        }));
        Assert.Equal(MachineStatus.Busy, slow.Poll(machine, [], TimeSpan.FromSeconds(30), now).Status);
        slow.Abandon();
        blocking.Set();
        await Task.Delay(50);
        var after = slow.Poll(machine, [], TimeSpan.FromSeconds(30), now.AddSeconds(1));
        Assert.NotEqual(MachineStatus.Online, after.Status);
    }

    private sealed class ScriptedPoller : ICounterPoller
    {
        private readonly Func<MachineTarget, MachineReadResult> _handle;
        public ScriptedPoller(Func<MachineTarget, MachineReadResult> handle) => _handle = handle;
        public MachineReadResult Poll(MachineTarget machine, IReadOnlyList<CounterPath> paths, TimeSpan stuckAfter, DateTimeOffset now) => _handle(machine);
        public void Abandon() { }
        public void Release(string machineId) { }
    }

    private sealed class ScriptedProbe : ICounterProbe
    {
        private readonly Func<MachineTarget, MachineReadResult> _handle;
        public ScriptedProbe(Func<MachineTarget, MachineReadResult> handle) => _handle = handle;
        public MachineReadResult Read(MachineTarget machine, IReadOnlyList<CounterPath> paths) => _handle(machine);
    }
}

public sealed class CatalogAndChartTests
{
    [Fact]
    public void Search_requires_every_token_and_keeps_cluster_counters()
    {
        var catalog = StarterCatalog.Create();
        var csv = CatalogSearch.Filter(catalog, "csv latency");
        Assert.Contains(csv, set => set.Name == "Cluster CSV File System");
        Assert.DoesNotContain(csv, set => set.Name == "Processor");
        var cpu = CatalogSearch.Filter(catalog, "% processor");
        Assert.Contains(cpu, set => set.Name == "Processor");
        Assert.DoesNotContain(cpu.Single(set => set.Name == "Processor").Counters, counter => counter.Name == "Interrupts/sec");
    }

    [Fact]
    public void Frames_split_by_counter_and_nulls_become_gaps()
    {
        var left = Snapshot("node01", "Processor", "% Processor Time", "_Total", 10, null, 12);
        var right = Snapshot("node02", "Processor", "% Processor Time", "_Total", 20, 21, 22);
        var memory = Snapshot("node01", "Memory", "Available MBytes", "", 100);
        var frames = ChartFrames.Build([left, right, memory], ChartGrouping.ByCounter, true, TimeSpan.FromMinutes(5));
        Assert.Equal(2, frames.Count);
        var cpu = frames.Single(frame => frame.Title.Contains("Processor", StringComparison.Ordinal));
        Assert.Equal(2, cpu.Series.Count);
        Assert.Contains(cpu.Series[0].Ys, double.IsNaN);
        Assert.True(cpu.XMax - cpu.XMin > TimeSpan.FromMinutes(4).TotalDays);
        Assert.Contains("node01", ChartFrames.Legend(left, ChartGrouping.ByCounter), StringComparison.Ordinal);
    }

    private static SeriesSnapshot Snapshot(string machine, string category, string counter, string instance, params double?[] values)
    {
        var start = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
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

public sealed class PersistenceTests
{
    [Fact]
    public void Csv_roundtrip_quotes_commas_and_keeps_gaps()
    {
        var path = new CounterPath("Process", "A, B", "w3wp");
        var samples = new List<SamplePoint>
        {
            new(DateTimeOffset.Parse("2026-11-11T09:00:00.0000000Z"), 1.5),
            new(DateTimeOffset.Parse("2026-11-11T09:00:01.0000000Z"), null)
        };
        var snapshot = new SeriesSnapshot
        {
            Key = "k",
            OverrideKey = "o",
            MachineId = "n2",
            MachineDisplay = "node02",
            Host = "node02",
            Path = path,
            SelectionId = null,
            Color = "#fff",
            Scale = 1,
            Visible = true,
            Samples = samples,
            Stats = SeriesStats.FromSamples(samples, 1)
        };
        var imported = CsvExchange.Import(CsvExchange.Export([snapshot]));
        var series = Assert.Single(imported.Series);
        Assert.Equal("A, B", series.Counter);
        Assert.Equal(1.5, series.Samples[0].Value);
        Assert.Null(series.Samples[1].Value);
    }

    [Fact]
    public void Pdh_csv_imports_paths_and_skips_blanks()
    {
        const string csv = """
            "(PDH-CSV 4.0) (UTC)(0)","\\node01\Processor(_Total)\% Processor Time","\\node02\Memory\Available MBytes"
            "11/11/2026 09:00:01.000","12.5","4096"
            "11/11/2026 09:00:02.000"," ","2048"
            """;
        var imported = CsvExchange.Import(csv);
        Assert.Equal(2, imported.Series.Count);
        var cpu = imported.Series.Single(series => series.Counter == "% Processor Time");
        Assert.Equal("node01", cpu.Machine);
        Assert.Equal("_Total", cpu.Instance);
        Assert.Equal(12.5, cpu.Samples[0].Value);
        Assert.Null(cpu.Samples[1].Value);
        Assert.Equal(2048, imported.Series.Single(series => series.Category == "Memory").Samples[1].Value);
    }

    [Fact]
    public void Blg_detection_and_relog_arguments()
    {
        Assert.True(BlgImportPlanner.LooksLikeBlg("node.blg", "{ not csv"u8));
        Assert.True(BlgImportPlanner.LooksLikeBlg("data.bin", "xxxxPDH-BIN"u8));
        Assert.False(BlgImportPlanner.LooksLikeBlg("data.csv", "(PDH-CSV 4.0)"u8));
        var plan = BlgImportPlanner.Create(@"C:\Logs\cluster cpu.blg");
        Assert.Equal("CSV", plan.Arguments[2]);
        Assert.Equal("-y", plan.Arguments[^1]);
        Assert.EndsWith("cluster cpu.csv", plan.CsvPath, StringComparison.Ordinal);
        Assert.Contains("\"C:\\Logs\\cluster cpu.blg\"", plan.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Sample_workspace_loads()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "azure-local-cluster.workspace.json");
        var loaded = WorkspaceStore.Load(path);
        Assert.Equal(3, loaded.Workspace.Machines.Count);
        Assert.Contains(loaded.Workspace.Counters, counter => counter.Instance == "*" && counter.Category.Contains("CSV", StringComparison.Ordinal));
        Assert.Equal(1000, loaded.Workspace.Counters.Single(counter => counter.Id == "csv").Scale);
        Assert.DoesNotContain("password", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Credential_file_roundtrips_through_a_test_protector()
    {
        var vault = new MemoryCredentialVault();
        vault.Set("n2", @"CONTOSO\jaromir", "s3cret");
        var copy = new MemoryCredentialVault();
        copy.ImportProtected(vault.ExportProtected(new ReverseProtector()), new ReverseProtector());
        Assert.True(copy.TryGet("n2", out var user, out var password));
        Assert.Equal(@"CONTOSO\jaromir", user);
        Assert.Equal("s3cret", password);
    }

    private sealed class ReverseProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext)
        {
            var copy = plaintext.ToArray();
            Array.Reverse(copy);
            return copy;
        }

        public byte[] Unprotect(byte[] protectedBytes) => Protect(protectedBytes);
    }
}
