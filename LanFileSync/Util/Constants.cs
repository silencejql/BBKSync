namespace LanFileSync;

internal static class Constants
{
    internal const int DefaultPort = 25010;
    internal const string DefaultRoot = "D:\\BBK";
    internal const string DefaultBackupDest = "D:\\BBK_AutoBackup";
    internal const string AppDataFolderName = "BBKFileSync";
    internal const string RegistryValueName = "BBKSync";

    internal const int MaxLogEntries = 4000;
    internal const int StreamBufferSize = 131_072;
    internal const int MaxRetryCount = 1;
    internal const int RetryDelayMs = 500;
    internal const long MaxFrameSize = 512_000_000;
    internal const string RolePush = "push";
    internal const string RolePull = "pull";
    internal const string RoleProbe = "probe";
    internal const string RoleTransfer = "transfer";
    internal const string RoleTransferPull = "transfer-pull";
    internal const string RoleUpdate = "update";
    internal const string RoleProcessClose = "process-close";
    internal const string RoleProcessOpen = "process-open";
    internal const string RoleFileList = "file-list";
    internal const string DefaultShareName = "c$";
    internal const string DefaultSharePath = "c$\\BBK";

    internal static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(1);
    internal const int BackupTagPrefixLength = 4;
}