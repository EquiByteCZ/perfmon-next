namespace PerfMonNext.Core;

public sealed record ClusterLookup(string ClusterName, IReadOnlyList<string> Nodes);

/// <summary>How the user wants the typed host to be added.</summary>
public enum AddHostKind
{
    Server,
    Cluster
}

public enum ClusterQueryStatus
{
    Cluster,
    NotCluster,
    AccessDenied,
    Unreachable,
    Failed
}

public sealed record ClusterNodeInfo(string Name, string State);

/// <summary>Result of reading a failover cluster's node list.</summary>
public sealed record ClusterQueryResult(
    ClusterQueryStatus Status,
    string? ClusterName,
    IReadOnlyList<ClusterNodeInfo> Nodes,
    string Message,
    string Method)
{
    public static ClusterQueryResult Found(string clusterName, IReadOnlyList<ClusterNodeInfo> nodes, string method) =>
        new(ClusterQueryStatus.Cluster, clusterName, nodes, "", method);

    public static ClusterQueryResult Error(ClusterQueryStatus status, string message, string method) =>
        new(status, null, [], message, method);

    public bool Succeeded => Status == ClusterQueryStatus.Cluster && Nodes.Count > 0;
}

public interface IClusterNodeLookup
{
    /// <summary>Reads the cluster that <paramref name="host"/> belongs to (a cluster name or any node name).</summary>
    ClusterQueryResult Query(string host, string? userName, string? password, CancellationToken cancellationToken = default);
}

