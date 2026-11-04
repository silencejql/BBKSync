using System.IO;
using System.Text.RegularExpressions;

namespace LanFileSync.ViewModels;

/// <summary>「其他小工具」标签页：剔除旧备份文件、删除日期命名。均为本地文件整理操作。</summary>
public partial class MainViewModel
{
    /// <summary>扫描命中的一个备份候选项（文件夹或压缩包）。</summary>
    private sealed class BackupCandidate
    {
        public required string FullPath { get; init; }
        /// <summary>相对扫描根目录的显示名（子文件夹内的项带相对路径）。</summary>
        public required string DisplayName { get; init; }
        /// <summary>去掉结尾日期段后的名称（文件保留扩展名，文件夹无扩展名）；即重命名目标名。</summary>
        public required string Key { get; init; }
        /// <summary>所在文件夹的绝对路径；与 Key 共同构成分组键（不同子文件夹分别成组）。</summary>
        public required string DirPath { get; init; }
        /// <summary>名称中日期段解析出的日期（组内按此排序）；无日期项为修改日期。</summary>
        public required DateTime NameDate { get; init; }
        /// <summary>文件修改时间。</summary>
        public required DateTime LastWrite { get; init; }
        /// <summary>名称日期的展示文本（无日期项展示修改日期）。</summary>
        public required string DateText { get; init; }
        public required bool IsDir { get; init; }
        /// <summary>名称是否带日期段；false 表示同名无日期项，比较时用修改日期。</summary>
        public required bool IsDated { get; init; }
        /// <summary>分组键：同一子文件夹内日期之前名称相同才算一组。</summary>
        public string GroupKey => DirPath + '\u0001' + Key;
    }

    // 结尾日期段：yyyyMMdd（也兼容 yyyy-MM-dd / yyyy_MM_dd），可选 6 位时间；不要求日期前必须有下划线
    private static readonly Regex DateSuffixRegex = new(
        @"(?<date>(?:19|20)\d{2}(?:[-_]?\d{2}){2}(?:[-_]?\d{2}(?:[-_]?\d{2}){2})?)$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z"
    };

    // ================================ 剔除旧备份文件 ================================

    private void PurgeRun()
    {
        if (PurgeItems is not List<BackupItemView> plan) return;
        var toDelete = plan.Where(i => i.IsChecked).ToList();
        if (toDelete.Count == 0)
        {
            _dialogs.Info("请先在预览列表中勾选要删除的项。", "提示");
            return;
        }
        if (!_dialogs.Confirm(
                $"确认将勾选的 {toDelete.Count} 项移入回收站？\n\n每组仅保留日期最新的一项（同名无日期项按修改日期比较）。\n删除项可在回收站中还原。",
                "确认剔除旧备份")) return;

        string dir = ToolsDir.Trim();
        RunToolsAction(() =>
        {
            int ok = 0, fail = 0;
            LogDivider();
            Log($"剔除旧备份：{dir}，共 {toDelete.Count} 项待删除");
            foreach (var item in toDelete)
            {
                try
                {
                    RecyclePath(item.FullPath, item.IsDir);
                    LogSuccess("已移入回收站: " + item.Name);
                    ok++;
                }
                catch (Exception ex)
                {
                    LogError("删除失败: " + item.Name + " —— " + ex.Message);
                    fail++;
                }
            }
            LogSuccess($"剔除完成：成功 {ok} 项" + (fail > 0 ? $"，失败 {fail} 项" : ""));
            return ok;
        }, rescanPurge: true);
    }

    // ================================ 删除日期命名 ================================

