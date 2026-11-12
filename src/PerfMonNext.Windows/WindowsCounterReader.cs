using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PerfMonNext.Core;

namespace PerfMonNext.Windows;

/// <summary>
/// Reads counters through <see cref="PerformanceCounter"/>. The first value from each
/// counter is discarded because rate counters return 0 until the next sample.
/// Remote alternate credentials are applied with an IPC$ session (WNetAddConnection2),
/// which is what PDH actually uses. Passwords stay in the vault, not in the workspace file.
/// </summary>
public sealed class WindowsCounterReader : ICounterProbe
{
    private readonly ICredentialVault _vault;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _sessionsGate = new();

    public WindowsCounterReader(ICredentialVault vault) => _vault = vault;

    public MachineReadResult Read(MachineTarget machine, IReadOnlyList<CounterPath> paths)
    {
        Session session;
        lock (_sessionsGate)
        {
            if (!_sessions.TryGetValue(machine.Id, out session!))
            {
                session = new Session();
                _sessions[machine.Id] = session;
            }
        }

        return session.Read(machine, paths, _vault);
    }

    public void Release(string machineId)
    {
        Session? session = null;
        lock (_sessionsGate)
        {
            if (_sessions.Remove(machineId, out var removed))
                session = removed;
        }

        session?.DisposeLater();
    }

    private sealed class Session
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, PerformanceCounter> _counters = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _warm = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (DateTime Expires, string[] Names)> _instances = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public MachineReadResult Read(MachineTarget machine, IReadOnlyList<CounterPath> paths, ICredentialVault vault)
        {
            lock (_sync)
            {
                if (_disposed)
                    return MachineReadResult.From(machine, MachineStatus.Error, "This machine was removed.", DateTimeOffset.UtcNow);

                try
                {
                    IpcConnector.Ensure(machine, vault);
                    var pcMachine = MachineHost.ToCounterMachine(machine.Host);
                    var readings = new List<CounterReading>();
                    var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var path in paths)
                    {
                        var concrete = Expand(pcMachine, path);
                        if (concrete.Count == 0)
                        {
                            readings.Add(new CounterReading(path, null, SampleQuality.Missing, "No instances match '" + path.Instance + "'."));
                            continue;
                        }

                        foreach (var item in concrete)
                        {
                            var key = pcMachine + "|" + item.Format();
                            needed.Add(key);
                            readings.Add(ReadOne(pcMachine, item, key));
                        }
                    }

                    foreach (var stale in _counters.Keys.Where(key => !needed.Contains(key)).ToArray())
                        Drop(stale);

                    if (readings.Count > 0 && readings.All(reading => reading.Quality == SampleQuality.Missing))
                    {
                        var message = readings.Select(reading => reading.Error).FirstOrDefault(error => !string.IsNullOrWhiteSpace(error))
                            ?? "Counters could not be read.";
                        return MachineReadResult.From(machine, MachineStatus.Error, message, DateTimeOffset.UtcNow, readings);
                    }

                    string? note = readings.Count > 0 && readings.All(reading => reading.Quality == SampleQuality.Warmup)
                        ? "Waiting for the next sample. Rate counters need two reads."
                        : null;
                    return MachineReadResult.From(machine, MachineStatus.Online, note, DateTimeOffset.UtcNow, readings);
                }
                catch (Exception ex)
                {
                    return MapFailure(machine, ex);
                }
            }
        }

        public void DisposeLater()
        {
            Task.Run(() =>
            {
                lock (_sync)
                {
                    _disposed = true;
                    foreach (var counter in _counters.Values)
                        counter.Dispose();
                    _counters.Clear();
                    _warm.Clear();
                }
            });
        }

        private List<CounterPath> Expand(string machine, CounterPath path)
        {
            if (string.IsNullOrEmpty(path.Instance) || (path.Instance != "*" && !path.Instance.EndsWith('*')))
                return [path with { Machine = null }];

            var names = GetInstances(machine, path.Category);
            var matched = names.Where(name => InstanceMatcher.Matches(path.Instance, name))
                .Select(name => path with { Instance = name, Machine = null })
                .ToList();
            return matched;
        }

        private string[] GetInstances(string machine, string category)
        {
            var cacheKey = machine + "" + category;
            if (_instances.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTime.UtcNow)
                return cached.Names;

            string[] names;
            try
            {
                names = WindowsCategorySampler.ReadInstanceNames(machine, category);
            }
            catch
            {
                names = [];
            }
            _instances[cacheKey] = (DateTime.UtcNow.AddSeconds(5), names);
            return names;
        }

        private CounterReading ReadOne(string machine, CounterPath path, string key)
        {
            try
            {
                if (string.IsNullOrEmpty(path.Instance))
                {
                    var instances = GetInstances(machine, path.Category);
                    if (instances.Length > 0)
                    {
                        return new CounterReading(
                            path,
                            null,
                            SampleQuality.Missing,
                            "This counter is multi-instance and was added without an instance. Select _Total or another instance.");
                    }
                }

                if (!_counters.TryGetValue(key, out var counter))
                {
                    counter = new PerformanceCounter(path.Category, path.Counter, path.Instance ?? "", machine);
                    _counters[key] = counter;
                }

                var raw = counter.NextValue();
                if (_warm.Add(key))
                    return new CounterReading(path, null, SampleQuality.Warmup, null);
                if (float.IsNaN(raw) || float.IsInfinity(raw))
                    return new CounterReading(path, null, SampleQuality.Missing, "Non-finite value.");
                return new CounterReading(path, raw, SampleQuality.Ok, null);
            }
            catch (Exception ex)
            {
                Drop(key);
                return new CounterReading(path, null, SampleQuality.Missing, ex.Message);
            }
        }

        private void Drop(string key)
        {
            if (_counters.Remove(key, out var counter))
                counter.Dispose();
            _warm.Remove(key);
        }
    }

    internal static MachineReadResult MapFailure(MachineTarget machine, Exception ex)
    {
        var now = DateTimeOffset.UtcNow;
        return ex switch
        {
            UnauthorizedAccessException => MachineReadResult.From(machine, MachineStatus.AccessDenied, ex.Message, now),
            Win32Exception win => MachineReadResult.From(machine, StatusOf(win.NativeErrorCode), win.Message, now),
            _ => MachineReadResult.From(machine, MachineStatus.Error, ex.Message, now)
        };
    }

    internal static MachineStatus StatusOf(int error) => error switch
    {
        5 or 1326 or 1219 => MachineStatus.AccessDenied,
        53 or 67 or 64 or 59 or 1722 or 1726 => MachineStatus.Offline,
        _ => MachineStatus.Error
    };
}

