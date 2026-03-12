namespace USBShare.Models;

public sealed class ShareRule
{
    public RuleNodeType NodeType { get; set; }
    public string NodeInstanceId { get; set; } = string.Empty;
    public Guid RemoteId { get; set; }
    public bool Enabled { get; set; } = true;
}
