using System.IO;

namespace LanFileSync;

/// <summary>更新引擎：按规则计算差异，可选同步前备份，接收文件并应用到本机。</summary>
public sealed class SyncEngine
{
    public string Root { get; }
    public SyncOptions Options { get; }

    private readonly List<FileEntry> _remote = new();
    private readonly List<string> _needed = new();
    private readonly List<string> _applied = new();
    private readonly RollbackGuard _guard = new();
    private readonly Action<string>? _log;
    private string? _binBackup;

    public SyncEngine(string root, SyncOptions options, Action<string>? log = null)
    {
        Root = root;
        Options = options;
        _log = log;
    }

    public IReadOnlyList<string> NeedList => _needed;

    /// <summary>本次实际写入(更新)的文件完整路径。</summary>
    public IReadOnlyList<string> Applied => _applied;

    public void Plan(IEnumerable<FileEntry> remote)
    {
        _remote.Clear();
        _remote.AddRange(remote);

        if (Options.FullReplaceBin)
            StashBinFolder();

        _needed.Clear();
        _applied.Clear();
        foreach (var f in _remote)
        {
            if (UploadRules.IsSkippedSync(f.RelPath))
                continue;
            if (NeedsUpdate(f))
                _needed.Add(f.RelPath);
        }
    }

    private bool NeedsUpdate(FileEntry remote)
    {
        string local = Path.Combine(Root, remote.RelPath);
        var fi = new FileInfo(local);
        if (!fi.Exists)
            return true;
        // 修改日期与文件大小都相同才视为一致，其余情况都需要更新
        if (fi.Length != remote.Size)
            return true;
        long diff = Math.Abs(fi.LastWriteTimeUtc.Ticks - remote.MTimeUtcTicks);
        return diff > Constants.TimeTolerance.Ticks;
    }

    public async Task ReceiveAndApplyAsync(PeerConnection peer, Action<string, long>? onFile, Action<string>? onError, CancellationToken ct)
    {
        long received = 0;
        try
        {
            while (true)
            {
                var frame = await peer.RecvJsonAsync(ct)
                    ?? throw new EndOfStreamException("连接已断开");

                string op = frame.GetProperty("op").GetString()!;
                if (op == ProtocolOp.Done)
                    break;
                if (op == ProtocolOp.Cancel)
                    throw new OperationCanceledException("主控端已取消操作");
                if (op == ProtocolOp.Error)
                    throw new InvalidOperationException(frame.GetProperty("msg").GetString());
                if (op == ProtocolOp.Skip)
                {
                    string sp = frame.GetProperty("p").GetString() ?? "";
                    string sm = frame.GetProperty("msg").GetString() ?? "";
                    onError?.Invoke($"{sp}  源文件读取失败，已跳过: {sm}");
                    continue;
                }
                if (op != ProtocolOp.Data)
                    throw new InvalidOperationException("未知消息: " + op);

                string rel = frame.GetProperty("p").GetString()!;
                long size = frame.GetProperty("s").GetInt64();
                long mtime = frame.GetProperty("t").GetInt64();

                try
                {
                    await DownloadAndApplyAsync(peer, rel, size, mtime, ct);
                    _applied.Add(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar)));
                    received += size;
                    onFile?.Invoke(rel, size);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    onError?.Invoke($"{rel}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            Rollback();
            throw;
        }

        Commit();
    }

    public async Task CopyNeededFromAsync(string sourceRoot, Action<string, long>? onFile, Action<string>? onError, CancellationToken ct)
    {
        var buf = new byte[Constants.StreamBufferSize];
        try
        {
            foreach (var rel in _needed)
            {
                ct.ThrowIfCancellationRequested();

                long size = 0;
                try { size = new FileInfo(Path.Combine(sourceRoot, rel)).Length; } catch { }

                string target = Path.Combine(Root, rel);
                _guard.EnsureDirectory(Path.GetDirectoryName(target)!);
                string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

                try
                {
                    // 覆盖前先暂存原文件，取消或失败时还原
                    _guard.PrepareOverwrite(target);
                    await using (var src = new FileStream(Path.Combine(sourceRoot, rel), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, Constants.StreamBufferSize, FileOptions.SequentialScan))
                    await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, Constants.StreamBufferSize, FileOptions.SequentialScan))
                    {
                        int n;
                        while ((n = await src.ReadAsync(buf.AsMemory(), ct)) > 0)
                            await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    }

                    MoveIntoPlace(tmp, target);
                    try
                    {
                        var fi = new FileInfo(Path.Combine(sourceRoot, rel));
                        File.SetLastWriteTimeUtc(target, fi.LastWriteTimeUtc);
                    }
                    catch { }
                    _guard.MarkWritten(target);
                    _applied.Add(target);
                    onFile?.Invoke(rel, size);
                }
                catch (OperationCanceledException)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    _guard.RestoreFile(target);
                    throw;
                }
                catch (Exception)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    _guard.RestoreFile(target);
                    onError?.Invoke(rel);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Rollback();
            throw;
        }