internal static class IpcConnector
{
    public static void Ensure(MachineTarget machine, ICredentialVault vault)
    {
        if (machine.Credential.Mode != CredentialMode.Alternate)
            return;
        if (MachineHost.IsLocal(machine.Host))
            throw new UnauthorizedAccessException("Alternate credentials apply to remote computers. The local computer is read as the signed-in user.");
        if (!vault.TryGet(machine.Id, out var user, out var password) || string.IsNullOrWhiteSpace(user))
            throw new UnauthorizedAccessException("Enter a user name and password for " + machine.DisplayName + ".");

        var resource = new NetResource
        {
            dwType = 0,
            lpRemoteName = @"\\" + machine.Host.Trim() + @"\IPC$"
        };
        var error = WNetAddConnection2(ref resource, password, user, 0);
        if (error == 0)
            return;
        throw new Win32Exception(error, Explain(error, machine.Host.Trim()));
    }

    private static string Explain(int error, string host) => error switch
    {
        1219 => "Windows already has a connection to " + host + " with different credentials. From a command prompt run: net use \\\\" + host + " /delete",
        1326 => "The user name or password was rejected.",
        53 or 67 => "Cannot reach \\\\" + host + "\\IPC$. Check the name, DNS, and that File and Printer Sharing is allowed.",
        5 => "Access denied while opening IPC$ on " + host + ".",
        _ => new Win32Exception(error).Message
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource resource, string? password, string? userName, int flags);
}
