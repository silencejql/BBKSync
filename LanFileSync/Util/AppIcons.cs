using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LanFileSync;

internal static class AppIcons
{
    private static byte[]? _bytes;

    private static byte[] GetIcoBytes()
    {
        if (_bytes != null)
            return _bytes;

        byte[]? data = null;
        try
        {
            var sri = Application.GetResourceStream(new Uri("app.ico", UriKind.Relative));
            using var ms = new MemoryStream();
            sri!.Stream.CopyTo(ms);
            data = ms.ToArray();
        }
        catch { }

        if (data == null)
        {
            try
            {
                using var s = typeof(AppIcons).Assembly.GetManifestResourceStream("LanFileSync.app.ico");
                if (s != null)
                {
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    data = ms.ToArray();
                }
            }
            catch { }
        }

        _bytes = data;
        return data ?? Array.Empty<byte>();
    }

    internal static ImageSource? WindowIcon()
    {
        var bytes = GetIcoBytes();
        if (bytes.Length == 0)
            return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    internal static System.Drawing.Icon TrayIcon()
    {
        var bytes = GetIcoBytes();
        if (bytes.Length > 0)
        {
            try { return new System.Drawing.Icon(new MemoryStream(bytes)); }
            catch { }
        }
        return System.Drawing.SystemIcons.Application;
    }
}