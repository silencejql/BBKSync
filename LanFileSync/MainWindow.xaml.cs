using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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
        // CheckBox 仅双击勾选：阻止单击 toggle，双击时主动勾选
        EventManager.RegisterClassHandler(typeof(CheckBox),
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((s, e) =>
            {
                if (s is CheckBox cb)
                {
                    if (e.ClickCount >= 2)
                        cb.IsChecked = !cb.IsChecked;
                    e.Handled = true; // 阻止默认 toggle(单击不勾选)
                }
            }));
        _startMinimized = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        InitializeComponent();
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        string verStr = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "";
        Title = $"BBK文件同步与备份工具 {verStr}";
        txtTitle.Text = $"BBKFileSync {verStr}";
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
        FreeFormKiller.ProcessPrefix = _settings.Settings.Process.ProcessPrefix;
        cboProcessPath.Text = _settings.Settings.Process.ProcessPath;
        cboProcessKillNames.Text = _settings.Settings.Process.ProcessKillNames;
        cboProcessFileSuffixes.Text = _settings.Settings.Process.ProcessFileSuffixes;
        chkAutoStart.IsChecked = _settings.Settings.Server.AutoStartAndListen;
        rbReceive.IsChecked = true;
        RbBackupSource_Changed(null, null!);
    }

    private void ReloadHistoryCombo()
    {
        string host = cboPeerIp.Text;
        cboPeerIp.ItemsSource = null;
        var items = new List<IpComboItem>();
        foreach (var e in _history.Entries)
        {
            string name = ComputerNameFor(e.Host);
            string display = name != e.Host ? $"{name}({e.Host})" : e.Host;
            if (!string.IsNullOrEmpty(e.Note)) display += $" [{e.Note}]";
            items.Add(new IpComboItem { Ip = e.Host, Label = display });
        }
        foreach (var c in _settings.Settings.Computers)
        {
            if (string.IsNullOrWhiteSpace(c.Ip)) continue;
            string ip = c.Ip.Trim();
            if (items.Any(i => string.Equals(i.Ip, ip, StringComparison.OrdinalIgnoreCase))) continue;
            string display = !string.IsNullOrWhiteSpace(c.Name) ? $"{c.Name}({ip})" : ip;
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
        string basePath = string.IsNullOrEmpty(line) ? dest : Path.Combine(dest, "Line" + FileHelper.SanitizeName(line));
        return Path.Combine(basePath, $"BBK_{FileHelper.SanitizeName(name)}_{DateTime.Now:yyyyMMdd}");
    }

    private string AutoDetectedName() => DeviceConfig.ReadFromRoot(txtRoot.Text.Trim()).Name;
    private string AutoDetectedLine() => DeviceConfig.ReadFromRoot(txtRoot.Text.Trim()).Line;

    private static Brush s_logBrush = new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2E));
    private static Brush s_logErrorBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    private void LogLine(string msg) => LogLine(msg, s_logBrush);
    private void LogLineError(string msg) => LogLine(msg, s_logErrorBrush);
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
            if (showErrors) DarkMessageBox.Show("端口无效，请输入 1~65535 之间的数字。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            LogLine($"服务已启动，端口 {port}，根目录 {root}。远端填上此 IP 与本端口即可同步。");
        }
        catch (Exception ex)
        {
            if (showErrors) DarkMessageBox.Show("启动服务失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
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
        List<string> hosts;
        try { hosts = IpHelper.ExpandIps(cboPeerIp.Text ?? ""); }
        catch (FormatException ex) { DarkMessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (hosts.Count == 0) { DarkMessageBox.Show("请输入远端 IP 或范围。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!TryGetPeerPort(out int port)) { DarkMessageBox.Show("端口无效，请输入 1~65535 之间的数字。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        btnTestConnect.IsEnabled = false;
        var okList = new List<string>();
        var failList = new List<string>();
        try
        {
            LogDivider();
            LogLine($"开始测试 {hosts.Count} 个远端地址（端口 {port}）...");
            foreach (string host in hosts)
            {
                (bool ok, string detail) = await TestOneHostAsync(host, port);
                if (ok)
                {
                    okList.Add(host);
                    LogLine($"[成功] {host}:{port} {detail}");
                }
                else
                {
                    failList.Add(host);
                    LogLineError($"[失败] {host}:{port} {detail}");
                }
            }
            LogLine($"测试完成：成功 {okList.Count} 个，失败 {failList.Count} 个。");
        }
        finally
        {
            btnTestConnect.IsEnabled = true;
        }

        string summary = failList.Count == 0
            ? $"全部 {okList.Count} 个地址连接正常，远端协议应答正确。"
            : $"成功 {okList.Count} 个，失败 {failList.Count} 个：\n" + string.Join("\n", failList.Select(h => h + ":" + port));
        DarkMessageBox.Show(summary, "测试结果", MessageBoxButton.OK,
            failList.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    /// <summary>测试单个远端地址：先测 TCP 连通性(1 秒)，再校验协议应答(1 秒)。返回是否成功及详情。</summary>
    private static async Task<(bool Ok, string Detail)> TestOneHostAsync(string host, int port)
    {
        using (var tcp = new TcpClient())
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await tcp.ConnectAsync(host, port, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return (false, "连接超时（1 秒内未建立连接：远端不可达，或防火墙静默丢弃）");
            }
            catch (SocketException ex)
            {
                string hint = ex.SocketErrorCode switch
                {
                    SocketError.AccessDenied => "10013 权限访问：本机安全软件/防火墙拦截，建议改用纯英文目录运行",
                    SocketError.ConnectionRefused => "10061 积极拒绝：远端端口未监听，服务没启动",
                    SocketError.TimedOut => "10060 超时：远端不可达，或防火墙静默丢弃",
                    _ => "错误码 " + (int)ex.SocketErrorCode,
                };
                return (false, $"TCP 连接失败：{ex.Message}（{hint}）");
            }
        }

        try
        {
            using var client = new PeerClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(host, port, Constants.RoleProbe, cts.Token);
            var (name, line) = await client.ProbeDeviceAsync(cts.Token);
            string info = string.IsNullOrWhiteSpace(name) ? "" : $"[设备 {name}{(string.IsNullOrWhiteSpace(line) ? "" : "/Line" + line)}]";
            return (true, "远端协议应答正确 " + info);
        }
        catch (OperationCanceledException)
        {
            return (false, "已连上但远端 1 秒内无协议应答：服务僵死、重复实例占用端口，或远端不是本程序");
        }
        catch (IOException ex)
        {
            return (false, $"已连上但连接被远端立刻关闭：{ex.Message}（服务未就绪或端口被其他程序占用）");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
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
    private SyncOptions ReadOptions() => new() { FullReplaceBin = cbBinReplace.IsChecked == true };
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
            ResetPasswordFields(pwdUpdate, txtUpdatePwd, eyeLine);
            txtUpdateError.Visibility = Visibility.Collapsed;
        }
        if (tabs.SelectedIndex != 3)
        {
            processOverlay.Visibility = Visibility.Visible;
            processContent.IsEnabled = false;
            ResetPasswordFields(pwdProcess, txtProcessPwd, eyeLineProcess);
            txtProcessError.Visibility = Visibility.Collapsed;
        }
        RefreshOpButtons();
    }

    /// <summary>清空密码/明文内容并恢复到密码框可见状态。</summary>
    private static void ResetPasswordFields(PasswordBox pwd, TextBox txt, System.Windows.Shapes.Line eyeLine)
    {
        pwd.Clear();
        txt.Clear();
        pwd.Visibility = Visibility.Visible;
        txt.Visibility = Visibility.Collapsed;
        eyeLine.Visibility = Visibility.Visible;
    }
    private void RefreshOpButtons()
    {
        if (btnSync == null || btnBackup == null || btnUpdateProgram == null || tabs == null) return;
        bool isBackup = tabs.SelectedIndex == 0;
        bool isBbkUpdate = tabs.SelectedIndex == 1;
        bool isFileTransfer = tabs.SelectedIndex == 2;
        bool isProcessMgmt = tabs.SelectedIndex == 3;
        bool isUpdateProgram = tabs.SelectedIndex == 4;
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
        "rbSyncShare","chkAutoStart","txtTransferPath","cbTransferSameSkip","cbTransferUpdate","rbTransferPush","rbTransferPull",
        "cbRunPreBackupBat","cbBinReplace","cbUpdateCompressZip",
        "cboProcessPath","cboProcessKillNames","cboProcessFileSuffixes","btnProcessOpen","btnProcessClose","btnProcessFetchFiles",
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

    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e) => ThemeManager.Toggle();

    internal void UpdateTheme(bool dark)
    {
        btnThemeToggle.ToolTip = dark ? "切换为亮色主题" : "切换为暗色主题";

        var icon = btnThemeToggle.Template.FindName("icon", btnThemeToggle) as System.Windows.Controls.TextBlock;
        if (icon != null) icon.Text = dark ? "☾" : "☀";

        s_logBrush = new SolidColorBrush(dark ? Color.FromRgb(0xE6, 0xED, 0xF3) : Color.FromRgb(0x20, 0x24, 0x2E));
        s_logErrorBrush = new SolidColorBrush(dark ? Color.FromRgb(0xFF, 0x6B, 0x6B) : Color.FromRgb(0xDC, 0x26, 0x26));
    }

    private void PwdUpdate_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnUpdateUnlock_Click(sender, e);
    }

    /// <summary>
    /// 切换密码框/明文框可见性并同步内容。txt.Visibility 为 Visible 时切回密码框，否则切到明文框。
    /// </summary>
    private static void TogglePasswordVisibility(PasswordBox pwd, TextBox txt, System.Windows.Shapes.Line eyeLine)
    {
        if (txt.Visibility == Visibility.Visible)
        {
            pwd.Password = txt.Text;
            pwd.Visibility = Visibility.Visible;
            txt.Visibility = Visibility.Collapsed;
            eyeLine.Visibility = Visibility.Visible;
            pwd.Focus();
        }
        else
        {
            txt.Text = pwd.Password;
            txt.Visibility = Visibility.Visible;
            pwd.Visibility = Visibility.Collapsed;
            eyeLine.Visibility = Visibility.Collapsed;
            txt.Focus();
            txt.CaretIndex = txt.Text.Length;
        }
    }

    private void BtnTogglePwd_Click(object sender, RoutedEventArgs e)
        => TogglePasswordVisibility(pwdUpdate, txtUpdatePwd, eyeLine);

    private void BtnUpdateUnlock_Click(object sender, RoutedEventArgs e)
    {
        string stored = _settings.Settings.UpdatePassword;
        if (string.IsNullOrEmpty(stored))
        {
            updateOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        string input = txtUpdatePwd.Visibility == Visibility.Visible ? txtUpdatePwd.Text : pwdUpdate.Password;
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
        UpdateTheme(ThemeManager.IsDark);
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
        _settings.Settings.Process.ProcessPath = cboProcessPath.Text.Trim();
        _settings.Settings.Process.ProcessKillNames = cboProcessKillNames.Text.Trim();
        _settings.Settings.Process.ProcessFileSuffixes = cboProcessFileSuffixes.Text.Trim();
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

    #region 进程管理
    private DateTime _processOpenCooldownUntil = DateTime.MinValue;

    private void BtnProcessBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要启动的程序",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            cboProcessPath.Text = dlg.FileName;
    }

    private async void BtnProcessFetchFiles_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetHostsAndPort(out var hosts, out int port)) return;

        string savedText = cboProcessPath.Text;
        cboProcessPath.ItemsSource = null;
            txtProcessStatus.Text = "正在获取文件列表...";
        StartBusy();
        var ct = _coordinator.Token; _opLabel = "获取文件列表";
        var okIps = new List<string>(); var failed = new List<string>();
        var allPaths = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RoleFileList, ct);
                    LogDivider(); LogLine($"连接 {host}:{port}，获取远端文件列表...");
                    var paths = await client.FileListAsync(cboProcessFileSuffixes.Text, ct);
                    allPaths.AddRange(paths);
                    okIps.Add(host);
                    txtProcessStatus.Text = $"{host}: 找到 {paths.Count} 个文件";
                    LogLine($"找到 {paths.Count} 个文件");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed.Add($"{host} - {ex.Message}"); LogLineError($"获取列表失败 {host}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { LogLine("已取消"); }
        finally { EndBusy(); }

        if (allPaths.Count > 0)
        {
            var distinct = allPaths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            cboProcessPath.ItemsSource = distinct;
            cboProcessPath.Text = savedText;
            cboProcessPath.IsDropDownOpen = true;
            txtProcessStatus.Text = $"共 {distinct.Count} 个文件，已在下拉列表中供选择";
            LogLine($"文件列表完成: {distinct.Count} 个文件");
        }
        else
        {
            cboProcessPath.ItemsSource = null;
            cboProcessPath.Text = savedText;
            txtProcessStatus.Text = "未找到匹配文件";
            LogLine("未找到匹配文件");
        }
        if (failed.Count > 0)
            DarkMessageBox.Show("以下电脑获取失败：\n" + string.Join("\n", failed), "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void BtnProcessClose_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetHostsAndPort(out var hosts, out int port)) return;
        string deviceInfo = hosts.Count == 1 ? await GetDeviceInfo(hosts[0]) : "";
        if (DarkMessageBox.Show("确认关闭指定电脑的应用程序？\n\n[" + cboPeerIp.Text + "]" + deviceInfo + "\n\n程序前缀名：" + cboProcessKillNames.Text + "。", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        string[] killNames = cboProcessKillNames.Text
            .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToArray();
        StartBusy();
        var ct = _coordinator.Token; _opLabel = "关闭FreeForm";
        var okIps = new List<string>(); var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RoleProcessClose, ct);
                    LogDivider(); LogLine($"连接 {host}:{port}，关闭远端进程...");
                    var (msg, remaining) = await client.ProcessCloseAsync(killNames, ct);
                    okIps.Add(host); LogLine($"{host}: {msg}");
                    txtProcessStatus.Text = $"[完成] {host}: {msg}";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed.Add($"{host} - {ex.Message}"); LogLineError($"失败 {host}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { LogLine("已取消"); }
        finally
        {
            if (okIps.Count > 0) SaveIpHistory(port);
            EndBusy();
        }
        if (okIps.Count > 0) txtStatus.Text = hosts.Count > 1 ? $"关闭完成({okIps.Count}/{hosts.Count} 台)" : "关闭完成";
        if (failed.Count > 0) DarkMessageBox.Show("以下电脑失败：\n" + string.Join("\n", failed), "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>探测远端设备名称和线路(失败时返回 host 作为名称)。</summary>
    private async Task<(string name, string line)> ProbeRemoteAsync(string host, int port)
    {
        try
        {
            var client = new PeerClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(host, port, Constants.RoleProbe, cts.Token);
            var (name, line) = await client.ProbeDeviceAsync(cts.Token);
            client.Dispose();
            return (string.IsNullOrWhiteSpace(name) ? host : name, line ?? "");
        }
        catch { return (host, ""); }
    }

    private async Task<string> GetDeviceInfo(string host)
    {
        if (!TryGetPeerPort(out int port)) { DarkMessageBox.Show("远端端口无效。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return ""; }
        var (name, line) = await ProbeRemoteAsync(host, port);
        return string.IsNullOrWhiteSpace(name) || name == host ? "" : $"[设备 {name}{(string.IsNullOrWhiteSpace(line) ? "" : "/Line" + line)}]";
    }

    private async void BtnProcessOpen_Click(object sender, RoutedEventArgs e)
    {
        if (DateTime.Now < _processOpenCooldownUntil)
        {
            int sec = (int)(_processOpenCooldownUntil - DateTime.Now).TotalSeconds + 1;
            DarkMessageBox.Show($"请等待 {sec} 秒后再试。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        string exePath = cboProcessPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(exePath)) { DarkMessageBox.Show("请输入程序路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!TryGetHostsAndPort(out var hosts, out int port)) return;
        string deviceInfo = hosts.Count == 1 ? await GetDeviceInfo(hosts[0]) : "";
        if (DarkMessageBox.Show("确认启动指定电脑的应用程序？\n\n[" + cboPeerIp.Text + "]" + deviceInfo + "\n\n程序路径：" + exePath + "。", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _processOpenCooldownUntil = DateTime.Now.AddSeconds(5);
        StartBusy();
        var ct = _coordinator.Token; _opLabel = "启动FreeForm";
        var okIps = new List<string>(); var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RoleProcessOpen, ct);
                    LogDivider(); LogLine($"连接 {host}:{port}[{deviceInfo}]，启动远端应用程序...");
                    var (msg, running) = await client.ProcessOpenAsync(exePath, ct);
                    okIps.Add(host); LogLine($"{host}: {msg}");
                    txtProcessStatus.Text = $"[完成] {host}: {msg}";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed.Add($"{host} - {ex.Message}"); LogLineError($"失败 {host}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { LogLine("已取消"); }
        finally
        {
            if (okIps.Count > 0) SaveIpHistory(port);
            EndBusy();
        }
        if (okIps.Count > 0) txtStatus.Text = hosts.Count > 1 ? $"启动完成({okIps.Count}/{hosts.Count} 台)" : "启动完成";
        if (failed.Count > 0) DarkMessageBox.Show("以下电脑失败：\n" + string.Join("\n", failed), "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void PwdProcess_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { BtnProcessUnlock_Click(sender, e); e.Handled = true; }
    }

    private void BtnToggleProcessPwd_Click(object sender, RoutedEventArgs e)
        => TogglePasswordVisibility(pwdProcess, txtProcessPwd, eyeLineProcess);

    private void BtnProcessUnlock_Click(object sender, RoutedEventArgs e)
    {
        string pwd = pwdProcess.Visibility == Visibility.Visible ? pwdProcess.Password : txtProcessPwd.Text;
        if (pwd == _settings.Settings.UpdatePassword)
        {
            processOverlay.Visibility = Visibility.Collapsed;
            processContent.IsEnabled = true;
        }
        else
        {
            txtProcessError.Visibility = Visibility.Visible;
        }
    }
    #endregion

    private static List<string> GetLocalIPs()
    {
        var list = HostHelper.GetLocalIPs();
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
