using System.Windows;
using Microsoft.Win32;

namespace LanFileSync.Services;

/// <summary>基于主窗口的对话框/子窗口服务实现。</summary>
public sealed class WindowDialogService : IDialogService
{
    private readonly Window _owner;
    private readonly HistoryStore _history;
    private readonly SettingsStore _settings;

    public WindowDialogService(Window owner, HistoryStore history, SettingsStore settings)
    {
        _owner = owner;
        _history = history;
        _settings = settings;
    }

    public void Info(string message, string title = "提示")
        => DarkMessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Warning(string message, string title = "错误")
        => DarkMessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void Error(string message, string title = "错误")
        => DarkMessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title = "确认")
        => DarkMessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickFile(string title, string filter = "所有文件 (*.*)|*.*")
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void ShowHistory() => new HistoryWindow(_history) { Owner = _owner }.ShowDialog();

    public void ShowComputers() => new ComputerNameWindow(_settings) { Owner = _owner }.ShowDialog();

    public void ShowHelp() => new HelpWindow { Owner = _owner }.ShowDialog();
}
