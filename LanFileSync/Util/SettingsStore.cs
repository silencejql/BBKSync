using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LanFileSync;

public sealed class ComputerMapping
{
    public string Ip { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class ServerSection
{
    public string Root { get; set; } = "D:\\BBK";
    public int Port { get; set; } = 25010;
    public bool AutoStartAndListen { get; set; } = false;
}

public sealed class BackupSection
{
    public string Dest { get; set; } = "D:\\BBK_AutoBackup";
    public bool BeforeSync { get; set; } = true;
    public bool LogRule { get; set; } = true;
    public int LogDays { get; set; } = 2;
    public bool IgnoreRegexEnabled { get; set; } = true;
    public string IgnoreRegexes { get; set; } = "副本|copy|-";
    public bool CompressZip { get; set; } = true;
    public bool CompressUpdateZip { get; set; } = true;
    public bool AutoFetchComputerName { get; set; } = true;
    public bool RunPreBackupBat { get; set; } = false;
}

public sealed class TransferSection
{
    public string Path { get; set; } = @"D:\BBK\FAS\Always.xmlFreeForm";
    public bool SameSkip { get; set; } = true;
}

public sealed class ProcessSection
{
    public string ProcessPrefix { get; set; } = "FreeFormsAlways";
    public string ProcessPath { get; set; } = @"D:\BBK\FAS\Always.xmlFreeForm";
    public string ProcessKillNames { get; set; } = "FreeFormsAlways";
    public string ProcessFileSuffixes { get; set; } = "xmlFreeForm";
}

public sealed class AppSettings
{
    public int Version { get; set; } = 2;
    public ServerSection Server { get; set; } = new();
    public BackupSection Backup { get; set; } = new();
    public TransferSection Transfer { get; set; } = new();
    public ProcessSection Process { get; set; } = new();
    public List<ComputerMapping> Computers { get; set; } = new();
    public string UpdatePassword { get; set; } = "BBK123";
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
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Server", out _))
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

            return MigrateFromLegacy(json);
        }
        catch
        {
            return new AppSettings();
        }
    }

    private AppSettings MigrateFromLegacy(string json)
    {
        var legacy = JsonSerializer.Deserialize<LegacySettings>(json) ?? new LegacySettings();
        var s = new AppSettings
        {
            Server =
            {
                Root = legacy.Root,
                Port = legacy.Port,
                AutoStartAndListen = legacy.AutoStartAndListen,
            },
            Backup =
            {
                Dest = legacy.BackupDest,
                BeforeSync = legacy.BackupBeforeSync,
                LogRule = legacy.BackupLogRule,
                LogDays = legacy.BackupLogDays,
                IgnoreRegexEnabled = legacy.IgnoreRegexEnabled,
                IgnoreRegexes = legacy.BackupIgnoreRegexes,
                CompressZip = legacy.CompressZip,
                CompressUpdateZip = legacy.CompressUpdateZip,
                AutoFetchComputerName = legacy.AutoFetchComputerName,
                RunPreBackupBat = legacy.RunPreBackupBat,
            },
            Transfer =
            {
                Path = legacy.TransferPath,
                SameSkip = legacy.TransferSameSkip,
            },
            Process =
            {
                ProcessPrefix = legacy.FreeFormProcessPrefix,
            },
            Computers = legacy.Computers,
        };
        Settings = s;
        Save();
        return s;
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            File.WriteAllText(_file, json);
        }
        catch { }
    }

    private sealed class LegacySettings
    {
        public string Root { get; set; } = "D:\\BBK";
        public int Port { get; set; } = 25010;
        public bool AutoStartAndListen { get; set; } = false;
        public string BackupDest { get; set; } = "D:\\BBK_AutoBackup";
        public bool BackupBeforeSync { get; set; } = true;
        public bool BackupLogRule { get; set; } = true;
        public int BackupLogDays { get; set; } = 2;
        public bool RunPreBackupBat { get; set; } = false;
        public string BackupIgnoreRegexes { get; set; } = "";
        public bool IgnoreRegexEnabled { get; set; } = true;
        public bool CompressZip { get; set; } = true;
        public bool CompressUpdateZip { get; set; } = true;
        public bool AutoFetchComputerName { get; set; } = true;
        public string TransferPath { get; set; } = "";
        public bool TransferSameSkip { get; set; } = true;
        public bool TransferKillFreeForm { get; set; } = true;
        public string FreeFormProcessPrefix { get; set; } = "FreeFormAlways";
        public List<ComputerMapping> Computers { get; set; } = new();
    }
}