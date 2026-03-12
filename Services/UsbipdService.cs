using System.Text.Json;
using System.Text.RegularExpressions;
using USBShare.Models;

namespace USBShare.Services;

public sealed record UsbipBindResult(bool Success, bool BoundBySession, bool PermissionDenied, string Message);
public sealed record UsbipCommandResult(bool Success, bool PermissionDenied, string Message);

public interface IUsbipdService
{
    Task<UsbipStateSnapshot> GetStateAsync(CancellationToken cancellationToken = default);
    Task<UsbipListResult> GetListAsync(CancellationToken cancellationToken = default);
    Task<UsbipBindResult> EnsureBoundAsync(string busId, CancellationToken cancellationToken = default);
    Task<UsbipCommandResult> UnbindAsync(string busId, CancellationToken cancellationToken = default);
}

public sealed class UsbipdService : IUsbipdService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly Regex ListRowRegex = new(
        @"^\s*(?<busid>\S+)\s+(?<vidpid>[0-9A-Fa-f]{4}:[0-9A-Fa-f]{4})\s+(?<tail>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IProcessRunner _processRunner;

    public UsbipdService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<UsbipStateSnapshot> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var result = await _processRunner
            .RunAsync("usbipd", "state", timeout: TimeSpan.FromSeconds(20), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"usbipd state failed: {MergeOutput(result)}");
        }

        var snapshot = JsonSerializer.Deserialize<UsbipStateSnapshot>(result.StandardOutput, JsonOptions);
        return snapshot ?? new UsbipStateSnapshot();
    }

    public async Task<UsbipListResult> GetListAsync(CancellationToken cancellationToken = default)
    {
        var result = await _processRunner
            .RunAsync("usbipd", "list", timeout: TimeSpan.FromSeconds(20), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"usbipd list failed: {MergeOutput(result)}");
        }

        return ParseUsbipdListOutput(result.StandardOutput);
    }

    public async Task<UsbipBindResult> EnsureBoundAsync(string busId, CancellationToken cancellationToken = default)
    {
        var result = await _processRunner
            .RunAsync("usbipd", $"bind --busid {busId}", timeout: TimeSpan.FromSeconds(20), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var output = MergeOutput(result);
        var permissionDenied = IsPermissionDenied(output);

        if (result.IsSuccess)
        {
            var alreadyBound = IsAlreadyBound(output);
            return new UsbipBindResult(
                Success: true,
                BoundBySession: !alreadyBound,
                PermissionDenied: false,
                Message: output);
        }

        if (IsAlreadyBound(output))
        {
            return new UsbipBindResult(
                Success: true,
                BoundBySession: false,
                PermissionDenied: false,
                Message: output);
        }

        return new UsbipBindResult(
            Success: false,
            BoundBySession: false,
            PermissionDenied: permissionDenied,
            Message: output);
    }

    public async Task<UsbipCommandResult> UnbindAsync(string busId, CancellationToken cancellationToken = default)
    {
        var result = await _processRunner
            .RunAsync("usbipd", $"unbind --busid {busId}", timeout: TimeSpan.FromSeconds(20), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var output = MergeOutput(result);
        var permissionDenied = IsPermissionDenied(output);

        if (result.IsSuccess || IsNotBound(output))
        {
            return new UsbipCommandResult(
                Success: true,
                PermissionDenied: false,
                Message: output);
        }

        return new UsbipCommandResult(
            Success: false,
            PermissionDenied: permissionDenied,
            Message: output);
    }

    private static string MergeOutput(ProcessResult result)
    {
        return $"{result.StandardOutput}\n{result.StandardError}".Trim();
    }

    private static bool IsPermissionDenied(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("administrator", StringComparison.OrdinalIgnoreCase)
            || text.Contains("elevat", StringComparison.OrdinalIgnoreCase)
            || text.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase)
            || text.Contains("管理员", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAlreadyBound(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("already", StringComparison.OrdinalIgnoreCase)
            || text.Contains("shared", StringComparison.OrdinalIgnoreCase)
            || text.Contains("正在共享", StringComparison.OrdinalIgnoreCase)
            || text.Contains("已共享", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNotBound(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("not shared", StringComparison.OrdinalIgnoreCase)
            || text.Contains("No devices", StringComparison.OrdinalIgnoreCase)
            || text.Contains("未共享", StringComparison.OrdinalIgnoreCase)
            || text.Contains("找不到", StringComparison.OrdinalIgnoreCase);
    }

    private static UsbipListResult ParseUsbipdListOutput(string output)
    {
        var result = new UsbipListResult();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || IsListHeaderLine(line))
            {
                continue;
            }

            if (TryParseDeviceLine(line, out var device))
            {
                result.Devices.Add(device);
            }
        }

        return result;
    }

    private static bool TryParseDeviceLine(string line, out UsbipListDevice device)
    {
        device = new UsbipListDevice();
        var match = ListRowRegex.Match(line);
        if (!match.Success)
        {
            return false;
        }

        var busId = match.Groups["busid"].Value.Trim();
        if (string.IsNullOrWhiteSpace(busId) || !busId.Contains('-', StringComparison.Ordinal))
        {
            return false;
        }

        var tail = match.Groups["tail"].Value.Trim();
        if (string.IsNullOrWhiteSpace(tail))
        {
            return false;
        }

        var parts = Regex.Split(tail, @"\s{2,}")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        string description;
        string state;
        if (parts.Length >= 2)
        {
            state = parts[^1].Trim();
            description = string.Join(" ", parts[..^1]).Trim();
        }
        else
        {
            description = tail;
            state = string.Empty;
        }

        device = new UsbipListDevice
        {
            BusId = busId,
            Description = description,
            State = state,
            InstanceId = string.Empty,
        };

        return true;
    }

    private static bool IsListHeaderLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith("Connected", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Persisted", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("BUSID", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Devices", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Shared", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("State", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return trimmed.All(ch => ch == '-' || char.IsWhiteSpace(ch));
    }
}
