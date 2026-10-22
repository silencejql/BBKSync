namespace LanFileSync.Mvvm;

/// <summary>日志行级别（决定显示颜色）。</summary>
public enum LogKind
{
    Info,
    Error,
    Success,
}

/// <summary>一条日志：已带时间戳的文本与级别。</summary>
public sealed record LogEntry(string Text, LogKind Kind);
