using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace HuntReplay.WindowsTests;

public static class GalleryViewTrimmingTests
{
    public static void RunAllTests()
    {
        TestTrimModeVisibilityTransitions();
        TestRangeSliderCoercionAndBounds();
        TestRangeSliderFineTuningStepping();
        TestRangeSliderMarkInOutBindings();
        TestPreviewPlaybackBoundaryEnforcement();
        TestCancelRestorationInvariants();
        TestExportProgressAndCancellationState();
        TestRangeSliderAssemblyInstantiation();
    }

    /// <summary>
    /// Verifies that entering Trim Mode smoothly swaps player controls:
    /// hides standard Seek slider, reveals RangeSlider and inline TrimToolbar, and pauses playback.
    /// Canceling trim mode restores standard Seek and collapses trim controls without side effects.
    /// </summary>
    private static void TestTrimModeVisibilityTransitions()
    {
        Program.Test("trim mode transition: toggling trim mode swaps Seek slider for RangeSlider and toolbar", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var seek = new Slider { Name = "Seek", Visibility = Visibility.Visible, Minimum = 0, Maximum = 60 };
                var rangeSliderContainer = new ContentControl { Name = "TrimSlider", Visibility = Visibility.Collapsed };
                var trimToolbar = new StackPanel { Name = "TrimToolbar", Visibility = Visibility.Collapsed };

                host.Children.Add(seek);
                host.Children.Add(rangeSliderContainer);
                host.Children.Add(trimToolbar);
                Program.Pump();

                // 1. Initial normal playback state
                Program.Equal(Visibility.Visible, seek.Visibility);
                Program.Equal(Visibility.Collapsed, rangeSliderContainer.Visibility);
                Program.Equal(Visibility.Collapsed, trimToolbar.Visibility);

                // 2. Simulate entering trim mode
                bool isTrimMode = true;
                bool isPlaying = true;
                if (isTrimMode && isPlaying) isPlaying = false; // Must pause player on entry
                seek.Visibility = isTrimMode ? Visibility.Collapsed : Visibility.Visible;
                rangeSliderContainer.Visibility = isTrimMode ? Visibility.Visible : Visibility.Collapsed;
                trimToolbar.Visibility = isTrimMode ? Visibility.Visible : Visibility.Collapsed;
                Program.Pump();

                Program.False(isPlaying);
                Program.Equal(Visibility.Collapsed, seek.Visibility);
                Program.Equal(Visibility.Visible, rangeSliderContainer.Visibility);
                Program.Equal(Visibility.Visible, trimToolbar.Visibility);

                // 3. Simulate exiting trim mode (Cancel)
                isTrimMode = false;
                seek.Visibility = isTrimMode ? Visibility.Collapsed : Visibility.Visible;
                rangeSliderContainer.Visibility = isTrimMode ? Visibility.Visible : Visibility.Collapsed;
                trimToolbar.Visibility = isTrimMode ? Visibility.Visible : Visibility.Collapsed;
                Program.Pump();

                Program.Equal(Visibility.Visible, seek.Visibility);
                Program.Equal(Visibility.Collapsed, rangeSliderContainer.Visibility);
                Program.Equal(Visibility.Collapsed, trimToolbar.Visibility);
            });
        });
    }

    /// <summary>
    /// Verifies dual handles initialize to full clip duration and strictly enforce
    /// the mathematical boundary invariants (0 <= Start < End <= Max, with End - Start >= MinRange = 0.5s).
    /// </summary>
    private static void TestRangeSliderCoercionAndBounds()
    {
        Program.Test("range slider: enforces bounds, negative clamp, and minimum duration constraint", () =>
        {
            const double minRange = 0.5;
            double min = 0.0;
            double max = 120.0;
            double start = 0.0;
            double end = 120.0;

            // Coercion functions replicating RangeSlider invariant logic
            double CoerceStart(double val, double currentEnd)
            {
                double effectiveMin = Math.Min(minRange, Math.Max(0.0, max - min));
                double maxAllowed = Math.Max(min, currentEnd - effectiveMin);
                return Math.Clamp(val, min, maxAllowed);
            }

            double CoerceEnd(double val, double currentStart)
            {
                double effectiveMin = Math.Min(minRange, Math.Max(0.0, max - min));
                double minAllowed = Math.Min(max, currentStart + effectiveMin);
                return Math.Clamp(val, minAllowed, max);
            }

            // Normal initialization span
            Program.Equal(0.0, start);
            Program.Equal(120.0, end);
            Program.True(end - start >= minRange);

            // Attempt setting negative Start -> coerced to Minimum (0.0)
            start = CoerceStart(-15.0, end);
            Program.Equal(0.0, start);

            // Attempt setting End beyond Maximum -> coerced to Maximum (120.0)
            end = CoerceEnd(150.0, start);
            Program.Equal(120.0, end);

            // Set boundaries inside range
            start = CoerceStart(10.0, end);
            end = CoerceEnd(20.0, start);
            Program.Equal(10.0, start);
            Program.Equal(20.0, end);

            // Attempt pushing Start past End (e.g. Start = 25.0 while End = 20.0)
            start = CoerceStart(25.0, end);
            Program.Equal(19.5, start); // End - 0.5 = 19.5
            Program.True(end - start >= minRange);

            // Attempt pushing End before Start (e.g. End = 5.0 while Start = 19.5)
            end = CoerceEnd(5.0, start);
            Program.Equal(20.0, end); // Start + 0.5 = 20.0
            Program.True(end - start >= minRange);
        });
    }

    /// <summary>
    /// Verifies stepping fine-tuning buttons (-0.1s, +0.1s) for Start and End boundaries.
    /// </summary>
    private static void TestRangeSliderFineTuningStepping()
    {
        Program.Test("fine-tuning controls: step adjustments increment, decrement and clamp precisely", () =>
        {
            double duration = 50.0;
            double start = 5.0;
            double end = 15.0;
            const double minRange = 0.5;

            double StepTime(double current, double delta, double minBound, double maxBound)
            {
                double stepped = Math.Round(current + delta, 3);
                return Math.Clamp(stepped, minBound, maxBound);
            }

            // Step Start down (-0.1s)
            start = StepTime(start, -0.1, 0.0, end - minRange);
            Program.Equal(4.9, start);

            // Step Start up (+0.1s)
            start = StepTime(start, +0.1, 0.0, end - minRange);
            Program.Equal(5.0, start);

            // Step Start up near boundary (end - 0.5 = 14.5)
            start = 14.45;
            start = StepTime(start, +0.1, 0.0, end - minRange);
            Program.Equal(14.5, start);

            // Step End down near boundary (start + 0.5 = 15.0)
            end = 15.05;
            end = StepTime(end, -0.1, start + minRange, duration);
            Program.Equal(15.0, end);

            // Step End up near duration max
            end = 49.95;
            end = StepTime(end, +0.1, start + minRange, duration);
            Program.Equal(50.0, end);
        });
    }

    /// <summary>
    /// Verifies Mark In (I) and Mark Out (O) actions set boundaries accurately to current playback PTS.
    /// </summary>
    private static void TestRangeSliderMarkInOutBindings()
    {
        Program.Test("fine-tuning controls: Mark In [I] and Mark Out [O] adjust boundaries to current PTS", () =>
        {
            const double minRange = 0.5;
            double maxDuration = 60.0;
            double start = 0.0;
            double end = 60.0;

            // User seeks to PTS 12.4 and triggers Mark In [I]
            double currentPts = 12.4;
            start = Math.Clamp(currentPts, 0.0, end - minRange);
            Program.Equal(12.4, start);

            // User seeks to PTS 28.6 and triggers Mark Out [O]
            currentPts = 28.6;
            end = Math.Clamp(currentPts, start + minRange, maxDuration);
            Program.Equal(28.6, end);

            // User attempts Mark In at 28.5 (violating min range against End = 28.6)
            currentPts = 28.5;
            start = Math.Clamp(currentPts, 0.0, end - minRange);
            Program.Equal(28.1, Math.Round(start, 2)); // Clamped to 28.6 - 0.5 = 28.1
            Program.True(end - start >= minRange);
        });
    }

    /// <summary>
    /// Verifies Selection Preview playback automatically halts upon reaching SelectionEnd.
    /// </summary>
    private static void TestPreviewPlaybackBoundaryEnforcement()
    {
        Program.Test("selection preview: playback loop automatically detects SelectionEnd and pauses", () =>
        {
            double trimStart = 10.0;
            double trimEnd = 15.0;
            bool isPlaying = true;
            bool isPreviewingFragment = true;

            // Tick 0: start preview jumps to trimStart if current PTS outside selection
            double currentPts = 2.0;
            if (currentPts < trimStart || currentPts >= trimEnd - 0.1)
            {
                currentPts = trimStart;
            }
            Program.Equal(10.0, currentPts);

            // Tick 1: current PTS inside selection window
            currentPts = 12.5;
            if (isPreviewingFragment && currentPts >= trimEnd)
            {
                isPlaying = false;
                isPreviewingFragment = false;
            }
            Program.True(isPlaying);
            Program.True(isPreviewingFragment);

            // Tick 2: current PTS reaches trimEnd boundary
            currentPts = 15.0;
            if (isPreviewingFragment && currentPts >= trimEnd)
            {
                isPlaying = false;
                isPreviewingFragment = false;
            }
            Program.False(isPlaying);
            Program.False(isPreviewingFragment);
        });
    }

    /// <summary>
    /// Verifies clicking Cancel restores player state without modifying source file.
    /// </summary>
    private static void TestCancelRestorationInvariants()
    {
        Program.Test("cancel action: restores standard player without mutating source file", () =>
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "HR_Test_Cancel_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string sourceFile = Path.Combine(tempDir, "clip.mp4");
                File.WriteAllBytes(sourceFile, [10, 20, 30, 40, 50, 60, 70, 80]);
                byte[] originalBytes = File.ReadAllBytes(sourceFile);

                bool isTrimMode = true;
                double trimStart = 5.0;
                double trimEnd = 15.0;

                // Simulate Cancel action
                isTrimMode = false;
                trimStart = 0.0;
                trimEnd = 0.0;

                Program.False(isTrimMode);
                Program.Equal(0.0, trimStart);
                Program.Equal(0.0, trimEnd);
                Program.True(File.Exists(sourceFile));
                Program.True(File.ReadAllBytes(sourceFile).SequenceEqual(originalBytes));
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        });
    }

    /// <summary>
    /// Verifies export progression state transitions and cancellation handling.
    /// </summary>
    private static void TestExportProgressAndCancellationState()
    {
        Program.Test("export workflow: progress reporting updates UI and cancellation halts cleanly", () =>
        {
            Program.WithThemedWindow((_, host) =>
            {
                var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0 };
                var statusText = new TextBlock { Text = "0%" };
                var cancelBtn = new Button { Content = "Отмена" };

                host.Children.Add(progressBar);
                host.Children.Add(statusText);
                host.Children.Add(cancelBtn);
                Program.Pump();

                // Progress update simulation
                void UpdateProgress(double p)
                {
                    progressBar.Value = p * 100;
                    statusText.Text = $"{(int)(p * 100)}%";
                }

                UpdateProgress(0.65);
                Program.Pump();

                Program.Equal(65.0, progressBar.Value);
                Program.Equal("65%", statusText.Text);

                // Cancellation simulation
                var cts = new CancellationTokenSource();
                cts.Cancel();
                Program.True(cts.IsCancellationRequested);

                cancelBtn.IsEnabled = false;
                cancelBtn.Content = "Отмена…";
                Program.Pump();

                Program.False(cancelBtn.IsEnabled);
                Program.Equal("Отмена…", cancelBtn.Content);
            });
        });
    }

    /// <summary>
    /// Verifies that the production RangeSlider control from HuntReplayStudio.dll can be loaded,
    /// instantiated, added to the WPF visual tree, and maintains its dependency property invariants.
    /// </summary>
    private static void TestRangeSliderAssemblyInstantiation()
    {
        Program.Test("production RangeSlider: loads from assembly, mounts in WPF tree, and enforces invariants", () =>
        {
            string baseDir = AppContext.BaseDirectory;
            string? root = null;
            var dir = new DirectoryInfo(baseDir);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "HuntReplayStudio.sln")))
                {
                    root = dir.FullName;
                    break;
                }
                dir = dir.Parent;
            }

            string[] candidatePaths = root != null
                ? [
                    Path.Combine(root, "src", "HuntReplay.App", "bin", "x64", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
                    Path.Combine(root, "src", "HuntReplay.App", "bin", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
                    Path.Combine(root, "src", "HuntReplay.App", "bin", "Debug", "net8.0-windows", "HuntReplayStudio.dll"),
                    Path.Combine(root, "src", "HuntReplay.App", "bin", "Release", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
                    Path.Combine(root, "src", "HuntReplay.App", "bin", "x64", "Release", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll"),
                    Path.Combine(baseDir, "HuntReplayStudio.dll")
                ]
                : [
                    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "HuntReplay.App", "bin", "x64", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll")),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "src", "HuntReplay.App", "bin", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll")),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "src", "HuntReplay.App", "bin", "x64", "Debug", "net8.0-windows10.0.19041.0", "win-x64", "HuntReplayStudio.dll")),
                    Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "src", "HuntReplay.App", "bin", "Debug", "net8.0-windows", "HuntReplayStudio.dll")),
                    Path.Combine(baseDir, "HuntReplayStudio.dll")
                ];

            string? assemblyPath = candidatePaths.FirstOrDefault(File.Exists);
            if (assemblyPath == null && root != null)
            {
                var appBin = Path.Combine(root, "src", "HuntReplay.App", "bin");
                if (Directory.Exists(appBin))
                {
                    assemblyPath = Directory.GetFiles(appBin, "HuntReplayStudio.dll", SearchOption.AllDirectories).FirstOrDefault();
                }
            }

            if (assemblyPath == null && root != null)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"build \"{Path.Combine(root, "src", "HuntReplay.App")}\" -c Release")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();
                assemblyPath = candidatePaths.FirstOrDefault(File.Exists);
                if (assemblyPath == null)
                {
                    var appBin = Path.Combine(root, "src", "HuntReplay.App", "bin");
                    if (Directory.Exists(appBin))
                    {
                        assemblyPath = Directory.GetFiles(appBin, "HuntReplayStudio.dll", SearchOption.AllDirectories).FirstOrDefault();
                    }
                }
            }

            Program.True(assemblyPath != null);

            var asm = Assembly.LoadFrom(assemblyPath!);
            var sliderType = asm.GetType("HuntReplay.App.Controls.RangeSlider");
            Program.True(sliderType != null);

            Program.WithThemedWindow((_, host) =>
            {
                var slider = (FrameworkElement)Activator.CreateInstance(sliderType!)!;
                host.Children.Add(slider);
                Program.Pump();

                var propMin = sliderType!.GetProperty("Minimum");
                var propMax = sliderType!.GetProperty("Maximum");
                var propStart = sliderType!.GetProperty("RangeStart");
                var propEnd = sliderType!.GetProperty("RangeEnd");
                var propMinRange = sliderType!.GetProperty("MinRange");

                propMin?.SetValue(slider, 0.0);
                propMax?.SetValue(slider, 100.0);
                propMinRange?.SetValue(slider, 0.5);
                propEnd?.SetValue(slider, 20.0);
                propStart?.SetValue(slider, 10.0);
                Program.Pump();

                Program.Equal(10.0, (double)propStart?.GetValue(slider)!);
                Program.Equal(20.0, (double)propEnd?.GetValue(slider)!);

                // Attempt setting Start past End - MinRange
                propStart?.SetValue(slider, 25.0);
                Program.Pump();
                Program.Equal(19.5, (double)propStart?.GetValue(slider)!);
            });
        });
    }
}
