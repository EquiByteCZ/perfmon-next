using System.Collections.Concurrent;

namespace PerfMonNext.Core;

public enum MachineStatus
{
    Unknown,
    Online,
    Busy,
    Timeout,
    Offline,
    AccessDenied,
    Error
}

public enum SampleQuality
{
    Ok,
    Warmup,
    Missing
}

public sealed record CounterReading(CounterPath Path, double? Value, SampleQuality Quality, string? Error);

public sealed record MachineReadResult(
    string MachineId,
    string Host,
    string DisplayName,
    MachineStatus Status,
    string? Message,
    DateTimeOffset Timestamp,
    IReadOnlyList<CounterReading> Readings)
{
    public static MachineReadResult From(
        MachineTarget machine,
        MachineStatus status,
        string? message,
        DateTimeOffset timestamp,
        IReadOnlyList<CounterReading>? readings = null) =>
        new(
            machine.Id,
            MachineHost.Normalize(machine.Host),
            MachineHost.Display(machine.Host, machine.DisplayName),
            status,
            message,
            timestamp,
            readings ?? []);
}

public sealed record MachineQuery(MachineTarget Machine, IReadOnlyList<CounterPath> Paths);

public interface ICounterProbe
{
    MachineReadResult Read(MachineTarget machine, IReadOnlyList<CounterPath> paths);
    void Release(string machineId) { }
}

public interface ICounterPoller
{
    MachineReadResult Poll(MachineTarget machine, IReadOnlyList<CounterPath> paths, TimeSpan stuckAfter, DateTimeOffset now);
    void Abandon();
    void Release(string machineId);
}

public enum PollStatus
{
    Value,
    Busy,
    Stuck,
    Faulted
}

public readonly struct PollResult<T>
{
    public PollStatus Status { get; }
    public T? Value { get; }
    public Exception? Error { get; }
    public TimeSpan Waited { get; }

    private PollResult(PollStatus status, T? value, Exception? error, TimeSpan waited)
    {
        Status = status;
        Value = value;
        Error = error;
        Waited = waited;
    }

    public static PollResult<T> Ready(T value) => new(PollStatus.Value, value, null, TimeSpan.Zero);
    public static PollResult<T> Failed(Exception error) => new(PollStatus.Faulted, default, error, TimeSpan.Zero);
    public static PollResult<T> Waiting(bool stuck, TimeSpan waited) =>
        new(stuck ? PollStatus.Stuck : PollStatus.Busy, default, null, waited);
}

/// <summary>
/// One in-flight read per caller. Polls return immediately so a stuck remote host
/// cannot block the rest of the sample. A second read is not started until the first finishes.
/// </summary>
public sealed class PollingFlight<T>
{
    private readonly object _gate = new();
    private Task<T>? _task;
    private bool _ready;
    private int _epoch;
    private int _taskEpoch;
    private DateTimeOffset _started;

    public PollResult<T> Poll(Func<T> work, TimeSpan stuckAfter, DateTimeOffset now)
    {
        Task<T>? take = null;
        lock (_gate)
        {
            // Completed but the continuation has not published it yet, or a read is still running.
            // Do not start a second read in either case.
            if (_task is not null && !_ready && _taskEpoch == _epoch)
            {
                var waited = now - _started;
                if (waited < TimeSpan.Zero)
                    waited = TimeSpan.Zero;
                var stuck = _task.IsCompleted ? false : waited >= stuckAfter;
                return PollResult<T>.Waiting(stuck, waited);
            }

            if (_task is { IsCompleted: false })
            {
                var waited = now - _started;
                if (waited < TimeSpan.Zero)
                    waited = TimeSpan.Zero;
                return PollResult<T>.Waiting(waited >= stuckAfter, waited);
            }

            if (_task is { IsCompleted: true } && _ready && _taskEpoch == _epoch)
            {
                take = _task;
                _ready = false;
                StartLocked(work, now);
            }
            else
            {
                StartLocked(work, now);
                return PollResult<T>.Waiting(false, TimeSpan.Zero);
            }
        }

        return Complete(take!);
    }

    public void Abandon()
    {
        lock (_gate)
        {
            _epoch++;
            _ready = false;
        }
    }

