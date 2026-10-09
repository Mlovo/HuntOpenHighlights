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
            return "Встроенный проигрыватель не смог открыть этот MP4 (код " + code + ", общая ошибка открытия файла). " +
                "Код не называет конкретную причину: это может быть видеокодек (например, AV1 или HEVC без декодера Windows), " +
                "повреждённый файл или проблема проигрывателя. Клип сохранён и не изменён. " +
                "Откройте его кнопкой «Открыть в проигрывателе» или для новых клипов выберите H.264 в настройках видео.";
        var detail = hresult == 0 ? "" : " (код " + code + ")";
        return "Встроенный проигрыватель не смог открыть этот MP4" + detail + ". " +
            "Клип сохранён и не изменён; для проверки откройте его кнопкой «Открыть в проигрывателе».";
    }

    public static string LogLine(int hresult, string fileName)
    {
        var symbol = SymbolName(hresult);
        return "Предпросмотр не открыл клип " + fileName + " · код " + Code(hresult) + (symbol is null ? "" : " · " + symbol);
    }
}
