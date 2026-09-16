using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LanFileSync;

public partial class DarkMessageBox : Window
{
    private MessageBoxResult _result = MessageBoxResult.Cancel;
    private MessageBoxButton _buttons = MessageBoxButton.OK;

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
            btn.Click += (_, _) =>
            {
                dlg._result = result;
                dlg.DialogResult = result is MessageBoxResult.OK or MessageBoxResult.Yes;
                dlg.Close();
            };
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
        _result = _buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
            ? MessageBoxResult.No : MessageBoxResult.Cancel;
        DialogResult = false;
        Close();
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
