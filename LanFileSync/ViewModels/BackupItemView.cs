using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LanFileSync.ViewModels;

/// <summary>「小工具」预览列表中的一行。</summary>
public sealed class BackupItemView : INotifyPropertyChanged
{
    /// <summary>是否为「删除日期命名」页的重命名保留项（此类行不可勾选删除）。</summary>
    public bool IsRename { get; init; }

    private bool _isChecked;
    /// <summary>勾选即表示要删除；旧项默认自动勾选，可手动调整。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Action));
        }
    }

    /// <summary>操作列文案：勾选为「删除」、未勾选为「保留」，重命名项固定「重命名」。</summary>
    public string Action => IsRename ? "重命名" : IsChecked ? "删除" : "保留";

    /// <summary>复选框是否可勾选（重命名保留项不可删除，禁用勾选）。</summary>
    public bool CanCheck => !IsRename;

    public string Name { get; init; } = "";
    public string NewName { get; init; } = "";
    public string LastWriteText { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public bool IsDir { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
