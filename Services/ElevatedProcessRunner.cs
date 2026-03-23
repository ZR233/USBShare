using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace USBShare.Services;

/// <summary>
/// Manages a persistent elevated helper process and forwards commands to it via a named pipe.
/// The helper is launched once with <c>Verb = "runas"</c> (triggering a single UAC prompt),
/// and all subsequent admin-level commands are sent through the pipe without further prompts.
/// </summary>
public interface IElevatedProcessRunner : IProcessRunner, IAsyncDisposable
{
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}

public sealed class ElevatedProcessRunner : IElevatedProcessRunner
{
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private Process? _workerProcess;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private string _pipeName = string.Empty;

    public bool IsRunning => _workerProcess is { HasExited: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return;

        // Clean up any stale resources from a previous session.
        await StopAsync().ConfigureAwait(false);

        _pipeName = $"USBShare_Worker_{Guid.NewGuid():N}";
        var readyFile = Path.Combine(Path.GetTempPath(), $"{_pipeName}.ready");
        var helperPath = ResolveHelperPath();

        _workerProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = _pipeName,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }
        };

        try
        {
            if (!_workerProcess.Start())
                throw new InvalidOperationException("无法启动提权辅助进程。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            _workerProcess.Dispose();
            _workerProcess = null;
            throw new OperationCanceledException("用户取消了管理员权限请求。", ex);
        }

        // Wait for the worker to signal readiness via a sentinel file.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(readyFile))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_workerProcess.HasExited)
                throw new InvalidOperationException(
                    $"提权辅助进程提前退出（退出码: {_workerProcess.ExitCode}）。");

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("提权辅助进程启动超时。");

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        try { File.Delete(readyFile); } catch { /* best-effort */ }

        // Connect to the worker's named pipe.
        _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(10_000, cancellationToken).ConfigureAwait(false);

        _reader = new StreamReader(_pipe, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(_pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
    }

    public async Task StopAsync()
    {
        // Send exit command.
        if (_writer is not null && IsRunning)
        {
            try
            {
                await _writer.WriteLineAsync(
                    JsonSerializer.Serialize(new { FileName = "", Arguments = "" })).ConfigureAwait(false);
            }
            catch { /* pipe may already be broken */ }
        }

        _reader?.Dispose();
        _writer?.Dispose();
        _reader = null;
        _writer = null;

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }

        if (_workerProcess is not null)
        {
            try
            {
                if (!_workerProcess.HasExited)
                {
                    _workerProcess.WaitForExit(3000);
                    if (!_workerProcess.HasExited)
                        _workerProcess.Kill(entireProcessTree: true);
                }
            }
            catch { /* ignore kill errors */ }

            _workerProcess.Dispose();
            _workerProcess = null;
        }
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning || _writer is null || _reader is null)
            throw new InvalidOperationException("提权辅助进程未运行，请先启动分享。");

        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = JsonSerializer.Serialize(new { FileName = fileName, Arguments = arguments });
            await _writer.WriteLineAsync(request).ConfigureAwait(false);

            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(effectiveTimeout);

            string? responseLine;
            try
            {
                responseLine = await _reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"提权辅助进程响应超时（{effectiveTimeout.TotalSeconds}s）。");
            }

            if (responseLine is null)
                throw new InvalidOperationException("提权辅助进程断开连接。");

            var doc = JsonSerializer.Deserialize<JsonElement>(responseLine);
            return new ProcessResult
            {
                ExitCode = doc.GetProperty("ExitCode").GetInt32(),
                StandardOutput = doc.GetProperty("StandardOutput").GetString() ?? string.Empty,
                StandardError = doc.GetProperty("StandardError").GetString() ?? string.Empty,
            };
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _commandLock.Dispose();
    }

    private static string ResolveHelperPath()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var path = Path.Combine(dir, "USBShare.ElevatedHelper.exe");
        if (File.Exists(path))
            return path;

        throw new FileNotFoundException(
            "未找到提权辅助程序 USBShare.ElevatedHelper.exe，请确保它已包含在应用包中。", path);
    }
}
