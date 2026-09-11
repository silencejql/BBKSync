using System.IO;
using System.Windows;

namespace LanFileSync;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            MessageBox.Show(
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