namespace USBShare.Models;

public sealed class AppConfig
{
    public List<RemoteConfig> Remotes { get; set; } = [];
    /// <summary>
    /// 启用分享的设备列表。
    /// 所有这些设备将分享到 Settings.SelectedRemoteId 指定的远程服务器。
    /// </summary>
    public List<DeviceEnabled> EnabledDevices { get; set; } = [];
    public List<SecretRef> SecretRefs { get; set; } = [];
    public AppSettings Settings { get; set; } = new();

    /// <summary>
    /// 旧版 ShareRules，用于配置迁移。
    /// 迁移完成后可以删除此属性。
    /// </summary>
    [Obsolete("使用 EnabledDevices 代替")]
    public List<ShareRule>? ShareRules { get; set; }
}
