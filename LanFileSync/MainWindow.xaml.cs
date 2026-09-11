using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace LanFileSync;

public partial class MainWindow : Window
{
    private PeerServer? _server;
    private readonly HistoryStore _history = new();
    private readonly SettingsStore _settings = new();
    private readonly OperationCoordinator _coordinator = new();
    private TrayIcon? _tray;
    private bool _allowExit;
    private int _doneCount;
    private int _totalCount;
    private string _opLabel = "更新";
    private readonly bool _startMinimized;

    public MainWindow()
    {
        _startMinimized = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        InitializeComponent();
        if (_startMinimized) { ShowInTaskbar = false; WindowState = WindowState.Minimized; }
        Icon = AppIcons.WindowIcon() ?? Icon;
        BindSettingsToControls();
        ReloadHistoryCombo();
        EnsureUpdateBat();
        EnsurePostBackupBat();
        var localIp = GetLocalIPs().FirstOrDefault(ip => !ip.StartsWith("127.", StringComparison.Ordinal)) ?? "";
        if (!string.IsNullOrEmpty(localIp)) cboPeerIp.Text = localIp;
        LogLine("工具已启动，使用目录: " + txtRoot.Text);
    }

    private static void EnsureUpdateBat()
    {
        string batPath = Path.Combine(AppPaths.ExeDir(), "Update_BBKSync.bat");
        if (File.Exists(batPath))
        {
            string existing = File.ReadAllText(batPath);
            if (!string.IsNullOrWhiteSpace(existing)) return;
        }
        string exeDir = AppPaths.ExeDir();
        string exeName = Path.GetFileName(Environment.ProcessPath ?? "BBKSync.exe");
        string content =
            "@echo off\r\n" +
            "chcp 65001 >nul 2>&1\r\n" +
            "cd /d \"" + exeDir + "\"\r\n" +
            "if not exist BBKSync_New.exe exit\r\n" +
            "echo 等待关闭 " + exeName + " ...\r\n" +
            "timeout /t 3 /nobreak >nul\r\n" +
            "taskkill /f /im " + exeName + " >nul 2>&1\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            "del /f /q \"" + exeName + "\" >nul 2>&1\r\n" +
            "ren BBKSync_New.exe " + exeName + "\r\n" +
            "start \" \" \"" + exeDir + "\\" + exeName + "\" --minimized\r\n";
        File.WriteAllText(batPath, content);
    }

    private static void EnsurePostBackupBat()
    {
        string batPath = Path.Combine(AppPaths.ExeDir(), "PostgreSQL_Backup.bat");
        if (File.Exists(batPath))
        {
            string existing = File.ReadAllText(batPath);
            if (!string.IsNullOrWhiteSpace(existing)) return;
        }
        string content =
            "@echo off\r\n" +
            "set DBName=LocalDB\r\n" +
            "set FileName=%DBName%_AutoBackup_%date:~0,4%%date:~5,2%%date:~8,2%.backup\r\n" +
            "set BACKUP_DIR=D:\\BBK\\DataBase\r\n" +
            "if not exist \"D:\\BBK\\DataBase\" (md D:\\BBK\\DataBase)\r\n" +
            "C:/\"Program Files (x86)\"/PostgreSQL/9.5/bin/pg_dump.exe --host localhost --port 5432 --username \"postgres\" --no-password --format custom --verbose --file \"%BACKUP_DIR%\\%FileName%\" \"%DBName%\"";
        File.WriteAllText(batPath, content);
    }

