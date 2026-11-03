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
            await _conn.SendJsonAsync(new { op = ProtocolOp.Cancel }, cts.Token);
        }
        catch { }
    }

    /// <summary>读取下一个 JSON 帧；连接断开时抛出 EndOfStreamException。</summary>
    private async Task<System.Text.Json.JsonElement> ReceiveFrameAsync(CancellationToken ct)
        => await _conn!.RecvJsonAsync(ct) ?? throw new EndOfStreamException("连接已断开");

    /// <summary>取帧中的 op 字段。</summary>
    private static string GetOp(System.Text.Json.JsonElement frame)
        => frame.GetProperty("op").GetString()!;

    /// <summary>若帧为 err 则抛异常，否则把 msgs 中的条目交给调用方。</summary>
    private static void CheckErrorAndRelayMessages(System.Text.Json.JsonElement frame, Action<string>? onError)
    {
        if (GetOp(frame) == ProtocolOp.Error)
            throw new InvalidOperationException(frame.GetProperty("msg").GetString());
        foreach (var m in frame.GetProperty("msgs").EnumerateArray())
            onError?.Invoke(m.GetString() ?? "");
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
            if (op == ProtocolOp.RemoteLog)
            {
                foreach (var m in frame.GetProperty("msgs").EnumerateArray())
                    log("[远端备份] " + (m.GetString() ?? ""));
                continue;
            }
            if (op == ProtocolOp.Cancel)
                throw new OperationCanceledException("主控端已取消操作");
            if (op == ProtocolOp.Error)
                throw new InvalidOperationException(frame.GetProperty("msg").GetString());
            return frame;
        }
    }

    /// <summary>接收更新：发送本机要拉取的相对路径清单，远端回传文件后由本地引擎应用，返回实际更新的文件路径。</summary>
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

            await _conn!.SendJsonAsync(new { op = ProtocolOp.Request, paths = engine.NeedList }, ct);
            await engine.ReceiveAndApplyAsync(_conn, onFile, onError, ct);
            return engine.Applied.ToList();
        }
        catch (OperationCanceledException)
        {
            await SendCancelAsync();
            throw;
        }
    }

    /// <summary>推送更新：发送本机文件清单并接收远端应用结果，返回远端实际更新的文件路径。</summary>
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

            await SourceSide.SendRequestedFilesAsync(_conn!, root, paths, onFile, ct);

            var resp = await ReceiveFrameAsync(ct);
            if (GetOp(resp) == ProtocolOp.Error)
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

    /// <summary>从远端拉取备份到 target；sourceFiltered 表示远端已按备份规则预筛选，无需本地再筛选。</summary>
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
                if (op == ProtocolOp.Error)
                    throw new InvalidOperationException(bat.GetProperty("msg").GetString());
                if (op != ProtocolOp.Bat)
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

            await _conn!.SendJsonAsync(new { op = ProtocolOp.Request, paths = engine.NeedList }, ct);
            await engine.ReceiveAndApplyAsync(_conn, onFile, onError, ct);
        }
        catch (OperationCanceledException)
        {
            await SendCancelAsync();
            throw;
        }
    }

    /// <summary>读取远端 BBK 配置中的设备编号与线别。</summary>
    public async Task<(string Name, string Line)> ProbeDeviceAsync(CancellationToken ct)
    {
        var frame = await _conn!.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op = frame.GetProperty("op").GetString()!;
        if (op == ProtocolOp.Error)
            throw new InvalidOperationException(frame.GetProperty("msg").GetString());
        if (op != ProtocolOp.DeviceConfig)
            throw new InvalidOperationException("未知消息: " + op);
        return (frame.GetProperty("name").GetString() ?? "", frame.GetProperty("line").GetString() ?? "");
    }

    /// <summary>把本机文件或文件夹传输到远端（远端为目标）。</summary>
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

            await TransferSide.SendRequestedFilesAsync(_conn!, paths, onFile, ct);

            var resp = await ReceiveFrameAsync(ct);
            if (GetOp(resp) == ProtocolOp.Error)
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

    /// <summary>
    /// 从远端拉取文件到本机。更新模式先重命名本地原文件/文件夹为备份再覆盖原路径；
    /// 拷贝模式不改动原文件，另存为 名称_来源设备_日期。
    /// </summary>
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
            await _conn!.SendJsonAsync(new { op = ProtocolOp.TransferInit, p = remotePath.Replace('\\', '/') }, ct);
            log("已请求远端文件清单，等待远端计算...");

            var list = new List<FileEntry>();
            await TargetSide.ReceiveManifestAsync(_conn, ct, list);
            log($"已收到远端文件清单({list.Count} 项)...");

            // 从远端同步不修改本地文件，拉取所有文件
            var needPaths = list.Select(e => e.RelPath).ToList();
            onTotal(needPaths.Count);
            log(needPaths.Count == 0 ? "远端没有匹配的文件，无需拉取。" : $"准备拉取 {needPaths.Count} 个文件...");

            string normRemote = remotePath.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
            bool dirMode = list.Count != 1
                || !string.Equals(list[0].RelPath.Replace('/', Path.DirectorySeparatorChar), normRemote, StringComparison.OrdinalIgnoreCase);
            var plan = BuildPullSavePlan(normRemote, dirMode, updateMode, remoteDevice, localDevice,
                DateTime.Now.ToString("yyyyMMdd"), log, deleteSameName);
            Directory.CreateDirectory(plan.DirMode ? plan.Root : (Path.GetDirectoryName(plan.Root) ?? plan.Root));

            // 发送请求列表
            await _conn.SendJsonAsync(new { op = ProtocolOp.Request, paths = needPaths }, ct);

            int received = await ReceivePulledFilesAsync(_conn, needPaths, path => ResolvePulledSavePath(path, plan),
                log, onFile, onError, ct);

            var resp = await ReceiveFrameAsync(ct);
            CheckErrorAndRelayMessages(resp, onError);

            log($"从远端拉取完成({received} 个文件保存到 {plan.Root})");
        }
        catch (OperationCanceledException)
        {
            await SendCancelAsync();
            throw;
        }
    }

    /// <summary>拉取落盘方案：保存根目录、原始远端路径、是否保留目录结构、是否更新模式。</summary>
    private readonly record struct PullSavePlan(
        string Root, string NormalizedRemote, bool DirMode, bool UpdateMode, string SourceTag, string Date);

    /// <summary>更新模式先重命名本地原文件/文件夹为备份；拷贝模式只计算另存目录。</summary>
    private static PullSavePlan BuildPullSavePlan(string normRemote, bool dirMode, bool updateMode,
        string remoteDevice, string localDevice, string dateStr, Action<string> log, bool deleteSameName)
    {
        if (updateMode)
        {
            string localTag = FileHelper.SanitizeName(string.IsNullOrWhiteSpace(localDevice) ? Environment.MachineName : localDevice);
            RenameLocalForUpdate(normRemote, dirMode, localTag, dateStr, log, deleteSameName);
            return new PullSavePlan(normRemote, normRemote, dirMode, true, "", dateStr);
        }

        string sourceTag = FileHelper.SanitizeName(string.IsNullOrWhiteSpace(remoteDevice) ? "远端" : remoteDevice);
        string saveRoot;
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
        return new PullSavePlan(saveRoot, normRemote, dirMode, false, sourceTag, dateStr);
    }

    /// <summary>计算单个远端文件在本机的落盘路径。</summary>
    private static string ResolvePulledSavePath(string remotePath, PullSavePlan plan)
    {
        string localPath = remotePath.Replace('/', Path.DirectorySeparatorChar);
        if (plan.DirMode)
        {
            string rel = localPath.StartsWith(plan.NormalizedRemote + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? localPath[(plan.NormalizedRemote.Length + 1)..]
                : Path.GetFileName(localPath);
            return Path.Combine(plan.Root, rel);
        }
        if (plan.UpdateMode)
            return plan.NormalizedRemote;

        string baseName = Path.GetFileNameWithoutExtension(localPath);
        string ext = Path.GetExtension(localPath);
        return FileHelper.UniquePath(
            Path.Combine(plan.Root, $"{baseName}_AutoUpdateFrom_{plan.SourceTag}_{plan.Date}{ext}"), isDir: false);
    }

    /// <summary>接收 data 帧并逐文件落盘，返回实际写入的文件数。</summary>
    private async Task<int> ReceivePulledFilesAsync(PeerConnection conn, IReadOnlyList<string> needPaths,
        Func<string, string> resolveSavePath, Action<string> log,
        Action<string, long>? onFile, Action<string>? onError, CancellationToken ct)
    {
        int received = 0;
        while (true)
        {
            var frame = await ReceiveFrameAsync(ct);
            string op = GetOp(frame);
            if (op == ProtocolOp.Done) break;
            if (op == ProtocolOp.Cancel) throw new OperationCanceledException("主控端已取消操作");
            if (op == ProtocolOp.Skip)
            {
                string skipPath = frame.GetProperty("p").GetString()!;
                onError?.Invoke("远端跳过: " + skipPath + " (" + frame.GetProperty("msg").GetString() + ")");
                continue;
            }
            if (op != ProtocolOp.Data) throw new InvalidOperationException("未知消息: " + op);

            string path = frame.GetProperty("p").GetString()!;
            long size = frame.GetProperty("s").GetInt64();
            long mtimeTicks = frame.TryGetProperty("t", out var tEl) ? tEl.GetInt64() : 0;
            string savedPath = resolveSavePath(path);

            string? savedDir = Path.GetDirectoryName(savedPath);
            if (!string.IsNullOrEmpty(savedDir)) Directory.CreateDirectory(savedDir);
            using (var fs = new FileStream(savedPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024))
            {
                var buf = new byte[128 * 1024];
                long remaining = size;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buf.Length, remaining);
                    await conn.ReadRawAsync(buf, toRead, ct);
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
        return received;
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

    /// <summary>关闭远端指定名称的进程，返回远端提示与剩余同类进程数。</summary>
    public async Task<(string Msg, int Remaining)> ProcessCloseAsync(string[] killNames, CancellationToken ct)
    {
        await _conn!.SendJsonAsync(new { op = ProtocolOp.ProcessClose, names = killNames }, ct);
        var frame = await _conn!.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op = frame.GetProperty("op").GetString()!;
        if (op == ProtocolOp.Error)
            throw new InvalidOperationException(frame.GetProperty("msg").GetString());
        if (op != ProtocolOp.Ok)
            throw new InvalidOperationException("未知消息: " + op);
        return (frame.GetProperty("msg").GetString() ?? "", frame.GetProperty("remaining").GetInt32());
    }

    /// <summary>启动远端指定路径的程序，返回远端提示与运行中的进程数。</summary>
    public async Task<(string Msg, int Running)> ProcessOpenAsync(string exePath, CancellationToken ct)
    {
        await _conn!.SendJsonAsync(new { op = ProtocolOp.ProcessOpen, path = exePath }, ct);
        var frame = await _conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op = frame.GetProperty("op").GetString()!;
        if (op == ProtocolOp.Error)
            throw new InvalidOperationException(frame.GetProperty("msg").GetString());
        if (op != ProtocolOp.Ok)
            throw new InvalidOperationException("未知消息: " + op);
        return (frame.GetProperty("msg").GetString() ?? "", frame.GetProperty("running").GetInt32());
    }

    /// <summary>获取远端 BBK 目录下符合后缀条件的文件列表。</summary>
    public async Task<List<string>> FileListAsync(string suffixes, CancellationToken ct)
    {
        await _conn!.SendJsonAsync(new { op = ProtocolOp.FileList, suffixes }, ct);
        var paths = new List<string>();
        while (true)
        {
            var frame = await _conn.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");
            string op = frame.GetProperty("op").GetString()!;
            if (op == ProtocolOp.Error)
                throw new InvalidOperationException(frame.GetProperty("msg").GetString());
            if (op == ProtocolOp.FileListEntry)
                paths.Add(frame.GetProperty("path").GetString() ?? "");
            else if (op == ProtocolOp.FileListEnd)
                return paths;
            else
                throw new InvalidOperationException("未知消息: " + op);
        }
    }

    /// <summary>把本机可执行文件发送到远端，并触发远端更新脚本。</summary>
    public async Task UpdateAsync(
        string exePath,
        Action<string> log,
        CancellationToken ct)
    {
        try
        {
            var frame = await _conn!.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");
            if (frame.GetProperty("op").GetString() != ProtocolOp.Ready)
                throw new InvalidOperationException("远端未就绪");

            log("正在发送程序文件...");
            var fi = new FileInfo(exePath);
            long size = fi.Length;
            await _conn.SendJsonAsync(new { op = ProtocolOp.Exe, s = size, t = fi.LastWriteTimeUtc.Ticks }, ct);

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
            if (op == ProtocolOp.Error)
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