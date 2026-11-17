namespace PerfMonNext.Core;

public readonly record struct SamplePoint(DateTimeOffset Timestamp, double? Value);

public readonly record struct SeriesStats(double? Last, double? Min, double? Max, double? Average, int Count, double? RawMaxAbs)
{
    public static SeriesStats FromSamples(IReadOnlyList<SamplePoint> samples, double scale)
    {
        double? last = null;
        double min = 0, max = 0, sum = 0;
        double rawMax = 0;
        var count = 0;
        var rawCount = 0;
        if (samples.Count > 0 && samples[^1].Value is double latest && double.IsFinite(latest))
            last = latest * scale;

        foreach (var sample in samples)
        {
            if (sample.Value is not double raw || !double.IsFinite(raw))
                continue;
            var abs = Math.Abs(raw);
            if (rawCount == 0 || abs > rawMax)
                rawMax = abs;
            rawCount++;
            var value = raw * scale;
            if (count == 0)
                min = max = value;
            else
            {
                if (value < min) min = value;
                if (value > max) max = value;
            }
            sum += value;
            count++;
        }

        if (count == 0)
            return new SeriesStats(null, null, null, null, 0, null);
        return new SeriesStats(last, min, max, sum / count, count, rawMax);
    }
}

public sealed class SampleRing
{
    private SamplePoint[] _items;
    private int _head;
    private int _count;

    public SampleRing(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new SamplePoint[capacity];
    }

    public int Capacity => _items.Length;
    public int Count => _count;

    public DateTimeOffset? Newest
    {
        get
        {
            if (_count == 0)
                return null;
            var index = (_head + _count - 1) % _items.Length;
            return _items[index].Timestamp;
        }
    }

    public void Append(SamplePoint point)
    {
        if (_count < _items.Length)
        {
            _items[(_head + _count) % _items.Length] = point;
            _count++;
            return;
        }

        _items[_head] = point;
        _head = (_head + 1) % _items.Length;
    }

    public List<SamplePoint> Copy(DateTimeOffset? notBefore = null)
    {
        var dest = new List<SamplePoint>(_count);
        for (var i = 0; i < _count; i++)
        {
            var point = _items[(_head + i) % _items.Length];
            if (notBefore is null || point.Timestamp >= notBefore.Value)
                dest.Add(point);
        }

        return dest;
    }

    public void EnsureCapacity(int capacity)
    {
        capacity = Math.Max(1, capacity);
        if (capacity == _items.Length)
            return;
        var copy = Copy();
        if (copy.Count > capacity)
            copy.RemoveRange(0, copy.Count - capacity);
        _items = new SamplePoint[capacity];
        _head = 0;
        _count = 0;
        foreach (var point in copy)
            Append(point);
    }
}

public sealed class SeriesSnapshot
{
    public required string Key { get; init; }
    public required string OverrideKey { get; init; }
    public required string MachineId { get; init; }
    public required string MachineDisplay { get; init; }
    public required string Host { get; init; }
    public required CounterPath Path { get; init; }
    public required string? SelectionId { get; init; }
    public required string Color { get; init; }
    public required double Scale { get; init; }
    public required bool Visible { get; init; }
    public required IReadOnlyList<SamplePoint> Samples { get; init; }
    public required SeriesStats Stats { get; init; }
    public string? LastError { get; init; }
}

public sealed class SeriesStore
{
    private sealed class Tracked
    {
        public required string MachineId { get; set; }
        public required string Host { get; set; }
        public required string DisplayName { get; set; }
        public required CounterPath Path { get; init; }
        public SampleRing Ring { get; } = new(1800);
        public string? LastError { get; set; }
    }

    private readonly Dictionary<string, Tracked> _series = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MachineReadResult> _machines = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, MachineReadResult> Machines => _machines;

    public void Clear()
    {
        _series.Clear();
        _machines.Clear();
    }