    private void BindSettingsToControls()
    {
        txtRoot.Text = _settings.Settings.Server.Root;
        txtPort.Text = _settings.Settings.Server.Port.ToString();
        txtPeerPort.Text = _settings.Settings.Server.Port.ToString();
        txtBackupDest.Text = _settings.Settings.Backup.Dest;
        cbBackupBeforeSync.IsChecked = _settings.Settings.Backup.BeforeSync;
        cbBackupLogRule.IsChecked = _settings.Settings.Backup.LogRule;
        txtBackupLogDays.Text = _settings.Settings.Backup.LogDays.ToString();
        txtBackupIgnore.Text = _settings.Settings.Backup.IgnoreRegexes;
        cbIgnoreEnabled.IsChecked = _settings.Settings.Backup.IgnoreRegexEnabled;
        cbCompressZip.IsChecked = _settings.Settings.Backup.CompressZip;
        cbUpdateCompressZip.IsChecked = _settings.Settings.Backup.CompressUpdateZip;
        cbRunPreBackupBat.IsChecked = _settings.Settings.Backup.RunPreBackupBat;
        cbAutoFetchName.IsChecked = _settings.Settings.Backup.AutoFetchComputerName;
        txtTransferPath.Text = _settings.Settings.Transfer.Path;
        cbTransferSameSkip.IsChecked = _settings.Settings.Transfer.SameSkip;
        cbTransferKillFreeForm.IsChecked = _settings.Settings.Transfer.KillFreeForm;
        FreeFormKiller.ProcessPrefix = _settings.Settings.FreeForm.ProcessPrefix;
        UpdateFreeFormTooltips();
        chkAutoStart.IsChecked = _settings.Settings.Server.AutoStartAndListen;
        rbReceive.IsChecked = true;
        RbBackupSource_Changed(null, null!);
    }

    private void UpdateFreeFormTooltips()
    {
        string asterisk = FreeFormKiller.ProcessPrefixAsterisk;
        cbKillFreeForm.Content = "替换出错时结束 " + asterisk + " 后重试";
        cbTransferKillFreeForm.Content = "出错时结束 " + asterisk + " 后重试一次";
        cbKillFreeForm.ToolTip = "相当于自动打开任务管理器结束 " + asterisk + " 开头的进程，最多重试3次。";
        cbTransferKillFreeForm.ToolTip = "更新报错时输出日志，并关闭目标电脑 " + asterisk + " 开头的进程后重试一次；仍失败则输出日志跳过。";
    }

    private void ReloadHistoryCombo()
    {
        string host = cboPeerIp.Text;
        cboPeerIp.ItemsSource = null;
        var items = new List<IpComboItem>();
        foreach (var e in _history.Entries)
        {
            string name = ComputerNameFor(e.Host);
            string display = name != e.Host ? $"{name}（{e.Host}）" : e.Host;
            if (!string.IsNullOrEmpty(e.Note)) display += $" [{e.Note}]";
            items.Add(new IpComboItem { Ip = e.Host, Label = display });
        }
        foreach (var c in _settings.Settings.Computers)
        {
            if (string.IsNullOrWhiteSpace(c.Ip)) continue;
            string ip = c.Ip.Trim();
            if (items.Any(i => string.Equals(i.Ip, ip, StringComparison.OrdinalIgnoreCase))) continue;
            string display = !string.IsNullOrWhiteSpace(c.Name) ? $"{c.Name}（{ip}）" : ip;
            items.Add(new IpComboItem { Ip = ip, Label = display });
        }
        cboPeerIp.ItemsSource = items; cboPeerIp.Text = host;
        if (cboPeerIp.Items.Count > 0 && string.IsNullOrWhiteSpace(cboPeerIp.Text)) cboPeerIp.SelectedIndex = 0;
    }

    private bool TryGetPeerPort(out int port)
        => int.TryParse(txtPeerPort.Text, out port) && port >= 1 && port <= 65535;

