using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HuntReplay.Core.Localization;

namespace HuntReplay.App.Interop;

internal static class RuntimeAssets
{
    private static readonly object Gate = new();
    private static bool loaded;
    private static IntPtr nativeLibrary;
    public static string? RuntimeDirectory { get; private set; }

    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (loaded) return;
            var assembly = Assembly.GetExecutingAssembly();
            using var manifest = assembly.GetManifestResourceStream("HuntReplay.Native.manifest")
                ?? throw new FileNotFoundException(Text.Get("Runtime.NotEmbedded"));
            using var reader = new StreamReader(manifest, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20];
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HuntReplayStudio", "runtime", version);
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(Text.Get("Runtime.DirectoryIsLink"));
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var entry = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (entry.Length != 2 || entry[0].Length != 64 || Path.GetFileName(entry[1]) != entry[1] ||
                    !entry[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(Text.Get("Runtime.BadManifest"));
                var path = Path.Combine(root, entry[1]);
                if (File.Exists(path))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException(Text.Get("Runtime.DllIsLink"));
                    using var existing = File.OpenRead(path);
                    if (Convert.ToHexString(SHA256.HashData(existing)).Equals(entry[0], StringComparison.OrdinalIgnoreCase)) continue;
                }
                using var source = assembly.GetManifestResourceStream("HuntReplay.Native." + entry[1])
                    ?? throw new FileNotFoundException(Text.Format("Runtime.MissingLibrary", entry[1]));
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
                    using (var check = File.OpenRead(temp))
                        if (!Convert.ToHexString(SHA256.HashData(check)).Equals(entry[0], StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(Text.Format("Runtime.ChecksumMismatch", entry[1]));
                    File.Move(temp, path, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            nativeLibrary = LoadLibraryEx(Path.Combine(root, "HuntReplay.Media.dll"), IntPtr.Zero, 0x100 | 0x1000);
            if (nativeLibrary == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), Text.Get("Runtime.LoadFailed"));
            NativeLibrary.SetDllImportResolver(assembly, (name, _, _) => name == "HuntReplay.Media.dll" ? nativeLibrary : IntPtr.Zero);
            if (NativeMethods.hr_abi_version() != 1) throw new InvalidOperationException(Text.Get("Runtime.AbiMismatch"));
            RuntimeDirectory = root;
            loaded = true;
            DeleteStaleRuntimes(root);
        }
    }

    // Every build with a new manifest extracts ~190 MB into a fresh folder; drop the old ones.
    private static void DeleteStaleRuntimes(string current)
    {
        foreach (var dir in new DirectoryInfo(Path.GetDirectoryName(current)!).EnumerateDirectories())
        {
            if (string.Equals(dir.FullName, current, StringComparison.OrdinalIgnoreCase) ||
                (dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            try { dir.Delete(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // still loaded by another running instance
        }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr reserved, uint flags);
}
