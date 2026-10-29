using System.Collections.Concurrent;
using Microsoft.Win32;

namespace HuntReplay.App.Services;

/// <summary>Driver version and dedicated video memory of a display adapter, as Windows records them for its driver.</summary>
internal sealed record GpuInfo(string? Driver, long? MemoryBytes)
{
    // Display adapter class; each adapter's driver has a numbered subkey with its description, version and memory size.
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private static readonly ConcurrentDictionary<string, GpuInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static GpuInfo For(string adapterName) => Cache.GetOrAdd(adapterName, Read);

    private static GpuInfo Read(string adapterName)
    {
        try
        {
            using var display = Registry.LocalMachine.OpenSubKey(DisplayClass);
            foreach (var name in display?.GetSubKeyNames() ?? [])
            {
                using var key = TryOpen(display!, name);
                if (key?.GetValue("DriverDesc") is not string description || !string.Equals(description.Trim(), adapterName.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                var version = key.GetValue("DriverVersion") as string;
                return new(DriverLabel(description, version, key.GetValue("RadeonSoftwareVersion") as string), Memory(key));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return new(null, null);
    }

    private static RegistryKey? TryOpen(RegistryKey parent, string name)
    {
        try { return parent.OpenSubKey(name); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return null; } // "Properties" is locked.
    }

    // The vendor's own version reads better than the Windows driver version: Adrenalin for AMD, the 5-digit release for NVIDIA.
    internal static string? DriverLabel(string description, string? version, string? radeonVersion)
    {
        if (!string.IsNullOrWhiteSpace(radeonVersion)) return radeonVersion.Trim();
        if (version is null) return null;
        var digits = new string(version.Where(char.IsAsciiDigit).ToArray());
        if (description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && digits.Length >= 5)
            return $"{digits[^5..^2]}.{digits[^2..]}"; // 32.0.15.6094 -> 560.94
        return version;
    }

    private static long? Memory(RegistryKey key) => key.GetValue("HardwareInformation.qwMemorySize") switch
    {
        long bytes when bytes > 0 => bytes,
        byte[] raw when raw.Length >= 8 => BitConverter.ToInt64(raw, 0),
        int bytes when bytes != 0 => (uint)bytes,
        _ => key.GetValue("HardwareInformation.MemorySize") switch { int bytes when bytes != 0 => (uint)bytes, byte[] raw when raw.Length >= 4 => BitConverter.ToUInt32(raw, 0), _ => null }
    };
}
