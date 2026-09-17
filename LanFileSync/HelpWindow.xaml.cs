using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DocList = System.Windows.Documents.List;

namespace LanFileSync;

public partial class HelpWindow : Window
{
    private static readonly FontFamily MonoFont = new("Consolas, Cascadia Mono, Courier New");
    private static readonly Regex InlineRegex =
        new(@"(\*\*.+?\*\*|`[^`\r\n]+`|\[[^]\r\n]+\]\([^)\r\n]+\))", RegexOptions.Compiled);
    private static readonly Regex ListItemRegex =
        new(@"^(?:(\d+)\.|-)\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex NestedBulletRegex =
        new(@"^\s{2,}-\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex NestedOrderedRegex =
        new(@"^\s{2,}\d+\.\s+(.+)$", RegexOptions.Compiled);

    private sealed class DocTheme
    {
        public required Brush Main { get; init; }
        public required Brush Sub { get; init; }
        public required Brush Accent { get; init; }
        public required Brush Border { get; init; }
        public required Brush CodeBlockBg { get; init; }
        public required Brush CodeBlockFg { get; init; }
        public required Brush InlineCodeBg { get; init; }
        public required Brush InlineCodeFg { get; init; }
        public required Brush QuoteBg { get; init; }
        public required Brush TableHeaderBg { get; init; }
    }

    public HelpWindow()
    {
        InitializeComponent();
        viewer.Document = BuildDocument(LoadReadme());
    }

    private static string LoadReadme()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("README.md", StringComparison.OrdinalIgnoreCase));
        if (name == null)
            return "# 未找到说明文档\n\nREADME.md 未嵌入程序，请联系发布者。";
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private DocTheme BuildTheme()
    {
        bool dark = ThemeManager.IsDark;
        Brush Res(string key) => (Brush)FindResource(key);
        Brush Solid(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
        return new DocTheme
        {
            Main = Res("TextMainBrush"),
            Sub = Res("TextSubBrush"),
            Accent = Res("AccentBrush"),
            Border = Res("CardBorderBrush"),
            CodeBlockBg = dark ? Solid(0x0F, 0x16, 0x20) : Solid(0xF3, 0xF5, 0xF9),
            CodeBlockFg = dark ? Solid(0xC9, 0xD6, 0xE5) : Solid(0x33, 0x3B, 0x49),
            InlineCodeBg = dark ? Solid(0x12, 0x24, 0x33) : Solid(0xEC, 0xEF, 0xF6),
            InlineCodeFg = dark ? Solid(0x67, 0xE8, 0xF9) : Solid(0xB4, 0x23, 0x18),
            QuoteBg = dark ? Solid(0x12, 0x1A, 0x26) : Solid(0xF0, 0xF4, 0xFB),
            TableHeaderBg = dark ? Solid(0x16, 0x20, 0x2F) : Solid(0xEA, 0xF0, 0xFA),
        };
    }

    private FlowDocument BuildDocument(string markdown)
    {
        var theme = BuildTheme();
        var doc = new FlowDocument
        {
            FontSize = 13.5,
            PagePadding = new Thickness(22, 16, 22, 22),
            Foreground = theme.Main,
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI Variable, Segoe UI"),
        };

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            string t = line.Trim();

            if (t.Length == 0) { i++; continue; }

            // 围栏代码块
            if (t.StartsWith("```"))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    code.Add(lines[i]);
                    i++;
                }
                i++; // 跳过结束围栏
                doc.Blocks.Add(MakeCodeBlock(code, theme));
                continue;
            }

            // 标题
            if (t[0] == '#')
            {
                int level = t.TakeWhile(c => c == '#').Count();
                if (level is >= 1 and <= 6 && t.Length > level && t[level] == ' ')
                {
                    doc.Blocks.Add(MakeHeading(level, t[(level + 1)..].Trim(), theme));
                    i++;
                    continue;
                }
            }

            // 分隔线
            if (t is "---" or "***" or "___")
            {
                doc.Blocks.Add(new BlockUIContainer(
                    new Rectangle { Height = 1, Fill = theme.Border, Margin = new Thickness(0, 8, 0, 8) }));
                i++;
                continue;
            }

            // 引用块（连续 > 行合并）
            if (t.StartsWith(">"))
            {
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">"))
                {
                    quote.Append(lines[i].TrimStart().TrimStart('>').TrimStart());
                    i++;
                }
                doc.Blocks.Add(MakeQuote(quote.ToString(), theme));
                continue;
            }

            // 表格
            if (t.StartsWith("|") && i + 1 < lines.Length && IsTableSeparator(lines[i + 1].Trim()))
            {
                var rows = new List<string> { t };
                i += 2; // 跳过表头与分隔行
                while (i < lines.Length && lines[i].Trim().StartsWith("|"))
                {
                    rows.Add(lines[i].Trim());
                    i++;
                }
                doc.Blocks.Add(MakeTable(rows, theme));
                continue;
            }

            // 列表（顶层行首，不含缩进）
            var topMatch = ListItemRegex.Match(t);
            if (topMatch.Success && !line.StartsWith(' '))
            {
                var raw = new List<string>();
                while (i < lines.Length)
                {
                    string l = lines[i];
                    if (l.Trim().Length == 0)
                    {
                        // 空行后若仍是缩进续行则保留在列表中
                        if (i + 1 < lines.Length && lines[i + 1].StartsWith("  ") &&
                            lines[i + 1].Trim().Length > 0)
                        {
                            i++;
                            continue;
                        }
                        break;
                    }
                    if (ListItemRegex.IsMatch(l) || l.StartsWith(' '))
                        raw.Add(l);
                    else
                        break;
                    i++;
                }
                doc.Blocks.Add(BuildList(raw, topMatch.Groups[1].Success, theme));
                continue;
            }

            // 普通段落
            var paraLines = new List<string> { t };
            i++;
            while (i < lines.Length)
            {
                string l = lines[i];
                string lt = l.Trim();
                if (lt.Length == 0 || lt.StartsWith("```") || lt.StartsWith("#") ||
                    lt.StartsWith(">") || lt.StartsWith("|") || lt is "---" or "***" or "___" ||
                    (!l.StartsWith(' ') && ListItemRegex.IsMatch(lt)))
                    break;
                paraLines.Add(lt);
                i++;
            }
            var para = new Paragraph { Margin = new Thickness(0, 3, 0, 3) };
            AddInlines(para.Inlines, string.Concat(paraLines), theme);
            doc.Blocks.Add(para);
        }

        return doc;
    }

    private static bool IsTableSeparator(string s)
        => s.Contains('-') && s.All(c => c is '-' or '|' or ':' or ' ');

    private Block MakeHeading(int level, string text, DocTheme theme)
    {
        double size = level switch { 1 => 21, 2 => 17, 3 => 14.5, _ => 13.5 };
        var p = new Paragraph
        {
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Foreground = level <= 2 ? theme.Accent : theme.Main,
            Margin = new Thickness(0, level <= 2 ? 14 : 10, 0, 6),
        };
        AddInlines(p.Inlines, text, theme);
        return p;
    }

    private Block MakeCodeBlock(List<string> code, DocTheme theme)
    {
        var section = new Section
        {
            Background = theme.CodeBlockBg,
            Foreground = theme.CodeBlockFg,
            BorderBrush = theme.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 6, 0, 8),
        };
        var para = new Paragraph
        {
            Margin = new Thickness(0),
            FontFamily = MonoFont,
            FontSize = 12.5,
        };
        for (int k = 0; k < code.Count; k++)
        {
            if (k > 0) para.Inlines.Add(new LineBreak());
            // 用不换行空格保留前导/连续空格，确保 ASCII 图表对齐
            para.Inlines.Add(new Run(code[k].Replace("\t", "    ").Replace(' ', '\u00A0')));
        }
        section.Blocks.Add(para);
        return section;
    }

    private Block MakeQuote(string text, DocTheme theme)
    {
        var section = new Section
        {
            Background = theme.QuoteBg,
            BorderBrush = theme.Accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(0, 6, 0, 8),
        };
        var para = new Paragraph { Margin = new Thickness(0), Foreground = theme.Sub };
        AddInlines(para.Inlines, text, theme);
        section.Blocks.Add(para);
        return section;
    }

    private Block MakeTable(List<string> rows, DocTheme theme)
    {
        static string[] SplitCells(string row)
        {
            string s = row.Trim();
            if (s.StartsWith('|')) s = s[1..];
            if (s.EndsWith('|')) s = s[..^1];
            return s.Split('|').Select(c => c.Trim()).ToArray();
        }

        var header = SplitCells(rows[0]);
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 6, 0, 10),
            BorderBrush = theme.Border,
            BorderThickness = new Thickness(1),
        };
        for (int c = 0; c < header.Length; c++)
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

        var group = new TableRowGroup();
        for (int ri = 0; ri < rows.Count; ri++)
        {
            var cells = SplitCells(rows[ri]);
            var row = new TableRow();
            for (int ci = 0; ci < header.Length; ci++)
            {
                var cellPara = new Paragraph { Margin = new Thickness(0) };
                AddInlines(cellPara.Inlines, ci < cells.Length ? cells[ci] : "", theme);
                bool lastCol = ci == header.Length - 1;
                bool lastRow = ri == rows.Count - 1;
                var cell = new TableCell(cellPara)
                {
                    Padding = new Thickness(8, 5, 8, 5),
                    BorderBrush = theme.Border,
                    BorderThickness = new Thickness(0, 0, lastCol ? 0 : 1, lastRow ? 0 : 1),
                };
                if (ri == 0)
                {
                    cell.Background = theme.TableHeaderBg;
                    cellPara.FontWeight = FontWeights.SemiBold;
                }
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private DocList BuildList(List<string> raw, bool topOrdered, DocTheme theme)
    {
        var list = new DocList
        {
            MarkerStyle = topOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            MarkerOffset = 0,
            Margin = new Thickness(0, 4, 0, 4),
        };

        ListItem? current = null;
        DocList? subList = null;
        ListItem? lastSub = null;

        foreach (var rawLine in raw)
        {
            if (rawLine.Trim().Length == 0) continue;

            var top = ListItemRegex.Match(rawLine);
            if (top.Success && !rawLine.StartsWith(' '))
            {
                var para = new Paragraph { Margin = new Thickness(0) };
                AddInlines(para.Inlines, top.Groups[2].Value, theme);
                current = new ListItem(para) { Margin = new Thickness(0, 2, 0, 2) };
                subList = null;
                lastSub = null;
                list.ListItems.Add(current);
                continue;
            }

            if (current == null) continue;

            var nestedBullet = NestedBulletRegex.Match(rawLine);
            var nestedOrdered = NestedOrderedRegex.Match(rawLine);
            if (nestedBullet.Success || nestedOrdered.Success)
            {
                string content = nestedBullet.Success
                    ? nestedBullet.Groups[1].Value
                    : nestedOrdered.Groups[1].Value;
                if (subList == null)
                {
                    subList = new DocList
                    {
                        MarkerStyle = nestedBullet.Success ? TextMarkerStyle.Circle : TextMarkerStyle.Decimal,
                        Margin = new Thickness(0),
                    };
                    current.Blocks.Add(subList);
                }
                var para = new Paragraph { Margin = new Thickness(0) };
                AddInlines(para.Inlines, content, theme);
                lastSub = new ListItem(para) { Margin = new Thickness(0, 2, 0, 2) };
                subList.ListItems.Add(lastSub);
            }
            else
            {
                // 缩进续行：追加到当前段落（列表项或最后一个子项）
                Paragraph target = lastSub != null
                    ? (Paragraph)lastSub.Blocks.FirstBlock!
                    : (Paragraph)current.Blocks.FirstBlock!;
                AddInlines(target.Inlines, rawLine.Trim(), theme);
            }
        }

        return list;
    }

    private void AddInlines(InlineCollection target, string text, DocTheme theme)
    {
        int pos = 0;
        foreach (Match m in InlineRegex.Matches(text))
        {
            if (m.Index > pos)
                target.Add(new Run(text[pos..m.Index]));

            string v = m.Value;
            if (v.StartsWith("**"))
            {
                var bold = new Bold();
                bold.Inlines.Add(new Run(v[2..^2]));
                target.Add(bold);
            }
            else if (v.StartsWith('`'))
            {
                target.Add(new Run(v[1..^1])
                {
                    FontFamily = MonoFont,
                    FontSize = 12.3,
                    Foreground = theme.InlineCodeFg,
                    Background = theme.InlineCodeBg,
                });
            }
            else
            {
                int close = v.IndexOf(']');
                string label = v[1..close];
                string url = v[(close + 2)..^1];
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var link = new Hyperlink
                    {
                        NavigateUri = new Uri(url),
                        Cursor = Cursors.Hand,
                    };
                    link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                    link.Inlines.Add(new Run(label));
                    link.RequestNavigate += (_, e) =>
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                        }
                        catch { }
                    };
                    target.Add(link);
                }
                else
                {
                    // 文档内锚点等非 http 链接仅显示文字
                    target.Add(new Run(label));
                }
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            target.Add(new Run(text[pos..]));
    }

    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // 最大化时展平圆角并约束尺寸，还原时恢复
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
