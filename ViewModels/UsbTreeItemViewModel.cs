using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using USBShare.Models;

namespace USBShare.ViewModels;

public sealed class UsbTreeItemViewModel : BindableBase
{
    private string _assignedRemoteName = "未分配";
    private string? _conflictMessage;

    public string InstanceId { get; init; } = string.Empty;
    public string? ParentInstanceId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public bool IsHub { get; init; }
    public bool IsShareable { get; init; }
    public string? BusId { get; init; }
    public RuleNodeType RuleNodeType => IsHub ? RuleNodeType.Hub : RuleNodeType.Device;
    public List<UsbTreeItemViewModel> Children { get; } = [];

    public bool CanAssign => IsHub || IsShareable;

    public string Glyph => IsHub ? "\uE7F4" : "\uEC4F";

    public string AssignedRemoteName
    {
        get => _assignedRemoteName;
        set
        {
            if (SetProperty(ref _assignedRemoteName, value))
            {
                OnPropertyChanged(nameof(AssignedSummary));
            }
        }
    }

    public string? ConflictMessage
    {
        get => _conflictMessage;
        set
        {
            if (SetProperty(ref _conflictMessage, value))
            {
                OnPropertyChanged(nameof(AssignedSummary));
                OnPropertyChanged(nameof(AssignmentBrush));
            }
        }
    }

    public string AssignedSummary => string.IsNullOrWhiteSpace(ConflictMessage) ? AssignedRemoteName : ConflictMessage!;

    public bool IsDimmed => !IsHub && !IsShareable;
    public double TitleOpacity => IsDimmed ? 0.55 : 1.0;

    public SolidColorBrush AssignmentBrush =>
        string.IsNullOrWhiteSpace(ConflictMessage)
            ? new SolidColorBrush(Colors.LightGray)
            : new SolidColorBrush(Colors.OrangeRed);
}
