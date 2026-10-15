using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace HuntReplay.App.Services;

public enum AppLanguage
{
    Russian,
    English
}

public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    private AppLanguage currentLanguage = AppLanguage.Russian;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppLanguage CurrentLanguage
    {
        get => currentLanguage;
        set
        {
            if (currentLanguage == value) return;
            currentLanguage = value;
            Notify(null); // Notify all properties bound to indexer / localized text
        }
    }

    public bool IsRussian => currentLanguage == AppLanguage.Russian;
    public bool IsEnglish => currentLanguage == AppLanguage.English;

    public string LanguageCode => currentLanguage == AppLanguage.Russian ? "RU" : "EN";

    public void ToggleLanguage()
    {
        CurrentLanguage = currentLanguage == AppLanguage.Russian ? AppLanguage.English : AppLanguage.Russian;
    }

    public void SetLanguage(string? code)
    {
        if (string.Equals(code, "en", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(code, "english", StringComparison.OrdinalIgnoreCase))
        {
            CurrentLanguage = AppLanguage.English;
        }
        else
        {
            CurrentLanguage = AppLanguage.Russian;
        }
    }

    public string this[string key] => Get(key);

    public string Get(string key)
    {
        bool en = currentLanguage == AppLanguage.English;
        return key switch
        {
            // Sidebar Navigation
            "Nav.Workspace" => en ? "WORKSPACE" : "РАБОЧЕЕ ПРОСТРАНСТВО",
            "Nav.Dashboard" => en ? "Overview" : "Обзор",
            "Nav.Gallery" => en ? "Clip Gallery" : "Галерея клипов",
            "Nav.Settings" => en ? "Settings" : "Настройки",
            "Nav.Diagnostics" => en ? "Diagnostics" : "Диагностика",
            "Nav.TaglineTitle" => en ? "YOUR MOMENTS. YOUR RAM." : "ВАШИ МОМЕНТЫ. ВАША RAM.",
            "Nav.TaglineDesc" => en ? "No cloud, no OBS, no extra keystrokes. Just play." : "Без облака, OBS и лишних нажатий. Просто играйте.",
            "Nav.OpenFolder" => en ? "Open clips folder  ↗" : "Открыть папку клипов  ↗",

            // Page Titles
            "PageTitle.Dashboard" => en ? "Overview" : "Обзор",
            "PageTitle.Gallery" => en ? "Highlight Library" : "Библиотека хайлайтов",
            "PageTitle.Settings" => en ? "Recording Settings" : "Настройки записи",
            "PageTitle.Diagnostics" => en ? "System Readiness" : "Готовность системы",

            // Header & Status
            "Header.Sub" => en ? "FIELD RECORDER  /  HUNT: SHOWDOWN 1896" : "FIELD RECORDER  /  HUNT: SHOWDOWN 1896",
            "Header.UpdateAvailable" => en ? "UPDATE AVAILABLE ↗" : "ДОСТУПНО ОБНОВЛЕНИЕ ↗",
            "Header.UpdateAvailableVersion" => en ? "UPDATE v{0} ↗" : "ОБНОВЛЕНИЕ v{0} ↗",
            "Header.UpdateTooltip" => en ? "Open release page on GitHub" : "Открыть страницу релиза на GitHub",
            "Header.SetupReady" => en ? "HIGHLIGHTS READY" : "HIGHLIGHTS ГОТОВ",
            "Header.SetupNeeded" => en ? "SETUP REQUIRED" : "НУЖНА УСТАНОВКА",
            "Header.ThemeTooltip" => en ? "Switch theme (Dark / Light)" : "Переключить тему (тёмная / светлая)",
            "Theme.Dark" => en ? "Dark" : "Тёмная",
            "Theme.Light" => en ? "Light" : "Светлая",
            "System.Theme" => en ? "Interface Theme" : "Тема интерфейса",

            // Footer
            "Footer.DefaultMessage" => en ? "All data stays on your computer." : "Все данные остаются на вашем компьютере.",
            "Footer.DiagnosticsDefault" => en ? "Run check to generate a local system readiness report." : "Запустите проверку, чтобы получить локальный отчёт о готовности системы.",

            // Dashboard
            "Dash.GameConnected" => en ? "{0} connected" : "{0} подключён",
            "Dash.GameWindowFound" => en ? "Game window detected · waiting for SDK" : "Окно игры найдено · ожидание SDK",
            "Dash.WaitingForGame" => en ? "Waiting for game…" : "Ожидание игры…",
            "Dash.HeroTitle" => en ? "Good hunting.\nNot a single moment lost." : "Хорошая охота.\nНи одного упущенного момента.",
            "Dash.HeroDesc" => en ? "Just play. Your best moments are saved automatically." : "Играйте. Ваши лучшие эпизоды сохранятся сами.",
            "Dash.RingBuffer" => en ? "RING BUFFER" : "КОЛЬЦЕВОЙ БУФЕР",
            "Dash.BufferWriting" => en ? "Buffer recording" : "Буфер записывает",
            "Dash.BufferWarming" => en ? "Buffer warming up…" : "Прогрев буфера…",
            "Dash.BufferStopped" => en ? "Buffer stopped" : "Буфер остановлен",
            "Dash.RamSuffix" => en ? "MB RAM" : "МБ RAM",
            "Dash.PendingEpisodes" => en ? "{0} clip(s) · awaiting post-roll" : "{0} эпизод(а) · ожидается постзапись",
            "Dash.SavingMp4" => en ? "Saving MP4 · {0}" : "Сохранение MP4 · {0}",
            "Dash.ReadyForNext" => en ? "Ready for next event" : "Готов к следующему событию",
            "Dash.RecordingEngine" => en ? "RECORDING ENGINE" : "ДВИЖОК ЗАПИСИ",
            "Dash.GpuWaiting" => en ? "{0} · idle" : "{0} · ожидание",
            "Dash.GpuUnknown" => en ? "GPU undetected" : "GPU не определён",
            "Dash.CpuExplicit" => en ? "CPU x264 · EXPLICIT" : "CPU x264 · ВЫБРАН ЯВНО",
            "Dash.GpuEncoding" => en ? "GPU ENCODING" : "GPU ENCODING",
            "Dash.WgcD3D" => en ? "Windows Graphics Capture · Direct3D 11" : "Windows Graphics Capture · Direct3D 11",
            "Dash.MemoryPressure" => en ? "RAM limit reached: start of clip may be trimmed" : "Лимит RAM: начало клипа может быть укорочено",
            "Dash.SourceStale" => en ? "Source untouched >2s (static screen / minimized window)" : "Источник не обновлялся >2 с (статичный экран / свёрнутое окно)",
            "Dash.FramesDetail" => en ? "Frames compressed: {0:N0} · dropped: {1:N0}" : "Сжатых кадров: {0:N0} · пропущено: {1:N0}",
            "Dash.SaveManual" => en ? "Save last {0} s" : "Сохранить последние {0} с",
            "Dash.StopBuffer" => en ? "Stop buffer" : "Остановить буфер",
            "Dash.StartBuffer" => en ? "Start buffer" : "Запустить буфер",
            "Dash.RecentHighlights" => en ? "Recent highlights" : "Последние хайлайты",
            "Dash.AllClips" => en ? "All clips  →" : "Все клипы  →",
            "Dash.NoClipsTitle" => en ? "Your first hunt is yet to come" : "Первая охота ещё впереди",
            "Dash.NoClipsDesc" => en ? "Launch the game or select a monitor in Settings. Real clips will appear here." : "Запустите игру или выберите монитор в настройках. Здесь появятся настоящие клипы.",
            "Dash.SessionLog" => en ? "Session Log" : "Журнал сессии",

            // Gallery
            "Gallery.Title" => en ? "Relive the moment" : "Снова проживите этот момент",
            "Gallery.Summary" => en ? "{0} clip(s) · {1:F2} GB" : "{0} клип(ов) · {1:F2} ГБ",
            "Gallery.Refresh" => en ? "Refresh" : "Обновить",
            "Gallery.RefreshTooltip" => en ? "Refresh clip list" : "Обновить список клипов",
            "Gallery.Folder" => en ? "Clips folder" : "Папка клипов",
            "Gallery.FolderTooltip" => en ? "Open saved recordings folder" : "Открыть папку с сохранёнными записями",
            "Gallery.SelectClipHint" => en ? "Select a clip to preview" : "Выберите клип, чтобы посмотреть",
            "Gallery.OpenFailed" => en ? "Failed to open video clip." : "Не удалось открыть видеоклип.",
            "Gallery.Rename" => en ? "Rename" : "Переименовать",
            "Gallery.Reveal" => en ? "Show in Explorer ↗" : "Показать в проводнике ↗",
            "Gallery.OpenInPlayer" => en ? "Open with system player ↗" : "Открыть в проигрывателе ↗",
            "Gallery.Delete" => en ? "Delete" : "Удалить",
            "Gallery.NextStory" => en ? "Your next story" : "Ваша следующая история",
            "Gallery.SearchPlaceholder" => en ? "Search by event or filename…" : "Поиск по событию или имени файла…",
            "Gallery.DeleteConfirm" => en ? "Delete clip permanently? File will be removed from disk." : "Удалить клип безвозвратно? Файл будет стерт с диска.",
            "Gallery.DeleteTitle" => en ? "Delete clip" : "Удаление клипа",
            "Gallery.RenameTitle" => en ? "Rename clip" : "Переименование клипа",
            "Gallery.RenamePrompt" => en ? "Enter new file name:" : "Введите новое имя файла:",
            "Gallery.Volume" => en ? "Volume" : "Громкость",
            "Gallery.Unmute" => en ? "Unmute" : "Включить звук",
            "Gallery.MuteAt" => en ? "Mute ({0}%)" : "Выключить звук ({0}%)",
            "Gallery.EmptyTitle" => en ? "Library is quiet for now" : "В библиотеке пока тихо",
            "Gallery.EmptyDesc" => en ? "After the first save, a preview of your actual clip will appear here." : "После первого сохранения здесь появится превью настоящего клипа.",
            "Gallery.NoClipSelected" => en ? "Your next story" : "Ваша следующая история",

            // Settings
            "Settings.Notice" => en ? "Changes will restart the buffer. Pending highlights will be saved first." : "Изменения перезапустят буфер. Ожидающие хайлайты сначала будут сохранены.",
            "Settings.Save" => en ? "Save Settings" : "Сохранить настройки",
            "Settings.UnsavedChangesTitle" => en ? "Unsaved Settings" : "Несохранённые настройки",
            "Settings.UnsavedChangesPrompt" => en ? "You have unsaved changes in settings. Would you like to save them before leaving?" : "У вас есть несохранённые изменения в настройках. Сохранить их перед переходом?",
            "Settings.TabVideo" => en ? "Video" : "Видео",
            "Settings.TabAudio" => en ? "Audio" : "Аудио",
            "Settings.TabReplay" => en ? "Buffer & Events" : "Буфер и события",
            "Settings.TabStorage" => en ? "Storage" : "Хранилище",
            "Settings.TabSystem" => en ? "System" : "Система",

            // Video tab
            "Video.Title" => en ? "Direct from the game into memory" : "Из игры — прямо в память",
            "Video.Subtitle" => en ? "WGC captures via Windows API. Encoding via GPU without external tools." : "WGC захватывает экран средствами Windows. Сжатие — через GPU, без внешнего рекордера.",
            "Video.Source" => en ? "Capture Source" : "Источник захвата",
            "Video.Monitor" => en ? "Monitor" : "Монитор",
            "Video.SourceHint" => en ? "In window mode, desktop is not captured when game is inactive. Monitor explicitly captures the entire display." : "В режиме окна рабочий стол не захватывается при отсутствии игры. Монитор — явное разрешение записывать весь экран.",
            "Video.Resolution" => en ? "Resolution" : "Разрешение",
            "Video.Fps" => en ? "Frame rate / FPS" : "Частота кадров / FPS",
            "Video.Gpu" => en ? "Graphics Adapter (GPU)" : "Видеокарта (GPU)",
            "Video.GpuHint" => en ? "Auto selects the GPU connected to the display. If the selected card is unavailable, the default adapter is used." : "Авто выбирает видеокарту, к которой подключён монитор. При недоступности выбранной карты используется адаптер по умолчанию.",
            "Video.Encoder" => en ? "Video Encoder" : "Видеокодировщик",
            "Video.Codec" => en ? "Codec" : "Кодек",
            "Video.EncoderHint" => en ? "Auto picks AMF / NVENC / QSV by the display's GPU. Validated at startup; no silent CPU fallback." : "Auto выбирает AMF / NVENC / QSV по GPU захватываемого дисплея. Возможность кодирования проверяется при старте; автоматического перехода на CPU нет.",
            "Video.Bitrate" => en ? "Bitrate" : "Битрейт",
            "Video.KeyframeInterval" => en ? "Keyframe interval / seconds" : "Интервал ключевых кадров / секунды",
            "Video.GopHint" => en ? "1s GOP provides precise trimming without re-encoding. HDR and 10-bit not supported: please use SDR." : "GOP 1 сек — более точная обрезка без перекодирования. HDR и 10-bit не поддерживаются: используйте SDR.",

            // Audio tab
            "Audio.Title" => en ? "The sound of your hunt" : "Звук вашей охоты",
            "Audio.Subtitle" => en ? "WASAPI loopback · AAC 48 kHz · stereo" : "WASAPI loopback · AAC 48 кГц · стерео",
            "Audio.Output" => en ? "Output / Game audio" : "Вывод / звук игры",
            "Audio.OutputHint" => en ? "All audio played on selected headphones/speakers is captured, including Discord and browser." : "Записывается весь звук выбранных наушников или динамиков, включая браузер и Discord. Это не изолированный захват только процесса игры.",
            "Audio.RecordMic" => en ? "Record microphone" : "Записывать микрофон",
            "Audio.Mic" => en ? "Microphone" : "Микрофон",
            "Audio.MicGain" => en ? "Microphone volume" : "Громкость микрофона",
            "Audio.Tracks" => en ? "Audio tracks" : "Аудиодорожки",
            "Audio.TracksHint" => en ? "In separate tracks mode, track 1 is game audio, track 2 is mic. Standard players usually play track 1." : "В раздельном режиме основной трек — звук игры; второй — микрофон. Встроенный Windows-плеер обычно воспроизводит первый трек.",
            "Audio.CardBottom" => en ? "If headphones are unplugged or Windows switches endpoints, an error is logged. Auto-restart retries up to 3 times." : "Если наушники отключены или Windows меняет endpoint, ошибка будет показана в журнале. Автоматический режим повторит запуск до трёх раз. Для микрофона разрешите доступ классическим приложениям в параметрах конфиденциальности Windows.",

            // Replay tab
            "Replay.Title" => en ? "Plenty of time before and after" : "Достаточно времени до и после",
            "Replay.Subtitle" => en ? "Compressed packets remain in RAM. Disk is only used when saving a clip." : "В RAM хранятся уже сжатые пакеты. Диск используется только при сохранении клипа.",
            "Replay.BufferLength" => en ? "Buffer length" : "Длина буфера",
            "Replay.BufferHint" => en ? "60 seconds at 25 Mbps ≈ 179 MiB. Unfinished kill streaks hold extra GOPs temporarily." : "60 секунд при 25 Мбит/с ≈ 179 МиБ видео. Незавершённая серия временно удерживает дополнительные GOP; при нехватке RAM начало клипа укорачивается с предупреждением.",
            "Replay.PostRoll" => en ? "Post-roll / seconds" : "Постзапись / секунды",
            "Replay.PreRoll" => en ? "Pre-roll default / seconds" : "Предзапись, если игра не задала окно / сек",
            "Replay.CoalesceCheck" => en ? "Coalesce consecutive events into a single highlight" : "Объединять близкие события в один хайлайт",
            "Replay.CoalesceHint" => en ? "Default merge window is 15 seconds. File appears after waiting for next kill, without padding." : "Окно объединения по умолчанию — 15 секунд. Файл появится после ожидания следующего фрага, но лишнее ожидание в сам клип не попадёт.",
            "Replay.CoalesceGap" => en ? "Gap between events / sec" : "Интервал между событиями / сек",
            "Replay.MaxSequence" => en ? "Max sequence length / sec" : "Максимальная серия / секунды",
            "Replay.GameEvents" => en ? "GAME EVENTS" : "ИГРОВЫЕ СОБЫТИЯ",
            "Replay.HunterKilled" => en ? "Hunter killed  /  HUNTER_KILLED" : "Убийство охотника  /  HUNTER_KILLED",
            "Replay.PlayerDowned" => en ? "Player downed or killed  /  PLAYER_DOWNED" : "Смерть или нок игрока  /  PLAYER_DOWNED",

            // Storage tab
            "Storage.Title" => en ? "Your local highlight library" : "Ваша локальная библиотека",
            "Storage.Subtitle" => en ? "Fast-start MP4 · original quality · zero re-encoding" : "MP4 с быстрым стартом · оригинальное качество · без повторного кодирования",
            "Storage.SaveDir" => en ? "Save folder" : "Папка сохранения",
            "Storage.Browse" => en ? "Browse…" : "Обзор…",
            "Storage.DirHint" => en ? "Only MP4 files are placed in this folder. Thumbnails and metadata are kept in the app data directory." : "В этой папке только видеофайлы MP4. Метаданные и превью клипов хранятся в папке данных Studio (HuntReplayStudio\\library), в режиме --portable — рядом с EXE.",
            "Storage.Template" => en ? "File name template" : "Шаблон имени файла",
            "Storage.TemplateHint" => en ? "Allowed tags: {event}, {date}, {time}. Example: Hunt_MULTI_KILL_x2_2026-10-08_21-14-08-123.mp4" : "Разрешены {event}, {date}, {time}. Например: Hunt_MULTI_KILL_x2_2026-10-08_21-14-08-123.mp4",
            "Storage.AutoCleanup" => en ? "Automatically delete old clips when disk limit is reached" : "Автоматически удалять старые клипы при достижении лимита",
            "Storage.DiskLimit" => en ? "Library storage limit" : "Лимит библиотеки",
            "Storage.CleanupHint" => en ? "Cleanup only removes clips created by Studio. Foreign files and active clips are untouched." : "Очистка удаляет только клипы, которые записала Studio. Чужие файлы в папке, файл, открытый в плеере, и только что сохранённый клип не затрагиваются. Безлимитный режим ничего не удаляет.",

            // System tab
            "System.Title" => en ? "Let Studio run quietly in the background" : "Пусть Studio работает в фоне",
            "System.Subtitle" => en ? "Administrator rights are only needed by MessageBus installer, never for capture or gameplay." : "Администратор нужен только установщику MessageBus, а не записи или игре.",
            "System.HotkeySection" => en ? "HOTKEY & NOTIFICATIONS" : "ГОРЯЧАЯ КЛАВИША И УВЕДОМЛЕНИЯ",
            "System.ManualHotkey" => en ? "Manual clip save hotkey" : "Ручное сохранение клипа",
            "System.SoundOnSave" => en ? "Short sound on successful clip save" : "Короткий звук после успешного сохранения",
            "System.AutomationSection" => en ? "AUTOMATION" : "АВТОМАТИЗАЦИЯ",
            "System.StartWithWindows" => en ? "Start with Windows · minimized to tray" : "Запускать с Windows · свёрнутым в трей",
            "System.AutoBuffer" => en ? "Start buffer with game and stop on exit" : "Запускать буфер вместе с игрой и останавливать при выходе",
            "System.CloseToTray" => en ? "Closing window minimizes application to system tray" : "Закрытие окна сворачивает приложение в трей",
            "System.TrayHint" => en ? "Manual stop pauses automation until next game launch. To exit completely, use the tray icon menu." : "Ручная остановка приостанавливает автоматику до следующего запуска игры. Для полного выхода используйте меню в трее.",
            "System.DiagnosticsCardTitle" => en ? "Something not recording?" : "Что-то не записывается?",
            "System.DiagnosticsCardDesc" => en ? "Checks DLL, registry keys, pipe status, and encoder support." : "Проверка DLL, реестра, пайпа и выбранного кодека.",
            "System.CheckSystemButton" => en ? "Check System" : "Проверить систему",
            "System.UpdatesCardTitle" => en ? "Application Updates" : "Обновления приложения",
            "System.CheckUpdatesButton" => en ? "Check for Updates" : "Проверить обновления",

            // Diagnostics View
            "Diag.Title" => en ? "Transparency instead of promises" : "Прозрачность вместо обещаний",
            "Diag.Subtitle" => en ? "All checks run locally. No telemetry, no startup DLL injection, no silent fallback to CPU." : "Проверка работает локально. Никакой телеметрии, загрузки DLL при запуске или скрытого перехода на CPU.",
            "Diag.CheckReadiness" => en ? "Check system readiness" : "Проверить готовность системы",
            "Diag.SaveReport" => en ? "Save report" : "Сохранить отчёт",
            "Diag.OpenLogs" => en ? "Open log folder" : "Открыть журнал",
            "Diag.NvidiaRegistration" => en ? "NVIDIA Highlights Registration" : "Регистрация NVIDIA Highlights",
            "Diag.NvidiaDesc" => en ? "Installation validates MessageBus.dll signature and backs up previous registry keys. Recovery never touches foreign keys or saved clips." : "Установка проверяет подпись MessageBus.dll и сохраняет предыдущие значения реестра. Восстановление не удаляет чужие ветки NVIDIA или ваши клипы.",
            "Diag.InstallShim" => en ? "Install / Fix  ↗ UAC" : "Установить / исправить  ↗ UAC",
            "Diag.RestoreRegistry" => en ? "Restore registry  ↗ UAC" : "Восстановить реестр  ↗ UAC",

            // Setup Wizard
            "Wizard.Title" => en ? "Welcome to HuntReplay Studio" : "Добро пожаловать в HuntReplay Studio",
            "Wizard.Eyebrow" => en ? "HUNTREPLAY  /  FIRST RUN" : "HUNTREPLAY  /  FIRST RUN",
            "Wizard.Heading" => en ? "Your hunt. Already recorded." : "Ваша охота. Уже в записи.",
            "Wizard.Subtitle" => en ? "Initialize NVIDIA Highlights for Hunt: Showdown and other compatible games — on AMD, Intel, and NVIDIA." : "Инициализация NVIDIA Highlights для Hunt: Showdown и других совместимых игр — на AMD, Intel и NVIDIA.",
            "Wizard.Step1Title" => en ? "01   Signed MessageBus.dll" : "01   Подписанная MessageBus.dll",
            "Wizard.Step1Desc" => en ? "Embedded original NVIDIA → C:\\NvidiaShim. Installer validates SHA-256 and Windows digital signature." : "Встроенный оригинал NVIDIA → C:\\NvidiaShim. Установщик сверит SHA-256 и цифровую подпись Windows.",
            "Wizard.Step2Title" => en ? "02   Registration and backup" : "02   Регистрация и резервная копия",
            "Wizard.Step2Desc" => en ? "UAC is requested only for the DLL and 4 HKLM values. The recorder itself runs entirely without administrator rights." : "UAC запрашивается только для DLL и четырёх значений HKLM в двух представлениях. Сам рекордер работает без администратора.",
            "Wizard.ReplaceCheck" => en ? "Allow replacing existing NVIDIA registration (with backup)" : "Разрешить заменить существующую регистрацию NVIDIA (с резервной копией)",
            "Wizard.Later" => en ? "Configure later" : "Настроить позже",
            "Wizard.Install" => en ? "Install and continue  ↗" : "Установить и продолжить  ↗",

            // Tray Menu
            "Tray.Tooltip" => en ? "HuntReplay Studio · autonomous highlights" : "HuntReplay Studio · автономные хайлайты",
            "Tray.Open" => en ? "Open HuntReplay Studio" : "Открыть HuntReplay Studio",
            "Tray.Save" => en ? "Save highlight" : "Сохранить повтор",
            "Tray.Toggle" => en ? "Start / stop buffer" : "Запустить / остановить буфер",
            "Tray.Exit" => en ? "Exit" : "Выход",

            // Defaults & choices
            "Choice.MonitorDefault" => en ? "Primary monitor · automatic" : "Основной монитор · автоматически",
            "Choice.OutputDefault" => en ? "Windows default audio output" : "Устройство вывода Windows по умолчанию",
            "Choice.MicDefault" => en ? "Windows default microphone" : "Микрофон Windows по умолчанию",
            "Choice.GpuDefault" => en ? "Auto" : "Авто",
            "Choice.MonitorUnavailable" => en ? "Unavailable · saved monitor disconnected" : "Недоступно · сохранённый монитор не подключён",
            "Choice.AudioUnavailable" => en ? "Unavailable · saved device disconnected" : "Недоступно · сохранённое устройство не подключено",
            "Choice.GpuUnavailable" => en ? "Unavailable · saved GPU is not available" : "Недоступно · сохранённая видеокарта недоступна",

            _ => key
        };
    }

    private void Notify(string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
