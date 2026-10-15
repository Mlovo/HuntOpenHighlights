using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
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
    private const uint SERVICE_START_PENDING = 2;
    private const uint SERVICE_STOP_PENDING = 3;
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
                return QueryServiceStatus(svc, ref status) &&
                    (status.dwCurrentState is SERVICE_RUNNING or SERVICE_START_PENDING);
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
                if (!QueryServiceStatus(svc, ref status)) return false;
                if (status.dwCurrentState == SERVICE_STOPPED) return true;

                if (status.dwCurrentState != SERVICE_STOP_PENDING)
                {
                    if (!ControlService(svc, SERVICE_CONTROL_STOP, ref status))
                    {
                        if (QueryServiceStatus(svc, ref status) &&
                            (status.dwCurrentState is SERVICE_STOPPED or SERVICE_STOP_PENDING))
                        {
                            // Already stopped or in the process of stopping
                        }
                        else
                        {
                            return false;
                        }
                    }
                }

                // Wait up to 3 seconds for service to finish stopping cleanly
                for (int i = 0; i < 15; i++)
                {
                    if (!QueryServiceStatus(svc, ref status)) break;
                    if (status.dwCurrentState == SERVICE_STOPPED) return true;
                    Thread.Sleep(200);
                }
                return status.dwCurrentState is SERVICE_STOPPED or SERVICE_STOP_PENDING;
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
                for (int i = 0; i < 25; i++)
                {
                    if (!QueryServiceStatus(svc, ref status)) break;
                    if (status.dwCurrentState is SERVICE_RUNNING or SERVICE_START_PENDING)
                        return true;
                    if (status.dwCurrentState != SERVICE_STOP_PENDING)
                        break;
                    Thread.Sleep(200);
                }
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

    public static void StopResidualProcesses(StudioLog? log = null)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var processes = Process.GetProcessesByName("nvcontainer");
                if (processes.Length == 0) break;
                foreach (var p in processes)
                {
                    try
                    {
                        if (!p.HasExited)
                        {
                            p.Kill(true);
                            p.WaitForExit(1000);
                            log?.Write($"Процесс nvcontainer (PID {p.Id}) успешно завершён.");
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Write($"Не удалось завершить процесс nvcontainer (PID {p.Id}): {ex.Message}", "WARN");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Write("Ошибка при завершении процессов nvcontainer: " + ex.Message, "WARN");
                break;
            }
            if (attempt == 0) Thread.Sleep(100);
        }
    }

    public static async Task EnsureServicePausedAsync(StudioLog? log)
    {
        var runningServices = CandidateServices.Where(IsServiceRunning).ToList();
        var runningProcesses = Process.GetProcessesByName("nvcontainer");
        bool hasProcesses = runningProcesses.Length > 0;
        foreach (var p in runningProcesses) p.Dispose();

        if (runningServices.Count == 0 && !hasProcesses) return;

        bool isAdmin = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { }

        bool directSuccess = false;
        if (isAdmin)
        {
            directSuccess = true;
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

            StopResidualProcesses(log);

            if (directSuccess)
            {
                if (StoppedServices.Count > 0)
                {
                    log?.Write("Служба NVIDIA временно остановлена для автозахвата хайлайтов. Будет включена обратно при выходе.");
                    bool keeperReady = await StartKeeperProcessAsync(StoppedServices, elevated: false, log);
                    if (keeperReady)
                    {
                        log?.Write("Хранитель службы NVIDIA активен.");
                    }
                }
                return;
            }
        }

        try
        {
            var servicesToPass = runningServices;
            bool signalled = await StartKeeperProcessAsync(servicesToPass, elevated: true, log);
            if (signalled)
            {
                log?.Write("Служба NVIDIA приостановлена хранителем. Будет автоматически возобновлена при закрытии.");
                await Task.Delay(300);
                StopResidualProcesses(log);
            }
            else
            {
                log?.Write("Хранитель службы NVIDIA не ответил в отведённое время.", "WARN");
            }
        }
        catch (Exception ex)
        {
            log?.Write("Не удалось приостановить службу NVIDIA: " + ex.Message, "WARN");
        }
    }

    private static EventWaitHandle CreateKeeperReadyEvent(string name, out bool createdNew)
    {
        try
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                EventWaitHandleRights.FullControl,
                AccessControlType.Allow));
            var handle = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, name, out createdNew, security);
            if (!createdNew) handle.Reset();
            return handle;
        }
        catch
        {
            var handle = new EventWaitHandle(false, EventResetMode.ManualReset, name, out createdNew);
            if (!createdNew) handle.Reset();
            return handle;
        }
    }

    private static async Task<bool> StartKeeperProcessAsync(IEnumerable<string> services, bool elevated, StudioLog? log)
    {
        int myPid = Environment.ProcessId;
        string readyEventName = @"Local\HuntReplay.NvidiaKeeperReady." + myPid;
        using var readyEvent = CreateKeeperReadyEvent(readyEventName, out _);

        var serviceArgs = string.Join(" ", services);
        var startInfo = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Неизвестен путь приложения."))
        {
            UseShellExecute = true,
            Verb = elevated ? "runas" : "",
            Arguments = $"--nvidia-service-keeper {myPid} {serviceArgs}".Trim(),
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var keeperProcess = Process.Start(startInfo);
        if (keeperProcess is not null)
        {
            if (elevated)
                log?.Write("Запрошены права администратора для временной приостановки службы NVIDIA.");
            var timeout = TimeSpan.FromSeconds(elevated ? 30 : 10);
            return await Task.Run(() => readyEvent.WaitOne(timeout));
        }
        return false;
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

    public static void RunKeeper(int mainPid, string[]? servicesToKeep = null)
    {
        var servicesToRestore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (servicesToKeep != null)
        {
            foreach (var s in servicesToKeep)
            {
                if (!string.IsNullOrWhiteSpace(s) &&
                    CandidateServices.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase))
                    servicesToRestore.Add(s.Trim());
            }
        }

        foreach (var svc in CandidateServices)
        {
            if (IsServiceRunning(svc))
            {
                servicesToRestore.Add(svc);
                StopServiceInternal(svc);
            }
        }

        StopResidualProcesses();

        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\HuntReplay.NvidiaKeeperReady." + mainPid, out var readyEvent))
            {
                readyEvent.Set();
                readyEvent.Dispose();
            }
        }
        catch { }

        if (servicesToRestore.Count == 0)
        {
            return;
        }

        try
        {
            using var mainProc = Process.GetProcessById(mainPid);
            mainProc.WaitForExit();
        }
        catch { }

        foreach (var svc in servicesToRestore)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (StartServiceInternal(svc)) break;
                }
                catch { }
                Thread.Sleep(500);
            }
        }
    }
}
