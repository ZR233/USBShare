using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using USBShare.Models;

namespace USBShare.ViewModels;

public sealed class UsbTreeItemViewModel : BindableBase
{
    private bool _isEnabled;
    private bool _isInherited;

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
    /// 是否已启用分享。
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(StatusGlyph));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(HasStatus));
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
            }
        }
    }

    /// <summary>
    /// 是否有状态显示（启用或继承）。
    /// </summary>
    public bool HasStatus => IsEnabled || IsInherited;

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

    public bool IsDimmed => !IsHub && !IsShareable;
    public double TitleOpacity => IsDimmed ? 0.55 : 1.0;
}
