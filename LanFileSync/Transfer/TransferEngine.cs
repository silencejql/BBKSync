using System.IO;

namespace LanFileSync;

/// <summary>文件传输引擎：计算需要传输的文件并落盘；更新模式先重命名原文件，拷贝模式另存副本。</summary>
public sealed class TransferEngine
{
    private readonly string _itemPath;
    private readonly bool _itemIsDir;
    private readonly bool _sameSkip;
    private readonly bool _updateMode;
    private readonly string _deviceTag;
    private readonly string _date;
    private readonly bool _deleteSameName;
    private readonly string? _copyRoot;
    private readonly List<string> _needed = new();
    private readonly RollbackGuard _guard = new();
    private readonly Action<string>? _log;
    private string? _backupPath;
    private bool _backupIsDir;
    private bool _backupReady;

    public TransferEngine(string itemPath, bool isDir, bool sameSkip, bool updateMode, string deviceTag,
        bool deleteSameName = false, Action<string>? log = null)
    {
        _itemPath = itemPath.Replace('/', Path.DirectorySeparatorChar);
        _itemIsDir = isDir;
        _sameSkip = sameSkip;
        _updateMode = updateMode;
        _deviceTag = FileHelper.SanitizeName(string.IsNullOrWhiteSpace(deviceTag) ? "远端" : deviceTag);
        _deleteSameName = deleteSameName;
        _log = log;
        // 正常备份命名只带到年月日；仅当备份命名重复时，才给旧备份追加 _HHmmss 后缀
        _date = DateTime.Now.ToString("yyyyMMdd");

        // 拷贝模式的落盘根目录(与原项目同级，命名为 文件夹名_设备信息_日期)
        if (!_updateMode && _itemIsDir)
        {
            string parent = Path.GetDirectoryName(_itemPath) ?? "";
            string folder = Path.GetFileName(_itemPath.TrimEnd(Path.DirectorySeparatorChar));
            _copyRoot = FileHelper.UniquePath(Path.Combine(parent, $"{folder}_AutoUpdateFrom_{_deviceTag}_{_date}"), isDir: true);
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
        // 备份远端文件/文件夹，命名为 文件名_AutoUpdateFrom_远端设备信息_日期
        string backupPath;
        if (_itemIsDir)
            backupPath = _itemPath + "_AutoBackup_" + _date;
        else
        {
            string dirOf = Path.GetDirectoryName(_itemPath) ?? "";
            string nameOf = Path.GetFileNameWithoutExtension(_itemPath);
            string extOf = Path.GetExtension(_itemPath);
            backupPath = Path.Combine(dirOf, nameOf + "_AutoBackup_" + _date + extOf);
        }
        if (File.Exists(backupPath) || Directory.Exists(backupPath))
        {
            if (_deleteSameName)
            {
                log("备份命名已存在，删除原备份: " + backupPath);
                if (_itemIsDir) Directory.Delete(backupPath, recursive: true);
                else File.Delete(backupPath);
            }
            else
            {
                // 取消勾选：将旧备份重命名为 名称_AutoBackup_年月日_时分秒 保留，然后新备份再按 _date 另存
                string oldBackup = backupPath;
                string newBackup = _itemIsDir
                    ? FileHelper.UniquePath(_itemPath + "_AutoBackup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), isDir: true)
                    : FileHelper.UniquePath(Path.Combine(Path.GetDirectoryName(_itemPath) ?? "",
                        Path.GetFileNameWithoutExtension(_itemPath) + "_AutoBackup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + Path.GetExtension(_itemPath)),
                        isDir: false);
                log("备份命名已存在，重命名旧备份保留: " + oldBackup + " → " + newBackup);
                try
                {
                    if (_itemIsDir) Directory.Move(oldBackup, newBackup);
                    else File.Move(oldBackup, newBackup);
                }
                catch (Exception ex)
                {
                    onError("重命名旧备份失败: " + ex.Message);
                    return false;
                }
            }
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
                _backupPath = backupPath;
                _backupIsDir = true;
                _backupReady = true;
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
                _backupPath = backupPath;
                _backupIsDir = false;
                _backupReady = true;
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
        try
        {
            await ReceiveLoopAsync(peer, log, onFile, onError, ct);
        }
        catch (OperationCanceledException)
        {
            Rollback();
            throw;
        }

        Commit();
    }

    private async Task ReceiveLoopAsync(PeerConnection peer, Action<string> log, Action<string, long>? onFile, Action<string> onError, CancellationToken ct)
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
                ReceiveMessage($"源文件读取失败，已跳过: {sp}({sm})", onError);
                continue;
            }
            if (op != ProtocolOp.Data)
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
            // 单文件拷贝：同目录，命名为 名称_AutoUpdateFrom_设备信息_日期.扩展名(避免重名)
            string dirOf = Path.GetDirectoryName(_itemPath) ?? "";
            string nameOf = Path.GetFileNameWithoutExtension(original);
            string extOf = Path.GetExtension(original);
            target = FileHelper.UniquePath(Path.Combine(dirOf, $"{nameOf}_AutoUpdateFrom_{_deviceTag}_{_date}{extOf}"), isDir: false);
        }
    }

    private async Task WriteOneAsync(PeerConnection peer, string p, long size, long mtimeTicks, Action<string> log, Action<string> onError, CancellationToken ct)
    {
        WriteOneAsyncTarget(p, out string target);
        _guard.EnsureDirectory(Path.GetDirectoryName(target)!);
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
        catch (OperationCanceledException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
        catch (IOException)
        {
            try
            {
                while (consumed < size)
                {
                    int n = (int)Math.Min(buf.LongLength, size - consumed);
                    await peer.ReadRawAsync(buf, n, ct);
                    consumed += n;
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            ReceiveMessage(target + "  写入失败，已跳过", onError);
            return;
        }

        if (!MoveIntoPlace(tmp, target, log, onError))
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return;
        }

        try { File.SetLastWriteTimeUtc(target, new DateTime(mtimeTicks, DateTimeKind.Utc)); } catch { }
        _guard.MarkWritten(target);
    }

    /// <summary>取消时删除本次传输写入的文件，并在更新模式下恢复传输前的原文件/文件夹。</summary>
    public int Rollback()
    {
        var written = _guard.Written.ToList();
        int removed = _guard.Rollback();
        RestoreBackup(written);
        if (removed > 0)
            _log?.Invoke($"取消清理：已删除本次已传输的 {removed} 个文件");
        return removed;
    }

    /// <summary>正常完成传输后清理覆盖前的临时记录。</summary>
    public void Commit() => _guard.Commit();

    /// <summary>更新模式下把 BackupItem 产生的备份还原回原位，再删除该备份。</summary>
    private void RestoreBackup(IReadOnlyList<string> written)
    {
        if (!_updateMode || !_backupReady || _backupPath == null)
            return;

        try
        {
            if (_backupIsDir)
            {
                string root = _itemPath.TrimEnd(Path.DirectorySeparatorChar);
                foreach (string target in written)
                {
                    string rel = target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        ? Path.GetRelativePath(root, target)
                        : Path.GetFileName(target);
                    string source = Path.Combine(_backupPath, rel);
                    if (!File.Exists(source))
                        continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, overwrite: true);
                }
                if (Directory.Exists(_backupPath))
                    Directory.Delete(_backupPath, recursive: true);
                _log?.Invoke("取消清理：已恢复更新前的原文件夹");
            }
            else
            {
                if (File.Exists(_backupPath))
                {
                    File.Move(_backupPath, _itemPath, overwrite: true);
                    _log?.Invoke("取消清理：已恢复更新前的原文件");
                }
            }
            _backupReady = false;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"取消清理：恢复原内容失败: {ex.Message}，备份保留在 {_backupPath}");
        }
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