using USBShare.Models;
using USBShare.Services;
using Xunit.Abstractions;

namespace USBShare.Tests;

public sealed class UsbTopologyServiceTests
{
    private readonly ITestOutputHelper _output;

    public UsbTopologyServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task BuildSnapshotAsync_ShouldBuildHubTree_AndMapShareableByListAndState()
    {
        var listedDeviceId = @"USB\VID_1234&PID_5678\DEV_LISTED";
        var unlistedDeviceId = @"USB\VID_ABCD&PID_0001\DEV_UNLISTED";

        var pnpRoots = new List<PnpDeviceNode>
        {
            Node(
                instanceId: @"USB\ROOT_HUB30\ROOT1",
                friendlyName: "USB Root Hub (USB 3.0)",
                deviceClass: "USBHUB",
                children:
                [
                    Node(
                        instanceId: @"USB\VID_1D6B&PID_0003\HUB1",
                        parentId: @"USB\ROOT_HUB30\ROOT1",
                        friendlyName: "External USB Hub",
                        deviceClass: "USBHUB",
                        children:
                        [
                            Node(
                                instanceId: listedDeviceId,
                                parentId: @"USB\VID_1D6B&PID_0003\HUB1",
                                friendlyName: "Listed Device",
                                deviceClass: "HIDClass"),
                            Node(
                                instanceId: unlistedDeviceId,
                                parentId: @"USB\VID_1D6B&PID_0003\HUB1",
                                friendlyName: "Not In usbipd list Device",
                                deviceClass: "HIDClass"),
                        ]),
                ]),
        };

        var usbipd = new FakeUsbipdService
        {
            ListResult = new UsbipListResult
            {
                Devices =
                [
                    new UsbipListDevice { BusId = "1-3", Description = "Listed Device", State = "Not shared" },
                    new UsbipListDevice { BusId = "1-99", Description = "Ghost Device", State = "Not shared" },
                ],
            },
            StateResult = new UsbipStateSnapshot
            {
                Devices =
                [
                    new UsbipStateDevice { BusId = "1-3", InstanceId = listedDeviceId, Description = "Listed Device" },
                    new UsbipStateDevice { BusId = "1-4", InstanceId = unlistedDeviceId, Description = "Not Listed Device" },
                ],
            },
        };

        var service = new UsbTopologyService(new FakePnpDeviceService(pnpRoots), usbipd);

        var snapshot = await service.BuildSnapshotAsync();

        _output.WriteLine("=== Hub Tree Snapshot ===");
        _output.WriteLine($"Nodes: {snapshot.Nodes.Count}");
        _output.WriteLine($"RootNodes: {snapshot.RootNodes.Count}");
        foreach (var root in snapshot.RootNodes)
        {
            LogNode(snapshot, root, 0);
        }

        Assert.Single(snapshot.RootNodes);
        Assert.All(snapshot.RootNodes, root => Assert.True(root.IsHub));

        Assert.True(snapshot.Nodes.TryGetValue(listedDeviceId, out var listedNode));
        Assert.NotNull(listedNode);
        Assert.True(listedNode!.IsShareable);
        Assert.Equal("1-3", listedNode.BusId);

        Assert.True(snapshot.Nodes.TryGetValue(unlistedDeviceId, out var unlistedNode));
        Assert.NotNull(unlistedNode);
        Assert.False(unlistedNode!.IsShareable);
        Assert.True(string.IsNullOrWhiteSpace(unlistedNode.BusId));

        Assert.DoesNotContain(snapshot.Nodes.Values, node => string.Equals(node.BusId, "1-99", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildSnapshotAsync_NodeWithChildren_ShouldBeHub()
    {
        var pnpRoots = new List<PnpDeviceNode>
        {
            Node(
                instanceId: @"USB\VID_DEAD&PID_BEEF\NON_HUB_ROOT",
                friendlyName: "USB Composite Device",
                deviceClass: "USB",
                children:
                [
                    Node(
                        instanceId: @"USB\ROOT_HUB30\REAL_HUB",
                        parentId: @"USB\VID_DEAD&PID_BEEF\NON_HUB_ROOT",
                        friendlyName: "USB Root Hub",
                        deviceClass: "USBHUB"),
                ]),
        };

        var service = new UsbTopologyService(
            new FakePnpDeviceService(pnpRoots),
            new FakeUsbipdService
            {
                ListResult = new UsbipListResult(),
                StateResult = new UsbipStateSnapshot(),
            });

        var snapshot = await service.BuildSnapshotAsync();

        _output.WriteLine("=== Hub Root Selection Debug ===");
        foreach (var root in snapshot.RootNodes)
        {
            _output.WriteLine($"Root: {root.DisplayName}, IsHub={root.IsHub}, Parent={root.ParentInstanceId ?? "(null)"}");
        }

        Assert.Single(snapshot.RootNodes);
        Assert.Equal(@"USB\VID_DEAD&PID_BEEF\NON_HUB_ROOT", snapshot.RootNodes[0].InstanceId);
        Assert.True(snapshot.RootNodes[0].IsHub);
        Assert.True(snapshot.Nodes[@"USB\VID_DEAD&PID_BEEF\NON_HUB_ROOT"].IsHub);
        Assert.True(snapshot.Nodes[@"USB\ROOT_HUB30\REAL_HUB"].IsHub);
    }

    private void LogNode(UsbTopologySnapshot snapshot, UsbTopologyNode node, int level)
    {
        var indent = new string(' ', level * 2);
        _output.WriteLine($"{indent}- {(node.IsHub ? "[HUB]" : "[DEV]")} {node.DisplayName}");
        _output.WriteLine($"{indent}  InstanceId={node.InstanceId}");
        _output.WriteLine($"{indent}  Parent={node.ParentInstanceId ?? "(null)"}");
        _output.WriteLine($"{indent}  BusId={node.BusId ?? "(null)"}");
        _output.WriteLine($"{indent}  IsShareable={node.IsShareable}");

        foreach (var childId in node.Children)
        {
            if (snapshot.Nodes.TryGetValue(childId, out var child))
            {
                LogNode(snapshot, child, level + 1);
            }
        }
    }

    private static PnpDeviceNode Node(
        string instanceId,
        string? parentId = null,
        string? friendlyName = null,
        string? deviceClass = null,
        List<PnpDeviceNode>? children = null)
    {
        return new PnpDeviceNode
        {
            InstanceId = instanceId,
            ParentInstanceId = parentId,
            FriendlyName = friendlyName,
            DeviceClass = deviceClass,
            Children = children ?? [],
        };
    }

    private sealed class FakePnpDeviceService : IPnpDeviceService
    {
        private readonly List<PnpDeviceNode> _roots;

        public FakePnpDeviceService(List<PnpDeviceNode> roots)
        {
            _roots = roots;
        }

        public Task<List<PnpDeviceNode>> GetUsbDeviceTreeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_roots);
        }
    }

    private sealed class FakeUsbipdService : IUsbipdService
    {
        public UsbipListResult ListResult { get; set; } = new();
        public UsbipStateSnapshot StateResult { get; set; } = new();

        public Task<UsbipStateSnapshot> GetStateAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(StateResult);
        }

        public Task<UsbipListResult> GetListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ListResult);
        }

        public Task<UsbipBindResult> EnsureBoundAsync(string busId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UsbipBindResult(true, false, false, "Fake"));
        }

        public Task<UsbipCommandResult> UnbindAsync(string busId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UsbipCommandResult(true, false, "Fake"));
        }
    }
}
