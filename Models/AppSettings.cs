namespace USBShare.Models;

public sealed class AppSettings
{
    public int PollIntervalSeconds { get; set; } = 3;
    /// <summary>
    /// 当前选中的分享目标远程服务器 ID。
    /// 所有启用的 USB 设备都将分享到此服务器。
    /// </summary>
    public Guid? SelectedRemoteId { get; set; }
}
