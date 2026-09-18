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
        public bool IsDir { get; init; }
    }

    /// <summary>扫描命中的一个备份候选项（文件夹或压缩包）。</summary>
    private sealed class BackupCandidate
    {
        public required string FullPath { get; init; }
        public required string DisplayName { get; init; }
        /// <summary>去掉结尾日期段后的分组键（文件保留扩展名，文件夹无扩展名）。</summary>
        public required string Key { get; init; }
        public required DateTime LastWrite { get; init; }
        public required bool IsDir { get; init; }
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
                $"确认将 {toDelete.Count} 个旧备份移入回收站？\n\n每组仅保留修改日期最新的一个。\n删除项可在回收站中还原。",
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
        if (toDelete.Count == 0 && toRename.Count == 0)
        {
            DarkMessageBox.Show("没有需要处理的备份（可能已整理，或存在名称冲突需人工处理）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (DarkMessageBox.Show(
                $"确认执行？\n\n重命名 {toRename.Count} 项（去掉名称结尾的「_日期」段）；\n" +
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
            foreach (var item in toRename)
            {
                try
                {
                    // 执行前再次确认目标不存在，防止扫描后被外部创建
                    if (item.IsDir ? Directory.Exists(item.TargetPath) : File.Exists(item.TargetPath))
                    {
                        LogLineError("目标名称已存在，跳过重命名: " + item.NewName);
                        fail++;
                        continue;
                    }
                    if (item.IsDir) Directory.Move(item.FullPath, item.TargetPath);
                    else File.Move(item.FullPath, item.TargetPath);
                    LogLine($"重命名: {item.Name}  →  {item.NewName}");
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
                    : BuildRenamePlan(candidates, dir);
                Dispatcher.Invoke(() =>
                {
                    if (purge)
                    {
                        lvPurge.ItemsSource = views;
                        int del = views.Count(v => v.Action == "删除");
                        int keep = views.Count - del;
                        txtPurgeSummary.Text = views.Count == 0
                            ? "未发现带日期命名的备份文件夹或压缩包。"
                            : $"发现 {candidates.Select(c => c.Key).Distinct().Count()} 组备份：将删除 {del} 项，保留最新 {keep} 项。";
                        btnPurgeRun.IsEnabled = del > 0;
                    }
                    else
                    {
                        lvRename.ItemsSource = views;
                        int del = views.Count(v => v.Action == "删除");
                        int ren = views.Count(v => v.Action == "重命名");
                        int skip = views.Count(v => v.Action == "跳过");
                        txtRenameSummary.Text = views.Count == 0
                            ? "未发现带日期命名的备份文件夹或压缩包。"
                            : $"重命名 {ren} 项，删除同名旧备份 {del} 项" + (skip > 0 ? $"，冲突跳过 {skip} 项" : "") + "。";
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

    /// <summary>枚举文件夹第一层中名称以「_日期」结尾的文件夹和压缩包。</summary>
    private static List<BackupCandidate> ScanBackupCandidates(string dir)
    {
        var result = new List<BackupCandidate>();

        IEnumerable<string> dirs;
        IEnumerable<string> files;
        try
        {
            dirs = Directory.EnumerateDirectories(dir);
            files = Directory.EnumerateFiles(dir);
        }
        catch { return result; }

        foreach (var path in dirs)
            AddIfMatch(result, path, isDir: true);

        foreach (var path in files)
        {
            if (!ArchiveExtensions.Contains(Path.GetExtension(path))) continue;
            AddIfMatch(result, path, isDir: false);
        }
        return result;
    }

    private static void AddIfMatch(List<BackupCandidate> result, string fullPath, bool isDir)
    {
        string fileName = Path.GetFileName(fullPath);
        string ext = isDir ? "" : Path.GetExtension(fileName);
        string nameNoExt = isDir ? fileName : Path.GetFileNameWithoutExtension(fileName);

        var m = DateSuffixRegex.Match(nameNoExt);
        if (!m.Success || m.Index <= 0) return;
        if (!TryParseDateToken(m.Groups["date"].Value, out _)) return;

        string key = nameNoExt[..m.Index] + ext;
        var info = isDir
            ? new DirectoryInfo(fullPath)
            : (FileSystemInfo)new FileInfo(fullPath);
        result.Add(new BackupCandidate
        {
            FullPath = fullPath,
            DisplayName = fileName,
            Key = key,
            LastWrite = info.LastWriteTime,
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

    /// <summary>剔除旧备份：每组保留修改日期最新的一项，其余标记删除。</summary>
    private static List<BackupItemView> BuildPurgePlan(List<BackupCandidate> candidates)
    {
        var views = new List<BackupItemView>();
        foreach (var group in candidates.GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderByDescending(c => c.LastWrite)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                views.Add(new BackupItemView
                {
                    Action = i == 0 ? "保留" : "删除",
                    Name = c.DisplayName,
                    LastWriteText = c.LastWrite.ToString("yyyy-MM-dd HH:mm:ss"),
                    FullPath = c.FullPath,
                    IsDir = c.IsDir
                });
            }
        }
        // 删除项在前，便于核对
        return views.OrderBy(v => v.Action == "删除" ? 0 : 1)
                    .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    /// <summary>删除日期命名：目标名不存在时最新项重命名、其余删除；目标名已存在则整组跳过。</summary>
    private static List<BackupItemView> BuildRenamePlan(List<BackupCandidate> candidates, string dir)
    {
        var views = new List<BackupItemView>();
        foreach (var group in candidates.GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group
                .OrderByDescending(c => c.LastWrite)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            string targetPath = Path.Combine(dir, group.Key);
            bool conflict = Directory.Exists(targetPath) || File.Exists(targetPath);

            for (int i = 0; i < ordered.Count; i++)
            {
                var c = ordered[i];
                string action, newName;
                if (conflict)
                {
                    action = "跳过";
                    newName = "目标名已存在";
                }
                else if (i == 0)
                {
                    action = "重命名";
                    newName = group.Key;
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
                    LastWriteText = c.LastWrite.ToString("yyyy-MM-dd HH:mm:ss"),
                    FullPath = c.FullPath,
                    TargetPath = targetPath,
                    IsDir = c.IsDir
                });
            }
        }
        return views
            .OrderBy(v => v.Action switch { "重命名" => 0, "删除" => 1, _ => 2 })
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
