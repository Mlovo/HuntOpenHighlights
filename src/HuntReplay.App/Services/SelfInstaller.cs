using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace HuntReplay.App.Services;

internal sealed record SetupStatus(bool DllValid, bool Registry64, bool Registry32, bool Conflict, string Detail)
{
    public bool Ready => DllValid && Registry64 && Registry32;
}

internal static class SelfInstaller
{
    public const string DllHash = "64e300997b5796da22ad8baa49e4eb919cdc170157cbe6770279f94508a84650";
    public const string ShimDirectory = @"C:\NvidiaShim";
    public const string DllPath = @"C:\NvidiaShim\MessageBus.dll";
    private const string BusKey = @"SOFTWARE\NVIDIA Corporation\NvContainer\MessageBus";
    private const string MapKey = @"SOFTWARE\NVIDIA Corporation\NvContainer\ModuleMap";
    private static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HuntReplayStudio");
    private static string BackupFile => Path.Combine(DataDirectory, "shim-installation.json");
    private static string LogDirectory => Path.Combine(DataDirectory, "MessageBusLogs");
    private static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];

    private sealed record BackupValue(RegistryView View, string Key, string Name, bool KeyExisted,
        bool Existed, RegistryValueKind Kind, JsonElement Value);
    private sealed class InstallationBackup
    {
        public string Owner { get; set; } = "HuntReplayStudio/1";
        public string Hash { get; set; } = DllHash;
        public bool CreatedDll { get; set; }
        public bool Completed { get; set; }
        public List<BackupValue> Values { get; set; } = [];
    }

    private static IEnumerable<(string Key, string Name, object Value, RegistryValueKind Kind)> DesiredValues()
    {
        yield return (BusKey, "InstallPath", ShimDirectory, RegistryValueKind.String);
        yield return (BusKey, "LogPath", LogDirectory, RegistryValueKind.String);
        yield return (BusKey, "LogLevel", 0, RegistryValueKind.DWord);
        yield return (MapKey, "MessageBus.dll", DllPath, RegistryValueKind.String);
    }

    public static SetupStatus Check()
    {
        var valid = false;
        var detail = "MessageBus.dll ещё не установлен.";
        if (File.Exists(DllPath))
        {
            try { CheckDll(DllPath); valid = true; detail = "SHA-256 и цифровая подпись NVIDIA подтверждены Windows."; }
            catch (Exception ex) { detail = ex.Message; }
        }
        bool r64 = RegistryReady(RegistryView.Registry64), r32 = RegistryReady(RegistryView.Registry32);
        var conflict = Views.Any(view =>
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(BusKey);
            using var map = hklm.OpenSubKey(MapKey);
            var install = key?.GetValue("InstallPath") as string;
            var dll = map?.GetValue("MessageBus.dll") as string;
            return (!string.IsNullOrEmpty(install) && !install.TrimEnd('\\').Equals(ShimDirectory, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrEmpty(dll) && !dll.Equals(DllPath, StringComparison.OrdinalIgnoreCase));
        });
        return new(valid, r64, r32, conflict, detail);
    }

    private static bool RegistryReady(RegistryView view)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = hklm.OpenSubKey(BusKey);
        using var map = hklm.OpenSubKey(MapKey);
        return string.Equals(key?.GetValue("InstallPath") as string, ShimDirectory, StringComparison.OrdinalIgnoreCase) &&
            key?.GetValue("LogPath") is string && key.GetValue("LogLevel") is int &&
            string.Equals(map?.GetValue("MessageBus.dll") as string, DllPath, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<int> RunElevatedAsync(bool uninstall = false, bool replaceNvidia = false)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Неизвестен путь приложения."))
        {
            UseShellExecute = true, Verb = "runas",
            Arguments = uninstall ? "--uninstall-shim" : "--install-shim" + (replaceNvidia ? " --replace-nvidia" : "")
        };
        using var child = Process.Start(start) ?? throw new IOException("UAC не запустил установщик.");
        await child.WaitForExitAsync().ConfigureAwait(false);
        return child.ExitCode;
    }

    public static void Install(bool replaceNvidia)
    {
        RequireAdministrator();
        RefuseLinks(ShimDirectory); RefuseLinks(DataDirectory);
        var status = Check();
        if (status.Conflict && !replaceNvidia)
            throw new InvalidOperationException("Обнаружена другая регистрация NVIDIA. Нужно явное согласие на замену с резервной копией.");
        if (File.Exists(DllPath)) CheckDll(DllPath); // Never overwrite an unknown DLL.
        ProtectDirectory(DataDirectory, writableByUsers: false);
        if (File.Exists(BackupFile)) RefuseFileLink(BackupFile);
        var beforeAttempt = CaptureRegistryState();
        bool hadBackup = File.Exists(BackupFile);
        bool createdDllThisAttempt = false;
        InstallationBackup backup;
        if (hadBackup)
        {
            backup = ReadBackup();
            if (!backup.Completed) throw new IOException("Незавершённая установка. Сначала выполните восстановление регистрации.");
        }
        else
        {
            backup = beforeAttempt;
            SaveBackup(backup);
        }
        try
        {
            ProtectDirectory(ShimDirectory, writableByUsers: false);
            // NVIDIA logging is disabled. Keep even the log directory protected: an elevated
            // vendor process must never be redirected through a user-writable log junction.
            ProtectDirectory(LogDirectory, writableByUsers: false);
            if (!File.Exists(DllPath))
            {
                var temp = Path.Combine(ShimDirectory, "MessageBus." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("HuntReplay.Payload.MessageBus.dll")
                        ?? throw new FileNotFoundException("Встроенная MessageBus.dll отсутствует."))
                    using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(destination);
                    CheckDll(temp);
                    File.Move(temp, DllPath);
                    createdDllThisAttempt = true;
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            ProtectFile(DllPath);
            foreach (var view in Views)
            foreach (var entry in DesiredValues())
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.CreateSubKey(entry.Key, true);
                key.SetValue(entry.Name, entry.Value, entry.Kind);
                key.Flush();
            }
            backup.Completed = true;
            SaveBackup(backup);
        }
        catch (Exception installError)
        {
            try
            {
                // A failed repair returns to the state BEFORE this attempt, not before the first
                // successful installation. Preserve its original backup for a later uninstall.
                RestoreValues(beforeAttempt, onlyIfOwned: true);
                if (createdDllThisAttempt && File.Exists(DllPath) && !DllReferenced())
                { CheckDll(DllPath); File.Delete(DllPath); }
                if (!hadBackup && File.Exists(BackupFile)) File.Delete(BackupFile);
            }
            catch (Exception rollbackError)
            {
                throw new IOException("Установка и автоматический rollback не завершены. Резервная копия сохранена; закройте игру и выполните восстановление. " + rollbackError.Message,
                    new AggregateException(installError, rollbackError));
            }
            throw;
        }
    }

    public static void Uninstall()
    {
        RequireAdministrator();
        RefuseLinks(DataDirectory); RefuseLinks(ShimDirectory);
        if (!File.Exists(BackupFile)) throw new IOException("Нет резервной копии этой установки. Чужие ключи удаляться не будут.");
        var backup = ReadBackup();
        RestoreValues(backup, onlyIfOwned: true);
        if (backup.CreatedDll && File.Exists(DllPath))
        {
            // Keep the DLL if any registry view still references it (e.g. a later installer changed LogPath).
            var referenced = DllReferenced();
            if (!referenced) { CheckDll(DllPath); File.Delete(DllPath); }
        }
        File.Delete(BackupFile);
        if (Directory.Exists(ShimDirectory) && !Directory.EnumerateFileSystemEntries(ShimDirectory).Any()) Directory.Delete(ShimDirectory);
        // Never delete NVIDIA parent keys, clip storage, logs, or arbitrary contents of C:\NvidiaShim.
    }

    private static bool DllReferenced() => Views.Any(view =>
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var map = hklm.OpenSubKey(MapKey);
        using var bus = hklm.OpenSubKey(BusKey);
        return string.Equals(map?.GetValue("MessageBus.dll") as string, DllPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(bus?.GetValue("InstallPath") as string, ShimDirectory, StringComparison.OrdinalIgnoreCase);
    });

    private static InstallationBackup CaptureRegistryState()
    {
        var backup = new InstallationBackup { CreatedDll = !File.Exists(DllPath) };
        foreach (var view in Views)
        foreach (var entry in DesiredValues())
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(entry.Key);
            var present = key?.GetValueNames().Contains(entry.Name, StringComparer.OrdinalIgnoreCase) == true;
            var value = present ? key!.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
            backup.Values.Add(new(view, entry.Key, entry.Name, key is not null, present,
                present ? key!.GetValueKind(entry.Name) : RegistryValueKind.String, JsonSerializer.SerializeToElement(value)));
        }
        return backup;
    }

    private static void RestoreValues(InstallationBackup backup, bool onlyIfOwned)
    {
        foreach (var item in backup.Values)
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, item.View);
            using var current = hklm.OpenSubKey(item.Key, true);
            var expected = DesiredValues().Single(v => v.Key == item.Key && v.Name == item.Name);
            if (onlyIfOwned && !Equals(current?.GetValue(item.Name), expected.Value)) continue;
            if (item.Existed)
            {
                using var key = hklm.CreateSubKey(item.Key, true);
                object value = item.Kind switch
                {
                    RegistryValueKind.DWord => item.Value.GetInt32(), RegistryValueKind.QWord => item.Value.GetInt64(),
                    RegistryValueKind.Binary or RegistryValueKind.None => item.Value.GetBytesFromBase64(),
                    RegistryValueKind.MultiString => item.Value.Deserialize<string[]>()!,
                    _ => item.Value.GetString()!
                };
                key.SetValue(item.Name, value, item.Kind);
            }
            else current?.DeleteValue(item.Name, false);
        }
        foreach (var item in backup.Values.Where(v => !v.KeyExisted).DistinctBy(v => (v.View, v.Key)))
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, item.View);
            bool empty;
            using (var key = hklm.OpenSubKey(item.Key)) empty = key is not null && key.ValueCount == 0 && key.SubKeyCount == 0;
            if (empty) hklm.DeleteSubKey(item.Key, false);
        }
    }

    private static InstallationBackup ReadBackup()
    {
        RefuseFileLink(BackupFile);
        var owner = new FileInfo(BackupFile).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidDataException("Не определён владелец резервной копии.");
        if (!owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !owner.IsWellKnown(WellKnownSidType.LocalSystemSid))
            throw new InvalidDataException("Резервная копия не принадлежит Administrators/SYSTEM. Установка остановлена.");
        if (new FileInfo(BackupFile).Length > 65536) throw new InvalidDataException("Некорректная резервная копия установки.");
        var backup = JsonSerializer.Deserialize<InstallationBackup>(File.ReadAllText(BackupFile)) ?? throw new InvalidDataException("Пустая резервная копия.");
        if (backup.Owner != "HuntReplayStudio/1" || backup.Hash != DllHash || backup.Values.Count != 8 ||
            backup.Values.Any(v => !Views.Contains(v.View) || !DesiredValues().Any(e => e.Key == v.Key && e.Name == v.Name)) ||
            backup.Values.DistinctBy(v => (v.View, v.Key, v.Name)).Count() != 8)
            throw new InvalidDataException("Резервная копия установки не принадлежит HuntReplay Studio.");
        return backup;
    }

    private static void SaveBackup(InstallationBackup backup)
    {
        var temp = BackupFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(file, backup); file.Flush(true); }
            ProtectFile(temp);
            File.Move(temp, BackupFile, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void CheckDll(string path)
    {
        RefuseFileLink(path);
        using (var stream = File.OpenRead(path))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(DllHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("MessageBus.dll отличается от встроенного оригинала (SHA-256). Файл не будет заменён.");
        Authenticode.VerifyNvidia(path);
    }

    private static void ProtectDirectory(string path, bool writableByUsers)
    {
        RefuseLinks(path);
        var directory = Directory.CreateDirectory(path);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(admins);
        foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            writableByUsers ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
    private static void ProtectFile(string path)
    {
        RefuseFileLink(path);
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(admins);
        acl.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }

    private static void RefuseLinks(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Установка через reparse point запрещена: " + directory.FullName);
    }
    private static void RefuseFileLink(string path)
    {
        RefuseLinks(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("DLL/резервная копия не должна быть ссылкой.");
    }
    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Для установки MessageBus нужен отдельный UAC-процесс.");
    }
}
