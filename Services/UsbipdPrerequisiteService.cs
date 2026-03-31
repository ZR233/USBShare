using System.Diagnostics;

namespace USBShare.Services;

public enum UsbipdInstallState
{
    InstalledReady,
    Missing,
    Unavailable,
}

public sealed class UsbipdPrerequisiteStatus
{
    public UsbipdInstallState State { get; init; }
    public bool CommandAvailable { get; init; }
    public bool ServiceAvailable { get; init; }
    public bool WingetAvailable { get; init; }
    public string DiagnosticMessage { get; init; } = string.Empty;
    public bool IsReady => State == UsbipdInstallState.InstalledReady;
}

public interface IUsbipdPrerequisiteService
{
    Task<UsbipdPrerequisiteStatus> CheckAsync(CancellationToken cancellationToken = default);
    Task<UsbipdPrerequisiteStatus> WaitForInstalledAsync(TimeSpan timeout, TimeSpan pollInterval, CancellationToken cancellationToken = default);
    bool TryLaunchWingetInstall();
    bool OpenOfficialInstallPage();
}

public sealed class UsbipdPrerequisiteService : IUsbipdPrerequisiteService
{
    private const string UsbipdServiceName = "usbipd";
    private const string UsbipdInstallUrl = "https://github.com/dorssel/usbipd-win";

    private readonly IProcessRunner _processRunner;

    public UsbipdPrerequisiteService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<UsbipdPrerequisiteStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var commandCheck = await TryRunAsync("where.exe", "usbipd", cancellationToken).ConfigureAwait(false);
        var serviceCheck = await TryRunAsync("sc.exe", $"query {UsbipdServiceName}", cancellationToken).ConfigureAwait(false);
        var wingetCheck = await TryRunAsync("where.exe", "winget", cancellationToken).ConfigureAwait(false);

        var commandAvailable = commandCheck.IsSuccess && !string.IsNullOrWhiteSpace(commandCheck.StandardOutput);
        var serviceAvailable = serviceCheck.MergedOutput.Contains($"SERVICE_NAME: {UsbipdServiceName}", StringComparison.OrdinalIgnoreCase);
        var wingetAvailable = wingetCheck.IsSuccess && !string.IsNullOrWhiteSpace(wingetCheck.StandardOutput);

        var state = commandAvailable
            ? UsbipdInstallState.InstalledReady
            : serviceAvailable
                ? UsbipdInstallState.Unavailable
                : UsbipdInstallState.Missing;

        return new UsbipdPrerequisiteStatus
        {
            State = state,
            CommandAvailable = commandAvailable,
            ServiceAvailable = serviceAvailable,
            WingetAvailable = wingetAvailable,
            DiagnosticMessage = string.Join(
                Environment.NewLine,
                new[] { commandCheck.MergedOutput, serviceCheck.MergedOutput }
                    .Where(text => !string.IsNullOrWhiteSpace(text))),
        };
    }

    public async Task<UsbipdPrerequisiteStatus> WaitForInstalledAsync(
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        UsbipdPrerequisiteStatus lastStatus = await CheckAsync(cancellationToken).ConfigureAwait(false);
        while (!lastStatus.IsReady && DateTimeOffset.UtcNow - startedAt < timeout)
        {
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            lastStatus = await CheckAsync(cancellationToken).ConfigureAwait(false);
        }

        return lastStatus;
    }

    public bool TryLaunchWingetInstall()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "winget.exe",
                Arguments = "install --interactive --exact dorssel.usbipd-win",
                UseShellExecute = true,
                WorkingDirectory = Environment.CurrentDirectory,
            });

            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    public bool OpenOfficialInstallPage()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = UsbipdInstallUrl,
                UseShellExecute = true,
            });

            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task<CommandProbeResult> TryRunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner
                .RunAsync(fileName, arguments, timeout: TimeSpan.FromSeconds(5), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return new CommandProbeResult(result.IsSuccess, result.StandardOutput, result.StandardError);
        }
        catch (Exception ex)
        {
            return new CommandProbeResult(false, string.Empty, ex.Message);
        }
    }

    private sealed record CommandProbeResult(bool IsSuccess, string StandardOutput, string StandardError)
    {
        public string MergedOutput => $"{StandardOutput}{Environment.NewLine}{StandardError}".Trim();
    }
}
