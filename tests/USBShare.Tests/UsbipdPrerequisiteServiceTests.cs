using USBShare.Services;

namespace USBShare.Tests;

public sealed class UsbipdPrerequisiteServiceTests
{
    [Fact]
    public async Task CheckAsync_WhenUsbipdExists_ShouldReturnInstalledReady()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (file, arguments) => (file, arguments) switch
            {
                ("where.exe", "usbipd") => Success(@"C:\Program Files\usbipd-win\usbipd.exe"),
                ("sc.exe", "query usbipd") => Success("SERVICE_NAME: usbipd"),
                ("where.exe", "winget") => Success(@"C:\Windows\System32\winget.exe"),
                _ => throw new InvalidOperationException($"Unexpected command: {file} {arguments}")
            },
        };

        var service = new UsbipdPrerequisiteService(runner);
        var status = await service.CheckAsync();

        Assert.Equal(UsbipdInstallState.InstalledReady, status.State);
        Assert.True(status.CommandAvailable);
        Assert.True(status.ServiceAvailable);
        Assert.True(status.WingetAvailable);
    }

    [Fact]
    public async Task CheckAsync_WhenUsbipdMissing_ShouldReturnMissing()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (file, arguments) => (file, arguments) switch
            {
                ("where.exe", "usbipd") => Failure("INFO: Could not find files for the given pattern(s)."),
                ("sc.exe", "query usbipd") => Failure("[SC] OpenService FAILED 1060: The specified service does not exist as an installed service."),
                ("where.exe", "winget") => Success(@"C:\Windows\System32\winget.exe"),
                _ => throw new InvalidOperationException($"Unexpected command: {file} {arguments}")
            },
        };

        var service = new UsbipdPrerequisiteService(runner);
        var status = await service.CheckAsync();

        Assert.Equal(UsbipdInstallState.Missing, status.State);
        Assert.False(status.CommandAvailable);
        Assert.False(status.ServiceAvailable);
        Assert.True(status.WingetAvailable);
    }

    [Fact]
    public async Task CheckAsync_WhenServiceExistsButCommandMissing_ShouldReturnUnavailable()
    {
        var runner = new FakeProcessRunner
        {
            Handler = (file, arguments) => (file, arguments) switch
            {
                ("where.exe", "usbipd") => Failure("INFO: Could not find files for the given pattern(s)."),
                ("sc.exe", "query usbipd") => Success("SERVICE_NAME: usbipd"),
                ("where.exe", "winget") => Failure("INFO: Could not find files for the given pattern(s)."),
                _ => throw new InvalidOperationException($"Unexpected command: {file} {arguments}")
            },
        };

        var service = new UsbipdPrerequisiteService(runner);
        var status = await service.CheckAsync();

        Assert.Equal(UsbipdInstallState.Unavailable, status.State);
        Assert.False(status.CommandAvailable);
        Assert.True(status.ServiceAvailable);
        Assert.False(status.WingetAvailable);
    }

    private static ProcessResult Success(string output) => new()
    {
        ExitCode = 0,
        StandardOutput = output,
        StandardError = string.Empty,
    };

    private static ProcessResult Failure(string error) => new()
    {
        ExitCode = 1,
        StandardOutput = string.Empty,
        StandardError = error,
    };

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Func<string, string, ProcessResult>? Handler { get; set; }

        public Task<ProcessResult> RunAsync(
            string fileName,
            string arguments,
            string? workingDirectory = null,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (Handler is null)
            {
                throw new InvalidOperationException("Handler is not configured.");
            }

            return Task.FromResult(Handler(fileName, arguments));
        }
    }
}
