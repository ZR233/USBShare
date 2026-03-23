// Elevated helper process for USBShare.
// Runs with admin privileges and executes commands on behalf of the main (non-elevated) app.
// Communication is via a named pipe whose name is passed as the first argument.
//
// Protocol (newline-delimited JSON):
//   Request:  {"FileName":"usbipd","Arguments":"bind --busid 1-1"}
//   Response: {"ExitCode":0,"StandardOutput":"...","StandardError":"..."}
//   Exit:     {"FileName":"","Arguments":""}   (empty FileName = shutdown)

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: USBShare.ElevatedHelper <pipeName>");
    return 1;
}

var pipeName = args[0];
var readyFile = Path.Combine(Path.GetTempPath(), $"{pipeName}.ready");

await using var server = new NamedPipeServerStream(
    pipeName,
    PipeDirection.InOut,
    1,
    PipeTransmissionMode.Byte,
    PipeOptions.Asynchronous);

// Signal that the pipe server is ready for a connection.
await File.WriteAllTextAsync(readyFile, "1");

try
{
    await server.WaitForConnectionAsync();

    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
    await using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

    while (true)
    {
        var line = await reader.ReadLineAsync();
        if (line is null)
            break;

        WorkerRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, WorkerJsonContext.Default.WorkerRequest);
        }
        catch
        {
            continue;
        }

        if (request is null || string.IsNullOrEmpty(request.FileName))
            break;

        var response = await ExecuteCommandAsync(request.FileName, request.Arguments);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, WorkerJsonContext.Default.WorkerResponse));
    }
}
finally
{
    try { File.Delete(readyFile); } catch { /* best-effort cleanup */ }
}

return 0;

// ---------------------------------------------------------------------------

static async Task<WorkerResponse> ExecuteCommandAsync(string fileName, string arguments)
{
    try
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) stdoutDone.TrySetResult();
            else stdout.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) stderrDone.TrySetResult();
            else stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            return new WorkerResponse { ExitCode = -1, StandardError = $"Failed to start: {fileName}" };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();
        await Task.WhenAll(stdoutDone.Task, stderrDone.Task);

        return new WorkerResponse
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString().TrimEnd(),
            StandardError = stderr.ToString().TrimEnd(),
        };
    }
    catch (Exception ex)
    {
        return new WorkerResponse { ExitCode = -1, StandardError = ex.Message };
    }
}

// DTO types with source-generated JSON for AOT/trimming compatibility.

internal sealed class WorkerRequest
{
    public string FileName { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
}

internal sealed class WorkerResponse
{
    public int ExitCode { get; set; }
    public string StandardOutput { get; set; } = string.Empty;
    public string StandardError { get; set; } = string.Empty;
}

[JsonSerializable(typeof(WorkerRequest))]
[JsonSerializable(typeof(WorkerResponse))]
internal sealed partial class WorkerJsonContext : JsonSerializerContext;