/// <summary>One discovered node offered to the user before it is added.</summary>
public sealed record ClusterNodeCandidate(string Host, string ShortName, string State, bool AlreadyAdded);

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

        var hosts = Distinct(lookup.Nodes);
        return hosts.Count == 0 ? [requestedHost] : hosts;
    }

    /// <summary>
    /// The DNS suffix of a typed FQDN ("clus.contoso.local" → "contoso.local"); null for a short name or an IP address.
    /// </summary>
    public static string? DomainSuffix(string? typedHost)
    {
        if (string.IsNullOrWhiteSpace(typedHost) || MachineHost.IsLocal(typedHost))
            return null;
        var value = typedHost.Trim().TrimEnd('.');
        if (System.Net.IPAddress.TryParse(value, out _))
            return null;
        var cut = value.IndexOf('.');
        return cut <= 0 || cut == value.Length - 1 ? null : value[(cut + 1)..];
    }

    /// <summary>
    /// Cluster node names are NetBIOS-style short names. When the user typed a fully qualified cluster name,
    /// qualify short node names with the same DNS suffix so they resolve from a machine outside the domain.
    /// </summary>
    public static string QualifyNode(string node, string typedHost)
    {
        var name = node.Trim().TrimEnd('.');
        if (name.Contains('.') || System.Net.IPAddress.TryParse(name, out _))
            return name;
        var suffix = DomainSuffix(typedHost);
        return suffix is null ? name : name + "." + suffix;
    }

    /// <summary>
    /// The node list to show before adding: trimmed, qualified, de-duplicated (short-name aware) and sorted,
    /// with nodes already in the workspace marked.
    /// </summary>
    public static IReadOnlyList<ClusterNodeCandidate> Candidates(
        string typedHost,
        IEnumerable<ClusterNodeInfo> nodes,
        IEnumerable<string> existingHosts)
    {
        var existing = existingHosts.Where(host => !string.IsNullOrWhiteSpace(host)).ToList();
        var result = new List<ClusterNodeCandidate>();
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Name))
                continue;
            var host = QualifyNode(node.Name, typedHost);
            if (result.Any(item => MachineHost.SameHost(item.Host, host)))
                continue;
            var already = existing.Any(item => MachineHost.SameHost(item, host));
            result.Add(new ClusterNodeCandidate(host, MachineHost.ShortName(host), string.IsNullOrWhiteSpace(node.State) ? "Unknown" : node.State, already));
        }

        result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Host, b.Host));
        return result;
    }

    /// <summary>MSCluster_Node.State → text.</summary>
    public static string NodeState(object? raw) => raw switch
    {
        null => "Unknown",
        string text when int.TryParse(text, out var parsed) => NodeState(parsed),
        string text => string.IsNullOrWhiteSpace(text) ? "Unknown" : text.Trim(),
        _ => Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture) switch
        {
            0 => "Up",
            1 => "Down",
            2 => "Paused",
            3 => "Joining",
            _ => "Unknown"
        }
    };

    /// <summary>
    /// Parses the output of the PowerShell fallback, one node per line as "name|state".
    /// Blank lines, headers and anything without a name are ignored.
    /// </summary>
    public static IReadOnlyList<ClusterNodeInfo> ParseNodeLines(string? output)
    {
        var nodes = new List<ClusterNodeInfo>();
        if (string.IsNullOrWhiteSpace(output))
            return nodes;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split('|');
            var name = parts[0].Trim();
            if (name.Length == 0 || name.Contains(' '))
                continue;
            var state = parts.Length > 1 ? NodeState(parts[1].Trim()) : "Unknown";
            if (nodes.Any(node => node.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            nodes.Add(new ClusterNodeInfo(name, state));
        }

        return nodes;
    }

    /// <summary>Cluster name from the "#cluster|NAME" line of the PowerShell fallback output.</summary>
    public static string? ParseClusterName(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#cluster|", StringComparison.OrdinalIgnoreCase))
            {
                var name = line["#cluster|".Length..].Trim();
                return name.Length == 0 ? null : name;
            }
        }

        return null;
    }

    /// <summary>
    /// True for a plain DNS name, NetBIOS name, IPv4 or IPv6 address. Used before a host name is put on a
    /// PowerShell command line, so quotes, spaces, semicolons and the like are rejected.
    /// </summary>
    public static bool IsSafeHostName(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;
        var value = host.Trim();
        if (value.Length > 255)
            return false;
        foreach (var ch in value)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':'))
                return false;
        }

        return value[0] != '-';
    }

    /// <summary>Maps an HRESULT from WMI/DCOM/RPC to a status the user can act on.</summary>
    public static ClusterQueryStatus Classify(int hresult) => unchecked((uint)hresult) switch
    {
        0x80070005 or 0x80041003 => ClusterQueryStatus.AccessDenied,      // E_ACCESSDENIED, WBEM_E_ACCESS_DENIED
        0x8004100E or 0x80041010 => ClusterQueryStatus.NotCluster,        // WBEM_E_INVALID_NAMESPACE, WBEM_E_INVALID_CLASS
        0x800706BA or 0x800706BF or 0x80070035 or 0x80070046 or 0x8007051F or 0x800705B4 or 0x80041032
            => ClusterQueryStatus.Unreachable,                            // RPC server unavailable, call failed, network path not found, timeouts
        _ => ClusterQueryStatus.Failed
    };

    public static string Describe(ClusterQueryResult result, string host) => result.Status switch
    {
        ClusterQueryStatus.Cluster when result.Nodes.Count == 0 => "Cluster " + (result.ClusterName ?? host) + " reported no nodes.",
        ClusterQueryStatus.Cluster => "Cluster " + result.ClusterName + ": " + result.Nodes.Count + (result.Nodes.Count == 1 ? " node" : " nodes") + " found (" + result.Method + ").",
        ClusterQueryStatus.NotCluster => host + " is not a failover cluster (no cluster WMI namespace). Add it as a server instead.",
        ClusterQueryStatus.AccessDenied => "Access denied reading the cluster on " + host + ". Use an account that is a local administrator on the cluster nodes.",
        ClusterQueryStatus.Unreachable => host + " is unreachable over WMI/RPC. Check the name, DNS and that Windows Management Instrumentation is allowed through the firewall.",
        _ => "Could not read the cluster on " + host + "." + (string.IsNullOrWhiteSpace(result.Message) ? "" : " " + result.Message)
    } + (result.Status is ClusterQueryStatus.Cluster or ClusterQueryStatus.Failed || string.IsNullOrWhiteSpace(result.Message) ? "" : " (" + result.Message + ")");

    private static List<string> Distinct(IEnumerable<string> names)
    {
        var hosts = new List<string>();
        foreach (var node in names)
        {
            if (string.IsNullOrWhiteSpace(node))
                continue;
            var host = node.Trim();
            if (hosts.Any(existing => MachineHost.SameHost(existing, host)))
                continue;
            hosts.Add(host);
        }

        return hosts;
    }
}
