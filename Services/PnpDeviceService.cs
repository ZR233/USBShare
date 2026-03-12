using System.Text;
using USBShare.Models;
using USBShare.Services.Native;

namespace USBShare.Services;

/// <summary>
/// PnP 设备服务接口
/// </summary>
public interface IPnpDeviceService
{
    /// <summary>
    /// 获取 USB 设备树
    /// </summary>
    Task<List<PnpDeviceNode>> GetUsbDeviceTreeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// PnP 设备服务实现
/// 使用 Windows CfgMgr32 API 获取完整的 USB 设备树
/// </summary>
public sealed class PnpDeviceService : IPnpDeviceService
{
    // 最大设备数量限制
    private const int MaxDeviceCount = 1024;
    // 最大树深度限制
    private const int MaxTreeDepth = 15;

    public PnpDeviceService()
    {
    }

    public async Task<List<PnpDeviceNode>> GetUsbDeviceTreeAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                // 获取所有 USB 设备的 InstanceId
                var deviceIds = GetUsbDeviceIds();

                if (deviceIds.Count == 0)
                {
                    return [];
                }

                // 限制数量
                if (deviceIds.Count > MaxDeviceCount)
                {
                    deviceIds = deviceIds.Take(MaxDeviceCount).ToList();
                }

                // 收集设备与其 Hub 祖先（使用不可变记录避免循环引用）
                var nodeDataDict = new Dictionary<string, PnpDeviceNodeData>(StringComparer.OrdinalIgnoreCase);
                foreach (var deviceId in deviceIds)
                {
                    try
                    {
                        CollectDeviceAndHubAncestors(deviceId, nodeDataDict);
                    }
                    catch
                    {
                        // 跳过失败的设备
                    }
                }

