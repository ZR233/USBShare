using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using USBShare.Models;
using USBShare.Services;
using USBShare.ViewModels;

namespace USBShare;

public sealed partial class MainWindow : Window
{
    private readonly IConfigStore _configStore = new ConfigStore();
    private readonly ISecretStore _secretStore = new SecretStore();
    private readonly IProcessRunner _processRunner = new ProcessRunner();
    private readonly IAdminService _adminService = new AdminService();
    private readonly IShareOrchestrator _orchestrator;
    private readonly IUsbipdService _usbipdService;
    private readonly IUsbTopologyService _usbTopologyService;
    private readonly Dictionary<string, UsbTreeItemViewModel> _treeByInstanceId = new(StringComparer.OrdinalIgnoreCase);

    private AppConfig _config = new();
    private UsbTopologySnapshot _topology = new();
    private ShareSessionState _sessionState = new();
    private UsbTreeItemViewModel? _selectedNode;
    private bool _isAdmin;

    public MainWindow()
    {
        InitializeComponent();

        _usbipdService = new UsbipdService(_processRunner);
        _usbTopologyService = new UsbTopologyService(_processRunner);
        _orchestrator = new ShareOrchestrator(
            _usbipdService,
            _usbTopologyService,
            new RuleResolver(),
            _secretStore);

        _orchestrator.StateChanged += OnOrchestratorStateChanged;
        RootGrid.Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        RuleRemoteComboBox.DisplayMemberPath = nameof(RemoteChoiceItem.Label);
        RuleRemoteComboBox.SelectedValuePath = nameof(RemoteChoiceItem.RemoteId);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadConfigurationAsync();
        RefreshRemoteList();
        RefreshRuleRemoteOptions();
        PollIntervalNumberBox.Value = _config.Settings.PollIntervalSeconds;

        _isAdmin = _adminService.IsRunningAsAdministrator();
        if (!_isAdmin)
        {
#if DEBUG
            AdminHintTextBlock.Text = "Debug 模式下不会自动提权。请以管理员方式启动 VS 或程序后再开始分享。";
#else
            if (_adminService.TryRelaunchAsAdministrator(out _))
            {
                App.Current.Exit();
                return;
            }

            AdminHintTextBlock.Text = "当前未获得管理员权限，无法执行 usbipd bind/unbind。请点击“管理员重启”。";
#endif
            StartShareButton.IsEnabled = false;
        }
        else
        {
            AdminHintTextBlock.Text = "管理员权限已就绪。";
        }

        await RefreshTopologyAsync();
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        try
        {
            await _orchestrator.StopAsync();
        }
        catch
        {
            // Ignore shutdown errors.
        }
    }

    private async Task LoadConfigurationAsync()
    {
        _config = await _configStore.LoadAsync();
        _config.Settings ??= new AppSettings();
        _config.Settings.PollIntervalSeconds = Math.Clamp(_config.Settings.PollIntervalSeconds, 1, 30);
    }

    private Task SaveConfigurationAsync() => _configStore.SaveAsync(_config);

