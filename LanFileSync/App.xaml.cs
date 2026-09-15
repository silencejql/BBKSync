using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace LanFileSync;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        _mutex = new Mutex(true, "BBKSync_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            DarkMessageBox.Show("BBKSync 已在运行中，不能重复启动。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            DarkMessageBox.Show(
                "程序遇到未处理的错误：\n\n" + e.Exception + "\n\n详细信息已写入崩溃日志 crash.log。",
                "程序错误", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppPaths.ExeDir(), "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
        }
        catch { }
    }
}