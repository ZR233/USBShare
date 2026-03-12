using USBShare.Models;

namespace USBShare.Services;

public interface IShareOrchestrator
{
    bool IsRunning { get; }
    ShareSessionState CurrentState { get; }
    event EventHandler<ShareSessionState>? StateChanged;
    Task StartAsync(AppConfig config, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task UpdateConfigurationAsync(AppConfig config, CancellationToken cancellationToken = default);
}

public sealed class ShareOrchestrator : IShareOrchestrator
{
    private readonly IUsbipdService _usbipdService;
    private readonly IUsbTopologyService _usbTopologyService;
    private readonly IDeviceEnabledResolver _deviceEnabledResolver;
    private readonly ISecretStore _secretStore;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private readonly HashSet<string> _attachedBusIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _boundBySession = new(StringComparer.OrdinalIgnoreCase);

    private AppConfig _config = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private ShareSessionState _state = new();
    private ISshRemoteSession? _currentSession;
    private RemoteConfig? _currentRemote;

    public ShareOrchestrator(
        IUsbipdService usbipdService,
        IUsbTopologyService usbTopologyService,
        IDeviceEnabledResolver deviceEnabledResolver,
        ISecretStore secretStore)
    {
        _usbipdService = usbipdService;
        _usbTopologyService = usbTopologyService;
        _deviceEnabledResolver = deviceEnabledResolver;
        _secretStore = secretStore;
    }

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public ShareSessionState CurrentState
    {
        get
        {
            lock (_state)
            {
                return CloneState(_state);
            }
        }
    }

    public event EventHandler<ShareSessionState>? StateChanged;

    public async Task StartAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _config = CloneConfig(config);

            // 验证已选择远程服务器
            if (!_config.Settings.SelectedRemoteId.HasValue)
            {
                throw new InvalidOperationException("请先选择一个远程服务器作为分享目标。");
            }

            if (!_config.Remotes.Any(r => r.Id == _config.Settings.SelectedRemoteId.Value))
            {
                throw new InvalidOperationException("选中的远程服务器不存在。请重新选择。");
            }

            if (_loopTask is { IsCompleted: false })
            {
                return;
            }

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SetState(state =>
            {
                state.IsRunning = true;
                state.LastErrorsByKey.Clear();
                state.ConflictsByInstanceId.Clear();
            });

            _loopTask = Task.Run(() => LoopAsync(_loopCts.Token), _loopCts.Token);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task UpdateConfigurationAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var oldRemoteId = _config.Settings.SelectedRemoteId;
            _config = CloneConfig(config);
            var newRemoteId = _config.Settings.SelectedRemoteId;

            // 如果远程服务器发生变化，需要重新连接
            if (oldRemoteId != newRemoteId && IsRunning)
            {
                SetState(state =>
                {
                    state.LastErrorsByKey["remote_change"] = "远程服务器已更改，将在下一个周期重新连接。";
                });
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loopTask;
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loopTask is null)
            {
                return;
            }

            _loopCts?.Cancel();
            loopTask = _loopTask;
        }
        finally
        {
            _stateLock.Release();
        }

        try
        {
            await loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping.
        }

