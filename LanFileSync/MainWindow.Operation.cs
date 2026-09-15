using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Windows;

namespace LanFileSync;

public partial class MainWindow
{
    /// <summary>
    /// 从目标 IP 列表中剔除本机地址(含回环地址)。
    /// 存在本机地址时弹窗+日志提示；全部为本机时返回 null(调用方应中止后续操作)。
    /// </summary>
    private List<string>? ExcludeLocalHosts(List<string> hosts)
    {
        var local = new HashSet<string>(GetLocalIPs(), StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "::1", "localhost" };
        try
        {
            foreach (var a in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                if (a.AddressFamily == AddressFamily.InterNetwork) local.Add(a.ToString());
        }
        catch { }

        var skipped = hosts.Where(h => local.Contains(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var remote = hosts.Where(h => !local.Contains(h)).ToList();
        if (skipped.Count > 0)
        {
            LogLine("目标 IP 为本机地址，已跳过: " + string.Join(", ", skipped));
            DarkMessageBox.Show("以下 IP 为本机地址，已跳过：\n" + string.Join("\n", skipped) +
                                (remote.Count > 0 ? "\n\n将继续对其余远端电脑执行操作。" : ""),
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return remote.Count == 0 ? null : remote;
    }
    private async void BtnBackup_Click(object sender, RoutedEventArgs e)
    {
        string dest = txtBackupDest.Text.Trim();
        if (string.IsNullOrWhiteSpace(dest)) { DarkMessageBox.Show("请输入备份目标文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (rbBackupRemote.IsChecked == true) { await BackupFromRemoteAsync(dest); }
        else
        {
            await RunPreBackupBatAsync();
            if (rbBackupShare.IsChecked == true) await BackupFromShareAsync(dest);
            else await BackupLocalAsync(dest);
        }
        LogLine("备份程序执行完成");
    }

    private async Task BackupLocalAsync(string dest)
    {
        string src = txtRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src))
        { DarkMessageBox.Show("源文件夹无效或不存在：" + src, "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (DarkMessageBox.Show("确认开始备份本地BBK程序？\n\n程序备份到：" + dest + "。", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        string fallback = Environment.MachineName;
        string name = BackupNameFor(fallback);
        string target = BackupTargetPath(dest, name);
        BackupEngine engine;
        try { engine = new BackupEngine(src, target, ReadBackupOptions()); }
        catch (Exception ex) { DarkMessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        StartBusy();
        try
        {
            var files = engine.Plan(); _totalCount = files.Count; _opLabel = "备份";
            OnTotal(files.Count);
            LogDivider(); LogLine($"开始备份 {src} → {target}(共 {files.Count} 项)...");
            await engine.RunAsync(OnFileProgress, m => LogLineError("跳过: " + m), _coordinator.Token);
            LogBackupSummary("备份", engine, files.Count); txtStatus.Text = "备份完成";
            await CompressAndRemoveFolderAsync(target, ShouldCompress);
        }
        catch (OperationCanceledException) { LogLine("备份已取消"); }
        catch (Exception ex) { LogLineError("备份失败: " + ex.Message); DarkMessageBox.Show("备份失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { EndBusy(); }
    }

    private async Task BackupFromRemoteAsync(string dest)
    {
        List<string> hosts;
        try { hosts = IpHelper.ExpandIps(cboPeerIp.Text ?? ""); }
        catch (FormatException ex) { DarkMessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (hosts.Count == 0) { DarkMessageBox.Show("请输入对方 IP 或范围。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!TryGetPeerPort(out int port)) { DarkMessageBox.Show("对方端口无效。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        string deviceInfo = hosts.Count == 1 ? await GetDeviceInfo(hosts[0]) : "";
        if (DarkMessageBox.Show("确认开始备份远端BBK程序？\n\n远端电脑：[" + string.Join(";", hosts) + "]" + deviceInfo + "\n程序备份到：" + dest + "。", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        StartBusy();
        var ct = _coordinator.Token; _opLabel = "备份";
        var okIps = new List<string>(); var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                string target;
                if (cbAutoFetchName.IsChecked == true)
                {
                    string name = "", line = "";
                    try
                    {
                        using var probe = new PeerClient();
                        await probe.ConnectAsync(host, port, Constants.RoleProbe, ct);
                        (name, line) = await probe.ProbeDeviceAsync(ct);
                    }
                    catch (Exception ex) { LogLineError($"从 {host} 读取设备配置失败: {ex.Message}"); }
                    if (string.IsNullOrWhiteSpace(name)) { LogLineError($"未能从 {host} 的 BBK 配置获取电脑名称，使用默认名称。"); name = ComputerNameFor(host); }
                    else LogLine($"已从 {host} 获取设备配置：DeviceNo={name}，LineNo={line}");
                    target = BackupTargetPath(dest, name, line);
                }
                else target = BackupTargetPath(dest, ComputerNameFor(host));
                try
                {
                    bool preBackupBat = cbRunPreBackupBat.IsChecked == true;
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RolePull, ct, preBackupBat);
                    LogDivider(); LogLine($"已连接对方 {host}:{port}，开始拉取备份 → {target} ...");
                    _opLabel = "备份";
                    await client.BackupPullAsync(target, ReadBackupOptions(), preBackupBat, LogLine, OnFileProgress, OnTotal, m => LogLineError("跳过: " + m), ct, LogLineError);
                    okIps.Add(host); LogLine($"备份完成: {host}");
                    await CompressAndRemoveFolderAsync(target, ShouldCompress);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed.Add($"{host} - {ex.Message}"); LogLineError($"备份失败 {host}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { LogLine("备份已取消"); }
        finally
        {
            if (okIps.Count > 0) { foreach (var ip in okIps) _history.Upsert(ip, port); ReloadHistoryCombo(); }
            EndBusy();
        }
        if (okIps.Count > 0) txtStatus.Text = hosts.Count > 1 ? $"远程备份完成({okIps.Count}/{hosts.Count} 台)" : "远程备份完成";
        if (failed.Count > 0) DarkMessageBox.Show("以下电脑备份失败：\n" + string.Join("\n", failed), "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task BackupFromShareAsync(string dest)
    {
        string? unc = TryGetShareUnc();
        if (unc == null) return;
        string target;
        if (cbAutoFetchName.IsChecked == true)
        {
            var (name, line) = DeviceConfig.ReadFromRoot(unc);
            if (string.IsNullOrWhiteSpace(name)) { LogLineError($"未能从共享 {unc} 的 BBK 配置获取电脑名称，使用默认名称。"); name = ComputerNameFor(txtShareIp.Text.Trim()); }
            else LogLine($"已从共享 {unc} 获取设备配置：DeviceNo={name}，LineNo={line}");
            target = BackupTargetPath(dest, name, line);
        }
        else target = BackupTargetPath(dest, ComputerNameFor(txtShareIp.Text.Trim()));
        BackupEngine engine;
        try { engine = new BackupEngine(unc, target, ReadBackupOptions()); }
        catch (Exception ex) { DarkMessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        StartBusy();
        try
        {
            var files = engine.Plan(); _totalCount = files.Count; _opLabel = "备份";
            OnTotal(files.Count);
            LogDivider(); LogLine($"开始从共享 {unc} 备份 → {target}(共 {files.Count} 项)...");
            await engine.RunAsync(OnFileProgress, m => LogLineError("跳过: " + m), _coordinator.Token);
            LogBackupSummary("共享备份", engine, files.Count); txtStatus.Text = "备份完成";
            await CompressAndRemoveFolderAsync(target, ShouldCompress);
        }
        catch (OperationCanceledException) { LogLine("备份已取消"); }
        catch (Exception ex) { LogLineError("备份失败: " + ex.Message); DarkMessageBox.Show("备份失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { EndBusy(); }
    }

    private string? TryGetShareUnc()
    {
        string ip = txtShareIp.Text.Trim();
        if (string.IsNullOrWhiteSpace(ip)) { DarkMessageBox.Show("请输入对方电脑的 IP。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return null; }
        string unc = SmbHelper.MakeUnc(ip, txtSharePath.Text.Trim());
        string user = txtShareUser.Text.Trim(), password = pwdSharePass.Password;
        try
        {
            SmbHelper.Connect(unc, string.IsNullOrEmpty(user) ? null : user, password);
            LogLine("已连接共享 " + unc); return unc;
        }
        catch (Exception ex) { LogLineError("连接共享失败: " + ex.Message); DarkMessageBox.Show("连接共享失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); return null; }
    }

    private void BtnShareTest_Click(object sender, RoutedEventArgs e)
    {
        string? unc = TryGetShareUnc();
        if (unc == null) return;
        try
        {
            string first = Directory.EnumerateDirectories(unc).FirstOrDefault() ?? Directory.EnumerateFiles(unc).FirstOrDefault() ?? "";
            LogLine(string.IsNullOrEmpty(first) ? $"连接成功 {unc}，但共享中没有可访问的内容。" : $"连接成功 {unc}，可访问。(示例: {first})");
        }
        catch (Exception ex) { LogLineError("连接成功但无法读取共享内容: " + ex.Message); DarkMessageBox.Show("连接成功但无法读取共享内容: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void BtnSync_Click(object sender, RoutedEventArgs e)
    {
        if (tabs.SelectedIndex == 2)
        {
            if (rbTransferPull.IsChecked == true) { await TransferFromRemoteAsync(); return; }
            await TransferToRemoteAsync(); return;
        }
        if (rbSyncShare.IsChecked == true) { await SyncFromShareAsync(); return; }
        List<string> hosts;
        try { hosts = IpHelper.ExpandIps(cboPeerIp.Text ?? ""); }
        catch (FormatException ex) { DarkMessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (hosts.Count == 0) { DarkMessageBox.Show("请输入对方 IP 或范围。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!TryGetPeerPort(out int port)) { DarkMessageBox.Show("对方端口无效。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (rbReceive.IsChecked == true && hosts.Count > 1) { DarkMessageBox.Show("拉取对方更新时只能选择一个IP地址", "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        hosts = ExcludeLocalHosts(hosts);
        if (hosts == null) return;
        bool push = rbPush.IsChecked == true;
        string deviceInfo = hosts.Count == 1 ? await GetDeviceInfo(hosts[0]) : "";
        string msgPush = $"将本机BBK按规则推送更新至远端电脑[{cboPeerIp.Text}]{deviceInfo}";
        string msgPull = $"将远端电脑[{cboPeerIp.Text}]{deviceInfo}的BBK按规则拉取更新至本机";
        if (DarkMessageBox.Show("确认开始更新？\n\n" + (push ? msgPush : msgPull) + "。", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        string root = txtRoot.Text.Trim();
        var options = ReadOptions();
        StartBusy();
        var ct = _coordinator.Token; _opLabel = "更新";
        if (!push && !await RunPreBackupBatAsync()) return;
        var okIps = new List<string>(); var failed = new List<string>();
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
                        LogDivider(); LogLine($"以本机为源连接对方 {host}:{port} 成功，推送 {root} 的文件清单...");
                        await client.PushAsync(root, options, LogLine, OnFileProgress, OnTotal, m => LogLineError("远端电脑: " + m), ct);
                    }
                    else
                    {
                        LogDivider(); LogLine($"以对方为源连接对方 {host}:{port} 成功，更新到本机 {root} ...");
                        if (cbBackupBeforeSync.IsChecked == true && !string.IsNullOrWhiteSpace(txtBackupDest.Text.Trim()))
                        {
                            string dest = Path.Combine(txtBackupDest.Text.Trim(), $"BBK_接收更新备份_{DateTime.Now:yyyyMMdd}");
                            LogLine($"先备份本机 BBK 到 {dest}");
                            try
                            {
                                var be = new BackupEngine(root, dest, ReadBackupOptions());
                                var bakFiles = be.Plan();
                                await be.RunAsync(OnFileProgress, m => LogLineError("同步前备份跳过: " + m), ct);
                                LogLine($"同步前备份完成({bakFiles.Count} 项)");
                                await CompressAndRemoveFolderAsync(dest, ShouldCompress);
                            }
                            catch (ArgumentException) { LogLine($"{txtBackupDest.Text.Trim()} 为空或与同步目录相同，跳过同步前备份。"); }
                            catch (Exception ex) { LogLineError("同步前备份失败: " + ex.Message); }
                        }
                        await client.PullAsync(root, options, LogLine, OnFileProgress, OnTotal, m => LogLineError("传输失败，跳过: " + m), ct);
                    }
                    okIps.Add(host); LogLine($"同步完成: {host}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed.Add($"{host} - {ex.Message}"); LogLineError($"同步失败 {host}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { LogLine("已取消"); }
        finally
        {
            if (okIps.Count > 0) { foreach (var ip in okIps) _history.Upsert(ip, port); ReloadHistoryCombo(); }
            EndBusy();
        }
        if (okIps.Count > 0) txtStatus.Text = hosts.Count > 1 ? $"同步完成({okIps.Count}/{hosts.Count} 台)" : "同步完成";
        if (failed.Count > 0) DarkMessageBox.Show("以下电脑同步失败：\n" + string.Join("\n", failed), "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task SyncFromShareAsync()
    {
        string? unc = TryGetShareUnc();
        if (unc == null) return;
        string root = txtRoot.Text.Trim(); var options = ReadOptions();
        StartBusy();
        var ct = _coordinator.Token; _opLabel = "更新";
        try
        {
            if (!await RunPreBackupBatAsync()) return;
            var remote = FileLister.ListFiles(unc);
            var engine = new SyncEngine(root, options); engine.Plan(remote);
            OnTotal(engine.NeedList.Count);
            LogDivider(); LogLine($"共享 {unc} 现共有 {remote.Count} 个文件，需要更新 {engine.NeedList.Count} 个...");
            if (engine.NeedList.Count == 0) { LogLine("所有文件与共享相同，无需更新。"); txtStatus.Text = "共享同步完成"; return; }
            if (engine.NeedList.Count > 0 && engine.NeedList.Count < 10)
                foreach (var p in engine.NeedList) LogLine("  ← " + p);
            await engine.CopyNeededFromAsync(unc, OnFileProgress, m => LogLineError("跳过: " + m), ct);
            LogLine("共享同步完成"); txtStatus.Text = "共享同步完成";
        }
        catch (OperationCanceledException) { LogLine("已取消"); }
        catch (Exception ex) { LogLineError("共享同步失败: " + ex.Message); DarkMessageBox.Show("共享同步失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { EndBusy(); }
    }

    private async Task TransferToRemoteAsync()
    {
        string itemPath = txtTransferPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(itemPath)) { DarkMessageBox.Show("请先选择要传输的文件或文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!Directory.Exists(itemPath) && !File.Exists(itemPath)) { DarkMessageBox.Show("路径无效或不存在：" + itemPath, "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        List<string> hosts;
        try { hosts = IpHelper.ExpandIps(cboPeerIp.Text ?? ""); }
        catch (FormatException ex) { DarkMessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (hosts.Count == 0) { DarkMessageBox.Show("请输入对方 IP 或范围。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!TryGetPeerPort(out int port)) { DarkMessageBox.Show("对方端口无效。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        hosts = ExcludeLocalHosts(hosts);
        if (hosts == null) return;
        bool isDir = Directory.Exists(itemPath);
        bool updateMode = cbTransferUpdate.IsChecked == true;
        string localDevice = DeviceConfig.ReadFromRoot(txtRoot.Text.Trim()).Name;
        if (string.IsNullOrWhiteSpace(localDevice)) localDevice = Environment.MachineName;
        string deviceInfo = hosts.Count == 1 ? await GetDeviceInfo(hosts[0]) : "";
        string modeDesc = updateMode
            ? "更新模式：会先将对方原文件/文件夹重命名为 名称_设备信息_日期，再覆盖到相同路径。"
            : "拷贝模式：不改动对方原文件，新文件以 名称_" + localDevice + "_日期 保存。";
        if (DarkMessageBox.Show("确认开始同步？\n\n将对 [" + cboPeerIp.Text + "]" + deviceInfo + "电脑同步 [" + itemPath + "](" + (isDir ? "文件夹" : "文件") + ")。\n\n" + modeDesc, "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        StartBusy();
        var ct = _coordinator.Token; _opLabel = "传输";
        var okIps = new List<string>(); var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RoleTransfer, ct);
                    LogDivider(); LogLine($"传输到 {host}:{port}({(updateMode ? "更新模式" : "拷贝模式")})...");
                    await client.TransferAsync(itemPath, isDir, cbTransferSameSkip.IsChecked == true, updateMode, localDevice, LogLine, OnFileProgress, OnTotal, m => LogLineError("远端电脑: " + m), ct);
                    okIps.Add(host); LogLine($"传输完成: {host}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed.Add($"{host} - {ex.Message}"); LogLineError($"传输失败 {host}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { LogLine("已取消"); }
        finally
        {
            if (okIps.Count > 0) { foreach (var ip in okIps) _history.Upsert(ip, port); ReloadHistoryCombo(); }
            EndBusy();
        }
        if (okIps.Count > 0) txtStatus.Text = hosts.Count > 1 ? $"传输完成({okIps.Count}/{hosts.Count} 台)" : "传输完成";
        if (failed.Count > 0) DarkMessageBox.Show("以下电脑传输失败：\n" + string.Join("\n", failed), "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task TransferFromRemoteAsync()
    {
        string itemPath = txtTransferPath.Text.Trim();
        if (string.IsNullOrWhiteSpace(itemPath)) { DarkMessageBox.Show("请先输入要拉取的文件或文件夹路径。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        List<string> hosts;
        try { hosts = IpHelper.ExpandIps(cboPeerIp.Text ?? ""); }
        catch (FormatException ex) { DarkMessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (hosts.Count == 0) { DarkMessageBox.Show("请输入对方 IP 或范围。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (!TryGetPeerPort(out int port)) { DarkMessageBox.Show("对方端口无效。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        hosts = ExcludeLocalHosts(hosts);
        if (hosts == null) return;

        string deviceInfo = hosts.Count == 1 ? await GetDeviceInfo(hosts[0]) : "";
        string deviceName = hosts.Count == 1 ? (await ProbeDeviceNameAsync(hosts[0], port)) : "";
        bool updateMode = cbTransferUpdate.IsChecked == true;
        string localDevice = DeviceConfig.ReadFromRoot(txtRoot.Text.Trim()).Name;
        if (string.IsNullOrWhiteSpace(localDevice)) localDevice = Environment.MachineName;
        string modeDesc = updateMode
            ? "更新模式：会先将本地原文件/文件夹重命名为 名称_" + localDevice + "_日期，再拉取到原路径。"
            : "拷贝模式：不修改本地原文件，拉取的文件命名为 名称_" + (string.IsNullOrWhiteSpace(deviceName) ? "远端" : deviceName) + "_日期。";

        if (DarkMessageBox.Show("确认从远端同步？\n\n将从[" + cboPeerIp.Text + "]" + deviceInfo + "拉取 [" + itemPath + "]。\n" + modeDesc, "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        StartBusy();
        var ct = _coordinator.Token; _opLabel = "拉取";
        var okIps = new List<string>(); var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RoleTransferPull, ct);
                    LogDivider(); LogLine($"从 {host}:{port} 拉取文件(远端路径: {itemPath}，{(updateMode ? "更新模式" : "拷贝模式")})...");
                    await client.TransferFromRemoteAsync(itemPath, updateMode, deviceName, localDevice, LogLine, OnFileProgress, OnTotal, m => LogLineError("远端电脑: " + m), ct);
                    okIps.Add(host); LogLine($"拉取完成: {host}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed.Add($"{host} - {ex.Message}"); LogLineError($"拉取失败 {host}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { LogLine("已取消"); }
        finally
        {
            if (okIps.Count > 0) { foreach (var ip in okIps) _history.Upsert(ip, port); ReloadHistoryCombo(); }
            EndBusy();
        }
        if (okIps.Count > 0) txtStatus.Text = hosts.Count > 1 ? $"拉取完成({okIps.Count}/{hosts.Count} 台)" : "拉取完成";
        if (failed.Count > 0) DarkMessageBox.Show("以下电脑拉取失败：\n" + string.Join("\n", failed), "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task<string> ProbeDeviceNameAsync(string host, int port)
    {
        try
        {
            var client = new PeerClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(host, port, Constants.RoleProbe, cts.Token);
            var (name, line) = await client.ProbeDeviceAsync(cts.Token);
            client.Dispose();
            return string.IsNullOrWhiteSpace(name) ? host : name;
        }
        catch { return host; }
    }

    private void LogBackupSummary(string what, BackupEngine engine, int total)
    {
        int copied = engine.CopiedCount, skipped = engine.SkippedCount;
        LogLine(copied == 0 ? $"{what}完成：共 {total} 项，全部与备份目标相同，无需传输。"
            : $"{what}完成：共 {total} 项，复制 {copied} 项" + (skipped > 0 ? $"，跳过相同 {skipped} 项" : "") + "。");
    }

    private void UpdateShareVisibility()
    {
        if (grpShare == null || rbSyncShare == null || rbBackupShare == null) return;
        grpShare.Visibility = ((rbSyncShare.IsChecked == true) || (rbBackupShare.IsChecked == true)) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CbTransferUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool update = cbTransferUpdate.IsChecked == true;
        if (cbTransferSameSkip != null) cbTransferSameSkip.IsEnabled = update;
        if (txtTransferModeHint != null)
            txtTransferModeHint.Text = update
                ? "更新模式：同步给远端会先把远端原文件重命名为 名称_设备信息_日期 再覆盖；从远端同步会先把本地原文件重命名为 名称_设备信息_日期 再拉取到原路径。"
                : "拷贝模式：不改动任何原文件。同步给远端的文件保存为 名称_本机设备_日期；从远端同步的文件保存为 名称_远端设备_日期。";
    }

    private void RbTransferDir_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool isPull = rbTransferPull.IsChecked == true;
        txtTransferHint.Text = isPull
            ? "从远端电脑拉取输入路径的文件/文件夹。更新模式先重命名本地原文件再拉取到原路径；拷贝模式另存为 名称_设备信息_日期。"
            : "将选中的文件/文件夹同步到对方电脑(对方需先\u201c启动服务\u201d;)。更新模式先重命名远端原文件再覆盖；拷贝模式另存为 名称_设备信息_日期。";
    }

    private void RbDir_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateShareVisibility();
        if (rbSyncShare.IsChecked == true)
        { txtHint.Text = "本机为目标：从下方共享连接直接读取对方机器上的文件并更新到本机文件夹，对方免安装。更新规则按本机设置执行。"; return; }
        txtHint.Text = rbPush.IsChecked == true
            ? "本机为源：对方需先\u201c启动服务\u201d。推送时规则以对方界面设置为准。"
            : "本机为目标：对方需先\u201c启动服务\u201d并告知 IP/端口。更新规则按本机界面设置执行。";
    }

    private void RbBackupSource_Changed(object sender, RoutedEventArgs e)
    {
        if (rowLocalName != null) rowLocalName.Visibility = Visibility.Visible;
        UpdateShareVisibility();
    }

    private async void BtnUpdateProgram_Click(object sender, RoutedEventArgs e)
    {
        string host = cboPeerIp.Text.Trim();
        if (string.IsNullOrWhiteSpace(host)) { DarkMessageBox.Show("请输入远端电脑 IP。", "提示", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        int port = int.TryParse(txtPeerPort.Text.Trim(), out int p) ? p : Constants.DefaultPort;

        string exePath = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        { DarkMessageBox.Show("无法获取当前程序路径。", "错误", MessageBoxButton.OK, MessageBoxImage.Error); return; }

        List<string> hosts;
        try { hosts = IpHelper.ExpandIps(host); }
        catch (FormatException ex) { DarkMessageBox.Show(ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        hosts = ExcludeLocalHosts(hosts);
        if (hosts == null) return;

        if (DarkMessageBox.Show("确认开始升级远端BBKSync程序？\n\n目标电脑：" + host + "。", "确认", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var failed = new List<string>();
        var okIps = new List<string>();

        StartBusy();
        txtStatus.Text = $"正在更新远端程序...";
        LogLine($"========== 开始更新远端程序 ==========");
        LogLine($"目标: {host}，端口: {port}");
        try
        {
            foreach (string h in hosts)
            {
                try
                {
                    LogLine($"--- 连接 {h}:{port} ---");
                    using var client = new PeerClient();
                    await client.ConnectAsync(h, port, Constants.RoleUpdate, _coordinator.Token);
                    LogLine("已连接，正在发送更新文件...");
                    await client.UpdateAsync(exePath, m => LogLine(m), _coordinator.Token);
                    okIps.Add(h);
                    LogLine($"{h} 更新完成");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogLineError($"{h} 更新失败: {ex.Message}");
                    failed.Add(h);
                }
            }
        }
        catch (OperationCanceledException) { LogLine("操作已取消"); }
        finally
        {
            if (okIps.Count > 0) { foreach (var ip in okIps) _history.Upsert(ip, port); ReloadHistoryCombo(); }
            EndBusy();
        }
        if (okIps.Count > 0) txtStatus.Text = hosts.Count > 1 ? $"更新完成({okIps.Count}/{hosts.Count} 台)" : "更新完成";
        if (failed.Count > 0) DarkMessageBox.Show("以下电脑更新失败：\n" + string.Join("\n", failed), "部分失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
