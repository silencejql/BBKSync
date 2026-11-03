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
    internal const int ConnectTimeoutSeconds = 5;
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

/// <summary>
/// 协议帧中 op 字段的取值。主控端与远端必须共用同一组常量，
/// 避免字符串字面量散落各处、拼写不一致时出现「未知消息」。
/// </summary>
internal static class ProtocolOp
{
    // 通用
    internal const string Cancel = "cancel";
    internal const string Error = "err";
    internal const string Request = "req";
    internal const string ManifestEntry = "f";
    internal const string ManifestEnd = "mend";
    internal const string Skip = "skip";
    internal const string Data = "data";
    internal const string Done = "done";
    internal const string Bye = "bye";

    // 更新 / 备份
    internal const string RemoteLog = "blog";
    internal const string Need = "need";
    internal const string Bat = "bat";
    internal const string TransferInit = "tinit";

    // 设备、进程与文件列表
    internal const string DeviceConfig = "cfg";
    internal const string ProcessClose = "aclose";
    internal const string ProcessOpen = "aopen";
    internal const string FileList = "flst";
    internal const string FileListEntry = "fpath";
    internal const string FileListEnd = "fend";
    internal const string Ok = "ok";

    // 程序升级
    internal const string Ready = "ready";
    internal const string Exe = "exe";
}