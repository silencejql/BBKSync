namespace LanFileSync;

/// <summary>
/// 服务端运行参数。窗口小程序从界面设置构建，Windows 服务从配置文件构建。
/// </summary>
public sealed class ServerRuntimeOptions
{
    public string Root { get; init; } = Constants.DefaultRoot;
    public int Port { get; init; } = Constants.DefaultPort;
    public bool FullReplaceBin { get; init; }
    public bool BackupBeforeSync { get; init; } = true;
    public string BackupDest { get; init; } = Constants.DefaultBackupDest;
    public bool ApplyLogRule { get; init; } = true;
    public int LogRetentionDays { get; init; } = 2;
    public bool IgnoreRegexEnabled { get; init; } = true;
    public string IgnoreRegexes { get; init; } = "副本|copy|-";
    public bool CompressUpdateZip { get; init; } = true;

    /// <summary>从共享设置文件的 AppSettings 映射。</summary>
    public static ServerRuntimeOptions FromSettings(AppSettings s) => new()
    {
        Root = string.IsNullOrWhiteSpace(s.Server.Root) ? Constants.DefaultRoot : s.Server.Root,
        Port = s.Server.Port is < 1 or > 65535 ? Constants.DefaultPort : s.Server.Port,
        BackupBeforeSync = s.Backup.BeforeSync,
        BackupDest = string.IsNullOrWhiteSpace(s.Backup.Dest) ? Constants.DefaultBackupDest : s.Backup.Dest,
        ApplyLogRule = s.Backup.LogRule,
        LogRetentionDays = s.Backup.LogDays < 1 ? 2 : s.Backup.LogDays,
        IgnoreRegexEnabled = s.Backup.IgnoreRegexEnabled,
        IgnoreRegexes = s.Backup.IgnoreRegexes,
        CompressUpdateZip = s.Backup.CompressUpdateZip,
    };
}

/// <summary>
/// 服务端宿主：封装 PeerServer 的构建/启停，供 WPF 小程序与 Windows 服务共用。
/// </summary>
public sealed class ServerHost : IDisposable
{
    private PeerServer? _server;
    private readonly Action<string> _log;
    private readonly Action<string>? _onError;

    public bool IsRunning => _server is { Running: true };

    /// <param name="log">普通日志回调</param>
    /// <param name="onError">错误/警告回调（可选，默认并入日志）</param>
    public ServerHost(Action<string> log, Action<string>? onError = null)
    {
        _log = log;
        _onError = onError;
    }

    public void Start(ServerRuntimeOptions o)
    {
        if (_server != null) return;

        string[] ignores = o.IgnoreRegexEnabled
            ? (o.IgnoreRegexes ?? "").Split(new[] { ',', '，', ';', '；', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        var backupOptions = new BackupOptions
        {
            ApplyLogRule = o.ApplyLogRule,
            LogRetentionDays = o.LogRetentionDays,
            IgnoreRegexes = ignores,
        };

        _server = new PeerServer(
            o.Root,
            o.Port,
            new SyncOptions { FullReplaceBin = o.FullReplaceBin },
            _log,
            onFile: (_, _) => { },
            onTotal: _ => { },
            onError: m => (_onError ?? _log)("执行失败: " + m),
            backupBeforeSync: o.BackupBeforeSync,
            backupDest: o.BackupDest,
            backupOptions: backupOptions,
            compressUpdateZip: o.CompressUpdateZip);

        _server.Start();
        _log($"服务已启动，监听端口 {o.Port}，根目录 {o.Root}");
    }

    public void Start(AppSettings settings) => Start(ServerRuntimeOptions.FromSettings(settings));

    public void Stop()
    {
        if (_server == null) return;
        _server.Stop();
        _server.Dispose();
        _server = null;
        _log("服务已停止");
    }

    public void Dispose() => Stop();
}
