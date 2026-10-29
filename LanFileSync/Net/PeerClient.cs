using System.IO;
using System.Net.Sockets;

namespace LanFileSync;

public sealed class PeerClient : IDisposable
{
    private TcpClient? _tcp;
    private PeerConnection? _conn;

    /// <summary>
    /// 连接远端并发送握手。可携带本机更新规则（Bin 整体替换、同步前备份、备份过滤），
    /// 让远端按本机勾选项执行，而不是按远端界面的设置执行。
    /// </summary>
    public async Task ConnectAsync(string host, int port, string role, CancellationToken ct, bool preBackupBat = false,
        bool binReplace = false, bool backupBeforeSync = false, BackupOptions? backupFilter = null)
    {
        _tcp = new TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(Constants.ConnectTimeoutSeconds));
        await _tcp.ConnectAsync(host, port, connectCts.Token);
        _conn = new PeerConnection(_tcp);
        object? filter = backupFilter == null
            ? null
            : new
            {
                logRule = backupFilter.ApplyLogRule,
                logDays = backupFilter.LogRetentionDays,
                ignore = backupFilter.IgnoreRegexes,
            };
        await _conn.SendJsonAsync(new { hello = true, role, preBackupBat, binReplace, backupBeforeSync, backupFilter = filter }, ct);
    }

    /// <summary>取消时尽力通知远端中止操作；即使发送失败，断开连接也会让远端结束等待。</summary>
    private async Task SendCancelAsync()
    {
        if (_conn == null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _conn.SendJsonAsync(new { op = "cancel" }, cts.Token);
        }
        catch { }
    }

    /// <summary>
    /// 读取远端帧直到「需要文件清单(need)」：
    /// 期间收到的 blog（远端备份完成日志）逐条显示，err 直接抛出。
    /// </summary>
    private async Task<System.Text.Json.JsonElement> RecvNeedFrameAsync(CancellationToken ct, Action<string> log)
    {
        while (true)
        {
            var frame = await _conn!.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");
            string op = frame.GetProperty("op").GetString()!;
            if (op == "blog")
            {
                foreach (var m in frame.GetProperty("msgs").EnumerateArray())
                    log("[远端备份] " + (m.GetString() ?? ""));
                continue;
            }
            if (op == "cancel")
                throw new OperationCanceledException("主控端已取消操作");
            if (op == "err")
                throw new InvalidOperationException(frame.GetProperty("msg").GetString());
            return frame;
        }
    }

    public async Task<List<string>> PullAsync(
        string root,
        SyncOptions options,
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        Action<string> onError,
        CancellationToken ct)
    {
        try
        {
            var engine = new SyncEngine(root, options);
            var list = new List<FileEntry>();

            await TargetSide.ReceiveManifestAsync(_conn!, ct, list);
            log($"已收到远端文件清单({list.Count} 项)，按本机规则计算...");

            engine.Plan(list);
            onTotal(engine.NeedList.Count);

            if (engine.NeedList.Count == 0)
                log("本机所有文件与远端相同，无需更新。");
            else if (engine.NeedList.Count < 10)
                foreach (var p in engine.NeedList) log("  ← " + p);

            await _conn!.SendJsonAsync(new { op = "req", paths = engine.NeedList }, ct);
            await engine.ReceiveAndApplyAsync(_conn, onFile, onError, ct);
            return engine.Applied.ToList();
        }
        catch (OperationCanceledException)
        {
            await SendCancelAsync();
            throw;
        }
    }

    public async Task<List<string>> PushAsync(
        string root,
        SyncOptions options,
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        Action<string> onError,
        CancellationToken ct)
    {
        try
        {
            await SourceSide.SendManifestAsync(_conn!, root, ct);
            log("本机文件清单已发送，等待远端按本机下发的更新规则计算...");

            var frame = await RecvNeedFrameAsync(ct, log);

            var paths = frame.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToList();
            onTotal(paths.Count);
            log(paths.Count == 0 ? "远端所有文件与本机一致，没有需要更新的文件。" : $"远端需要 {paths.Count} 个文件，开始发送...");
            if (paths.Count > 0 && paths.Count < 10)
                foreach (var p in paths) log("  → " + p);

            await SourceSide.SendRequestedFilesAsync(_conn, root, paths, onFile, ct);

            var resp = await _conn.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");
            if (resp.GetProperty("op").GetString() == "err")
                throw new InvalidOperationException(resp.GetProperty("msg").GetString());

            foreach (var m in resp.GetProperty("msgs").EnumerateArray())
                onError?.Invoke(m.GetString() ?? "");

            // 远端实际写入的文件完整路径
            var applied = new List<string>();
            if (resp.TryGetProperty("files", out var filesEl))
                foreach (var f in filesEl.EnumerateArray())
                {
                    string? p = f.GetString();
                    if (!string.IsNullOrEmpty(p)) applied.Add(p);
                }

            log("远端已应用完成");
            return applied;
        }
        catch (OperationCanceledException)
        {
            await SendCancelAsync();
            throw;
        }
    }

    public async Task BackupPullAsync(
        string dest,
        BackupOptions options,
        bool runPreBackupBat,
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        Action<string> onError,
        CancellationToken ct,
        bool sourceFiltered = false,
        Action<string>? logError = null)
    {
        try
        {
            if (runPreBackupBat)
            {
                var bat = await _conn!.RecvJsonAsync(ct)
                    ?? throw new EndOfStreamException("连接已断开");
                string op = bat.GetProperty("op").GetString()!;
                if (op == "err")
                    throw new InvalidOperationException(bat.GetProperty("msg").GetString());
                if (op != "bat")
                    throw new InvalidOperationException("未知消息: " + op);
                string msg = bat.GetProperty("msg").GetString() ?? "";
                if (bat.GetProperty("ok").GetBoolean())
                    log("远端备份前脚本: " + msg);
                else
                    (logError ?? log)("远端备份前脚本失败，已继续备份: " + msg);
            }

            // Log 日期与忽略规则已在远端筛选完成，本地不再重复筛选，只按大小/修改日期比对
            var engineOptions = sourceFiltered
                ? new BackupOptions { ApplyLogRule = false, IgnoreRegexes = Array.Empty<string>() }
                : options;
            var engine = new BackupSyncEngine(dest, engineOptions);
            var list = new List<FileEntry>();

            await TargetSide.ReceiveManifestAsync(_conn!, ct, list);
            log($"已收到远端文件清单({list.Count} 项)，按备份规则计算...");

            engine.Plan(list);
            onTotal(engine.NeedList.Count);

            if (engine.NeedList.Count == 0)
                log("所有文件与备份目标相同，没有需要备份的文件。");
            else if (engine.NeedList.Count < 10)
                foreach (var p in engine.NeedList) log("  ← " + p);

            await _conn!.SendJsonAsync(new { op = "req", paths = engine.NeedList }, ct);
            await engine.ReceiveAndApplyAsync(_conn, onFile, onError, ct);
        }
        catch (OperationCanceledException)
        {
            await SendCancelAsync();
            throw;
        }
    }

    public async Task<(string Name, string Line)> ProbeDeviceAsync(CancellationToken ct)
    {
        var frame = await _conn!.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op = frame.GetProperty("op").GetString()!;
        if (op == "err")
            throw new InvalidOperationException(frame.GetProperty("msg").GetString());
        if (op != "cfg")
            throw new InvalidOperationException("未知消息: " + op);
        return (frame.GetProperty("name").GetString() ?? "", frame.GetProperty("line").GetString() ?? "");
    }

    public async Task TransferAsync(
        string itemPath,
        bool isDir,
        bool sameSkip,
        bool updateMode,
        string deviceTag,
        bool deleteSameName,
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        Action<string> onError,
        CancellationToken ct)
    {
        try
        {
            await TransferSide.SendTransferManifestAsync(_conn!, itemPath, isDir, sameSkip, updateMode, deviceTag, deleteSameName, ct);
            log("传输文件清单已发送，等待远端按相同路径计算...");

            var frame = await RecvNeedFrameAsync(ct, log);

            var paths = frame.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToList();
            onTotal(paths.Count);
            log(paths.Count == 0 ? "远端相应文件完全相同，没有需要传输的文件。" : $"远端需要 {paths.Count} 个文件，开始发送...");

            await TransferSide.SendRequestedFilesAsync(_conn, paths, onFile, ct);

            var resp = await _conn.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");
            string rop = resp.GetProperty("op").GetString()!;
            if (rop == "err")
                throw new InvalidOperationException(resp.GetProperty("msg").GetString());

            foreach (var m in resp.GetProperty("msgs").EnumerateArray())
                onError?.Invoke(m.GetString() ?? "");

            log("远端已应用完成");
        }
        catch (OperationCanceledException)
        {
            await SendCancelAsync();
            throw;
        }
    }

    public async Task TransferFromRemoteAsync(
        string remotePath,
        bool updateMode,
        string remoteDevice,
        string localDevice,
        bool deleteSameName,
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        Action<string> onError,
        CancellationToken ct)
    {
        try
        {
            // 请求远端发送文件清单(isDir 由远端根据其文件系统自行判断)
            await _conn!.SendJsonAsync(new { op = "tinit", p = remotePath.Replace('\\', '/') }, ct);
            log("已请求远端文件清单，等待远端计算...");

            var list = new List<FileEntry>();
            await TargetSide.ReceiveManifestAsync(_conn, ct, list);
            log($"已收到远端文件清单({list.Count} 项)...");

            // 从远端同步不修改本地文件，拉取所有文件
            var needPaths = list.Select(e => e.RelPath).ToList();
            onTotal(needPaths.Count);
            log(needPaths.Count == 0 ? "远端没有匹配的文件，无需拉取。" : $"准备拉取 {needPaths.Count} 个文件...");

        // 正常命名只带到年月日；仅备份命名重复时，旧备份才追加 _HHmmss 后缀
        string dateStr = DateTime.Now.ToString("yyyyMMdd");
        string normRemote = remotePath.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        bool dirMode = list.Count != 1
            || !string.Equals(list[0].RelPath.Replace('/', Path.DirectorySeparatorChar), normRemote, StringComparison.OrdinalIgnoreCase);

        // 更新模式：先把本地原文件/文件夹重命名为 名称_本地设备_日期，然后拉取到原路径
        // 拷贝模式：不动原文件，另存为 名称_来源设备_日期
        string saveRoot;
        if (updateMode)
        {
            string localTag = FileHelper.SanitizeName(string.IsNullOrWhiteSpace(localDevice) ? Environment.MachineName : localDevice);
            RenameLocalForUpdate(normRemote, dirMode, localTag, dateStr, log, deleteSameName);
            saveRoot = normRemote;
        }
        else
        {
            string sourceTag = FileHelper.SanitizeName(string.IsNullOrWhiteSpace(remoteDevice) ? "远端" : remoteDevice);
            if (dirMode)
            {
                string parent = Path.GetDirectoryName(normRemote) ?? "";
                string folder = Path.GetFileName(normRemote);
                saveRoot = FileHelper.UniquePath(Path.Combine(parent, $"{folder}_AutoUpdateFrom_{sourceTag}_{dateStr}"), isDir: true);
            }
            else
            {
                saveRoot = Path.GetDirectoryName(normRemote) ?? "";
            }
        }
        Directory.CreateDirectory(dirMode ? saveRoot : (Path.GetDirectoryName(saveRoot) ?? saveRoot));

        // 发送请求列表
        await _conn.SendJsonAsync(new { op = "req", paths = needPaths }, ct);

        int received = 0;
        while (true)
        {
            var frame = await _conn.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");
            string op = frame.GetProperty("op").GetString()!;
            if (op == "done") break;
            if (op == "cancel") throw new OperationCanceledException("主控端已取消操作");
            if (op == "skip")
            {
                string skipPath = frame.GetProperty("p").GetString()!;
                onError?.Invoke("远端跳过: " + skipPath + " (" + frame.GetProperty("msg").GetString() + ")");
                continue;
            }
            if (op != "data") throw new InvalidOperationException("未知消息: " + op);

            string path = frame.GetProperty("p").GetString()!;
            long size = frame.GetProperty("s").GetInt64();
            long mtimeTicks = frame.TryGetProperty("t", out var tEl) ? tEl.GetInt64() : 0;
            string localPath = path.Replace('/', Path.DirectorySeparatorChar);

            string savedPath;
            if (dirMode)
            {
                string rel = localPath.StartsWith(normRemote + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? localPath[(normRemote.Length + 1)..]
                    : Path.GetFileName(localPath);
                savedPath = Path.Combine(saveRoot, rel);
            }
            else if (updateMode)
            {
                savedPath = normRemote;
            }
            else
            {
                string sourceTag = FileHelper.SanitizeName(string.IsNullOrWhiteSpace(remoteDevice) ? "远端" : remoteDevice);
                string baseName = Path.GetFileNameWithoutExtension(localPath);
                string ext = Path.GetExtension(localPath);
                savedPath = FileHelper.UniquePath(Path.Combine(saveRoot, $"{baseName}_AutoUpdateFrom_{sourceTag}_{dateStr}{ext}"), isDir: false);
            }

            string? savedDir = Path.GetDirectoryName(savedPath);
            if (!string.IsNullOrEmpty(savedDir)) Directory.CreateDirectory(savedDir);
            using (var fs = new FileStream(savedPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024))
            {
                var buf = new byte[128 * 1024];
                long remaining = size;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    await _conn.ReadRawAsync(buf, toRead, ct);
                    await fs.WriteAsync(buf.AsMemory(0, toRead), ct);
                    remaining -= toRead;
                }
            }

            if (mtimeTicks > 0)
                try { File.SetLastWriteTimeUtc(savedPath, new DateTime(mtimeTicks, DateTimeKind.Utc)); } catch { }

            received++;
            onFile?.Invoke(savedPath, size);
            if (received % 10 == 0 || received == needPaths.Count)
                log($"已拉取 {received}/{needPaths.Count} 个文件...");
        }

        var resp = await _conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string rop = resp.GetProperty("op").GetString()!;
        if (rop == "err")
            throw new InvalidOperationException(resp.GetProperty("msg").GetString());

        foreach (var m in resp.GetProperty("msgs").EnumerateArray())
            onError?.Invoke(m.GetString() ?? "");

            log($"从远端拉取完成({received} 个文件保存到 {saveRoot})");
        }
        catch (OperationCanceledException)
        {
            await SendCancelAsync();
            throw;
        }
    }

    /// <summary>更新模式拉取前，处理本地已有文件/文件夹：deleteSame=true 时直接删除旧备份（保持原始路径）；false 时把同名旧备份重命名为 _HHmmss 后缀保留。</summary>
    private static void RenameLocalForUpdate(string itemPath, bool isDir, string deviceTag, string date, Action<string> log, bool deleteSame)
    {
        // date 只到年月日；仅备份命名重复时才给旧备份追加 _HHmmss 后缀
        if (isDir)
        {
            if (!Directory.Exists(itemPath)) { log("本地文件夹不存在，无需重命名: " + itemPath); return; }
            string parent = Path.GetDirectoryName(itemPath) ?? "";
            string folder = Path.GetFileName(itemPath);
            string renameTo = Path.Combine(parent, $"{folder}_AutoBackup_{date}");
            if (Directory.Exists(renameTo))
            {
                if (deleteSame)
                {
                    log("存在同名备份，先删除: " + renameTo);
                    Directory.Delete(renameTo, recursive: true);
                }
                else
                {
                    string oldBackup = renameTo;
                    string newBackup = FileHelper.UniquePath(
                        Path.Combine(parent, $"{folder}_AutoBackup_{DateTime.Now:yyyyMMdd_HHmmss}"), isDir: true);
                    log("存在同名备份，重命名旧备份保留: " + oldBackup + " → " + newBackup);
                    Directory.Move(oldBackup, newBackup);
                }
            }
            log($"更新前重命名本地文件夹: {itemPath} → {renameTo}");
            Directory.Move(itemPath, renameTo);
        }
        else
        {
            if (!File.Exists(itemPath)) { log("本地文件不存在，无需重命名: " + itemPath); return; }
            string dirOf = Path.GetDirectoryName(itemPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(itemPath);
            string ext = Path.GetExtension(itemPath);
            string renameTo = Path.Combine(dirOf, $"{baseName}_AutoBackup_{date}{ext}");
            if (File.Exists(renameTo))
            {
                if (deleteSame)
                {
                    log("存在同名备份，先删除: " + renameTo);
                    File.Delete(renameTo);
                }
                else
                {
                    string oldBackup = renameTo;
                    string newBackup = FileHelper.UniquePath(
                        Path.Combine(dirOf, $"{baseName}_AutoBackup_{DateTime.Now:yyyyMMdd_HHmmss}{ext}"), isDir: false);
                    log("存在同名备份，重命名旧备份保留: " + oldBackup + " → " + newBackup);
                    File.Move(oldBackup, newBackup);
                }
            }
            log($"更新前重命名本地文件: {itemPath} → {renameTo}");
            File.Move(itemPath, renameTo);
        }
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _tcp?.Dispose();
    }

    public async Task<(string Msg, int Remaining)> ProcessCloseAsync(string[] killNames, CancellationToken ct)
    {
        await _conn!.SendJsonAsync(new { op = "aclose", names = killNames }, ct);
        var frame = await _conn!.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op = frame.GetProperty("op").GetString()!;
        if (op == "err")
            throw new InvalidOperationException(frame.GetProperty("msg").GetString());
        if (op != "ok")
            throw new InvalidOperationException("未知消息: " + op);
        return (frame.GetProperty("msg").GetString() ?? "", frame.GetProperty("remaining").GetInt32());
    }

    public async Task<(string Msg, int Running)> ProcessOpenAsync(string exePath, CancellationToken ct)
    {
        await _conn!.SendJsonAsync(new { op = "aopen", path = exePath }, ct);
        var frame = await _conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op = frame.GetProperty("op").GetString()!;
        if (op == "err")
            throw new InvalidOperationException(frame.GetProperty("msg").GetString());
        if (op != "ok")
            throw new InvalidOperationException("未知消息: " + op);
        return (frame.GetProperty("msg").GetString() ?? "", frame.GetProperty("running").GetInt32());
    }

    public async Task<List<string>> FileListAsync(string suffixes, CancellationToken ct)
    {
        await _conn!.SendJsonAsync(new { op = "flst", suffixes }, ct);
        var paths = new List<string>();
        while (true)
        {
            var frame = await _conn.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");
            string op = frame.GetProperty("op").GetString()!;
            if (op == "err")
                throw new InvalidOperationException(frame.GetProperty("msg").GetString());
            if (op == "fpath")
                paths.Add(frame.GetProperty("path").GetString() ?? "");
            else if (op == "fend")
                return paths;
            else
                throw new InvalidOperationException("未知消息: " + op);
        }
    }

    public async Task UpdateAsync(
        string exePath,
        Action<string> log,
        CancellationToken ct)
    {
        try
        {
            var frame = await _conn!.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");
            if (frame.GetProperty("op").GetString() != "ready")
                throw new InvalidOperationException("远端未就绪");

            log("正在发送程序文件...");
            var fi = new FileInfo(exePath);
            long size = fi.Length;
            await _conn.SendJsonAsync(new { op = "exe", s = size, t = fi.LastWriteTimeUtc.Ticks }, ct);

            var buf = new byte[128 * 1024];
            using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.SequentialScan))
            {
                long remaining = size;
                while (remaining > 0)
                {
                    int read = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), ct);
                    if (read <= 0) throw new EndOfStreamException("读取程序文件失败");
                    await _conn.SendRawAsync(buf, read, ct);
                    remaining -= read;
                }
            }

            log("等待远端执行更新...");
            var resp = await _conn.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");
            string op = resp.GetProperty("op").GetString()!;
            if (op == "err")
                throw new InvalidOperationException(resp.GetProperty("msg").GetString());
            log("远端程序更新完成");
        }
        catch (OperationCanceledException)
        {
            await SendCancelAsync();
            throw;
        }
    }
}