    private void StartLocked(Func<T> work, DateTimeOffset now)
    {
        var epoch = _epoch;
        _started = now;
        _taskEpoch = epoch;
        _ready = false;
        var task = Task.Run(work);
        _task = task;
        _ = task.ContinueWith(
            finished =>
            {
                _ = finished.Exception;
                lock (_gate)
                {
                    if (epoch == _epoch && ReferenceEquals(_task, finished))
                        _ready = true;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private static PollResult<T> Complete(Task<T> task)
    {
        try
        {
            return PollResult<T>.Ready(task.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return PollResult<T>.Failed(ex);
        }
    }
}

public sealed class GuardedCounterPoller : ICounterPoller
{
    private readonly ICounterProbe _inner;
    private readonly ConcurrentDictionary<string, PollingFlight<MachineReadResult>> _flights = new(StringComparer.Ordinal);

    public GuardedCounterPoller(ICounterProbe inner) => _inner = inner;

    public MachineReadResult Poll(MachineTarget machine, IReadOnlyList<CounterPath> paths, TimeSpan stuckAfter, DateTimeOffset now)
    {
        var flight = _flights.GetOrAdd(machine.Id, _ => new PollingFlight<MachineReadResult>());
        var poll = flight.Poll(() => _inner.Read(machine, paths), stuckAfter, now);
        return poll.Status switch
        {
            PollStatus.Value => poll.Value!,
            PollStatus.Busy => MachineReadResult.From(machine, MachineStatus.Busy, "Reading counters…", now),
            PollStatus.Stuck => MachineReadResult.From(
                machine,
                MachineStatus.Timeout,
                $"Still waiting ({poll.Waited.TotalSeconds:0}s). The current attempt is kept; a second read is not started.",
                now),
            PollStatus.Faulted => MachineReadResult.From(machine, MachineStatus.Error, poll.Error?.Message ?? "Read failed.", now),
            _ => MachineReadResult.From(machine, MachineStatus.Error, "Unexpected poll state.", now)
        };
    }

    public void Abandon()
    {
        foreach (var flight in _flights.Values)
            flight.Abandon();
    }

    public void Release(string machineId)
    {
        if (_flights.TryRemove(machineId, out var flight))
            flight.Abandon();
        _inner.Release(machineId);
    }
}

public static class SelectionExpander
{
    public static IReadOnlyList<MachineQuery> Expand(Workspace workspace)
    {
        var queries = new List<MachineQuery>(workspace.Machines.Count);
        foreach (var machine in workspace.Machines)
        {
            var paths = new List<CounterPath>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var selection in workspace.Counters)
            {
                if (!selection.Enabled)
                    continue;
                var inScope = selection.MachineId == "*"
                    || string.Equals(selection.MachineId, machine.Id, StringComparison.Ordinal);
                if (!inScope)
                    continue;
                var path = new CounterPath(selection.Category, selection.Counter, selection.Instance ?? "");
                if (seen.Add(path.Format()))
                    paths.Add(path);
            }

            queries.Add(new MachineQuery(machine, paths));
        }

        return queries;
    }
}

public sealed class SamplingEngine
{
    private readonly ICounterPoller _poller;
    private readonly TimeProvider _time;

    public SamplingEngine(ICounterPoller poller, TimeProvider? time = null)
    {
        _poller = poller;
        _time = time ?? TimeProvider.System;
    }

    public static TimeSpan StuckAfter(int intervalMs)
    {
        var ms = Math.Clamp(Math.Max(intervalMs * 2, 8_000), 8_000, 20_000);
        return TimeSpan.FromMilliseconds(ms);
    }

    public void Collect(Workspace workspace, IProgress<MachineReadResult> progress, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var stuckAfter = StuckAfter(workspace.SampleIntervalMs);
        foreach (var query in SelectionExpander.Expand(workspace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.Paths.Count == 0)
                continue;

            MachineReadResult result;
            try
            {
                result = _poller.Poll(query.Machine, query.Paths, stuckAfter, now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = MachineReadResult.From(query.Machine, MachineStatus.Error, ex.Message, now);
            }

            progress.Report(result);
        }
    }

    public void Stop() => _poller.Abandon();
}