    private void RefreshRemoteList()
    {
        RemoteListView.ItemsSource = _config.Remotes
            .OrderBy(remote => remote.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void RefreshRuleRemoteOptions()
    {
        var options = new List<RemoteChoiceItem>
        {
            new() { RemoteId = null, Label = "(不分享)" },
        };

        options.AddRange(_config.Remotes
            .OrderBy(remote => remote.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .Select(remote => new RemoteChoiceItem
            {
                RemoteId = remote.Id,
                Label = remote.DisplayTitle,
            }));

        RuleRemoteComboBox.ItemsSource = options;
    }

    private async Task RefreshTopologyAsync()
    {
        SetStatus("正在扫描 USB 拓扑，首次刷新可能需要 10-30 秒…", InfoBarSeverity.Informational);

        try
        {
            var state = await _usbipdService.GetStateAsync();
            _topology = await _usbTopologyService.BuildSnapshotAsync(state);
            BuildTree();
            SetStatus($"设备拓扑刷新完成，共 {_treeByInstanceId.Count} 个节点。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"刷新设备失败: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void BuildTree()
    {
        _treeByInstanceId.Clear();
        var rootNodes = _topology.RootNodes
            .OrderBy(node => node.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        UsbTreeView.RootNodes.Clear();
        foreach (var root in rootNodes)
        {
            var vm = BuildTreeViewModel(root.InstanceId);
            if (vm is null)
            {
                continue;
            }

            UsbTreeView.RootNodes.Add(BuildTreeViewNode(vm));
        }

        ApplyAssignmentsAndConflicts();
    }

    private UsbTreeItemViewModel? BuildTreeViewModel(string instanceId)
    {
        if (!_topology.Nodes.TryGetValue(instanceId, out var node))
        {
            return null;
        }

        var viewModel = new UsbTreeItemViewModel
        {
            InstanceId = node.InstanceId,
            ParentInstanceId = node.ParentInstanceId,
            Title = node.DisplayName,
            Subtitle = BuildSubtitle(node),
            IsHub = node.IsHub,
            IsShareable = node.IsShareable,
            BusId = node.BusId,
        };

        _treeByInstanceId[viewModel.InstanceId] = viewModel;

        foreach (var childId in node.Children)
        {
            var child = BuildTreeViewModel(childId);
            if (child is not null)
            {
                viewModel.Children.Add(child);
            }
        }

        return viewModel;
    }

    private static string BuildSubtitle(UsbTopologyNode node)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(node.DeviceClass))
        {
            parts.Add(node.DeviceClass);
        }

        if (node.IsShareable && !string.IsNullOrWhiteSpace(node.BusId))
        {
            parts.Add($"BusId: {node.BusId}");
        }
        else if (!node.IsHub)
        {
            parts.Add("不可直接分享");
        }

        return string.Join(" | ", parts);
    }

    private static TreeViewNode BuildTreeViewNode(UsbTreeItemViewModel viewModel)
    {
        var node = new TreeViewNode
        {
            Content = viewModel,
            IsExpanded = true,
        };

        foreach (var child in viewModel.Children)
        {
            node.Children.Add(BuildTreeViewNode(child));
        }

        return node;
    }

    private void ApplyAssignmentsAndConflicts()
    {
        foreach (var vm in _treeByInstanceId.Values)
        {
            vm.ConflictMessage = null;
            var rule = FindRuleForNode(vm);
            if (rule is null)
            {
                vm.AssignedRemoteName = "未分配";
            }
            else
            {
                var remote = _config.Remotes.FirstOrDefault(candidate => candidate.Id == rule.RemoteId);
                vm.AssignedRemoteName = remote is null ? "规则远程已丢失" : $"-> {remote.DisplayTitle}";
            }

            if (_sessionState.ConflictsByInstanceId.TryGetValue(vm.InstanceId, out var conflict))
            {
                vm.ConflictMessage = conflict;
            }
        }
    }

    private ShareRule? FindRuleForNode(UsbTreeItemViewModel vm)
    {
        return _config.ShareRules.FirstOrDefault(rule =>
            rule.Enabled &&
            rule.NodeType == vm.RuleNodeType &&
            string.Equals(rule.NodeInstanceId, vm.InstanceId, StringComparison.OrdinalIgnoreCase));
    }

    private RemoteConfig? SelectedRemote => RemoteListView.SelectedItem as RemoteConfig;

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private async Task PersistAndPropagateConfigurationAsync()
    {
        await SaveConfigurationAsync();
        if (_orchestrator.IsRunning)
        {
            await _orchestrator.UpdateConfigurationAsync(_config);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshTopologyAsync();
    }

    private async void StartShareButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isAdmin)
        {
            SetStatus("未获得管理员权限，无法开始分享。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            await _orchestrator.StartAsync(_config);
            StartShareButton.IsEnabled = false;
            StopShareButton.IsEnabled = true;
            SetStatus("分享编排器已启动。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"启动失败: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void StopShareButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _orchestrator.StopAsync();
            StartShareButton.IsEnabled = _isAdmin;
            StopShareButton.IsEnabled = false;
            SetStatus("分享已停止并完成会话回滚。", InfoBarSeverity.Informational);
            await RefreshTopologyAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"停止失败: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        SetStatus("Debug 模式下不执行真实提权重启。请手动以管理员方式启动。", InfoBarSeverity.Informational);
        return;
#else
        if (_adminService.TryRelaunchAsAdministrator(out var error))
        {
            App.Current.Exit();
            return;
        }

        SetStatus($"管理员重启失败: {error}", InfoBarSeverity.Error);
#endif
    }

    private void RemoteListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection is consumed by edit/delete/test actions.
    }

    private async void AddRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await ShowRemoteEditorDialogAsync(null);
        if (result is null)
        {
            return;
        }

        _config.Remotes.Add(result.Remote);
        await PersistRemoteSecretsAsync(result);
        SyncSecretRefs(result.Remote.Id);
        await PersistAndPropagateConfigurationAsync();

        RefreshRemoteList();
        RefreshRuleRemoteOptions();
        ApplyAssignmentsAndConflicts();
    }

    private async void EditRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRemote;
        if (selected is null)
        {
            SetStatus("请先选择一个远程。", InfoBarSeverity.Warning);
            return;
        }

        var result = await ShowRemoteEditorDialogAsync(selected);
        if (result is null)
        {
            return;
        }

        var index = _config.Remotes.FindIndex(remote => remote.Id == selected.Id);
        if (index >= 0)
        {
            _config.Remotes[index] = result.Remote;
        }

        await PersistRemoteSecretsAsync(result);
        SyncSecretRefs(result.Remote.Id);
        await PersistAndPropagateConfigurationAsync();

        RefreshRemoteList();
        RefreshRuleRemoteOptions();
        ApplyAssignmentsAndConflicts();
    }

    private async void DeleteRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRemote;
        if (selected is null)
        {
            SetStatus("请先选择要删除的远程。", InfoBarSeverity.Warning);
            return;
        }

        var confirmDialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "删除远程",
            Content = $"确认删除远程 “{selected.DisplayTitle}”？关联规则将一并删除。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        var confirm = await confirmDialog.ShowAsync();
        if (confirm != ContentDialogResult.Primary)
        {
            return;
        }

        _config.Remotes.RemoveAll(remote => remote.Id == selected.Id);
        _config.ShareRules.RemoveAll(rule => rule.RemoteId == selected.Id);
        _config.SecretRefs.RemoveAll(reference => reference.RemoteId == selected.Id);
        await _secretStore.DeleteSecretAsync(selected.Id, SecretKind.Ssh);
        await _secretStore.DeleteSecretAsync(selected.Id, SecretKind.Sudo);
        await PersistAndPropagateConfigurationAsync();

        RefreshRemoteList();
        RefreshRuleRemoteOptions();
        ApplyAssignmentsAndConflicts();
        SetStatus("远程已删除。", InfoBarSeverity.Success);
    }

    private async void TestRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRemote;
        if (selected is null)
        {
            SetStatus("请先选择一个远程。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var sshSecret = await _secretStore.GetSecretAsync(selected.Id, SecretKind.Ssh);
            await using var session = new SshRemoteSession(selected);
            await session.ConnectAsync(sshSecret);
            var probe = await session.ProbeAsync();
            if (probe.Success && probe.Output.Contains("READY", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus($"远程 {selected.DisplayTitle} 连接成功，usbip/sudo 可用。", InfoBarSeverity.Success);
            }
            else
            {
                SetStatus($"远程 {selected.DisplayTitle} 可连接，但缺少 usbip 或 sudo。{probe.Output} {probe.Error}".Trim(), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"远程连接失败: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void SaveRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            SetStatus("请先选择一个树节点。", InfoBarSeverity.Warning);
            return;
        }

        if (!_selectedNode.CanAssign)
        {
            SetStatus("该节点不可分配分享规则。", InfoBarSeverity.Warning);
            return;
        }

        Guid? selectedRemoteId = RuleRemoteComboBox.SelectedValue is Guid value ? value : null;
        _config.ShareRules.RemoveAll(rule =>
            rule.NodeType == _selectedNode.RuleNodeType &&
            string.Equals(rule.NodeInstanceId, _selectedNode.InstanceId, StringComparison.OrdinalIgnoreCase));

        if (selectedRemoteId.HasValue)
        {
            _config.ShareRules.Add(new ShareRule
            {
                NodeType = _selectedNode.RuleNodeType,
                NodeInstanceId = _selectedNode.InstanceId,
                RemoteId = selectedRemoteId.Value,
                Enabled = true,
            });
        }

        await PersistAndPropagateConfigurationAsync();
        ApplyAssignmentsAndConflicts();
        SetStatus("规则已保存。", InfoBarSeverity.Success);
    }

    private async void ClearRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            SetStatus("请先选择一个树节点。", InfoBarSeverity.Warning);
            return;
        }

        _config.ShareRules.RemoveAll(rule =>
            rule.NodeType == _selectedNode.RuleNodeType &&
            string.Equals(rule.NodeInstanceId, _selectedNode.InstanceId, StringComparison.OrdinalIgnoreCase));

        await PersistAndPropagateConfigurationAsync();
        RuleRemoteComboBox.SelectedValue = null;
        ApplyAssignmentsAndConflicts();
        SetStatus("规则已清除。", InfoBarSeverity.Informational);
    }

    private void UsbTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode?.Content is not UsbTreeItemViewModel vm)
        {
            _selectedNode = null;
            SelectedNodeTextBlock.Text = "选中节点: (无)";
            RuleRemoteComboBox.SelectedValue = null;
            return;
        }

        _selectedNode = vm;
        SelectedNodeTextBlock.Text = $"选中节点: {vm.Title}";
        var rule = FindRuleForNode(vm);
        RuleRemoteComboBox.SelectedValue = rule?.RemoteId;
    }

    private async void PollIntervalNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(sender.Value))
        {
            return;
        }

        _config.Settings.PollIntervalSeconds = Math.Clamp((int)Math.Round(sender.Value), 1, 30);
        sender.Value = _config.Settings.PollIntervalSeconds;
        await PersistAndPropagateConfigurationAsync();
    }

