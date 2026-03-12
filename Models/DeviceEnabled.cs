namespace USBShare.Models;

/// <summary>
/// 表示一个 USB 设备或 Hub 的启用分享状态。
/// 与旧版的 ShareRule 不同，这里不包含远程选择信息，
/// 因为所有设备都统一分享到同一个选中的远程服务器。
/// </summary>
public sealed class DeviceEnabled
{
    /// <summary>
    /// 节点的实例 ID（设备或 Hub）。
    /// </summary>
    public string NodeInstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用分享。启用后，该设备（或 Hub 下的所有设备）将分享到选中的远程服务器。
    /// </summary>
    public bool Enabled { get; set; } = true;
}
