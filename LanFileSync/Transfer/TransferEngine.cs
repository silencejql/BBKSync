using System.IO;

namespace LanFileSync;

public sealed class TransferEngine
{
    private readonly string _itemPath;
    private readonly bool _itemIsDir;
    private readonly bool _sameSkip;
    private readonly bool _updateMode;
    private readonly string _deviceTag;
    private readonly string _date;
    private readonly string? _copyRoot;
    private readonly List<string> _needed = new();

    public TransferEngine(string itemPath, bool isDir, bool sameSkip, bool updateMode, string deviceTag)
    {
        _itemPath = itemPath.Replace('/', Path.DirectorySeparatorChar);
        _itemIsDir = isDir;
        _sameSkip = sameSkip;
        _updateMode = updateMode;
        _deviceTag = SanitizeName(string.IsNullOrWhiteSpace(deviceTag) ? "远端" : deviceTag);
        _date = DateTime.Now.ToString("yyyyMMdd");

        // 拷贝模式的落盘根目录(与原项目同级，命名为 文件夹名_设备信息_日期)
        if (!_updateMode && _itemIsDir)
        {
            string parent = Path.GetDirectoryName(_itemPath) ?? "";
            string folder = Path.GetFileName(_itemPath.TrimEnd(Path.DirectorySeparatorChar));
            _copyRoot = UniquePath(Path.Combine(parent, $"{folder}_{_deviceTag}_{_date}"), isDir: true);
        }
    }

    /// <summary>拷贝模式下实际保存位置(文件为目录，文件夹为新文件夹根)。</summary>
    public string EffectiveTarget => _copyRoot ?? (Path.GetDirectoryName(_itemPath) ?? "");

    public IReadOnlyList<string> NeedList => _needed;

    public List<string> TransferMessages { get; } = new();

    public void Plan(IEnumerable<FileEntry> remote)
    {
        _needed.Clear();
        foreach (var f in remote)
        {
            // 拷贝模式不覆盖原文件，全部需要；更新模式按 sameSkip 判断
            if (_updateMode && _sameSkip && !NeedsUpdate(f))
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
        string backupPath;
        if (_itemIsDir)
            backupPath = _itemPath + "_" + _deviceTag + "_" + _date;
        else
        {
            string dirOf = Path.GetDirectoryName(_itemPath) ?? "";
            string nameOf = Path.GetFileNameWithoutExtension(_itemPath);
            string extOf = Path.GetExtension(_itemPath);
            backupPath = Path.Combine(dirOf, nameOf + "_" + _deviceTag + "_" + _date + extOf);
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
                ReceiveMessage($"源文件读取失败，已跳过: {sp}({sm})", onError);
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

    private void WriteOneAsyncTarget(string p, out string target)
    {
        string original = p.Replace('/', Path.DirectorySeparatorChar);
        if (_updateMode)
        {
            target = original;
            return;
        }

        if (_itemIsDir)
        {
            // 文件夹拷贝：保留相对结构，落到 文件夹名_设备信息_日期 新目录
            string root = _itemPath.TrimEnd(Path.DirectorySeparatorChar);
            string rel = original.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? original[(root.Length + 1)..]
                : Path.GetFileName(original);
            target = Path.Combine(_copyRoot!, rel);
        }
        else
        {
            // 单文件拷贝：同目录，命名为 名称_设备信息_日期.扩展名(避免重名)
            string dirOf = Path.GetDirectoryName(_itemPath) ?? "";
            string nameOf = Path.GetFileNameWithoutExtension(original);
            string extOf = Path.GetExtension(original);
            target = UniquePath(Path.Combine(dirOf, $"{nameOf}_{_deviceTag}_{_date}{extOf}"), isDir: false);
        }
    }

    private static string SanitizeName(string name)
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

    private async Task WriteOneAsync(PeerConnection peer, string p, long size, long mtimeTicks, Action<string> log, Action<string> onError, CancellationToken ct)
    {
        WriteOneAsyncTarget(p, out string target);
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

        if (!MoveIntoPlace(tmp, target, log, onError))
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return;
        }

        try { File.SetLastWriteTimeUtc(target, new DateTime(mtimeTicks, DateTimeKind.Utc)); } catch { }
    }

    private bool MoveIntoPlace(string tmp, string target, Action<string> log, Action<string> onError)
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
                if (attempt <= Constants.MaxRetryCount)
                {
                    ReceiveMessage($"更新出错: {target}，重试第 {attempt} 次...", onError);
                    Thread.Sleep(Constants.RetryDelayMs * attempt);
                    continue;
                }
                ReceiveMessage($"更新失败(已重试 {attempt - 1} 次): {target}", onError);
                return false;
            }
        }
    }
}