using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace LanFileSync;

public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayIcon(Action show, Action exit)
    {
        _icon = new TaskbarIcon
        {
            Icon = AppIcons.TrayIcon(),
            ToolTipText = "BBK 同步与备份工具",
            Visibility = Visibility.Visible,
        };

        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = "打开主界面" };
        openItem.Click += (_, _) => show();
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => exit();
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);
        _icon.ContextMenu = menu;
        _icon.DoubleClickCommand = new RelayCommand(() => show());
    }

    public void Show()
    { }

    public void Notify(string text)
        => _icon.ShowBalloonTip("BBK 同步与备份工具", text, BalloonIcon.Info);

    public void Dispose()
    {
        _icon.Dispose();
    }
}

internal sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute) => _execute = execute;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}