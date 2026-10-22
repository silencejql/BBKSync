using System.Windows;

namespace LanFileSync.Services;

/// <summary>UI 线程调度抽象，供视图模型从后台线程安全更新绑定状态。</summary>
public interface IUiDispatcher
{
    void Invoke(Action action);
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    public void Invoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}
