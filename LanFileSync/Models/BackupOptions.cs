using System.Text.RegularExpressions;

namespace LanFileSync;

public sealed class BackupOptions
{
    public bool ApplyLogRule { get; init; } = true;
    public int LogRetentionDays { get; init; } = 2;
    /// <summary>只备份 Log：非 Log 文件全部排除，Log 文件按 <see cref="LogFromDate"/>~<see cref="LogToDate"/> 的修改日期筛选。</summary>
    public bool OnlyLogs { get; init; }
    public DateTime? LogFromDate { get; init; }
    public DateTime? LogToDate { get; init; }
    public IReadOnlyList<string> IgnoreRegexes { get; init; } = Array.Empty<string>();

    /// <summary>仅 Log 模式下，非 Log 路径一律不备份。</summary>
    public bool ExcludesByLogOnly(string relPath)
        => OnlyLogs && !UploadRules.HasSegment(relPath, "Log");

    /// <summary>
    /// Log 文件按修改日期过滤：仅 Log 模式使用指定区间（起止相同即单日）；
    /// 否则按保留天数从今天往前推算。
    /// </summary>
    public bool ExcludesLogByDate(DateTime lastWriteLocal)
    {
        DateTime date = lastWriteLocal.Date;
        if (OnlyLogs)
        {
            DateTime from = (LogFromDate ?? DateTime.MinValue).Date;
            DateTime to = (LogToDate ?? DateTime.MaxValue).Date;
            return date < from || date > to;
        }

        if (!ApplyLogRule)
            return false;
        if (LogRetentionDays <= 0)
            return true;
        DateTime cut = DateTime.Today.AddDays(-(LogRetentionDays - 1));
        return date < cut;
    }

    private Regex[]? _compiled;

    public bool IsIgnored(string relPath)
    {
        if (IgnoreRegexes.Count == 0)
            return false;

        if (_compiled == null)
        {
            _compiled = IgnoreRegexes
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s =>
                {
                    try { return new Regex(s, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1)); }
                    catch (Exception) { return null; }
                })
                .Where(r => r != null)
                .Cast<Regex>()
                .ToArray();
        }

        foreach (var rx in _compiled)
        {
            try
            {
                if (rx.IsMatch(relPath))
                    return true;
            }
            catch (Exception) { }
        }
        return false;
    }
}