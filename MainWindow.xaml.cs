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
    private readonly IUsbipdPrerequisiteService _usbipdPrerequisiteService;
    private readonly IUsbipdService _usbipdService;
    private readonly IUsbTopologyService _usbTopologyService;
    private readonly Dictionary<string, UsbTreeItemViewModel> _treeByInstanceId = new(StringComparer.OrdinalIgnoreCase);

    private AppConfig _config = new();
    private UsbTopologySnapshot _topology = new();
    private UsbipdPrerequisiteStatus _usbipdPrerequisiteStatus = new() { State = UsbipdInstallState.Missing };
    private ShareSessionState _sessionState = new();
    private UsbTreeItemViewModel? _selectedNode;
    private bool _isAdmin;
    private bool _isUsbipdAutoInstallInProgress;
    private bool _isInitializingLanguageSelection;
    private bool _hasShownUsbipdMissingDialog;
    private CancellationTokenSource? _usbipdInstallMonitorCts;
    private RemoteConfig? _selectedRemote;

    public MainWindow()
    {
        InitializeComponent();
        ApplyLocalizedStaticText();

        _usbipdPrerequisiteService = new UsbipdPrerequisiteService(_processRunner);
        _usbipdService = new UsbipdService(_processRunner);
        _usbTopologyService = new UsbTopologyService(new PnpDeviceService(), _usbipdService);
        _orchestrator = new ShareOrchestrator(
            _usbipdService,
            _usbTopologyService,
            new DeviceEnabledResolver(),
            _secretStore);

        _orchestrator.StateChanged += OnOrchestratorStateChanged;
        RootGrid.Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadConfigurationAsync();

        // 执行配置迁移（如果需要）
        ConfigMigration.Migrate(_config);
        await SaveConfigurationAsync();

        RefreshRemoteList();
        UpdateCurrentTargetDisplay();
        PollIntervalNumberBox.Value = _config.Settings.PollIntervalSeconds;
        AutoStartToggle.IsOn = _config.Settings.AutoStart;
        InitializeLanguageOptions();

        _isAdmin = _adminService.IsRunningAsAdministrator();
        if (_isAdmin)
        {
            AdminHintTextBlock.Text = LocalizationService.GetString("Status.AdminReady");
        }
        else
        {
            AdminHintTextBlock.Text = LocalizationService.GetString("Status.AdminRequired");
        }

        UpdateShareButtonState();
        await EnsureUsbipdPrerequisiteAsync(showDialogIfMissing: true, notifyIfMissing: true);
        await RefreshTopologyAsync(skipPrerequisiteCheck: true);
        await TryAutoStartAsync();
    }

    private void ApplyLocalizedStaticText()
    {
        Title = LocalizationService.GetString("MainWindow.Title");
        RefreshButton.Label = LocalizationService.GetString("RefreshButton.Label");
        StartShareButton.Label = LocalizationService.GetString("StartShareButton.Label");
        StopShareButton.Label = LocalizationService.GetString("StopShareButton.Label");
        RemoteSectionTitleTextBlock.Text = LocalizationService.GetString("RemoteSectionTitle.Text");
        CurrentTargetLabelTextBlock.Text = LocalizationService.GetString("CurrentTargetLabel.Text");
        AddRemoteButton.Content = LocalizationService.GetString("AddRemoteButton.Content");
        EditRemoteButton.Content = LocalizationService.GetString("EditRemoteButton.Content");
        DeleteRemoteButton.Content = LocalizationService.GetString("DeleteRemoteButton.Content");
        TestRemoteButton.Content = LocalizationService.GetString("TestRemoteButton.Content");
        SetAsTargetButton.Content = LocalizationService.GetString("SetAsTargetButton.Content");
        PollIntervalLabelTextBlock.Text = LocalizationService.GetString("PollIntervalLabel.Text");
        AutoStartToggle.Header = LocalizationService.GetString("AutoStartToggle.Header");
        LanguageLabelTextBlock.Text = LocalizationService.GetString("LanguageLabel.Text");
        LanguageRestartHintTextBlock.Text = LocalizationService.GetString("LanguageRestartHintTextBlock.Text");
        UsbShareSectionTitleTextBlock.Text = LocalizationService.GetString("UsbShareSectionTitle.Text");
        UsbipdPrerequisiteTitleTextBlock.Text = LocalizationService.GetString("UsbipdPrerequisite.Title");
        AutoInstallUsbipdButton.Content = LocalizationService.GetString("Usbipd.AutoInstallButton.Content");
        ManualInstallUsbipdButton.Content = LocalizationService.GetString("Usbipd.ManualInstallButton.Content");
        StatusInfoBar.Message = LocalizationService.GetString("StatusInfoBar.Message");
        BottomHintTextBlock.Text = LocalizationService.GetString("BottomHintTextBlock.Text");
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        try
        {
            _usbipdInstallMonitorCts?.Cancel();
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
        _config.Settings.PreferredLanguage = LocalizationService.NormalizePreference(_config.Settings.PreferredLanguage);
    }

    private Task SaveConfigurationAsync() => _configStore.SaveAsync(_config);

    private bool CanAutoStart() =>
        _config.Settings.AutoStart &&
        _isAdmin &&
        _usbipdPrerequisiteStatus.IsReady &&
        _config.Settings.SelectedRemoteId.HasValue &&
        _config.Remotes.Any(r => r.Id == _config.Settings.SelectedRemoteId!.Value) &&
        _config.EnabledDevices.Any(e => e.Enabled);

    private async Task TryAutoStartAsync()
    {
        if (!CanAutoStart())
        {
            return;
        }

        try
        {
            await _orchestrator.StartAsync(_config);
            UpdateShareButtonState();
            SetLocalizedStatus("Status.AutoStarted", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            UpdateShareButtonState();
            SetLocalizedStatus("Status.AutoStartFailed", InfoBarSeverity.Error, ex.Message);
        }
    }

    private async void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _config.Settings.AutoStart = AutoStartToggle.IsOn;
        await PersistAndPropagateConfigurationAsync();
    }

    private void InitializeLanguageOptions()
    {
        _isInitializingLanguageSelection = true;
        try
        {
            var options = new List<LanguageOption>
            {
                new(LocalizationService.SystemLanguage, LocalizationService.GetString("Language.Option.System")),
                new(LocalizationService.ChineseLanguage, LocalizationService.GetString("Language.Option.ZhCn")),
                new(LocalizationService.EnglishLanguage, LocalizationService.GetString("Language.Option.EnUs")),
            };

            LanguageComboBox.DisplayMemberPath = nameof(LanguageOption.Label);
            LanguageComboBox.ItemsSource = options;
            LanguageComboBox.SelectedItem = options.FirstOrDefault(option =>
                string.Equals(option.Value, _config.Settings.PreferredLanguage, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _isInitializingLanguageSelection = false;
        }
    }

    private async void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingLanguageSelection || LanguageComboBox.SelectedItem is not LanguageOption option)
        {
            return;
        }

        var preferredLanguage = LocalizationService.NormalizePreference(option.Value);
        if (string.Equals(_config.Settings.PreferredLanguage, preferredLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _config.Settings.PreferredLanguage = preferredLanguage;
        await SaveConfigurationAsync();
        SetLocalizedStatus("Status.LanguageSavedRestart", InfoBarSeverity.Informational);
    }

    private void RefreshRemoteList()
    {
        RemoteListView.ItemsSource = _config.Remotes
            .OrderBy(remote => remote.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void UpdateCurrentTargetDisplay()
    {
        if (_config.Settings.SelectedRemoteId.HasValue)
        {
            var target = _config.Remotes.FirstOrDefault(r => r.Id == _config.Settings.SelectedRemoteId.Value);
            CurrentTargetTextBlock.Text = target is not null
                ? target.DisplayTitle
                : LocalizationService.GetString("Status.TargetDeleted");
        }
        else
        {
            CurrentTargetTextBlock.Text = LocalizationService.GetString("Status.TargetNone");
        }

        UpdateShareButtonState();
    }

    private async Task RefreshTopologyAsync()
    {
        await RefreshTopologyAsync(skipPrerequisiteCheck: false);
    }

    private async Task RefreshTopologyAsync(bool skipPrerequisiteCheck)
    {
        if (!skipPrerequisiteCheck)
        {
            var ready = await EnsureUsbipdPrerequisiteAsync(showDialogIfMissing: false, notifyIfMissing: true);
            if (!ready)
            {
                UsbTreeView.RootNodes.Clear();
                _treeByInstanceId.Clear();
                return;
            }
        }

        if (!_usbipdPrerequisiteStatus.IsReady)
        {
            UsbTreeView.RootNodes.Clear();
            _treeByInstanceId.Clear();
            return;
        }

        SetLocalizedStatus("Status.ScanStarting", InfoBarSeverity.Informational);

        try
        {
            _topology = await _usbTopologyService.BuildSnapshotAsync();
            BuildTree();
            SetLocalizedStatus("Status.RefreshCompleted", InfoBarSeverity.Success, _treeByInstanceId.Count);
        }
        catch (Exception ex)
        {
            SetLocalizedStatus("Status.RefreshFailed", InfoBarSeverity.Error, ex.Message);
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

        ApplyEnabledStates();
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
        viewModel.UpdateShareStatus();

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
            parts.Add(LocalizationService.Format("Tree.Subtitle.BusId", node.BusId));
        }
        else if (!node.IsHub)
        {
            parts.Add(LocalizationService.GetString("Tree.Subtitle.NotDirectShare"));
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

    private void ApplyEnabledStates()
    {
        // 首先重置所有状态
        foreach (var vm in _treeByInstanceId.Values)
        {
            vm.IsEnabled = false;
            vm.IsInherited = false;
        }

        // 标记直接启用的节点
        var enabledHubInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enabledDeviceInstanceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var enabled in _config.EnabledDevices.Where(e => e.Enabled))
        {
            if (_treeByInstanceId.TryGetValue(enabled.NodeInstanceId, out var vm))
            {
                if (vm.IsHub)
                {
                    enabledHubInstanceIds.Add(enabled.NodeInstanceId);
                }
                else if (vm.IsShareable)
                {
                    enabledDeviceInstanceIds.Add(enabled.NodeInstanceId);
                }
            }
        }

        // 应用直接启用状态
        foreach (var instanceId in enabledDeviceInstanceIds)
        {
            if (_treeByInstanceId.TryGetValue(instanceId, out var vm))
            {
                vm.IsEnabled = true;
            }
        }

        foreach (var instanceId in enabledHubInstanceIds)
        {
            if (_treeByInstanceId.TryGetValue(instanceId, out var vm))
            {
                vm.IsEnabled = true;
            }
        }

        // 应用继承状态
        foreach (var vm in _treeByInstanceId.Values)
        {
            if (vm.IsEnabled || vm.IsHub)
            {
                continue;
            }

            // 检查是否有启用的祖先 Hub
            if (HasEnabledAncestorHub(vm, enabledHubInstanceIds))
            {
                vm.IsInherited = true;
            }
        }

        foreach (var vm in _treeByInstanceId.Values)
        {
            vm.UpdateShareStatus();
        }

        UpdatePrimaryActionGuide();
    }

    private bool HasEnabledAncestorHub(UsbTreeItemViewModel vm, HashSet<string> enabledHubIds)
    {
        var cursor = vm.ParentInstanceId;
        while (!string.IsNullOrWhiteSpace(cursor))
        {
            if (enabledHubIds.Contains(cursor))
            {
                return true;
            }

            if (!_treeByInstanceId.TryGetValue(cursor, out var parent))
            {
                break;
            }

            cursor = parent.ParentInstanceId;
        }

        return false;
    }

    private DeviceEnabled? GetEnabledDeviceForNode(UsbTreeItemViewModel vm)
    {
        return _config.EnabledDevices.FirstOrDefault(e =>
            string.Equals(e.NodeInstanceId, vm.InstanceId, StringComparison.OrdinalIgnoreCase));
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private void SetLocalizedStatus(string resourceKey, InfoBarSeverity severity, params object[] args)
    {
        SetStatus(LocalizationService.Format(resourceKey, args), severity);
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
        var ready = await EnsureUsbipdPrerequisiteAsync(showDialogIfMissing: false, notifyIfMissing: true);
        if (!ready)
        {
            return;
        }

        await RefreshTopologyAsync(skipPrerequisiteCheck: true);
    }

    private async void StartShareButton_Click(object sender, RoutedEventArgs e)
    {
        var ready = await EnsureUsbipdPrerequisiteAsync(showDialogIfMissing: true, notifyIfMissing: true);
        if (!ready)
        {
            SetLocalizedStatus("Status.UsbipdStartBlocked", InfoBarSeverity.Warning);
            return;
        }

        if (!_isAdmin)
        {
            SetLocalizedStatus("Status.StartRequiresAdmin", InfoBarSeverity.Warning);
            return;
        }

        if (!_config.Settings.SelectedRemoteId.HasValue)
        {
            SetLocalizedStatus("Status.TargetRequired", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            await _orchestrator.StartAsync(_config);
            UpdateShareButtonState();
            SetLocalizedStatus("Status.OrchestratorStarted", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SetLocalizedStatus("Status.StartFailed", InfoBarSeverity.Error, ex.Message);
        }
    }

    private async void StopShareButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _orchestrator.StopAsync();
            UpdateShareButtonState();
            SetLocalizedStatus("Status.Stopped", InfoBarSeverity.Informational);
            await RefreshTopologyAsync(skipPrerequisiteCheck: true);
        }
        catch (Exception ex)
        {
            SetLocalizedStatus("Status.StopFailed", InfoBarSeverity.Error, ex.Message);
        }
    }

    private void RemoteListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRemote = RemoteListView.SelectedItem as RemoteConfig;
        UpdateSetAsTargetButtonState();
    }

    private void UpdateSetAsTargetButtonState()
    {
        if (_selectedRemote is null)
        {
            SetAsTargetButton.IsEnabled = false;
            return;
        }

        // 如果已经选中了这个远程，禁用按钮
        var isAlreadySelected = _config.Settings.SelectedRemoteId.HasValue &&
                                _selectedRemote.Id == _config.Settings.SelectedRemoteId.Value;
        SetAsTargetButton.IsEnabled = !isAlreadySelected;
    }

    private async void SetAsTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRemote is null)
        {
            SetLocalizedStatus("Status.RemoteRequired", InfoBarSeverity.Warning);
            return;
        }

        // 如果正在运行，需要先停止
        if (_orchestrator.IsRunning)
        {
            SetLocalizedStatus("Status.StopBeforeChangeTarget", InfoBarSeverity.Warning);
            return;
        }

        _config.Settings.SelectedRemoteId = _selectedRemote.Id;
        await PersistAndPropagateConfigurationAsync();

        UpdateCurrentTargetDisplay();
        RefreshRemoteList();
        UpdateSetAsTargetButtonState();

        SetLocalizedStatus("Status.TargetSet", InfoBarSeverity.Success, _selectedRemote.DisplayTitle);
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
        ApplyEnabledStates();
    }

    private async void EditRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRemote is null)
        {
            SetLocalizedStatus("Status.RemoteSelectionRequired", InfoBarSeverity.Warning);
            return;
        }

        var result = await ShowRemoteEditorDialogAsync(_selectedRemote);
        if (result is null)
        {
            return;
        }

        var index = _config.Remotes.FindIndex(remote => remote.Id == _selectedRemote!.Id);
        if (index >= 0)
        {
            _config.Remotes[index] = result.Remote;
        }

        await PersistRemoteSecretsAsync(result);
        SyncSecretRefs(result.Remote.Id);
        await PersistAndPropagateConfigurationAsync();

        RefreshRemoteList();
        ApplyEnabledStates();
    }

    private async void DeleteRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRemote is null)
        {
            SetLocalizedStatus("Status.RemoteDeleteSelectionRequired", InfoBarSeverity.Warning);
            return;
        }

        // 如果是当前选中的远程，需要先清除
        if (_config.Settings.SelectedRemoteId.HasValue &&
            _selectedRemote.Id == _config.Settings.SelectedRemoteId.Value)
        {
            if (_orchestrator.IsRunning)
            {
                SetLocalizedStatus("Status.RemoteDeleteInUse", InfoBarSeverity.Warning);
                return;
            }

            _config.Settings.SelectedRemoteId = null;
        }

        var confirmDialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = LocalizationService.GetString("Dialog.DeleteRemote.Title"),
            Content = LocalizationService.Format("Dialog.DeleteRemote.Content", _selectedRemote.DisplayTitle),
            PrimaryButtonText = LocalizationService.GetString("Dialog.DeleteRemote.Primary"),
            CloseButtonText = LocalizationService.GetString("Dialog.Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        var confirm = await confirmDialog.ShowAsync();
        if (confirm != ContentDialogResult.Primary)
        {
            return;
        }

        _config.Remotes.RemoveAll(remote => remote.Id == _selectedRemote.Id);
        _config.SecretRefs.RemoveAll(reference => reference.RemoteId == _selectedRemote.Id);
        await _secretStore.DeleteSecretAsync(_selectedRemote.Id, SecretKind.Ssh);
        await _secretStore.DeleteSecretAsync(_selectedRemote.Id, SecretKind.Sudo);
        await PersistAndPropagateConfigurationAsync();

        RefreshRemoteList();
        UpdateCurrentTargetDisplay();
        ApplyEnabledStates();
        SetLocalizedStatus("Status.RemoteDeleted", InfoBarSeverity.Success);
    }

    private async void TestRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRemote is null)
        {
            SetLocalizedStatus("Status.RemoteSelectionRequired", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var sshSecret = await _secretStore.GetSecretAsync(_selectedRemote.Id, SecretKind.Ssh);
            await using var session = new SshRemoteSession(_selectedRemote);
            await session.ConnectAsync(sshSecret);
            var probe = await session.ProbeAsync();
            if (probe.Success && probe.Output.Contains("READY", StringComparison.OrdinalIgnoreCase))
            {
                SetLocalizedStatus("Status.RemoteConnectionSuccess", InfoBarSeverity.Success, _selectedRemote.DisplayTitle);
            }
            else
            {
                SetLocalizedStatus(
                    "Status.RemoteConnectionMissingRequirements",
                    InfoBarSeverity.Warning,
                    _selectedRemote.DisplayTitle,
                    $"{probe.Output} {probe.Error}".Trim());
            }
        }
        catch (Exception ex)
        {
            SetLocalizedStatus("Status.RemoteConnectionFailed", InfoBarSeverity.Error, ex.Message);
        }
    }

    private async void EnableDeviceCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        // 此方法已废弃，现在使用 DeviceCheckBox_Click 进行内联交互
        // 保留此方法以防向后兼容
        if (_selectedNode is null)
        {
            return;
        }

        if (!_selectedNode.CanEnable)
        {
            SetLocalizedStatus("Status.NodeUnavailable", InfoBarSeverity.Warning);
            return;
        }

        // 切换设备状态
        var newEnabled = !_selectedNode.IsEnabled;
        await ToggleDeviceEnabledAsync(_selectedNode.InstanceId, newEnabled);
        SetLocalizedStatus(newEnabled ? "Status.DeviceEnabled" : "Status.DeviceDisabled", InfoBarSeverity.Success);
    }

    private void UsbTreeView_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (sender.SelectedNode?.Content is not UsbTreeItemViewModel vm)
        {
            _selectedNode = null;
            return;
        }

        _selectedNode = vm;
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
        DispatcherQueue?.TryEnqueue(() =>
        {
            _sessionState = state;
            UpdateRuntimeStates();

            if (state.LastErrorsByKey.Count > 0)
            {
                var latestError = state.LastErrorsByKey.Last();
                SetLocalizedStatus("Status.RuntimeWarning", InfoBarSeverity.Warning, latestError.Value);
            }

            UpdateShareButtonState();
        });
    }

    private void UpdateShareButtonState()
    {
        StartShareButton.IsEnabled = !_orchestrator.IsRunning && _isAdmin && _usbipdPrerequisiteStatus.IsReady && HasSelectedTarget();
        StopShareButton.IsEnabled = _orchestrator.IsRunning;
        StartShareButton.Opacity = _orchestrator.IsRunning ? 0.8 : 1.0;
        StopShareButton.Opacity = _orchestrator.IsRunning ? 1.0 : 0.8;
        UpdatePrimaryActionGuide();
    }

    private bool HasSelectedTarget()
    {
        return _config.Settings.SelectedRemoteId.HasValue &&
               _config.Remotes.Any(r => r.Id == _config.Settings.SelectedRemoteId.Value);
    }

    private async Task<bool> EnsureUsbipdPrerequisiteAsync(bool showDialogIfMissing, bool notifyIfMissing)
    {
        _usbipdPrerequisiteStatus = await _usbipdPrerequisiteService.CheckAsync();
        UpdateUsbipdPrerequisiteUi();
        UpdateShareButtonState();

        if (_usbipdPrerequisiteStatus.IsReady)
        {
            return true;
        }

        if (notifyIfMissing)
        {
            SetLocalizedStatus(
                _usbipdPrerequisiteStatus.State == UsbipdInstallState.Unavailable
                    ? "Status.UsbipdUnavailable"
                    : "Status.UsbipdMissing",
                InfoBarSeverity.Warning);
        }

        if (showDialogIfMissing && !_hasShownUsbipdMissingDialog)
        {
            _hasShownUsbipdMissingDialog = true;
            await ShowUsbipdMissingDialogAsync();
        }

        return false;
    }

    private void UpdateUsbipdPrerequisiteUi()
    {
        if (_usbipdPrerequisiteStatus.IsReady)
        {
            UsbipdPrerequisiteBorder.Visibility = Visibility.Collapsed;
            return;
        }

        UsbipdPrerequisiteBorder.Visibility = Visibility.Visible;
        UsbipdPrerequisiteTitleTextBlock.Text = LocalizationService.GetString("UsbipdPrerequisite.Title");

        string message;
        if (_isUsbipdAutoInstallInProgress)
        {
            message = LocalizationService.GetString("UsbipdPrerequisite.MessageWaiting");
        }
        else
        {
            message = _usbipdPrerequisiteStatus.State == UsbipdInstallState.Unavailable
                ? LocalizationService.GetString("UsbipdPrerequisite.MessageUnavailable")
                : LocalizationService.GetString("UsbipdPrerequisite.MessageMissing");
        }

        if (!_usbipdPrerequisiteStatus.WingetAvailable)
        {
            message = $"{message} {LocalizationService.GetString("UsbipdPrerequisite.WingetUnavailable")}".Trim();
        }

        UsbipdPrerequisiteTextBlock.Text = message;
        AutoInstallUsbipdButton.IsEnabled = !_isUsbipdAutoInstallInProgress && _usbipdPrerequisiteStatus.WingetAvailable;
        ManualInstallUsbipdButton.IsEnabled = true;
        UpdatePrimaryActionGuide();
    }

    private async Task ShowUsbipdMissingDialogAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = LocalizationService.GetString("Dialog.UsbipdMissing.Title"),
            Content = BuildUsbipdMissingDialogContent(),
            DefaultButton = ContentDialogButton.Primary,
            CloseButtonText = LocalizationService.GetString("Dialog.Common.Cancel"),
        };

        if (_usbipdPrerequisiteStatus.WingetAvailable)
        {
            dialog.PrimaryButtonText = LocalizationService.GetString("Usbipd.AutoInstallButton.Content");
            dialog.SecondaryButtonText = LocalizationService.GetString("Usbipd.ManualInstallButton.Content");
        }
        else
        {
            dialog.PrimaryButtonText = LocalizationService.GetString("Usbipd.ManualInstallButton.Content");
        }

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (_usbipdPrerequisiteStatus.WingetAvailable)
            {
                await StartUsbipdAutoInstallAsync();
            }
            else
            {
                await OpenUsbipdManualInstallAsync();
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await OpenUsbipdManualInstallAsync();
        }
    }

    private object BuildUsbipdMissingDialogContent()
    {
        var key = _usbipdPrerequisiteStatus.State == UsbipdInstallState.Unavailable
            ? "Dialog.UsbipdMissing.ContentUnavailable"
            : "Dialog.UsbipdMissing.ContentMissing";
        var text = LocalizationService.GetString(key);

        if (!_usbipdPrerequisiteStatus.WingetAvailable)
        {
            text = $"{text} {LocalizationService.GetString("UsbipdPrerequisite.WingetUnavailable")}".Trim();
        }

        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.WrapWholeWords,
        };
    }

    private async void AutoInstallUsbipdButton_Click(object sender, RoutedEventArgs e)
    {
        await StartUsbipdAutoInstallAsync();
    }

    private async void ManualInstallUsbipdButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenUsbipdManualInstallAsync();
    }

    private async Task StartUsbipdAutoInstallAsync()
    {
        if (!_usbipdPrerequisiteStatus.WingetAvailable)
        {
            SetLocalizedStatus("Status.WingetUnavailable", InfoBarSeverity.Warning);
            await OpenUsbipdManualInstallAsync();
            return;
        }

        var launched = _usbipdPrerequisiteService.TryLaunchWingetInstall();
        if (!launched)
        {
            SetLocalizedStatus("Status.UsbipdAutoInstallLaunchFailed", InfoBarSeverity.Error);
            await OpenUsbipdManualInstallAsync();
            return;
        }

        SetLocalizedStatus("Status.UsbipdInstallStarted", InfoBarSeverity.Informational);
        StartUsbipdInstallMonitoring();
    }

    private async Task OpenUsbipdManualInstallAsync()
    {
        var opened = _usbipdPrerequisiteService.OpenOfficialInstallPage();
        if (!opened)
        {
            SetLocalizedStatus("Status.UsbipdManualInstallOpenFailed", InfoBarSeverity.Error);
            return;
        }

        SetLocalizedStatus("Status.UsbipdManualInstallOpened", InfoBarSeverity.Informational);
        await Task.CompletedTask;
    }

    private void StartUsbipdInstallMonitoring()
    {
        _usbipdInstallMonitorCts?.Cancel();
        _usbipdInstallMonitorCts?.Dispose();
        _usbipdInstallMonitorCts = new CancellationTokenSource();
        _isUsbipdAutoInstallInProgress = true;
        UpdateUsbipdPrerequisiteUi();
        _ = MonitorUsbipdInstallAsync(_usbipdInstallMonitorCts.Token);
    }

    private async Task MonitorUsbipdInstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await _usbipdPrerequisiteService
                .WaitForInstalledAsync(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);

            await EnqueueOnUiThreadAsync(async () =>
            {
                _isUsbipdAutoInstallInProgress = false;
                _usbipdPrerequisiteStatus = status;
                UpdateUsbipdPrerequisiteUi();
                UpdateShareButtonState();

                if (status.IsReady)
                {
                    SetLocalizedStatus("Status.UsbipdInstallDetected", InfoBarSeverity.Success);
                    await RefreshTopologyAsync(skipPrerequisiteCheck: true);
                }
                else
                {
                    SetLocalizedStatus("Status.UsbipdInstallTimedOut", InfoBarSeverity.Warning);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Ignore cancelled monitoring.
        }
    }

    private Task EnqueueOnUiThreadAsync(Func<Task> action)
    {
        if (DispatcherQueue is null)
        {
            return action();
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        if (!enqueued)
        {
            tcs.TrySetException(new InvalidOperationException("Failed to enqueue action on UI thread."));
        }

        return tcs.Task;
    }

    /// <summary>
    /// 更新运行时状态（bound/attached）到视图模型。
    /// </summary>
    private void UpdateRuntimeStates()
    {
        var boundBusIds = _sessionState.BoundBusIds;
        var attachedBusIds = _sessionState.AttachedBusIds;

        foreach (var vm in _treeByInstanceId.Values)
        {
            vm.IsBound = !string.IsNullOrWhiteSpace(vm.BusId) &&
                         boundBusIds.Contains(vm.BusId);
            vm.IsAttached = !string.IsNullOrWhiteSpace(vm.BusId) &&
                            attachedBusIds.Contains(vm.BusId);
        }
    }

    /// <summary>
    /// 设备复选框点击事件处理（内联交互）。
    /// </summary>
    private async void DeviceCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is string instanceId)
        {
            await ToggleDeviceEnabledAsync(instanceId, checkBox.IsChecked ?? false);
        }
    }

    /// <summary>
    /// 切换设备启用状态。
    /// </summary>
    private async Task ToggleDeviceEnabledAsync(string instanceId, bool enabled)
    {
        // 移除旧记录
        _config.EnabledDevices.RemoveAll(e =>
            string.Equals(e.NodeInstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

        // 添加新记录
        if (enabled)
        {
            _config.EnabledDevices.Add(new DeviceEnabled
            {
                NodeInstanceId = instanceId,
                Enabled = true,
            });
        }

        await PersistAndPropagateConfigurationAsync();
        ApplyEnabledStates();
    }

    private async Task<RemoteEditorResult?> ShowRemoteEditorDialogAsync(RemoteConfig? existingRemote)
    {
        var nameBox = new TextBox { Header = LocalizationService.GetString("Dialog.RemoteEditor.NameHeader"), Text = existingRemote?.Name ?? string.Empty };
        var hostBox = new TextBox { Header = LocalizationService.GetString("Dialog.RemoteEditor.HostHeader"), Text = existingRemote?.Host ?? string.Empty };
        var portBox = new NumberBox
        {
            Header = LocalizationService.GetString("Dialog.RemoteEditor.PortHeader"),
            Minimum = 1,
            Maximum = 65535,
            Value = existingRemote?.Port ?? 22,
        };
        var userBox = new TextBox { Header = LocalizationService.GetString("Dialog.RemoteEditor.UserHeader"), Text = existingRemote?.User ?? string.Empty };
        var authOptions = new List<AuthTypeOption>
        {
            new(AuthType.Password, LocalizationService.GetAuthTypeLabel(AuthType.Password)),
            new(AuthType.PrivateKey, LocalizationService.GetAuthTypeLabel(AuthType.PrivateKey)),
        };
        var authBox = new ComboBox
        {
            Header = LocalizationService.GetString("Dialog.RemoteEditor.AuthHeader"),
            DisplayMemberPath = nameof(AuthTypeOption.Label),
            ItemsSource = authOptions,
            SelectedItem = authOptions.First(option => option.Value == (existingRemote?.AuthType ?? AuthType.PrivateKey)),
        };
        var keyPathBox = new TextBox
        {
            Header = LocalizationService.GetString("Dialog.RemoteEditor.KeyPathHeader"),
            Text = existingRemote?.KeyPath ?? string.Empty,
            PlaceholderText = LocalizationService.GetString("Dialog.RemoteEditor.KeyPathPlaceholder"),
        };
        var tunnelPortBox = new NumberBox
        {
            Header = LocalizationService.GetString("Dialog.RemoteEditor.TunnelPortHeader"),
            Minimum = 1,
            Maximum = 65535,
            Value = existingRemote?.TunnelPort ?? 3240,
        };
        var sshPasswordBox = new PasswordBox { Header = LocalizationService.GetString("Dialog.RemoteEditor.SshPasswordHeader") };
        var sudoPasswordBox = new PasswordBox { Header = LocalizationService.GetString("Dialog.RemoteEditor.SudoPasswordHeader") };
        var validationTextBlock = new TextBlock
        {
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.WrapWholeWords,
            Visibility = Visibility.Collapsed,
        };

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
                validationTextBlock,
            },
        };

        void SetValidationMessage(string? message)
        {
            validationTextBlock.Text = message ?? string.Empty;
            validationTextBlock.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        string? ValidateInputs()
        {
            var authType = authBox.SelectedItem is AuthTypeOption authOption ? authOption.Value : AuthType.PrivateKey;
            if (string.IsNullOrWhiteSpace(hostBox.Text) || string.IsNullOrWhiteSpace(userBox.Text))
            {
                return LocalizationService.GetString("Validation.HostUserRequired");
            }

            if (double.IsNaN(portBox.Value) || portBox.Value < 1 || portBox.Value > 65535)
            {
                return LocalizationService.GetString("Validation.SshPortRange");
            }

            if (double.IsNaN(tunnelPortBox.Value) || tunnelPortBox.Value < 1 || tunnelPortBox.Value > 65535)
            {
                return LocalizationService.GetString("Validation.TunnelPortRange");
            }

            if (authType == AuthType.Password &&
                string.IsNullOrWhiteSpace(sshPasswordBox.Password) &&
                existingRemote is null)
            {
                return LocalizationService.GetString("Validation.PasswordRequiredOnCreate");
            }

            return null;
        }

        void UpdateAuthUi()
        {
            var auth = authBox.SelectedItem is AuthTypeOption authOption ? authOption.Value : AuthType.PrivateKey;
            keyPathBox.IsEnabled = auth == AuthType.PrivateKey;
            keyPathBox.Visibility = auth == AuthType.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
        }

        authBox.SelectionChanged += (_, _) =>
        {
            UpdateAuthUi();
            SetValidationMessage(null);
        };
        hostBox.TextChanged += (_, _) => SetValidationMessage(null);
        userBox.TextChanged += (_, _) => SetValidationMessage(null);
        keyPathBox.TextChanged += (_, _) => SetValidationMessage(null);
        sshPasswordBox.PasswordChanged += (_, _) => SetValidationMessage(null);
        portBox.ValueChanged += (_, _) => SetValidationMessage(null);
        tunnelPortBox.ValueChanged += (_, _) => SetValidationMessage(null);
        UpdateAuthUi();

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = LocalizationService.GetString(existingRemote is null ? "Dialog.RemoteEditor.AddTitle" : "Dialog.RemoteEditor.EditTitle"),
            Content = panel,
            PrimaryButtonText = LocalizationService.GetString("Dialog.Common.Save"),
            CloseButtonText = LocalizationService.GetString("Dialog.Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var validationMessage = ValidateInputs();
            if (validationMessage is null)
            {
                SetValidationMessage(null);
                return;
            }

            args.Cancel = true;
            SetValidationMessage(validationMessage);
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary)
        {
            return null;
        }

        var authType = authBox.SelectedItem is AuthTypeOption authOption ? authOption.Value : AuthType.PrivateKey;
        var remote = new RemoteConfig
        {
            Id = existingRemote?.Id ?? Guid.NewGuid(),
            Name = nameBox.Text.Trim(),
            Host = hostBox.Text.Trim(),
            Port = (int)Math.Round(portBox.Value),
            User = userBox.Text.Trim(),
            AuthType = authType,
            KeyPath = authType == AuthType.PrivateKey && !string.IsNullOrWhiteSpace(keyPathBox.Text) ? keyPathBox.Text.Trim() : null,
            TunnelPort = (int)Math.Round(tunnelPortBox.Value),
        };

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

    private void UpdatePrimaryActionGuide()
    {
        var content = BuildPrimaryActionGuideContent();
        BottomHintTextBlock.Text = $"{content.Title}: {content.Description} {content.Summary}".Trim();
    }

    private PrimaryActionGuideContent BuildPrimaryActionGuideContent()
    {
        var enabledDeviceCount = _config.EnabledDevices.Count(device => device.Enabled);
        var targetLabel = HasSelectedTarget()
            ? _config.Remotes.First(remote => remote.Id == _config.Settings.SelectedRemoteId!.Value).DisplayTitle
            : LocalizationService.GetString("Status.TargetNone");
        var summary = LocalizationService.Format("ActionGuide.Summary.TargetAndDeviceCount", targetLabel, enabledDeviceCount);

        if (_orchestrator.IsRunning)
        {
            return new PrimaryActionGuideContent(
                PrimaryActionGuideKind.Running,
                LocalizationService.GetString("ActionGuide.Title.Running"),
                LocalizationService.GetString("ActionGuide.Description.Running"),
                summary);
        }

        if (!_usbipdPrerequisiteStatus.IsReady)
        {
            return new PrimaryActionGuideContent(
                PrimaryActionGuideKind.Blocked,
                LocalizationService.GetString("ActionGuide.Title.Usbipd"),
                LocalizationService.GetString("ActionGuide.Description.Usbipd"),
                summary);
        }

        if (!_isAdmin)
        {
            return new PrimaryActionGuideContent(
                PrimaryActionGuideKind.Blocked,
                LocalizationService.GetString("ActionGuide.Title.Admin"),
                LocalizationService.GetString("ActionGuide.Description.Admin"),
                summary);
        }

        if (!HasSelectedTarget())
        {
            return new PrimaryActionGuideContent(
                PrimaryActionGuideKind.Blocked,
                LocalizationService.GetString("ActionGuide.Title.Target"),
                LocalizationService.GetString("ActionGuide.Description.Target"),
                summary);
        }

        return new PrimaryActionGuideContent(
            PrimaryActionGuideKind.Ready,
            LocalizationService.GetString("ActionGuide.Title.Ready"),
            LocalizationService.GetString("ActionGuide.Description.Ready"),
            summary);
    }

    private sealed class RemoteEditorResult
    {
        public required RemoteConfig Remote { get; init; }
        public string? SshSecret { get; init; }
        public string? SudoSecret { get; init; }
    }

    private enum PrimaryActionGuideKind
    {
        Ready,
        Running,
        Blocked,
    }

    private sealed record PrimaryActionGuideContent(
        PrimaryActionGuideKind Kind,
        string Title,
        string Description,
        string Summary);

    private sealed record LanguageOption(string Value, string Label);

    private sealed record AuthTypeOption(AuthType Value, string Label);
}
