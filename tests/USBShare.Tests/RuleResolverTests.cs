using USBShare.Models;
using USBShare.Services;

namespace USBShare.Tests;

public sealed class RuleResolverTests
{
    private readonly RuleResolver _resolver = new();

    [Fact]
    public void HubRule_ShouldOverrideDeviceRule_WhenBothMatched()
    {
        var remoteHub = Guid.NewGuid();
        var remoteDevice = Guid.NewGuid();

        var topology = BuildTopology(
            CreateNode("H1", null, isHub: true, isShareable: false),
            CreateNode("D1", "H1", isHub: false, isShareable: true, busId: "2-1"));

        var rules = new List<ShareRule>
        {
            new() { NodeType = RuleNodeType.Hub, NodeInstanceId = "H1", RemoteId = remoteHub, Enabled = true },
            new() { NodeType = RuleNodeType.Device, NodeInstanceId = "D1", RemoteId = remoteDevice, Enabled = true },
        };

        var result = _resolver.Resolve(topology, rules, [remoteHub, remoteDevice]);

        Assert.True(result.TargetsByBusId.TryGetValue("2-1", out var targetRemote));
        Assert.Equal(remoteHub, targetRemote);
        Assert.Empty(result.ConflictsByInstanceId);
    }

    [Fact]
    public void MultipleAncestorHubRemotes_ShouldProduceConflict_AndSkipTarget()
    {
        var remoteA = Guid.NewGuid();
        var remoteB = Guid.NewGuid();

        var topology = BuildTopology(
            CreateNode("H_ROOT", null, isHub: true, isShareable: false),
            CreateNode("H_CHILD", "H_ROOT", isHub: true, isShareable: false),
            CreateNode("D1", "H_CHILD", isHub: false, isShareable: true, busId: "2-3"));

        var rules = new List<ShareRule>
        {
            new() { NodeType = RuleNodeType.Hub, NodeInstanceId = "H_ROOT", RemoteId = remoteA, Enabled = true },
            new() { NodeType = RuleNodeType.Hub, NodeInstanceId = "H_CHILD", RemoteId = remoteB, Enabled = true },
        };

        var result = _resolver.Resolve(topology, rules, [remoteA, remoteB]);

        Assert.False(result.TargetsByBusId.ContainsKey("2-3"));
        Assert.True(result.ConflictsByInstanceId.ContainsKey("D1"));
    }

    [Fact]
    public void NoRule_ShouldKeepTargetsEmpty()
    {
        var topology = BuildTopology(
            CreateNode("H1", null, isHub: true, isShareable: false),
            CreateNode("D1", "H1", isHub: false, isShareable: true, busId: "2-6"));

        var result = _resolver.Resolve(topology, [], []);

        Assert.Empty(result.TargetsByBusId);
        Assert.Empty(result.ConflictsByInstanceId);
    }

    [Fact]
    public void RulePointingToMissingRemote_ShouldMarkConflict()
    {
        var staleRemote = Guid.NewGuid();
        var topology = BuildTopology(CreateNode("D1", null, isHub: false, isShareable: true, busId: "2-10"));
        var rules = new List<ShareRule>
        {
            new() { NodeType = RuleNodeType.Device, NodeInstanceId = "D1", RemoteId = staleRemote, Enabled = true },
        };

        var result = _resolver.Resolve(topology, rules, []);

        Assert.False(result.TargetsByBusId.ContainsKey("2-10"));
        Assert.True(result.ConflictsByInstanceId.ContainsKey("D1"));
    }

    private static UsbTopologySnapshot BuildTopology(params UsbTopologyNode[] nodes)
    {
        var map = nodes.ToDictionary(node => node.InstanceId, StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.ParentInstanceId) &&
                map.TryGetValue(node.ParentInstanceId, out var parent))
            {
                parent.Children.Add(node.InstanceId);
            }
        }

        return new UsbTopologySnapshot
        {
            Nodes = map,
            RootNodes = nodes
                .Where(node => string.IsNullOrWhiteSpace(node.ParentInstanceId))
                .ToList(),
        };
    }

    private static UsbTopologyNode CreateNode(
        string instanceId,
        string? parentId,
        bool isHub,
        bool isShareable,
        string? busId = null)
    {
        return new UsbTopologyNode
        {
            InstanceId = instanceId,
            ParentInstanceId = parentId,
            DisplayName = instanceId,
            DeviceClass = "USB",
            IsHub = isHub,
            IsShareable = isShareable,
            BusId = busId,
        };
    }
}
