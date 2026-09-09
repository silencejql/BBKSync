using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace LanFileSync;

public partial class MainWindow : Window
{
    private PeerServer? _server;
    private CancellationTokenSource? _cts;
    private readonly HistoryStore _history = new();
    private readonly SettingsStore _settings = new();
    private TrayIcon? _tray;
    private bool _allowExit;
    private bool _busy;
    private int _doneCount;
    private int _totalCount;
    private string _opLabel = "更新";

    public MainWindow()
    {
        InitializeComponent();
        Icon = AppIcons.WindowIcon() ?? Icon;
        txtRoot.Text = _settings.Settings.Root;
        txtPort.Text = _settings.Settings.Port.ToString();
        txtPeerPort.Text = _settings.Settings.Port.ToString();
        txtBackupDest.Text = _settings.Settings.BackupDest;
        cbBackupBeforeSync.IsChecked = _settings.Settings.BackupBeforeSync;
        cbBackupLogRule.IsChecked = _settings.Settings.BackupLogRule;
        txtBackupLogDays.Text = _settings.Settings.BackupLogDays.ToString();
        txtBackupIgnore.Text = _settings.Settings.BackupIgnoreRegexes;
        cbIgnoreEnabled.IsChecked = _settings.Settings.IgnoreRegexEnabled;
        cbCompressZip.IsChecked = _settings.Settings.CompressZip;
        cbRunPreBackupBat.IsChecked = _settings.Settings.RunPreBackupBat;
        cbAutoFetchName.IsChecked = _settings.Settings.AutoFetchComputerName;
        txtTransferPath.Text = _settings.Settings.TransferPath;
        cbTransferSameSkip.IsChecked = _settings.Settings.TransferSameSkip;
        cbTransferKillFreeForm.IsChecked = _settings.Settings.TransferKillFreeForm;
        FreeFormKiller.ProcessPrefix = _settings.Settings.FreeFormProcessPrefix;
        cbKillFreeForm.Content = "替换出错时结束 " + FreeFormKiller.ProcessPrefixAsterisk + " 后重试";
        cbTransferKillFreeForm.Content = "出错时结束 " + FreeFormKiller.ProcessPrefixAsterisk + " 后重试一次";
        cbKillFreeForm.ToolTip = "相当于自动打开任务管理器结束 " + FreeFormKiller.ProcessPrefixAsterisk + " 开头的进程，最多重试3次。";
        cbTransferKillFreeForm.ToolTip = "更新报错时输出日志，并关闭目标电脑 " + FreeFormKiller.ProcessPrefixAsterisk + " 开头的进程后重试一次；仍失败则输出日志跳过。";
        chkAutoStart.IsChecked = _settings.Settings.AutoStartAndListen;
        rbReceive.IsChecked = true;
        RbBackupSource_Changed(null, null!);
        ReloadHistoryCombo();
        string localIp = GetLocalIPs().FirstOrDefault(ip => !ip.StartsWith("127.", StringComparison.Ordinal)) ?? "";
        if (!string.IsNullOrEmpty(localIp))
            cboPeerIp.Text = localIp;
        LogLine("工具已启动，使用目录: " + txtRoot.Text);
    }

    private sealed class IpComboItem
    {
        public string Ip { get; init; } = "";
        public string Label { get; init; } = "";
        public override string ToString() => Ip;
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
            if (!string.IsNullOrEmpty(e.Note))
                display += $" [{e.Note}]";
            items.Add(new IpComboItem { Ip = e.Host, Label = display });
        }

        foreach (var c in _settings.Settings.Computers)
        {
            if (string.IsNullOrWhiteSpace(c.Ip))
                continue;
            string ip = c.Ip.Trim();
            if (items.Any(i => string.Equals(i.Ip, ip, StringComparison.OrdinalIgnoreCase)))
                continue;
            string display = !string.IsNullOrWhiteSpace(c.Name) ? $"{c.Name}（{ip}）" : ip;
            items.Add(new IpComboItem { Ip = ip, Label = display });
        }

