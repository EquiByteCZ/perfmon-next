namespace PerfMonNext.Core;

public sealed record ClusterLookup(string ClusterName, IReadOnlyList<string> Nodes);

public interface IClusterNodeLookup
{
    ClusterLookup? TryGetCluster(string host, string? userName, string? password);
}

public static class ClusterExpansion
{
    public static bool IsClusterName(string host, ClusterLookup lookup) =>
        MachineHost.SameHost(host, lookup.ClusterName);

    /// <summary>
    /// If the typed name is the cluster, return its nodes. If it is a node (or not a cluster), return the typed host only.
    /// </summary>
    public static IReadOnlyList<string> HostsToAdd(string requestedHost, ClusterLookup? lookup)
    {
        if (lookup is null || lookup.Nodes.Count == 0 || !IsClusterName(requestedHost, lookup))
            return [requestedHost];

        var hosts = new List<string>();
        foreach (var node in lookup.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node))
                continue;
            var host = node.Trim();
            if (hosts.Any(existing => MachineHost.SameHost(existing, host)))
                continue;
            hosts.Add(host);
        }

        return hosts.Count == 0 ? [requestedHost] : hosts;
    }
}
