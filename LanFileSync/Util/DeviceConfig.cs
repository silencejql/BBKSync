using System.IO;
using System.Xml.Linq;

namespace LanFileSync;

internal static class DeviceConfig
{
    internal static (string Name, string Line) ReadFromRoot(string root)
    {
        string? newest = null;
        DateTime newestTime = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return ("", "");
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                string folder = Path.GetFileName(dir);
                if (folder.StartsWith("Bin", StringComparison.OrdinalIgnoreCase))
                    continue;
                string cfg = Path.Combine(dir, "Config", "SystemConfig.xml");
                if (!File.Exists(cfg))
                    continue;
                DateTime t = File.GetLastWriteTimeUtc(cfg);
                if (t > newestTime)
                {
                    newestTime = t;
                    newest = cfg;
                }
            }
            if (newest == null)
                return ("", "");
            return (ReadKeyValue(newest, "DeviceNo").Trim(), ReadKeyValue(newest, "LineNo").Trim());
        }
        catch
        {
            return ("", "");
        }
    }

    internal static string ReadKeyValue(string xmlPath, string keyName)
    {
        try
        {
            var doc = XDocument.Load(xmlPath);
            foreach (var other in doc.Descendants("Other"))
            {
                string key = other.Element("KeyName")?.Value.Trim() ?? "";
                if (string.Equals(key, keyName, StringComparison.OrdinalIgnoreCase))
                    return other.Element("Value")?.Value.Trim() ?? "";
            }
            return "";
        }
        catch
        {
            return "";
        }
    }
}