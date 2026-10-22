namespace LanFileSync.ViewModels;

/// <summary>「进程管理」标签页：获取远端文件列表、启动/关闭远端应用程序。</summary>
public partial class MainViewModel
{
    private DateTime _processOpenCooldownUntil = DateTime.MinValue;

    private async Task FetchProcessFilesAsync()
    {
        if (!TryGetHostsAndPort(out var hosts, out int port)) return;

        string savedText = ProcessPath;
        ProcessPathItems = null;
        ProcessStatusText = "正在获取文件列表...";
        StartBusy();
        var ct = _coordinator.Token;
        _opLabel = "获取文件列表";
        var okIps = new List<string>();
        var failed = new List<string>();
        var allPaths = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RoleFileList, ct);
                    LogDivider();
                    Log($"连接 {host}:{port}，获取远端文件列表...");
                    var paths = await client.FileListAsync(ProcessFileSuffixes, ct);
                    allPaths.AddRange(paths);
                    okIps.Add(host);
                    ProcessStatusText = $"{host}: 找到 {paths.Count} 个文件";
                    Log($"找到 {paths.Count} 个文件");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed.Add($"{host} - {ex.Message}");
                    LogError($"获取列表失败 {host}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { Log("已取消"); }
        finally { EndBusy(); }

        if (allPaths.Count > 0)
        {
            var distinct = allPaths.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            ProcessPathItems = distinct;
            ProcessPath = savedText;
            IsProcessPathDropDownOpen = true;
            ProcessStatusText = $"共 {distinct.Count} 个文件，已在下拉列表中供选择";
            Log($"文件列表完成: {distinct.Count} 个文件");
        }
        else
        {
            ProcessPathItems = null;
            ProcessPath = savedText;
            ProcessStatusText = "未找到匹配文件";
            Log("未找到匹配文件");
        }
        if (failed.Count > 0)
            _dialogs.Warning("以下电脑获取失败：\n" + string.Join("\n", failed), "部分失败");
        await LogHostSummaryAsync("获取进程文件列表", okIps, failed, port);
    }

    private async Task CloseProcessAsync()
    {
        if (!TryGetHostsAndPort(out var hosts, out int port)) return;
        string deviceInfo = hosts.Count == 1 ? await GetDeviceInfo(hosts[0]) : "";
        if (!_dialogs.Confirm("确认关闭指定电脑的应用程序？\n\n[" + PeerIp + "]" + deviceInfo +
                              "\n\n程序前缀名：" + ProcessKillNames + "。", "确认")) return;

        string[] killNames = ProcessKillNames
            .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToArray();
        StartBusy();
        var ct = _coordinator.Token;
        _opLabel = "关闭FreeForm";
        var okIps = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RoleProcessClose, ct);
                    LogDivider();
                    Log($"连接 {host}:{port}，关闭远端进程...");
                    var (msg, _) = await client.ProcessCloseAsync(killNames, ct);
                    okIps.Add(host);
                    LogError($"{host}: {msg}");
                    ProcessStatusText = $"[完成] {host}: {msg}";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed.Add($"{host} - {ex.Message}");
                    LogError($"失败 {host}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { Log("已取消"); }
        finally
        {
            if (okIps.Count > 0) SaveIpHistory(port);
            EndBusy();
        }
        if (okIps.Count > 0)
            StatusText = hosts.Count > 1 ? $"关闭完成({okIps.Count}/{hosts.Count} 台)" : "关闭完成";
        if (failed.Count > 0)
            _dialogs.Warning("以下电脑失败：\n" + string.Join("\n", failed), "部分失败");
        await LogHostSummaryAsync("关闭进程", okIps, failed, port);
    }

    private async Task OpenProcessAsync()
    {
        if (DateTime.Now < _processOpenCooldownUntil)
        {
            int sec = (int)(_processOpenCooldownUntil - DateTime.Now).TotalSeconds + 1;
            _dialogs.Info($"请等待 {sec} 秒后再试。", "提示");
            return;
        }
        string exePath = ProcessPath.Trim();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _dialogs.Info("请输入程序路径。", "提示");
            return;
        }
        if (!TryGetHostsAndPort(out var hosts, out int port)) return;
        string deviceInfo = hosts.Count == 1 ? await GetDeviceInfo(hosts[0]) : "";
        if (!_dialogs.Confirm("确认启动指定电脑的应用程序？\n\n[" + PeerIp + "]" + deviceInfo +
                              "\n\n程序路径：" + exePath + "。", "确认")) return;

        _processOpenCooldownUntil = DateTime.Now.AddSeconds(5);
        StartBusy();
        var ct = _coordinator.Token;
        _opLabel = "启动FreeForm";
        var okIps = new List<string>();
        var failed = new List<string>();
        try
        {
            foreach (var host in hosts)
            {
                try
                {
                    using var client = new PeerClient();
                    await client.ConnectAsync(host, port, Constants.RoleProcessOpen, ct);
                    LogDivider();
                    Log($"连接 {host}:{port}[{deviceInfo}]，启动远端应用程序...");
                    var (msg, _) = await client.ProcessOpenAsync(exePath, ct);
                    okIps.Add(host);
                    LogSuccess($"{host}: {msg}");
                    ProcessStatusText = $"[完成] {host}: {msg}";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed.Add($"{host} - {ex.Message}");
                    LogError($"失败 {host}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { Log("已取消"); }
        finally
        {
            if (okIps.Count > 0) SaveIpHistory(port);
            EndBusy();
        }
        if (okIps.Count > 0)
            StatusText = hosts.Count > 1 ? $"启动完成({okIps.Count}/{hosts.Count} 台)" : "启动完成";
        if (failed.Count > 0)
            _dialogs.Warning("以下电脑失败：\n" + string.Join("\n", failed), "部分失败");
        await LogHostSummaryAsync("启动进程", okIps, failed, port);
    }
}
