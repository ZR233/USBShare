using USBShare.Models;

namespace USBShare.Services;

public interface IUsbTopologyService
{
    Task<UsbTopologySnapshot> BuildSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class UsbTopologyService : IUsbTopologyService
{
    private readonly IPnpDeviceService _pnpDeviceService;
    private readonly IUsbipdService _usbipdService;

    public UsbTopologyService(IPnpDeviceService pnpDeviceService, IUsbipdService usbipdService)
    {
        _pnpDeviceService = pnpDeviceService;
        _usbipdService = usbipdService;
    }

    public async Task<UsbTopologySnapshot> BuildSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await BuildPnpTreeAsync(cancellationToken).ConfigureAwait(false);
        await MergeUsbipdDataAsync(nodes, cancellationToken).ConfigureAwait(false);
        RebuildChildren(nodes);

        return new UsbTopologySnapshot
        {
            Nodes = nodes,
            RootNodes = BuildHubRootNodes(nodes),
        };
    }

    private async Task<Dictionary<string, UsbTopologyNode>> BuildPnpTreeAsync(CancellationToken cancellationToken)
    {
        List<PnpDeviceNode> roots;
        try
        {
            roots = await _pnpDeviceService.GetUsbDeviceTreeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            roots = [];
        }

        var nodes = new Dictionary<string, UsbTopologyNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            AddPnpNode(root, nodes, fallbackParentInstanceId: null);
        }

        RebuildChildren(nodes);

        foreach (var node in nodes.Values)
        {
            if (!node.IsHub && node.Children.Count > 0)
            {
                node.IsHub = LooksLikeHub(node.InstanceId, node.DisplayName, node.DeviceClass);
            }
        }

        return nodes;
    }

    private async Task MergeUsbipdDataAsync(Dictionary<string, UsbTopologyNode> nodes, CancellationToken cancellationToken)
    {
        UsbipListResult list;
        UsbipStateSnapshot state;

        try
        {
            var listTask = _usbipdService.GetListAsync(cancellationToken);
            var stateTask = _usbipdService.GetStateAsync(cancellationToken);
            await Task.WhenAll(listTask, stateTask).ConfigureAwait(false);
            list = listTask.Result;
            state = stateTask.Result;
        }
        catch
        {
            return;
        }

        var stateByBusId = state.Devices
            .Where(device => !string.IsNullOrWhiteSpace(device.BusId))
            .GroupBy(device => device.BusId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var listDevice in list.Devices.Where(device => !string.IsNullOrWhiteSpace(device.BusId)))
        {
            var busId = listDevice.BusId.Trim();
            if (!stateByBusId.TryGetValue(busId, out var stateDevice))
            {
                // 通过 busid 无法映射到 state，就无法进一步映射到 InstanceId。
                continue;
            }

            var instanceId = NormalizeInstanceId(stateDevice.InstanceId) ?? NormalizeInstanceId(listDevice.InstanceId);
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                continue;
            }

            if (!nodes.TryGetValue(instanceId, out var node))
            {
                node = new UsbTopologyNode
                {
                    InstanceId = instanceId,
                    ParentInstanceId = null,
                    DisplayName = ResolveDisplayName(listDevice.Description, stateDevice.Description, instanceId),
                    DeviceClass = "USB",
                    IsHub = LooksLikeHub(instanceId, listDevice.Description, "USB"),
                };
                nodes[instanceId] = node;
            }

            node.BusId = busId;
            node.IsShareable = true;

            if (string.IsNullOrWhiteSpace(node.DisplayName) ||
                string.Equals(node.DisplayName, node.InstanceId, StringComparison.OrdinalIgnoreCase))
            {
                node.DisplayName = ResolveDisplayName(listDevice.Description, stateDevice.Description, node.InstanceId);
            }
        }
    }

    private static void AddPnpNode(
        PnpDeviceNode source,
        Dictionary<string, UsbTopologyNode> nodes,
        string? fallbackParentInstanceId)
    {
        var instanceId = NormalizeInstanceId(source.InstanceId);
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return;
        }

        var parentInstanceId = NormalizeInstanceId(source.ParentInstanceId) ?? NormalizeInstanceId(fallbackParentInstanceId);
        if (string.Equals(instanceId, parentInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            parentInstanceId = null;
        }

        if (!nodes.TryGetValue(instanceId, out var node))
        {
            node = new UsbTopologyNode
            {
                InstanceId = instanceId,
                ParentInstanceId = parentInstanceId,
                DisplayName = ResolveDisplayName(source.FriendlyName, source.Description, instanceId),
                DeviceClass = source.DeviceClass?.Trim() ?? string.Empty,
                IsHub = source.IsHub || LooksLikeHub(instanceId, source.FriendlyName, source.DeviceClass),
            };
            nodes[instanceId] = node;
        }
        else
        {
            node.ParentInstanceId ??= parentInstanceId;
            node.IsHub = node.IsHub || source.IsHub || LooksLikeHub(instanceId, source.FriendlyName, source.DeviceClass);

            if (string.IsNullOrWhiteSpace(node.DeviceClass))
            {
                node.DeviceClass = source.DeviceClass?.Trim() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(node.DisplayName) ||
                string.Equals(node.DisplayName, node.InstanceId, StringComparison.OrdinalIgnoreCase))
            {
                node.DisplayName = ResolveDisplayName(source.FriendlyName, source.Description, instanceId);
            }
        }

        foreach (var child in source.Children)
        {
            AddPnpNode(child, nodes, instanceId);
        }
    }

    private static void RebuildChildren(Dictionary<string, UsbTopologyNode> nodes)
    {
        foreach (var node in nodes.Values)
        {
            node.Children.Clear();
        }

        foreach (var node in nodes.Values)
        {
            if (string.IsNullOrWhiteSpace(node.ParentInstanceId) ||
                !nodes.TryGetValue(node.ParentInstanceId, out var parent) ||
                string.Equals(node.InstanceId, parent.InstanceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            parent.Children.Add(node.InstanceId);
        }

        foreach (var node in nodes.Values)
        {
            node.Children = node.Children
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(childId => nodes.TryGetValue(childId, out var child) ? child.DisplayName : childId,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    private static List<UsbTopologyNode> BuildHubRootNodes(Dictionary<string, UsbTopologyNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return [];
        }

        var roots = nodes.Values
            .Where(node =>
                node.IsHub &&
                (string.IsNullOrWhiteSpace(node.ParentInstanceId) ||
                 !nodes.TryGetValue(node.ParentInstanceId, out var parent) ||
                 !parent.IsHub))
            .OrderBy(node => node.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (roots.Count > 0)
        {
            return roots;
        }

        return nodes.Values
            .Where(node => string.IsNullOrWhiteSpace(node.ParentInstanceId) || !nodes.ContainsKey(node.ParentInstanceId))
            .OrderBy(node => node.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string? NormalizeInstanceId(string? instanceId)
    {
        return string.IsNullOrWhiteSpace(instanceId) ? null : instanceId.Trim();
    }

    private static string ResolveDisplayName(string? preferred, string? alternate, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        if (!string.IsNullOrWhiteSpace(alternate))
        {
            return alternate.Trim();
        }

        return fallback;
    }

    private static bool LooksLikeHub(string instanceId, string? displayName, string? deviceClass)
    {
        if (instanceId.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (instanceId.Contains("ROOT_DEVICE_ROUTER", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(deviceClass, "USBHUB", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        return displayName.Contains("hub", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("集线器", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("router", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("路由器", StringComparison.OrdinalIgnoreCase);
    }
}
