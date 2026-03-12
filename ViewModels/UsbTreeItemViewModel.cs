using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using USBShare.Models;

namespace USBShare.ViewModels;

/// <summary>
/// 设备分享状态枚举。
/// </summary>
public enum DeviceShareStatus
{
    /// <summary>不可分享（非 USB 设备或接口）</summary>
    Unavailable,
    /// <summary>可用但未启用</summary>
    Available,
    /// <summary>已启用但未运行</summary>
    Enabled,
    /// <summary>继承启用状态</summary>
    Inherited,
    /// <summary>已绑定（bound）</summary>
    Bound,
    /// <summary>已分享（attached）</summary>
    Attached,
    /// <summary>错误状态</summary>
    Error
}

public sealed class UsbTreeItemViewModel : BindableBase
{
    private bool _isEnabled;
    private bool _isInherited;
    private bool _isBound;
    private bool _isAttached;
    private DeviceShareStatus _shareStatus;

    public string InstanceId { get; init; } = string.Empty;
    public string? ParentInstanceId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public bool IsHub { get; init; }
    public bool IsShareable { get; init; }
    public string? BusId { get; init; }
    public List<UsbTreeItemViewModel> Children { get; } = [];

    public bool CanEnable => IsHub || IsShareable;

    public string Glyph => IsHub ? "\uE7F4" : "\uEC4F";

    /// <summary>
    /// 运行时状态：已 usbipd bind。
    /// </summary>
    public bool IsBound
    {
        get => _isBound;
        set
        {
            if (SetProperty(ref _isBound, value))
            {
                UpdateShareStatus();
            }
        }
    }

    /// <summary>
    /// 运行时状态：已在远程 attach。
    /// </summary>
    public bool IsAttached
    {
        get => _isAttached;
        set
        {
            if (SetProperty(ref _isAttached, value))
            {
                UpdateShareStatus();
            }
        }
    }