    public void Apply(MachineReadResult result, Workspace workspace)
    {
        _machines[result.MachineId] = result;
        var capacity = Math.Clamp(workspace.HistoryCapacity, WorkspaceSanitizer.MinHistory, WorkspaceSanitizer.MaxHistory);

        if (result.Readings.Count == 0)
        {
            if (result.Status is MachineStatus.Offline or MachineStatus.AccessDenied or MachineStatus.Error)
                AppendGap(result, capacity);
            return;
        }

        foreach (var reading in result.Readings)
        {
            if (reading.Quality == SampleQuality.Warmup)
                continue;

            var key = SeriesIdentity.Key(result.Host, reading.Path);
            if (!_series.TryGetValue(key, out var tracked))
            {
                tracked = new Tracked
                {
                    MachineId = result.MachineId,
                    Host = result.Host,
                    DisplayName = result.DisplayName,
                    Path = reading.Path
                };
                _series[key] = tracked;
            }

            tracked.MachineId = result.MachineId;
            tracked.Host = result.Host;
            tracked.DisplayName = result.DisplayName;
            tracked.Ring.EnsureCapacity(capacity);
            if (reading.Quality == SampleQuality.Ok)
                tracked.LastError = null;
            else
                tracked.LastError = string.IsNullOrWhiteSpace(reading.Error) ? result.Message ?? "No value." : reading.Error;
            double? value = reading.Quality == SampleQuality.Ok ? reading.Value : null;
            tracked.Ring.Append(new SamplePoint(result.Timestamp, value));
        }
    }

    public void LoadImported(IReadOnlyList<ImportedSeries> series, Workspace workspace)
    {
        _series.Clear();
        var capacity = Math.Clamp(workspace.HistoryCapacity, WorkspaceSanitizer.MinHistory, WorkspaceSanitizer.MaxHistory);
        foreach (var imported in series)
        {
            var path = new CounterPath(imported.Category, imported.Counter, imported.Instance);
            var machine = workspace.Machines.FirstOrDefault(m =>
                string.Equals(MachineHost.Normalize(m.Host), MachineHost.Normalize(imported.Machine), StringComparison.OrdinalIgnoreCase));
            var tracked = new Tracked
            {
                MachineId = machine?.Id ?? imported.Machine,
                Host = MachineHost.Normalize(imported.Machine),
                DisplayName = machine?.DisplayName ?? MachineHost.Display(imported.Machine, null),
                Path = path
            };
            tracked.Ring.EnsureCapacity(capacity);
            foreach (var sample in imported.Samples.TakeLast(capacity))
                tracked.Ring.Append(sample);
            _series[SeriesIdentity.Key(tracked.Host, path)] = tracked;
        }
    }

