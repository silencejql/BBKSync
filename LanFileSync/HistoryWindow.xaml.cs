using System.Windows;
using System.Windows.Controls;

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
        if (MessageBox.Show($"删除记录 {host}？\n只删除历史记录，不影响电脑文件。", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _store.Remove(host);
        lvHistory.Items.Refresh();
        LoadSelected();
    }
}