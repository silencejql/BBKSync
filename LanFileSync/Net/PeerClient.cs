using System.IO;
using System.Net.Sockets;

namespace LanFileSync;

public sealed class PeerClient : IDisposable
{
    private TcpClient? _tcp;
    private PeerConnection? _conn;

    public async Task ConnectAsync(string host, int port, string role, CancellationToken ct)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct);
        _conn = new PeerConnection(_tcp);
        await _conn.SendJsonAsync(new { hello = true, role }, ct);
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
        log($"已收到对方文件清单（{list.Count} 项），按本机规则计算...");

        engine.Plan(list);
        onTotal(engine.NeedList.Count);

        await _conn!.SendJsonAsync(new { op = "req", paths = engine.NeedList }, ct);
        await engine.ReceiveAndApplyAsync(_conn, onFile, onError, ct);
    }

    public async Task PushAsync(
        string root,
        SyncOptions options,
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
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
        log($"对方需要 {paths.Count} 个文件，开始发送...");

        await SourceSide.SendRequestedFilesAsync(_conn, root, paths, onFile, ct);

        var resp = await _conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        if (resp.GetProperty("op").GetString() == "err")
            throw new InvalidOperationException(resp.GetProperty("msg").GetString());

        log("对方已应用完成");
    }

    public async Task BackupPullAsync(
        string dest,
        BackupOptions options,
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        Action<string> onError,
        CancellationToken ct)
    {
        var engine = new BackupSyncEngine(dest, options);
        var list = new List<FileEntry>();

        await TargetSide.ReceiveManifestAsync(_conn!, ct, list);
        log($"已收到对方文件清单（{list.Count} 项），按备份规则计算...");

        engine.Plan(list);
        onTotal(engine.NeedList.Count);

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
        bool killFreeForm,
        Action<string> log,
        Action<string, long> onFile,
        Action<int> onTotal,
        CancellationToken ct)
    {
        await TransferSide.SendTransferManifestAsync(_conn!, itemPath, isDir, sameSkip, killFreeForm, ct);
        log("传输文件清单已发送，等待对方按相同路径计算...");

        var frame = await _conn!.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        string op = frame.GetProperty("op").GetString()!;
        if (op == "err")
            throw new InvalidOperationException(frame.GetProperty("msg").GetString());

        var paths = frame.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToList();
        onTotal(paths.Count);
        log($"对方需要 {paths.Count} 个文件，开始发送...");

        await TransferSide.SendRequestedFilesAsync(_conn, paths, onFile, ct);

        var resp = await _conn.RecvJsonAsync(ct)
            ?? throw new EndOfStreamException("连接已断开");
        if (resp.GetProperty("op").GetString() == "err")
            throw new InvalidOperationException(resp.GetProperty("msg").GetString());

        log("对方已应用完成");
    }

    public void Dispose()
    {
        _conn?.Dispose();
        _tcp?.Dispose();
    }
}