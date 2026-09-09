using System.Windows.Forms;

namespace LanFileSync;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Action _show;
    private readonly Action _exit;

    public TrayIcon(Action show, Action exit)
    {
        _show = show;
        _exit = exit;

        _icon = new NotifyIcon
        {
            Icon = AppIcons.TrayIcon(),
            Text = "BBK 同步与备份工具",
            Visible = false,
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => _show());
        menu.Items.Add("退出", null, (_, _) => _exit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => _show();
    }

    public void Show()
        => _icon.Visible = true;

    public void Notify(string text)
        => _icon.ShowBalloonTip(3000, "BBK 同步与备份工具", text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}