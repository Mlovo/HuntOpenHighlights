using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HuntReplay.App.Interop;
using HuntReplay.App.Services;
using HuntReplay.App.Views;
using HuntReplay.Core.Configuration;
using HuntReplay.Core.Localization;
using HuntReplay.Core.Media;
using HuntReplay.Core.Storage;
using HuntReplay.Core.Support;
using HuntReplay.Core.Updates;
using Microsoft.Win32;

namespace HuntReplay.App.ViewModels;

internal sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly SettingsStore store;
    private readonly RecorderService recorder;
    private readonly HighlightsPipeServer pipe;
    private readonly StudioLog log;
    private readonly bool portable;
    private readonly string libraryRoot;
    // The store App chose for this run (portable or %LocalAppData%); views must not recompute it.
    public string LibraryRoot => libraryRoot;
    // Single source of truth: <Version> in HuntReplay.App.csproj.
    public string AppVersion { get; } = UpdateCheckerService.GetCurrentVersionString();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly OverlayService overlay;
    private Task? watcher;
    private HotkeyService? hotkey;
    private bool userPaused, manuallyStarted;
    private IntPtr lastGameWindow;
    // Refreshed by the background game probe; reading it never enumerates processes on the UI thread.
    private volatile bool nvidiaActive;
    private int retryCount;
    private long nextRetry;
    private StudioSettings settingsEdit;
    private string page = "Dashboard";
    private string? customMessage, customDiagnostics;
    private RecorderStatus status = new(false, default, default, "", 0, 0);
    private bool busy, setupReady;
    private bool hasUpdate, isCheckingUpdate;
    private string? latestVersion;
    private ReleaseInfo? release;
    // Set while an update downloads; the header button cancels it.
    private CancellationTokenSource? updateDownload;
    private double updateProgress;
    private string search = "";
    private ClipViewModel? selectedClip;
    public IReadOnlyList<DeviceOption> Devices { get; private set; } = [];
    public DeviceSelectionViewModel MonitorSelection { get; }
    public DeviceSelectionViewModel OutputSelection { get; }
    public DeviceSelectionViewModel MicrophoneSelection { get; }
    public DeviceSelectionViewModel GpuSelection { get; }
    public ObservableCollection<LogEntry> Events { get; } = [];
    public ObservableCollection<ClipViewModel> Clips { get; } = [];
    public ICollectionView FilteredClips { get; }
    public IEnumerable<ClipViewModel> RecentClips => Clips.Take(3);
    public bool HasClips => Clips.Count > 0;
    public int ClipCount => Clips.Count;
    public LocalizationService Loc => LocalizationService.Instance;
    public ThemeService ThemeSvc => ThemeService.Instance;
    public string LanguageButtonLabel => Loc["Header.LanguageToggle"];
    public string ThemeButtonLabel => Loc[ThemeSvc.IsDark ? "Header.ThemeDark" : "Header.ThemeLight"];
    public string Page
    {
        get => page;
        set
        {
            if (page == value) return;
            if (page == "Settings" && HasUnsavedSettings)
            {
                // This setter runs inside the navigation button's click and binding update. The dialog is shown once
                // that input has been processed, so no click is swallowed and the binding is not re-entered.
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = HandleUnsavedAndNavigateAsync(value)));
                return;
            }
            if (SetProperty(ref page, value)) OnPropertyChanged(nameof(PageTitle));
        }
    }

    public async Task<bool> ConfirmLeaveSettingsAsync()
    {
        if (!HasUnsavedSettings) return true;
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            Loc["Settings.UnsavedChangesPrompt"],
            Loc["Settings.UnsavedChangesTitle"],
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            return await SaveSettingsDirectAsync();
        }
        if (result == MessageBoxResult.No)
        {
            RevertSettings();
            return true;
        }
        return false;
    }

    private async Task HandleUnsavedAndNavigateAsync(string targetPage)
    {
        Mouse.Capture(null);
        OnPropertyChanged(nameof(Page)); // Keep the Settings button selected while the question is open.
        if (await ConfirmLeaveSettingsAsync())
        {
            // Outside the Page setter the caller name would be this method's: name the property explicitly, or the
            // page content (bound to Page) never switches while the title (notified below) does.
            if (SetProperty(ref page, targetPage, nameof(Page))) OnPropertyChanged(nameof(PageTitle));
        }
        else
        {
            // Re-trigger notification to ensure radio button stays on Settings
            OnPropertyChanged(nameof(Page));
        }
    }

    public string PageTitle => Page switch
    {
        "Gallery" => Loc["PageTitle.Gallery"],
        "Settings" => Loc["PageTitle.Settings"],
        "Diagnostics" => Loc["PageTitle.Diagnostics"],
        _ => Loc["PageTitle.Dashboard"]
    };
    public string Message
    {
        get => customMessage ?? Loc["Footer.DefaultMessage"];
        private set { if (SetProperty(ref customMessage, value)) OnPropertyChanged(); }
    }
    public string Diagnostics
    {
        get => customDiagnostics ?? Loc["Footer.DiagnosticsDefault"];
        private set { if (SetProperty(ref customDiagnostics, value)) OnPropertyChanged(); }
    }
    public string SelectedEventLabel => SelectedClip?.EventLabel ?? Loc["Gallery.NoClipSelected"];
    public bool Busy { get => busy; private set { if (SetProperty(ref busy, value)) NotifyCommands(ToggleBufferCommand, SaveCommand, ApplySettingsCommand, DiagnoseCommand, InstallCommand, UninstallCommand); } }
    public StudioSettings SettingsEdit
    {
        get => settingsEdit;
        private set { if (SetProperty(ref settingsEdit, value)) RefreshDeviceSelections(); }
    }
    public string Search { get => search; set { if (SetProperty(ref search, value)) FilteredClips.Refresh(); } }
    public ClipViewModel? SelectedClip
    {
        get => selectedClip;
        set { if (SetProperty(ref selectedClip, value)) { recorder.ProtectPlayingClip(value?.Clip.Metadata.Id); OnPropertyChanged(nameof(SelectedEventLabel)); NotifyCommands(DeleteCommand, RenameCommand, RevealCommand, OpenInPlayerCommand); } }
    }
    public bool IsBufferActive => status.Running && status.Buffer.Seconds > 0;
    public bool IsGameConnected => pipe.Games.Count > 0;
    public string GameStatus => IsGameConnected ? string.Format(Loc["Dash.GameConnected"], string.Join(", ", pipe.Games.Select(g => g.Game).Distinct())) :
        lastGameWindow != IntPtr.Zero ? Loc["Dash.GameWindowFound"] : Loc["Dash.WaitingForGame"];
    public string BufferStatus => status.Running ? (IsBufferActive ? Loc["Dash.BufferWriting"] : Loc["Dash.BufferWarming"]) : Loc["Dash.BufferStopped"];
    public string BufferTime => TimeSpan.FromSeconds(Math.Max(0, status.Buffer.Seconds)).ToString(@"mm\:ss");
    public string BufferTarget => $"/ {TimeSpan.FromSeconds(recorder.Settings.Replay.BufferSeconds):mm\\:ss}";
    public double BufferFill => Math.Clamp(status.Buffer.Seconds / recorder.Settings.Replay.BufferSeconds * 100, 0, 100);
    public string MemoryText => $"{status.Buffer.Bytes / 1024d / 1024:F1} {Loc["Dash.RamSuffix"]}";
    public string GpuStatus => status.Running ? status.Encoder :
        string.Format(Loc["Dash.GpuWaiting"],
            DeviceOption.ResolveGpu(recorder.Settings.Video.GpuDevice, Devices)?.Name
            ?? Devices.FirstOrDefault(d => d.Kind == 3)?.Name
            ?? Loc["Dash.GpuUnknown"]);
    public string EncodingModeLabel => recorder.Settings.Video.Encoder == VideoEncoder.X264 ? Loc["Dash.CpuExplicit"] : Loc["Dash.GpuEncoding"];
    public string VideoStatus => status.Running ? $"{status.Native.Width} × {status.Native.Height} · {recorder.Settings.Video.Fps} FPS · {recorder.Settings.Video.Codec}" :
        Loc["Dash.WgcD3D"];
    public string CaptureDetail => status.Buffer.MemoryPressure ? Loc["Dash.MemoryPressure"] :
        status.Native.SourceStale != 0 ? Loc["Dash.SourceStale"] :
        status.Running ? string.Format(Loc["Dash.FramesDetail"], status.Native.Frames, status.Native.DroppedFrames) : "";
    public string PendingText => status.Pending > 0 ? string.Format(Loc["Dash.PendingEpisodes"], status.Pending) :
        status.Exports > 0 ? string.Format(Loc["Dash.SavingMp4"], status.Exports) : Loc["Dash.ReadyForNext"];
    public string ToggleLabel => status.Running ? Loc["Dash.StopBuffer"] : Loc["Dash.StartBuffer"];
    public string ManualLabel => string.Format(Loc["Dash.SaveManual"], recorder.Settings.Replay.BufferSeconds);
    public string HotkeyLabel => "F" + (recorder.Settings.System.HotkeyVirtualKey - 0x6F);
    public string SetupLabel => setupReady ? Loc["Header.SetupReady"] : Loc["Header.SetupNeeded"];
    public string PipeLabel => pipe.IsListening ? "IPC • ONLINE" : nvidiaActive ? Loc["Header.PipeNvidiaActive"] : "IPC • OFFLINE";
    public string LibrarySummary => string.Format(Loc["Gallery.Summary"], Clips.Count, Clips.Sum(c => c.Clip.Metadata.Bytes) / 1_000_000_000d);
    public bool CloseToTray => recorder.Settings.System.CloseToTray;
    public bool HasUpdate => hasUpdate;
    public bool IsCheckingUpdate => isCheckingUpdate;
    public string? LatestVersion => latestVersion;
    public string UpdateButtonLabel => updateDownload is not null ? Loc.Format("Header.UpdateDownloading", (int)(updateProgress * 100)) :
        string.IsNullOrWhiteSpace(latestVersion) ? Loc["Header.UpdateAvailable"] : string.Format(Loc["Header.UpdateAvailableVersion"], latestVersion);
    public string UpdateButtonTooltip => updateDownload is not null ? Loc["Header.UpdateCancelTooltip"] : Loc["Header.UpdateTooltip"];
    public string UpdateStatusLabel => isCheckingUpdate ? Loc["Update.Checking"] :
        hasUpdate ? Loc.Format("Update.VersionAvailable", latestVersion) : Loc["Update.UpToDate"];
    public IRelayCommand ToggleBufferCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand ApplySettingsCommand { get; }
    public IRelayCommand BrowseCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand RenameCommand { get; }
    public IRelayCommand RevealCommand { get; }
    public IRelayCommand OpenInPlayerCommand { get; }
    public IRelayCommand DiagnoseCommand { get; }
    public IRelayCommand SaveReportCommand { get; }
    public IRelayCommand InstallCommand { get; }
    public IRelayCommand UninstallCommand { get; }
    public IRelayCommand OpenLogsCommand { get; }
    public IRelayCommand SelectRecentCommand { get; }
    public IRelayCommand CheckUpdateCommand { get; }
    public IRelayCommand InstallUpdateCommand { get; }
    public IRelayCommand ToggleLanguageCommand { get; }
    public IRelayCommand ToggleThemeCommand { get; }
    public IRelayCommand PreviewOverlayCommand { get; }
    public IRelayCommand SupportCommand { get; }
    public IReadOnlyList<SupportChannel> SupportOptions => SupportChannels.Available;
    public event Action? ClosePlayer;

    private readonly Choice<CaptureSource>[] sources = [
        new(CaptureSource.HuntWindow, ""),
        new(CaptureSource.Monitor, "")
    ];
    private readonly Choice<VideoResolution>[] resolutions =
        [new(VideoResolution.Native, ""), .. VideoResolutions.Presets.Select(r => new Choice<VideoResolution>(r, ""))];
    private readonly Choice<VideoEncoder>[] encoders = [
        new(VideoEncoder.Auto, ""),
        new(VideoEncoder.Amf, "AMD AMF"),
        new(VideoEncoder.Nvenc, "NVIDIA NVENC"),
        new(VideoEncoder.Qsv, "Intel QSV"),
        new(VideoEncoder.X264, "")
    ];
    private readonly Choice<VideoCodec>[] codecs = [
        new(VideoCodec.H264, ""),
        new(VideoCodec.Hevc, ""),
        new(VideoCodec.Av1, "")
    ];
    private readonly Choice<AudioMode>[] audioModes = [
        new(AudioMode.Mix, ""),
        new(AudioMode.SeparateTracks, "")
    ];
    private readonly Choice<int>[] diskLimits = [
        new(15, ""),
        new(30, ""),
        new(60, ""),
        new(0, "")
    ];
    private readonly Choice<OverlayCorner>[] overlayCorners = [
        new(OverlayCorner.TopLeft, ""),
        new(OverlayCorner.TopRight, ""),
        new(OverlayCorner.BottomLeft, ""),
        new(OverlayCorner.BottomRight, "")
    ];
    private readonly Choice<int>[] overlayDurations = [new(2, ""), new(3, ""), new(5, "")];

    public IReadOnlyList<Choice<CaptureSource>> Sources => sources;
    public IReadOnlyList<Choice<OverlayCorner>> OverlayCorners => overlayCorners;
    public IReadOnlyList<Choice<int>> OverlayDurations => overlayDurations;
    public IReadOnlyList<Choice<VideoResolution>> Resolutions => resolutions;
    public IReadOnlyList<Choice<VideoEncoder>> Encoders => encoders;
    public IReadOnlyList<Choice<VideoCodec>> Codecs => codecs;
    public IReadOnlyList<int> FrameRates { get; } = [60, 120, 30];
    public IReadOnlyList<int> GopLengths { get; } = [1, 2];
    public IReadOnlyList<Choice<AudioMode>> AudioModes => audioModes;
    public IReadOnlyList<Choice<int>> DiskLimits => diskLimits;
    public IReadOnlyList<Choice<int>> Hotkeys { get; } = Enumerable.Range(0x70, 12).Select(k => new Choice<int>(k, "F" + (k - 0x6F))).ToArray();

    private void RebuildChoices()
    {
        sources[0].Label = Loc["Choice.SourceHuntWindow"];
        sources[1].Label = Loc["Choice.SourceMonitor"];

        RefreshResolutionChoices();

        encoders[0].Label = Loc["Choice.EncoderAuto"];
        encoders[4].Label = Loc["Choice.EncoderX264"];

        codecs[0].Label = Loc["Choice.CodecH264"];
        codecs[1].Label = Loc["Choice.CodecHevc"];
        codecs[2].Label = Loc["Choice.CodecAv1"];

        audioModes[0].Label = Loc["Choice.AudioMixed"];
        audioModes[1].Label = Loc["Choice.AudioSeparate"];

        diskLimits[0].Label = Loc.Format("Choice.DiskLimitGb", 15);
        diskLimits[1].Label = Loc.Format("Choice.DiskLimitGb", 30);
        diskLimits[2].Label = Loc.Format("Choice.DiskLimitGb", 60);
        diskLimits[3].Label = Loc["Choice.DiskUnlimited"];

        overlayCorners[0].Label = Loc["Choice.CornerTopLeft"];
        overlayCorners[1].Label = Loc["Choice.CornerTopRight"];
        overlayCorners[2].Label = Loc["Choice.CornerBottomLeft"];
        overlayCorners[3].Label = Loc["Choice.CornerBottomRight"];
        foreach (var duration in overlayDurations) duration.Label = Loc.Format("Choice.Seconds", duration.Value);
    }

    public MainViewModel(SettingsStore store, RecorderService recorder, HighlightsPipeServer pipe, StudioLog log, bool portable, string libraryRoot)
    {
        dispatcher = Dispatcher.CurrentDispatcher;
        this.store = store; this.recorder = recorder; this.pipe = pipe; this.log = log; this.portable = portable; this.libraryRoot = libraryRoot;
        overlay = new(log);
        Loc.SetLanguage(recorder.Settings.System.Language);
        settingsEdit = recorder.Settings.Clone();
        MonitorSelection = new(0,
            () => Loc["Choice.MonitorDefault"],
            () => Loc["Choice.MonitorUnavailable"],
            () => SettingsEdit.Video.MonitorDevice, value => SettingsEdit.Video.MonitorDevice = value);
        OutputSelection = new(1,
            () => Loc["Choice.OutputDefault"],
            () => Loc["Choice.AudioUnavailable"],
            () => SettingsEdit.Audio.OutputDeviceId, value => SettingsEdit.Audio.OutputDeviceId = value);
        MicrophoneSelection = new(2,
            () => Loc["Choice.MicDefault"],
            () => Loc["Choice.AudioUnavailable"],
            () => SettingsEdit.Audio.MicrophoneDeviceId, value => SettingsEdit.Audio.MicrophoneDeviceId = value);
        GpuSelection = new(3,
            () => Loc["Choice.GpuDefault"],
            () => Loc["Choice.GpuUnavailable"],
            () => SettingsEdit.Video.GpuDevice, value => SettingsEdit.Video.GpuDevice = value);
        FilteredClips = CollectionViewSource.GetDefaultView(Clips);
        FilteredClips.Filter = value => value is ClipViewModel c &&
            (string.IsNullOrWhiteSpace(Search) || c.FileName.Contains(Search, StringComparison.OrdinalIgnoreCase) || c.EventLabel.Contains(Search, StringComparison.OrdinalIgnoreCase));
        ToggleBufferCommand = Async(ToggleAsync, () => !Busy);
        SaveCommand = Async(recorder.SaveManualAsync, () => IsBufferActive && !Busy);
        ApplySettingsCommand = Async(ApplyAsync, () => !Busy);
        BrowseCommand = new RelayCommand(() => Safe(Browse));
        OpenFolderCommand = new RelayCommand(() => Safe(() => DesktopIntegration.OpenFolder(recorder.Settings.Storage.Directory)));
        RefreshCommand = Async(RefreshClipsAsync);
        DeleteCommand = new RelayCommand(() => Safe(DeleteClip), () => SelectedClip is not null);
        RenameCommand = new RelayCommand(() => Safe(RenameClip), () => SelectedClip is not null);
        RevealCommand = new RelayCommand(() => Safe(() => DesktopIntegration.RevealFile(SelectedClip!.Clip.FullPath)), () => SelectedClip is not null);
        OpenInPlayerCommand = new RelayCommand(() => Safe(() => DesktopIntegration.OpenWithDefaultPlayer(SelectedClip!.Clip.FullPath)), () => SelectedClip is not null);
        DiagnoseCommand = Async(DiagnoseAsync, () => !Busy);
        SaveReportCommand = new RelayCommand(() => Safe(SaveReport));
        InstallCommand = Async(InstallAsync, () => !Busy);
        UninstallCommand = Async(UninstallAsync, () => !Busy);
        OpenLogsCommand = new RelayCommand(() => Safe(() => DesktopIntegration.OpenFolder(log.DirectoryPath)));
        SelectRecentCommand = new RelayCommand<object?>(value => { if (value is ClipViewModel clip) { Page = "Gallery"; SelectedClip = clip; } });
        CheckUpdateCommand = Async(CheckUpdateManualAsync);
        InstallUpdateCommand = new RelayCommand(() => { if (updateDownload is { } download) download.Cancel(); else _ = InstallUpdateAsync(); });
        ToggleLanguageCommand = new RelayCommand(() => Safe(ToggleLanguage));
        SupportCommand = new RelayCommand<object?>(value => Safe(() => UseSupportChannel(value)));
        ThemeSvc.SetTheme(recorder.Settings.System.Theme);
        ToggleThemeCommand = new RelayCommand(() => Safe(ToggleTheme));
        // Uses the unsaved position and duration, so they can be tried before saving.
        PreviewOverlayCommand = new RelayCommand(() => Safe(() =>
            overlay.Notify(SettingsEdit.System, OverlayKind.Info, Loc["Overlay.PreviewTitle"], Loc["Overlay.PreviewDetail"], preview: true)));
        RebuildChoices();
        log.Entry += OnLog;
        recorder.Changed += OnChanged;
        pipe.StateChanged += OnChanged;
        recorder.ClipSaved += OnSaved;
    }

    public async Task InitializeAsync(Window window)
    {
        await operation.WaitAsync(); Busy = true;
        try
        {
            hotkey = new HotkeyService(window, () => SaveCommand.Execute(null));
            try { hotkey.Register(recorder.Settings.System.HotkeyVirtualKey); } catch (Exception ex) { Error(ex); }
            try { DesktopIntegration.ConfigureStartup(recorder.Settings.System.StartWithWindows, portable); } catch (Exception ex) { Error(ex); }
            await ReloadDevicesAsync();
            try { setupReady = (await Task.Run(SelfInstaller.Check)).Ready; } catch (Exception ex) { Error(ex); }
            await RefreshClipsAsync();
            RefreshStatus();
            if (!stop.IsCancellationRequested) watcher = Task.Run(WatchGameAsync);
            _ = Task.Run(() => CheckUpdateSilentAsync(stop.Token));
        }
        finally { Busy = false; operation.Release(); }
    }

    private async Task ReloadDevicesAsync()
    {
        try { Devices = await Task.Run(NativeCaptureEngine.EnumerateDevices); }
        catch (Exception ex) { Error(ex); Devices = []; }
        RefreshDeviceSelections();
        OnPropertyChanged(nameof(GpuStatus));
    }

    private void RefreshDeviceSelections()
    {
        MonitorSelection.Refresh(Devices);
        OutputSelection.Refresh(Devices);
        MicrophoneSelection.Refresh(Devices);
        GpuSelection.Refresh(Devices);
        RefreshResolutionChoices();
    }

    /// <summary>
    /// Labels each preset with the size it records from the monitor the capture uses, and hides presets that would
    /// scale that monitor up. The saved preset always stays visible. Window capture is sized from the window itself;
    /// the primary monitor stands in for it here.
    /// </summary>
    public void RefreshResolutionChoices()
    {
        var video = SettingsEdit.Video;
        var monitor = (video.Source == CaptureSource.Monitor && video.MonitorDevice.Length > 0
                ? Devices.FirstOrDefault(d => d.Kind == 0 && d.Id == video.MonitorDevice)
                : null)
            ?? Devices.FirstOrDefault(d => d.Kind == 0 && d.IsDefault);
        int width = monitor?.Width ?? 0, height = monitor?.Height ?? 0;
        foreach (var choice in resolutions)
        {
            if (choice.Value == VideoResolution.Native)
            {
                choice.Label = height > 0 ? $"{Loc["Choice.ResolutionNative"]} · {width} × {height}" : Loc["Choice.ResolutionNative"];
                continue;
            }
            int presetHeight = choice.Value.Height();
            if (height > 0)
            {
                var (outputWidth, outputHeight) = choice.Value.OutputSize(width, height);
                choice.Label = $"{presetHeight}p · {outputWidth} × {outputHeight}";
            }
            else choice.Label = $"{presetHeight}p";
            choice.IsAvailable = height == 0 || presetHeight < height || choice.Value == video.Resolution;
        }
    }

    private async Task ToggleAsync()
    {
        await operation.WaitAsync(); Busy = true;
        try
        {
            if (recorder.Status.Running)
            {
                userPaused = true; manuallyStarted = false;
                await recorder.StopAsync();
            }
            else
            {
                userPaused = false; manuallyStarted = true; retryCount = 0;
                await recorder.StopAsync();
                await StartCurrentAsync();
            }
        }
        finally { Busy = false; operation.Release(); RefreshStatus(); }
    }
    private async Task StartCurrentAsync()
    {
        var pid = pipe.Games.FirstOrDefault().Pid;
        var window = GameWindow.Find((int)pid);
        var devices = await Task.Run(NativeCaptureEngine.EnumerateDevices);
        await recorder.StartAsync(window, devices);
    }

    private async Task WatchGameAsync()
    {
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1.5));
        try
        {
            while (await tick.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                // The pipe can be busy at startup (NVIDIA) or its listen loop can fail later; claim it once it is free.
                if (!pipe.IsListening) pipe.TryStart(logFailure: false);
                // Process snapshots enumerate every process on the machine; probe on the thread pool, not the UI thread.
                int probePid = (int)pipe.Games.FirstOrDefault().Pid;
                var processes = ProcessSnapshot.Take();
                var probedWindow = GameWindow.Find(processes, probePid);
                bool probedRunning = probedWindow != IntPtr.Zero || pipe.Games.Count > 0 || GameWindow.HasGameProcess(processes, probePid);
                nvidiaActive = !pipe.IsListening && processes.Has("nvcontainer");
                // All automation state and user operations are serialized on the UI dispatcher.
                await dispatcher.InvokeAsync(async () =>
                {
                    if (stop.IsCancellationRequested || Busy || !await operation.WaitAsync(0)) return;
                    try
                    {
                        var window = probedWindow;
                        bool gameRunning = probedRunning;

                        if (window != lastGameWindow) { userPaused = false; retryCount = 0; nextRetry = 0; }
                        var previous = lastGameWindow; lastGameWindow = window;
                        // One status and settings read per pass; StopAsync below is the only thing that changes them here.
                        var settings = recorder.Settings;
                        var current = recorder.Status;
                        bool running = current.Running;
                        if (!running && current.Native.ElapsedUs > 0)
                        {
                            await recorder.StopAsync();
                            if (gameRunning || settings.Video.Source == CaptureSource.Monitor) retryCount++;
                            nextRetry = Environment.TickCount64 + 5000;
                        }

                        // Game window capture stops when the window disappears (auto mode) or is replaced.
                        // Monitor capture keeps running for as long as the game process runs.
                        bool autoStop = settings.System.AutoBufferWithGame && !manuallyStarted;
                        bool stopForSource = running && (settings.Video.Source == CaptureSource.HuntWindow
                            ? previous != IntPtr.Zero && (window == IntPtr.Zero ? autoStop : previous != window)
                            : autoStop && !gameRunning);
                        if (stopForSource)
                        {
                            await recorder.StopAsync();
                            running = false;
                        }

                        bool sourceAvailable = settings.Video.Source == CaptureSource.Monitor || window != IntPtr.Zero;
                        bool wanted = manuallyStarted || (settings.System.AutoBufferWithGame && gameRunning);
                        if (wanted && sourceAvailable && !userPaused && !running && retryCount < 3 && Environment.TickCount64 >= nextRetry)
                        {
                            Busy = true;
                            try { await StartCurrentAsync(); running = recorder.Status.Running; }
                            catch (Exception ex) { retryCount++; nextRetry = Environment.TickCount64 + retryCount * 15000; Error(ex); }
                            finally { Busy = false; }
                        }
                        if (retryCount >= 3 && wanted && !running)
                            Message = Loc["Status.AutoRetryStopped"];
                        RefreshStatus();
                    }
                    catch (Exception ex) { Error(ex); }
                    finally { operation.Release(); }
                }).Task.Unwrap().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public bool HasUnsavedSettings => !SettingsEdit.IsEquivalent(recorder.Settings);

    public void RevertSettings()
    {
        SettingsEdit = recorder.Settings.Clone();
    }

    public async Task<bool> SaveSettingsDirectAsync()
    {
        try
        {
            await ApplyAsync();
            return true;
        }
        catch (Exception ex)
        {
            Error(ex);
            return false;
        }
    }

    private async Task ApplyAsync()
    {
        if (Application.Current.MainWindow is { } root && HasFormErrors(root, new HashSet<DependencyObject>()))
            throw new InvalidDataException(Loc["Settings.InvalidFields"]);
        var updated = SettingsEdit.Clone(); updated.Validate();
        await operation.WaitAsync(); Busy = true;
        var old = recorder.Settings.Clone();
        var wasRunning = recorder.Status.Running;
        bool committed = false;
        try
        {
            hotkey?.Register(updated.System.HotkeyVirtualKey);
            DesktopIntegration.ConfigureStartup(updated.System.StartWithWindows, portable);
            store.Save(updated);
            await recorder.ApplySettingsAsync(updated);
            committed = true;
            SettingsEdit = updated.Clone();
            await ReloadDevicesAsync();
            await RefreshClipsAsync();
            Message = Loc["Settings.Saved"];
            retryCount = 0; nextRetry = 0; userPaused = false;
            int pid = (int)pipe.Games.FirstOrDefault().Pid;
            var processes = ProcessSnapshot.Take();
            bool gameActive = GameWindow.Find(processes, pid) != IntPtr.Zero || pipe.Games.Count > 0 || GameWindow.HasGameProcess(processes, pid);
            bool shouldRun = wasRunning || (updated.System.AutoBufferWithGame && gameActive);
            if (shouldRun)
            {
                await StartCurrentAsync();
            }
            log.Write(Loc[shouldRun ? "Log.SettingsAppliedRestarted" : "Log.SettingsAppliedIdle"]);
        }
        catch
        {
            // If persistence failed, preserve the previous functional shortcut/startup entry.
            if (!committed)
            {
                hotkey?.Register(old.System.HotkeyVirtualKey);
                DesktopIntegration.ConfigureStartup(old.System.StartWithWindows, portable);
            }
            throw;
        }
        finally { Busy = false; operation.Release(); RefreshStatus(); }
    }

    private static bool HasFormErrors(DependencyObject node, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node)) return false;
        if (System.Windows.Controls.Validation.GetHasError(node)) return true;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            if (HasFormErrors(child, visited)) return true;
        if (node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D)
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); ++i)
                if (HasFormErrors(System.Windows.Media.VisualTreeHelper.GetChild(node, i), visited)) return true;
        return false;
    }

    private ClipLibrary Library() => new(recorder.Settings.Storage.Directory, libraryRoot);

    public async Task RefreshClipsAsync()
    {
        var directory = recorder.Settings.Storage.Directory;
        var selectedId = SelectedClip?.Clip.Metadata.Id;
        var result = await Task.Run(() =>
        {
            var library = new ClipLibrary(directory, libraryRoot);
            try
            {
                // Moves 0.1.2 sidecars out of the clips folder and tidies records of deleted MP4 files.
                var report = library.Maintain();
                if (report.Adopted + report.Relocated + report.Pruned > 0)
                    log.Write(Loc.Format("Log.LibraryMaintained", report.Adopted, report.Relocated, report.Pruned));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { log.Write(Loc.Format("Log.LibraryMaintenanceSkipped", ex.Message), "WARN"); }
            return library.List().Take(500).Select(c => new ClipViewModel(c)).ToArray();
        });
        Clips.Clear(); foreach (var clip in result) Clips.Add(clip);
        SelectedClip = Clips.FirstOrDefault(c => c.Clip.Metadata.Id == selectedId);
        OnPropertyChanged(nameof(RecentClips)); OnPropertyChanged(nameof(HasClips)); OnPropertyChanged(nameof(ClipCount)); OnPropertyChanged(nameof(LibrarySummary));
    }
    private void Browse()
    {
        var dialog = new OpenFolderDialog { Title = Loc["Dialog.ClipsFolder"], InitialDirectory = SettingsEdit.Storage.Directory };
        if (dialog.ShowDialog() == true) { SettingsEdit.Storage.Directory = dialog.FolderName; SettingsEdit = SettingsEdit.Clone(); }
    }
    private void DeleteClip()
    {
        if (SelectedClip is not { } clip || MessageBox.Show(
            Loc.Format("Dialog.DeleteClipQuestion", clip.FileName),
            Loc["Dialog.DeleteClipTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        ClosePlayer?.Invoke();
        Library().Delete(clip.Clip);
        Clips.Remove(clip); SelectedClip = null;
        OnPropertyChanged(nameof(RecentClips)); OnPropertyChanged(nameof(HasClips)); OnPropertyChanged(nameof(ClipCount)); OnPropertyChanged(nameof(LibrarySummary));
    }
    private void RenameClip()
    {
        if (SelectedClip is not { } clip) return;
        var dialog = new InputDialog(Loc["Dialog.RenameClip"], clip.FileName) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        ClosePlayer?.Invoke();
        var renamed = Library().Rename(clip.Clip, dialog.Value);
        var index = Clips.IndexOf(clip); var model = new ClipViewModel(renamed);
        Clips[index] = model; SelectedClip = model; OnPropertyChanged(nameof(RecentClips));
    }
    private async Task DiagnoseAsync()
    {
        await operation.WaitAsync(); Busy = true; Page = "Diagnostics";
        try { Diagnostics = Loc["Diag.Testing"];
            Diagnostics = await Task.Run(() => DiagnosticsService.Run(recorder.Settings.Clone(), recorder, pipe)); }
        finally { Busy = false; operation.Release(); }
    }
    private void SaveReport()
    {
        var dialog = new SaveFileDialog { FileName = "HuntReplay-diagnostics.txt", Filter = Loc["Dialog.TextReportFilter"] };
        if (dialog.ShowDialog() == true) File.WriteAllText(dialog.FileName, Diagnostics);
    }
    private async Task InstallAsync()
    {
        await operation.WaitAsync(); Busy = true;
        try
        {
            var status = await Task.Run(SelfInstaller.Check);
            var wizard = new SetupWizard(status) { Owner = Application.Current.MainWindow };
            wizard.ShowDialog();
            setupReady = (await Task.Run(SelfInstaller.Check)).Ready; OnPropertyChanged(nameof(SetupLabel));
        }
        finally { Busy = false; operation.Release(); }
    }
    private async Task UninstallAsync()
    {
        await operation.WaitAsync(); Busy = true;
        try
        {
            if (MessageBox.Show(
                Loc["Dialog.RestoreNvidiaQuestion"],
                Loc["Dialog.RestoreNvidiaTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var code = await SelfInstaller.RunElevatedAsync(uninstall: true);
            if (code != 0) throw new IOException(Loc["Error.RestoreFailed"]);
            setupReady = false; OnPropertyChanged(nameof(SetupLabel));
        }
        finally { Busy = false; operation.Release(); }
    }
    private void OnLog(LogEntry entry) => OnUi(() =>
    {
        Events.Insert(0, entry);
        while (Events.Count > 250) Events.RemoveAt(Events.Count - 1);
        if (entry.Level == "ERROR")
        {
            Message = entry.Message;
            overlay.Notify(recorder.Settings.System, OverlayKind.Problem, Loc["Overlay.Problem"], entry.Message);
        }
    });
    private void OnChanged() => OnUi(RefreshStatus);
    private void OnSaved(LibraryClip clip) => OnUi(async () =>
    {
        overlay.Notify(recorder.Settings.System, OverlayKind.Info, Loc["Overlay.ClipSaved"], clip.Metadata.FileName);
        try { await RefreshClipsAsync(); DesktopIntegration.NotifySaved(clip, recorder.Settings, log); Message = Loc.Format("Status.ClipSaved", clip.Metadata.FileName); }
        catch (Exception ex) { Error(ex); }
    });
    private void OnUi(Action action)
    {
        if (!dispatcher.HasShutdownStarted && !stop.IsCancellationRequested) dispatcher.BeginInvoke(action);
    }
    private static readonly string[] StatusProperties =
    [
        nameof(IsBufferActive), nameof(IsGameConnected), nameof(GameStatus), nameof(BufferStatus), nameof(BufferTime),
        nameof(BufferTarget), nameof(BufferFill), nameof(MemoryText), nameof(GpuStatus), nameof(VideoStatus), nameof(EncodingModeLabel), nameof(CaptureDetail), nameof(PendingText),
        nameof(ToggleLabel), nameof(ManualLabel), nameof(HotkeyLabel), nameof(SetupLabel), nameof(PipeLabel)
    ];
    private void RefreshStatus()
    {
        bool wasActive = IsBufferActive, wasRunning = status.Running;
        status = recorder.Status;
        foreach (var property in StatusProperties) OnPropertyChanged(property);
        if (status.Running != wasRunning)
            overlay.Notify(recorder.Settings.System, OverlayKind.Info,
                Loc[status.Running ? "Overlay.BufferStarted" : "Overlay.BufferStopped"],
                status.Running ? Loc.Format("Overlay.BufferStartedDetail", HotkeyLabel) : "");
        // Only SaveCommand depends on live capture state.
        if (IsBufferActive != wasActive) SaveCommand.NotifyCanExecuteChanged();
    }
    private void UseSupportChannel(object? value)
    {
        if (value is not SupportChannel channel) return;
        if (channel.IsLink) { DesktopIntegration.OpenUrl(channel.Value); return; }
        Clipboard.SetText(channel.Value);
        Message = Loc.Format("Support.Copied", channel.Name);
    }
    public void Error(Exception ex) { Message = ex.Message; log.Write(ex.Message, "ERROR"); }
    // A preview failure is not an export failure: the saved MP4 stays as written, so this is a warning, not an error.
    public void PlaybackFailed(int hresult, string fileName)
    {
        Message = Loc.Format("Status.PlaybackFailed", fileName, PlaybackDiagnostics.Code(hresult));
        log.Write(PlaybackDiagnostics.LogLine(hresult, fileName), "WARN");
    }
    private async Task CheckUpdateSilentAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(2500, token); // Let the app start up and render before network I/O
            var res = await UpdateCheckerService.CheckForUpdatesAsync(token);
            if (token.IsCancellationRequested) return;
            OnUi(() => ApplyUpdateCheck(res));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.Write("Background update check: " + ex.Message, "DEBUG");
        }
    }

    public async Task CheckUpdateManualAsync()
    {
        isCheckingUpdate = true;
        OnPropertyChanged(nameof(IsCheckingUpdate));
        OnPropertyChanged(nameof(UpdateStatusLabel));
        Message = Loc["Update.CheckingGitHub"];
        try
        {
            var res = await Task.Run(() => UpdateCheckerService.CheckForUpdatesAsync(stop.Token));
            if (updateDownload is not null) return; // A check must not swap the release an install is using.
            ApplyUpdateCheck(res);

            if (res.HasUpdate)
            {
                Message = Loc.Format("Update.NewVersion", res.LatestVersion);
            }
            else if (!string.IsNullOrEmpty(res.ErrorMessage))
            {
                Message = Loc.Format("Update.Failed", res.ErrorMessage);
            }
            else
            {
                Message = Loc.Format("Update.Latest", res.CurrentVersion);
            }
        }
        finally
        {
            isCheckingUpdate = false;
            OnPropertyChanged(nameof(IsCheckingUpdate));
            OnPropertyChanged(nameof(UpdateStatusLabel));
        }
    }

    private void ApplyUpdateCheck(UpdateCheckResult res)
    {
        if (updateDownload is not null) return;
        hasUpdate = res.HasUpdate;
        latestVersion = res.LatestVersion;
        release = res.Release;
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(UpdateButtonLabel));
        OnPropertyChanged(nameof(UpdateStatusLabel));
    }

    public void ReportUpdated() => Message = Loc.Format("Update.Installed", AppVersion);

    private async Task InstallUpdateAsync()
    {
        if (release is not { } target) return;
        var owner = Application.Current.MainWindow;
        var prompt = Loc.Format("Update.ConfirmPrompt", target.Version, AppVersion);
        if (status.Running) prompt += "\n\n" + Loc["Update.ConfirmRecording"];
        if (AppUpdate.PlainNotes(target.Notes) is { } notes) prompt += "\n\n" + (notes.Length > 1200 ? notes[..1200].TrimEnd() + "…" : notes.Trim());
        if (MessageBox.Show(owner, prompt, Loc["Update.ConfirmTitle"], MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        while (true)
        {
            try
            {
                await DownloadAndInstallAsync(target);
                return;
            }
            catch (OperationCanceledException)
            {
                Message = Loc["Update.DownloadCancelled"];
                return;
            }
            catch (Exception ex)
            {
                // Every failure is explained, with the choice to try again or to download the release by hand.
                log.Write("Update failed: " + ex, "ERROR");
                Message = Loc.Format("Update.InstallFailed", ex.Message);
                if (stop.IsCancellationRequested) return;
                var dialog = new UpdateFailedDialog(ex.Message) { Owner = owner };
                dialog.ShowDialog();
                if (dialog.Choice == UpdateFailedChoice.Retry) continue;
                if (dialog.Choice == UpdateFailedChoice.OpenReleases) Safe(() => OpenReleasePage(target.PageUrl));
                return;
            }
        }
    }

    private async Task DownloadAndInstallAsync(ReleaseInfo target)
    {
        using var download = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        updateDownload = download;
        SetUpdateProgress(0);
        Message = Loc.Format("Update.Downloading", target.Version);
        try
        {
            // Created on the UI thread, so reports arrive there.
            var progress = new Progress<double>(SetUpdateProgress);
            var staged = await Task.Run(() => UpdateInstallerService.DownloadAsync(target, progress, download.Token));
            download.Token.ThrowIfCancellationRequested();
            Message = Loc["Update.Installing"];
            UpdateInstallerService.Install(staged);
        }
        finally
        {
            updateDownload = null;
            SetUpdateProgress(0);
        }
        try { UpdateInstallerService.Restart(portable); }
        catch (Exception ex)
        {
            // The new version is already in place; it runs from the next start.
            Error(new InvalidOperationException(Loc.Format("Update.RestartFailed", ex.Message), ex));
            return;
        }
        log.Write(Loc.Format("Update.Restarting", target.Version));
        await ((App)Application.Current).ExitAsync();
    }

    private void SetUpdateProgress(double value)
    {
        updateProgress = value;
        OnPropertyChanged(nameof(UpdateButtonLabel));
        OnPropertyChanged(nameof(UpdateButtonTooltip));
    }

    private static void OpenReleasePage(string? pageUrl)
    {
        // The URL comes from a network response; ReleaseInfo keeps it only when it is this repository's page.
        var url = AppUpdate.IsTrustedPageUrl(pageUrl) ? pageUrl! : AppUpdate.ReleasesPage;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void ToggleLanguage()
    {
        Loc.ToggleLanguage();
        var language = Text.Language;
        store.Save(recorder.UpdatePreferences(s => s.Language = language));
        // Keep the edit copy in step, or the toggle reads as an unsaved change and a later Save reverts it.
        SettingsEdit.System.Language = language;
        RebuildChoices();
        // Monitor captions are built in the current language at enumeration time.
        _ = ReloadDevicesAsync();
        customMessage = null;
        customDiagnostics = null;
        // Language affects almost every computed label; an empty name refreshes all bindings on this view model.
        OnPropertyChanged(string.Empty);
        FilteredClips.Refresh();
    }

    private void ToggleTheme()
    {
        ThemeSvc.ToggleTheme();
        store.Save(recorder.UpdatePreferences(s => s.Theme = ThemeSvc.ThemeCode));
        SettingsEdit.System.Theme = ThemeSvc.ThemeCode;
        OnPropertyChanged(nameof(ThemeButtonLabel));
    }

    private void Safe(Action action) { try { action(); } catch (Exception ex) { Error(ex); } }
    // AsyncRelayCommand blocks re-entry while it runs; failures surface through Error like every other command.
    private AsyncRelayCommand Async(Func<Task> execute, Func<bool>? canExecute = null) =>
        new(async () => { try { await execute(); } catch (Exception ex) { Error(ex); } }, canExecute ?? (() => true));
    // Properties can change before the constructor has created every command.
    private static void NotifyCommands(params IRelayCommand?[] commands)
    {
        foreach (var command in commands) command?.NotifyCanExecuteChanged();
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (watcher is not null) await watcher;
        await operation.WaitAsync(); // Wait for a user-initiated start/settings/probe/UAC workflow too.
        Busy = true;
        hotkey?.Dispose();
        overlay.Dispose();
        recorder.Changed -= OnChanged; recorder.ClipSaved -= OnSaved; pipe.StateChanged -= OnChanged; log.Entry -= OnLog;
        stop.Dispose(); operation.Dispose();
    }
}