    private void RenameRun()
    {
        if (RenameItems is not List<BackupItemView> plan) return;
        var toDelete = plan.Where(i => i.IsChecked).ToList();
        var toRename = plan.Where(i => i.Action == "重命名").ToList();
        if (toDelete.Count == 0 && toRename.Count == 0)
        {
            _dialogs.Info("没有需要处理的备份（可在预览列表中勾选要删除的项）。", "提示");
            return;
        }
        if (!_dialogs.Confirm(
                $"确认执行？\n\n重命名 {toRename.Count} 项（去掉名称结尾的日期段）；\n" +
                $"勾选的 {toDelete.Count} 项移入回收站。\n\n删除项可在回收站中还原。",
                "确认删除日期命名")) return;

        string dir = ToolsDir.Trim();
        RunToolsAction(() =>
        {
            int ok = 0, fail = 0;
            LogDivider();
            Log($"删除日期命名：{dir}，重命名 {toRename.Count} 项，删除 {toDelete.Count} 项");
            // 先删除多余备份，再重命名保留项，避免目标名与待删除项无关（目标名本就无日期，不会冲突）
            foreach (var item in toDelete)
            {
                try
                {
                    RecyclePath(item.FullPath, item.IsDir);
                    LogSuccess("已移入回收站: " + item.Name);
                    ok++;
                }
                catch (Exception ex)
                {
                    LogError("删除失败: " + item.Name + " —— " + ex.Message);
                    fail++;
                }
            }
            foreach (var item in toRename)
            {
                try
                {
                    // 执行前再次确认目标状态，防止扫描后被外部改动（或同名旧项未勾选删除）
                    if (Directory.Exists(item.TargetPath) || File.Exists(item.TargetPath))
                    {
                        LogError("目标名称已存在，跳过重命名: " + item.NewName);
                        fail++;
                        continue;
                    }
                    if (item.IsDir) Directory.Move(item.FullPath, item.TargetPath);
                    else File.Move(item.FullPath, item.TargetPath);
                    LogSuccess($"重命名: {item.Name}  →  {item.NewName}");
                    ok++;
                }
                catch (Exception ex)
                {
                    LogError($"重命名失败: {item.Name} —— {ex.Message}");
                    fail++;
                }
            }
            LogSuccess($"处理完成：成功 {ok} 项" + (fail > 0 ? $"，失败/跳过 {fail} 项" : ""));
            return ok;
        }, rescanPurge: false);
    }

    // ================================ 共享逻辑 ================================

    private void ToolsScan(bool purge)
    {
        if (_toolsBusy) return;
        string dir = ToolsDir.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            // 留空时默认使用「备份到」目录
            dir = BackupDestPath.Trim();
            if (string.IsNullOrWhiteSpace(dir))
            {
                _dialogs.Info("请先选择/输入要整理的备份所在文件夹。", "提示");
                return;
            }
            ToolsDir = dir;
        }
        if (!Directory.Exists(dir))
        {
            _dialogs.Warning("文件夹不存在：" + dir, "错误");
            return;
        }

