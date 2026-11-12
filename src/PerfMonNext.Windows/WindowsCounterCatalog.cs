using System.Diagnostics;
using PerfMonNext.Core;

namespace PerfMonNext.Windows;

public sealed class WindowsCounterCatalog
{
    private readonly ICredentialVault _vault;

    public WindowsCounterCatalog(ICredentialVault vault) => _vault = vault;

    public CounterCatalog Load(MachineTarget machine, CancellationToken cancellationToken)
    {
        IpcConnector.Ensure(machine, _vault);
        var pcMachine = MachineHost.ToCounterMachine(machine.Host);
        WindowsCategorySampler.RefreshMachine(pcMachine);
        var categories = PerformanceCounterCategory.GetCategories(pcMachine);
        var sets = new List<CounterSetInfo>(categories.Length);
        var skipped = 0;
        foreach (var category in categories.OrderBy(item => item.CategoryName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var built = WindowsCategorySampler.ReadSet(pcMachine, category.CategoryName);
                if (built.Counters.Count == 0)
                {
                    skipped++;
                    sets.Add(new CounterSetInfo
                    {
                        Name = category.CategoryName,
                        MultiInstance = built.MultiInstance,
                        Help = "This set is installed, but its counters could not be listed.",
                        EnumerationError = built.EnumerationError ?? "No counters could be listed."
                    });
                    continue;
                }
                sets.Add(built);
                if (built.EnumerationError is not null)
                    skipped++;
            }
            catch
            {
                skipped++;
                sets.Add(new CounterSetInfo
                {
                    Name = category.CategoryName,
                    MultiInstance = true,
                    Help = "This set is installed, but its counters could not be listed.",
                    EnumerationError = "This set is installed, but its counters could not be listed."
                });
            }
        }

        return new CounterCatalog
        {
            SourceHost = machine.Host,
            Warning = skipped == 0 ? null : skipped + " counter sets could not be listed completely (permissions or an empty instance list).",
            Sets = sets
        };
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
