namespace USBShare.Models;

public sealed class UsbTopologyNode
{
    public string InstanceId { get; set; } = string.Empty;
    public string? ParentInstanceId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string DeviceClass { get; set; } = string.Empty;
    public bool IsHub { get; set; }
    public bool IsShareable { get; set; }
    public string? BusId { get; set; }
    public List<string> Children { get; set; } = [];
}

public sealed class UsbTopologySnapshot
{
    public Dictionary<string, UsbTopologyNode> Nodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<UsbTopologyNode> RootNodes { get; set; } = [];
}
