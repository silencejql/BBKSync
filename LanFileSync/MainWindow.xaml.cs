using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanFileSync.Services;
using LanFileSync.ViewModels;

namespace LanFileSync;

/// <summary>主窗口视图层：只保留窗口装饰、托盘、主题、密码遮罩与视图级交互；
/// 状态、命令和业务逻辑全部在 MainViewModel 中。</summary>
public partial class MainWindow : Window
{
    private readonly HistoryStore _history = new();
    private readonly SettingsStore _settings = new();
    private readonly MainViewModel _vm;
    private TrayIcon? _tray;
    private bool _allowExit;
    private readonly bool _startMinimized;

    public MainWindow()
    {
        // CheckBox 仅双击勾选：阻止单击 toggle，双击时主动勾选
        EventManager.RegisterClassHandler(typeof(CheckBox),
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((s, e) =>
            {
                if (s is CheckBox cb)
                {
                    if (e.ClickCount >= 2)
                        cb.IsChecked = !cb.IsChecked;
                    e.Handled = true; // 阻止默认 toggle(单击不勾选)
                }
            }));

        _startMinimized = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        InitializeComponent();

        _vm = new MainViewModel(_history, _settings,
            new WindowDialogService(this, _history, _settings), new WpfUiDispatcher());
        DataContext = _vm;

        if (_startMinimized) { ShowInTaskbar = false; WindowState = WindowState.Minimized; }
        Icon = AppIcons.WindowIcon() ?? Icon;
        HookPasswordAutoMask();
        pwdSharePass.PasswordChanged += (_, _) => _vm.SharePassword = pwdSharePass.Password;
        _vm.Log("工具已启动，使用目录: " + _vm.RootPath);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var screen = SystemParameters.PrimaryScreenWidth > 0 ? SystemParameters.PrimaryScreenWidth : 1920;
        var screenH = SystemParameters.PrimaryScreenHeight > 0 ? SystemParameters.PrimaryScreenHeight : 1080;
        Width = Math.Min(1200, screen * 0.85);
        Height = Math.Min(900, screenH * 0.85);
        Left = (screen - Width) / 2;
        Top = (screenH - Height) / 2;
        tabs.SelectedIndex = 0;
        _vm.StartServer(showErrors: false);
        UpdateTheme(ThemeManager.IsDark);

        bool minimizeToTray = _startMinimized || _settings.Settings.Server.AutoStartAndListen;
        if (minimizeToTray)
        {
            _tray = new TrayIcon(ShowMainWindow, ExitApp);
            _tray.Show();
            Hide();
            _tray.Notify("BBK 工具已在后台运行，双击托盘图标可打开主界面，右键图标可完全退出。");
        }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // 「其他小工具」内嵌 TabControl 的切换事件会冒泡到这里，只处理外层标签切换
        if (!ReferenceEquals(e.OriginalSource, tabs)) return;

        if (tabs.SelectedIndex != (int)MainTab.Update)
            LockPasswordTab(MainTab.Update, updateOverlay, pwdUpdate, txtUpdatePwd, txtUpdateError, eyeLine);
        if (tabs.SelectedIndex != (int)MainTab.Transfer)
            LockPasswordTab(MainTab.Transfer, transferOverlay, pwdTransfer, txtTransferPwd, txtTransferError, eyeLineTransfer);
        if (tabs.SelectedIndex != (int)MainTab.Process)
            LockPasswordTab(MainTab.Process, processOverlay, pwdProcess, txtProcessPwd, txtProcessError, eyeLineProcess);

        _vm.SelectedTabIndex = tabs.SelectedIndex;
    }

    #region 密码保护

    /// <summary>重新上锁密码保护标签页：显示遮罩、清空密码并隐藏错误提示。</summary>
    private void LockPasswordTab(MainTab tab, Border overlay, PasswordBox pwd,
        TextBox txt, TextBlock error, System.Windows.Shapes.Line eyeLine)
    {
        overlay.Visibility = Visibility.Visible;
        ResetPasswordFields(pwd, txt, eyeLine);
        error.Visibility = Visibility.Collapsed;
        _vm.SetUnlocked(tab, false);
    }

