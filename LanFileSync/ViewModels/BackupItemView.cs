namespace LanFileSync.ViewModels;

/// <summary>「小工具」预览列表中的一行。</summary>
public sealed class BackupItemView
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
