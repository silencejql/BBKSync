using System.IO;
using System.Text.Encodings.Web;
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
    public string BackupDest { get; set; } = "D:\\BBK_AutoBackup";
    public bool BackupBeforeSync { get; set; } = true;
    public bool BackupLogRule { get; set; } = true;
    public int BackupLogDays { get; set; } = 2;
    public bool RunPreBackupBat { get; set; } = false;
    public string PreBackupScript { get; set; } =
        "echo off\r\n" +
        "set DBName=LocalDB\r\n" +
        "set FileName=%DBName%_AutoBackup_%date:~0,4%%date:~5,2%%date:~8,2%.backup\r\n" +
        "set BACKUP_DIR=D:\\BBK\\DataBase\r\n" +
        "if not exist \"D:\\BBK\\DataBase\" (md D:\\BBK\\DataBase)\r\n" +
        "C:/\"" + "Program Files (x86)" + "\"/PostgreSQL/9.5/bin/pg_dump.exe --host localhost --port 5432 --username \"postgres\" --no-password  --format custom --verbose --file \"%BACKUP_DIR%\\%FileName%\" \"%DBName%\"";
    public string BackupIgnoreRegexes { get; set; } = "";
    public bool IgnoreRegexEnabled { get; set; } = true;
    public bool CompressZip { get; set; } = true;
    public string LocalBackupName { get; set; } = "";
    public bool AutoFetchComputerName { get; set; } = true;
    public string TransferPath { get; set; } = "";
    public bool TransferSameSkip { get; set; } = true;
    public bool TransferKillFreeForm { get; set; } = true;
    public string FreeFormProcessPrefix { get; set; } = "FreeFormAlways";
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
            string json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            File.WriteAllText(_file, json);
        }
        catch { }
    }
}