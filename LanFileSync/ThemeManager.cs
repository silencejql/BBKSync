using System.Windows;
using System.Windows.Media;

namespace LanFileSync;

public static class ThemeManager
{
    private static readonly ResourceDictionary DarkDict = new() { Source = new Uri("Themes/Dark.xaml", UriKind.Relative) };
    private static readonly ResourceDictionary LightDict = new() { Source = new Uri("Themes/Light.xaml", UriKind.Relative) };

    public static bool IsDark { get; private set; } = false;

    public static void Apply(bool dark)
    {
        var app = Application.Current;
        if (app == null) return;

        var merged = app.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(dark ? DarkDict : LightDict);

        app.Resources["WindowBgBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x0D, 0x11, 0x17) : Color.FromRgb(0xF3, 0xF5, 0xF9));
        app.Resources["CardBorderBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x1E, 0x24, 0x33) : Color.FromRgb(0xE1, 0xE6, 0xEE));
        app.Resources["AccentBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x00, 0xD4, 0xFF) : Color.FromRgb(0x25, 0x63, 0xEB));
        app.Resources["InputBorderBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x2A, 0x30, 0x40) : Color.FromRgb(0xC9, 0xD2, 0xE0));
        app.Resources["TextMainBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0xE6, 0xED, 0xF3) : Color.FromRgb(0x20, 0x24, 0x2E));
        app.Resources["TextSubBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x7D, 0x85, 0x90) : Color.FromRgb(0x6B, 0x72, 0x80));

        IsDark = dark;

        foreach (Window w in app.Windows)
            if (w is MainWindow mw) mw.UpdateTheme(dark);
    }

    public static void Toggle() => Apply(!IsDark);
}
