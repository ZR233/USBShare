namespace USBShare.Models;

/// <summary>
/// usbipd list 命令的解析结果
/// </summary>
public sealed class UsbipListResult
{
    /// <summary>
    /// 当前已连接/可用的 USB 设备列表
    /// </summary>
    public List<UsbipListDevice> Devices { get; set; } = [];
}

/// <summary>
/// usbipd list 输出中的单个设备
/// </summary>
public sealed class UsbipListDevice
{
    /// <summary>
    /// USB 总线 ID (如 "1-2", "2-5")
    /// </summary>
    public string BusId { get; set; } = string.Empty;

    /// <summary>
    /// 设备描述名称
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 设备状态 (Shared, Attached, 或空)
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// 设备实例 ID (USB\VID_xxxx&PID_xxxx...)
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 是否已绑定 (Shared 或 Attached 状态)
    /// </summary>
    public bool IsBound =>
        State.Equals("Shared", StringComparison.OrdinalIgnoreCase) ||
        State.Equals("Attached", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否已附加
    /// </summary>
    public bool IsAttached =>
        State.Equals("Attached", StringComparison.OrdinalIgnoreCase);
}
