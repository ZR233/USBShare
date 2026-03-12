namespace USBShare.Models;

public sealed class ShareSessionState
{
    public bool IsRunning { get; set; }
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.MinValue;
    public Dictionary<string, string> ConflictsByInstanceId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> LastErrorsByKey { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 已通过 usbipd bind 的设备 BusId 集合（当前会话绑定）。
    /// </summary>
    public HashSet<string> BoundBusIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 已在远程附加（attach）的设备 BusId 集合。
    /// </summary>
    public HashSet<string> AttachedBusIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
