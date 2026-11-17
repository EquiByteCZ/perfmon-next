using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using PerfMonNext.Core;

namespace PerfMonNext.Windows;

/// <summary>
/// Reads a failover cluster's node list. First over WMI (root\MSCluster, packet privacy) so the FailoverClusters
/// PowerShell module is not needed on this PC; if that fails for a reason other than access denied and the
/// module is installed, falls back to Get-ClusterNode (current Windows credentials only).
/// </summary>
public sealed class WindowsClusterNodeLookup : IClusterNodeLookup
{
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PowerShellTimeout = TimeSpan.FromSeconds(60);

    public ClusterQueryResult Query(string host, string? userName, string? password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return ClusterQueryResult.Error(ClusterQueryStatus.Failed, "Enter a cluster name.", "WMI");

        var target = MachineHost.IsLocal(host) ? "." : host.Trim();
        var wmi = QueryWmi(target, userName, password);
        if (wmi.Succeeded || wmi.Status == ClusterQueryStatus.AccessDenied || cancellationToken.IsCancellationRequested)
            return wmi;

        // Get-ClusterNode has no -Credential parameter, so the fallback only runs with the current Windows user.
        if (!string.IsNullOrWhiteSpace(userName))
            return wmi;

        var ps = QueryPowerShell(target, cancellationToken);
        if (ps is null)
            return wmi;
        if (ps.Succeeded)
            return ps;
        // Prefer the more specific of the two errors.
        return wmi.Status == ClusterQueryStatus.Failed ? ps : wmi;
    }

    private static ClusterQueryResult QueryWmi(string host, string? userName, string? password)
    {
        const string method = "WMI";
        try
        {
            var options = new ConnectionOptions
            {
                Timeout = WmiTimeout,
                Impersonation = ImpersonationLevel.Impersonate,
                Authentication = AuthenticationLevel.PacketPrivacy,
                EnablePrivileges = true
            };
            if (!string.IsNullOrWhiteSpace(userName) && host != ".")
            {
                options.Username = userName;
                options.Password = password ?? "";
            }

            var scope = new ManagementScope(@"\\" + host + @"\root\MSCluster", options);
            scope.Connect();

            var clusterName = FirstProperty(scope, "SELECT Name FROM MSCluster_Cluster", "Name");
            if (string.IsNullOrWhiteSpace(clusterName))
                return ClusterQueryResult.Error(ClusterQueryStatus.NotCluster, "MSCluster_Cluster is empty.", method);

            var nodes = new List<ClusterNodeInfo>();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, State FROM MSCluster_Node"), Enumeration());
            foreach (ManagementObject row in searcher.Get())
            {
                using (row)
                {
                    var name = row["Name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(name))
                        nodes.Add(new ClusterNodeInfo(name.Trim(), ClusterExpansion.NodeState(SafeGet(row, "State"))));
                }
            }

            return nodes.Count == 0
                ? new ClusterQueryResult(ClusterQueryStatus.Cluster, clusterName, [], "", method)
                : ClusterQueryResult.Found(clusterName, nodes, method);
        }
        catch (ManagementException ex)
        {
            return ClusterQueryResult.Error(ClusterExpansion.Classify((int)ex.ErrorCode), ex.Message.Trim(), method);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ClusterQueryResult.Error(ClusterQueryStatus.AccessDenied, ex.Message.Trim(), method);
        }
        catch (COMException ex)
        {
            return ClusterQueryResult.Error(ClusterExpansion.Classify(ex.HResult), ex.Message.Trim(), method);
        }
        catch (Exception ex)
        {
            return ClusterQueryResult.Error(ClusterExpansion.Classify(ex.HResult), ex.Message.Trim(), method);
        }
    }

    /// <summary>Returns null when PowerShell or the FailoverClusters module is not available.</summary>
    private static ClusterQueryResult? QueryPowerShell(string host, CancellationToken cancellationToken)
    {
        const string method = "Get-ClusterNode";
        if (!ClusterExpansion.IsSafeHostName(host))
            return null;
        var name = host == "." ? "." : host;
        var script =
            "$ErrorActionPreference='Stop';" +
            "if (-not (Get-Module -ListAvailable -Name FailoverClusters)) { exit 3 };" +
            "Import-Module FailoverClusters;" +
            "try {" +
            $" $c = Get-Cluster -Name '{name}';" +
            " Write-Output ('#cluster|' + $c.Name);" +
            $" Get-ClusterNode -Cluster '{name}' | ForEach-Object {{ $_.Name + '|' + [int]$_.State }}" +
            "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 2 }";

        var exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(exe))
            return null;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
                process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            if (!process.WaitForExit((int)PowerShellTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return ClusterQueryResult.Error(ClusterQueryStatus.Unreachable, "Get-ClusterNode timed out.", method);
            }

            var output = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult().Trim();
            if (process.ExitCode == 3)
                return null;
            if (process.ExitCode != 0)
            {
                var status = error.Contains("denied", StringComparison.OrdinalIgnoreCase) ? ClusterQueryStatus.AccessDenied
                    : error.Contains("not", StringComparison.OrdinalIgnoreCase) && error.Contains("cluster", StringComparison.OrdinalIgnoreCase) ? ClusterQueryStatus.NotCluster
                    : ClusterQueryStatus.Failed;
                return ClusterQueryResult.Error(status, error, method);
            }

            var clusterName = ClusterExpansion.ParseClusterName(output) ?? host;
            var nodes = ClusterExpansion.ParseNodeLines(output);
            return new ClusterQueryResult(ClusterQueryStatus.Cluster, clusterName, nodes, "", method);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    private static System.Management.EnumerationOptions Enumeration() => new() { Timeout = WmiTimeout, ReturnImmediately = false };

    private static object? SafeGet(ManagementBaseObject row, string property)
    {
        try
        {
            return row[property];
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static string? FirstProperty(ManagementScope scope, string query, string property)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query), Enumeration());
        foreach (ManagementObject row in searcher.Get())
        {
            using (row)
                return row[property]?.ToString();
        }

        return null;
    }
}
