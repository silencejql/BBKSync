using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LanFileSync;

/// <summary>「其他小工具」标签页：剔除旧备份文件、删除日期命名。均为本地文件整理操作。</summary>
public partial class MainWindow
{
    /// <summary>预览列表中的一行。</summary>
    private sealed class BackupItemView
    {
        /// <summary>操作：保留 / 删除 / 重命名 / 跳过。</summary>
        public string Action { get; init; } = "";
        public string Name { get; init; } = "";
        public string NewName { get; init; } = "";
        public string LastWriteText { get; init; } = "";
        public string FullPath { get; init; } = "";
        public string TargetPath { get; init; } = "";
        /// <summary>重命名时目标已存在：true 表示候选更新，执行前先把旧目标移入回收站。</summary>
        public bool ReplaceTarget { get; init; }
        public bool IsDir { get; init; }
    }

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
        /// <summary>名称中日期段解析出的日期（组内按此排序，而非文件修改时间）。</summary>
        public required DateTime NameDate { get; init; }
        /// <summary>文件修改时间（与已存在目标比较新旧用）。</summary>
        public required DateTime LastWrite { get; init; }
        /// <summary>名称日期的展示文本。</summary>
        public required string DateText { get; init; }
        public required bool IsDir { get; init; }
        /// <summary>分组键：同一子文件夹内日期之前名称相同才算一组。</summary>
        public string GroupKey => DirPath + '\u0001' + Key;
    }

    // 结尾日期段：下划线开头，yyyyMMdd（也兼容 yyyy-MM-dd / yyyy_MM_dd），可选 6 位时间
    private static readonly Regex DateSuffixRegex = new(
        @"_(?<date>(?:19|20)\d{2}(?:[-_]?\d{2}){2}(?:[-_]?\d{2}(?:[-_]?\d{2}){2})?)$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z"
    };

    private bool _toolsBusy;

    private void BtnToolsBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要整理的备份所在文件夹" };
        if (dlg.ShowDialog() == true) txtToolsDir.Text = dlg.FolderName;
    }

    // ================================ 剔除旧备份文件 ================================

    private void BtnPurgeScan_Click(object sender, RoutedEventArgs e) => ToolsScan(purge: true);

    private void BtnPurgeRun_Click(object sender, RoutedEventArgs e)
    {
        if (lvPurge.ItemsSource is not List<BackupItemView> plan) return;
        var toDelete = plan.Where(i => i.Action == "删除").ToList();
        if (toDelete.Count == 0)
        {
            DarkMessageBox.Show("没有需要删除的旧备份。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (DarkMessageBox.Show(
                $"确认将 {toDelete.Count} 个旧备份移入回收站？\n\n每组仅保留名称日期最新的一个。\n删除项可在回收站中还原。",
                "确认剔除旧备份", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        string dir = ToolsCurrentDir();
        RunToolsAction(() =>
        {
            int ok = 0, fail = 0;
            LogDivider();
            LogLine($"剔除旧备份：{dir}，共 {toDelete.Count} 项待删除");
            foreach (var item in toDelete)
            {
                try
                {
                    if (item.IsDir)
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            item.FullPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                            item.FullPath,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    LogLine("已移入回收站: " + item.Name);
                    ok++;
                }
                catch (Exception ex)
                {
                    LogLineError("删除失败: " + item.Name + " —— " + ex.Message);
                    fail++;
                }
            }
            LogLine($"剔除完成：成功 {ok} 项" + (fail > 0 ? $"，失败 {fail} 项" : ""));
            return ok;
        }, rescanPurge: true);
    }

    // ================================ 删除日期命名 ================================

    private void BtnRenameScan_Click(object sender, RoutedEventArgs e) => ToolsScan(purge: false);

    private void BtnRenameRun_Click(object sender, RoutedEventArgs e)
    {
        if (lvRename.ItemsSource is not List<BackupItemView> plan) return;
        var toDelete = plan.Where(i => i.Action == "删除").ToList();
        var toRename = plan.Where(i => i.Action == "重命名").ToList();
        int toReplace = toRename.Count(i => i.ReplaceTarget);
        if (toDelete.Count == 0 && toRename.Count == 0)
        {
            DarkMessageBox.Show("没有需要处理的备份（可能已整理，或存在名称冲突需人工处理）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (DarkMessageBox.Show(
                $"确认执行？\n\n重命名 {toRename.Count} 项（去掉名称结尾的「_日期」段）" +
                (toReplace > 0 ? $"，其中 {toReplace} 项替换修改日期更旧的已有目标（旧目标移入回收站）" : "") + "；\n" +
                $"同名的 {toDelete.Count} 个旧备份移入回收站。\n\n删除项可在回收站中还原。",
                "确认删除日期命名", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        string dir = ToolsCurrentDir();
        RunToolsAction(() =>
        {
            int ok = 0, fail = 0;
            LogDivider();
            LogLine($"删除日期命名：{dir}，重命名 {toRename.Count} 项，删除 {toDelete.Count} 项");
            // 先删除多余备份，再重命名保留项，避免目标名与待删除项无关（目标名本就无日期，不会冲突）
            foreach (var item in toDelete)
            {
                try
                {
                    RecyclePath(item.FullPath, item.IsDir);
                    LogLine("已移入回收站: " + item.Name);
                    ok++;
                }
                catch (Exception ex)
                {
                    LogLineError("删除失败: " + item.Name + " —— " + ex.Message);
                    fail++;
                }
            }
            foreach (var item in toRename)
            {
                try
                {
                    // 执行前再次确认目标状态，防止扫描后被外部改动
                    if (item.IsDir ? Directory.Exists(item.TargetPath) : File.Exists(item.TargetPath))
                    {
                        if (!item.ReplaceTarget)
                        {
                            LogLineError("目标名称已存在，跳过重命名: " + item.NewName);
                            fail++;
                            continue;
                        }
                        // 替换：仅当候选仍比目标新时清走旧目标，否则跳过
                        if (GetLastWrite(item.TargetPath) >= GetLastWrite(item.FullPath))
                        {
                            LogLineError("目标修改日期不早于候选，跳过替换: " + item.NewName);
                            fail++;
                            continue;
                        }
                        RecyclePath(item.TargetPath, item.IsDir);
                        LogLine("旧目标已移入回收站: " + item.NewName);
                    }
                    if (item.IsDir) Directory.Move(item.FullPath, item.TargetPath);
                    else File.Move(item.FullPath, item.TargetPath);
                    LogLine($"重命名: {item.Name}  →  {item.NewName}" + (item.ReplaceTarget ? "（已替换旧目标）" : ""));
                    ok++;
                }
                catch (Exception ex)
                {
                    LogLineError($"重命名失败: {item.Name} —— {ex.Message}");
                    fail++;
                }
            }
            LogLine($"处理完成：成功 {ok} 项" + (fail > 0 ? $"，失败/跳过 {fail} 项" : ""));
            return ok;
        }, rescanPurge: false);
    }

    // ================================ 共享逻辑 ================================

    private string ToolsCurrentDir() => txtToolsDir.Text.Trim();

    private void ToolsScan(bool purge)
    {
        if (_toolsBusy) return;
        string dir = txtToolsDir.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            // 留空时默认使用「备份到」目录
            dir = txtBackupDest.Text.Trim();
            if (string.IsNullOrWhiteSpace(dir))
            {
                DarkMessageBox.Show("请先选择/输入要整理的备份所在文件夹。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            txtToolsDir.Text = dir;
        }
        if (!Directory.Exists(dir))
        {
            DarkMessageBox.Show("文件夹不存在：" + dir, "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetToolsBusy(true);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var candidates = ScanBackupCandidates(dir);
                List<BackupItemView> views = purge
                    ? BuildPurgePlan(candidates)
                    : BuildRenamePlan(candidates);
                Dispatcher.Invoke(() =>
                {
                    if (purge)
                    {
                        lvPurge.ItemsSource = views;
                        int del = views.Count(v => v.Action == "删除");
                        int keep = views.Count - del;
                        txtPurgeSummary.Text = views.Count == 0
                            ? "未发现带日期命名的备份文件夹或压缩包（含各子文件夹）。"
                            : $"发现 {candidates.Select(c => c.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 组备份：将删除 {del} 项，保留最新 {keep} 项（按所在文件夹分组）。";
                        btnPurgeRun.IsEnabled = del > 0;
                    }
                    else
                    {
                        lvRename.ItemsSource = views;
                        int del = views.Count(v => v.Action == "删除");
                        int ren = views.Count(v => v.Action == "重命名");
                        int skip = views.Count(v => v.Action == "跳过");
                        int rep = views.Count(v => v.ReplaceTarget);
                        txtRenameSummary.Text = views.Count == 0
                            ? "未发现带日期命名的备份文件夹或压缩包（含各子文件夹）。"
                            : $"重命名 {ren} 项" + (rep > 0 ? $"（替换旧目标 {rep} 项）" : "") +
                              $"，删除同名旧备份 {del} 项" + (skip > 0 ? $"，冲突跳过 {skip} 项" : "") + "（按所在文件夹分组）。";
                        btnRenameRun.IsEnabled = del > 0 || ren > 0;
                    }
                    LogLine(purge
                        ? $"扫描完成（剔除旧备份）：{dir}，命中 {candidates.Count} 项备份"
                        : $"扫描完成（删除日期命名）：{dir}，命中 {candidates.Count} 项备份");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    DarkMessageBox.Show("扫描失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            finally
            {
                Dispatcher.Invoke(() => SetToolsBusy(false));
            }
        });
    }

    /// <summary>
    /// 递归枚举备份候选项：普通子文件夹继续深入；名称带日期段的文件夹本身就是一个完整备份单元，
    /// 收录但不再深入（避免父子同时被改名/删除导致路径失效）。压缩包仅识别 .zip/.rar/.7z。
    /// </summary>
    private static List<BackupCandidate> ScanBackupCandidates(string root)
    {
        var result = new List<BackupCandidate>();
        ScanBackupDir(root, root, result);
        return result;
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

    /// <summary>判断文件夹/文件名（压缩包不含扩展名部分）是否以合法「_日期」段结尾。</summary>
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

        string key = nameNoExt[..m.Index] + ext;
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
            IsDir = isDir
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

    /// <summary>剔除旧备份：每个子文件夹内每组保留修改日期最新的一项，其余标记删除。</summary>
    private static List<BackupItemView> BuildPurgePlan(List<BackupCandidate> candidates)
    {
        var views = new List<BackupItemView>();
        foreach (var group in candidates.GroupBy(c => c.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderByDescending(c => c.NameDate)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                views.Add(new BackupItemView
                {
                    Action = i == 0 ? "保留" : "删除",
                    Name = c.DisplayName,
                    LastWriteText = c.DateText,
                    FullPath = c.FullPath,
                    IsDir = c.IsDir
                });
            }
        }
        // 按相对路径（名称）排序，同组相邻便于对比，不按操作排序
        return views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>删除日期命名：同一子文件夹内目标名不存在时最新项重命名、其余删除；
    /// 目标名已存在时按修改日期取舍——候选更新则替换旧目标（旧目标入回收站），目标更新则保留目标并删除组内旧项。</summary>
    private static List<BackupItemView> BuildRenamePlan(List<BackupCandidate> candidates)
    {
        var views = new List<BackupItemView>();
        foreach (var group in candidates.GroupBy(c => c.GroupKey, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderByDescending(c => c.NameDate)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // 重命名始终在候选项自身所在的文件夹内进行；不同子文件夹分别判定冲突
            var first = ordered[0];
            string targetPath = Path.Combine(first.DirPath, first.Key);
            bool conflict = Directory.Exists(targetPath) || File.Exists(targetPath);
            // 冲突时按文件修改日期取舍：候选更新→替换；目标更新→跳过候选
            bool replace = conflict && first.LastWrite > GetLastWrite(targetPath);
            string relDir = Path.GetDirectoryName(first.DisplayName) ?? "";
            string targetDisplay = string.IsNullOrEmpty(relDir) ? first.Key : Path.Combine(relDir, first.Key);

            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                string action, newName;
                if (conflict && !replace)
                {
                    // 目标更新：最新候选原样保留，旧项仍删除
                    action = i == 0 ? "跳过" : "删除";
                    newName = i == 0 ? "目标已存在且更新" : "—";
                }
                else if (i == 0)
                {
                    action = "重命名";
                    newName = targetDisplay;
                }
                else
                {
                    action = "删除";
                    newName = "—";
                }
                views.Add(new BackupItemView
                {
                    Action = action,
                    Name = c.DisplayName,
                    NewName = newName,
                    LastWriteText = c.DateText,
                    FullPath = c.FullPath,
                    TargetPath = targetPath,
                    ReplaceTarget = i == 0 && replace,
                    IsDir = c.IsDir
                });
            }
        }
        // 按相对路径（名称）排序，同组相邻便于对比，不按操作排序
        return views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

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
        SetToolsBusy(true);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { action(); }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    DarkMessageBox.Show("操作失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error));
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    SetToolsBusy(false);
                    ToolsScan(rescanPurge); // 执行后自动刷新对应预览
                });
            }
        });
    }

    private void SetToolsBusy(bool busy)
    {
        _toolsBusy = busy;
        txtToolsDir.IsEnabled = !busy;
        btnToolsBrowse.IsEnabled = !busy;
        btnPurgeScan.IsEnabled = !busy;
        btnRenameScan.IsEnabled = !busy;
        btnPurgeRun.IsEnabled = !busy && lvPurge.ItemsSource is List<BackupItemView> p && p.Any(i => i.Action == "删除");
        btnRenameRun.IsEnabled = !busy && lvRename.ItemsSource is List<BackupItemView> r
            && (r.Any(i => i.Action == "删除") || r.Any(i => i.Action == "重命名"));
    }
}