        ToolsBusy = true;
        Task.Run(() =>
        {
            try
            {
                var candidates = ScanBackupCandidates(dir);
                List<BackupItemView> views = purge ? BuildPurgePlan(candidates) : BuildRenamePlan(candidates);
                int undated = candidates.Count(c => !c.IsDated);
                _ui.Invoke(() =>
                {
                    if (purge)
                    {
                        PurgeItems = views;
                        int del = views.Count(v => v.IsChecked);
                        int keep = views.Count - del;
                        PurgeSummary = views.Count == 0
                            ? "未发现带日期命名的备份文件夹或压缩包（含各子文件夹）。"
                            : $"发现 {candidates.Select(c => c.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 组备份" +
                              (undated > 0 ? $"（含 {undated} 个同名无日期项，按修改日期比较）" : "") +
                              $"：默认勾选删除 {del} 项，保留最新 {keep} 项，可手动调整勾选（按所在文件夹分组）。";
                    }
                    else
                    {
                        RenameItems = views;
                        int del = views.Count(v => v.IsChecked);
                        int ren = views.Count(v => v.Action == "重命名");
                        int keep = views.Count(v => v.Action == "保留");
                        RenameSummary = views.Count == 0
                            ? "未发现带日期命名的备份文件夹或压缩包（含各子文件夹）。"
                            : $"重命名 {ren} 项，默认勾选删除 {del} 项" +
                              (keep > 0 ? $"，保留无日期最新项 {keep} 项" : "") +
                              "，可手动调整勾选（按所在文件夹分组）。";
                    }
                    Log(purge
                        ? $"扫描完成（剔除旧备份）：{dir}，命中 {candidates.Count} 项备份"
                        : $"扫描完成（删除日期命名）：{dir}，命中 {candidates.Count} 项备份");
                });
            }
            catch (Exception ex)
            {
                _ui.Invoke(() => _dialogs.Warning("扫描失败: " + ex.Message, "错误"));
            }
            finally
            {
                _ui.Invoke(() => ToolsBusy = false);
            }
        });
    }

    /// <summary>
    /// 递归枚举备份候选项：普通子文件夹继续深入；名称带日期段的文件夹本身就是一个完整备份单元，
    /// 收录但不再深入（避免父子同时被改名/删除导致路径失效）。压缩包仅识别 .zip/.rar/.7z。
    /// 最后为每组补充同名但无日期后缀的文件/文件夹（按修改日期参与新旧比较）。
    /// </summary>
    private static List<BackupCandidate> ScanBackupCandidates(string root)
    {
        var result = new List<BackupCandidate>();
        ScanBackupDir(root, root, result);
        AppendUndatedMembers(root, result);
        return result;
    }

    /// <summary>为每组带日期命名的备份补充同名无日期后缀的文件/文件夹（无日期项用修改日期参与比较）。</summary>
    private static void AppendUndatedMembers(string root, List<BackupCandidate> result)
    {
        foreach (var group in result.GroupBy(c => c.GroupKey, StringComparer.OrdinalIgnoreCase).ToList())
        {
            var first = group.First();
            string undated = Path.Combine(first.DirPath, first.Key);
            bool isDir;
            if (Directory.Exists(undated)) isDir = true;
            else if (File.Exists(undated)) isDir = false;
            else continue;
            DateTime lastWrite = GetLastWrite(undated);
            string display;
            try { display = Path.GetRelativePath(root, undated); }
            catch { display = first.Key; }
            result.Add(new BackupCandidate
            {
                FullPath = undated,
                DisplayName = display,
                Key = first.Key,
                DirPath = first.DirPath,
                NameDate = lastWrite,
                LastWrite = lastWrite,
                DateText = lastWrite.ToString("yyyy-MM-dd HH:mm:ss"),
                IsDir = isDir,
                IsDated = false
            });
        }
    }

    private static void ScanBackupDir(string root, string dir, List<BackupCandidate> result)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(dir);
        }
        catch
        {
            // 无权限等情况：跳过该目录，不影响其他分支
            return;
        }

        foreach (var path in entries)
        {
            FileAttributes attr;
            try { attr = File.GetAttributes(path); }
            catch { continue; }
            bool isDir = (attr & FileAttributes.Directory) != 0;

            if (!isDir)
            {
                if (ArchiveExtensions.Contains(Path.GetExtension(path)))
                    TryAddCandidate(result, root, path, isDir: false);
                continue;
            }

            // 名称带日期段的文件夹视为一个备份整体，不再向其内部递归
            if (IsDatedName(path, isDir: true))
                TryAddCandidate(result, root, path, isDir: true);
            else
            {
                // 跳过 junction/符号链接等重分析点，防止循环递归或越出所选文件夹
                if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                ScanBackupDir(root, path, result);
            }
        }
    }

    /// <summary>判断文件夹/文件名（压缩包不含扩展名部分）是否以合法日期段结尾。</summary>
    private static bool IsDatedName(string fullPath, bool isDir)
    {
        string nameNoExt = isDir ? Path.GetFileName(fullPath) : Path.GetFileNameWithoutExtension(fullPath);
        var m = DateSuffixRegex.Match(nameNoExt);
        return m.Success && m.Index > 0 && TryParseDateToken(m.Groups["date"].Value, out _);
    }

    private static void TryAddCandidate(List<BackupCandidate> result, string root, string fullPath, bool isDir)
    {
        string fileName = Path.GetFileName(fullPath);
        string ext = isDir ? "" : Path.GetExtension(fileName);
        string nameNoExt = isDir ? fileName : Path.GetFileNameWithoutExtension(fileName);

        var m = DateSuffixRegex.Match(nameNoExt);
        if (!m.Success || m.Index <= 0) return;
        string dateToken = m.Groups["date"].Value;
        if (!TryParseDateToken(dateToken, out var nameDate)) return;

        string key = nameNoExt[..m.Index].TrimEnd('_', '-', ' ', '.') + ext;
        // 去掉日期段后名称为空时不算备份（例如文件夹本身就叫 20261026）
        if (key.Length <= ext.Length) return;
        // 名称日期展示：带时间段的显示到秒，仅日期的显示到日
        string dateText = dateToken.Count(char.IsDigit) > 8
            ? nameDate.ToString("yyyy-MM-dd HH:mm:ss")
            : nameDate.ToString("yyyy-MM-dd");
        string display;
        try { display = Path.GetRelativePath(root, fullPath); }
        catch { display = fileName; }
        DateTime lastWrite;
        try { lastWrite = File.GetLastWriteTime(fullPath); }
        catch { lastWrite = DateTime.MinValue; }
        result.Add(new BackupCandidate
        {
            FullPath = fullPath,
            DisplayName = display,
            Key = key,
            DirPath = Path.GetDirectoryName(fullPath) ?? root,
            NameDate = nameDate,
            LastWrite = lastWrite,
            DateText = dateText,
            IsDir = isDir,
            IsDated = true
        });
    }

    /// <summary>日期段仅保留数字后按 8 位（yyyyMMdd）或 14 位（yyyyMMddHHmmss）校验为真实日期。</summary>
    private static bool TryParseDateToken(string token, out DateTime dt)
    {
        string digits = new(token.Where(char.IsDigit).ToArray());
        if (digits.Length == 8)
            return DateTime.TryParseExact(digits, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out dt);
        if (digits.Length == 14)
            return DateTime.TryParseExact(digits, "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out dt);
        dt = default;
        return false;
    }

    /// <summary>剔除旧备份：每个子文件夹内每组保留日期最新的一项（无日期项按修改日期比较），其余默认勾选删除。</summary>
    private static List<BackupItemView> BuildPurgePlan(List<BackupCandidate> candidates)
    {
        var views = new List<BackupItemView>();
        foreach (var group in candidates.GroupBy(c => c.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = OrderByNewest(group);
            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                bool keep = i == 0;
                views.Add(new BackupItemView
                {
                    Action = keep ? "保留" : "删除",
                    IsChecked = !keep,
                    Name = c.DisplayName,
                    LastWriteText = DateTextOf(c),
                    FullPath = c.FullPath,
                    IsDir = c.IsDir
                });
            }
        }
        // 按相对路径（名称）排序，同组相邻便于对比，不按操作排序
        return views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>删除日期命名：组内最新项为带日期命名时重命名为无日期名称，其余项默认勾选删除；
    /// 同名无日期项最新（修改日期不早于各名称日期）则原样保留，组内带日期旧备份默认勾选删除。</summary>
    private static List<BackupItemView> BuildRenamePlan(List<BackupCandidate> candidates)
    {
        var views = new List<BackupItemView>();
        foreach (var group in candidates.GroupBy(c => c.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = OrderByNewest(group);
            var keeper = ordered[0];
            // 重命名始终在候选项自身所在的文件夹内进行；不同子文件夹分别成组
            bool rename = keeper.IsDated;
            string targetPath = Path.Combine(keeper.DirPath, keeper.Key);
            string relDir = Path.GetDirectoryName(keeper.DisplayName) ?? "";
            string targetDisplay = string.IsNullOrEmpty(relDir) ? keeper.Key : Path.Combine(relDir, keeper.Key);

            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                string action, newName;
                bool canCheck = true;
                if (i == 0 && rename)
                {
                    action = "重命名";
                    newName = targetDisplay;
                    canCheck = false; // 重命名保留项不可勾选删除
                }
                else if (i == 0)
                {
                    action = "保留";
                    newName = "—";
                }
                else
                {
                    action = "删除";
                    newName = "—";
                }
                views.Add(new BackupItemView
                {
                    Action = action,
                    IsChecked = action == "删除",
                    CanCheck = canCheck,
                    Name = c.DisplayName,
                    NewName = newName,
                    LastWriteText = DateTextOf(c),
                    FullPath = c.FullPath,
                    TargetPath = targetPath,
                    IsDir = c.IsDir
                });
            }
        }
        // 按相对路径（名称）排序，同组相邻便于对比，不按操作排序
        return views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>组内按新旧排序：带日期项按名称日期，无日期项按修改日期；日期相同时带日期命名的优先保留。</summary>
    private static List<BackupCandidate> OrderByNewest(IEnumerable<BackupCandidate> group) =>
        group.OrderByDescending(c => c.NameDate)
             .ThenByDescending(c => c.IsDated)
             .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>日期列展示：带日期项显示名称日期，无日期项显示修改日期并标注。</summary>
    private static string DateTextOf(BackupCandidate c) =>
        c.IsDated ? c.DateText : c.DateText + "（修改日期）";

    /// <summary>取文件/文件夹的修改时间；异常时返回最小值。</summary>
    private static DateTime GetLastWrite(string path)
    {
        try { return File.GetLastWriteTime(path); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>把文件/文件夹移入回收站。</summary>
    private static void RecyclePath(string path, bool isDir)
    {
        if (isDir)
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    private void RunToolsAction(Func<int> action, bool rescanPurge)
    {
        if (_toolsBusy) return;
        ToolsBusy = true;
        Task.Run(() =>
        {
            try { action(); }
            catch (Exception ex)
            {
                _ui.Invoke(() => _dialogs.Error("操作失败: " + ex.Message, "错误"));
            }
            finally
            {
                _ui.Invoke(() =>
                {
                    ToolsBusy = false;
                    ToolsScan(rescanPurge); // 执行后自动刷新对应预览
                });
            }
        });
    }
}