    private void OnOrchestratorStateChanged(object? sender, ShareSessionState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _sessionState = state;
            ApplyAssignmentsAndConflicts();

            if (state.LastErrorsByKey.Count > 0)
            {
                var latestError = state.LastErrorsByKey.Last();
                SetStatus($"运行告警: {latestError.Value}", InfoBarSeverity.Warning);
            }
        });
    }

    private async Task<RemoteEditorResult?> ShowRemoteEditorDialogAsync(RemoteConfig? existingRemote)
    {
        var nameBox = new TextBox { Header = "名称", Text = existingRemote?.Name ?? string.Empty };
        var hostBox = new TextBox { Header = "Host", Text = existingRemote?.Host ?? string.Empty };
        var portBox = new NumberBox
        {
            Header = "SSH 端口",
            Minimum = 1,
            Maximum = 65535,
            Value = existingRemote?.Port ?? 22,
        };
        var userBox = new TextBox { Header = "用户名", Text = existingRemote?.User ?? string.Empty };
        var authBox = new ComboBox
        {
            Header = "认证方式",
            ItemsSource = Enum.GetValues<AuthType>(),
            SelectedItem = existingRemote?.AuthType ?? AuthType.PrivateKey,
        };
        var keyPathBox = new TextBox
        {
            Header = "私钥路径(密钥认证)",
            Text = existingRemote?.KeyPath ?? string.Empty,
        };
        var tunnelPortBox = new NumberBox
        {
            Header = "远端映射端口",
            Minimum = 1,
            Maximum = 65535,
            Value = existingRemote?.TunnelPort ?? 3240,
        };
        var sshPasswordBox = new PasswordBox { Header = "SSH密码/密钥口令(留空=不修改)" };
        var sudoPasswordBox = new PasswordBox { Header = "sudo密码(留空=不修改)" };

        var panel = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                nameBox,
                hostBox,
                portBox,
                userBox,
                authBox,
                keyPathBox,
                tunnelPortBox,
                sshPasswordBox,
                sudoPasswordBox,
            },
        };

        void UpdateAuthUi()
        {
            var auth = (AuthType)authBox.SelectedItem;
            keyPathBox.IsEnabled = auth == AuthType.PrivateKey;
            keyPathBox.Visibility = auth == AuthType.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
        }

        authBox.SelectionChanged += (_, _) => UpdateAuthUi();
        UpdateAuthUi();

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = existingRemote is null ? "新增远程" : "编辑远程",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary)
        {
            return null;
        }

        var authType = (AuthType)authBox.SelectedItem;
        var remote = new RemoteConfig
        {
            Id = existingRemote?.Id ?? Guid.NewGuid(),
            Name = nameBox.Text.Trim(),
            Host = hostBox.Text.Trim(),
            Port = (int)Math.Round(portBox.Value),
            User = userBox.Text.Trim(),
            AuthType = authType,
            KeyPath = string.IsNullOrWhiteSpace(keyPathBox.Text) ? null : keyPathBox.Text.Trim(),
            TunnelPort = (int)Math.Round(tunnelPortBox.Value),
        };

        if (string.IsNullOrWhiteSpace(remote.Host) || string.IsNullOrWhiteSpace(remote.User))
        {
            SetStatus("Host 和 用户名不能为空。", InfoBarSeverity.Warning);
            return null;
        }

        if (authType == AuthType.PrivateKey && string.IsNullOrWhiteSpace(remote.KeyPath))
        {
            SetStatus("密钥认证需要填写私钥路径。", InfoBarSeverity.Warning);
            return null;
        }

        if (authType == AuthType.Password &&
            string.IsNullOrWhiteSpace(sshPasswordBox.Password) &&
            existingRemote is null)
        {
            SetStatus("密码认证在新建时必须提供 SSH 密码。", InfoBarSeverity.Warning);
            return null;
        }

        return new RemoteEditorResult
        {
            Remote = remote,
            SshSecret = string.IsNullOrWhiteSpace(sshPasswordBox.Password) ? null : sshPasswordBox.Password,
            SudoSecret = string.IsNullOrWhiteSpace(sudoPasswordBox.Password) ? null : sudoPasswordBox.Password,
        };
    }

    private async Task PersistRemoteSecretsAsync(RemoteEditorResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.SshSecret))
        {
            await _secretStore.SaveSecretAsync(result.Remote.Id, SecretKind.Ssh, result.SshSecret);
        }

        if (!string.IsNullOrWhiteSpace(result.SudoSecret))
        {
            await _secretStore.SaveSecretAsync(result.Remote.Id, SecretKind.Sudo, result.SudoSecret);
        }
    }

    private void SyncSecretRefs(Guid remoteId)
    {
        EnsureSecretRef(remoteId, SecretKind.Ssh);
        EnsureSecretRef(remoteId, SecretKind.Sudo);
    }

    private void EnsureSecretRef(Guid remoteId, SecretKind kind)
    {
        if (_config.SecretRefs.Any(reference => reference.RemoteId == remoteId && reference.SecretKind == kind))
        {
            return;
        }

        _config.SecretRefs.Add(new SecretRef
        {
            RemoteId = remoteId,
            SecretKind = kind,
        });
    }

    private sealed class RemoteEditorResult
    {
        public required RemoteConfig Remote { get; init; }
        public string? SshSecret { get; init; }
        public string? SudoSecret { get; init; }
    }
}
