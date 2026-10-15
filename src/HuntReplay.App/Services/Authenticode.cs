using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using HuntReplay.Core.Localization;

namespace HuntReplay.App.Services;

internal static class Authenticode
{
    public static void VerifyNvidia(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
            File = pointer, StateAction = 1, ProviderFlags = 0x1000 // Cache-only trust; first run never downloads a DLL.
        };
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            var result = WinVerifyTrust(new IntPtr(-1), in action, ref data);
            if (result != 0) throw new InvalidDataException(Text.Format("Install.SignatureInvalid", $"0x{result:X8}"));
#pragma warning disable SYSLIB0057 // .NET 8 Authenticode certificate API; not a PFX import.
            using var signed = X509Certificate.CreateFromSignedFile(path);
            using var certificate = new X509Certificate2(signed);
#pragma warning restore SYSLIB0057
            if (!certificate.GetNameInfo(X509NameType.SimpleName, false).Equals("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Text.Get("Install.PublisherNotNvidia"));
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), in action, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr File, KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
    }
    [DllImport("wintrust", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, in Guid action, ref TrustData data);
}
