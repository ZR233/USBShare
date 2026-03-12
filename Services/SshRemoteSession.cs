using System.Text.RegularExpressions;
using Renci.SshNet;
using USBShare.Models;

namespace USBShare.Services;

public sealed record RemoteExecutionResult(bool Success, int ExitCode, string Output, string Error);

public interface ISshRemoteSession : IAsyncDisposable
{
    RemoteConfig Remote { get; }
    Task ConnectAsync(string? sshSecret, CancellationToken cancellationToken = default);
    Task<RemoteExecutionResult> ProbeAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAttachedAsync(string busId, CancellationToken cancellationToken = default);
    Task<RemoteExecutionResult> AttachAsync(string busId, string? sudoPassword, CancellationToken cancellationToken = default);
    Task<RemoteExecutionResult> DetachAsync(string busId, string? sudoPassword, CancellationToken cancellationToken = default);
}

public sealed class SshRemoteSession : ISshRemoteSession
{
    private readonly RemoteConfig _remote;
    private SshClient? _client;
    private ForwardedPortRemote? _forwardedPort;
    private readonly object _sync = new();

    public SshRemoteSession(RemoteConfig remote)
    {
        _remote = remote;
    }

    public RemoteConfig Remote => _remote;

    public async Task ConnectAsync(string? sshSecret, CancellationToken cancellationToken = default)
    {
        if (_client is { IsConnected: true })
        {
            return;
        }

        await Task.Run(
                () =>
                {
                    lock (_sync)
                    {
                        if (_client is { IsConnected: true })
                        {
                            return;
                        }

                        DisposeClientAndForward();

                        var connectionInfo = BuildConnectionInfo(_remote, sshSecret);
                        _client = new SshClient(connectionInfo)
                        {
                            KeepAliveInterval = TimeSpan.FromSeconds(20),
                        };
                        _client.Connect();

                        _forwardedPort = new ForwardedPortRemote("127.0.0.1", (uint)_remote.TunnelPort, "127.0.0.1", 3240);
                        _client.AddForwardedPort(_forwardedPort);
                        _forwardedPort.Start();
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<RemoteExecutionResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        const string script = "command -v usbip >/dev/null 2>&1 && command -v sudo >/dev/null 2>&1 && echo READY";
        return ExecuteBashAsync(script, cancellationToken);
    }

    public async Task<bool> IsAttachedAsync(string busId, CancellationToken cancellationToken = default)
    {
        var script = $"usbip port | grep -F -- {QuoteForSingleShell(busId)} >/dev/null 2>&1; if [ $? -eq 0 ]; then echo 1; else echo 0; fi";
        var result = await ExecuteBashAsync(script, cancellationToken).ConfigureAwait(false);
        return result.Success && result.Output.Contains("1", StringComparison.Ordinal);
    }

    public async Task<RemoteExecutionResult> AttachAsync(string busId, string? sudoPassword, CancellationToken cancellationToken = default)
    {
        if (await IsAttachedAsync(busId, cancellationToken).ConfigureAwait(false))
        {
            return new RemoteExecutionResult(true, 0, "Already attached.", string.Empty);
        }

        var attachCommand = BuildAttachCommand(busId, _remote.TunnelPort);
        var script = BuildSudoScript(attachCommand, sudoPassword);
        var result = await ExecuteBashAsync(script, cancellationToken).ConfigureAwait(false);

        if (!result.Success && result.Output.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteExecutionResult(true, 0, result.Output, result.Error);
        }

        return result;
    }

    public async Task<RemoteExecutionResult> DetachAsync(string busId, string? sudoPassword, CancellationToken cancellationToken = default)
    {
        var portsResult = await ExecuteBashAsync("usbip port", cancellationToken).ConfigureAwait(false);
        if (!portsResult.Success)
        {
            return portsResult;
        }

        var targetPort = TryGetPortByBusId(portsResult.Output, busId);
        if (targetPort is null)
        {
            return new RemoteExecutionResult(true, 0, "Already detached.", string.Empty);
        }

        var detachScript = BuildSudoScript($"usbip detach --port {targetPort}", sudoPassword);
        return await ExecuteBashAsync(detachScript, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        DisposeClientAndForward();
        return ValueTask.CompletedTask;
    }

    private async Task<RemoteExecutionResult> ExecuteBashAsync(string script, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var bashCommand = $"bash -lc {QuoteForSingleShell(script)}";
        return await ExecuteAsync(bashCommand, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteExecutionResult> ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("SSH client is not connected.");
        }

        return await Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cmd = _client.CreateCommand(command);
                    cmd.CommandTimeout = TimeSpan.FromSeconds(30);
                    var output = cmd.Execute();
                    return new RemoteExecutionResult(
                        Success: (cmd.ExitStatus ?? -1) == 0,
                        ExitCode: cmd.ExitStatus ?? -1,
                        Output: output.Trim(),
                        Error: cmd.Error.Trim());
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true })
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException($"SSH session for remote '{_remote.DisplayTitle}' is not connected.");
    }

    private static string BuildAttachCommand(string busId, int tunnelPort)
    {
        if (tunnelPort == 3240)
        {
            return $"usbip attach --remote 127.0.0.1 --busid {busId}";
        }

        return $"usbip attach --remote 127.0.0.1 --tcp-port {tunnelPort} --busid {busId}";
    }

    private static string BuildSudoScript(string command, string? sudoPassword)
    {
        if (!string.IsNullOrWhiteSpace(sudoPassword))
        {
            return $"printf '%s\\n' {QuoteForSingleShell(sudoPassword)} | sudo -k -S -p '' {command}";
        }

        return $"sudo -n {command}";
    }

    private static ConnectionInfo BuildConnectionInfo(RemoteConfig remote, string? sshSecret)
    {
        if (string.IsNullOrWhiteSpace(remote.Host))
        {
            throw new InvalidOperationException("Remote host is required.");
        }

        if (string.IsNullOrWhiteSpace(remote.User))
        {
            throw new InvalidOperationException("Remote user is required.");
        }

        AuthenticationMethod authMethod;
        switch (remote.AuthType)
        {
            case AuthType.Password:
            {
                if (string.IsNullOrEmpty(sshSecret))
                {
                    throw new InvalidOperationException("SSH password is missing.");
                }

                authMethod = new PasswordAuthenticationMethod(remote.User, sshSecret);
                break;
            }
            case AuthType.PrivateKey:
            {
                if (string.IsNullOrWhiteSpace(remote.KeyPath))
                {
                    throw new InvalidOperationException("Private key path is required for key authentication.");
                }

                var expandedKeyPath = Environment.ExpandEnvironmentVariables(remote.KeyPath);
                if (!File.Exists(expandedKeyPath))
                {
                    throw new FileNotFoundException($"Private key does not exist: {expandedKeyPath}");
                }

                var keyFile = string.IsNullOrWhiteSpace(sshSecret)
                    ? new PrivateKeyFile(expandedKeyPath)
                    : new PrivateKeyFile(expandedKeyPath, sshSecret);

                authMethod = new PrivateKeyAuthenticationMethod(remote.User, keyFile);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }

        return new ConnectionInfo(remote.Host, remote.Port, remote.User, authMethod);
    }

    private static string QuoteForSingleShell(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private static int? TryGetPortByBusId(string output, string busId)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        int? currentPort = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Port ", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(line, @"Port\s+(\d+):", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed))
                {
                    currentPort = parsed;
                }

                continue;
            }

            if (currentPort.HasValue && line.Contains(busId, StringComparison.OrdinalIgnoreCase))
            {
                return currentPort.Value;
            }
        }

        return null;
    }

    private void DisposeClientAndForward()
    {
        try
        {
            if (_forwardedPort is { IsStarted: true })
            {
                _forwardedPort.Stop();
            }
        }
        catch
        {
            // Ignore port stop errors.
        }

        try
        {
            _forwardedPort?.Dispose();
        }
        catch
        {
            // Ignore disposal errors.
        }

        _forwardedPort = null;

        try
        {
            if (_client is { IsConnected: true })
            {
                _client.Disconnect();
            }
        }
        catch
        {
            // Ignore disconnect errors.
        }

        try
        {
            _client?.Dispose();
        }
        catch
        {
            // Ignore disposal errors.
        }

        _client = null;
    }
}