        cboPeerIp.ItemsSource = items;
        cboPeerIp.Text = host;
        if (cboPeerIp.Items.Count > 0 && string.IsNullOrWhiteSpace(cboPeerIp.Text))
            cboPeerIp.SelectedIndex = 0;
    }

    private bool TryGetPeerPort(out int port)
        => int.TryParse(txtPeerPort.Text, out port) && port >= 1 && port <= 65535;

    private void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new HistoryWindow(_history) { Owner = this };
        dlg.ShowDialog();
        ReloadHistoryCombo();
    }

    private void BtnComputers_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ComputerNameWindow(_settings) { Owner = this };
        dlg.ShowDialog();
        ReloadHistoryCombo();
    }

    private async void BtnTestConnect_Click(object sender, RoutedEventArgs e)
    {
        string host = cboPeerIp.Text.Trim();
        if (string.IsNullOrEmpty(host))
        {
            MessageBox.Show("请输入对方 IP 地址。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryGetPeerPort(out int port))
        {
            MessageBox.Show("端口无效，请输入 1~65535 之间的数字。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await tcp.ConnectAsync(host, port, cts.Token);
            LogLine($"连接成功：{host}:{port}");
            MessageBox.Show($"连接成功：{host}:{port}", "测试结果", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogLineError($"连接失败：{host}:{port} - {ex.Message}");
            MessageBox.Show($"连接失败：{host}:{port} - {ex.Message}", "测试结果",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string ComputerNameFor(string ip)
    {
        string norm = (ip ?? "").Trim();
        foreach (var c in _settings.Settings.Computers)
        {
            if (string.Equals((c.Ip ?? "").Trim(), norm, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(c.Name) ? norm : c.Name.Trim();
        }
        return norm;
    }

    private static string BackupFolderName(string machineName, string tag = "备份")
        => $"BBK_{tag}_{DateTime.Now:yyyyMMdd}";

    private string BackupTargetPath(string dest, string name, string line = "")
    {
        if (string.IsNullOrWhiteSpace(line))
            line = AutoDetectedLine();
        string basePath = string.IsNullOrEmpty(line) ? dest : Path.Combine(dest, "Line" + SanitizeName(line));
        return Path.Combine(basePath, $"BBK_{SanitizeName(name)}_{DateTime.Now:yyyyMMdd}");
    }

    private string AutoDetectedName()
        => DeviceConfig.ReadFromRoot(txtRoot.Text.Trim()).Name;

    private string AutoDetectedLine()
        => DeviceConfig.ReadFromRoot(txtRoot.Text.Trim()).Line;

    private static string SanitizeName(string s)
    {
        char[] invalids = Path.GetInvalidFileNameChars();
        var chars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
            chars[i] = Array.IndexOf(invalids, s[i]) >= 0 ? '_' : s[i];
        return new string(chars);
    }

    private void LogLine(string msg) => LogLine(msg, SystemColors.ControlTextBrush);

    private void LogLineError(string msg) => LogLine(msg, Brushes.Red);

    private void LogDivider() => LogLine("----------------");

    private void LogLine(string msg, System.Windows.Media.Brush brush)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        Dispatcher.Invoke(() =>
        {
            var tb = new TextBlock { Text = line, Foreground = brush, TextWrapping = TextWrapping.Wrap };
            lbLog.Items.Add(tb);
            lbLog.ScrollIntoView(tb);
            if (lbLog.Items.Count > Constants.MaxLogEntries)
                lbLog.Items.RemoveAt(0);
        });
    }

    private SyncOptions ReadOptions()
    {
        return new SyncOptions
        {
            FullReplaceBin = cbBinReplace.IsChecked == true,
            KillFreeFormFirst = cbKillFreeForm.IsChecked ?? true,
        };
    }

    private void OnTotal(int total)
    {
        Dispatcher.Invoke(() =>
        {
            _doneCount = 0;
            _totalCount = total;
            progressBar.Maximum = Math.Max(1, total);
            progressBar.Value = 0;
            if (total == 0)
            {
                progressBar.Value = 1;
                txtCur.Text = $"无需{_opLabel}文件";
            }
        });
    }

    private void OnFileProgress(string rel, long size)
    {
        Dispatcher.Invoke(() =>
        {
            _doneCount++;
            progressBar.Value = _doneCount;
            txtCur.Text = $"文件 {_doneCount}/{_totalCount}: {rel} ({FormatSize(size)})";
        });
    }

    private void LogBackupSummary(string what, BackupEngine engine, int total)
    {
        int copied = engine.CopiedCount;
        int skipped = engine.SkippedCount;
        if (copied == 0)
            LogLine($"{what}完成：共 {total} 项，全部与备份目标相同，无需传输。");
        else
            LogLine($"{what}完成：共 {total} 项，复制 {copied} 项" + (skipped > 0 ? $"，跳过相同 {skipped} 项" : "") + "。");
    }

    private static string FormatSize(long bytes)
    {
        const long K = 1024, M = 1024 * K, G = 1024 * M;
        if (bytes >= G) return $"{bytes / (double)G:F2} GB";
        if (bytes >= M) return $"{bytes / (double)M:F2} MB";
        if (bytes >= K) return $"{bytes / (double)K:F1} KB";
        return $"{bytes} B";
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog { Description = "选择文件夹", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
            txtRoot.Text = dlg.SelectedPath;
    }

    private void BtnBackupBrowse_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog { Description = "选择备份目标文件夹", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
            txtBackupDest.Text = dlg.SelectedPath;
    }

    private void BtnTransferFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要传输的文件",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dlg.ShowDialog() == true)
            txtTransferPath.Text = dlg.FileName;
    }

    private void BtnTransferDir_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog { Description = "选择要传输的文件夹", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
            txtTransferPath.Text = dlg.SelectedPath;
    }

    private BackupOptions ReadBackupOptions()
    {
        if (!int.TryParse(txtBackupLogDays.Text, out int days) || days < 1)
            days = 2;

        bool ignoreEnabled = cbIgnoreEnabled.IsChecked ?? true;
        string[] ignores = txtBackupIgnore.Text
            .Split(new[] { ',', '，', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();

        return new BackupOptions
        {
            ApplyLogRule = cbBackupLogRule.IsChecked ?? true,
            LogRetentionDays = days,
            IgnoreRegexes = ignoreEnabled ? ignores : Array.Empty<string>(),
        };
    }

    private static bool IsBackupDestBad(string dest, string src)
    {
        string d, s;
        try
        {
            d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dest));
            s = Path.TrimEndingDirectorySeparator(Path.GetFullPath(src));
        }
        catch (Exception)
        {
            return true;
        }

        return string.Equals(d, s, StringComparison.OrdinalIgnoreCase)
               || d.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldCompress => cbCompressZip.IsChecked == true;

    private async Task CompressAndRemoveFolderAsync(string folderPath)
    {
        if (!ShouldCompress)
            return;
        try
        {
            if (!Directory.Exists(folderPath))
                return;
            string zipPath = folderPath + ".zip";
            LogLine("正在压缩备份文件夹 ...");
            await Task.Run(() =>
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                ZipFile.CreateFromDirectory(folderPath, zipPath, CompressionLevel.Optimal, false);
                Directory.Delete(folderPath, recursive: true);
            });
            LogLine("已压缩为 " + Path.GetFileName(zipPath));
        }
        catch (Exception ex)
        {
            LogLineError("压缩失败: " + ex.Message);
        }
    }

    private async Task<bool> RunBackupBeforeSyncAsync()
    {
        if (cbBackupBeforeSync.IsChecked != true)
            return true;

        string src = txtRoot.Text.Trim();
        string baseDest = txtBackupDest.Text.Trim();
        if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src))
        {
            LogLine($"本机目录 {src} 尚不存在，跳过同步前备份。");
            return true;
        }
        if (string.IsNullOrWhiteSpace(baseDest))
        {
            LogLine("备份目录为空，跳过同步前备份。");
            return true;
        }
        if (!ShouldCompress && IsBackupDestBad(baseDest, src))
        {
            LogLine($"备份目录 {baseDest} 与同步目录相同/位于其内部，跳过同步前备份。");
            return true;
        }

        string dest = Path.Combine(baseDest, BackupFolderName(SanitizeName(Environment.MachineName), "自动更新备份"));

        try
        {
            var engine = new BackupEngine(src, dest, ReadBackupOptions());
            var files = engine.Plan();
            _totalCount = files.Count;
            progressBar.Maximum = Math.Max(1, files.Count);
            LogDivider();
            LogLine($"同步前先备份本机 {src} → {dest}（共 {files.Count} 项）...");
            await engine.RunAsync(OnFileProgress, m => LogLineError("跳过: " + m), _cts!.Token);
            LogBackupSummary("同步前备份", engine, files.Count);
            await CompressAndRemoveFolderAsync(dest);
            return true;
        }
        catch (OperationCanceledException)
        {
            LogLine("同步前备份已取消");
            return false;
        }
        catch (Exception ex)
        {
            LogLineError("同步前备份失败: " + ex.Message);
            return false;
        }
    }

    private async void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        string dest = txtBackupDest.Text.Trim();
        if (string.IsNullOrWhiteSpace(dest))
        {
            MessageBox.Show("请输入备份目标文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (rbBackupRemote.IsChecked == true)
        {
            await BackupFromRemoteAsync(dest);
        }
        else
        {
            await RunPreBackupBatAsync();
            if (rbBackupShare.IsChecked == true)
                await BackupFromShareAsync(dest);
            else
                await BackupLocalAsync(dest);
        }
    }

    private async Task RunPreBackupBatAsync()
    {
        if (cbRunPreBackupBat.IsChecked != true)
            return;

        string batDir = AppPaths.ExeDir();
        string batPath = Path.Combine(batDir, "LocalDB_Backup.bat");
        if (!File.Exists(batPath))
        {
            LogLineError($"未找到 {batPath}，跳过备份前脚本并继续备份");
            return;
        }

        LogDivider();
        LogLine($"开始执行备份前脚本: {batPath} ...");
        try
        {
            bool ok = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{batPath}\"",
                    WorkingDirectory = batDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return p == null || p.ExitCode == 0;
            });
            if (ok)
                LogLine("备份前脚本执行完成");
            else
                LogLineError("备份前脚本执行失败（ExitCode 非 0），继续备份");
        }
        catch (Exception ex)
        {
            LogLineError("执行备份前脚本异常，继续备份: " + ex.Message);
        }
    }

    private string BackupNameFor(string fallback)
    {
        if (cbAutoFetchName.IsChecked == true)
        {
            string auto = AutoDetectedName();
            if (!string.IsNullOrWhiteSpace(auto))
                return auto;
            LogLineError("自动获取电脑名称失败：未在 BBK 目录中找到包含 DeviceNo 的 SystemConfig.xml，使用默认名称。");
        }
        return fallback;
    }

    private async Task BackupLocalAsync(string dest)
    {
        string src = txtRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src))
        {
            MessageBox.Show("源文件夹无效或不存在：" + src, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string fallback = Environment.MachineName;
        string name = BackupNameFor(fallback);
        string target = BackupTargetPath(dest, name);

        BackupEngine engine;
        try
        {
            engine = new BackupEngine(src, target, ReadBackupOptions());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartBusy();
        try
        {
            var files = engine.Plan();
            _totalCount = files.Count;
            _opLabel = "备份";
            progressBar.Maximum = Math.Max(1, files.Count);
            LogDivider();
            LogLine($"开始备份 {src} → {target}（共 {files.Count} 项）...");
            await engine.RunAsync(OnFileProgress, m => LogLineError("跳过: " + m), _cts!.Token);
            LogBackupSummary("备份", engine, files.Count);
            txtStatus.Text = "备份完成";
            await CompressAndRemoveFolderAsync(target);
        }
        catch (OperationCanceledException)
        {
            LogLine("备份已取消");
        }
        catch (Exception ex)
        {
            LogLineError("备份失败: " + ex.Message);
            MessageBox.Show("备份失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task BackupFromRemoteAsync(string dest)
    {
        List<string> hosts;
        try
        {
            hosts = IpHelper.ExpandIps(cboPeerIp.Text ?? "");
        }
        catch (FormatException ex)
        {
            MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (hosts.Count == 0)
        {
            MessageBox.Show("请输入对方 IP 或范围。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryGetPeerPort(out int port))
        {
            MessageBox.Show("对方端口无效。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartBusy();
        var ct = _cts!.Token;

        var okIps = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                string target;
                if (cbAutoFetchName.IsChecked == true)
                {
                    string name = "";
                    string line = "";
                    try
                    {
                        using (var probe = new PeerClient())
                        {
                            await probe.ConnectAsync(host, port, Constants.RoleProbe, ct);
                            (name, line) = await probe.ProbeDeviceAsync(ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogLineError($"从 {host} 读取设备配置失败: {ex.Message}");
                    }
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        LogLineError($"未能从 {host} 的 BBK 配置获取电脑名称，使用默认名称。");
                        name = ComputerNameFor(host);
                    }
                    else
                    {
                        LogLine($"已从 {host} 获取设备配置：DeviceNo={name}，LineNo={line}");
                    }
                    target = BackupTargetPath(dest, name, line);
                }
                else
                {
                    target = BackupTargetPath(dest, ComputerNameFor(host));
                }

                try
                {
                    bool preBackupBat = cbRunPreBackupBat.IsChecked == true;
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RolePull, ct, preBackupBat);
                    LogDivider();
                    LogLine($"已连接对方 {host}:{port}，开始拉取备份 → {target} ...");
                    _opLabel = "备份";
                    await client.BackupPullAsync(target, ReadBackupOptions(), preBackupBat, LogLine, OnFileProgress, OnTotal,
                        m => LogLineError("跳过: " + m), ct, LogLineError);                    okIps.Add(host);
                    LogLine($"备份完成: {host}");
                    await CompressAndRemoveFolderAsync(target);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed.Add($"{host} - {ex.Message}");
                    LogLineError($"备份失败 {host}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogLine("备份已取消");
        }
        finally
        {
            if (okIps.Count > 0)
            {
                foreach (var ip in okIps)
                    _history.Upsert(ip, port);
                ReloadHistoryCombo();
            }
            EndBusy();
        }

        if (okIps.Count > 0)
            txtStatus.Text = hosts.Count > 1 ? $"远程备份完成（{okIps.Count}/{hosts.Count} 台）" : "远程备份完成";
        if (failed.Count > 0)
            MessageBox.Show("以下电脑备份失败：\n" + string.Join("\n", failed), "部分失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task BackupFromShareAsync(string dest)
    {
        string? unc = TryGetShareUnc();
        if (unc == null)
            return;

        string target;
        if (cbAutoFetchName.IsChecked == true)
        {
            var (name, line) = DeviceConfig.ReadFromRoot(unc);
            if (string.IsNullOrWhiteSpace(name))
            {
                LogLineError($"未能从共享 {unc} 的 BBK 配置获取电脑名称，使用默认名称。");
                name = ComputerNameFor(txtShareIp.Text.Trim());
            }
            else
            {
                LogLine($"已从共享 {unc} 获取设备配置：DeviceNo={name}，LineNo={line}");
            }
            target = BackupTargetPath(dest, name, line);
        }
        else
        {
            target = BackupTargetPath(dest, ComputerNameFor(txtShareIp.Text.Trim()));
        }

        BackupEngine engine;
        try
        {
            engine = new BackupEngine(unc, target, ReadBackupOptions());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        StartBusy();
        try
        {
            var files = engine.Plan();
            _totalCount = files.Count;
            _opLabel = "备份";
            progressBar.Maximum = Math.Max(1, files.Count);
            LogDivider();
            LogLine($"开始从共享 {unc} 备份 → {target}（共 {files.Count} 项）...");
            await engine.RunAsync(OnFileProgress, m => LogLineError("跳过: " + m), _cts!.Token);
            LogBackupSummary("共享备份", engine, files.Count);
            txtStatus.Text = "备份完成";
            await CompressAndRemoveFolderAsync(target);
        }
        catch (OperationCanceledException)
        {
            LogLine("备份已取消");
        }
        catch (Exception ex)
        {
            LogLineError("备份失败: " + ex.Message);
            MessageBox.Show("备份失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndBusy();
        }
    }

    private string? TryGetShareUnc()
    {
        string ip = txtShareIp.Text.Trim();
        if (string.IsNullOrWhiteSpace(ip))
        {
            MessageBox.Show("请输入对方电脑的 IP。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        string unc = SmbHelper.MakeUnc(ip, txtSharePath.Text.Trim());
        string user = txtShareUser.Text.Trim();
        string password = pwdSharePass.Password;
        try
        {
            SmbHelper.Connect(unc, string.IsNullOrEmpty(user) ? null : user, password);
            LogLine("已连接共享 " + unc);
            return unc;
        }
        catch (Exception ex)
        {
            LogLineError("连接共享失败: " + ex.Message);
            MessageBox.Show("连接共享失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    private void BtnShareTest_Click(object sender, RoutedEventArgs e)
    {
        string? unc = TryGetShareUnc();
        if (unc == null)
            return;

        try
        {
            string first = Directory.EnumerateDirectories(unc).FirstOrDefault()
                           ?? Directory.EnumerateFiles(unc).FirstOrDefault()
                           ?? "";
            if (string.IsNullOrEmpty(first))
                LogLine($"连接成功 {unc}，但共享中没有可访问的内容。");
            else
                LogLine($"连接成功 {unc}，可访问。（示例: {first}）");
        }
        catch (Exception ex)
        {
            LogLineError("连接成功但无法读取共享内容: " + ex.Message);
            MessageBox.Show("连接成功但无法读取共享内容: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartBusy()
    {
        _cts = new CancellationTokenSource();
        _doneCount = 0;
        _totalCount = 0;
        progressBar.Maximum = 1;
        progressBar.Value = 0;
        SetBusy(true);
    }

    private void EndBusy()
    {
        _cts?.Dispose();
        _cts = null;
        SetBusy(false);
        txtStatus.Text = "就绪";
    }

    private void BtnStartServer_Click(object sender, RoutedEventArgs e)
    {
        StartServer(showErrors: true);
    }

    private void StartServer(bool showErrors)
    {
        if (_server != null)
            return;

        if (!int.TryParse(txtPort.Text, out int port) || port < 1 || port > 65535)
        {
            if (showErrors)
                MessageBox.Show("端口无效，请输入 1~65535 之间的数字。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string root = txtRoot.Text.Trim();
        var options = ReadOptions();

        _server = new PeerServer(root, port, options,
            LogLine, OnFileProgress, OnTotal, m => LogLineError("接收失败: " + m),
            cbBackupBeforeSync.IsChecked ?? true,
            txtBackupDest.Text.Trim(),
            ReadBackupOptions());
        try
        {
            _server.Start();
            btnStart.IsEnabled = false;
            btnStop.IsEnabled = true;
            txtLocalIp.Text = "本机地址: " + string.Join("   ", GetLocalIPs());
            LogLine($"服务已启动，端口 {port}，根目录 {root}。对方填上此 IP 与本端口即可同步。");
        }
        catch (Exception ex)
        {
            if (showErrors)
                MessageBox.Show("启动服务失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                LogLineError("启动服务失败: " + ex.Message);
            _server?.Dispose();
            _server = null;
        }
    }

    private void BtnStopServer_Click(object sender, RoutedEventArgs e)
    {
        StopServer();
    }

    private void StopServer()
    {
        _server?.Stop();
        _server = null;
        btnStart.IsEnabled = true;
        btnStop.IsEnabled = false;
        txtLocalIp.Text = "服务已停止";
        LogLine("服务已停止");
    }

    private static List<string> GetLocalIPs()
    {
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    list.Add(addr.Address.ToString());
            }
        }
        if (list.Count == 0)
            list.Add("(未检测到局域网地址)");
        return list;
    }

    private void UpdateShareVisibility()
    {
        if (grpShare == null || rbSyncShare == null || rbBackupShare == null)
            return;
        bool show = (rbSyncShare.IsChecked == true) || (rbBackupShare.IsChecked == true);
        grpShare.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RbDir_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        UpdateShareVisibility();
        if (rbSyncShare.IsChecked == true)
        {
            txtHint.Text = "本机为目标：从下方共享连接直接读取对方机器上的文件并更新到本机文件夹，对方免安装。更新规则按本机设置执行。";
            return;
        }

        bool push = rbPush.IsChecked == true;
        txtHint.Text = push
            ? "本机为源：对方需先“启动服务”。推送时规则以对方界面设置为准。"
            : "本机为目标：对方需先“启动服务”并告知 IP/端口。更新规则按本机界面设置执行。";
    }

    private void RbBackupSource_Changed(object sender, RoutedEventArgs e)
    {
        if (rowLocalName != null)
            rowLocalName.Visibility = Visibility.Visible;
        if (grpShare == null || rbSyncShare == null || rbBackupShare == null)
            return;
        UpdateShareVisibility();
    }

    private async void BtnSync_Click(object sender, RoutedEventArgs e)
    {
        if (tabs.SelectedIndex == 2)
        {
            await TransferToRemoteAsync();
            return;
        }

        if (MessageBox.Show($"确认开始更新？\n\n将按当前配置对[{cboPeerIp.Text}]电脑执行文件同步。",
                "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        if (rbSyncShare.IsChecked == true)
        {
            await SyncFromShareAsync();
            return;
        }

        List<string> hosts;
        try
        {
            hosts = IpHelper.ExpandIps(cboPeerIp.Text ?? "");
        }
        catch (FormatException ex)
        {
            MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (hosts.Count == 0)
        {
            MessageBox.Show("请输入对方 IP 或范围。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryGetPeerPort(out int port))
        {
            MessageBox.Show("对方端口无效。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool push = rbPush.IsChecked == true;
        string root = txtRoot.Text.Trim();
        var options = ReadOptions();
        StartBusy();
        var ct = _cts!.Token;
        _opLabel = "更新";

        if (!push && !await RunBackupBeforeSyncAsync())
            return;

        var okIps = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, push ? Constants.RolePush : Constants.RolePull, ct);

                    if (push)
                    {
                        LogDivider();
                        LogLine($"以本机为源连接对方 {host}:{port} 成功，推送 {root} 的文件清单...");
                        await client.PushAsync(root, options, LogLine, OnFileProgress, OnTotal,
                            m => LogLineError("远端电脑: " + m), ct);
                    }
                    else
                    {
                        LogDivider();
                        LogLine($"以对方为源连接对方 {host}:{port} 成功，更新到本机 {root} ...");
                        await client.PullAsync(root, options, LogLine, OnFileProgress, OnTotal,
                            m => LogLineError("传输失败，跳过: " + m), ct);
                    }

                    okIps.Add(host);
                    LogLine($"同步完成: {host}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed.Add($"{host} - {ex.Message}");
                    LogLineError($"同步失败 {host}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogLine("已取消");
        }
        finally
        {
            if (okIps.Count > 0)
            {
                foreach (var ip in okIps)
                    _history.Upsert(ip, port);
                ReloadHistoryCombo();
            }
            EndBusy();
        }

        if (okIps.Count > 0)
            txtStatus.Text = hosts.Count > 1 ? $"同步完成（{okIps.Count}/{hosts.Count} 台）" : "同步完成";
        if (failed.Count > 0)
            MessageBox.Show("以下电脑同步失败：\n" + string.Join("\n", failed), "部分失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task SyncFromShareAsync()
    {
        string? unc = TryGetShareUnc();
        if (unc == null)
            return;

        string root = txtRoot.Text.Trim();
        var options = ReadOptions();

        StartBusy();
        var ct = _cts!.Token;
        _opLabel = "更新";

        try
        {
            if (!await RunBackupBeforeSyncAsync())
                return;

            var remote = FileLister.ListFiles(unc);
            var engine = new SyncEngine(root, options);
            engine.Plan(remote);
            OnTotal(engine.NeedList.Count);
            LogDivider();
            LogLine($"共享 {unc} 现共有 {remote.Count} 个文件，需要更新 {engine.NeedList.Count} 个...");
            if (engine.NeedList.Count == 0)
            {
                LogLine("所有文件与共享相同，无需更新。");
                txtStatus.Text = "共享同步完成";
                return;
            }
            await engine.CopyNeededFromAsync(unc, OnFileProgress, m => LogLineError("跳过: " + m), ct);
            LogLine("共享同步完成");
            txtStatus.Text = "共享同步完成";
        }
        catch (OperationCanceledException)
        {
            LogLine("已取消");
        }
        catch (Exception ex)
        {
            LogLineError("共享同步失败: " + ex.Message);
            MessageBox.Show("共享同步失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task TransferToRemoteAsync()
    {
        string itemPath = txtTransferPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            MessageBox.Show("请先选择要传输的文件或文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Directory.Exists(itemPath) && !File.Exists(itemPath))
        {
            MessageBox.Show("路径无效或不存在：" + itemPath, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        List<string> hosts;
        try
        {
            hosts = IpHelper.ExpandIps(cboPeerIp.Text ?? "");
        }
        catch (FormatException ex)
        {
            MessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (hosts.Count == 0)
        {
            MessageBox.Show("请输入对方 IP 或范围。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!TryGetPeerPort(out int port))
        {
            MessageBox.Show("对方端口无效。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool isDir = Directory.Exists(itemPath);
        if (MessageBox.Show($"确认开始传输？\n\n将把 [{itemPath}] 传输到[{cboPeerIp.Text}]电脑的相同路径（{(isDir ? "文件夹" : "文件")}）。\n传输前会自动备份对方相应的文件/文件夹。",
                "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        StartBusy();
        var ct = _cts!.Token;
        _opLabel = "传输";

        var okIps = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RoleTransfer, ct);
                    LogDivider();
                    LogLine($"传输到 {host}:{port}（目标路径不变，同名同大小同时跳过）...");
                    await client.TransferAsync(itemPath, isDir,
                        cbTransferSameSkip.IsChecked == true,
                        cbTransferKillFreeForm.IsChecked == true,
LogLine, OnFileProgress, OnTotal,
                        m => LogLineError("远端电脑: " + m), ct);
                    okIps.Add(host);
                    LogLine($"传输完成: {host}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed.Add($"{host} - {ex.Message}");
                    LogLineError($"传输失败 {host}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogLine("已取消");
        }
        finally
        {
            if (okIps.Count > 0)
            {
                foreach (var ip in okIps)
                    _history.Upsert(ip, port);
                ReloadHistoryCombo();
            }
            EndBusy();
        }

        if (okIps.Count > 0)
            txtStatus.Text = hosts.Count > 1 ? $"传输完成（{okIps.Count}/{hosts.Count} 台）" : "传输完成";
        if (failed.Count > 0)
            MessageBox.Show("以下电脑传输失败：\n" + string.Join("\n", failed), "部分失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void Tabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        RefreshOpButtons();
    }

    private void RefreshOpButtons()
    {
        if (btnSync == null || btnBackup == null || tabs == null)
            return;
        int idx = tabs.SelectedIndex;
        bool isBackup = idx == 0;
        btnBackup.IsEnabled = !_busy && isBackup;
        btnSync.IsEnabled = !_busy && !isBackup;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshOpButtons();
        btnCancel.IsEnabled = busy;
        btnStart.IsEnabled = !busy && _server == null;
        btnStop.IsEnabled = !busy && _server != null;
        txtRoot.IsEnabled = !busy;
        txtPort.IsEnabled = !busy;
        cboPeerIp.IsEnabled = !busy;
        txtPeerPort.IsEnabled = !busy;
        btnHistory.IsEnabled = !busy;
        btnTestConnect.IsEnabled = !busy;
        txtBackupDest.IsEnabled = !busy;
        cbBackupLogRule.IsEnabled = !busy;
        cbBackupBeforeSync.IsEnabled = !busy;
        cbCompressZip.IsEnabled = !busy;
        cbIgnoreEnabled.IsEnabled = !busy;
        txtBackupIgnore.IsEnabled = !busy;
        txtBackupLogDays.IsEnabled = !busy;
        rbBackupLocal.IsEnabled = !busy;
        rbBackupRemote.IsEnabled = !busy;
        rbBackupShare.IsEnabled = !busy;
        txtShareIp.IsEnabled = !busy;
        txtSharePath.IsEnabled = !busy;
        txtShareUser.IsEnabled = !busy;
        pwdSharePass.IsEnabled = !busy;
        btnShareTest.IsEnabled = !busy;
        rbSyncShare.IsEnabled = !busy;
        chkAutoStart.IsEnabled = !busy;
        txtTransferPath.IsEnabled = !busy;
        cbTransferSameSkip.IsEnabled = !busy;
        cbTransferKillFreeForm.IsEnabled = !busy;
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        lbLog.Items.Clear();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _tray != null)
            Hide();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        tabs.SelectedIndex = 0;
        RefreshOpButtons();
        StartServer(showErrors: false);
        if (_settings.Settings.AutoStartAndListen)
        {
            _tray = new TrayIcon(ShowMainWindow, ExitApp);
            _tray.Show();
            Hide();
            _tray.Notify("BBK 工具已在后台运行，双击托盘图标可打开主界面，右键图标可完全退出。");
        }
    }

    private void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitApp()
    {
        Dispatcher.Invoke(() =>
        {
            _allowExit = true;
            Close();
        });
    }

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        bool enabled = chkAutoStart.IsChecked == true;
        try
        {
            string exe = Environment.ProcessPath
                         ?? Process.GetCurrentProcess().MainModule?.FileName
                         ?? "BBKSync.exe";
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key == null)
                throw new InvalidOperationException("无法打开注册表 Run 键");
            if (enabled)
            {
                string dir = Path.GetDirectoryName(exe) ?? ".";
                key.SetValue(Constants.RegistryValueName, $"cmd.exe /c cd /d \"{dir}\" && \"{exe}\" --autostart");
            }
            else
                key.DeleteValue(Constants.RegistryValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            LogLineError("写入开机自启失败: " + ex.Message);
        }

        _settings.Settings.AutoStartAndListen = enabled;
        SaveSettings();
        LogLine(enabled ? "已启用：开机自动启动并后台监听" : "已关闭开机自启");
    }

    private void SaveSettings()
    {
        _settings.Settings.Root = txtRoot.Text.Trim();
        _settings.Settings.Port = int.TryParse(txtPort.Text, out int p) ? p : _settings.Settings.Port;
        _settings.Settings.BackupDest = txtBackupDest.Text.Trim();
        _settings.Settings.BackupBeforeSync = cbBackupBeforeSync.IsChecked ?? true;
        _settings.Settings.BackupLogRule = cbBackupLogRule.IsChecked ?? true;
        _settings.Settings.BackupLogDays = int.TryParse(txtBackupLogDays.Text, out int d) && d >= 1 ? d : 2;
        _settings.Settings.BackupIgnoreRegexes = txtBackupIgnore.Text;
        _settings.Settings.IgnoreRegexEnabled = cbIgnoreEnabled.IsChecked ?? true;
        _settings.Settings.CompressZip = cbCompressZip.IsChecked ?? true;
        _settings.Settings.RunPreBackupBat = cbRunPreBackupBat.IsChecked ?? true;
        _settings.Settings.AutoFetchComputerName = cbAutoFetchName.IsChecked == true;
        _settings.Settings.TransferPath = txtTransferPath.Text.Trim();
        _settings.Settings.TransferSameSkip = cbTransferSameSkip.IsChecked ?? true;
        _settings.Settings.TransferKillFreeForm = cbTransferKillFreeForm.IsChecked ?? true;
        _settings.Save();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_tray != null && !_allowExit)
        {
            e.Cancel = true;
            Hide();
            _tray.Notify("BBK 工具仍在后台运行：双击托盘图标打开主界面，右键图标选择“退出”可完全退出。");
            return;
        }

        _allowExit = true;
        SaveSettings();
        _cts?.Cancel();
        StopServer();
        _tray?.Dispose();
        _tray = null;
    }
}