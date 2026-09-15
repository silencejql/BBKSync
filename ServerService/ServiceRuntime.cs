using System.Runtime.InteropServices;

namespace LanFileSync.ServerService;

/// <summary>
/// 零依赖 Windows 服务宿主：通过 advapi32 SCM API 注册服务入口。
/// 直接运行 exe（非 SCM 启动）时 TryRunAsService 返回 false，调用方可进入控制台调试模式。
/// </summary>
internal static class ServiceRuntime
{
    private const int ServiceWin32OwnProcess = 0x00000010;
    private const int ServiceAcceptStop = 0x00000001;
    private const int ServiceControlStop = 0x00000001;
    private const int ServiceStopped = 1;
    private const int ServiceStartPending = 2;
    private const int ServiceStopPending = 3;
    private const int ServiceRunning = 4;
    private const int ErrorFailedToStart = 1063;
    private const int NoError = 0;
    private const int Win32ErrorServiceSpecificError = 1066;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? ServiceName;
        public IntPtr ServiceMain;
    }

    private delegate void ServiceMainDelegate(int argc, IntPtr argv);
    private delegate int ServiceControlDelegate(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcherW(ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName,
        ServiceControlDelegate controlHandler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr serviceStatusHandle, ref ServiceStatus status);

    // 委托必须以静态字段持有，防止 GC 回收非托管函数指针
    private static readonly ServiceMainDelegate _mainCallback = ServiceMain;
    private static readonly ServiceControlDelegate _controlCallback = HandleControl;

    private static Action<CancellationToken>? _run;
    private static string _serviceName = "";
    private static CancellationTokenSource? _cts;
    private static IntPtr _statusHandle;
    private static Thread? _workThread;

    /// <summary>
    /// 尝试以 Windows 服务方式运行。run 在工作线程执行且应阻塞到收到停止信号。
    /// 返回 false 表示进程不是由 SCM 启动（应回退到控制台模式）。
    /// </summary>
    public static bool TryRunAsService(string serviceName, Action<CancellationToken> run)
    {
        _run = run;
        _serviceName = serviceName;
        var table = new[]
        {
            new ServiceTableEntry { ServiceName = serviceName, ServiceMain = Marshal.GetFunctionPointerForDelegate(_mainCallback) },
            new ServiceTableEntry { ServiceName = null, ServiceMain = IntPtr.Zero },
        };

        if (!StartServiceCtrlDispatcherW(table))
        {
            if (Marshal.GetLastWin32Error() == ErrorFailedToStart)
                return false;
            throw new InvalidOperationException("StartServiceCtrlDispatcher 失败，Win32Error=" + Marshal.GetLastWin32Error());
        }
        return true;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerExW(_serviceName, _controlCallback, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
            throw new InvalidOperationException("RegisterServiceCtrlHandlerExW 失败");

        SetStatus(ServiceStartPending, waitHint: 5000, checkpoint: 1);

        _cts = new CancellationTokenSource();
        _workThread = new Thread(() =>
        {
            try
            {
                _run!(_cts.Token);
            }
            catch (Exception ex)
            {
                ServiceLog.Log("服务工作线程异常: " + ex);
                SetStatus(ServiceStopped, win32ExitCode: 1);
            }
        });
        _workThread.IsBackground = true;
        _workThread.Start();

        SetStatus(ServiceRunning);
    }

    private static int HandleControl(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control != ServiceControlStop)
            return NoError;

        SetStatus(ServiceStopPending, waitHint: 5000, checkpoint: 1);
        try
        {
            _cts?.Cancel();
            _workThread?.Join(TimeSpan.FromSeconds(10));
        }
        catch { }
        SetStatus(ServiceStopped, win32ExitCode: NoError);
        return NoError;
    }

    private static void SetStatus(int state, int win32ExitCode = 0, int waitHint = 0, int checkpoint = 0)
    {
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == ServiceRunning ? ServiceAcceptStop : 0,
            Win32ExitCode = state == ServiceStopped && win32ExitCode != 0
                ? Win32ErrorServiceSpecificError
                : win32ExitCode,
            CheckPoint = checkpoint,
            WaitHint = waitHint,
        };
        try { SetServiceStatus(_statusHandle, ref status); } catch { }
    }
}
