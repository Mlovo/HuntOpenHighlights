using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using HuntReplay.Core.Localization;

namespace HuntReplay.App.Services;

internal static class NvidiaServiceManager
{
    private static readonly string[] CandidateServices =
    [
        "NVDisplay.ContainerLocalSystem",
        "NvContainerLocalSystem"
    ];

    private static readonly List<string> StoppedServices = [];

    public static bool IsServiceRunning(string name) =>
        WithService(name, service => service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending);

    public static bool StopServiceInternal(string name) => WithService(name, service =>
    {
        if (service.Status == ServiceControllerStatus.Stopped) return true;
        if (service.Status != ServiceControllerStatus.StopPending)
        {
            try { service.Stop(); }
            catch (InvalidOperationException)
            {
                // The service may have stopped on its own in the meantime; anything else is a refusal.
                service.Refresh();
                if (service.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)) return false;
            }
        }
        // Wait up to 3 seconds for the service to finish stopping cleanly.
        try { service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(3)); }
        catch (System.ServiceProcess.TimeoutException) { }
        service.Refresh();
        return service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending;
    });

    public static bool StartServiceInternal(string name) => WithService(name, service =>
    {
        if (service.Status == ServiceControllerStatus.StopPending)
        {
            try { service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5)); }
            catch (System.ServiceProcess.TimeoutException) { }
            service.Refresh();
        }
        if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return true;
        service.Start();
        return true;
    });

    // A missing service, denied access or a refused start/stop reads as "no", as the raw SCM calls did.
    private static bool WithService(string name, Func<ServiceController, bool> action)
    {
        try
        {
            using var service = new ServiceController(name);
            return action(service);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
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
                            log?.Write(Text.Format("Nvidia.ProcessStopped", p.Id));
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Write(Text.Format("Nvidia.ProcessStopFailed", p.Id, ex.Message), "WARN");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Write(Text.Format("Nvidia.ProcessesStopError", ex.Message), "WARN");
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
                    log?.Write(Text.Get("Nvidia.ServicePaused"));
                    bool keeperReady = await StartKeeperProcessAsync(StoppedServices, elevated: false, log);
                    if (keeperReady)
                    {
                        log?.Write(Text.Get("Nvidia.KeeperActive"));
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
                log?.Write(Text.Get("Nvidia.PausedByKeeper"));
                await Task.Delay(300);
                StopResidualProcesses(log);
            }
            else
            {
                log?.Write(Text.Get("Nvidia.KeeperTimeout"), "WARN");
            }
        }
        catch (Exception ex)
        {
            log?.Write(Text.Format("Nvidia.PauseFailed", ex.Message), "WARN");
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
        var startInfo = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException(Text.Get("Startup.ExePathUnknown")))
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
                log?.Write(Text.Get("Nvidia.ElevationRequested"));
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
                    log?.Write(Text.Format("Nvidia.ServiceRestored", svc));
                }
            }
            catch (Exception ex)
            {
                log?.Write(Text.Format("Nvidia.ServiceRestoreFailed", svc, ex.Message), "WARN");
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
