using System.Collections.ObjectModel;
using System.Windows;

namespace LanFileSync;

public partial class ComputerNameWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly ObservableCollection<ComputerMapping> _items = new();

    public ComputerNameWindow(SettingsStore settings)
    {
        InitializeComponent();
        _settings = settings;
        foreach (var item in _settings.Settings.Computers)
            _items.Add(item);
        grid.ItemsSource = _items;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _items.Add(new ComputerMapping());
        grid.SelectedIndex = _items.Count - 1;
        grid.ScrollIntoView(grid.SelectedItem);
        grid.Focus();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is ComputerMapping item)
            _items.Remove(item);
    }

    private void SaveClose_Click(object sender, RoutedEventArgs e)
    {
        var list = _settings.Settings.Computers;
        list.Clear();
        foreach (var c in _items)
        {
            if (!string.IsNullOrWhiteSpace(c.Ip))
                list.Add(c);
        }
        _settings.Save();
        DialogResult = true;
        Close();
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