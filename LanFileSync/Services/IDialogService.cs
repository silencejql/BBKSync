namespace LanFileSync.Services;

/// <summary>视图模型使用的对话框/窗口服务抽象，避免直接依赖具体窗口。</summary>
public interface IDialogService
{
    void Info(string message, string title = "提示");
    void Warning(string message, string title = "错误");
    void Error(string message, string title = "错误");
    bool Confirm(string message, string title = "确认");

    string? PickFolder(string title);
    string? PickFile(string title, string filter = "所有文件 (*.*)|*.*");

    void ShowHistory();
    void ShowComputers();
    void ShowHelp();
}
