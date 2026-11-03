using System.IO;

namespace LanFileSync;

/// <summary>
/// 记录一次写入操作产生的落盘结果，用于取消时回滚：
/// 覆盖已有文件前先把它移到同目录临时文件，正常完成时删除临时文件，取消时删除新文件并还原旧文件。
/// 清理阶段的所有异常都会被忽略，避免掩盖原始的取消原因。
/// </summary>
internal sealed class RollbackGuard
{
    private readonly List<string> _written = new();
    private readonly List<(string Target, string Backup)> _overwritten = new();
    private readonly HashSet<string> _createdDirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本次已成功写入的全部目标文件。</summary>
    public IReadOnlyList<string> Written => _written;

    public int WrittenCount => _written.Count;

    /// <summary>创建目录并记录本次新建的目录，取消时一并清理。</summary>
    public void EnsureDirectory(string dir)
    {
        if (Directory.Exists(dir))
            return;

        var missing = new List<string>();
        string? current = dir;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            missing.Add(current);
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;
            current = parent;
        }

        Directory.CreateDirectory(dir);
        foreach (var item in missing)
            _createdDirs.Add(item);
    }

    /// <summary>
    /// 覆盖写入前调用：若目标已存在，则移动到同目录的临时文件(命名匹配 .tmp 规则，不会被备份包含)。
    /// 返回临时文件路径；目标不存在时返回 null。
    /// </summary>
    public string? PrepareOverwrite(string target)
    {
        if (!File.Exists(target))
            return null;

        string backup = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Move(target, backup);
        _overwritten.Add((target, backup));
        return backup;
    }

    /// <summary>记录一个已成功落盘的新文件。</summary>
    public void MarkWritten(string target) => _written.Add(target);

    /// <summary>文件写入失败时，立即把该文件的覆盖前备份移回原位。</summary>
    public void RestoreFile(string target)
    {
        int index = _overwritten.FindLastIndex(
            e => string.Equals(e.Target, target, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        var (_, backup) = _overwritten[index];
        _overwritten.RemoveAt(index);
        TryRestore(backup, target);
    }

    /// <summary>正常完成：删除覆盖前临时文件并清空记录。</summary>
    public void Commit()
    {
        foreach (var (_, backup) in _overwritten)
            TryDeleteFile(backup);
        _overwritten.Clear();
        _written.Clear();
        _createdDirs.Clear();
    }

    /// <summary>
    /// 取消回滚：删除本次写入的文件，把覆盖前临时文件移回原位，再删除本次新建的空目录。
    /// 返回删除的文件数。
    /// </summary>
    public int Rollback()
    {
        int removed = 0;
        for (int i = _written.Count - 1; i >= 0; i--)
        {
            if (TryDeleteFile(_written[i]))
                removed++;
        }
        _written.Clear();

        for (int i = _overwritten.Count - 1; i >= 0; i--)
        {
            var (target, backup) = _overwritten[i];
            TryRestore(backup, target);
        }
        _overwritten.Clear();

        // 先删深层目录，再删父目录，避免父目录因仍含子目录而删除失败
        foreach (var dir in _createdDirs.OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { }
        }
        _createdDirs.Clear();
        return removed;
    }

    private static void TryRestore(string backup, string target)
    {
        try
        {
            if (!File.Exists(backup))
                return;
            TryDeleteFile(target);
            File.Move(backup, target, overwrite: true);
        }
        catch { }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
