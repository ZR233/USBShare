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
    private readonly IRuleResolver _ruleResolver;
    private readonly ISecretStore _secretStore;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private readonly Dictionary<Guid, ISshRemoteSession> _sessions = [];
    private readonly Dictionary<Guid, HashSet<string>> _attachedBySession = [];
    private readonly HashSet<string> _boundBySession = new(StringComparer.OrdinalIgnoreCase);

    private AppConfig _config = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private ShareSessionState _state = new();

    public ShareOrchestrator(
        IUsbipdService usbipdService,
        IUsbTopologyService usbTopologyService,
        IRuleResolver ruleResolver,
        ISecretStore secretStore)
    {
        _usbipdService = usbipdService;
        _usbTopologyService = usbTopologyService;
        _ruleResolver = ruleResolver;
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
            if (_loopTask is { IsCompleted: false })
            {
                return;
            }

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SetState(state =>
            {
                state.IsRunning = true;
                state.LastErrorsByKey.Clear();
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
            _config = CloneConfig(config);
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
        var remotesById = config.Remotes.ToDictionary(remote => remote.Id);

        var state = await _usbipdService.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var topology = await _usbTopologyService.BuildSnapshotAsync(state, cancellationToken).ConfigureAwait(false);
        var resolution = _ruleResolver.Resolve(topology, config.ShareRules, remotesById.Keys.ToArray());

        SetState(sessionState =>
        {
            sessionState.ConflictsByInstanceId = new Dictionary<string, string>(resolution.ConflictsByInstanceId, StringComparer.OrdinalIgnoreCase);
        });

        await CleanupDetachedTargetsAsync(config, resolution.TargetsByBusId, cancellationToken).ConfigureAwait(false);
        await EnsureDesiredTargetsAsync(config, remotesById, resolution.TargetsByBusId, cancellationToken).ConfigureAwait(false);
        await CleanupStaleBindingsAsync(resolution.TargetsByBusId, cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupDetachedTargetsAsync(
        AppConfig config,
        Dictionary<string, Guid> desiredTargets,
        CancellationToken cancellationToken)
    {
        foreach (var pair in _attachedBySession.ToList())
        {
            var remoteId = pair.Key;
            var attachedBusIds = pair.Value.ToList();
            if (!_sessions.TryGetValue(remoteId, out var session))
            {
                continue;
            }

            var sudoPassword = await _secretStore.GetSecretAsync(remoteId, SecretKind.Sudo, cancellationToken).ConfigureAwait(false);
            foreach (var busId in attachedBusIds)
            {
                if (desiredTargets.TryGetValue(busId, out var desiredRemoteId) && desiredRemoteId == remoteId)
                {
                    continue;
                }

                var detachResult = await session.DetachAsync(busId, sudoPassword, cancellationToken).ConfigureAwait(false);
                if (detachResult.Success)
                {
                    pair.Value.Remove(busId);
                }
                else
                {
                    SetError($"detach:{remoteId}:{busId}", CompactRemoteError(detachResult));
                }
            }
        }
    }

    private async Task EnsureDesiredTargetsAsync(
        AppConfig config,
        Dictionary<Guid, RemoteConfig> remotesById,
        Dictionary<string, Guid> desiredTargets,
        CancellationToken cancellationToken)
    {
        foreach (var target in desiredTargets)
        {
            var busId = target.Key;
            var remoteId = target.Value;

            if (!remotesById.TryGetValue(remoteId, out var remote))
            {
                continue;
            }

            var session = await GetOrCreateSessionAsync(remote, cancellationToken).ConfigureAwait(false);
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

            var attached = await session.IsAttachedAsync(busId, cancellationToken).ConfigureAwait(false);
            if (attached)
            {
                continue;
            }

            var sudoPassword = await _secretStore.GetSecretAsync(remoteId, SecretKind.Sudo, cancellationToken).ConfigureAwait(false);
            var attachResult = await session.AttachAsync(busId, sudoPassword, cancellationToken).ConfigureAwait(false);
            if (!attachResult.Success)
            {
                SetError($"attach:{remoteId}:{busId}", CompactRemoteError(attachResult));
                continue;
            }

            if (!_attachedBySession.TryGetValue(remoteId, out var attachedSet))
            {
                attachedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _attachedBySession[remoteId] = attachedSet;
            }

            attachedSet.Add(busId);
        }
    }

    private async Task CleanupStaleBindingsAsync(
        Dictionary<string, Guid> desiredTargets,
        CancellationToken cancellationToken)
    {
        var managedAttached = _attachedBySession.Values
            .SelectMany(values => values)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var busId in _boundBySession.ToList())
        {
            if (desiredTargets.ContainsKey(busId) || managedAttached.Contains(busId))
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

    private async Task<ISshRemoteSession> GetOrCreateSessionAsync(RemoteConfig remote, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(remote.Id, out var existing))
        {
            return existing;
        }

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

        _sessions[remote.Id] = session;
        _attachedBySession.TryAdd(remote.Id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return session;
    }

    private async Task RollbackSessionResourcesAsync(CancellationToken cancellationToken)
    {
        foreach (var pair in _attachedBySession.ToList())
        {
            var remoteId = pair.Key;
            if (!_sessions.TryGetValue(remoteId, out var session))
            {
                continue;
            }

            var sudoPassword = await _secretStore.GetSecretAsync(remoteId, SecretKind.Sudo, cancellationToken).ConfigureAwait(false);
            foreach (var busId in pair.Value.ToList())
            {
                var detach = await session.DetachAsync(busId, sudoPassword, cancellationToken).ConfigureAwait(false);
                if (!detach.Success)
                {
                    SetError($"detach-stop:{remoteId}:{busId}", CompactRemoteError(detach));
                }
            }
        }

        foreach (var busId in _boundBySession.ToList())
        {
            var unbind = await _usbipdService.UnbindAsync(busId, cancellationToken).ConfigureAwait(false);
            if (!unbind.Success)
            {
                SetError($"unbind-stop:{busId}", unbind.Message);
            }
        }

        _boundBySession.Clear();
        _attachedBySession.Clear();

        foreach (var session in _sessions.Values.ToList())
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
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
        };
    }

    private static AppConfig CloneConfig(AppConfig source)
    {
        return new AppConfig
        {
            Remotes = source.Remotes.Select(CloneRemote).ToList(),
            ShareRules = source.ShareRules.Select(CloneRule).ToList(),
            SecretRefs = source.SecretRefs.Select(reference => new SecretRef
            {
                RemoteId = reference.RemoteId,
                SecretKind = reference.SecretKind,
            }).ToList(),
            Settings = new AppSettings
            {
                PollIntervalSeconds = source.Settings.PollIntervalSeconds,
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

    private static ShareRule CloneRule(ShareRule source)
    {
        return new ShareRule
        {
            NodeType = source.NodeType,
            NodeInstanceId = source.NodeInstanceId,
            RemoteId = source.RemoteId,
            Enabled = source.Enabled,
        };
    }
}