    /// <summary>
    /// 综合分享状态。
    /// </summary>
    public DeviceShareStatus ShareStatus
    {
        get => _shareStatus;
        private set
        {
            if (SetProperty(ref _shareStatus, value))
            {
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(StatusBrushNew));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ShowStatusBadge));
                OnPropertyChanged(nameof(StatusBadgeBrush));
                OnPropertyChanged(nameof(HasStatusIcon));
            }
        }
    }

    /// <summary>
    /// 是否已启用分享。
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(IsInteractive));
                OnPropertyChanged(nameof(ShowCheckBox));
                OnPropertyChanged(nameof(StatusGlyph));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(HasStatus));
                UpdateShareStatus();
            }
        }
    }

    /// <summary>
    /// 是否通过继承获得启用状态（来自祖先 Hub）。
    /// </summary>
    public bool IsInherited
    {
        get => _isInherited;
        set
        {
            if (SetProperty(ref _isInherited, value))
            {
                OnPropertyChanged(nameof(StatusGlyph));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(IsInteractive));
                OnPropertyChanged(nameof(ShowCheckBox));
                UpdateShareStatus();
            }
        }
    }

    /// <summary>
    /// 是否有状态显示（启用或继承）。
    /// </summary>
    public bool HasStatus => IsEnabled || IsInherited;

    /// <summary>
    /// 是否可交互（非继承状态）。
    /// </summary>
    public bool IsInteractive => CanEnable && !IsInherited;

    /// <summary>
    /// 是否显示 CheckBox（可交互且可分享）。
    /// </summary>
    public bool ShowCheckBox => IsInteractive;

    /// <summary>
    /// 状态图标。
    /// </summary>
    public string StatusGlyph
    {
        get
        {
            if (IsInherited)
            {
                return "\uE890"; // Link/Chain icon for inherited
            }
            if (IsEnabled)
            {
                return "\uE73E"; // Check icon
            }
            return "\uE8A7"; // Empty circle
        }
    }

    /// <summary>
    /// 状态颜色。
    /// </summary>
    public SolidColorBrush StatusBrush =>
        IsInherited
            ? new SolidColorBrush(Colors.YellowGreen)  // 继承状态用黄绿色
            : IsEnabled
                ? new SolidColorBrush(Colors.Green)   // 直接启用用绿色
                : new SolidColorBrush(Colors.LightGray);

    /// <summary>
    /// 基于 ShareStatus 的状态色（用于新版状态图标绑定）。
    /// </summary>
    public SolidColorBrush StatusBrushNew => ShareStatus switch
    {
        DeviceShareStatus.Enabled => new SolidColorBrush(ColorFromString("#228B22")),
        DeviceShareStatus.Inherited => new SolidColorBrush(ColorFromString("#9ACD32")),
        DeviceShareStatus.Bound => new SolidColorBrush(ColorFromString("#1E90FF")),
        DeviceShareStatus.Attached => new SolidColorBrush(ColorFromString("#32CD32")),
        DeviceShareStatus.Error => new SolidColorBrush(ColorFromString("#FF4500")),
        _ => new SolidColorBrush(Colors.Gray),
    };

    /// <summary>
    /// 新的状态图标（基于 ShareStatus）。
    /// </summary>
    public string StatusIcon => ShareStatus switch
    {
        DeviceShareStatus.Unavailable => "\uE7BA",  // Blocked
        DeviceShareStatus.Available => "\uE8A7",    // Circle
        DeviceShareStatus.Enabled => "\uE73E",      // CheckMark
        DeviceShareStatus.Inherited => "\uE890",    // Link
        DeviceShareStatus.Bound => "\uE74B",        // Pin
        DeviceShareStatus.Attached => "\uE727",     // StatusCircleCheck
        DeviceShareStatus.Error => "\uE783",        // ErrorBadge
        _ => "\uE8A7"
    };

    /// <summary>
    /// 状态文本描述。
    /// </summary>
    public string StatusText => ShareStatus switch
    {
        DeviceShareStatus.Unavailable => "不可分享",
        DeviceShareStatus.Available => "可用",
        DeviceShareStatus.Enabled => "已启用",
        DeviceShareStatus.Inherited => "继承",
        DeviceShareStatus.Bound => "已绑定",
        DeviceShareStatus.Attached => "已分享",
        DeviceShareStatus.Error => "错误",
        _ => string.Empty
    };

    /// <summary>
    /// 是否显示状态徽章。
    /// </summary>
    public bool ShowStatusBadge => ShareStatus != DeviceShareStatus.Available && ShareStatus != DeviceShareStatus.Unavailable;

    /// <summary>
    /// 状态徽章背景色。
    /// </summary>
    public SolidColorBrush StatusBadgeBrush => ShareStatus switch
    {
        DeviceShareStatus.Enabled => new SolidColorBrush(ColorFromString("#228B22")),
        DeviceShareStatus.Inherited => new SolidColorBrush(ColorFromString("#9ACD32")),
        DeviceShareStatus.Bound => new SolidColorBrush(ColorFromString("#1E90FF")),
        DeviceShareStatus.Attached => new SolidColorBrush(ColorFromString("#32CD32")),
        DeviceShareStatus.Error => new SolidColorBrush(ColorFromString("#FF4500")),
        _ => new SolidColorBrush(Colors.Gray)
    };

    /// <summary>
    /// 是否有状态图标显示。
    /// </summary>
    public bool HasStatusIcon => ShareStatus != DeviceShareStatus.Available && ShareStatus != DeviceShareStatus.Unavailable;

    public bool IsDimmed => !IsHub && !IsShareable;
    public double TitleOpacity => IsDimmed ? 0.55 : 1.0;

    /// <summary>
    /// 更新综合分享状态。
    /// </summary>
    public void UpdateShareStatus()
    {
        var newStatus = ComputeShareStatus();
        if (_shareStatus != newStatus)
        {
            ShareStatus = newStatus;
        }
    }

    private DeviceShareStatus ComputeShareStatus()
    {
        // 不可分享
        if (!IsShareable && !IsHub)
        {
            return DeviceShareStatus.Unavailable;
        }

        // Hub 节点不显示运行时状态
        if (IsHub)
        {
            return IsEnabled ? DeviceShareStatus.Enabled : DeviceShareStatus.Available;
        }

        // 运行时状态优先
        if (IsAttached)
        {
            return DeviceShareStatus.Attached;
        }

        if (IsBound)
        {
            return DeviceShareStatus.Bound;
        }

        // 配置状态
        if (IsInherited)
        {
            return DeviceShareStatus.Inherited;
        }

        if (IsEnabled)
        {
            return DeviceShareStatus.Enabled;
        }

        return DeviceShareStatus.Available;
    }

    private static Color ColorFromString(string hex)
    {
        try
        {
            // 移除可能的 # 前缀
            hex = hex.TrimStart('#');

            // 确保是 8 位十六进制（ARGB）或 6 位（RGB）
            if (hex.Length == 6)
            {
                hex = "FF" + hex; // 添加完全不透明的 alpha 通道
            }

            if (hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint uintColor))
            {
                var a = (byte)(uintColor >> 24);
                var r = (byte)(uintColor >> 16);
                var g = (byte)(uintColor >> 8);
                var b = (byte)(uintColor >> 0);
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch
        {
            // 忽略解析错误
        }
        return Colors.Gray;
    }
}
