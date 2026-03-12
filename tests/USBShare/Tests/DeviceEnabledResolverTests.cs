using USBShare.Models;
using USBShare.Services;

namespace USBShare.Tests;

public sealed class DeviceEnabledResolverTests
{
    private readonly DeviceEnabledResolver _resolver = new();

    [Fact]
    public void EnabledDevice_ShouldBeIncluded_InResult()
    {
        var topology = BuildTopology(
            CreateNode("H1", null, isHub: true, isShareable: false),
            CreateNode("D1", "H1", isHub: false, isShareable: true, busId: "2-1"));

        var enabledDevices = new List<DeviceEnabled>
        {
            new() { NodeInstanceId = "D1", Enabled = true },
        };

        var result = _resolver.Resolve(topology, enabledDevices);

        Assert.True(result.EnabledBusIds.ContainsKey("2-1"));
        Assert.False(result.InheritedBusIds.Contains("2-1"));
    }

    [Fact]
    public void EnabledHub_ShouldIncludeAllDescendants_AsInherited()
    {
        var topology = BuildTopology(
            CreateNode("H_ROOT", null, isHub: true, isShareable: false),
            CreateNode("H_CHILD", "H_ROOT", isHub: true, isShareable: false),
            CreateNode("D1", "H_CHILD", isHub: false, isShareable: true, busId: "2-3"));

        var enabledDevices = new List<DeviceEnabled>
        {
            new() { NodeInstanceId = "H_ROOT", Enabled = true },
        };

        var result = _resolver.Resolve(topology, enabledDevices);

        Assert.True(result.EnabledBusIds.ContainsKey("2-3"));
        Assert.True(result.InheritedBusIds.Contains("2-3"));
    }

    [Fact]
    public void DisabledDevice_ShouldNotBeIncluded()
    {
        var topology = BuildTopology(
            CreateNode("H1", null, isHub: true, isShareable: false),
            CreateNode("D1", "H1", isHub: false, isShareable: true, busId: "2-6"));

        var enabledDevices = new List<DeviceEnabled>();

        var result = _resolver.Resolve(topology, enabledDevices);

        Assert.Empty(result.EnabledBusIds);
        Assert.Empty(result.InheritedBusIds);
    }

    [Fact]
    public void DirectDeviceEnabled_ShouldOverrideParentHub()
    {
        var topology = BuildTopology(
            CreateNode("H1", null, isHub: true, isShareable: false),
            CreateNode("D1", "H1", isHub: false, isShareable: true, busId: "2-10"));

        var enabledDevices = new List<DeviceEnabled>
        {
            new() { NodeInstanceId = "D1", Enabled = true },
        };

        var result = _resolver.Resolve(topology, enabledDevices);

        Assert.True(result.EnabledBusIds.ContainsKey("2-10"));
        Assert.False(result.InheritedBusIds.Contains("2-10"));
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
