using System.Management;
using PerfMonNext.Core;

namespace PerfMonNext.Windows;

public sealed class WindowsClusterNodeLookup : IClusterNodeLookup
{
    public ClusterLookup? TryGetCluster(string host, string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(host) || MachineHost.IsLocal(host))
            return null;

        try
        {
            var options = new ConnectionOptions
            {
                Timeout = TimeSpan.FromSeconds(8),
                Impersonation = ImpersonationLevel.Impersonate,
                Authentication = AuthenticationLevel.PacketPrivacy
            };
            if (!string.IsNullOrWhiteSpace(userName))
            {
                options.Username = userName;
                options.Password = password ?? "";
            }

            var scope = new ManagementScope(@"\\" + MachineHost.Normalize(host) + @"\root\MSCluster", options);
            scope.Connect();

            var clusterName = FirstProperty(scope, "SELECT Name FROM MSCluster_Cluster", "Name");
            if (string.IsNullOrWhiteSpace(clusterName))
                return null;

            var nodes = new List<string>();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name FROM MSCluster_Node"));
            foreach (ManagementObject row in searcher.Get())
            {
                var name = row["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    nodes.Add(name);
            }

            return nodes.Count == 0 ? null : new ClusterLookup(clusterName, nodes);
        }
        catch (ManagementException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static string? FirstProperty(ManagementScope scope, string query, string property)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query));
        foreach (ManagementObject row in searcher.Get())
            return row[property]?.ToString();
        return null;
    }
}