    private string ComputerNameFor(string ip)
    {
        string norm = (ip ?? "").Trim();
        foreach (var c in _settings.Settings.Computers)
            if (string.Equals((c.Ip ?? "").Trim(), norm, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(c.Name) ? norm : c.Name.Trim();
        return norm;
    }

    private static string BackupFolderName(string machineName, string tag = "备份")
        => $"BBK_{tag}_{DateTime.Now:yyyyMMdd}";

    private string BackupTargetPath(string dest, string name, string line = "")
    {
        if (string.IsNullOrWhiteSpace(line)) line = AutoDetectedLine();
        string basePath = string.IsNullOrEmpty(line) ? dest : Path.Combine(dest, "Line" + SanitizeName(line));
        return Path.Combine(basePath, $"BBK_{SanitizeName(name)}_{DateTime.Now:yyyyMMdd}");
    }

    private string AutoDetectedName() => DeviceConfig.ReadFromRoot(txtRoot.Text.Trim()).Name;
    private string AutoDetectedLine() => DeviceConfig.ReadFromRoot(txtRoot.Text.Trim()).Line;
    private static string SanitizeName(string s)
    {
        char[] invalids = Path.GetInvalidFileNameChars();
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++) chars[i] = Array.IndexOf(invalids, s[i]) >= 0 ? '_' : s[i];
        return new string(chars);
    }

    private void LogLine(string msg) => LogLine(msg, Brushes.Black);
    private void LogLineError(string msg) => LogLine(msg, Brushes.Red);
    private void LogDivider() => LogLine("------------------------------------------------");

    private void LogLine(string msg, Brush brush)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Dispatcher.Invoke(() =>
        {
            if (txtLog == null) return;
            var para = new Paragraph(new Run(line) { Foreground = brush }) { Margin = new Thickness(0) };
            txtLog.Document.Blocks.Add(para);
            if (txtLog.Document.Blocks.Count > Constants.MaxLogEntries)
                txtLog.Document.Blocks.Remove(txtLog.Document.Blocks.FirstBlock);
            txtLog.ScrollToEnd();
        });
    }

    private static string FormatSize(long bytes)
    {
        const long K = 1024, M = 1024 * K, G = 1024 * M;
        if (bytes >= G) return $"{bytes / (double)G:F2} GB";
        if (bytes >= M) return $"{bytes / (double)M:F2} MB";
        if (bytes >= K) return $"{bytes / (double)K:F1} KB";
        return $"{bytes} B";
    }

