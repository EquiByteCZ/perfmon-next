using System.Text.Json.Serialization;

namespace PerfMonNext.Core;

public enum ChartGrouping
{
    Combined,
    ByMachine,
    ByCounter
}

public enum CredentialMode
{
    Current,
    Alternate
}

public sealed class MachineCredential
{
    public CredentialMode Mode { get; set; } = CredentialMode.Current;
    public string? UserName { get; set; }
}

public sealed class MachineTarget
{
    public string Id { get; set; } = Ids.New("m");
    public string Host { get; set; } = ".";
    public string DisplayName { get; set; } = "Local";
    public MachineCredential Credential { get; set; } = new();
}

public sealed class CounterSelection
{
    public string Id { get; set; } = Ids.New("c");
    public string MachineId { get; set; } = "*";
    public string Category { get; set; } = "";
    public string Counter { get; set; } = "";
    public string Instance { get; set; } = "";
    public double Scale { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public string? Color { get; set; }
}

public sealed class Workspace
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled";
    public int SampleIntervalMs { get; set; } = 1000;
    public int HistoryCapacity { get; set; } = 1800;
    public ChartGrouping ChartGrouping { get; set; } = ChartGrouping.ByCounter;
    public int VisibleWindowSeconds { get; set; } = 300;
    public bool FollowLive { get; set; } = true;
    public bool ShowLegend { get; set; } = true;
    public List<MachineTarget> Machines { get; set; } = [];
    public List<CounterSelection> Counters { get; set; } = [];
    public Dictionary<string, string> ColorOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> ScaleOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> Visibility { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public TimeSpan? VisibleWindow => VisibleWindowSeconds <= 0 ? null : TimeSpan.FromSeconds(VisibleWindowSeconds);
}

public static class WorkspaceFactory
{
    public static Workspace CreateNew()
    {
        return new Workspace
        {
            Name = "Local overview",
            Machines = [new MachineTarget { Id = "local", Host = ".", DisplayName = "Local" }],
            Counters =
            [
                Selection("*", "Processor", "% Processor Time", "_Total"),
                Selection("*", "Memory", "Available MBytes", ""),
                Selection("*", "System", "Processor Queue Length", "")
            ]
        };
    }

    public static CounterSelection Selection(string machineId, string category, string counter, string instance) =>
        new()
        {
            MachineId = machineId,
            Category = category,
            Counter = counter,
            Instance = instance
        };
}

public static class WorkspaceSanitizer
{
    public const int MinIntervalMs = 200;
    public const int MaxIntervalMs = 600_000;
    public const int MinHistory = 30;
    public const int MaxHistory = 100_000;

    public static IReadOnlyList<string> Sanitize(Workspace workspace)
    {
        var warnings = new List<string>();
        if (workspace.Version <= 0)
            workspace.Version = 1;

        if (workspace.SampleIntervalMs < MinIntervalMs)
        {
            workspace.SampleIntervalMs = MinIntervalMs;
            warnings.Add("Sample interval was raised to 200 ms.");
        }
        else if (workspace.SampleIntervalMs > MaxIntervalMs)
        {
            workspace.SampleIntervalMs = MaxIntervalMs;
            warnings.Add("Sample interval was capped at 10 minutes.");
        }

        if (workspace.HistoryCapacity < MinHistory)
            workspace.HistoryCapacity = MinHistory;
        else if (workspace.HistoryCapacity > MaxHistory)
            workspace.HistoryCapacity = MaxHistory;

        if (string.IsNullOrWhiteSpace(workspace.Name))
            workspace.Name = "Untitled";

        workspace.Machines ??= [];
        workspace.Counters ??= [];
        workspace.ColorOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        workspace.ScaleOverrides ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        workspace.Visibility ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = workspace.Machines.Count - 1; i >= 0; i--)
        {
            var machine = workspace.Machines[i];
            if (string.IsNullOrWhiteSpace(machine.Id) || !seenIds.Add(machine.Id))
            {
                workspace.Machines.RemoveAt(i);
                warnings.Add("Removed a machine with a missing or duplicate id.");
                continue;
            }

            machine.Host = string.IsNullOrWhiteSpace(machine.Host) ? "." : machine.Host.Trim();
            machine.DisplayName = MachineHost.Display(machine.Host, machine.DisplayName);
            machine.Credential ??= new MachineCredential();
        }

        if (workspace.Machines.Count == 0)
        {
            workspace.Machines.Add(new MachineTarget { Id = "local", Host = ".", DisplayName = "Local" });
            warnings.Add("No machines were listed. Added the local computer.");
        }

        workspace.Counters.RemoveAll(c => string.IsNullOrWhiteSpace(c.Category) || string.IsNullOrWhiteSpace(c.Counter));
        foreach (var counter in workspace.Counters)
        {
            if (string.IsNullOrWhiteSpace(counter.Id))
                counter.Id = Ids.New("c");
            if (string.IsNullOrWhiteSpace(counter.MachineId))
                counter.MachineId = "*";
            counter.Instance ??= "";
            if (!double.IsFinite(counter.Scale) || counter.Scale == 0)
                counter.Scale = 1;
        }

