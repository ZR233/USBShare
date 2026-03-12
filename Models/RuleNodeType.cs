namespace USBShare.Models;

/// <summary>
/// 旧的规则节点类型枚举。
/// 已被 <see cref="DeviceEnabled"/> 替代。
/// 仅保留用于配置迁移，请勿在新代码中使用。
/// </summary>
[Obsolete("此类型仅用于配置迁移")]
public enum RuleNodeType
{
    Hub = 0,
    Device = 1,
}
