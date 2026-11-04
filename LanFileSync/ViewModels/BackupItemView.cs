namespace LanFileSync.ViewModels;

/// <summary>「小工具」预览列表中的一行。</summary>
public sealed class BackupItemView
{
    /// <summary>操作：保留 / 删除 / 重命名。</summary>
    public string Action { get; init; } = "";
    /// <summary>勾选即表示要删除；旧项默认自动勾选，可手动调整。</summary>
    public bool IsChecked { get; set; }
    /// <summary>复选框是否可勾选（重命名保留项不可删除，禁用勾选）。</summary>
    public bool CanCheck { get; init; } = true;
    public string Name { get; init; } = "";
    public string NewName { get; init; } = "";
    public string LastWriteText { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public bool IsDir { get; init; }
}
