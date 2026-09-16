using System.Windows;
using System.Windows.Media;

namespace LanFileSync;

/// <summary>
/// 附加属性：按控件实际尺寸维护圆角矩形裁剪几何，
/// 使任意控件（含自带方形边框/背景的 ListView、DataGrid、窗口内容区）呈现平滑圆角。
/// Radius 设为 0 时取消裁剪。
/// </summary>
public static class CornerClip
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached("Radius", typeof(double), typeof(CornerClip),
            new FrameworkPropertyMetadata(0.0, OnRadiusChanged));

    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.SizeChanged -= OnSizeChanged;
        if (GetRadius(fe) > 0)
        {
            fe.SizeChanged += OnSizeChanged;
            Apply(fe);
        }
        else
        {
            fe.ClearValue(UIElement.ClipProperty);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement fe) Apply(fe);
    }

    private static void Apply(FrameworkElement fe)
    {
        double radius = GetRadius(fe);
        if (radius <= 0 || fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return;
        fe.Clip = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), radius, radius);
    }
}
