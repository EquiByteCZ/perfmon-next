using PerfMonNext.Core;
using Xunit;

namespace PerfMonNext.Core.Tests;

public sealed class ClusterDiscoveryTests
{
    [Fact]
    public void Short_node_names_get_the_dns_suffix_of_a_typed_fqdn()
    {
        Assert.Equal("contoso.local", ClusterExpansion.DomainSuffix("S2DCLUS.contoso.local"));
        Assert.Null(ClusterExpansion.DomainSuffix("s2dclus"));
        Assert.Null(ClusterExpansion.DomainSuffix("10.0.0.10"));
        Assert.Null(ClusterExpansion.DomainSuffix("."));
        Assert.Equal("node1.contoso.local", ClusterExpansion.QualifyNode("node1", "clus.contoso.local"));
        Assert.Equal("node1", ClusterExpansion.QualifyNode("node1", "clus"));
        Assert.Equal("node1.other.local", ClusterExpansion.QualifyNode("node1.other.local", "clus.contoso.local"));
        Assert.Equal("node1", ClusterExpansion.QualifyNode(" node1 ", "10.0.0.10"));
    }

    [Fact]
    public void Candidates_are_deduplicated_sorted_and_mark_existing_machines()
    {
        var nodes = new[]
        {
            new ClusterNodeInfo("NODE2", "Up"),
            new ClusterNodeInfo("node1", "Paused"),
            new ClusterNodeInfo("node1.contoso.local", "Up"),
            new ClusterNodeInfo("  ", "Up"),
            new ClusterNodeInfo("node3", "")
        };

        var list = ClusterExpansion.Candidates("clus.contoso.local", nodes, ["NODE2", "."]);

        Assert.Equal(["node1.contoso.local", "NODE2.contoso.local", "node3.contoso.local"], list.Select(c => c.Host));
        Assert.Equal("Paused", list[0].State);
        Assert.False(list[0].AlreadyAdded);
        Assert.True(list[1].AlreadyAdded);
        Assert.Equal("NODE2", list[1].ShortName);
        Assert.Equal("Unknown", list[2].State);
    }

    [Fact]
    public void Node_state_maps_mscluster_codes()
    {
        Assert.Equal("Up", ClusterExpansion.NodeState(0u));
        Assert.Equal("Down", ClusterExpansion.NodeState(1));
        Assert.Equal("Paused", ClusterExpansion.NodeState("2"));
        Assert.Equal("Joining", ClusterExpansion.NodeState(3L));
        Assert.Equal("Unknown", ClusterExpansion.NodeState(-1));
        Assert.Equal("Unknown", ClusterExpansion.NodeState(null));
    }

    [Fact]
    public void Powershell_output_is_parsed()
    {
        const string output = "#cluster|S2DCLUS\r\nnode1|0\r\nnode2|1\r\n\r\nNODE1|0\r\nsome junk line\r\nnode3\r\n";
        Assert.Equal("S2DCLUS", ClusterExpansion.ParseClusterName(output));
        var nodes = ClusterExpansion.ParseNodeLines(output);
        Assert.Equal(["node1", "node2", "node3"], nodes.Select(n => n.Name));
        Assert.Equal(["Up", "Down", "Unknown"], nodes.Select(n => n.State));
        Assert.Empty(ClusterExpansion.ParseNodeLines(""));
        Assert.Null(ClusterExpansion.ParseClusterName("node1|0"));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), ClusterQueryStatus.AccessDenied)]
    [InlineData(unchecked((int)0x80041003), ClusterQueryStatus.AccessDenied)]
    [InlineData(unchecked((int)0x8004100E), ClusterQueryStatus.NotCluster)]
    [InlineData(unchecked((int)0x800706BA), ClusterQueryStatus.Unreachable)]
    [InlineData(unchecked((int)0x80070035), ClusterQueryStatus.Unreachable)]
    [InlineData(unchecked((int)0x80004005), ClusterQueryStatus.Failed)]
    public void Hresults_are_classified(int hresult, ClusterQueryStatus expected) =>
        Assert.Equal(expected, ClusterExpansion.Classify(hresult));

    [Fact]
    public void Error_messages_tell_the_user_what_to_do()
    {
        var denied = ClusterExpansion.Describe(ClusterQueryResult.Error(ClusterQueryStatus.AccessDenied, "Access is denied.", "WMI"), "clus");
        Assert.Contains("administrator", denied, StringComparison.Ordinal);
        var notCluster = ClusterExpansion.Describe(ClusterQueryResult.Error(ClusterQueryStatus.NotCluster, "Invalid namespace", "WMI"), "srv1");
        Assert.Contains("not a failover cluster", notCluster, StringComparison.Ordinal);
        Assert.Contains("server", notCluster, StringComparison.Ordinal);
        var unreachable = ClusterExpansion.Describe(ClusterQueryResult.Error(ClusterQueryStatus.Unreachable, "The RPC server is unavailable.", "WMI"), "clus");
        Assert.Contains("firewall", unreachable, StringComparison.Ordinal);
        var found = ClusterExpansion.Describe(ClusterQueryResult.Found("CLUS", [new ClusterNodeInfo("n1", "Up"), new ClusterNodeInfo("n2", "Up")], "WMI"), "clus");
        Assert.Equal("Cluster CLUS: 2 nodes found (WMI).", found);
        Assert.False(new ClusterQueryResult(ClusterQueryStatus.Cluster, "CLUS", [], "", "WMI").Succeeded);
    }

    [Theory]
    [InlineData("clus.contoso.local", true)]
    [InlineData("S2D-CLUS_01", true)]
    [InlineData("10.0.0.10", true)]
    [InlineData("fe80::1", true)]
    [InlineData("clus'; Remove-Item C:\\", false)]
    [InlineData("clus name", false)]
    [InlineData("-Command", false)]
    [InlineData("", false)]
    public void Only_plain_host_names_reach_powershell(string host, bool safe) =>
        Assert.Equal(safe, ClusterExpansion.IsSafeHostName(host));

    [Fact]
    public void Cluster_label_round_trips_and_is_omitted_for_servers()
    {
        var workspace = WorkspaceFactory.CreateNew();
        workspace.Machines.Add(new MachineTarget { Host = "node1.contoso.local", DisplayName = "node1", Cluster = "CLUS" });
        var json = WorkspaceStore.Serialize(workspace);
        Assert.Contains("\"cluster\"", json, StringComparison.OrdinalIgnoreCase);
        var server = WorkspaceFactory.CreateNew();
        Assert.DoesNotContain("\"cluster\"", WorkspaceStore.Serialize(server), StringComparison.OrdinalIgnoreCase);
    }
}
