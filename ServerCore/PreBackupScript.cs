using System.IO;

namespace LanFileSync;

/// <summary>
/// 管理 exe 同目录下的 PostgreSQL_Backup.bat 备份前脚本模板。
/// 该脚本由用户按现场环境编辑；文件不存在或为空时写入默认模板。
/// </summary>
internal static class PreBackupScript
{
    public static void Ensure()
    {
        string batPath = Path.Combine(AppPaths.ExeDir(), "PostgreSQL_Backup.bat");
        if (File.Exists(batPath))
        {
            string existing = File.ReadAllText(batPath);
            if (!string.IsNullOrWhiteSpace(existing)) return;
        }
        string content =
            "@echo off\r\n" +
            "set DBName=LocalDB\r\n" +
            "set FileName=%DBName%_AutoBackup_%date:~0,4%%date:~5,2%%date:~8,2%.backup\r\n" +
            "set BACKUP_DIR=D:\\BBK\\DataBase\r\n" +
            "if not exist \"D:\\BBK\\DataBase\" (md D:\\BBK\\DataBase)\r\n" +
            "C:/\"Program Files (x86)\"/PostgreSQL/9.5/bin/pg_dump.exe --host localhost --port 5432 --username \"postgres\" --no-password --format custom --verbose --file \"%BACKUP_DIR%\\%FileName%\" \"%DBName%\"";
        File.WriteAllText(batPath, content);
    }
}
