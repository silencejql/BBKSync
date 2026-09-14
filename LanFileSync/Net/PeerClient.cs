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
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        Action<string> onError,
        CancellationToken ct)
    {
        await TransferSide.SendTransferManifestAsync(_conn!, itemPath, isDir, sameSkip, ct);
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
        var frame = await _conn!.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op = frame.GetProperty("op").GetString()!;
        if (op == "err")
            throw new InvalidOperationException(frame.GetProperty("msg").GetString());
        if (op != "ok")
            throw new InvalidOperationException("未知消息: " + op);
        return (frame.GetProperty("msg").GetString() ?? "", frame.GetProperty("running").GetInt32());
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