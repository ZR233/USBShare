namespace USBShare.Models;

/// <summary>
/// PnP 设备节点原始数据
/// 从 CfgMgr32/SetupAPI 获取的 USB 设备树节点
/// </summary>
public sealed class PnpDeviceNode
{
    /// <summary>
    /// 设备实例 ID (如 "USB\VID_xxxx&amp;PID_xxxx\...")
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 父设备的实例 ID
    /// </summary>
    public string? ParentInstanceId { get; set; }

    /// <summary>
    /// 设备友好名称（用户可见名称）
    /// </summary>
    public string? FriendlyName { get; set; }

    /// <summary>
    /// 设备类（如 "USB", "HIDCLASS", "DiskDevice" 等）
    /// </summary>
    public string? DeviceClass { get; set; }

    /// <summary>
    /// 设备描述
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 制造商
    /// </summary>
    public string? Manufacturer { get; set; }

    /// <summary>
    /// 子设备列表
    /// </summary>
    public List<PnpDeviceNode> Children { get; set; } = [];

    /// <summary>
    /// 判断此设备是否为 USB Hub
    /// </summary>
    public bool IsHub =>
        InstanceId.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase) ||
        InstanceId.Contains("ROOT_DEVICE_ROUTER", StringComparison.OrdinalIgnoreCase) ||
        FriendlyName?.Contains("hub", StringComparison.OrdinalIgnoreCase) == true ||
        FriendlyName?.Contains("集线器", StringComparison.OrdinalIgnoreCase) == true ||
        FriendlyName?.Contains("router", StringComparison.OrdinalIgnoreCase) == true ||
        FriendlyName?.Contains("路由器", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 判断此设备是否为 USB 根设备（没有父设备的 USB 设备）
    /// </summary>
    public bool IsRoot =>
        string.IsNullOrWhiteSpace(ParentInstanceId);
}
