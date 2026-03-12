namespace USBShare.Models;

public sealed class AppConfig
{
    public List<RemoteConfig> Remotes { get; set; } = [];
    public List<ShareRule> ShareRules { get; set; } = [];
    public List<SecretRef> SecretRefs { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}