    /// <summary>清空密码/明文内容并恢复到密码框可见状态。</summary>
    private static void ResetPasswordFields(PasswordBox pwd, TextBox txt, System.Windows.Shapes.Line eyeLine)
    {
        pwd.Clear();
        txt.Clear();
        pwd.Visibility = Visibility.Visible;
        txt.Visibility = Visibility.Collapsed;
        eyeLine.Visibility = Visibility.Visible;
    }

    private DateTime _lastUserInput = DateTime.Now;

    /// <summary>监听窗口键盘/鼠标活动；5 分钟无任何操作时，把已解锁的密码保护标签页重新上锁。</summary>
    private void HookPasswordAutoMask()
    {
        PreviewKeyDown += (_, _) => _lastUserInput = DateTime.Now;
        PreviewMouseMove += (_, _) => _lastUserInput = DateTime.Now;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) =>
        {
            if ((DateTime.Now - _lastUserInput).TotalMinutes < 5) return;
            // 同步/备份等操作进行中不上锁，待操作结束后重新计时 5 分钟
            if (_vm.IsBusy) { _lastUserInput = DateTime.Now; return; }
            bool lockedAny = false;
            if (updateOverlay.Visibility != Visibility.Visible)
            {
                LockPasswordTab(MainTab.Update, updateOverlay, pwdUpdate, txtUpdatePwd, txtUpdateError, eyeLine);
                lockedAny = true;
            }
            if (transferOverlay.Visibility != Visibility.Visible)
            {
                LockPasswordTab(MainTab.Transfer, transferOverlay, pwdTransfer, txtTransferPwd, txtTransferError, eyeLineTransfer);
                lockedAny = true;
            }
            if (processOverlay.Visibility != Visibility.Visible)
            {
                LockPasswordTab(MainTab.Process, processOverlay, pwdProcess, txtProcessPwd, txtProcessError, eyeLineProcess);
                lockedAny = true;
            }
            if (lockedAny)
            {
                _lastUserInput = DateTime.Now;
                _vm.Log("空闲超过 5 分钟，密码保护标签页已自动重新上锁。");
            }
        };
        timer.Start();
    }

    /// <summary>切换密码框/明文框可见性并同步内容。</summary>
    private static void TogglePasswordVisibility(PasswordBox pwd, TextBox txt, System.Windows.Shapes.Line eyeLine)
    {
        if (txt.Visibility == Visibility.Visible)
        {
            pwd.Password = txt.Text;
            pwd.Visibility = Visibility.Visible;
            txt.Visibility = Visibility.Collapsed;
            eyeLine.Visibility = Visibility.Visible;
            pwd.Focus();
        }
        else
        {
            txt.Text = pwd.Password;
            txt.Visibility = Visibility.Visible;
            pwd.Visibility = Visibility.Collapsed;
            eyeLine.Visibility = Visibility.Collapsed;
            txt.Focus();
            txt.CaretIndex = txt.Text.Length;
        }
    }

    private void PwdUpdate_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) BtnUpdateUnlock_Click(sender, e);
    }

    private void BtnTogglePwd_Click(object sender, RoutedEventArgs e)
        => TogglePasswordVisibility(pwdUpdate, txtUpdatePwd, eyeLine);

    private void BtnUpdateUnlock_Click(object sender, RoutedEventArgs e)
    {
        string stored = _settings.Settings.UpdatePassword;
        if (string.IsNullOrEmpty(stored))
        {
            updateOverlay.Visibility = Visibility.Collapsed;
            _vm.SetUnlocked(MainTab.Update, true);
            return;
        }
        string input = txtUpdatePwd.Visibility == Visibility.Visible ? txtUpdatePwd.Text : pwdUpdate.Password;
        if (input == stored)
        {
            updateOverlay.Visibility = Visibility.Collapsed;
            _vm.SetUnlocked(MainTab.Update, true);
        }
        else
        {
            txtUpdateError.Visibility = Visibility.Visible;
            pwdUpdate.Clear();
            pwdUpdate.Focus();
        }
    }

    private void PwdTransfer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { BtnTransferUnlock_Click(sender, e); e.Handled = true; }
    }

    private void BtnToggleTransferPwd_Click(object sender, RoutedEventArgs e)
        => TogglePasswordVisibility(pwdTransfer, txtTransferPwd, eyeLineTransfer);

    private void BtnTransferUnlock_Click(object sender, RoutedEventArgs e)
    {
        string pwd = pwdTransfer.Visibility == Visibility.Visible ? pwdTransfer.Password : txtTransferPwd.Text;
        if (pwd == _settings.Settings.UpdatePassword)
        {
            transferOverlay.Visibility = Visibility.Collapsed;
            _vm.SetUnlocked(MainTab.Transfer, true);
        }
        else
        {
            txtTransferError.Visibility = Visibility.Visible;
            pwdTransfer.Clear();
            pwdTransfer.Focus();
        }
    }

    private void PwdProcess_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { BtnProcessUnlock_Click(sender, e); e.Handled = true; }
    }

    private void BtnToggleProcessPwd_Click(object sender, RoutedEventArgs e)
        => TogglePasswordVisibility(pwdProcess, txtProcessPwd, eyeLineProcess);

    private void BtnProcessUnlock_Click(object sender, RoutedEventArgs e)
    {
        string pwd = pwdProcess.Visibility == Visibility.Visible ? pwdProcess.Password : txtProcessPwd.Text;
        if (pwd == _settings.Settings.UpdatePassword)
        {
            processOverlay.Visibility = Visibility.Collapsed;
            _vm.SetUnlocked(MainTab.Process, true);
        }
        else
        {
            txtProcessError.Visibility = Visibility.Visible;
        }
    }

    #endregion

    #region 窗口与日志

    private void Window_StateChanged(object sender, EventArgs e)
    {
        // 最大化时展平圆角避免屏幕四角露出桌面，并用主屏尺寸约束窗口，
        // 避免 WindowChrome 的 ResizeBorderThickness 使窗口向屏幕四周溢出；
        // 还原后恢复圆角并清除约束（覆盖按钮 / 双击标题栏 / Win+↑ 等所有路径）。
        bool maximized = WindowState == WindowState.Maximized;
        rootBorder.CornerRadius = new CornerRadius(maximized ? 0 : 12);
        CornerClip.SetRadius(rootContent, maximized ? 0 : 11);
        if (maximized)
        {
            MaxWidth = SystemParameters.MaximizedPrimaryScreenWidth;
            MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
        }
        else
        {
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
        }
        // 勾选开机自启后：启动时后台驻留托盘，正常最小化到任务栏，点击关闭再最小化到托盘。
        // 托盘驻留与否由 MainWindow_Loaded 决定；Window_Closing 已处理关闭到托盘。
    }

    private void TitleMin_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }

    private void TitleMax_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void TitleClose_Click(object sender, RoutedEventArgs e) { Close(); }

    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        // 主题字典替换会重建日志框模板并把滚动位置重置到顶部，这里记录偏移并在布局完成后恢复
        double offset = txtLog.VerticalOffset;

        // 切换前抓取当前界面快照，切换后让旧画面淡出，形成主题交叉过渡
        var snapshot = CaptureSnapshot();

        ThemeManager.Toggle();
        // 主题固定到配置文件，下次启动直接读取
        _vm.SaveSettings();
        RestoreLogScroll(offset);
        SpinThemeIcon();
        FadeOutSnapshot(snapshot);
    }

    /// <summary>点击主题按钮时让图标旋转一周，强化切换反馈。</summary>
    private void SpinThemeIcon()
    {
        if (btnThemeToggle.Template.FindName("icon", btnThemeToggle) is not TextBlock icon) return;
        icon.RenderTransformOrigin = new Point(0.5, 0.5);
        if (icon.RenderTransform is not RotateTransform rotate)
        {
            rotate = new RotateTransform(0);
            icon.RenderTransform = rotate;
        }
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(560))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    /// <summary>抓取整个窗口内容快照；尺寸异常时返回 null（不做过渡，不影响主题切换）。</summary>
    private ImageSource? CaptureSnapshot()
    {
        // 若上一次过渡尚未结束，先移除叠层，避免把旧快照一起拍进去
        themeFadeHost.BeginAnimation(UIElement.OpacityProperty, null);
        themeFadeHost.Visibility = Visibility.Collapsed;
        themeFadeTint.BeginAnimation(UIElement.OpacityProperty, null);
        themeFadeOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        themeFadeOverlay.Source = null;

        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(rootHost.ActualWidth * dpi.DpiScaleX);
        int h = (int)Math.Ceiling(rootHost.ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return null;

        var bmp = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bmp.Render(rootHost);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>用快照覆盖新主题画面，并配合亮度呼吸淡出，形成平滑的主题切换过渡。</summary>
    private void FadeOutSnapshot(ImageSource? snapshot)
    {
        if (snapshot is null) return;

        themeFadeOverlay.Source = snapshot;
        themeFadeOverlay.Opacity = 1;
        themeFadeTint.Opacity = 0;
        themeFadeHost.Opacity = 1;
        themeFadeHost.Visibility = Visibility.Visible;

        // 缓入缓出更接近连续变化，避免 CubicEase/前快后慢造成的“突然变色”感
        const int durationMs = 260;
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = ease
        };
        fade.Completed += (_, _) =>
        {
            themeFadeOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            themeFadeTint.BeginAnimation(UIElement.OpacityProperty, null);
            themeFadeOverlay.Opacity = 1;
            themeFadeTint.Opacity = 0;
            themeFadeHost.Visibility = Visibility.Collapsed;
            themeFadeOverlay.Source = null;
        };
        themeFadeOverlay.BeginAnimation(UIElement.OpacityProperty, fade);

        // 呼吸灯：先随新主题背景提亮/压暗，再平滑回落到新界面本身
        var breathe = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(durationMs)
        };
        breathe.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        breathe.KeyFrames.Add(new EasingDoubleKeyFrame(0.26, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        breathe.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        themeFadeTint.BeginAnimation(UIElement.OpacityProperty, breathe);
    }

    private void RestoreLogScroll(double offset)
    {
        // 两次 Background 回调：第一次等主题资源生效，第二次等文档布局完成、可滚动高度恢复后再定位
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            txtLog.ScrollToVerticalOffset(offset);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => txtLog.ScrollToVerticalOffset(offset)));
        }));
    }

    internal void UpdateTheme(bool dark)
    {
        btnThemeToggle.ToolTip = dark ? "切换为亮色主题" : "切换为暗色主题";

        var icon = btnThemeToggle.Template.FindName("icon", btnThemeToggle) as TextBlock;
        if (icon != null) icon.Text = dark ? "☾" : "☀";
    }

    private void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitApp() { Dispatcher.Invoke(() => { _allowExit = true; Close(); }); }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_tray != null && !_allowExit)
        {
            e.Cancel = true;
            Hide();
            _tray.Notify("BBK 工具仍在后台运行：双击托盘图标打开主界面，右键图标选择“退出”可完全退出。");
            return;
        }
        _allowExit = true;
        _vm.SaveSettings();
        _vm.Coordinator.Cancel();
        _vm.StopServer();
        _tray?.Dispose();
        _tray = null;
    }

    #endregion
}
