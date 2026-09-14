using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LanFileSync;

public partial class DarkMessageBox : Window
{
    private DarkMessageBox() { InitializeComponent(); }

    public static MessageBoxResult Show(string messageBoxText, string caption = "提示",
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        var owner = Application.Current.MainWindow;
        var dlg = new DarkMessageBox();
        dlg.txtTitle.Text = caption;
        dlg.txtMessage.Text = messageBoxText;

        dlg.txtIcon.Text = icon switch
        {
            MessageBoxImage.Information => "\u2139",
            MessageBoxImage.Warning => "\u26A0",
            MessageBoxImage.Error => "\u2716",
            MessageBoxImage.Question => "\u2753",
            _ => ""
        };
        dlg.txtIcon.Foreground = icon switch
        {
            MessageBoxImage.Error => new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)),
            MessageBoxImage.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0xD4, 0x3B)),
            _ => (Brush)Application.Current.FindResource("AccentBrush")
        };

        void AddBtn(string content, MessageBoxResult result, bool isAccent)
        {
            var btn = new Button
            {
                Content = content,
                Padding = new Thickness(16, 6, 16, 6),
                MinHeight = 30,
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = result
            };
            btn.Style = isAccent
                ? (Style)Application.Current.FindResource("AccentButton")
                : (Style)Application.Current.TryFindResource(typeof(Button)) ?? new Style(typeof(Button));
            btn.Click += (_, _) => { dlg.DialogResult = result == MessageBoxResult.OK || result == MessageBoxResult.Yes; dlg.Close(); };
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

        if (owner != null && owner != dlg)
        {
            dlg.Owner = owner;
        }
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dlg.ShowDialog();
        return dlg.DialogResult == true
            ? (button == MessageBoxButton.YesNo || button == MessageBoxButton.YesNoCancel ? MessageBoxResult.Yes : MessageBoxResult.OK)
            : (button == MessageBoxButton.YesNo || button == MessageBoxButton.YesNoCancel ? MessageBoxResult.No : MessageBoxResult.Cancel);
    }
}
