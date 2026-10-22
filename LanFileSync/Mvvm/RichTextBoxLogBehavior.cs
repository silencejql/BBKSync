using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace LanFileSync.Mvvm;

/// <summary>
/// 把 ObservableCollection&lt;LogEntry&gt; 绑定到 RichTextBox 的附加行为：
/// 追加彩色段落、自动滚底，并保留文档尾部的占位块（保证滚到底时最后一条不被悬浮按钮遮挡）。
/// </summary>
public static class LogView
{
    public static readonly DependencyProperty EntriesProperty = DependencyProperty.RegisterAttached(
        "Entries", typeof(IEnumerable<LogEntry>), typeof(LogView),
        new PropertyMetadata(null, OnEntriesChanged));

    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(NotifyCollectionChangedEventHandler), typeof(LogView),
        new PropertyMetadata(null));

    public static void SetEntries(DependencyObject element, IEnumerable<LogEntry>? value)
        => element.SetValue(EntriesProperty, value);

    public static IEnumerable<LogEntry>? GetEntries(DependencyObject element)
        => (IEnumerable<LogEntry>?)element.GetValue(EntriesProperty);

    private static void OnEntriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box) return;

        if (box.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler oldHandler &&
            e.OldValue is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= oldHandler;
        }
        box.SetValue(HandlerProperty, null);

        RemoveAllParagraphs(box);

        if (e.NewValue is IEnumerable<LogEntry> items)
        {
            foreach (var entry in items) Append(box, entry);
        }

        if (e.NewValue is INotifyCollectionChanged collection)
        {
            NotifyCollectionChangedEventHandler handler = (_, args) => OnCollectionChanged(box, args);
            box.SetValue(HandlerProperty, handler);
            collection.CollectionChanged += handler;
        }
    }

    private static void OnCollectionChanged(RichTextBox box, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                    foreach (LogEntry entry in e.NewItems) Append(box, entry);
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems != null)
                    for (int i = 0; i < e.OldItems.Count; i++) RemoveFirstParagraph(box);
                break;
            case NotifyCollectionChangedAction.Reset:
                RemoveAllParagraphs(box);
                break;
        }
    }

    private static void Append(RichTextBox box, LogEntry entry)
    {
        var paragraph = new Paragraph(new Run(entry.Text) { Foreground = BrushFor(entry.Kind) })
        {
            Margin = new Thickness(0)
        };
        var blocks = box.Document.Blocks;
        if (blocks.LastBlock is BlockUIContainer spacer) blocks.InsertBefore(spacer, paragraph);
        else blocks.Add(paragraph);

        // 文档实际高度在 Render 优先级的布局阶段才更新；连续多条日志快速追加时
        // 立即 ScrollToEnd 拿到的 ScrollableHeight 是旧值，会沉不到底，
        // 再在 Background 优先级（布局完成后）补滚一次确保最后一条到达视口底端。
        box.ScrollToEnd();
        box.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(box.ScrollToEnd));
    }

    private static void RemoveFirstParagraph(RichTextBox box)
    {
        if (box.Document.Blocks.FirstBlock is Paragraph first)
            box.Document.Blocks.Remove(first);
    }

    private static void RemoveAllParagraphs(RichTextBox box)
    {
        var toRemove = box.Document.Blocks.OfType<Paragraph>().ToList();
        foreach (var paragraph in toRemove) box.Document.Blocks.Remove(paragraph);
    }

    private static Brush BrushFor(LogKind kind) => kind switch
    {
        LogKind.Error => new SolidColorBrush(ThemeManager.IsDark
            ? Color.FromRgb(0xFF, 0x6B, 0x6B)
            : Color.FromRgb(0xDC, 0x26, 0x26)),
        LogKind.Success => new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),
        _ => new SolidColorBrush(ThemeManager.IsDark
            ? Color.FromRgb(0xE6, 0xED, 0xF3)
            : Color.FromRgb(0x20, 0x24, 0x2E)),
    };
}
