using System.ComponentModel;
using System.Drawing;

namespace TrayIconManager.Models;

public class TrayIconInfo : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public Icon? Icon { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string TooltipText { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;

    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    public IntPtr HWnd { get; set; }
    public IntPtr OwnerHWnd { get; set; }
    public uint OwnerPid { get; set; }
    public int ButtonIndex { get; set; }
    public bool IsHidden { get; set; }

    public string DisplayName => !string.IsNullOrEmpty(TooltipText) ? TooltipText : ProcessName;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
