using System.Security.Cryptography;
using System.Text;
using USBShare.Models;

namespace USBShare.Services;

public interface ISecretStore
{
    Task SaveSecretAsync(Guid remoteId, SecretKind kind, string secret, CancellationToken cancellationToken = default);
    Task<string?> GetSecretAsync(Guid remoteId, SecretKind kind, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(Guid remoteId, SecretKind kind, CancellationToken cancellationToken = default);
}

public sealed class SecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("USBShare.Local.Secret.v1");

    public async Task SaveSecretAsync(Guid remoteId, SecretKind kind, string secret, CancellationToken cancellationToken = default)
    {
        var raw = Encoding.UTF8.GetBytes(secret);
        var encrypted = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(GetSecretPath(remoteId, kind), encrypted, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GetSecretAsync(Guid remoteId, SecretKind kind, CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(remoteId, kind);
        if (!File.Exists(path))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var raw = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(raw);
    }

    public Task DeleteSecretAsync(Guid remoteId, SecretKind kind, CancellationToken cancellationToken = default)
    {
        var path = GetSecretPath(remoteId, kind);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private static string GetSecretPath(Guid remoteId, SecretKind kind)
    {
        return Path.Combine(AppPaths.SecretsDirectory, $"{remoteId:N}.{kind}.bin");
    }
}
