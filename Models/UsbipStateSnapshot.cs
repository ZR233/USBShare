using System.Text.Json.Serialization;

namespace USBShare.Models;

public sealed class UsbipStateSnapshot
{
    [JsonPropertyName("Devices")]
    public List<UsbipStateDevice> Devices { get; set; } = [];
}

public sealed class UsbipStateDevice
{
    [JsonPropertyName("BusId")]
    public string BusId { get; set; } = string.Empty;

    [JsonPropertyName("ClientIPAddress")]
    public string? ClientIPAddress { get; set; }

    [JsonPropertyName("Description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("InstanceId")]
    public string InstanceId { get; set; } = string.Empty;

    [JsonPropertyName("IsForced")]
    public bool IsForced { get; set; }

    [JsonPropertyName("PersistedGuid")]
    public string? PersistedGuid { get; set; }

    [JsonPropertyName("StubInstanceId")]
    public string? StubInstanceId { get; set; }
}
