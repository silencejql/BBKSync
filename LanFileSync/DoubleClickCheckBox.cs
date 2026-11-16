using System.Windows.Controls;
using System.Windows.Input;

namespace LanFileSync;

/// <summary>仅双击切换的复选框：单击只获取焦点，不改变勾选状态。
/// 直接屏蔽 OnClick，避免任何鼠标/键盘路径带来的单击切换。</summary>
public sealed class DoubleClickCheckBox : CheckBox
{
    protected override void OnClick()
    {
        // 单击不会走到这里切换：勾选只由双击或代码赋值改变
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ClickCount >= 2)
            IsChecked = !IsChecked;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            IsChecked = !IsChecked;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
