using System.IO;
using System.Net.Sockets;

namespace LanFileSync;

public sealed class PeerClient : IDisposable
{
    private TcpClient? _tcp;
    private PeerConnection? _conn;

    public async Task ConnectAsync(string host, int port, string role, CancellationToken ct, bool preBackupBat = false)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct);
        _conn = new PeerConnection(_tcp);
        await _conn.SendJsonAsync(new { hello = true, role, preBackupBat }, ct);
    }

    public async Task PullAsync(
        string root,
        SyncOptions options,
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        Action<string> onError,
        CancellationToken ct)
    {
        var engine = new SyncEngine(root, options);
        var list = new List<FileEntry>();

        await TargetSide.ReceiveManifestAsync(_conn!, ct, list);
        log($"已收到对方文件清单({list.Count} 项)，按本机规则计算...");

        engine.Plan(list);
        onTotal(engine.NeedList.Count);

        if (engine.NeedList.Count == 0)
            log("所有文件与对方相同，无需更新。");
        else if (engine.NeedList.Count < 10)
            foreach (var p in engine.NeedList) log("  ← " + p);

        await _conn!.SendJsonAsync(new { op = "req", paths = engine.NeedList }, ct);
        await engine.ReceiveAndApplyAsync(_conn, onFile, onError, ct);
    }

    public async Task PushAsync(
        string root,
        SyncOptions options,
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        Action<string> onError,
        CancellationToken ct)
    {
        await SourceSide.SendManifestAsync(_conn!, root, ct);
        log("本机文件清单已发送，等待对方按对方界面的规则计算...");

        var frame = await _conn!.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op = frame.GetProperty("op").GetString()!;
        if (op == "err")
            throw new InvalidOperationException(frame.GetProperty("msg").GetString());

        var paths = frame.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToList();
        onTotal(paths.Count);
        log(paths.Count == 0 ? "对方所有文件完全相同，无需更新。" : $"对方需要 {paths.Count} 个文件，开始发送...");
        if (paths.Count > 0 && paths.Count < 10)
            foreach (var p in paths) log("  → " + p);

        await SourceSide.SendRequestedFilesAsync(_conn, root, paths, onFile, ct);

        var resp = await _conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        if (resp.GetProperty("op").GetString() == "err")
            throw new InvalidOperationException(resp.GetProperty("msg").GetString());

        foreach (var m in resp.GetProperty("msgs").EnumerateArray())
            onError?.Invoke(m.GetString() ?? "");

        log("对方已应用完成");
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
        Action<string>? logError = null)
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
                log("对方备份前脚本: " + msg);
            else
                (logError ?? log)("对方备份前脚本失败，已继续备份: " + msg);
        }

        var engine = new BackupSyncEngine(dest, options);
        var list = new List<FileEntry>();

        await TargetSide.ReceiveManifestAsync(_conn!, ct, list);
        log($"已收到对方文件清单({list.Count} 项)，按备份规则计算...");

        engine.Plan(list);
        onTotal(engine.NeedList.Count);

        if (engine.NeedList.Count == 0)
            log("所有文件与备份目标相同，无需备份。");
        else if (engine.NeedList.Count < 10)
            foreach (var p in engine.NeedList) log("  ← " + p);

        await _conn!.SendJsonAsync(new { op = "req", paths = engine.NeedList }, ct);
        await engine.ReceiveAndApplyAsync(_conn, onFile, onError, ct);
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
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        Action<string> onError,
        CancellationToken ct)
    {
        await TransferSide.SendTransferManifestAsync(_conn!, itemPath, isDir, sameSkip, updateMode, deviceTag, ct);
        log("传输文件清单已发送，等待对方按相同路径计算...");

        var frame = await _conn!.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op = frame.GetProperty("op").GetString()!;
        if (op == "err")
            throw new InvalidOperationException(frame.GetProperty("msg").GetString());

        var paths = frame.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToList();
        onTotal(paths.Count);
        log(paths.Count == 0 ? "对方相应文件完全相同，无需传输。" : $"对方需要 {paths.Count} 个文件，开始发送...");

        await TransferSide.SendRequestedFilesAsync(_conn, paths, onFile, ct);

        var resp = await _conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string rop = resp.GetProperty("op").GetString()!;
        if (rop == "err")
            throw new InvalidOperationException(resp.GetProperty("msg").GetString());

        foreach (var m in resp.GetProperty("msgs").EnumerateArray())
            onError?.Invoke(m.GetString() ?? "");

        log("对方已应用完成");
    }

    public async Task TransferFromRemoteAsync(
        string remotePath,
        bool updateMode,
        string remoteDevice,
        string localDevice,
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        Action<string> onError,
        CancellationToken ct)
    {
        // 请求远端发送文件清单(isDir 由远端根据其文件系统自行判断)
        await _conn!.SendJsonAsync(new { op = "tinit", p = remotePath.Replace('\\', '/') }, ct);
        log("已请求远端文件清单，等待对方计算...");

        var list = new List<FileEntry>();
        await TargetSide.ReceiveManifestAsync(_conn, ct, list);
        log($"已收到远端文件清单({list.Count} 项)...");

        // 从远端同步不修改本地文件，拉取所有文件
        var needPaths = list.Select(e => e.RelPath).ToList();
        onTotal(needPaths.Count);
        log(needPaths.Count == 0 ? "远端无匹配文件。" : $"准备拉取 {needPaths.Count} 个文件...");

        string dateStr = DateTime.Now.ToString("yyyyMMdd");
        string normRemote = remotePath.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
        bool dirMode = list.Count != 1
            || !string.Equals(list[0].RelPath.Replace('/', Path.DirectorySeparatorChar), normRemote, StringComparison.OrdinalIgnoreCase);

        // 更新模式：先把本地原文件/文件夹重命名为 名称_本地设备_日期，然后拉取到原路径
        // 拷贝模式：不动原文件，另存为 名称_来源设备_日期
        string saveRoot;
        if (updateMode)
        {
            string localTag = SanitizeFileName(string.IsNullOrWhiteSpace(localDevice) ? Environment.MachineName : localDevice);
            RenameLocalForUpdate(normRemote, dirMode, localTag, dateStr, log);
            saveRoot = normRemote;
        }
        else
        {
            string sourceTag = SanitizeFileName(string.IsNullOrWhiteSpace(remoteDevice) ? "远端" : remoteDevice);
            if (dirMode)
            {
                string parent = Path.GetDirectoryName(normRemote) ?? "";
                string folder = Path.GetFileName(normRemote);
                saveRoot = UniquePath(Path.Combine(parent, $"{folder}_AutoBackupFrom_{sourceTag}_{dateStr}"), isDir: true);
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
            if (op == "skip")
            {
                string skipPath = frame.GetProperty("p").GetString()!;
                onError?.Invoke("远端跳过: " + skipPath + " (" + frame.GetProperty("msg").GetString() + ")");
                continue;
            }
            if (op != "data") throw new InvalidOperationException("未知消息: " + op);

            string path = frame.GetProperty("p").GetString()!;
            long size = frame.GetProperty("s").GetInt64();
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
                string sourceTag = SanitizeFileName(string.IsNullOrWhiteSpace(remoteDevice) ? "远端" : remoteDevice);
                string baseName = Path.GetFileNameWithoutExtension(localPath);
                string ext = Path.GetExtension(localPath);
                savedPath = UniquePath(Path.Combine(saveRoot, $"{baseName}_AutoBackupFrom_{sourceTag}_{dateStr}{ext}"), isDir: false);
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

    /// <summary>更新模式拉取前，将本地已有文件/文件夹重命名为 名称_设备_日期。</summary>
    private static void RenameLocalForUpdate(string itemPath, bool isDir, string deviceTag, string date, Action<string> log)
    {
        if (isDir)
        {
            if (!Directory.Exists(itemPath)) { log("本地文件夹不存在，无需重命名: " + itemPath); return; }
            string parent = Path.GetDirectoryName(itemPath) ?? "";
            string folder = Path.GetFileName(itemPath);
            string renamed = UniquePath(Path.Combine(parent, $"{folder}_AutoBackup_{date}"), isDir: true);
            log($"更新前重命名本地文件夹: {itemPath} → {renamed}");
            Directory.Move(itemPath, renamed);
        }
        else
        {
            if (!File.Exists(itemPath)) { log("本地文件不存在，无需重命名: " + itemPath); return; }
            string dirOf = Path.GetDirectoryName(itemPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(itemPath);
            string ext = Path.GetExtension(itemPath);
            string renamed = UniquePath(Path.Combine(dirOf, $"{baseName}_AutoBackup_{date}{ext}"), isDir: false);
            log($"更新前重命名本地文件: {itemPath} → {renamed}");
            File.Move(itemPath, renamed);
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static string UniquePath(string path, bool isDir)
    {
        if (!(isDir ? Directory.Exists(path) : File.Exists(path))) return path;
        string parent = Path.GetDirectoryName(path) ?? "";
        string baseName = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        string ext = isDir ? "" : Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(parent, $"{baseName}_{i}{ext}");
            if (!(isDir ? Directory.Exists(candidate) : File.Exists(candidate))) return candidate;
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
        var frame = await _conn!.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        if (frame.GetProperty("op").GetString() != "ready")
            throw new InvalidOperationException("对方未就绪");

        log("正在发送程序文件...");
        var fi = new FileInfo(exePath);
        long size = fi.Length;
        await _conn.SendJsonAsync(new { op = "exe", s = size }, ct);

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

        log("等待对方执行更新...");
        var resp = await _conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op = resp.GetProperty("op").GetString()!;
        if (op == "err")
            throw new InvalidOperationException(resp.GetProperty("msg").GetString());
        log("远端程序更新完成");
    }
}