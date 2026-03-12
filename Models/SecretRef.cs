namespace USBShare.Models;

public sealed class SecretRef
{
    public Guid RemoteId { get; set; }
    public SecretKind SecretKind { get; set; }
}
