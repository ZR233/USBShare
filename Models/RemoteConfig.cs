using System.Text.Json.Serialization;

namespace USBShare.Models;

public sealed class RemoteConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string User { get; set; } = string.Empty;
    public AuthType AuthType { get; set; } = AuthType.PrivateKey;
    public string? KeyPath { get; set; }
    public int TunnelPort { get; set; } = 3240;

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Name) ? $"{User}@{Host}" : Name;

    [JsonIgnore]
    public string DisplaySubtitle => $"{User}@{Host}:{Port} | Tunnel:{TunnelPort} | {AuthType}";
}
