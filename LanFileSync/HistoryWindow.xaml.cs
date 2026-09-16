using System.Windows;

namespace LanFileSync;

public partial class HistoryWindow : Window
{
    private readonly HistoryStore _store;

    public HistoryWindow(HistoryStore store)
    {
        InitializeComponent();
        _store = store;
        lvHistory.ItemsSource = store.Entries;
        lvHistory.SelectionChanged += (_, _) => LoadSelected();
        if (store.Entries.Count > 0)
            lvHistory.SelectedIndex = 0;
    }

    private PeerHistoryEntry? Selected => lvHistory.SelectedItem as PeerHistoryEntry;

    private void LoadSelected()
    {
        txtNote.Text = Selected?.Note ?? "";
        txtNote.IsEnabled = Selected != null;
    }

    private void BtnSaveNote_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null)
            return;
        _store.SetNote(Selected.Host, txtNote.Text);
        lvHistory.Items.Refresh();
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null)
            return;
        string host = Selected.Host;
        if (DarkMessageBox.Show($"删除记录 {host}？\n只删除历史记录，不影响电脑文件。", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _store.Remove(host);
        lvHistory.Items.Refresh();
        LoadSelected();
    }

    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // 最大化时展平圆角；同时用主屏尺寸约束窗口，
        // 避免 WindowChrome 的 ResizeBorderThickness 使窗口向屏幕四周溢出。
        // 还原时恢复圆角并清除约束（覆盖双击标题栏 / Win+↑ 等所有最大化路径）。
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
    }
}