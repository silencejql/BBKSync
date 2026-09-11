using System.IO;
using System.IO.Compression;
using System.Text;

namespace LanFileSync;

internal static class ZipHelper
{
    internal static void CompressFolder(string sourceDir, string zipPath)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create, Encoding.UTF8);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string entryName = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
        }
    }
}