        Commit();
    }

    private async Task DownloadAndApplyAsync(PeerConnection peer, string rel, long size, long mtimeTicks, CancellationToken ct)
    {
        string target = Path.Combine(Root, rel);
        string dir = Path.GetDirectoryName(target)!;
        _guard.EnsureDirectory(dir);
        string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            // 覆盖前先暂存原文件，取消或失败时还原
            _guard.PrepareOverwrite(target);
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, Constants.StreamBufferSize, FileOptions.SequentialScan))
            {
                long remaining = size;
                var buf = new byte[Constants.StreamBufferSize];
                while (remaining > 0)
                {
                    int n = (int)Math.Min(buf.LongLength, remaining);
                    await peer.ReadRawAsync(buf, n, ct);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    remaining -= n;
                }
            }

            MoveIntoPlace(tmp, target);
            try { File.SetLastWriteTimeUtc(target, new DateTime(mtimeTicks, DateTimeKind.Utc)); } catch { }
            _guard.MarkWritten(target);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            _guard.RestoreFile(target);
            throw;
        }
    }

    /// <summary>整体替换 Bin 时先移动到 Root 外的临时目录，取消时还原，正常完成后删除。</summary>
    private void StashBinFolder()
    {
        string bin = Path.Combine(Root, "Bin");
        if (!Directory.Exists(bin))
            return;

        string rootTrimmed = Path.TrimEndingDirectorySeparator(Root);
        string backup = rootTrimmed + "_BinBackup_" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.Move(bin, backup);
            _binBackup = backup;
        }
        catch
        {
            // 跨盘或父目录不可写时退回原行为：直接删除
            SafeHardDelete(bin);
            if (Directory.Exists(bin))
                throw new IOException("无法删除 Bin 文件夹(被占用): " + bin);
        }
    }

    /// <summary>取消时回滚本次写入的文件，并还原整体替换前的 Bin 文件夹。</summary>
    public int Rollback()
    {
        int removed = _guard.Rollback();
        bool binRestored = RestoreBinFolder();
        _applied.Clear();
        if (removed > 0 || binRestored)
            _log?.Invoke($"取消清理：已删除本次已更新的 {removed} 个文件" +
                         (binRestored ? "，并恢复原 Bin 文件夹" : ""));
        return removed;
    }

    /// <summary>正常完成：删除覆盖前临时文件与 Bin 备份。</summary>
    public void Commit()
    {
        _guard.Commit();
        if (_binBackup == null)
            return;
        try
        {
            if (Directory.Exists(_binBackup))
                Directory.Delete(_binBackup, recursive: true);
        }
        catch { }
        _binBackup = null;
    }

    private bool RestoreBinFolder()
    {
        if (_binBackup == null)
            return false;

        string bin = Path.Combine(Root, "Bin");
        try
        {
            if (Directory.Exists(bin))
                Directory.Delete(bin, recursive: true);
            if (Directory.Exists(_binBackup))
                Directory.Move(_binBackup, bin);
            _binBackup = null;
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"恢复原 Bin 文件夹失败: {ex.Message}，备份保留在 {_binBackup}");
            return false;
        }
    }

    private void MoveIntoPlace(string tmp, string target)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                File.Move(tmp, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < Constants.MaxRetryCount)
            {
                attempt++;
                Thread.Sleep(Constants.RetryDelayMs * attempt);
            }
        }
    }

    private void SafeHardDelete(string path)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < Constants.MaxRetryCount)
            {
                attempt++;
                Thread.Sleep(Constants.RetryDelayMs * attempt);
            }
        }
    }
}