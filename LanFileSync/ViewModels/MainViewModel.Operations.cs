using System.IO;
using System.Net.Sockets;

namespace LanFileSync.ViewModels;

/// <summary>主视图模型的业务操作：备份、更新、文件同步、程序升级、测试连接。</summary>
public partial class MainViewModel
{
    /// <summary>从目标 IP 列表中剔除本机地址(含回环地址)。全部为本机时返回 null。</summary>
    private List<string>? ExcludeLocalHosts(List<string> hosts)
    {
        var (remote, skipped) = HostHelper.FilterLocalHosts(hosts);
        if (skipped.Count > 0)
        {
            Log("目标 IP 为本机地址，已跳过: " + string.Join(", ", skipped));
            _dialogs.Info("以下 IP 为本机地址，已跳过：\n" + string.Join("\n", skipped) +
                          (remote.Count > 0 ? "\n\n将继续对其余远端电脑执行操作。" : ""), "提示");
        }
        return remote.Count == 0 ? null : remote;
    }

    /// <summary>统一解析 IP 输入和端口校验。失败时弹窗提示并返回 false。</summary>
    private bool TryGetHostsAndPort(out List<string> hosts, out int port)
    {
        hosts = new List<string>();
        port = 0;
        try { hosts = IpHelper.ExpandIps(PeerIp ?? ""); }
        catch (FormatException ex) { _dialogs.Warning(ex.Message, "错误"); return false; }
        if (hosts.Count == 0) { _dialogs.Info("请输入远端 IP 或范围。", "提示"); return false; }
        if (!TryGetPeerPort(out port)) { _dialogs.Warning("远端端口无效。", "错误"); return false; }
        return true;
    }

    private async Task BackupAsync()
    {
        if (!TryEnterOperation()) return;
        try
        {
            string dest = BackupDestPath.Trim();
            if (string.IsNullOrWhiteSpace(dest))
            {
                _dialogs.Info("请输入备份目标文件夹。", "提示");
                return;
            }
            if (IsBackupRemote) await BackupFromRemoteAsync(dest);
            else if (IsBackupShare) await BackupFromShareAsync(dest);
            else await BackupLocalAsync(dest);
            Log("备份程序执行完成");
        }
        finally { ExitOperation(); }
    }

