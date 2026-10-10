using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace HuntReplay.App.Services;

internal static class NvidiaServiceManager
{
    private static readonly string[] CandidateServices =
    [
        "NVDisplay.ContainerLocalSystem",
        "NvContainerLocalSystem"
    ];

    private static readonly List<string> StoppedServices = [];

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_CONTROL_STOP = 0x0001;
    private const uint SERVICE_STOPPED = 1;
    private const uint SERVICE_RUNNING = 4;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartService(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    public static bool IsServiceRunning(string name)
    {
        IntPtr scm = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = OpenService(scm, name, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return false;
            try
            {
                var status = new SERVICE_STATUS();
                return QueryServiceStatus(svc, ref status) && status.dwCurrentState == SERVICE_RUNNING;
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static bool StopServiceInternal(string name)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = OpenService(scm, name, SERVICE_STOP | SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return false;
            try
            {
                var status = new SERVICE_STATUS();
                if (!ControlService(svc, SERVICE_CONTROL_STOP, ref status))
                {
                    if (QueryServiceStatus(svc, ref status) && status.dwCurrentState == SERVICE_STOPPED)
                        return true;
                    return false;
                }
                return true;
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static bool StartServiceInternal(string name)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return false;
        try
        {
            IntPtr svc = OpenService(scm, name, SERVICE_START | SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return false;
            try
            {
                var status = new SERVICE_STATUS();
                if (QueryServiceStatus(svc, ref status) && status.dwCurrentState == SERVICE_RUNNING)
                    return true;
                return StartService(svc, 0, IntPtr.Zero);
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static async Task EnsureServicePausedAsync(StudioLog? log)
    {
        if (!SelfInstaller.IsNvidiaActive()) return;

        var runningServices = CandidateServices.Where(IsServiceRunning).ToList();
        if (runningServices.Count == 0 && Process.GetProcessesByName("nvcontainer").Length == 0) return;

        bool directSuccess = true;
        foreach (var svc in runningServices)
        {
            if (StopServiceInternal(svc))
            {
                StoppedServices.Add(svc);
            }
            else
            {
                directSuccess = false;
                break;
            }
        }

        if (directSuccess && StoppedServices.Count > 0)
        {
            log?.Write("Служба NVIDIA временно остановлена для автозахвата хайлайтов. Будет включена обратно при выходе.");
            await Task.Delay(500);
            return;
        }

        try
        {
            int myPid = Environment.ProcessId;
            string readyEventName = @"Local\HuntReplay.NvidiaKeeperReady." + myPid;
            using var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);

            var startInfo = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Неизвестен путь приложения."))
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--nvidia-service-keeper {myPid}",
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var keeperProcess = Process.Start(startInfo);
            if (keeperProcess is not null)
            {
                log?.Write("Запрошены права администратора для временной приостановки службы NVIDIA.");
                bool signalled = await Task.Run(() => readyEvent.WaitOne(TimeSpan.FromSeconds(5)));
                if (signalled)
                {
                    log?.Write("Служба NVIDIA приостановлена хранителем. Будет автоматически возобновлена при закрытии.");
                    await Task.Delay(300);
                }
            }
        }
        catch (Exception ex)
        {
            log?.Write("Не удалось приостановить службу NVIDIA: " + ex.Message, "WARN");
        }
    }

    public static void RestoreServices(StudioLog? log)
    {
        foreach (var svc in StoppedServices)
        {
            try
            {
                if (StartServiceInternal(svc))
                {
                    log?.Write($"Служба {svc} успешно возобновлена.");
                }
            }
            catch (Exception ex)
            {
                log?.Write($"Ошибка запуска службы {svc}: " + ex.Message, "WARN");
            }
        }
        StoppedServices.Clear();
    }

    public static void RunKeeper(int mainPid)
    {
        var servicesToRestore = new List<string>();
        foreach (var svc in CandidateServices)
        {
            if (IsServiceRunning(svc) && StopServiceInternal(svc))
            {
                servicesToRestore.Add(svc);
            }
        }

        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\HuntReplay.NvidiaKeeperReady." + mainPid, out var readyEvent))
            {
                readyEvent.Set();
                readyEvent.Dispose();
            }
        }
        catch { }

        try
        {
            var mainProc = Process.GetProcessById(mainPid);
            mainProc.WaitForExit();
        }
        catch { }

        foreach (var svc in servicesToRestore)
        {
            try
            {
                StartServiceInternal(svc);
            }
            catch { }
        }
    }
}
