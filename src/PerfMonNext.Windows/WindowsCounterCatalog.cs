using System.Diagnostics;
using PerfMonNext.Core;

namespace PerfMonNext.Windows;

public sealed class WindowsCounterCatalog
{
    private readonly ICredentialVault _vault;
    private readonly WinRmClient _winRm;

    public WindowsCounterCatalog(ICredentialVault vault, WinRmClient winRm)
    {
        _vault = vault;
        _winRm = winRm;
    }

    public CounterCatalog Load(MachineTarget machine, CancellationToken cancellationToken)
    {
        if (machine.Connection == ConnectionMode.WinRm)
            return _winRm.LoadCatalog(machine, cancellationToken);

        // Names only. GetInstanceNames() on the objects returned by GetCategories(machine)
        // is often empty for remote multi-instance sets (Hyper-V logical processors included),
        // which used to store the set with zero counters. Details are loaded when the set is selected.
        IpcConnector.Ensure(machine, _vault);
        cancellationToken.ThrowIfCancellationRequested();
        var categories = PerformanceCounterCategory.GetCategories(MachineHost.ToCounterMachine(machine.Host));
        var sets = categories
            .Select(category => category.CategoryName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new CounterSetInfo { Name = name, DetailsLoaded = false, MultiInstance = true })
            .ToList();
        return new CounterCatalog { SourceHost = machine.Host, Sets = sets };
    }

    public CounterSetInfo LoadSet(MachineTarget machine, string categoryName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (machine.Connection == ConnectionMode.WinRm)
            return _winRm.LoadSet(machine, categoryName, cancellationToken);

        try
        {
            var set = LoadPdhSet(machine, categoryName);
            if (set.Counters.Count > 0 || MachineHost.IsLocal(machine.Host))
                return set;
        }
        catch (Exception) when (!MachineHost.IsLocal(machine.Host))
        {
            // Fall through to WinRM. Remote Registry / PDH is blocked on a lot of cluster nodes.
        }

        var viaWinRm = _winRm.LoadSet(machine, categoryName, cancellationToken);
        if (viaWinRm.Counters.Count > 0)
            machine.Connection = ConnectionMode.WinRm;
        return viaWinRm;
    }

    private CounterSetInfo LoadPdhSet(MachineTarget machine, string categoryName)
    {
        IpcConnector.Ensure(machine, _vault);
        var category = new PerformanceCounterCategory(categoryName, MachineHost.ToCounterMachine(machine.Host));
        var rows = new List<CounterSetBuilder.SampleRow>();
        try
        {
            foreach (InstanceDataCollection column in category.ReadCategory().Values)
            {
                if (column.Count == 0 && !string.IsNullOrWhiteSpace(column.CounterName))
                {
                    rows.Add(new CounterSetBuilder.SampleRow(column.CounterName, ""));
                    continue;
                }

                foreach (InstanceData sample in column.Values)
                    rows.Add(new CounterSetBuilder.SampleRow(column.CounterName, sample.InstanceName ?? ""));
            }
        }
        catch
        {
            rows.Clear();
        }

        if (!rows.Any(row => !string.IsNullOrWhiteSpace(row.Counter)))
            rows.AddRange(ReadWithCountersApi(category));

        return CounterSetBuilder.Build(categoryName, null, rows, rows.Any(row => !string.IsNullOrEmpty(row.Instance)));
    }

    private static IEnumerable<CounterSetBuilder.SampleRow> ReadWithCountersApi(PerformanceCounterCategory category)
    {
        PerformanceCounter[]? counters = null;
        try
        {
            if (category.CategoryType == PerformanceCounterCategoryType.SingleInstance)
            {
                counters = category.GetCounters();
                return counters.Select(counter => new CounterSetBuilder.SampleRow(counter.CounterName, "")).ToList();
            }

            string[] instances;
            try
            {
                instances = category.GetInstanceNames();
            }
            catch (InvalidOperationException)
            {
                instances = [];
            }

            if (instances.Length == 0)
            {
                try
                {
                    counters = category.GetCounters("_Total");
                    return counters.Select(counter => new CounterSetBuilder.SampleRow(counter.CounterName, "_Total")).ToList();
                }
                catch
                {
                    counters = category.GetCounters();
                    return counters.Select(counter => new CounterSetBuilder.SampleRow(counter.CounterName, "")).ToList();
                }
            }

            var probe = instances.FirstOrDefault(name => name.Equals("_Total", StringComparison.OrdinalIgnoreCase)) ?? instances[0];
            counters = category.GetCounters(probe);
            var names = counters.Select(counter => counter.CounterName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return names.SelectMany(name => instances.Select(instance => new CounterSetBuilder.SampleRow(name, instance))).ToList();
        }
        finally
        {
            if (counters is not null)
            {
                foreach (var counter in counters)
                    counter.Dispose();
            }
        }
    }

    public string ReadHelp(MachineTarget machine, string category, string counter)
    {
        IpcConnector.Ensure(machine, _vault);
        var pcMachine = MachineHost.ToCounterMachine(machine.Host);
        foreach (var instance in new[] { "", "_Total" })
        {
            try
            {
                using var performanceCounter = new PerformanceCounter(category, counter, instance, pcMachine);
                var help = performanceCounter.CounterHelp;
                if (!string.IsNullOrWhiteSpace(help))
                    return help.Trim();
            }
            catch
            {
                // Try the next instance name. Single-instance and multi-instance counters differ here.
            }
        }

        return "No explain text is available for " + category + "\\" + counter + ".";
    }
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) =>
        System.Security.Cryptography.ProtectedData.Protect(plaintext, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] protectedBytes) =>
        System.Security.Cryptography.ProtectedData.Unprotect(protectedBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
}

public sealed class RelogBlgConverter
{
    public async Task<string> ConvertToCsvAsync(string blgPath, CancellationToken cancellationToken)
    {
        var plan = BlgImportPlanner.Create(blgPath);
        var exe = Path.Combine(Environment.SystemDirectory, "relog.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("relog.exe was not found. On Windows, convert the log with: " + plan.CommandLine, exe);

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in plan.Arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start relog.exe.");
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(("relog.exe exited with code " + process.ExitCode + ". " + stderr).Trim());
        if (!File.Exists(plan.CsvPath))
            throw new FileNotFoundException("relog.exe did not write a CSV file.", plan.CsvPath);
        return plan.CsvPath;
    }
}
