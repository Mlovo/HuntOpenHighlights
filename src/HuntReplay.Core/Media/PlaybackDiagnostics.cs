using HuntReplay.Core.Localization;

namespace HuntReplay.Core.Media;

/// <summary>
/// Wording for a failed in-app preview. Preview is a convenience only: the recorder wrote the MP4 and
/// export used stream copy, so a playback failure never proves the saved clip is broken, and nothing
/// here re-encodes, deletes or changes the file or the user's codec setting.
/// </summary>
public static class PlaybackDiagnostics
{
    // Windows Media Player's generic "file open failed" (NS_E_WMP_FILE_OPEN_FAILED). The code does not
    // name the codec or the cause, so the hint offers possibilities and never a diagnosis.
    private const int FileOpenFailed = unchecked((int)0xC00D11B1);

    public static string Code(int hresult) => $"0x{(uint)hresult:X8}";

    public static string? SymbolName(int hresult) => hresult == FileOpenFailed ? "NS_E_WMP_FILE_OPEN_FAILED" : null;

    public static string Hint(int hresult)
    {
        var code = Code(hresult);
        if (hresult == FileOpenFailed)
            return Text.Format("Playback.OpenFailedGeneric", code);
        var detail = hresult == 0 ? "" : Text.Format("Playback.CodeSuffix", code);
        return Text.Format("Playback.OpenFailed", detail);
    }

    public static string LogLine(int hresult, string fileName)
    {
        var symbol = SymbolName(hresult);
        return Text.Format("Playback.LogLine", fileName, Code(hresult)) + (symbol is null ? "" : " · " + symbol);
    }
}
