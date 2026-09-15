using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;

namespace LanFileSync.ServerApp;

public partial class MainWindow : Window
{
    private const int MaxLogChars = 30000;

    private readonly SettingsStore _settings = new();
    private ServerHost? _host;
    private TaskbarIcon? _tray;
    private bool _realExit;

    public MainWindow()
    {
        InitializeComponent();

        ServerSelfUpdater.EnsureBat("--minimized");
        PreBackupScript.Ensure();

        LoadSettingsToControls();
        InitTray();
        ShowLocalIps();

        bool startMinimized = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (startMinimized)
        {
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
            Loaded += (_, _) => Hide();
        }

        Log("BBK 远端服务程序已启动");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (cbAutoStart.IsChecked == true)
            StartServer();
    }

    private void LoadSettingsToControls()
    {
        var s = _settings.Settings;
        txtPort.Text = s.Server.Port.ToString();
        txtRoot.Text = s.Server.Root;
        txtBackupDest.Text = s.Backup.Dest;
        cbBackupBeforeSync.IsChecked = s.Backup.BeforeSync;
        cbCompressUpdateZip.IsChecked = s.Backup.CompressUpdateZip;
        cbLogRule.IsChecked = s.Backup.LogRule;
        txtLogDays.Text = s.Backup.LogDays.ToString();
        cbIgnoreEnabled.IsChecked = s.Backup.IgnoreRegexEnabled;
        txtIgnoreRegexes.Text = s.Backup.IgnoreRegexes;
        cbAutoStart.IsChecked = s.Server.AutoStartAndListen;
    }

    private void SaveSettingsFromControls()
    {
        var s = _settings.Settings;
        if (int.TryParse(txtPort.Text.Trim(), out int port) && port is >= 1 and <= 65535)
            s.Server.Port = port;
        s.Server.Root = txtRoot.Text.Trim();
        s.Backup.Dest = txtBackupDest.Text.Trim();
        s.Backup.BeforeSync = cbBackupBeforeSync.IsChecked == true;
        s.Backup.CompressUpdateZip = cbCompressUpdateZip.IsChecked == true;
        s.Backup.LogRule = cbLogRule.IsChecked == true;
        if (int.TryParse(txtLogDays.Text.Trim(), out int days) && days >= 1)
            s.Backup.LogDays = days;
        s.Backup.IgnoreRegexEnabled = cbIgnoreEnabled.IsChecked == true;
        s.Backup.IgnoreRegexes = txtIgnoreRegexes.Text.Trim();
        s.Server.AutoStartAndListen = cbAutoStart.IsChecked == true;
        _settings.Save();
    }

    private void BtnBrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择 BBK 根目录" };
        if (dlg.ShowDialog() == true) txtRoot.Text = dlg.FolderName;
    }

    private void BtnBrowseBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择备份目标文件夹" };
        if (dlg.ShowDialog() == true) txtBackupDest.Text = dlg.FolderName;
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e) => StartServer();

    private void BtnStop_Click(object sender, RoutedEventArgs e) => StopServer();

    private void StartServer()
    {
        if (_host is { IsRunning: true }) return;

        if (!int.TryParse(txtPort.Text.Trim(), out int port) || port is < 1 or > 65535)
        {
            MessageBox.Show("端口无效，请输入 1~65535 之间的数字。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(txtRoot.Text.Trim()))
        {
            MessageBox.Show("请输入 BBK 根目录。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveSettingsFromControls();
        _host = new ServerHost(Log, msg => Log("【错误】" + msg));
        try
        {
            _host.Start(ServerRuntimeOptions.FromSettings(_settings.Settings));
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            SetControlsEditable(false);
            txtStatus.Text = $"服务运行中，端口 {port}";
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动服务失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            _host.Dispose();
            _host = null;
        }
    }

    private void StopServer()
    {
        _host?.Dispose();
        _host = null;
        btnStart.IsEnabled = true;
        btnStop.IsEnabled = false;
        SetControlsEditable(true);
        txtStatus.Text = "服务已停止";
    }

    private void SetControlsEditable(bool editable)
    {
        txtPort.IsEnabled = editable;
        txtRoot.IsEnabled = editable;
        btnBrowseRoot.IsEnabled = editable;
        txtBackupDest.IsEnabled = editable;
        btnBrowseBackup.IsEnabled = editable;
    }

    private void ShowLocalIps()
    {
        var ips = GetLocalIps();
        txtStatus.Text = ips.Count == 0
            ? "未检测到本机 IPv4 地址"
            : "本机地址：" + string.Join("    ", ips);
    }

    private static List<string> GetLocalIps()
    {
        var result = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            foreach (var ip in nic.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    result.Add(ip.Address.ToString());
            }
        }
        return result;
    }

    private void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        Dispatcher.Invoke(() =>
        {
            txtLog.AppendText(line + Environment.NewLine);
            if (txtLog.Text.Length > MaxLogChars)
                txtLog.Text = txtLog.Text[^MaxLogChars..];
            txtLog.ScrollToEnd();
        });
    }

    private void InitTray()
    {
        _tray = new TaskbarIcon
        {
            Icon = LoadTrayIcon(),
            ToolTipText = "BBK 远端服务程序",
            Visibility = Visibility.Visible,
        };
        var menu = new System.Windows.Controls.ContextMenu();
        var showItem = new System.Windows.Controls.MenuItem { Header = "显示主界面" };
        showItem.Click += (_, _) => ShowWindow();
        var exitItem = new System.Windows.Controls.MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(showItem);
        menu.Items.Add(exitItem);
        _tray.ContextMenu = menu;
        _tray.TrayMouseDoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
    }

    private void ExitApp()
    {
        _realExit = true;
        StopServer();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_realExit) return;
        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
        _tray?.ShowBalloonTip("BBK 远端服务程序",
            "程序已最小化到托盘后台运行，右键托盘图标可退出。", BalloonIcon.Info);
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var sri = Application.GetResourceStream(new Uri("app.ico", UriKind.Relative));
            if (sri != null)
            {
                using var ms = new MemoryStream();
                sri.Stream.CopyTo(ms);
                return new System.Drawing.Icon(ms);
            }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }
}
