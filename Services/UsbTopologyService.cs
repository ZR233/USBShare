using System.Text.Json;
using USBShare.Models;

namespace USBShare.Services;

public interface IUsbTopologyService
{
    Task<UsbTopologySnapshot> BuildSnapshotAsync(UsbipStateSnapshot state, CancellationToken cancellationToken = default);
}

public sealed class UsbTopologyService : IUsbTopologyService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IProcessRunner _processRunner;

    public UsbTopologyService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<UsbTopologySnapshot> BuildSnapshotAsync(UsbipStateSnapshot state, CancellationToken cancellationToken = default)
    {
        List<PnpDeviceRecord> records;
        try
        {
            records = await QueryUsbPnpDevicesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Fallback to shareable devices only when topology query fails.
            records = [];
        }

        var nodes = new Dictionary<string, UsbTopologyNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.InstanceId))
            {
                continue;
            }

            var instanceId = record.InstanceId.Trim();
            nodes[instanceId] = new UsbTopologyNode
            {
                InstanceId = instanceId,
                ParentInstanceId = string.IsNullOrWhiteSpace(record.ParentInstanceId) ? null : record.ParentInstanceId.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(record.FriendlyName) ? instanceId : record.FriendlyName.Trim(),
                DeviceClass = record.Class?.Trim() ?? string.Empty,
                IsHub = LooksLikeHub(record.InstanceId, record.FriendlyName),
            };
        }

        foreach (var device in state.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.InstanceId))
            {
                continue;
            }

            var instanceId = device.InstanceId.Trim();
            if (!nodes.TryGetValue(instanceId, out var node))
            {
                node = new UsbTopologyNode
                {
                    InstanceId = instanceId,
                    DisplayName = string.IsNullOrWhiteSpace(device.Description) ? instanceId : device.Description,
                    DeviceClass = "USB",
                    ParentInstanceId = null,
                    IsHub = false,
                };
                nodes[instanceId] = node;
            }

            node.IsShareable = !string.IsNullOrWhiteSpace(device.BusId);
            node.BusId = device.BusId;
        }

        foreach (var node in nodes.Values)
        {
            if (!string.IsNullOrWhiteSpace(node.ParentInstanceId) &&
                nodes.TryGetValue(node.ParentInstanceId, out var parent))
            {
                parent.Children.Add(node.InstanceId);
            }
        }

        foreach (var node in nodes.Values)
        {
            if (!node.IsHub && node.Children.Count > 0)
            {
                node.IsHub = LooksLikeHub(node.InstanceId, node.DisplayName) ||
                             string.Equals(node.DeviceClass, "USB", StringComparison.OrdinalIgnoreCase);
            }
        }

        var rootNodes = nodes.Values
            .Where(node => string.IsNullOrWhiteSpace(node.ParentInstanceId) || !nodes.ContainsKey(node.ParentInstanceId))
            .OrderBy(node => node.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (rootNodes.Count == 0 && nodes.Count > 0)
        {
            rootNodes = nodes.Values
                .Where(node => node.IsHub)
                .OrderBy(node => node.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        if (rootNodes.Count == 0 && nodes.Count > 0)
        {
            rootNodes = nodes.Values
                .OrderBy(node => node.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Take(64)
                .ToList();
        }

        foreach (var node in nodes.Values)
        {
            node.Children = node.Children
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(childId => nodes.TryGetValue(childId, out var childNode) ? childNode.DisplayName : childId,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        return new UsbTopologySnapshot
        {
            Nodes = nodes,
            RootNodes = rootNodes,
        };
    }

    private async Task<List<PnpDeviceRecord>> QueryUsbPnpDevicesAsync(CancellationToken cancellationToken)
    {
        const string script = """
                              $devices = Get-PnpDevice -PresentOnly |
                                Where-Object { $_.InstanceId -like 'USB*' -or $_.InstanceId -like 'USB4*' -or $_.InstanceId -like 'USBROOT*' } |
                                Select-Object InstanceId, FriendlyName, Class

                              $parents = $devices |
                                Get-PnpDeviceProperty -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue |
                                Select-Object InstanceId, Data

                              $parentById = @{}
                              foreach ($p in $parents) {
                                $parentById[$p.InstanceId] = $p.Data
                              }

                              $rows = foreach ($d in $devices) {
                                [PSCustomObject]@{
                                  InstanceId = $d.InstanceId
                                  ParentInstanceId = $parentById[$d.InstanceId]
                                  FriendlyName = $d.FriendlyName
                                  Class = $d.Class
                                }
                              }

                              $rows | ConvertTo-Json -Depth 4 -Compress
                              """;

        var encodedScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var command = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedScript}";
        var result = await _processRunner
            .RunAsync("powershell", command, timeout: TimeSpan.FromSeconds(90), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to query USB PnP topology: {result.StandardError}");
        }

        var json = result.StandardOutput;
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        json = json.Trim();
        if (json.StartsWith("{", StringComparison.Ordinal))
        {
            var single = JsonSerializer.Deserialize<PnpDeviceRecord>(json, JsonOptions);
            return single is null ? [] : [single];
        }

        var list = JsonSerializer.Deserialize<List<PnpDeviceRecord>>(json, JsonOptions);
        return list ?? [];
    }

    private static bool LooksLikeHub(string instanceId, string? displayName)
    {
        if (instanceId.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (instanceId.Contains("ROOT_DEVICE_ROUTER", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        return displayName.Contains("hub", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("集线器", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("router", StringComparison.OrdinalIgnoreCase)
               || displayName.Contains("路由器", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PnpDeviceRecord
    {
        public string InstanceId { get; set; } = string.Empty;
        public string? ParentInstanceId { get; set; }
        public string? FriendlyName { get; set; }
        public string? Class { get; set; }
    }
}
