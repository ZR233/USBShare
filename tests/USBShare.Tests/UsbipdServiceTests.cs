using USBShare.Services;
using Xunit.Abstractions;

namespace USBShare.Tests;

public sealed class UsbipdServiceTests
{
    private readonly ITestOutputHelper _output;

    public UsbipdServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task GetListAsync_ShouldParseConnectedDevices()
    {
        var raw = """
                  Connected:
                  BUSID  VID:PID    DEVICE                                                        STATE
                  1-11   369b:00f3  USB 输入设备                                                      Not shared
                  5-1    1d50:6089  HackRF One                                                    Shared

                  Persisted:
                  GUID                                  DEVICE
                  """;

        var runner = new FakeProcessRunner
        {
            Handler = (file, arguments) =>
            {
                Assert.Equal("usbipd", file);
                Assert.Equal("list", arguments);

                return new ProcessResult
                {
                    ExitCode = 0,
                    StandardOutput = raw,
                    StandardError = string.Empty,
                };
            },
        };

        var service = new UsbipdService(runner);
        var result = await service.GetListAsync();

        _output.WriteLine("=== usbipd list parse debug ===");
        _output.WriteLine($"Device count: {result.Devices.Count}");
        foreach (var device in result.Devices)
        {
            _output.WriteLine($"BusId={device.BusId}, Description={device.Description}, State={device.State}");
        }

        Assert.Equal(2, result.Devices.Count);
        Assert.Equal("1-11", result.Devices[0].BusId);
        Assert.Equal("Not shared", result.Devices[0].State);
        Assert.Equal("5-1", result.Devices[1].BusId);
        Assert.Equal("Shared", result.Devices[1].State);
    }

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