                // 构建树结构
                return BuildTree(nodeDataDict);
            }
            catch
            {
                return [];
            }
        }, cancellationToken);
    }

    /// <summary>
    /// 收集设备本身以及向上的 Hub 祖先链。
    /// </summary>
    private void CollectDeviceAndHubAncestors(
        string startInstanceId,
        Dictionary<string, PnpDeviceNodeData> nodeDataDict)
    {
        var visitedInChain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = startInstanceId;
        var depth = 0;

        while (!string.IsNullOrWhiteSpace(cursor) && depth < MaxTreeDepth)
        {
            depth++;
            var currentId = cursor.Trim();
            if (currentId.Length <= 3 || currentId.Length >= 256)
            {
                break;
            }

            if (!visitedInChain.Add(currentId))
            {
                break;
            }

            if (!nodeDataDict.TryGetValue(currentId, out var current))
            {
                current = GetDeviceInfoData(currentId);
                if (current is null)
                {
                    break;
                }

                nodeDataDict[current.InstanceId] = current;
            }

            if (string.IsNullOrWhiteSpace(current.ParentInstanceId))
            {
                break;
            }

            var parentId = current.ParentInstanceId.Trim();
            if (parentId.Equals(currentId, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!IsUsbLikeInstanceId(parentId))
            {
                break;
            }

            if (!nodeDataDict.TryGetValue(parentId, out var parent))
            {
                parent = GetDeviceInfoData(parentId);
                if (parent is null)
                {
                    break;
                }

                nodeDataDict[parent.InstanceId] = parent;
            }

            cursor = parent.InstanceId;
        }
    }

    /// <summary>
    /// 设备信息数据结构（不包含 Children，避免循环引用）
    /// </summary>
    private record PnpDeviceNodeData
    {
        public string InstanceId { get; init; } = string.Empty;
        public string? ParentInstanceId { get; init; }
        public string? FriendlyName { get; init; }
        public string? DeviceClass { get; init; }
    }

    /// <summary>
    /// 获取所有 USB 设备的 InstanceId
    /// </summary>
    private List<string> GetUsbDeviceIds()
    {
        var deviceIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // 查询系统中所有设备，再本地筛选 USB 前缀，兼容不同机器上的 CfgMgr 过滤行为差异。
            var result = CfgMgr32.CM_Get_Device_ID_List_Size(out uint bufferSize, null, 0);

            if (result != CfgMgr32.CR_SUCCESS || bufferSize <= 1 || bufferSize > 1_048_576)
            {
                return deviceIds;
            }

            // 获取 MULTI_SZ 结果；CfgMgr 这里应使用 char[]，StringBuilder 会导致无效指针错误。
            var buffer = new char[bufferSize];
            result = CfgMgr32.CM_Get_Device_ID_List(null, buffer, bufferSize, 0);

            if (result != CfgMgr32.CR_SUCCESS)
            {
                return deviceIds;
            }

            // 解析结果
            var bufferString = new string(buffer);
            var parts = bufferString.Split('\0', StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                var instanceId = part.Trim();
                if (instanceId.Length <= 3 || instanceId.Length >= 512)
                {
                    continue;
                }

                if (!IsUsbLikeInstanceId(instanceId))
                {
                    continue;
                }

                if (seen.Add(instanceId))
                {
                    deviceIds.Add(instanceId);
                }

                if (deviceIds.Count >= MaxDeviceCount)
                {
                    break;
                }
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (BadImageFormatException)
        {
        }
        catch
        {
        }

        return deviceIds;
    }

    /// <summary>
    /// 获取单个设备的信息数据
    /// </summary>
    private PnpDeviceNodeData? GetDeviceInfoData(string instanceId)
    {
        var result = CfgMgr32.CM_Locate_DevNode(out uint devInst, instanceId, 0);
        if (result != CfgMgr32.CR_SUCCESS)
        {
            return null;
        }

        // 获取友好名称
        var friendlyName = GetDeviceProperty(devInst, CfgMgr32.DevicePropertyKeys.DEVPKEY_Device_FriendlyName);
        friendlyName ??= GetDeviceProperty(devInst, CfgMgr32.DevicePropertyKeys.DEVPKEY_Name);

        // 获取设备类
        var deviceClass = GetDeviceProperty(devInst, CfgMgr32.DevicePropertyKeys.DEVPKEY_Device_Class);

        // 获取父设备
        string? parentInstanceId = null;
        result = CfgMgr32.CM_Get_Parent(out uint parentDevInst, devInst, 0);
        if (result == CfgMgr32.CR_SUCCESS)
        {
            parentInstanceId = GetDeviceId(parentDevInst);
        }

        return new PnpDeviceNodeData
        {
            InstanceId = instanceId,
            ParentInstanceId = parentInstanceId,
            FriendlyName = friendlyName,
            DeviceClass = deviceClass,
        };
    }

    /// <summary>
    /// 获取设备属性（字符串）
    /// </summary>
    private string? GetDeviceProperty(uint devInst, in CfgMgr32.DEVPROPKEY propertyKey)
    {
        var buffer = new StringBuilder(256);
        var bufferSize = (uint)buffer.Capacity;

        var result = CfgMgr32.CM_Get_DevNode_PropertyW(
            devInst,
            propertyKey,
            out var propType,
            buffer,
            ref bufferSize,
            0);

        if (result != CfgMgr32.CR_SUCCESS || propType != CfgMgr32.DEVPROP_TYPE_STRING)
        {
            return null;
        }

        return buffer.ToString();
    }

    /// <summary>
    /// 获取设备的 InstanceId
    /// </summary>
    private string? GetDeviceId(uint devInst)
    {
        var buffer = new StringBuilder(256);
        var bufferSize = (uint)buffer.Capacity;
        var result = CfgMgr32.CM_Get_Device_ID(devInst, buffer, bufferSize, 0);

        if (result != CfgMgr32.CR_SUCCESS)
        {
            return null;
        }

        return buffer.ToString();
    }

    /// <summary>
    /// 从设备数据字典构建树结构（带循环检测）
    /// </summary>
    private List<PnpDeviceNode> BuildTree(Dictionary<string, PnpDeviceNodeData> nodeDataDict)
    {
        // 创建节点字典
        var nodes = new Dictionary<string, PnpDeviceNode>(StringComparer.OrdinalIgnoreCase);
        var processedPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 第一遍：创建所有节点
        foreach (var data in nodeDataDict.Values)
        {
            nodes[data.InstanceId] = new PnpDeviceNode
            {
                InstanceId = data.InstanceId,
                ParentInstanceId = data.ParentInstanceId,
                FriendlyName = data.FriendlyName,
                DeviceClass = data.DeviceClass,
                Children = []
            };
        }

        // 第二遍：建立父子关系（带循环检测）
        foreach (var data in nodeDataDict.Values)
        {
            if (string.IsNullOrWhiteSpace(data.ParentInstanceId))
            {
                continue;
            }

            var parentId = data.ParentInstanceId!;
            var childId = data.InstanceId;

            // 防止自引用
            if (parentId.Equals(childId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 防止重复处理
            var pairKey = $"{parentId}|{childId}";
            if (processedPairs.Contains(pairKey))
            {
                continue;
            }
            processedPairs.Add(pairKey);

            if (nodes.TryGetValue(parentId, out var parent) &&
                nodes.TryGetValue(childId, out var child))
            {
                // 防止重复添加
                if (!parent.Children.Any(c => c.InstanceId.Equals(childId, StringComparison.OrdinalIgnoreCase)))
                {
                    parent.Children.Add(child);
                }
            }
        }

        // 返回根节点列表
        return nodes.Values
            .Where(n => string.IsNullOrWhiteSpace(n.ParentInstanceId) || !nodes.ContainsKey(n.ParentInstanceId))
            .ToList();
    }

    private static bool LooksLikeHub(PnpDeviceNodeData data)
    {
        if (data.InstanceId.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase) ||
            data.InstanceId.Contains("ROOT_DEVICE_ROUTER", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (data.FriendlyName?.Contains("hub", StringComparison.OrdinalIgnoreCase) == true ||
            data.FriendlyName?.Contains("集线器", StringComparison.OrdinalIgnoreCase) == true ||
            data.FriendlyName?.Contains("router", StringComparison.OrdinalIgnoreCase) == true ||
            data.FriendlyName?.Contains("路由器", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return data.DeviceClass?.Equals("USBHUB", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsUsbLikeInstanceId(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        var id = instanceId.Trim();
        return id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("USB4\\", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith("USBROOT\\", StringComparison.OrdinalIgnoreCase)
               || id.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase);
    }
}
