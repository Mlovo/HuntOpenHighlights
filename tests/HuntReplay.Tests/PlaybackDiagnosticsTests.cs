using HuntReplay.Core.Media;

namespace HuntReplay.Tests;

public static partial class Program
{
    private static void PlaybackDiagnosticsTests()
    {
        const int fileOpenFailed = unchecked((int)0xC00D11B1);
        Test("preview error keeps the HRESULT and its documented WMP symbol", () =>
        {
            Equal("0xC00D11B1", PlaybackDiagnostics.Code(fileOpenFailed));
            Equal("NS_E_WMP_FILE_OPEN_FAILED", PlaybackDiagnostics.SymbolName(fileOpenFailed));
            Equal("0x80004005", PlaybackDiagnostics.Code(unchecked((int)0x80004005)));
            True(PlaybackDiagnostics.SymbolName(unchecked((int)0x80004005)) is null);
        });
        Test("generic file-open hint offers possible causes without a diagnosis", () =>
        {
            var hint = PlaybackDiagnostics.Hint(fileOpenFailed);
            True(hint.Contains("0xC00D11B1")); True(hint.Contains("не называет конкретную причину"));
            True(hint.Contains("может быть видеокодек")); True(hint.Contains("AV1")); True(hint.Contains("HEVC"));
            False(hint.Contains("точно")); False(hint.Contains("отсутствует")); False(hint.Contains("не установлен"));
        });
        Test("hint says the saved clip is intact and offers Windows playback and H.264 for new clips", () =>
        {
            var hint = PlaybackDiagnostics.Hint(fileOpenFailed);
            True(hint.Contains("Клип сохранён и не изменён"));
            True(hint.Contains("Открыть в проигрывателе")); True(hint.Contains("H.264"));
            var other = PlaybackDiagnostics.Hint(unchecked((int)0x80004005));
            True(other.Contains("0x80004005")); True(other.Contains("Клип сохранён и не изменён"));
            False(other.Contains("NS_E_WMP"));
            var unknown = PlaybackDiagnostics.Hint(0);
            False(unknown.Contains("0x00000000")); True(unknown.Contains("Клип сохранён и не изменён"));
        });
        Test("log line names the code and file but carries no control characters", () =>
        {
            var line = PlaybackDiagnostics.LogLine(fileOpenFailed, "Hunt_HUNTER_KILLED_2026-10-08.mp4");
            True(line.Contains("0xC00D11B1")); True(line.Contains("NS_E_WMP_FILE_OPEN_FAILED"));
            True(line.Contains("Hunt_HUNTER_KILLED_2026-10-08.mp4"));
            False(line.Any(char.IsControl));
        });
    }
}
