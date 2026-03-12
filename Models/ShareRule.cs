namespace USBShare.Models;

/// <summary>
/// 旧的分享规则模型。
/// 已被 <see cref="DeviceEnabled"/> 替代。
/// 仅保留用于配置迁移，请勿在新代码中使用。
/// </summary>
[Obsolete("使用 DeviceEnabled 代替，此类型仅用于配置迁移")]
public sealed class ShareRule
{
    public RuleNodeType NodeType { get; set; }
    public string NodeInstanceId { get; set; } = string.Empty;
    public Guid RemoteId { get; set; }
    public bool Enabled { get; set; } = true;
}
