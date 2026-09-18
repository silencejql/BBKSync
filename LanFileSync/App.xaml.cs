using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace LanFileSync;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局约束：ToolTip 显示时不超出所属窗口（主界面）的范围
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent,
            new RoutedEventHandler(OnToolTipOpened));

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

    /// <summary>
    /// ToolTip 打开时改用自定义定位，将其左上角限制在所属窗口客户区内，
    /// 靠近窗口边缘时自动内收，避免提示超出主界面范围。
    /// </summary>
    private static void OnToolTipOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip tip || tip.PlacementTarget is not UIElement target)
            return;

        tip.CustomPopupPlacementCallback = (popupSize, _, _) =>
        {
            var window = Window.GetWindow(target)
                ?? Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                ?? Current.MainWindow;
            if (window is null)
                return new[] { new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None) };

            var dpi = VisualTreeHelper.GetDpi(window);

            // 期望位置：鼠标指针右下方（与系统默认观感接近），转换到屏幕物理坐标
            var mouse = Mouse.GetPosition(target);
            var preferred = target.PointToScreen(new Point(mouse.X + 14, mouse.Y + 20));

            // 窗口客户区在屏幕上的物理矩形
            var origin = window.PointToScreen(new Point(0, 0));
            double winW = window.ActualWidth * dpi.DpiScaleX;
            double winH = window.ActualHeight * dpi.DpiScaleY;
            double tipW = popupSize.Width * dpi.DpiScaleX;
            double tipH = popupSize.Height * dpi.DpiScaleY;
            double marginX = 8 * dpi.DpiScaleX;
            double marginY = 8 * dpi.DpiScaleY;

            // 将期望点夹取到窗口边界内
            double minX = origin.X + marginX;
            double maxX = origin.X + winW - tipW - marginX;
            double minY = origin.Y + marginY;
            double maxY = origin.Y + winH - tipH - marginY;
            double x = maxX < minX ? minX : Math.Clamp(preferred.X, minX, maxX);
            double y = maxY < minY ? minY : Math.Clamp(preferred.Y, minY, maxY);

            // 转回相对 PlacementTarget 的坐标（自动处理 DPI 换算）
            var point = target.PointFromScreen(new Point(x, y));
            return new[] { new CustomPopupPlacement(point, PopupPrimaryAxis.None) };
        };
        tip.Placement = PlacementMode.Custom;
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