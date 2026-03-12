using USBShare.Models;

namespace USBShare.Services;

/// <summary>
/// 解析哪些 USB 设备应该启用分享。
/// 与旧的 RuleResolver 不同，这里不处理远程选择，
/// 而是仅解析启用状态，并处理 Hub 层级的继承关系。
/// </summary>
public interface IDeviceEnabledResolver
{
    DeviceEnabledResult Resolve(UsbTopologySnapshot topology, IReadOnlyList<DeviceEnabled> enabledDevices);
}

public sealed class DeviceEnabledResolver : IDeviceEnabledResolver
{
    public DeviceEnabledResult Resolve(UsbTopologySnapshot topology, IReadOnlyList<DeviceEnabled> enabledDevices)
    {
        var result = new DeviceEnabledResult();
        var enabledHubInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledDeviceInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 首先收集所有直接启用的节点
        foreach (var enabled in enabledDevices.Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.NodeInstanceId)))
        {
            var instanceId = enabled.NodeInstanceId.Trim();
            if (topology.Nodes.TryGetValue(instanceId, out var node))
            {
                if (node.IsHub)
                {
                    enabledHubInstanceIds.Add(instanceId);
                }
                else if (node.IsShareable && !string.IsNullOrWhiteSpace(node.BusId))
                {
                    enabledDeviceInstanceIds.Add(instanceId);
                }
            }
        }

        // 然后遍历所有可分享设备，确定是否应该启用
        foreach (var node in topology.Nodes.Values.Where(node => node.IsShareable && !node.IsHub && !string.IsNullOrWhiteSpace(node.BusId)))
        {
            var busId = node.BusId!;
            var shouldEnable = false;
            var isInherited = false;

            // 检查是否直接启用
            if (enabledDeviceInstanceIds.Contains(node.InstanceId))
            {
                shouldEnable = true;
            }
            else
            {
                // 检查是否有启用的祖先 Hub
                var ancestorHub = FindEnabledAncestorHub(node, topology.Nodes, enabledHubInstanceIds);
                if (ancestorHub is not null)
                {
                    shouldEnable = true;
                    isInherited = true;
                }
            }

            if (shouldEnable)
            {
                result.EnabledBusIds[busId] = true;
                if (isInherited)
                {
                    result.InheritedBusIds.Add(busId);
                }
            }
        }

        return result;
    }

    private static string? FindEnabledAncestorHub(
        UsbTopologyNode node,
        Dictionary<string, UsbTopologyNode> allNodes,
        HashSet<string> enabledHubIds)
    {
        var cursor = node.ParentInstanceId;
        string? lastEnabledHub = null;

        while (!string.IsNullOrWhiteSpace(cursor))
        {
            if (enabledHubIds.Contains(cursor))
            {
                lastEnabledHub = cursor;
            }

            if (!allNodes.TryGetValue(cursor, out var parent))
            {
                break;
            }

            cursor = parent.ParentInstanceId;
        }

        return lastEnabledHub;
    }
}
