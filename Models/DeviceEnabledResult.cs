namespace USBShare.Models;

/// <summary>
/// 设备启用解析结果。
/// 包含所有应该分享的设备的 BusId 列表。
/// </summary>
public sealed class DeviceEnabledResult
{
    /// <summary>
    /// 应该分享的设备的 BusId 列表（键为 BusId，值为 true 表示启用）。
    /// </summary>
    public Dictionary<string, bool> EnabledBusIds { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 没有启用但有启用规则祖先的设备（会被自动包含）。
    /// </summary>
    public HashSet<string> InheritedBusIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}
