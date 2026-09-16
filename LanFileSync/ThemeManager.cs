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

        app.Resources["WindowBgBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x0B, 0x0F, 0x16) : Color.FromRgb(0xF2, 0xF4, 0xF8));
        app.Resources["CardBorderBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x23, 0x2C, 0x3C) : Color.FromRgb(0xE3, 0xE8, 0xF0));
        app.Resources["AccentBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x00, 0xD4, 0xFF) : Color.FromRgb(0x25, 0x63, 0xEB));
        app.Resources["InputBorderBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x2E, 0x3A, 0x4E) : Color.FromRgb(0xCB, 0xD4, 0xE1));
        app.Resources["TextMainBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0xE6, 0xED, 0xF3) : Color.FromRgb(0x1F, 0x24, 0x30));
        app.Resources["TextSubBrush"] = new SolidColorBrush(dark ? Color.FromRgb(0x8B, 0x95, 0xA7) : Color.FromRgb(0x6B, 0x72, 0x80));

        IsDark = dark;

        foreach (Window w in app.Windows)
            if (w is MainWindow mw) mw.UpdateTheme(dark);
    }

    public static void Toggle() => Apply(!IsDark);
}