        await RollbackSessionResourcesAsync(cancellationToken).ConfigureAwait(false);

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _loopTask = null;
            _loopCts?.Dispose();
            _loopCts = null;
        }
        finally
        {
            _stateLock.Release();
        }

        SetState(state => { state.IsRunning = false; });
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _cycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var config = CloneConfig(_config);
                var pollSeconds = Math.Clamp(config.Settings.PollIntervalSeconds, 1, 30);
                await ExecuteCycleAsync(config, cancellationToken).ConfigureAwait(false);
                SetState(state => { state.LastUpdated = DateTimeOffset.Now; });
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetState(state =>
                {
                    state.LastErrorsByKey["loop"] = ex.Message;
                    state.LastUpdated = DateTimeOffset.Now;
                });
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _cycleLock.Release();
            }
        }
    }

    private async Task ExecuteCycleAsync(AppConfig config, CancellationToken cancellationToken)
    {
        // 获取选中的远程服务器
        var selectedRemoteId = config.Settings.SelectedRemoteId;
        if (!selectedRemoteId.HasValue)
        {
            SetError("remote", "未选择远程服务器。");
            return;
        }

        var remote = config.Remotes.FirstOrDefault(r => r.Id == selectedRemoteId.Value);
        if (remote is null)
        {
            SetError("remote", "选中的远程服务器不存在。");
            return;
        }

        var topology = await _usbTopologyService.BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var enabledResult = _deviceEnabledResolver.Resolve(topology, config.EnabledDevices);

        // 清空冲突（新的简化模型没有冲突）
        SetState(sessionState =>
        {
            sessionState.ConflictsByInstanceId.Clear();
        });

        await EnsureSessionAsync(remote, cancellationToken).ConfigureAwait(false);
        await CleanupDetachedDevicesAsync(enabledResult.EnabledBusIds.Keys, cancellationToken).ConfigureAwait(false);
        await EnsureEnabledDevicesAsync(enabledResult.EnabledBusIds.Keys, cancellationToken).ConfigureAwait(false);
        await CleanupStaleBindingsAsync(enabledResult.EnabledBusIds.Keys, cancellationToken).ConfigureAwait(false);

        // 同步绑定状态到 session state
        SetState(sessionState =>
        {
            sessionState.BoundBusIds.Clear();
            sessionState.BoundBusIds.UnionWith(_boundBySession);
            sessionState.AttachedBusIds.Clear();
            sessionState.AttachedBusIds.UnionWith(_attachedBusIds);
        });
    }

    private async Task EnsureSessionAsync(RemoteConfig remote, CancellationToken cancellationToken)
    {
        // 如果远程服务器变更，先断开旧会话
        if (_currentRemote is not null && _currentRemote.Id != remote.Id)
        {
            await DisconnectSessionAsync(cancellationToken).ConfigureAwait(false);
        }

        // 创建新会话
        if (_currentSession is null)
        {
            var session = new SshRemoteSession(remote);
            var sshSecret = await _secretStore.GetSecretAsync(remote.Id, SecretKind.Ssh, cancellationToken).ConfigureAwait(false);
            await session.ConnectAsync(sshSecret, cancellationToken).ConfigureAwait(false);

            var probeResult = await session.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (!probeResult.Success || !probeResult.Output.Contains("READY", StringComparison.OrdinalIgnoreCase))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"远程 {remote.DisplayTitle} 环境检查失败，请确认已安装 usbip 和 sudo。{CompactRemoteError(probeResult)}");
            }

            _currentSession = session;
            _currentRemote = remote;
            _attachedBusIds.Clear();
        }
    }

    private async Task DisconnectSessionAsync(CancellationToken cancellationToken)
    {
        if (_currentSession is null)
        {
            return;
        }

        // Detach 所有设备
        if (_currentRemote is not null)
        {
            var sudoPassword = await _secretStore.GetSecretAsync(_currentRemote.Id, SecretKind.Sudo, cancellationToken).ConfigureAwait(false);
            foreach (var busId in _attachedBusIds.ToList())
            {
                var detachResult = await _currentSession.DetachAsync(busId, sudoPassword, cancellationToken).ConfigureAwait(false);
                if (!detachResult.Success)
                {
                    SetError($"detach:{busId}", CompactRemoteError(detachResult));
                }
            }
        }

        await _currentSession.DisposeAsync().ConfigureAwait(false);
        _currentSession = null;
        _currentRemote = null;
        _attachedBusIds.Clear();
    }

    private async Task CleanupDetachedDevicesAsync(IEnumerable<string> desiredBusIds, CancellationToken cancellationToken)
    {
        if (_currentSession is null || _currentRemote is null)
        {
            return;
        }

        var desiredSet = new HashSet<string>(desiredBusIds, StringComparer.OrdinalIgnoreCase);
        var sudoPassword = await _secretStore.GetSecretAsync(_currentRemote.Id, SecretKind.Sudo, cancellationToken).ConfigureAwait(false);

        foreach (var busId in _attachedBusIds.ToList())
        {
            if (desiredSet.Contains(busId))
            {
                continue;
            }

            var detachResult = await _currentSession.DetachAsync(busId, sudoPassword, cancellationToken).ConfigureAwait(false);
            if (detachResult.Success)
            {
                _attachedBusIds.Remove(busId);
            }
            else
            {
                SetError($"detach:{busId}", CompactRemoteError(detachResult));
            }
        }
    }

    private async Task EnsureEnabledDevicesAsync(IEnumerable<string> desiredBusIds, CancellationToken cancellationToken)
    {
        if (_currentSession is null || _currentRemote is null)
        {
            return;
        }

        var sudoPassword = await _secretStore.GetSecretAsync(_currentRemote.Id, SecretKind.Sudo, cancellationToken).ConfigureAwait(false);

        foreach (var busId in desiredBusIds)
        {
            // Bind 设备
            var bindResult = await _usbipdService.EnsureBoundAsync(busId, cancellationToken).ConfigureAwait(false);
            if (!bindResult.Success)
            {
                SetError($"bind:{busId}", bindResult.Message);
                if (bindResult.PermissionDenied)
                {
                    SetError("permission", "usbipd bind 需要管理员权限。");
                }
                continue;
            }

            if (bindResult.BoundBySession)
            {
                _boundBySession.Add(busId);
            }

            // 检查是否已 attached
            if (_attachedBusIds.Contains(busId))
            {
                var isAttached = await _currentSession.IsAttachedAsync(busId, cancellationToken).ConfigureAwait(false);
                if (isAttached)
                {
                    continue;
                }
            }

            // Attach 设备
            var attachResult = await _currentSession.AttachAsync(busId, sudoPassword, cancellationToken).ConfigureAwait(false);
            if (!attachResult.Success)
            {
                SetError($"attach:{busId}", CompactRemoteError(attachResult));
                continue;
            }

            _attachedBusIds.Add(busId);
        }
    }

    private async Task CleanupStaleBindingsAsync(IEnumerable<string> desiredBusIds, CancellationToken cancellationToken)
    {
        var desiredSet = new HashSet<string>(desiredBusIds, StringComparer.OrdinalIgnoreCase);

        foreach (var busId in _boundBySession.ToList())
        {
            if (desiredSet.Contains(busId) || _attachedBusIds.Contains(busId))
            {
                continue;
            }

            var unbindResult = await _usbipdService.UnbindAsync(busId, cancellationToken).ConfigureAwait(false);
            if (unbindResult.Success)
            {
                _boundBySession.Remove(busId);
            }
            else
            {
                SetError($"unbind:{busId}", unbindResult.Message);
            }
        }
    }

    private async Task RollbackSessionResourcesAsync(CancellationToken cancellationToken)
    {
        await DisconnectSessionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var busId in _boundBySession.ToList())
        {
            var unbind = await _usbipdService.UnbindAsync(busId, cancellationToken).ConfigureAwait(false);
            if (!unbind.Success)
            {
                SetError($"unbind-stop:{busId}", unbind.Message);
            }
        }

        _boundBySession.Clear();
        _attachedBusIds.Clear();
    }

    private static string CompactRemoteError(RemoteExecutionResult result)
    {
        return $"{result.Output} {result.Error}".Trim();
    }

    private void SetError(string key, string message)
    {
        SetState(state => { state.LastErrorsByKey[key] = string.IsNullOrWhiteSpace(message) ? "Unknown error" : message; });
    }

    private void SetState(Action<ShareSessionState> mutator)
    {
        ShareSessionState snapshot;
        lock (_state)
        {
            mutator(_state);
            snapshot = CloneState(_state);
        }

        StateChanged?.Invoke(this, snapshot);
    }

    private static ShareSessionState CloneState(ShareSessionState source)
    {
        return new ShareSessionState
        {
            IsRunning = source.IsRunning,
            LastUpdated = source.LastUpdated,
            ConflictsByInstanceId = new Dictionary<string, string>(source.ConflictsByInstanceId, StringComparer.OrdinalIgnoreCase),
            LastErrorsByKey = new Dictionary<string, string>(source.LastErrorsByKey, StringComparer.OrdinalIgnoreCase),
            BoundBusIds = new HashSet<string>(source.BoundBusIds, StringComparer.OrdinalIgnoreCase),
            AttachedBusIds = new HashSet<string>(source.AttachedBusIds, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static AppConfig CloneConfig(AppConfig source)
    {
        return new AppConfig
        {
            Remotes = source.Remotes.Select(CloneRemote).ToList(),
            EnabledDevices = source.EnabledDevices.Select(device => new DeviceEnabled
            {
                NodeInstanceId = device.NodeInstanceId,
                Enabled = device.Enabled,
            }).ToList(),
            SecretRefs = source.SecretRefs.Select(reference => new SecretRef
            {
                RemoteId = reference.RemoteId,
                SecretKind = reference.SecretKind,
            }).ToList(),
            Settings = new AppSettings
            {
                PollIntervalSeconds = source.Settings.PollIntervalSeconds,
                SelectedRemoteId = source.Settings.SelectedRemoteId,
            },
        };
    }

    private static RemoteConfig CloneRemote(RemoteConfig source)
    {
        return new RemoteConfig
        {
            Id = source.Id,
            Name = source.Name,
            Host = source.Host,
            Port = source.Port,
            User = source.User,
            AuthType = source.AuthType,
            KeyPath = source.KeyPath,
            TunnelPort = source.TunnelPort,
        };
    }
}