    private async Task BackupLocalAsync(string dest)
    {
        string src = RootPath.Trim();
        if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src))
        {
            _dialogs.Warning("源文件夹无效或不存在：" + src, "错误");
            return;
        }
        if (!_dialogs.Confirm("确认开始备份本地BBK程序？\n\n程序备份到：" + dest + "。", "确认")) return;
        // 确认之后再执行 PostgreSQL 备份前脚本：取消备份时不会白跑数据库备份
        await RunPreBackupBatAsync();
        string name = BackupNameFor(Environment.MachineName);
        string target = BackupTargetPath(dest, name);
        BackupEngine engine;
        try { engine = new BackupEngine(src, target, ReadBackupOptions()); }
        catch (Exception ex) { _dialogs.Warning(ex.Message, "错误"); return; }

        StartBusy("正在备份本机 BBK...");
        try
        {
            var files = engine.Plan();
            _totalCount = files.Count;
            _opLabel = "备份";
            OnTotal(files.Count);
            LogDivider();
            Log($"开始备份 {src} → {target}(共 {files.Count} 项)...");
            if (files.Count == 0) Log("没有需要备份的文件：目标位置已是最新。");
            await engine.RunAsync(OnFileProgress, m => LogError("跳过: " + m), _coordinator.Token);
            LogBackupSummary("备份", engine, files.Count);
            StatusText = files.Count == 0 ? "备份完成（无需备份文件）" : "备份完成";
            await CompressAndRemoveFolderAsync(target, ShouldCompress);
        }
        catch (OperationCanceledException)
        {
            Log("备份已取消");
            StatusText = "备份已取消";
        }
        catch (Exception ex)
        {
            LogError("备份失败: " + ex.Message);
            _dialogs.Error("备份失败: " + ex.Message, "错误");
        }
        finally
        {
            EndBusy();
            LogSingleOperationSummary("本地备份", true);
        }
    }

    private async Task BackupFromRemoteAsync(string dest)
    {
        if (!TryGetHostsAndPort(out var hosts, out int port)) return;
        string deviceInfo = hosts.Count == 1 ? await GetDeviceInfo(hosts[0]) : "";
        if (!_dialogs.Confirm("确认开始备份远端 BBK 程序？\n\n远端电脑：" + string.Join("、", hosts) + deviceInfo +
                              "\n备份到：" + dest + "。", "确认")) return;

        StartBusy("正在备份远端 BBK...");
        var ct = _coordinator.Token;
        _opLabel = "备份";
        var okIps = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                if (!await EnsureRemoteReadyAsync(host, port))
                {
                    failed.Add($"{host} - 连接测试失败");
                    continue;
                }
                string target;
                if (AutoFetchName)
                {
                    string name = "", line = "";
                    try
                    {
                        using var probe = new PeerClient();
                        await probe.ConnectAsync(host, port, Constants.RoleProbe, ct);
                        (name, line) = await probe.ProbeDeviceAsync(ct);
                    }
                    catch (Exception ex) { LogError($"从 {host} 读取设备配置失败: {ex.Message}"); }
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        LogError($"未能从 {host} 的 BBK 配置获取电脑名称，使用默认名称。");
                        name = ComputerNameFor(host);
                    }
                    else Log($"已从 {host} 获取设备配置：DeviceNo={name}，LineNo={line}");
                    target = BackupTargetPath(dest, name, line);
                }
                else target = BackupTargetPath(dest, ComputerNameFor(host));

                try
                {
                    bool preBackupBat = RunPreBackupBat;
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RolePull, ct, preBackupBat,
                        backupFilter: ReadBackupOptions());
                    LogDivider();
                    Log($"已连接远端 {host}:{port}，开始拉取备份 → {target} ...");
                    _opLabel = "备份";
                    // Log 日期与忽略规则已随握手下发，远端先筛选；本地不再重复筛选
                    await client.BackupPullAsync(target, ReadBackupOptions(), preBackupBat, Log, OnFileProgress, OnTotal,
                        m => LogError("跳过: " + m), ct, sourceFiltered: true, LogError);
                    okIps.Add(host);
                    LogSuccess($"备份完成: {host}");
                    await CompressAndRemoveFolderAsync(target, ShouldCompress);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed.Add($"{host} - {ex.Message}");
                    LogError($"备份失败 {host}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("备份已取消，已通知远端中止");
            StatusText = "备份已取消";
        }
        finally { EndHostOperation(okIps, port); }
        await FinishHostOperationAsync("远程备份", "远程备份", okIps, failed, hosts.Count, port, "备份");
    }

    private async Task BackupFromShareAsync(string dest)
    {
        string? unc = TryGetShareUnc();
        if (unc == null) return;
        // 共享连接与相关提示都通过后再执行 PostgreSQL 备份前脚本
        await RunPreBackupBatAsync();
        string target;
        if (AutoFetchName)
        {
            var (name, line) = DeviceConfig.ReadFromRoot(unc);
            if (string.IsNullOrWhiteSpace(name))
            {
                LogError($"未能从共享 {unc} 的 BBK 配置获取电脑名称，使用默认名称。");
                name = ComputerNameFor(ShareIp.Trim());
            }
            else Log($"已从共享 {unc} 获取设备配置：DeviceNo={name}，LineNo={line}");
            target = BackupTargetPath(dest, name, line);
        }
        else target = BackupTargetPath(dest, ComputerNameFor(ShareIp.Trim()));

        BackupEngine engine;
        try { engine = new BackupEngine(unc, target, ReadBackupOptions()); }
        catch (Exception ex) { _dialogs.Warning(ex.Message, "错误"); return; }

        StartBusy("正在从共享备份 BBK...");
        try
        {
            var files = engine.Plan();
            _totalCount = files.Count;
            _opLabel = "备份";
            OnTotal(files.Count);
            LogDivider();
            Log($"开始从共享 {unc} 备份 → {target}(共 {files.Count} 项)...");
            if (files.Count == 0) Log("没有需要备份的文件：目标位置已是最新。");
            await engine.RunAsync(OnFileProgress, m => LogError("跳过: " + m), _coordinator.Token);
            LogBackupSummary("共享备份", engine, files.Count);
            StatusText = files.Count == 0 ? "备份完成（无需备份文件）" : "备份完成";
            await CompressAndRemoveFolderAsync(target, ShouldCompress);
        }
        catch (OperationCanceledException)
        {
            Log("备份已取消");
            StatusText = "备份已取消";
        }
        catch (Exception ex)
        {
            LogError("备份失败: " + ex.Message);
            _dialogs.Error("备份失败: " + ex.Message, "错误");
        }
        finally
        {
            EndBusy();
            LogSingleOperationSummary("共享备份", true);
        }
    }

    private string? TryGetShareUnc()
    {
        string ip = ShareIp.Trim();
        if (string.IsNullOrWhiteSpace(ip))
        {
            _dialogs.Info("请输入远端电脑的 IP。", "提示");
            return null;
        }
        string unc = SmbHelper.MakeUnc(ip, SharePath.Trim());
        string user = ShareUser.Trim(), password = SharePassword;
        try
        {
            SmbHelper.Connect(unc, string.IsNullOrEmpty(user) ? null : user, password);
            Log("已连接共享 " + unc);
            return unc;
        }
        catch (Exception ex)
        {
            LogError("连接共享失败: " + ex.Message);
            _dialogs.Error("连接共享失败: " + ex.Message, "错误");
            return null;
        }
    }

    private void ShareTest()
    {
        string? unc = TryGetShareUnc();
        if (unc == null) return;
        try
        {
            string first = Directory.EnumerateDirectories(unc).FirstOrDefault()
                           ?? Directory.EnumerateFiles(unc).FirstOrDefault() ?? "";
            Log(string.IsNullOrEmpty(first)
                ? $"连接成功 {unc}，但共享中没有可访问的内容。"
                : $"连接成功 {unc}，可访问。(示例: {first})");
        }
        catch (Exception ex)
        {
            LogError("连接成功但无法读取共享内容: " + ex.Message);
            _dialogs.Error("连接成功但无法读取共享内容: " + ex.Message, "错误");
        }
    }

    private async Task SyncAsync()
    {
        if (!TryEnterOperation()) return;
        try { await DoSyncAsync(); }
        finally { ExitOperation(); }
    }

    private async Task DoSyncAsync()
    {
        if (SelectedTabIndex == (int)MainTab.Transfer)
        {
            if (IsTransferFromRemote) { await TransferFromRemoteAsync(); return; }
            await TransferToRemoteAsync();
            return;
        }
        if (IsSyncShareSelected) { await SyncFromShareAsync(); return; }
        if (!TryGetHostsAndPort(out var hosts, out int port)) return;
        if (IsReceiveSelected && hosts.Count > 1)
        {
            _dialogs.Warning("拉取更新时只能选择一个远端 IP。", "错误");
            return;
        }
        hosts = ExcludeLocalHosts(hosts);
        if (hosts == null) return;
        bool push = IsPushSelected;
        string deviceInfo = hosts.Count == 1 ? await GetDeviceInfo(hosts[0]) : "";
        string msgPush = $"将本机BBK按本机更新规则推送更新至远端电脑[{PeerIp}]{deviceInfo}\n（Bin 整体替换、同步前备份、备份的 Log/忽略规则均按本机勾选执行）";
        string msgPull = $"将远端电脑[{PeerIp}]{deviceInfo}的BBK按本机更新规则拉取更新至本机\n（接收更新与同步前备份均按本机勾选执行）";
        if (!_dialogs.Confirm("确认开始更新？\n\n" + (push ? msgPush : msgPull), "确认")) return;

        string root = RootPath.Trim();
        var options = ReadSyncOptions();
        StartBusy(push ? "正在推送更新到远端..." : "正在从远端更新本机...");
        var ct = _coordinator.Token;
        _opLabel = "更新";
        if (!push) await RunPreBackupBatAsync();
        var okIps = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                if (!await EnsureRemoteReadyAsync(host, port))
                {
                    failed.Add($"{host} - 连接测试失败");
                    continue;
                }
                try
                {
                    using var client = new PeerClient();
                    // 推送时把本机更新规则下发给远端执行；接收更新时本地引擎已按本机规则计算
                    await client.ConnectAsync(host, port, push ? Constants.RolePush : Constants.RolePull, ct,
                        binReplace: push && options.FullReplaceBin,
                        backupBeforeSync: push && BackupBeforeSync,
                        backupFilter: push ? ReadBackupOptions() : null);
                    List<string> updated;
                    if (push)
                    {
                        LogDivider();
                        Log($"以本机为源连接远端 {host}:{port} 成功，推送 {root} 的文件清单...");
                        updated = await client.PushAsync(root, options, Log, OnFileProgress, OnTotal,
                            m => LogError("远端电脑: " + m), ct);
                    }
                    else
                    {
                        LogDivider();
                        Log($"以远端为源连接远端 {host}:{port} 成功，更新到本机 {root} ...");
                        if (BackupBeforeSync && !string.IsNullOrWhiteSpace(BackupDestPath.Trim()))
                        {
                            string dest = Path.Combine(BackupDestPath.Trim(), $"BBK_AutoBackup_{DateTime.Now:yyyyMMdd}");
                            Log($"先备份本机 BBK 到 {dest}");
                            try
                            {
                                var be = new BackupEngine(root, dest, ReadBackupOptions());
                                var bakFiles = be.Plan();
                                await be.RunAsync(OnFileProgress, m => LogError("同步前备份跳过: " + m), ct);
                                Log($"同步前备份完成({bakFiles.Count} 项)");
                                await CompressAndRemoveFolderAsync(dest, ShouldCompress);
                            }
                            catch (ArgumentException)
                            {
                                Log($"{BackupDestPath.Trim()} 为空或与同步目录相同，跳过同步前备份。");
                            }
                            catch (Exception ex) { LogError("同步前备份失败: " + ex.Message); }
                        }
                        updated = await client.PullAsync(root, options, Log, OnFileProgress, OnTotal,
                            m => LogError("传输失败，跳过: " + m), ct);
                    }
                    okIps.Add(host);
                    LogSuccess($"同步完成: {host}");
                    LogUpdatedFiles(updated, push ? $"远端 {host}" : "本机");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed.Add($"{host} - {ex.Message}");
                    LogError($"同步失败 {host}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("已取消，已通知远端中止本次更新");
            StatusText = "更新已取消";
        }
        finally { EndHostOperation(okIps, port); }
        await FinishHostOperationAsync("远端同步", "同步", okIps, failed, hosts.Count, port);
    }

    private async Task SyncFromShareAsync()
    {
        string? unc = TryGetShareUnc();
        if (unc == null) return;
        string root = RootPath.Trim();
        var options = ReadSyncOptions();
        StartBusy("正在从共享更新本机...");
        var ct = _coordinator.Token;
        _opLabel = "更新";
        try
        {
            await RunPreBackupBatAsync();
            var remote = FileLister.ListFiles(unc);
            var engine = new SyncEngine(root, options);
            engine.Plan(remote);
            OnTotal(engine.NeedList.Count);
            LogDivider();
            Log($"共享 {unc} 现共有 {remote.Count} 个文件，需要更新 {engine.NeedList.Count} 个...");
            if (engine.NeedList.Count == 0)
            {
                Log("所有文件与共享相同，无需更新。");
                StatusText = "共享同步完成";
                return;
            }
            if (engine.NeedList.Count < 10)
                foreach (var p in engine.NeedList) Log("  ← " + p);
            await engine.CopyNeededFromAsync(unc, OnFileProgress, m => LogError("跳过: " + m), ct);
            LogSuccess("共享同步完成");
            LogUpdatedFiles(engine.Applied, "本机");
            StatusText = "共享同步完成";
        }
        catch (OperationCanceledException)
        {
            Log("已取消");
            StatusText = "更新已取消";
        }
        catch (Exception ex)
        {
            LogError("共享同步失败: " + ex.Message);
            _dialogs.Error("共享同步失败: " + ex.Message, "错误");
        }
        finally
        {
            EndBusy();
            LogSingleOperationSummary("共享同步", true);
        }
    }

    private async Task TransferToRemoteAsync()
    {
        string itemPath = TransferPath.Trim();
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            _dialogs.Info("请先选择要同步的文件或文件夹。", "提示");
            return;
        }
        if (!Directory.Exists(itemPath) && !File.Exists(itemPath))
        {
            _dialogs.Warning("路径无效或不存在：" + itemPath, "错误");
            return;
        }
        if (!TryGetHostsAndPort(out var hosts, out int port)) return;
        hosts = ExcludeLocalHosts(hosts);
        if (hosts == null) return;

        bool isDir = Directory.Exists(itemPath);
        bool updateMode = TransferUpdate;
        string localDevice = DeviceConfig.ReadFromRoot(RootPath.Trim()).Name;
        if (string.IsNullOrWhiteSpace(localDevice)) localDevice = Environment.MachineName;
        string deviceInfo = hosts.Count == 1 ? await GetDeviceInfo(hosts[0]) : "";
        string modeDesc = updateMode
            ? "更新模式：会先将远端原文件/文件夹重命名为 名称_设备信息_日期，再覆盖到相同路径。"
            : "拷贝模式：不改动远端原文件，新文件以 名称_" + localDevice + "_日期 保存。";
        if (!_dialogs.Confirm("确认开始同步？\n\n远端：" + PeerIp + deviceInfo + "\n同步内容：" + itemPath +
                              "（" + (isDir ? "文件夹" : "文件") + "）\n\n" + modeDesc, "确认")) return;

        StartBusy("正在传输文件到远端...");
        var ct = _coordinator.Token;
        _opLabel = "传输";
        var okIps = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                if (!await EnsureRemoteReadyAsync(host, port))
                {
                    failed.Add($"{host} - 连接测试失败");
                    continue;
                }
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RoleTransfer, ct);
                    LogDivider();
                    Log($"传输到 {host}:{port}({(updateMode ? "更新模式" : "拷贝模式")})...");
                    await client.TransferAsync(itemPath, isDir, TransferSameSkip, updateMode, localDevice,
                        TransferDeleteSameName, Log, OnFileProgress, OnTotal, m => LogError("远端电脑: " + m), ct);
                    okIps.Add(host);
                    LogSuccess($"传输完成: {host}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed.Add($"{host} - {ex.Message}");
                    LogError($"传输失败 {host}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("已取消，已通知远端中止本次传输");
            StatusText = "传输已取消";
        }
        finally { EndHostOperation(okIps, port); }
        await FinishHostOperationAsync("文件传输", "传输", okIps, failed, hosts.Count, port);
    }

    private async Task TransferFromRemoteAsync()
    {
        string itemPath = TransferPath.Trim();
        if (string.IsNullOrWhiteSpace(itemPath))
        {
            _dialogs.Info("请先输入要拉取的远端文件或文件夹路径。", "提示");
            return;
        }
        if (!TryGetHostsAndPort(out var hosts, out int port)) return;
        hosts = ExcludeLocalHosts(hosts);
        if (hosts == null) return;

        string deviceInfo = "";
        string deviceName = "";
        if (hosts.Count == 1)
        {
            var (name, line) = await ProbeRemoteAsync(hosts[0], port);
            deviceName = name;
            deviceInfo = string.IsNullOrWhiteSpace(name) || name == hosts[0]
                ? ""
                : $"[设备 {name}{(string.IsNullOrWhiteSpace(line) ? "" : "/Line" + line)}]";
        }
        bool updateMode = TransferUpdate;
        string localDevice = DeviceConfig.ReadFromRoot(RootPath.Trim()).Name;
        if (string.IsNullOrWhiteSpace(localDevice)) localDevice = Environment.MachineName;
        string modeDesc = updateMode
            ? "更新模式：会先将本地原文件/文件夹重命名为 名称_" + localDevice + "_日期，再拉取到原路径。"
            : "拷贝模式：不修改本地原文件，拉取的文件命名为 名称_" +
              (string.IsNullOrWhiteSpace(deviceName) ? "远端" : deviceName) + "_日期。";
        if (!_dialogs.Confirm("确认从远端同步？\n\n远端：" + PeerIp + deviceInfo + "\n拉取内容：" + itemPath +
                              "\n\n" + modeDesc, "确认")) return;

        StartBusy("正在从远端拉取文件...");
        var ct = _coordinator.Token;
        _opLabel = "拉取";
        var okIps = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                if (!await EnsureRemoteReadyAsync(host, port))
                {
                    failed.Add($"{host} - 连接测试失败");
                    continue;
                }
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RoleTransferPull, ct);
                    LogDivider();
                    Log($"从 {host}:{port} 拉取文件(远端路径: {itemPath}，{(updateMode ? "更新模式" : "拷贝模式")})...");
                    await client.TransferFromRemoteAsync(itemPath, updateMode, deviceName, localDevice, TransferDeleteSameName,
                        Log, OnFileProgress, OnTotal, m => LogError("远端电脑: " + m), ct);
                    okIps.Add(host);
                    LogSuccess($"拉取完成: {host}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed.Add($"{host} - {ex.Message}");
                    LogError($"拉取失败 {host}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("已取消，已通知远端中止本次拉取");
            StatusText = "拉取已取消";
        }
        finally { EndHostOperation(okIps, port); }
        await FinishHostOperationAsync("文件拉取", "拉取", okIps, failed, hosts.Count, port);
    }

    private void LogBackupSummary(string what, BackupEngine engine, int total)
    {
        int copied = engine.CopiedCount, skipped = engine.SkippedCount;
        Log(copied == 0
            ? $"{what}完成：共 {total} 项，全部与备份目标相同，无需传输。"
            : $"{what}完成：共 {total} 项，复制 {copied} 项" +
              (skipped > 0 ? $"，跳过相同 {skipped} 项" : "") + "。");
    }

    /// <summary>更新完成后逐条列出本次实际更新的文件完整路径。</summary>
    private void LogUpdatedFiles(IReadOnlyList<string> updated, string scope)
    {
        if (updated.Count == 0)
        {
            Log($"{scope}没有文件需要更新。");
            return;
        }
        Log($"{scope}共更新 {updated.Count} 个文件，完整路径如下：");
        foreach (var path in updated) Log("  " + path);
    }

    /// <summary>多主机操作收尾：状态栏结果、失败弹窗、带设备名的日志汇总。</summary>
    private async Task FinishHostOperationAsync(string opName, string statusLabel, List<string> okIps,
        List<string> failed, int hostCount, int port, string? failNoun = null)
    {
        if (okIps.Count > 0)
            StatusText = hostCount > 1 ? $"{statusLabel}完成({okIps.Count}/{hostCount} 台)" : $"{statusLabel}完成";
        else if (failed.Count > 0)
            StatusText = $"{statusLabel}失败({failed.Count} 台未完成)";
        if (failed.Count > 0)
            _dialogs.Warning($"以下电脑{failNoun ?? statusLabel}失败：\n" + string.Join("\n", failed), "部分失败");
        await LogHostSummaryAsync(opName, okIps, failed, port);
    }

    /// <summary>多主机操作结束：有成功主机时记录 IP 历史，并解除忙碌状态。</summary>
    private void EndHostOperation(List<string> okIps, int port)
    {
        if (okIps.Count > 0) SaveIpHistory(port);
        EndBusy();
    }

    private async Task UpdateProgramAsync()
    {
        string host = PeerIp.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            _dialogs.Info("请输入远端 IP 或范围。", "提示");
            return;
        }
        int port = int.TryParse(PeerPortText.Trim(), out int p) ? p : Constants.DefaultPort;

        string exePath = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            _dialogs.Error("无法获取当前程序路径。", "错误");
            return;
        }

        List<string> hosts;
        try { hosts = IpHelper.ExpandIps(host); }
        catch (FormatException ex) { _dialogs.Warning(ex.Message, "错误"); return; }
        var filtered = ExcludeLocalHosts(hosts);
        if (filtered == null) return;
        hosts = filtered;

        if (!_dialogs.Confirm("确认开始升级远端程序？\n\n目标电脑：" + host, "确认")) return;
        var failed = new List<string>();
        var okIps = new List<string>();

        StartBusy("正在更新远端程序...");
        Log("========== 开始更新远端程序 ==========");
        Log($"目标: {host}，端口: {port}");
        try
        {
            foreach (string h in hosts)
            {
                if (!await EnsureRemoteReadyAsync(h, port))
                {
                    failed.Add(h);
                    continue;
                }
                try
                {
                    Log($"--- 连接 {h}:{port} ---");
                    using var client = new PeerClient();
                    await client.ConnectAsync(h, port, Constants.RoleUpdate, _coordinator.Token);
                    Log("已连接，正在发送更新文件...");
                    await client.UpdateAsync(exePath, m => Log(m), _coordinator.Token);
                    okIps.Add(h);
                    LogSuccess($"{h} 更新完成");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogError($"{h} 更新失败: {ex.Message}");
                    failed.Add(h);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("操作已取消，已通知远端中止");
            StatusText = "程序更新已取消";
        }
        finally { EndHostOperation(okIps, port); }
        await FinishHostOperationAsync("程序升级", "更新", okIps, failed, hosts.Count, port);
    }

    /// <summary>远端操作前先测试连接；失败时记录日志并取消对该电脑的操作。</summary>
    private async Task<bool> EnsureRemoteReadyAsync(string host, int port)
    {
        var (ok, detail) = await TestOneHostAsync(host, port);
        if (ok) return true;
        LogError($"[连接测试失败] {host}:{port} {detail}，已取消对该电脑的操作。");
        return false;
    }

    private async Task TestConnectionAsync()
    {
        List<string> hosts;
        try { hosts = IpHelper.ExpandIps(PeerIp ?? ""); }
        catch (FormatException ex) { _dialogs.Warning(ex.Message, "错误"); return; }
        if (hosts.Count == 0)
        {
            _dialogs.Info("请输入远端 IP 或范围。", "提示");
            return;
        }
        if (!TryGetPeerPort(out int port))
        {
            _dialogs.Warning("端口无效，请输入 1~65535 之间的数字。", "错误");
            return;
        }

        _isTestRunning = true;
        NotifyCommandState();
        var okList = new List<string>();
        var failList = new List<string>();
        try
        {
            LogDivider();
            Log($"开始测试 {hosts.Count} 个远端地址（端口 {port}）...");
            foreach (string host in hosts)
            {
                var (ok, detail) = await TestOneHostAsync(host, port);
                if (ok)
                {
                    okList.Add(host);
                    LogSuccess($"[成功] {host}:{port} {detail}");
                }
                else
                {
                    failList.Add(host);
                    LogError($"[失败] {host}:{port} {detail}");
                }
            }
            Log($"测试完成：成功 {okList.Count} 个，失败 {failList.Count} 个。");
        }
        finally
        {
            _isTestRunning = false;
            NotifyCommandState();
        }

        string summary = failList.Count == 0
            ? $"全部 {okList.Count} 个地址连接正常，远端协议应答正确。"
            : $"成功 {okList.Count} 个，失败 {failList.Count} 个：\n" +
              string.Join("\n", failList.Select(h => h + ":" + port));
        if (failList.Count == 0) _dialogs.Info(summary, "测试结果");
        else _dialogs.Warning(summary, "测试结果");
        await LogHostSummaryAsync("测试连接", okList, failList, port);
    }

    /// <summary>测试单个远端地址：先测 TCP 连通性(1 秒)，再校验协议应答(1 秒)。</summary>
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
            string info = string.IsNullOrWhiteSpace(name)
                ? ""
                : $"[设备 {name}{(string.IsNullOrWhiteSpace(line) ? "" : "/Line" + line)}]";
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
}
