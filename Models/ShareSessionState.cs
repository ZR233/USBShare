namespace USBShare.Models;

public sealed class ShareSessionState
{
    public bool IsRunning { get; set; }
    public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.MinValue;
    public Dictionary<string, string> ConflictsByInstanceId { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> LastErrorsByKey { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
