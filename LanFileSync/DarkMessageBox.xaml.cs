using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LanFileSync;

public partial class DarkMessageBox : Window
{
    private MessageBoxResult _result = MessageBoxResult.Cancel;
    private MessageBoxButton _buttons = MessageBoxButton.OK;
    private bool _closing;

    private DarkMessageBox()
    { InitializeComponent(); }

    public static MessageBoxResult Show(string messageBoxText, string caption = "提示",
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        var owner = Application.Current.MainWindow;
        var dlg = new DarkMessageBox { _buttons = button };
        dlg.txtTitle.Text = caption;
        dlg.txtMessage.Text = messageBoxText;

        ApplyIcon(dlg, icon);
        BuildButtons(dlg, button);

        if (owner != null && owner != dlg)
            dlg.Owner = owner;
        else
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dlg.ShowDialog();
        return dlg._result;
    }

    private static void ApplyIcon(DarkMessageBox dlg, MessageBoxImage icon)
    {
        Color color;
        string glyph;
        switch (icon)
        {
            case MessageBoxImage.Information:
                color = ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color;
                glyph = "\u2139"; // ℹ
                break;
            case MessageBoxImage.Warning:
                color = Color.FromRgb(0xF5, 0xA6, 0x23);
                glyph = "\u26A0"; // ⚠
                break;
            case MessageBoxImage.Error:
                color = Color.FromRgb(0xE5, 0x48, 0x4D);
                glyph = "\u2715"; // ✕
                break;
            case MessageBoxImage.Question:
                color = ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color;
                glyph = "?";
                break;
            default:
                dlg.badgeIcon.Visibility = Visibility.Collapsed;
                return;
        }

        dlg.txtIcon.Text = glyph;
        dlg.txtIcon.Foreground = new SolidColorBrush(color);
        // 徽章底色为图标色 14% 透明度
        dlg.badgeIcon.Background = new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B));
    }

    private static void BuildButtons(DarkMessageBox dlg, MessageBoxButton button)
    {
        void AddBtn(string content, MessageBoxResult result, bool isAccent)
        {
            var btn = new Button
            {
                Content = content,
                MinHeight = 34,
                Margin = new Thickness(10, 0, 0, 0),
                Tag = result,
            };
            btn.Style = isAccent
                ? (Style)Application.Current.FindResource("AccentButton")
                : (Style)dlg.FindResource("DlgSecondaryButton");
            btn.Click += (_, _) => dlg.BeginClose(result);
            dlg.panelButtons.Children.Add(btn);
        }

        switch (button)
        {
            case MessageBoxButton.OK:
                AddBtn("确定", MessageBoxResult.OK, true);
                break;
            case MessageBoxButton.OKCancel:
                AddBtn("取消", MessageBoxResult.Cancel, false);
                AddBtn("确定", MessageBoxResult.OK, true);
                break;
            case MessageBoxButton.YesNo:
                AddBtn("否", MessageBoxResult.No, false);
                AddBtn("是", MessageBoxResult.Yes, true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddBtn("取消", MessageBoxResult.Cancel, false);
                AddBtn("否", MessageBoxResult.No, false);
                AddBtn("是", MessageBoxResult.Yes, true);
                break;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        // 关闭按钮等同于取消/否
        BeginClose(_buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
            ? MessageBoxResult.No : MessageBoxResult.Cancel);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => AnimateOpen();

    /// <summary>打开：从 90% 缩放平滑放大到原尺寸，同时淡入。</summary>
    private void AnimateOpen()
    {
        var scaleEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scaleIn = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = scaleEase
        };
        var scaleInY = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = scaleEase
        };
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };

        dlgScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleIn);
        dlgScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleInY);
        dlgRoot.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    /// <summary>关闭：缩放回收到 92% 并淡出，动画结束后才真正关闭窗口。</summary>
    private void BeginClose(MessageBoxResult result)
    {
        if (_closing) return;
        _closing = true;
        _result = result;
        dlgRoot.IsHitTestVisible = false;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var scaleOut = new DoubleAnimation(1, 0.92, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = ease
        };
        var scaleOutY = new DoubleAnimation(1, 0.92, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = ease
        };
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) => Close();

        dlgScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleOut);
        dlgScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleOutY);
        dlgRoot.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // Alt+F4 等非按钮路径也先播完关闭动画再真正关闭
        if (_closing) return;
        e.Cancel = true;
        BtnClose_Click(sender, new RoutedEventArgs());
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            BtnClose_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // 回车触发主按钮(确定/是)
            if (panelButtons.Children.Count > 0 &&
                panelButtons.Children[^1] is Button primary)
            {
                primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
            }
        }
    }
}
