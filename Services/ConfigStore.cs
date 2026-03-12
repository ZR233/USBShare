using System.Text.Json;
using USBShare.Models;

namespace USBShare.Services;

public interface IConfigStore
{
    Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default);
}

public sealed class ConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(AppPaths.ConfigFilePath))
        {
            return new AppConfig();
        }

        var content = await File.ReadAllTextAsync(AppPaths.ConfigFilePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
        {
            return new AppConfig();
        }

        var config = JsonSerializer.Deserialize<AppConfig>(content, SerializerOptions);
        return config ?? new AppConfig();
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(config, SerializerOptions);
        await File.WriteAllTextAsync(AppPaths.ConfigFilePath, payload, cancellationToken).ConfigureAwait(false);
    }
}