        return warnings;
    }
}

public static class WorkspaceEditor
{
    public static CounterSelection AddCounterPath(Workspace workspace, string pathText, string scopeMachineId)
    {
        var path = CounterPathParser.Parse(pathText);
        var machineId = scopeMachineId;
        if (!string.IsNullOrWhiteSpace(path.Machine))
        {
            var host = MachineHost.Normalize(path.Machine);
            var existing = workspace.Machines.FirstOrDefault(m =>
                string.Equals(MachineHost.Normalize(m.Host), host, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new MachineTarget
                {
                    Id = Ids.New("m"),
                    Host = MachineHost.IsLocal(path.Machine) ? "." : path.Machine.Trim(),
                    DisplayName = MachineHost.Display(path.Machine, null)
                };
                workspace.Machines.Add(existing);
            }

            machineId = existing.Id;
        }

        var created = new CounterSelection
        {
            MachineId = machineId,
            Category = path.Category,
            Counter = path.Counter,
            Instance = path.Instance
        };
        var duplicate = workspace.Counters.FirstOrDefault(c => SameSelection(c, created));
        if (duplicate is not null)
            return duplicate;

        workspace.Counters.Add(created);
        return created;
    }

    public static void SetInstances(
        Workspace workspace,
        string scopeMachineId,
        string category,
        string counter,
        IReadOnlyList<string> instances,
        double? scale = null,
        string? color = null)
    {
        var previous = workspace.Counters.FirstOrDefault(c => MatchesCounter(c, scopeMachineId, category, counter));
        workspace.Counters.RemoveAll(c => MatchesCounter(c, scopeMachineId, category, counter));
        foreach (var instance in instances.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            workspace.Counters.Add(new CounterSelection
            {
                MachineId = scopeMachineId,
                Category = category,
                Counter = counter,
                Instance = instance,
                Scale = scale ?? previous?.Scale ?? 1,
                Color = color ?? previous?.Color,
                Enabled = previous?.Enabled ?? true
            });
        }
    }

    public static string? DefaultInstance(CounterSetInfo set)
    {
        if (set.Instances.Any(i => i.Equals("_Total", StringComparison.OrdinalIgnoreCase)))
            return "_Total";
        if (set.Instances.Count > 0)
            return "*";
        if (set.MultiInstance)
            return null;
        return "";
    }

    public static bool HasCounter(Workspace workspace, string scopeMachineId, string category, string counter) =>
        workspace.Counters.Any(c => c.Enabled && MatchesCounter(c, scopeMachineId, category, counter));

    public static IReadOnlyList<string> InstancesFor(Workspace workspace, string scopeMachineId, string category, string counter) =>
        workspace.Counters
            .Where(c => c.Enabled && MatchesCounter(c, scopeMachineId, category, counter))
            .Select(c => c.Instance)
            .ToList();

    public static IReadOnlyList<string> ToggleInstance(IReadOnlyList<string> current, string instance, bool enable)
    {
        var next = current.ToList();
        if (enable)
        {
            if (instance == "*")
                return ["*"];
            next.RemoveAll(value => value == "*");
            if (!next.Contains(instance, StringComparer.OrdinalIgnoreCase))
                next.Add(instance);
            return next;
        }

        next.RemoveAll(value => string.Equals(value, instance, StringComparison.OrdinalIgnoreCase));
        return next;
    }

    public static void RemoveMachine(Workspace workspace, string machineId)
    {
        workspace.Machines.RemoveAll(m => m.Id == machineId);
        workspace.Counters.RemoveAll(c => c.MachineId == machineId);
    }

    public static bool SameSelection(CounterSelection left, CounterSelection right) =>
        string.Equals(left.MachineId, right.MachineId, StringComparison.Ordinal)
        && NameEquals(left.Category, right.Category)
        && NameEquals(left.Counter, right.Counter)
        && string.Equals(left.Instance ?? "", right.Instance ?? "", StringComparison.OrdinalIgnoreCase);

    public static bool MatchesCounter(CounterSelection selection, string scopeMachineId, string category, string counter) =>
        string.Equals(selection.MachineId, scopeMachineId, StringComparison.Ordinal)
        && NameEquals(selection.Category, category)
        && NameEquals(selection.Counter, counter);

    public static bool NameEquals(string? left, string? right) =>
        string.Equals(left?.Trim() ?? "", right?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
}

public static class SeriesIdentity
{
    public static string Key(string? host, CounterPath path) =>
        string.Join('\u001f', MachineHost.Normalize(host), path.Category, path.Counter, path.Instance ?? "");

    public static string OverrideKey(string? host, CounterPath path) =>
        string.Join("|", MachineHost.Normalize(host), path.Category, path.Counter, path.Instance ?? "");
}
