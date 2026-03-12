namespace USBShare.Models;

/// <summary>
/// 旧的规则解析结果模型。
/// 已被 <see cref="DeviceEnabledResult"/> 替代。
/// 仅保留用于向后兼容，请勿在新代码中使用。
/// </summary>
[Obsolete("使用 DeviceEnabledResult 代替")]
public sealed class RuleResolutionResult
{
    public Dictionary<string, Guid> TargetsByBusId { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ConflictsByInstanceId { get; } = new(StringComparer.OrdinalIgnoreCase);
}