    public void Prune(Workspace workspace)
    {
        var machineIds = workspace.Machines.Select(machine => machine.Id).ToHashSet(StringComparer.Ordinal);
        var remove = _series.Where(pair =>
                !machineIds.Contains(pair.Value.MachineId)
                || !MatchesAny(workspace, pair.Value.MachineId, pair.Value.Path))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in remove)
            _series.Remove(key);
    }

    public IReadOnlyList<SeriesSnapshot> Snapshot(Workspace workspace, TimeSpan? window)
    {
        Prune(workspace);
        DateTimeOffset? latest = null;
        foreach (var tracked in _series.Values)
        {
            if (tracked.Ring.Newest is { } newest && (latest is null || newest > latest))
                latest = newest;
        }

        var from = latest is not null && window is not null ? latest.Value - window.Value : (DateTimeOffset?)null;
        var ordered = _series.Values
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Path.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Path.Counter, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Path.Instance, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var siblingCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tracked in ordered)
        {
            var selection = MatchSelection(workspace, tracked.MachineId, tracked.Path);
            if (selection is null)
                continue;
            siblingCounts[selection.Id] = siblingCounts.GetValueOrDefault(selection.Id) + 1;
        }

        var snapshots = new List<SeriesSnapshot>(ordered.Count);
        var paletteIndex = 0;
        foreach (var tracked in ordered)
        {
            var samples = tracked.Ring.Copy(from);
            var selection = MatchSelection(workspace, tracked.MachineId, tracked.Path);
            var siblings = selection is null ? 1 : Math.Max(1, siblingCounts.GetValueOrDefault(selection.Id));
            var overrideKey = SeriesIdentity.OverrideKey(tracked.Host, tracked.Path);
            var color = workspace.ColorOverrides.TryGetValue(overrideKey, out var chosen)
                ? chosen
                : SeriesPalette.Resolve(selection?.Color, paletteIndex, siblings);
            var scale = workspace.ScaleOverrides.TryGetValue(overrideKey, out var scaleOverride)
                ? scaleOverride
                : selection?.Scale ?? 1;
            if (!double.IsFinite(scale) || scale == 0)
                scale = 1;
            var visible = !workspace.Visibility.TryGetValue(overrideKey, out var shown) || shown;
            snapshots.Add(new SeriesSnapshot
            {
                Key = SeriesIdentity.Key(tracked.Host, tracked.Path),
                OverrideKey = overrideKey,
                MachineId = tracked.MachineId,
                MachineDisplay = tracked.DisplayName,
                Host = tracked.Host,
                Path = tracked.Path,
                SelectionId = selection?.Id,
                Color = color,
                Scale = scale,
                Visible = visible,
                Samples = samples,
                Stats = SeriesStats.FromSamples(samples, 1),
                LastError = tracked.LastError
            });
            paletteIndex++;
        }

        return snapshots;
    }

    private void AppendGap(MachineReadResult result, int capacity)
    {
        foreach (var tracked in _series.Values.Where(s => s.MachineId == result.MachineId))
        {
            tracked.Ring.EnsureCapacity(capacity);
            tracked.Ring.Append(new SamplePoint(result.Timestamp, null));
        }
    }

    public static bool MatchesAny(Workspace workspace, string machineId, CounterPath path) =>
        MatchSelection(workspace, machineId, path) is not null;

    public static CounterSelection? MatchSelection(Workspace workspace, string machineId, CounterPath path)
    {
        CounterSelection? wildcard = null;
        foreach (var selection in workspace.Counters)
        {
            if (!selection.Enabled)
                continue;
            if (!WorkspaceEditor.NameEquals(selection.Category, path.Category)
                || !WorkspaceEditor.NameEquals(selection.Counter, path.Counter)
                || !InstanceMatcher.Matches(selection.Instance, path.Instance))
                continue;
            if (string.Equals(selection.MachineId, machineId, StringComparison.Ordinal))
                return selection;
            if (selection.MachineId == "*")
                wildcard = selection;
        }

        return wildcard;
    }
}

public static class SeriesPalette
{
    public static readonly string[] Colors =
    [
        "#4C9AFF", "#3DDC97", "#F0B429", "#F07178", "#C792EA",
        "#2EC4B6", "#FF8C42", "#82AAFF", "#E879C0", "#A3BE8C",
        "#FFD166", "#EF476F", "#06D6A0", "#118AB2", "#9B5DE5",
        "#F15BB5", "#FEE440", "#00BBF9", "#00F5D4", "#B8C0CC"
    ];

    public static string ColorFor(int index) => Colors[Math.Abs(index) % Colors.Length];

    public static string Resolve(string? preferred, int index, int siblingCount)
    {
        if (siblingCount <= 1 && !string.IsNullOrWhiteSpace(preferred))
            return preferred!;
        return ColorFor(index);
    }

    public static string Next(string? current)
    {
        var index = Array.FindIndex(Colors, color => string.Equals(color, current, StringComparison.OrdinalIgnoreCase));
        return ColorFor(index < 0 ? 0 : index + 1);
    }
}

public static class ValueFormat
{
    public static string Format(double? value)
    {
        if (value is not double number || !double.IsFinite(number))
            return "—";
        var abs = Math.Abs(number);
        if (abs == 0)
            return "0";
        if (abs >= 1000)
            return number.ToString("#,##0.##", System.Globalization.CultureInfo.CurrentCulture);
        if (abs >= 1)
            return number.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        return number.ToString("0.####", System.Globalization.CultureInfo.CurrentCulture);
    }
}
