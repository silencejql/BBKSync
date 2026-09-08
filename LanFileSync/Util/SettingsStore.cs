using System.IO;
using System.Text.Json;

namespace LanFileSync;

public sealed class ComputerMapping
{
    public string Ip { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class AppSettings
{
    public string Root { get; set; } = "D:\\BBK";
    public int Port { get; set; } = 25010;
    public bool AutoStartAndListen { get; set; } = false;
    public string BackupDest { get; set; } = "D:\\BBK_Backup";
    public bool BackupBeforeSync { get; set; } = true;
    public bool BackupLogRule { get; set; } = true;
    public int BackupLogDays { get; set; } = 2;
    public string BackupIgnoreRegexes { get; set; } = "";
    public bool IgnoreRegexEnabled { get; set; } = true;
    public bool CompressZip { get; set; } = true;
    public string LocalBackupName { get; set; } = "";
    public bool AutoFetchComputerName { get; set; } = true;
    public List<ComputerMapping> Computers { get; set; } = new();
}

public sealed class SettingsStore
{
    private readonly string _file;

    public AppSettings Settings { get; private set; } = new();

    public SettingsStore()
    {
        _file = AppPaths.ResolveFile("settings.json");
        Settings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_file))
                return new AppSettings();
            string json = File.ReadAllText(_file);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_file, json);
        }
        catch { }
    }
}