namespace USBShare.Models;

public sealed class RuleResolutionResult
{
    public Dictionary<string, Guid> TargetsByBusId { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ConflictsByInstanceId { get; } = new(StringComparer.OrdinalIgnoreCase);
}
