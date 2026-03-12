using USBShare.Models;

namespace USBShare.Services;

/// <summary>
/// 配置迁移服务，将旧版本的 ShareRules 迁移到新版本的 EnabledDevices 模型。
/// </summary>
public static class ConfigMigration
{
    /// <summary>
    /// 检查配置是否需要迁移。
    /// </summary>
    public static bool NeedsMigration(AppConfig config)
    {
        // 如果没有 EnabledDevices 但有旧的 ShareRules，则需要迁移
        return (config.EnabledDevices.Count == 0) && (config.ShareRules?.Count > 0);
    }

    /// <summary>
    /// 执行配置迁移。
    /// 将旧的 ShareRules 转换为新的 EnabledDevices，
    /// 并选择最常用的远程作为 SelectedRemoteId。
    /// </summary>
    public static void Migrate(AppConfig config)
    {
        if (!NeedsMigration(config))
        {
            return;
        }

        var oldRules = config.ShareRules;
        if (oldRules is null || oldRules.Count == 0)
        {
            return;
        }

        // 统计每个远程被使用的次数
        var remoteUsageCount = new Dictionary<Guid, int>();
        foreach (var rule in oldRules.Where(r => r.Enabled))
        {
            var remoteId = rule.RemoteId;
            if (!remoteUsageCount.ContainsKey(remoteId))
            {
                remoteUsageCount[remoteId] = 0;
            }
            remoteUsageCount[remoteId]++;
        }

        // 选择最常用的远程
        Guid? selectedRemoteId = null;
        int maxUsage = 0;
        foreach (var pair in remoteUsageCount)
        {
            if (pair.Value > maxUsage)
            {
                maxUsage = pair.Value;
                selectedRemoteId = pair.Key;
            }
        }

        // 如果没有启用的规则，选择第一个可用的远程
        if (!selectedRemoteId.HasValue && config.Remotes.Count > 0)
        {
            selectedRemoteId = config.Remotes[0].Id;
        }

        // 设置选中的远程
        config.Settings.SelectedRemoteId = selectedRemoteId;

        // 转换规则：只保留指向选中远程的规则，转换为启用状态
        var enabledDevices = new List<DeviceEnabled>();
        foreach (var rule in oldRules.Where(r =>
            r.Enabled &&
            (selectedRemoteId.HasValue == false || r.RemoteId == selectedRemoteId.Value)))
        {
            enabledDevices.Add(new DeviceEnabled
            {
                NodeInstanceId = rule.NodeInstanceId,
                Enabled = true,
            });
        }

        config.EnabledDevices = enabledDevices;

        // 可选：清除旧规则以释放空间
        config.ShareRules = null;
    }
}
