using System.IO;

namespace LanFileSync;

public sealed class TransferEngine
{
    private readonly string _itemPath;
    private readonly bool _itemIsDir;
    private readonly bool _sameSkip;
    private readonly bool _killFreeForm;
    private readonly List<string> _needed = new();

    public TransferEngine(string itemPath, bool isDir, bool sameSkip, bool killFreeForm)
    {
        _itemPath = itemPath.Replace('/', Path.DirectorySeparatorChar);
        _itemIsDir = isDir;
        _sameSkip = sameSkip;
        _killFreeForm = killFreeForm;
    }

    public IReadOnlyList<string> NeedList => _needed;

    public List<string> TransferMessages { get; } = new();

    public void Plan(IEnumerable<FileEntry> remote)
    {
        _needed.Clear();
        foreach (var f in remote)
        {
            if (_sameSkip && !NeedsUpdate(f))
                continue;
            _needed.Add(f.RelPath);
        }
    }

    private bool NeedsUpdate(FileEntry remote)
    {
        string target = remote.RelPath.Replace('/', Path.DirectorySeparatorChar);
        var fi = new FileInfo(target);
        if (!fi.Exists)
            return true;
        if (fi.Length != remote.Size)
            return true;
        long diff = Math.Abs(fi.LastWriteTimeUtc.Ticks - remote.MTimeUtcTicks);
        return diff > Constants.TimeTolerance.Ticks;
    }

    public bool BackupItem(Action<string> log, Action<string> onError)
    {
        string date = DateTime.Now.ToString("yyyyMMdd");
        string backupPath;
        if (_itemIsDir)
            backupPath = _itemPath + "-更新自动备份-" + date;
        else
        {
            string dirOf = Path.GetDirectoryName(_itemPath) ?? "";
            string nameOf = Path.GetFileNameWithoutExtension(_itemPath);
            string extOf = Path.GetExtension(_itemPath);
            backupPath = Path.Combine(dirOf, nameOf + "-更新自动备份-" + date + extOf);
        }
        if (File.Exists(backupPath) || Directory.Exists(backupPath))
        {
            log("已存在本次备份，跳过: " + backupPath);
            return true;
        }
        if (_itemIsDir)
        {
            if (!Directory.Exists(_itemPath))
            {
                log("目标文件夹不存在，无需备份: " + _itemPath);
                return true;
            }
            log("更新前自动备份目标文件夹: " + _itemPath + " → " + backupPath);
            try
            {
                CopyDirectory(_itemPath, backupPath);
                log("备份完成: " + backupPath);
                return true;
            }
            catch (Exception ex)
            {
                onError("备份失败: " + ex.Message);
                return false;
            }
        }
        else
        {
            if (!File.Exists(_itemPath))
            {
                log("目标文件不存在，无需备份: " + _itemPath);
                return true;
            }
            log("更新前将原文件重命名为备份: " + _itemPath + " → " + backupPath);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Move(_itemPath, backupPath);
                log("已重命名备份原文件: " + backupPath);
                return true;
            }
            catch (Exception ex)
            {
                onError("备份失败: " + ex.Message);
                return false;
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        string src = sourceDir.TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(destDir);
        foreach (string dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(destDir, rel));
        }
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            string target = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public async Task ReceiveAndApplyAsync(PeerConnection peer, Action<string> log, Action<string, long>? onFile, Action<string> onError, CancellationToken ct)
    {
        while (true)
        {
            var frame = await peer.RecvJsonAsync(ct)
                ?? throw new EndOfStreamException("连接已断开");

            string op = frame.GetProperty("op").GetString()!;
            if (op == "done")
                break;
            if (op == "err")
                throw new InvalidOperationException(frame.GetProperty("msg").GetString());
            if (op == "skip")
            {
                string sp = frame.GetProperty("p").GetString() ?? "";
                string sm = frame.GetProperty("msg").GetString() ?? "";
                ReceiveMessage($"源文件读取失败，已跳过: {sp}（{sm}）", onError);
                continue;
            }
            if (op != "data")
                throw new InvalidOperationException("未知消息: " + op);

            string p = frame.GetProperty("p").GetString()!;
            long size = frame.GetProperty("s").GetInt64();
            long mtime = frame.GetProperty("t").GetInt64();

            try
            {
                await WriteOneAsync(peer, p, size, mtime, log, onError, ct);
                onFile?.Invoke(p, size);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ReceiveMessage($"{p}: {ex.Message}", onError);
            }
        }
    }

    private void ReceiveMessage(string msg, Action<string> onError)
    {
        TransferMessages.Add(msg);
        try { onError(msg); } catch { }
    }

    private async Task WriteOneAsync(PeerConnection peer, string p, long size, long mtimeTicks, Action<string> log, Action<string> onError, CancellationToken ct)
    {
        string target = p.Replace('/', Path.DirectorySeparatorChar);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

        long consumed = 0;
        var buf = new byte[Constants.StreamBufferSize];
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, Constants.StreamBufferSize, FileOptions.SequentialScan))
            {
                while (consumed < size)
                {
                    int n = (int)Math.Min(buf.LongLength, size - consumed);
                    await peer.ReadRawAsync(buf, n, ct);
                    consumed += n;
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
        }
        catch (IOException)
        {
            while (consumed < size)
            {
                int n = (int)Math.Min(buf.LongLength, size - consumed);
                await peer.ReadRawAsync(buf, n, ct);
                consumed += n;
            }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            ReceiveMessage(target + "  写入失败，已跳过", onError);
            return;
        }

        if (!MoveIntoPlace(tmp, target, _killFreeForm, log, onError))
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return;
        }

        try { File.SetLastWriteTimeUtc(target, new DateTime(mtimeTicks, DateTimeKind.Utc)); } catch { }
    }

    private bool MoveIntoPlace(string tmp, string target, bool killFreeForm, Action<string> log, Action<string> onError)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                File.Move(tmp, target, overwrite: true);
                return true;
            }
            catch (IOException)
            {
                attempt++;
                if (killFreeForm && attempt <= Constants.MaxRetryCount)
                {
                    int killed = FreeFormKiller.KillAll();
                    ReceiveMessage($"更新出错: {target}，已结束远端电脑 {killed} 个 {FreeFormKiller.ProcessPrefixAsterisk} 进程，重试第 {attempt} 次...", onError);
                    Thread.Sleep(Constants.RetryDelayMs * attempt);
                    continue;
                }
                ReceiveMessage($"更新失败（已重试 {attempt - 1} 次）: {target}", onError);
                return false;
            }
        }
    }
}