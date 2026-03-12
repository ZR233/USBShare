using System.Text.Json;
using USBShare.Models;

namespace USBShare.Services;

public sealed record UsbipBindResult(bool Success, bool BoundBySession, bool PermissionDenied, string Message);
public sealed record UsbipCommandResult(bool Success, bool PermissionDenied, string Message);

public interface IUsbipdService
{
    Task<UsbipStateSnapshot> GetStateAsync(CancellationToken cancellationToken = default);
    Task<UsbipBindResult> EnsureBoundAsync(string busId, CancellationToken cancellationToken = default);
    Task<UsbipCommandResult> UnbindAsync(string busId, CancellationToken cancellationToken = default);
}

public sealed class UsbipdService : IUsbipdService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

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
}
