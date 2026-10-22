using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using LanFileSync.Mvvm;
using LanFileSync.Services;
using Microsoft.Win32;

namespace LanFileSync.ViewModels;

/// <summary>主界面标签页序号，与 XAML 中 TabControl 的顺序一致。</summary>
public enum MainTab
{
    Backup = 0,
    Update = 1,
    Transfer = 2,
    Process = 3,
    UpdateProgram = 4,
    Tools = 5,
}

/// <summary>备份来源。</summary>
public enum BackupSource
{
    Local,
    Remote,
    Share,
}

/// <summary>更新方向。</summary>
public enum SyncDirection
{
    Receive,
    Push,
    Share,
}

/// <summary>主窗口视图模型：界面状态、命令与所有业务操作入口。</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly HistoryStore _history;
    private readonly SettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly OperationCoordinator _coordinator = new();

    private PeerServer? _server;
    private bool _opEntered;
    private int _doneCount;
    private int _totalCount;
    private string _opLabel = "更新";
    private bool _isBusy;
    private bool _isTestRunning;

    private string _statusText = "就绪";
    private string _localIpText = "未启动服务";
    private double _progressMaximum = 1;
    private double _progressValue;
    private string _currentFileText = "进度: -";

    private string _rootPath = "";
    private string _portText = "";
    private bool _isAutoStart;
    private string _peerIp = "";
    private string _peerPortText = "";
    private BackupSource _backupSource = BackupSource.Local;
    private string _backupDestPath = "";
    private bool _backupLogRule = true;
    private string _backupLogDaysText = "2";
    private bool _autoFetchName = true;
    private bool _compressZip = true;
    private bool _runPreBackupBat;
    private bool _ignoreEnabled = true;
    private string _backupIgnore = "";
    private SyncDirection _syncDirection = SyncDirection.Receive;
    private bool _binReplace;
    private bool _backupBeforeSync = true;
    private bool _updateCompressZip = true;
    private bool _transferIsPull;
    private string _transferPath = "";
    private bool _transferUpdate = true;
    private bool _transferSameSkip = true;
    private string _processPath = "";
    private string _processFileSuffixes = "";
    private string _processKillNames = "";
    private string _processStatusText = "";
    private List<string>? _processPathItems;
    private string _toolsDir = "";
    private List<BackupItemView>? _purgeItems;
    private List<BackupItemView>? _renameItems;
    private string _purgeSummary = "尚未扫描。";
    private string _renameSummary = "尚未扫描。";
    private bool _toolsBusy;
    private int _selectedTabIndex;
    private bool _isUpdateUnlocked;
    private bool _isTransferUnlocked;
    private bool _isProcessUnlocked;
    private bool _isProcessPathDropDownOpen;
    private string _shareIp = "";
    private string _shareUser = "";
    private string _sharePath = "c$\\BBK";
    private string _sharePassword = "";

    public MainViewModel(HistoryStore history, SettingsStore settings, IDialogService dialogs, IUiDispatcher ui)
    {
        _history = history;
        _settings = settings;
        _dialogs = dialogs;
        _ui = ui;

        var s = settings.Settings;
        _rootPath = s.Server.Root;
        _portText = s.Server.Port.ToString();
        _peerPortText = s.Server.Port.ToString();
        _isAutoStart = s.Server.AutoStartAndListen;
        _backupDestPath = s.Backup.Dest;
        _backupLogRule = s.Backup.LogRule;
        _backupLogDaysText = s.Backup.LogDays.ToString();
        _backupIgnore = s.Backup.IgnoreRegexes;
        _ignoreEnabled = s.Backup.IgnoreRegexEnabled;
        _compressZip = s.Backup.CompressZip;
        _updateCompressZip = s.Backup.CompressUpdateZip;
        _runPreBackupBat = s.Backup.RunPreBackupBat;
        _autoFetchName = s.Backup.AutoFetchComputerName;
        _backupBeforeSync = s.Backup.BeforeSync;
        _transferPath = s.Transfer.Path;
        _transferSameSkip = s.Transfer.SameSkip;
        _processPath = s.Process.ProcessPath;
        _processKillNames = s.Process.ProcessKillNames;
        _processFileSuffixes = s.Process.ProcessFileSuffixes;
        FreeFormKiller.ProcessPrefix = s.Process.ProcessPrefix;

        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        string versionText = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "";
        WindowTitle = $"BBK文件同步与备份工具 {versionText}";
        AppTitle = $"BBKFileSync {versionText}";

        BrowseRootCommand = new RelayCommand(() =>
        {
            string? folder = _dialogs.PickFolder("选择文件夹");
            if (folder != null) RootPath = folder;
        });
        BrowseBackupDestCommand = new RelayCommand(() =>
        {
            string? folder = _dialogs.PickFolder("选择备份目标文件夹");
            if (folder != null) BackupDestPath = folder;
        });
        BrowseTransferFileCommand = new RelayCommand(() =>
        {
            string? file = _dialogs.PickFile("选择要传输的文件");
            if (file != null) TransferPath = file;
        });
        BrowseTransferDirCommand = new RelayCommand(() =>
        {
            string? folder = _dialogs.PickFolder("选择要传输的文件夹");
            if (folder != null) TransferPath = folder;
        });
        BrowseProcessCommand = new RelayCommand(() =>
        {
            string? file = _dialogs.PickFile("选择要启动的程序", "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*");
            if (file != null) ProcessPath = file;
        });
        BrowseToolsDirCommand = new RelayCommand(() =>
        {
            string? folder = _dialogs.PickFolder("选择要整理的备份所在文件夹");
            if (folder != null) ToolsDir = folder;
        });

        ShowHistoryCommand = new RelayCommand(() => { _dialogs.ShowHistory(); ReloadHistory(); });
        ShowComputersCommand = new RelayCommand(() => { _dialogs.ShowComputers(); ReloadHistory(); });
        ShowHelpCommand = new RelayCommand(() => _dialogs.ShowHelp());
        ShareTestCommand = new RelayCommand(ShareTest, () => CanShareTest);

        StartServerCommand = new RelayCommand(() => StartServer(showErrors: true), () => CanStartServer);
        StopServerCommand = new RelayCommand(StopServer, () => CanStopServer);
        CancelCommand = new RelayCommand(() => _coordinator.Cancel(), () => CanCancel);
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, () => CanTestConnection);

        BackupCommand = new AsyncRelayCommand(BackupAsync, () => CanBackup);
        SyncCommand = new AsyncRelayCommand(SyncAsync, () => CanSync);
        UpdateProgramCommand = new AsyncRelayCommand(UpdateProgramAsync, () => CanUpdateProgram);

        PurgeScanCommand = new RelayCommand(() => ToolsScan(purge: true), () => CanToolsScan);
        PurgeRunCommand = new RelayCommand(PurgeRun, () => CanPurgeRun);
        RenameScanCommand = new RelayCommand(() => ToolsScan(purge: false), () => CanToolsScan);
        RenameRunCommand = new RelayCommand(RenameRun, () => CanRenameRun);

        ProcessFetchFilesCommand = new AsyncRelayCommand(FetchProcessFilesAsync, () => CanRunProcess);
        ProcessOpenCommand = new AsyncRelayCommand(OpenProcessAsync, () => CanRunProcess);
        ProcessCloseCommand = new AsyncRelayCommand(CloseProcessAsync, () => CanRunProcess);

        ClearLogCommand = new RelayCommand(() => LogEntries.Clear());

        EnsureUpdateBat();
        EnsurePostBackupBat();
        ReloadHistory();
        string localIp = HostHelper.GetLocalIPs().FirstOrDefault(ip => !ip.StartsWith("127.", StringComparison.Ordinal)) ?? "";
        if (!string.IsNullOrEmpty(localIp)) PeerIp = localIp;
    }

    /// <summary>确保程序升级脚本存在（远端更新后由脚本替换并重启程序）。</summary>
    private static void EnsureUpdateBat()
    {
        string batPath = Path.Combine(AppPaths.ExeDir(), "Update_BBKSync.bat");
        string exeDir = AppPaths.ExeDir();
        string exeName = Path.GetFileName(Environment.ProcessPath ?? "BBKSync.exe");
        string content =
            "@echo off\r\n" +
            "chcp 65001 >nul 2>&1\r\n" +
            "cd /d \"" + exeDir + "\"\r\n" +
            "if not exist BBKSync_New.exe exit\r\n" +
            "echo 等待关闭 " + exeName + " ...\r\n" +
            "timeout /t 3 /nobreak >nul\r\n" +
            "taskkill /f /im " + exeName + " >nul 2>&1\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            "del /f /q \"" + exeName + "\" >nul 2>&1\r\n" +
            "ren BBKSync_New.exe " + exeName + "\r\n" +
            "start \" \" \"" + exeDir + "\\" + exeName + "\" --minimized\r\n";
        File.WriteAllText(batPath, content);
    }

    /// <summary>首次运行时生成 PostgreSQL 备份脚本模板（已存在且非空则保留用户脚本）。</summary>
    private static void EnsurePostBackupBat()
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

    public string WindowTitle { get; }
    public string AppTitle { get; }
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string LocalIpText { get => _localIpText; set => SetProperty(ref _localIpText, value); }
    public double ProgressMaximum { get => _progressMaximum; set => SetProperty(ref _progressMaximum, value); }
    public double ProgressValue { get => _progressValue; set => SetProperty(ref _progressValue, value); }
    public string CurrentFileText { get => _currentFileText; set => SetProperty(ref _currentFileText, value); }

    public string RootPath { get => _rootPath; set => SetProperty(ref _rootPath, value); }
    public string PortText { get => _portText; set => SetProperty(ref _portText, value); }
    public string PeerPortText { get => _peerPortText; set => SetProperty(ref _peerPortText, value); }
    public string PeerIp { get => _peerIp; set => SetProperty(ref _peerIp, value); }
    public ObservableCollection<IpComboItem> PeerHistory { get; } = new();

    public bool IsAutoStart
    {
        get => _isAutoStart;
        set
        {
            if (!SetProperty(ref _isAutoStart, value)) return;
            ApplyAutoStart(value);
        }
    }

    public string BackupDestPath { get => _backupDestPath; set => SetProperty(ref _backupDestPath, value); }
    public bool BackupLogRule { get => _backupLogRule; set => SetProperty(ref _backupLogRule, value); }
    public string BackupLogDaysText { get => _backupLogDaysText; set => SetProperty(ref _backupLogDaysText, value); }
    public bool AutoFetchName { get => _autoFetchName; set => SetProperty(ref _autoFetchName, value); }
    public bool CompressZip { get => _compressZip; set => SetProperty(ref _compressZip, value); }
    public bool RunPreBackupBat { get => _runPreBackupBat; set => SetProperty(ref _runPreBackupBat, value); }
    public bool IgnoreEnabled { get => _ignoreEnabled; set => SetProperty(ref _ignoreEnabled, value); }
    public string BackupIgnore { get => _backupIgnore; set => SetProperty(ref _backupIgnore, value); }

    public bool IsBackupLocal
    {
        get => _backupSource == BackupSource.Local;
        set { if (value) SetBackupSource(BackupSource.Local); }
    }

    public bool IsBackupRemote
    {
        get => _backupSource == BackupSource.Remote;
        set { if (value) SetBackupSource(BackupSource.Remote); }
    }

    public bool IsBackupShare
    {
        get => _backupSource == BackupSource.Share;
        set { if (value) SetBackupSource(BackupSource.Share); }
    }

    public bool IsReceiveSelected
    {
        get => _syncDirection == SyncDirection.Receive;
        set { if (value) SetSyncDirection(SyncDirection.Receive); }
    }

    public bool IsPushSelected
    {
        get => _syncDirection == SyncDirection.Push;
        set { if (value) SetSyncDirection(SyncDirection.Push); }
    }

    public bool IsSyncShareSelected
    {
        get => _syncDirection == SyncDirection.Share;
        set { if (value) SetSyncDirection(SyncDirection.Share); }
    }

    public string UpdateHint => _syncDirection switch
    {
        SyncDirection.Share => "本机为目标：从共享连接直接读取远端文件并更新到本机，远端免安装。更新规则按本机设置执行。",
        SyncDirection.Push => "本机为源：远端需先启动服务。推送时规则以远端界面设置为准。",
        _ => "本机为目标：远端需先启动服务并告知 IP/端口。更新规则按本机界面设置执行。",
    };

    public bool BinReplace { get => _binReplace; set => SetProperty(ref _binReplace, value); }
    public bool BackupBeforeSync { get => _backupBeforeSync; set => SetProperty(ref _backupBeforeSync, value); }
    public bool UpdateCompressZip { get => _updateCompressZip; set => SetProperty(ref _updateCompressZip, value); }

    public bool IsTransferToRemote
    {
        get => !_transferIsPull;
        set { if (value) SetTransferPull(false); }
    }

    public bool IsTransferFromRemote
    {
        get => _transferIsPull;
        set { if (value) SetTransferPull(true); }
    }

    public string TransferHint => _transferIsPull
        ? "从远端电脑拉取输入路径的文件/文件夹。更新模式先备份本地原文件再拉取覆盖；拷贝模式另存为 名称_设备信息_日期。"
        : "将选中的文件/文件夹同步到远端电脑（远端需先启动服务）。更新模式先备份远端原文件再覆盖；拷贝模式另存为 名称_设备信息_日期。";

    public string TransferPath { get => _transferPath; set => SetProperty(ref _transferPath, value); }

    public bool TransferUpdate
    {
        get => _transferUpdate;
        set
        {
            if (!SetProperty(ref _transferUpdate, value)) return;
            OnPropertyChanged(nameof(IsTransferSameSkipEnabled));
            OnPropertyChanged(nameof(TransferModeHint));
        }
    }

    public bool TransferSameSkip { get => _transferSameSkip; set => SetProperty(ref _transferSameSkip, value); }
    public bool IsTransferSameSkipEnabled => _transferUpdate;
    public string TransferModeHint => _transferUpdate
        ? "更新模式：同步前自动备份原文件，再覆盖到原路径。备份命名：文件名_AutoBackup_日期。"
        : "拷贝模式：不改动原文件，新文件以 名称_设备信息_日期 另存。";

    public string ProcessPath
    {
        get => _processPath;
        set
        {
            if (!SetProperty(ref _processPath, value)) return;
            // ComboBox 重设 ItemsSource 后会丢失编辑文本，重新通知以便界面回填
            OnPropertyChanged(nameof(ProcessPath));
        }
    }

    public string ProcessFileSuffixes { get => _processFileSuffixes; set => SetProperty(ref _processFileSuffixes, value); }
    public string ProcessKillNames { get => _processKillNames; set => SetProperty(ref _processKillNames, value); }
    public string ProcessStatusText { get => _processStatusText; set => SetProperty(ref _processStatusText, value); }
    public List<string>? ProcessPathItems { get => _processPathItems; set => SetProperty(ref _processPathItems, value); }

    public string ToolsDir { get => _toolsDir; set => SetProperty(ref _toolsDir, value); }
    public List<BackupItemView>? PurgeItems
    {
        get => _purgeItems;
        set
        {
            if (!SetProperty(ref _purgeItems, value)) return;
            NotifyCommandState();
        }
    }

    public List<BackupItemView>? RenameItems
    {
        get => _renameItems;
        set
        {
            if (!SetProperty(ref _renameItems, value)) return;
            NotifyCommandState();
        }
    }
    public string PurgeSummary { get => _purgeSummary; set => SetProperty(ref _purgeSummary, value); }
    public string RenameSummary { get => _renameSummary; set => SetProperty(ref _renameSummary, value); }
    public bool ToolsBusy
    {
        get => _toolsBusy;
        set
        {
            if (!SetProperty(ref _toolsBusy, value)) return;
            OnPropertyChanged(nameof(IsToolsIdle));
            NotifyCommandState();
        }
    }

    public bool IsToolsIdle => !_toolsBusy;

    public bool IsProcessPathDropDownOpen
    {
        get => _isProcessPathDropDownOpen;
        set => SetProperty(ref _isProcessPathDropDownOpen, value);
    }

    public string ShareIp { get => _shareIp; set => SetProperty(ref _shareIp, value); }
    public string ShareUser { get => _shareUser; set => SetProperty(ref _shareUser, value); }
    public string SharePath { get => _sharePath; set => SetProperty(ref _sharePath, value); }
    public string SharePassword { get => _sharePassword; set => _sharePassword = value; }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (!SetProperty(ref _selectedTabIndex, value)) return;
            OnPropertyChanged(nameof(ShowSyncProgress));
            NotifyCommandState();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(ShowSyncProgress));
            OnPropertyChanged(nameof(IsNotBusy));
            OnPropertyChanged(nameof(IsTransferContentEnabled));
            OnPropertyChanged(nameof(IsProcessContentEnabled));
            NotifyCommandState();
        }
    }

    public bool IsNotBusy => !_isBusy;

    public bool IsUpdateUnlocked => _isUpdateUnlocked;
    public bool IsTransferUnlocked => _isTransferUnlocked;
    public bool IsProcessUnlocked => _isProcessUnlocked;
    public bool IsUpdateLocked => !_isUpdateUnlocked;
    public bool IsTransferLocked => !_isTransferUnlocked;
    public bool IsProcessLocked => !_isProcessUnlocked;
    public bool IsTransferContentEnabled => _isTransferUnlocked && !_isBusy;
    public bool IsProcessContentEnabled => _isProcessUnlocked && !_isBusy;

    public bool ShowShareGroup => _backupSource == BackupSource.Share || _syncDirection == SyncDirection.Share;
    public bool ShowSyncProgress => _isBusy || _selectedTabIndex != (int)MainTab.Tools;

    public bool CanBackup => IsIdle && _selectedTabIndex == (int)MainTab.Backup;
    public bool CanSync => IsIdle && ((_selectedTabIndex == (int)MainTab.Update && _isUpdateUnlocked)
                                     || (_selectedTabIndex == (int)MainTab.Transfer && _isTransferUnlocked));
    public bool CanUpdateProgram => IsIdle && _selectedTabIndex == (int)MainTab.UpdateProgram;
    public bool CanCancel => _isBusy;
    public bool CanTestConnection => !_isTestRunning && !_isBusy;
    public bool CanShareTest => !_isBusy;
    public bool CanStartServer => !_isBusy && _server == null;
    public bool CanStopServer => !_isBusy && _server != null;
    public bool CanRunProcess => !_isBusy;
    public bool CanToolsScan => !_toolsBusy;
    public bool CanPurgeRun => !_toolsBusy && (_purgeItems?.Any(i => i.Action == "删除") ?? false);
    public bool CanRenameRun => !_toolsBusy && (_renameItems?.Any(i => i.Action == "删除" || i.Action == "重命名") ?? false);

    private bool IsIdle => !_isBusy && !_opEntered;

    public RelayCommand BrowseRootCommand { get; }
    public RelayCommand BrowseBackupDestCommand { get; }
    public RelayCommand BrowseTransferFileCommand { get; }
    public RelayCommand BrowseTransferDirCommand { get; }
    public RelayCommand BrowseProcessCommand { get; }
    public RelayCommand BrowseToolsDirCommand { get; }
    public RelayCommand ShowHistoryCommand { get; }
    public RelayCommand ShowComputersCommand { get; }
    public RelayCommand ShowHelpCommand { get; }
    public RelayCommand ShareTestCommand { get; }
    public RelayCommand StartServerCommand { get; }
    public RelayCommand StopServerCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand TestConnectionCommand { get; }
    public AsyncRelayCommand BackupCommand { get; }
    public AsyncRelayCommand SyncCommand { get; }
    public AsyncRelayCommand UpdateProgramCommand { get; }
    public RelayCommand PurgeScanCommand { get; }
    public RelayCommand PurgeRunCommand { get; }
    public RelayCommand RenameScanCommand { get; }
    public RelayCommand RenameRunCommand { get; }
    public AsyncRelayCommand ProcessFetchFilesCommand { get; }
    public AsyncRelayCommand ProcessOpenCommand { get; }
    public AsyncRelayCommand ProcessCloseCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    /// <summary>密码保护标签页解锁状态由视图决定（PasswordBox 无法绑定），视图在解锁/上锁后调用。</summary>
    public void SetUnlocked(MainTab tab, bool unlocked)
    {
        switch (tab)
        {
            case MainTab.Update:
                if (_isUpdateUnlocked == unlocked) return;
                _isUpdateUnlocked = unlocked;
                OnPropertyChanged(nameof(IsUpdateUnlocked));
                OnPropertyChanged(nameof(IsUpdateLocked));
                break;
            case MainTab.Transfer:
                if (_isTransferUnlocked == unlocked) return;
                _isTransferUnlocked = unlocked;
                OnPropertyChanged(nameof(IsTransferUnlocked));
                OnPropertyChanged(nameof(IsTransferLocked));
                OnPropertyChanged(nameof(IsTransferContentEnabled));
                break;
            case MainTab.Process:
                if (_isProcessUnlocked == unlocked) return;
                _isProcessUnlocked = unlocked;
                OnPropertyChanged(nameof(IsProcessUnlocked));
                OnPropertyChanged(nameof(IsProcessLocked));
                OnPropertyChanged(nameof(IsProcessContentEnabled));
                break;
            default:
                return;
        }
        NotifyCommandState();
    }

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(CanBackup));
        OnPropertyChanged(nameof(CanSync));
        OnPropertyChanged(nameof(CanUpdateProgram));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(CanShareTest));
        OnPropertyChanged(nameof(CanStartServer));
        OnPropertyChanged(nameof(CanStopServer));
        OnPropertyChanged(nameof(CanRunProcess));
        OnPropertyChanged(nameof(CanToolsScan));
        OnPropertyChanged(nameof(CanPurgeRun));
        OnPropertyChanged(nameof(CanRenameRun));
        BackupCommand.RaiseCanExecuteChanged();
        SyncCommand.RaiseCanExecuteChanged();
        UpdateProgramCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        TestConnectionCommand.RaiseCanExecuteChanged();
        ShareTestCommand.RaiseCanExecuteChanged();
        StartServerCommand.RaiseCanExecuteChanged();
        StopServerCommand.RaiseCanExecuteChanged();
        ProcessFetchFilesCommand.RaiseCanExecuteChanged();
        ProcessOpenCommand.RaiseCanExecuteChanged();
        ProcessCloseCommand.RaiseCanExecuteChanged();
        PurgeScanCommand.RaiseCanExecuteChanged();
        PurgeRunCommand.RaiseCanExecuteChanged();
        RenameScanCommand.RaiseCanExecuteChanged();
        RenameRunCommand.RaiseCanExecuteChanged();
    }

    private void SetBackupSource(BackupSource source)
    {
        if (_backupSource == source) return;
        _backupSource = source;
        OnPropertyChanged(nameof(IsBackupLocal));
        OnPropertyChanged(nameof(IsBackupRemote));
        OnPropertyChanged(nameof(IsBackupShare));
        OnPropertyChanged(nameof(ShowShareGroup));
    }

    private void SetSyncDirection(SyncDirection direction)
    {
        if (_syncDirection == direction) return;
        _syncDirection = direction;
        OnPropertyChanged(nameof(IsReceiveSelected));
        OnPropertyChanged(nameof(IsPushSelected));
        OnPropertyChanged(nameof(IsSyncShareSelected));
        OnPropertyChanged(nameof(UpdateHint));
        OnPropertyChanged(nameof(ShowShareGroup));
    }

    private void SetTransferPull(bool pull)
    {
        if (_transferIsPull == pull) return;
        _transferIsPull = pull;
        OnPropertyChanged(nameof(IsTransferToRemote));
        OnPropertyChanged(nameof(IsTransferFromRemote));
        OnPropertyChanged(nameof(TransferHint));
    }

    public void ReloadHistory()
    {
        string host = _peerIp;
        var items = new List<IpComboItem>();
        foreach (var entry in _history.Entries)
        {
            string name = ComputerNameFor(entry.Host);
            string display = name != entry.Host ? $"{name}({entry.Host})" : entry.Host;
            if (!string.IsNullOrEmpty(entry.Note)) display += $" [{entry.Note}]";
            items.Add(new IpComboItem { Ip = entry.Host, Label = display });
        }
        foreach (var computer in _settings.Settings.Computers)
        {
            if (string.IsNullOrWhiteSpace(computer.Ip)) continue;
            string ip = computer.Ip.Trim();
            if (items.Any(i => string.Equals(i.Ip, ip, StringComparison.OrdinalIgnoreCase))) continue;
            string display = !string.IsNullOrWhiteSpace(computer.Name) ? $"{computer.Name}({ip})" : ip;
            items.Add(new IpComboItem { Ip = ip, Label = display });
        }

        PeerHistory.Clear();
        foreach (var item in items) PeerHistory.Add(item);

        PeerIp = host;
        if (PeerHistory.Count > 0 && string.IsNullOrWhiteSpace(PeerIp)) PeerIp = PeerHistory[0].Ip;
    }

    private string ComputerNameFor(string ip)
    {
        string normalized = (ip ?? "").Trim();
        foreach (var computer in _settings.Settings.Computers)
        {
            if (string.Equals((computer.Ip ?? "").Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(computer.Name) ? normalized : computer.Name.Trim();
        }
        return normalized;
    }

    private bool TryGetPeerPort(out int port)
        => int.TryParse(PeerPortText, out port) && port >= 1 && port <= 65535;

    private void SaveIpHistory(int port)
    {
        _history.Upsert(PeerIp.Trim(), port);
        ReloadHistory();
    }

    public void StartServer(bool showErrors)
    {
        if (_server != null) return;
        if (!int.TryParse(PortText, out int port) || port < 1 || port > 65535)
        {
            if (showErrors) _dialogs.Warning("端口无效，请输入 1~65535 之间的数字。", "错误");
            return;
        }
        string root = RootPath.Trim();
        _server = new PeerServer(root, port, ReadSyncOptions(), Log, OnFileProgress, OnTotal,
            m => LogError("执行失败: " + m), BackupBeforeSync, BackupDestPath.Trim(), ReadBackupOptions(), UpdateCompressZip);
        try
        {
            _server.Start();
            LocalIpText = "本机地址: " + string.Join("   ", GetLocalIPs());
            LogSuccess($"服务已启动，端口 {port}，根目录 {root}。远端填上此 IP 与本端口即可同步。");
            NotifyCommandState();
        }
        catch (Exception ex)
        {
            if (showErrors) _dialogs.Error("启动服务失败: " + ex.Message, "错误");
            else LogError("启动服务失败: " + ex.Message);
            _server?.Dispose();
            _server = null;
        }
    }

    public void StopServer()
    {
        _server?.Stop();
        _server = null;
        LocalIpText = "服务已停止";
        LogError("服务已停止");
        NotifyCommandState();
    }

    public void CancelAll() => _coordinator.Cancel();
    public OperationCoordinator Coordinator => _coordinator;

    public void SaveSettings()
    {
        _settings.Settings.Server.Root = RootPath.Trim();
        _settings.Settings.Server.Port = int.TryParse(PortText, out int p) ? p : _settings.Settings.Server.Port;
        _settings.Settings.Backup.Dest = BackupDestPath.Trim();
        _settings.Settings.Backup.BeforeSync = BackupBeforeSync;
        _settings.Settings.Backup.LogRule = BackupLogRule;
        _settings.Settings.Backup.LogDays = int.TryParse(BackupLogDaysText, out int d) && d >= 1 ? d : 2;
        _settings.Settings.Backup.IgnoreRegexes = BackupIgnore;
        _settings.Settings.Backup.IgnoreRegexEnabled = IgnoreEnabled;
        _settings.Settings.Backup.CompressZip = CompressZip;
        _settings.Settings.Backup.CompressUpdateZip = UpdateCompressZip;
        _settings.Settings.Backup.RunPreBackupBat = RunPreBackupBat;
        _settings.Settings.Backup.AutoFetchComputerName = AutoFetchName;
        _settings.Settings.Transfer.Path = TransferPath.Trim();
        _settings.Settings.Transfer.SameSkip = TransferSameSkip;
        _settings.Settings.Process.ProcessPath = ProcessPath.Trim();
        _settings.Settings.Process.ProcessKillNames = ProcessKillNames.Trim();
        _settings.Settings.Process.ProcessFileSuffixes = ProcessFileSuffixes.Trim();
        _settings.Save();
    }

    private void ApplyAutoStart(bool enabled)
    {
        try
        {
            string exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "BBKSync.exe";
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key == null) throw new InvalidOperationException("无法打开注册表 Run 键");
            if (enabled)
            {
                string dir = Path.GetDirectoryName(exe) ?? ".";
                key.SetValue(Constants.RegistryValueName, $"cmd.exe /c cd /d \"{dir}\" && \"{exe}\" --autostart");
            }
            else key.DeleteValue(Constants.RegistryValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { LogError("写入开机自启失败: " + ex.Message); }

        _settings.Settings.Server.AutoStartAndListen = enabled;
        SaveSettings();
        Log(enabled ? "已启用：开机自动启动并后台监听" : "已关闭开机自启");
    }

    private static List<string> GetLocalIPs()
    {
        var list = HostHelper.GetLocalIPs();
        if (list.Count == 0) list.Add("(未检测到局域网地址)");
        return list;
    }

    public void Log(string message) => AppendLog(message, LogKind.Info);
    public void LogError(string message) => AppendLog(message, LogKind.Error);
    public void LogSuccess(string message) => AppendLog(message, LogKind.Success);
    public void LogDivider() => Log("------------------------------------------------");

    private void AppendLog(string message, LogKind kind)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _ui.Invoke(() =>
        {
            LogEntries.Add(new LogEntry(line, kind));
            while (LogEntries.Count > Constants.MaxLogEntries) LogEntries.RemoveAt(0);
        });
    }

    private async Task LogHostSummaryAsync(string opName, List<string> okIps, List<string> failed, int port)
    {
        int total = okIps.Count + failed.Count;
        var allIps = okIps.Concat(failed.Select(ExtractIpFromFailed)).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        var deviceMap = await ProbeDeviceNamesAsync(allIps, port);

        string Label(string ip) => deviceMap.TryGetValue(ip, out var device) && !string.IsNullOrEmpty(device) && device != ip
            ? $"{ip} [{device}]" : ip;

        LogDivider();
        if (failed.Count == 0)
        {
            LogSuccess($"✅ {opName}完成：{okIps.Count}/{total} 台全部成功");
            foreach (var host in okIps) LogSuccess($"  成功: {Label(host)}");
        }
        else if (okIps.Count == 0)
        {
            LogError($"❌ {opName}完成：0/{total} 台成功，全部失败");
            foreach (var host in failed) LogError($"  失败: {Label(ExtractIpFromFailed(host))} — {ExtractMsgFromFailed(host)}");
        }
        else
        {
            Log($"⚠️  {opName}完成：{okIps.Count}/{total} 台成功，{failed.Count} 台失败");
            foreach (var host in okIps) LogSuccess($"  成功: {Label(host)}");
            foreach (var host in failed) LogError($"  失败: {Label(ExtractIpFromFailed(host))} — {ExtractMsgFromFailed(host)}");
        }
    }

    private static string ExtractIpFromFailed(string entry)
    {
        int index = entry.IndexOf(" - ", StringComparison.Ordinal);
        return index > 0 ? entry.Substring(0, index) : entry;
    }

    private static string ExtractMsgFromFailed(string entry)
    {
        int index = entry.IndexOf(" - ", StringComparison.Ordinal);
        return index > 0 ? entry.Substring(index + 3) : "";
    }

    private async Task<Dictionary<string, string>> ProbeDeviceNamesAsync(List<string> ips, int port)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ips.Count == 0 || port <= 0) return map;
        var tasks = ips.Select(async ip =>
        {
            try
            {
                var (name, line) = await ProbeRemoteAsync(ip, port);
                string label = string.IsNullOrWhiteSpace(name) || name == ip ? ip : name;
                if (!string.IsNullOrWhiteSpace(line)) label += $"/Line{line}";
                return (ip, label);
            }
            catch { return (ip, ip); }
        });
        foreach (var (ip, label) in await Task.WhenAll(tasks)) map[ip] = label;
        return map;
    }

    private void LogSingleOperationSummary(string opName, bool success, string detail = "")
    {
        LogDivider();
        if (success) LogSuccess($"✅ {opName}完成{(string.IsNullOrEmpty(detail) ? "" : "：" + detail)}");
        else LogError($"❌ {opName}失败{(string.IsNullOrEmpty(detail) ? "" : "：" + detail)}");
    }

    private async Task<(string name, string line)> ProbeRemoteAsync(string host, int port)
    {
        try
        {
            using var client = new PeerClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await client.ConnectAsync(host, port, Constants.RoleProbe, cts.Token);
            var (name, line) = await client.ProbeDeviceAsync(cts.Token);
            return (string.IsNullOrWhiteSpace(name) ? host : name, line ?? "");
        }
        catch { return (host, ""); }
    }

    private async Task<string> GetDeviceInfo(string host)
    {
        if (!TryGetPeerPort(out int port))
        {
            _dialogs.Warning("远端端口无效。", "错误");
            return "";
        }
        var (name, line) = await ProbeRemoteAsync(host, port);
        return string.IsNullOrWhiteSpace(name) || name == host
            ? ""
            : $"[设备 {name}{(string.IsNullOrWhiteSpace(line) ? "" : "/Line" + line)}]";
    }

    private static string FormatSize(long bytes)
    {
        const long K = 1024, M = 1024 * K, G = 1024 * M;
        if (bytes >= G) return $"{bytes / (double)G:F2} GB";
        if (bytes >= M) return $"{bytes / (double)M:F2} MB";
        if (bytes >= K) return $"{bytes / (double)K:F1} KB";
        return $"{bytes} B";
    }

    private void OnTotal(int total) => _ui.Invoke(() =>
    {
        _doneCount = 0;
        _totalCount = total;
        ProgressMaximum = Math.Max(1, total);
        ProgressValue = 0;
        if (total == 0)
        {
            ProgressValue = 1;
            CurrentFileText = $"无需{_opLabel}文件";
        }
    });

    private void OnFileProgress(string relativePath, long size) => _ui.Invoke(() =>
    {
        _doneCount++;
        ProgressValue = _doneCount;
        CurrentFileText = $"文件 {_doneCount}/{_totalCount}: {relativePath} ({FormatSize(size)})";
    });

    private void StartBusy()
    {
        _coordinator.StartBusy();
        _doneCount = 0;
        _totalCount = 0;
        IsBusy = true;
    }

    private void EndBusy()
    {
        _coordinator.EndBusy();
        IsBusy = false;
        StatusText = "就绪";
    }

    // 主操作（备份/更新）的同步重入保护：点击后立即占用并禁用按钮，直到整个流程
    // （含结果汇总）结束。用于覆盖 StartBusy 之前还有异步探测（设备名）与确认框的空档。
    private bool TryEnterOperation()
    {
        if (_opEntered || _coordinator.Busy) return false;
        _opEntered = true;
        NotifyCommandState();
        return true;
    }

    private void ExitOperation()
    {
        if (!_opEntered) return;
        _opEntered = false;
        NotifyCommandState();
    }

    private BackupOptions ReadBackupOptions()
    {
        if (!int.TryParse(BackupLogDaysText, out int days) || days < 1) days = 2;
        string[] ignores = BackupIgnore
            .Split(new[] { ',', '，', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToArray();
        return new BackupOptions
        {
            ApplyLogRule = BackupLogRule,
            LogRetentionDays = days,
            IgnoreRegexes = IgnoreEnabled ? ignores : Array.Empty<string>(),
        };
    }

    private SyncOptions ReadSyncOptions() => new() { FullReplaceBin = BinReplace };
    private bool ShouldCompress => CompressZip;

    private async Task CompressAndRemoveFolderAsync(string folderPath, bool compress)
    {
        if (!compress || !Directory.Exists(folderPath)) return;
        try
        {
            string zipPath = folderPath + ".zip";
            Log("正在压缩备份文件夹 ...");
            await Task.Run(() =>
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                ZipHelper.CompressFolder(folderPath, zipPath);
                Directory.Delete(folderPath, recursive: true);
            });
            Log("已压缩为 " + Path.GetFileName(zipPath));
        }
        catch (Exception ex) { LogError("压缩失败: " + ex.Message); }
    }

    private async Task<bool> RunPreBackupBatAsync()
    {
        if (!RunPreBackupBat) return true;
        string batPath = Path.Combine(AppPaths.ExeDir(), "PostgreSQL_Backup.bat");
        if (!File.Exists(batPath) || new FileInfo(batPath).Length == 0)
        {
            LogError("PostgreSQL_Backup.bat 不存在或为空，跳过备份前脚本");
            return true;
        }
        string script = File.ReadAllText(batPath);
        LogDivider();
        Log("开始执行备份前脚本 ...");
        try
        {
            string? error = await Task.Run(() => ScriptRunner.Run(script, AppPaths.ExeDir(), Log));
            if (error != null) LogError(error + "，继续备份");
            else LogSuccess("备份前脚本执行完成");
        }
        catch (Exception ex) { LogError("执行备份前脚本异常，继续备份: " + ex.Message); }
        return true;
    }

    private string BackupNameFor(string fallback)
    {
        if (!AutoFetchName) return fallback;
        string auto = DeviceConfig.ReadFromRoot(RootPath.Trim()).Name;
        if (!string.IsNullOrWhiteSpace(auto)) return auto;
        LogError("自动获取电脑名称失败：未在 BBK 目录中找到包含 DeviceNo 的系统配置文件，使用默认名称。");
        return fallback;
    }

    private string BackupTargetPath(string dest, string name, string line = "")
    {
        if (string.IsNullOrWhiteSpace(line)) line = DeviceConfig.ReadFromRoot(RootPath.Trim()).Line;
        string basePath = string.IsNullOrEmpty(line) ? dest : Path.Combine(dest, "Line" + FileHelper.SanitizeName(line));
        return Path.Combine(basePath, $"BBK_{FileHelper.SanitizeName(name)}_{DateTime.Now:yyyyMMdd}");
    }
}
