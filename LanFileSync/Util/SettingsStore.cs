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
    public string IgnoreRegexes { get; set; } = "";
    public bool CompressZip { get; set; } = true;
    public bool CompressUpdateZip { get; set; } = true;
    public string LocalBackupName { get; set; } = "";
    public bool AutoFetchComputerName { get; set; } = true;
    public bool RunPreBackupBat { get; set; } = false;
    public string PreBackupScript { get; set; } =
        "echo off\r\n" +
        "set DBName=LocalDB\r\n" +
        "set FileName=%DBName%_AutoBackup_%date:~0,4%%date:~5,2%%date:~8,2%.backup\r\n" +
        "set BACKUP_DIR=D:\\BBK\\DataBase\r\n" +
        "if not exist \"D:\\BBK\\DataBase\" (md D:\\BBK\\DataBase)\r\n" +
        "C:/\"" + "Program Files (x86)" + "\"/PostgreSQL/9.5/bin/pg_dump.exe --host localhost --port 5432 --username \"postgres\" --no-password  --format custom --verbose --file \"%BACKUP_DIR%\\%FileName%\" \"%DBName%\"";
}

public sealed class TransferSection
{
    public string Path { get; set; } = "";
    public bool SameSkip { get; set; } = true;
    public bool KillFreeForm { get; set; } = true;
}

public sealed class FreeFormSection
{
    public string ProcessPrefix { get; set; } = "FreeFormAlways";
}

public sealed class AppSettings
{
    public int Version { get; set; } = 2;
    public ServerSection Server { get; set; } = new();
    public BackupSection Backup { get; set; } = new();
    public TransferSection Transfer { get; set; } = new();
    public FreeFormSection FreeForm { get; set; } = new();
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
                LocalBackupName = legacy.LocalBackupName,
                AutoFetchComputerName = legacy.AutoFetchComputerName,
                RunPreBackupBat = legacy.RunPreBackupBat,
                PreBackupScript = legacy.PreBackupScript,
            },
            Transfer =
            {
                Path = legacy.TransferPath,
                SameSkip = legacy.TransferSameSkip,
                KillFreeForm = legacy.TransferKillFreeForm,
            },
            FreeForm =
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
        public string PreBackupScript { get; set; } = "";
        public string BackupIgnoreRegexes { get; set; } = "";
        public bool IgnoreRegexEnabled { get; set; } = true;
        public bool CompressZip { get; set; } = true;
        public bool CompressUpdateZip { get; set; } = true;
        public string LocalBackupName { get; set; } = "";
        public bool AutoFetchComputerName { get; set; } = true;
        public string TransferPath { get; set; } = "";
        public bool TransferSameSkip { get; set; } = true;
        public bool TransferKillFreeForm { get; set; } = true;
        public string FreeFormProcessPrefix { get; set; } = "FreeFormAlways";
        public List<ComputerMapping> Computers { get; set; } = new();
    }
}