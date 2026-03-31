using USBShare.Models;

namespace USBShare.Services;

public interface IConfigStore
{
    Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default);
}

public sealed class ConfigStore : IConfigStore
{
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

        var config = System.Text.Json.JsonSerializer.Deserialize(content, AppJsonSerializerContext.Default.AppConfig);
        return config ?? new AppConfig();
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(config, AppJsonSerializerContext.Default.AppConfig);
        await File.WriteAllTextAsync(AppPaths.ConfigFilePath, payload, cancellationToken).ConfigureAwait(false);
    }
}