    private void OnTotal(int total)
    {
        Dispatcher.Invoke(() => { _doneCount = 0; _totalCount = total; progressBar.Maximum = Math.Max(1, total); progressBar.Value = 0; if (total == 0) { progressBar.Value = 1; txtCur.Text = $"无需{_opLabel}文件"; } });
    }
    private void OnFileProgress(string rel, long size)
    {
        Dispatcher.Invoke(() => { _doneCount++; progressBar.Value = _doneCount; txtCur.Text = $"文件 {_doneCount}/{_totalCount}: {rel} ({FormatSize(size)})"; });
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择文件夹" };
        if (dlg.ShowDialog() == true) txtRoot.Text = dlg.FolderName;
    }
    private void BtnBackupBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择备份目标文件夹" };
        if (dlg.ShowDialog() == true) txtBackupDest.Text = dlg.FolderName;
    }
    private void BtnTransferFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择要传输的文件", CheckFileExists = true, Multiselect = false };
        if (dlg.ShowDialog() == true) txtTransferPath.Text = dlg.FileName;
    }
    private void BtnTransferDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要传输的文件夹" };
        if (dlg.ShowDialog() == true) txtTransferPath.Text = dlg.FolderName;
    }
    private void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        new HistoryWindow(_history) { Owner = this }.ShowDialog(); ReloadHistoryCombo();
    }
    private void BtnStartServer_Click(object sender, RoutedEventArgs e) => StartServer(showErrors: true);
    private void BtnStopServer_Click(object sender, RoutedEventArgs e) => StopServer();

    private void StartServer(bool showErrors)
    {
        if (_server != null) return;
        if (!int.TryParse(txtPort.Text, out int port) || port < 1 || port > 65535)
        {
            if (showErrors) MessageBox.Show("端口无效，请输入 1~65535 之间的数字。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string root = txtRoot.Text.Trim();
        var options = ReadOptions();
        _server = new PeerServer(root, port, options, LogLine, OnFileProgress, OnTotal, m => LogLineError("执行失败: " + m),
            cbBackupBeforeSync.IsChecked ?? true, txtBackupDest.Text.Trim(), ReadBackupOptions(),
            _settings.Settings.Backup.CompressUpdateZip);
        try
        {
            _server.Start();
            btnStart.IsEnabled = false; btnStop.IsEnabled = true;
            txtLocalIp.Text = "本机地址: " + string.Join("   ", GetLocalIPs());
            LogLine($"服务已启动，端口 {port}，根目录 {root}。对方填上此 IP 与本端口即可同步。");
        }
        catch (Exception ex)
        {
            if (showErrors) MessageBox.Show("启动服务失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            else LogLineError("启动服务失败: " + ex.Message);
            _server?.Dispose(); _server = null;
        }
    }

    private void StopServer()
    {
        _server?.Stop(); _server = null;
        btnStart.IsEnabled = true; btnStop.IsEnabled = false;
        txtLocalIp.Text = "服务已停止"; LogLine("服务已停止");
    }

    private void BtnComputers_Click(object sender, RoutedEventArgs e)
    {
        new ComputerNameWindow(_settings) { Owner = this }.ShowDialog(); ReloadHistoryCombo();
    }

    private async void BtnTestConnect_Click(object sender, RoutedEventArgs e)
    {
        string host = cboPeerIp.Text.Trim();
        if (string.IsNullOrEmpty(host)) { MessageBox.Show("请输入对方 IP 地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!TryGetPeerPort(out int port)) { MessageBox.Show("端口无效，请输入 1~65535 之间的数字。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        using var tcp = new TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(host, port, cts.Token);
            LogLine($"已连上 {host}:{port}，正在校验对方协议应答...");
        }
        catch (OperationCanceledException)
        {
            var msg = $"连接超时：{host}:{port}\n4 秒内未建立连接（10060 超时：对方不可达，或防火墙静默丢弃）。";
            LogLineError(msg); MessageBox.Show(msg, "测试结果", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        catch (SocketException ex)
        {
            string hint = ex.SocketErrorCode switch
            {
                SocketError.AccessDenied => "（10013 权限访问：本机安全软件/防火墙拦截本程序外发连接；若程序放在桌面/深层局部目录运行，请改用纯英文目录如 C:\\BBKApp 再试）",
                SocketError.ConnectionRefused => "（10061 积极拒绝：对方端口未监听，服务没启动）",
                SocketError.TimedOut => "（10060 超时：对方不可达，或防火墙静默丢弃）",
                _ => $"（错误码 {(int)ex.SocketErrorCode}）",
            };
            var msg = $"连接失败：{host}:{port}\n{ex.Message} {hint}";
            LogLineError(msg); MessageBox.Show(msg, "测试结果", MessageBoxButton.OK, MessageBoxImage.Warning); return;
        }
        try
        {
            using var client = new PeerClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, Constants.RoleProbe, cts.Token);
            var (name, line) = await client.ProbeDeviceAsync(cts.Token);
            string info = string.IsNullOrWhiteSpace(name) ? "" : $"[设备 {name}{(string.IsNullOrWhiteSpace(line) ? "" : "/Line" + line)}]";
            LogLine($"连接正常：{host}:{port}，对方协议应答正确 {info}");
            MessageBox.Show($"连接正常：{host}:{port}，对方协议应答正确\n {info}: {host}:{port}", "测试结果", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            var msg = $"已连上 {host}:{port}，但对方 5 秒内无协议应答：多为对方 BBKSync 进程僵死、重复实例占用端口，或对方跑的不是本程序。\n\n建议到对方机器：tasklist | findstr /i BBKSync 核对实例数，必要时 taskkill /f /im BBKSync 后重启。";
            LogLineError(msg); MessageBox.Show(msg, "测试结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (IOException ex)
        {
            var msg = $"已连上 {host}:{port}，但连接被对端立刻关闭：{ex.Message}\n\n多为对方服务未就绪或端口被其他程序占用。";
            LogLineError($"已连上 {host}:{port}，但连接被对端立刻关闭：{ex.Message}"); MessageBox.Show(msg, "测试结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            var msg = $"连接失败：{host}:{port} - {ex.Message}";
            LogLineError(msg); MessageBox.Show(msg, "测试结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private BackupOptions ReadBackupOptions()
    {
        if (!int.TryParse(txtBackupLogDays.Text, out int days) || days < 1) days = 2;
        bool ignoreEnabled = cbIgnoreEnabled.IsChecked ?? true;
        string[] ignores = txtBackupIgnore.Text
            .Split(new[] { ',', '，', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToArray();
        return new BackupOptions { ApplyLogRule = cbBackupLogRule.IsChecked ?? true, LogRetentionDays = days, IgnoreRegexes = ignoreEnabled ? ignores : Array.Empty<string>() };
    }
    private SyncOptions ReadOptions() => new() { FullReplaceBin = cbBinReplace.IsChecked == true, KillFreeFormFirst = cbKillFreeForm.IsChecked ?? true };
    private bool ShouldCompress => cbCompressZip.IsChecked == true;

    private async Task CompressAndRemoveFolderAsync(string folderPath, bool compress)
    {
        if (!compress || !Directory.Exists(folderPath)) return;
        try
        {
            string zipPath = folderPath + ".zip";
            LogLine("正在压缩备份文件夹 ...");
            await Task.Run(() => { try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { } ZipHelper.CompressFolder(folderPath, zipPath); Directory.Delete(folderPath, recursive: true); });
            LogLine("已压缩为 " + Path.GetFileName(zipPath));
        }
        catch (Exception ex) { LogLineError("压缩失败: " + ex.Message); }
    }

    private async Task<bool> RunPreBackupBatAsync()
    {
        if (cbRunPreBackupBat.IsChecked != true) return true;
        string batPath = Path.Combine(AppPaths.ExeDir(), "PostgreSQL_Backup.bat");
        if (!File.Exists(batPath) || new FileInfo(batPath).Length == 0)
        { LogLineError("PostgreSQL_Backup.bat 不存在或为空，跳过备份前脚本"); return true; }
        string script = File.ReadAllText(batPath);
        LogDivider(); LogLine("开始执行备份前脚本 ...");
        try { string? err = await Task.Run(() => ScriptRunner.Run(script, AppPaths.ExeDir(), LogLine)); if (err != null) { LogLineError(err + "，继续备份"); } else { LogLine("备份前脚本执行完成"); } }
        catch (Exception ex) { LogLineError("执行备份前脚本异常，继续备份: " + ex.Message); }
        return true;
    }

    private string BackupNameFor(string fallback)
    {
        if (cbAutoFetchName.IsChecked == true) { string auto = AutoDetectedName(); if (!string.IsNullOrWhiteSpace(auto)) return auto; LogLineError("自动获取电脑名称失败：未在 BBK 目录中找到包含 DeviceNo 的 SystemConfig.xml，使用默认名称。"); }
        return fallback;
    }

    private void StartBusy() { _coordinator.StartBusy(); _doneCount = 0; _totalCount = 0; SetBusy(true); }
    private void EndBusy() { _coordinator.EndBusy(); SetBusy(false); txtStatus.Text = "就绪"; }
    private void BtnCancel_Click(object sender, RoutedEventArgs e) => _coordinator.Cancel();
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (tabs.SelectedIndex != 1)
        {
            updateOverlay.Visibility = Visibility.Visible;
            pwdUpdate.Clear();
            txtUpdatePwd.Clear();
            txtUpdateError.Visibility = Visibility.Collapsed;
            _pwdVisible = false;
            pwdUpdate.Visibility = Visibility.Visible;
            txtUpdatePwd.Visibility = Visibility.Collapsed;
            eyeLine.Visibility = Visibility.Visible;
        }
        RefreshOpButtons();
    }
    private void RefreshOpButtons()
    {
        if (btnSync == null || btnBackup == null || btnUpdateProgram == null || tabs == null) return;
        bool isBackup = tabs.SelectedIndex == 0;
        bool isBbkUpdate = tabs.SelectedIndex == 1;
        bool isFileTransfer = tabs.SelectedIndex == 2;
        bool isUpdateProgram = tabs.SelectedIndex == 3;
        bool pwdOk = updateOverlay.Visibility != Visibility.Visible;
        btnBackup.IsEnabled = !_coordinator.Busy && isBackup;
        btnSync.IsEnabled = !_coordinator.Busy && ((isBbkUpdate && pwdOk) || isFileTransfer);
        btnUpdateProgram.IsEnabled = !_coordinator.Busy && isUpdateProgram;
    }
    private void SetBusy(bool busy)
    {
        foreach (var ctrl in BusyControls) if (FindName(ctrl) is FrameworkElement fe) fe.IsEnabled = !busy;
        btnCancel.IsEnabled = busy;
        btnStart.IsEnabled = !busy && _server == null;
        btnStop.IsEnabled = !busy && _server != null;
    }

    private static readonly string[] BusyControls =
    {
        "txtRoot","txtPort","cboPeerIp","txtPeerPort","btnHistory","btnTestConnect",
        "txtBackupDest","cbAutoFetchName","cbBackupLogRule","cbBackupBeforeSync","cbCompressZip",
        "cbIgnoreEnabled","txtBackupIgnore","txtBackupLogDays","rbBackupLocal","rbBackupRemote",
        "rbBackupShare","txtShareIp","txtSharePath","txtShareUser","pwdSharePass","btnShareTest",
        "rbSyncShare","chkAutoStart","txtTransferPath","cbTransferSameSkip","cbTransferKillFreeForm",
        "cbRunPreBackupBat","cbBinReplace","cbKillFreeForm","cbUpdateCompressZip",
    };

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) { txtLog?.Document.Blocks.Clear(); }
    private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (txtLog == null) return;
        var textRange = new TextRange(txtLog.Document.ContentStart, txtLog.Document.ContentEnd);
        if (!string.IsNullOrEmpty(textRange.Text)) { Clipboard.SetText(textRange.Text); }
    }
    private void Window_StateChanged(object sender, EventArgs e) { if (WindowState == WindowState.Minimized && _tray != null) Hide(); }

    private void TitleMin_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }

    private void TitleMax_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
        else
        {
            MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
            MaxWidth = SystemParameters.MaximizedPrimaryScreenWidth;
            WindowState = WindowState.Maximized;
        }
    }

    private void TitleClose_Click(object sender, RoutedEventArgs e) { Close(); }

    private void PwdUpdate_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnUpdateUnlock_Click(sender, e);
    }

    private bool _pwdVisible;

    private void BtnTogglePwd_Click(object sender, RoutedEventArgs e)
    {
        _pwdVisible = !_pwdVisible;
        if (_pwdVisible)
        {
            txtUpdatePwd.Text = pwdUpdate.Password;
            txtUpdatePwd.Visibility = Visibility.Visible;
            pwdUpdate.Visibility = Visibility.Collapsed;
            eyeLine.Visibility = Visibility.Collapsed;
            txtUpdatePwd.Focus();
            txtUpdatePwd.CaretIndex = txtUpdatePwd.Text.Length;
        }
        else
        {
            pwdUpdate.Password = txtUpdatePwd.Text;
            pwdUpdate.Visibility = Visibility.Visible;
            txtUpdatePwd.Visibility = Visibility.Collapsed;
            eyeLine.Visibility = Visibility.Visible;
            pwdUpdate.Focus();
        }
    }

    private void BtnUpdateUnlock_Click(object sender, RoutedEventArgs e)
    {
        string stored = _settings.Settings.UpdatePassword;
        if (string.IsNullOrEmpty(stored))
        {
            updateOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        string input = _pwdVisible ? txtUpdatePwd.Text : pwdUpdate.Password;
        if (input == stored)
        {
            updateOverlay.Visibility = Visibility.Collapsed;
            RefreshOpButtons();
        }
        else
        {
            txtUpdateError.Visibility = Visibility.Visible;
            pwdUpdate.Clear();
            pwdUpdate.Focus();
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var screen = SystemParameters.PrimaryScreenWidth > 0 ? SystemParameters.PrimaryScreenWidth : 1920;
        var screenH = SystemParameters.PrimaryScreenHeight > 0 ? SystemParameters.PrimaryScreenHeight : 1080;
        Width = Math.Min(1200, screen * 0.85); Height = Math.Min(900, screenH * 0.85);
        Left = (screen - Width) / 2; Top = (screenH - Height) / 2;
        tabs.SelectedIndex = 0; RefreshOpButtons(); StartServer(showErrors: false);
        bool minimizeToTray = _startMinimized || _settings.Settings.Server.AutoStartAndListen;
        if (minimizeToTray)
        {
            _tray = new TrayIcon(ShowMainWindow, ExitApp); _tray.Show(); Hide();
            _tray.Notify("BBK 工具已在后台运行，双击托盘图标可打开主界面，右键图标可完全退出。");
        }
    }

    private void ShowMainWindow() { Dispatcher.Invoke(() => { ShowInTaskbar = true; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }); }
    private void ExitApp() { Dispatcher.Invoke(() => { _allowExit = true; Close(); }); }

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool enabled = chkAutoStart.IsChecked == true;
        try
        {
            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "BBKSync.exe";
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key == null) throw new InvalidOperationException("无法打开注册表 Run 键");
            if (enabled) { string dir = Path.GetDirectoryName(exe) ?? "."; key.SetValue(Constants.RegistryValueName, $"cmd.exe /c cd /d \"{dir}\" && \"{exe}\" --autostart"); }
            else key.DeleteValue(Constants.RegistryValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { LogLineError("写入开机自启失败: " + ex.Message); }
        _settings.Settings.Server.AutoStartAndListen = enabled; SaveSettings();
        LogLine(enabled ? "已启用：开机自动启动并后台监听" : "已关闭开机自启");
    }

    private void SaveSettings()
    {
        _settings.Settings.Server.Root = txtRoot.Text.Trim();
        _settings.Settings.Server.Port = int.TryParse(txtPort.Text, out int p) ? p : _settings.Settings.Server.Port;
        _settings.Settings.Backup.Dest = txtBackupDest.Text.Trim();
        _settings.Settings.Backup.BeforeSync = cbBackupBeforeSync.IsChecked ?? true;
        _settings.Settings.Backup.LogRule = cbBackupLogRule.IsChecked ?? true;
        _settings.Settings.Backup.LogDays = int.TryParse(txtBackupLogDays.Text, out int d) && d >= 1 ? d : 2;
        _settings.Settings.Backup.IgnoreRegexes = txtBackupIgnore.Text;
        _settings.Settings.Backup.IgnoreRegexEnabled = cbIgnoreEnabled.IsChecked ?? true;
        _settings.Settings.Backup.CompressZip = cbCompressZip.IsChecked ?? true;
        _settings.Settings.Backup.CompressUpdateZip = cbUpdateCompressZip.IsChecked ?? true;
        _settings.Settings.Backup.RunPreBackupBat = cbRunPreBackupBat.IsChecked ?? true;
        _settings.Settings.Backup.AutoFetchComputerName = cbAutoFetchName.IsChecked == true;
        _settings.Settings.Transfer.Path = txtTransferPath.Text.Trim();
        _settings.Settings.Transfer.SameSkip = cbTransferSameSkip.IsChecked ?? true;
        _settings.Settings.Transfer.KillFreeForm = cbTransferKillFreeForm.IsChecked ?? true;
        _settings.Save();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_tray != null && !_allowExit)
        {
            e.Cancel = true; Hide();
            _tray.Notify("BBK 工具仍在后台运行：双击托盘图标打开主界面，右键图标选择“退出”可完全退出。");
            return;
        }
        _allowExit = true; SaveSettings(); _coordinator.Cancel(); StopServer(); _tray?.Dispose(); _tray = null;
    }

    private static List<string> GetLocalIPs()
    {
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses) if (addr.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(addr.Address.ToString());
        }
        if (list.Count == 0) list.Add("(未检测到局域网地址)");
        return list;
    }
}

public sealed class IpComboItem
{
    public string Ip { get; init; } = "";
    public string Label { get; init; } = "";
    public override string ToString() => Ip;
}
