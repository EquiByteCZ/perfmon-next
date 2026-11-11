using System.Diagnostics;
using PerfMonNext.Core;

namespace PerfMonNext.Windows;

public sealed class WinRmClient
{
    private readonly ICredentialVault _vault;
    private readonly Dictionary<string, (DateTimeOffset Expires, CounterCatalog Catalog)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public WinRmClient(ICredentialVault vault) => _vault = vault;

    public CounterCatalog LoadCatalog(MachineTarget machine, CancellationToken cancellationToken)
    {
        var cached = Cached(machine.Id);
        if (cached is not null)
            return cached;
        var json = Run(machine, WinRmScripts.ListSets(machine.Host, UseCredential(machine)), TimeSpan.FromSeconds(90), cancellationToken);
        var catalog = WinRmJson.ParseSets(json, machine.Host);
        lock (_gate)
            _cache[machine.Id] = (DateTimeOffset.UtcNow.AddMinutes(2), catalog);
        return catalog;
    }

    public CounterSetInfo LoadSet(MachineTarget machine, string categoryName, CancellationToken cancellationToken)
    {
        var catalog = LoadCatalog(machine, cancellationToken);
        return catalog.Sets.FirstOrDefault(set => set.Name.Equals(categoryName, StringComparison.OrdinalIgnoreCase))
            ?? CounterSetBuilder.Build(categoryName, "This counter set was not returned by Get-Counter -ListSet.", [], false);
    }

    public MachineReadResult Read(MachineTarget machine, IReadOnlyList<CounterPath> paths)
    {
        if (paths.Count == 0)
            return MachineReadResult.From(machine, MachineStatus.Online, null, DateTimeOffset.UtcNow);
        try
        {
            var localPaths = paths.Select(path => path.Format()).ToList();
            var json = Run(machine, WinRmScripts.Sample(machine.Host, localPaths, UseCredential(machine)), TimeSpan.FromSeconds(40), CancellationToken.None);
            var samples = WinRmJson.ParseSamples(json);
            var readings = samples.Select(sample => new CounterReading(sample.Path, sample.Value, SampleQuality.Ok, null)).ToList();
            if (readings.Count == 0)
                return MachineReadResult.From(machine, MachineStatus.Error, "WinRM returned no counter samples.", DateTimeOffset.UtcNow);
            return MachineReadResult.From(machine, MachineStatus.Online, null, DateTimeOffset.UtcNow, readings);
        }
        catch (Exception ex)
        {
            var status = ex.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)
                ? MachineStatus.AccessDenied
                : MachineStatus.Error;
            return MachineReadResult.From(machine, status, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    public void Release(string machineId)
    {
        lock (_gate)
            _cache.Remove(machineId);
    }

    private CounterCatalog? Cached(string machineId)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(machineId, out var entry) && entry.Expires > DateTimeOffset.UtcNow)
                return entry.Catalog;
            return null;
        }
    }

    private bool UseCredential(MachineTarget machine) =>
        machine.Credential.Mode == CredentialMode.Alternate && !MachineHost.IsLocal(machine.Host);

    private string Run(MachineTarget machine, string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var preamble = "";
        string? password = null;
        if (UseCredential(machine))
        {
            if (!_vault.TryGet(machine.Id, out var user, out password) || string.IsNullOrWhiteSpace(user))
                throw new UnauthorizedAccessException("Enter a user name and password for " + machine.DisplayName + ".");
            preamble = WinRmScripts.CredentialPreamble(user);
        }

        var start = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command -")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (password is not null)
            start.Environment["PERFMON_WINRM_PASSWORD"] = password;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start powershell.exe.");
        process.StandardInput.Write(preamble);
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException("WinRM did not answer within " + (int)timeout.TotalSeconds + "s.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr.Trim());
        return stdout;
    }
}

public sealed class WinRmCounterProbe : ICounterProbe
{
    private readonly WinRmClient _client;

    public WinRmCounterProbe(WinRmClient client) => _client = client;

    public MachineReadResult Read(MachineTarget machine, IReadOnlyList<CounterPath> paths) => _client.Read(machine, paths);

    public void Release(string machineId) => _client.Release(machineId);
}
