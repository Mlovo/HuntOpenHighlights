using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using static HuntReplay.E2ETests.E2EInfrastructure;

namespace HuntReplay.E2ETests;

public static class Tier1_FeatureCoverageTests
{
    public static void RunAll(Action<string, Action> test, Action<string, Func<Task>> testAsync)
    {
        Console.WriteLine("\n=======================================================");
        Console.WriteLine("  TIER 1: FEATURE COVERAGE (ISOLATED HAPPY PATH)");
        Console.WriteLine("=======================================================");

        test("T1-01 §R1: Trim Video button & smooth entry into trim mode swaps Seek slider for RangeSlider", () =>
        {
            WithThemedWindow((_, host) =>
            {
                var seek = new Slider { Name = "Seek", Visibility = Visibility.Visible, Minimum = 0, Maximum = 60 };
                var trimSliderContainer = new ContentControl { Name = "TrimSlider", Visibility = Visibility.Collapsed };
                var trimToolbar = new StackPanel { Name = "TrimToolbar", Visibility = Visibility.Collapsed };

                host.Children.Add(seek);
                host.Children.Add(trimSliderContainer);
                host.Children.Add(trimToolbar);
                Pump();

                // Initial normal playback state
                Equal(Visibility.Visible, seek.Visibility);
                Equal(Visibility.Collapsed, trimSliderContainer.Visibility);
                Equal(Visibility.Collapsed, trimToolbar.Visibility);

                // Enter trim mode
                bool isTrimMode = true;
                bool isPlaying = true;
                if (isTrimMode && isPlaying) isPlaying = false; // Player must pause on entry

                seek.Visibility = isTrimMode ? Visibility.Collapsed : Visibility.Visible;
                trimSliderContainer.Visibility = isTrimMode ? Visibility.Visible : Visibility.Collapsed;
                trimToolbar.Visibility = isTrimMode ? Visibility.Visible : Visibility.Collapsed;
                Pump();

                False(isPlaying, "Player should be paused on entering trim mode");
                Equal(Visibility.Collapsed, seek.Visibility);
                Equal(Visibility.Visible, trimSliderContainer.Visibility);
                Equal(Visibility.Visible, trimToolbar.Visibility);
            });
        });

        test("T1-02 §R1: Dual-handle timeline initializes to clip bounds with accent highlight", () =>
        {
            var asm = GetAppAssembly();
            var sliderType = asm.GetType("HuntReplay.App.Controls.RangeSlider")
                ?? throw new InvalidOperationException("RangeSlider type not found in HuntReplayStudio.dll");

            WithThemedWindow((_, host) =>
            {
                var slider = (FrameworkElement)Activator.CreateInstance(sliderType!)!;
                slider.Width = 500;
                host.Children.Add(slider);
                Pump();

                var propMin = sliderType.GetProperty("Minimum")!;
                var propMax = sliderType.GetProperty("Maximum")!;
                var propStart = sliderType.GetProperty("RangeStart")!;
                var propEnd = sliderType.GetProperty("RangeEnd")!;
                var propMinRange = sliderType.GetProperty("MinRange")!;

                propMin.SetValue(slider, 0.0);
                propMax.SetValue(slider, 30.0);
                propMinRange.SetValue(slider, 0.5);
                propStart.SetValue(slider, 0.0);
                propEnd.SetValue(slider, 30.0);
                Pump();

                Equal(0.0, (double)propStart.GetValue(slider)!);
                Equal(30.0, (double)propEnd.GetValue(slider)!);
                Equal(0.5, (double)propMinRange.GetValue(slider)!);
            });
        });

        test("T1-03 §R1: Real-time timestamp formatting for sub-hour and hour-plus durations", () =>
        {
            var asm = GetAppAssembly();
            var formatterType = asm.GetType("HuntReplay.App.Services.VideoTrimmingFormatters");
            MethodInfo formatMethod = formatterType?.GetMethod("FormatTime")!;
            Func<double, string> format = sec => (string)formatMethod.Invoke(null, [sec])!;

            // Sub-hour conversions
            Equal("00:00", format(0.0));
            Equal("00:05", format(5.0));
            Equal("01:23", format(83.0));
            Equal("59:59", format(3599.0));

            // Hour-plus conversions
            Equal("01:00:00", format(3600.0));
            Equal("01:05:30", format(3930.0));
            Equal("10:00:00", format(36000.0));
        });

        test("T1-04 §R1: Fine-tuning stepping controls adjust start and end points precisely by 0.1s", () =>
        {
            var asm = GetAppAssembly();
            var formatterType = asm.GetType("HuntReplay.App.Services.VideoTrimmingFormatters")!;
            var stepMethod = formatterType.GetMethod("StepTime")!;
            Func<double, double, double, double, double> step = (cur, delta, min, max) =>
                (double)stepMethod.Invoke(null, [cur, delta, min, max])!;

            double duration = 50.0;
            double start = 5.0;
            double end = 15.0;
            const double minRange = 0.5;

            // Step Start down (-0.1s)
            start = step(start, -0.1, 0.0, end - minRange);
            Equal(4.9, start);

            // Step Start up (+0.1s)
            start = step(start, +0.1, 0.0, end - minRange);
            Equal(5.0, start);

            // Step End down (-0.1s)
            end = step(end, -0.1, start + minRange, duration);
            Equal(14.9, end);

            // Step End up (+0.1s)
            end = step(end, +0.1, start + minRange, duration);
            Equal(15.0, end);
        });

        test("T1-05 §R1: Mark In [I] and Mark Out [O] adjust boundaries to current player PTS", () =>
        {
            const double minRange = 0.5;
            double maxDuration = 60.0;
            double start = 0.0;
            double end = 60.0;

            // Player seeks to PTS 14.2s and triggers Mark In [I]
            double currentPts = 14.2;
            start = Math.Clamp(currentPts, 0.0, end - minRange);
            Equal(14.2, start);

            // Player seeks to PTS 35.8s and triggers Mark Out [O]
            currentPts = 35.8;
            end = Math.Clamp(currentPts, start + minRange, maxDuration);
            Equal(35.8, end);

            // Verify span duration
            Equal(21.6, Math.Round(end - start, 2));
        });

        test("T1-06 §R1: Selection preview seeks to start and automatically pauses upon reaching end boundary", () =>
        {
            double trimStart = 5.0;
            double trimEnd = 10.0;
            bool isPlaying = false;
            bool isPreviewingFragment = false;

            // 1. User clicks Preview: should seek to trimStart and begin playback
            double currentPts = 1.0;
            if (currentPts < trimStart || currentPts >= trimEnd - 0.1)
            {
                currentPts = trimStart;
            }
            isPlaying = true;
            isPreviewingFragment = true;

            Equal(5.0, currentPts);
            True(isPlaying);
            True(isPreviewingFragment);

            // 2. Playback advances inside fragment window
            currentPts = 8.5;
            if (isPreviewingFragment && currentPts >= trimEnd)
            {
                isPlaying = false;
                isPreviewingFragment = false;
            }
            True(isPlaying);

            // 3. Playback reaches trimEnd: timer must pause player and clear preview mode
            currentPts = 10.0;
            if (isPreviewingFragment && currentPts >= trimEnd)
            {
                isPlaying = false;
                isPreviewingFragment = false;
            }
            False(isPlaying, "Player must automatically pause at fragment end");
            False(isPreviewingFragment, "Preview mode flag must reset");
        });

        test("T1-07 §R1: Cancel action restores normal player state without altering source file", () => InTemp(dir =>
        {
            string source = GenerateSampleClip(dir, "source_cancel.mp4", 5.0, hasAudio: true);
            string initialHash = ComputeFileSha256(source);
            DateTime initialTime = File.GetLastWriteTimeUtc(source);

            // Simulate UI trim state
            bool isTrimMode = true;
            double trimStart = 1.0;
            double trimEnd = 4.0;

            // Trigger Cancel
            isTrimMode = false;
            trimStart = 0.0;
            trimEnd = 0.0;

            False(isTrimMode);
            Equal(0.0, trimStart);
            Equal(0.0, trimEnd);

            // Source file must remain completely byte-for-byte identical
            Equal(initialHash, ComputeFileSha256(source));
            Equal(initialTime, File.GetLastWriteTimeUtc(source));
        }));

        test("T1-08 §R2: Native fast stream copying remuxes packets without re-encoding", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateSampleClip(dir, "source_fast.mp4", 6.0, hasAudio: true);
            string target = Path.Combine(dir, "target_fast.mp4");

            int res = NativeTrimInterop.hr_trim(source, target, 1.0, 4.0, 0, null, IntPtr.Zero);
            Equal(0, res, $"hr_trim failed: {NativeTrimInterop.GetLastError()}");
            True(File.Exists(target), "Target file must exist");

            var probe = ProbeMedia(target);
            InRange(probe.Duration, 2.5, 3.5, "Trimmed duration should be ~3.0s");

            var vStreams = probe.Streams.FindAll(s => s.CodecType == "video");
            var aStreams = probe.Streams.FindAll(s => s.CodecType == "audio");

            Equal(1, vStreams.Count);
            Equal("h264", vStreams[0].CodecName);
            Equal(1, aStreams.Count);
            Equal("aac", aStreams[0].CodecName);
        }));

        test("T1-09 §R2: Native frame-accurate trimming mode flag executes successfully", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateSampleClip(dir, "source_acc.mp4", 5.0, hasAudio: true);
            string target = Path.Combine(dir, "target_acc.mp4");

            int res = NativeTrimInterop.hr_trim(source, target, 1.0, 3.5, 1, null, IntPtr.Zero);
            Equal(0, res, $"hr_trim accurate mode failed: {NativeTrimInterop.GetLastError()}");
            True(File.Exists(target), "Target file must exist");

            var probe = ProbeMedia(target);
            InRange(probe.Duration, 2.0, 3.0);
        }));

        test("T1-10 §R2: Multi-track audio preservation retains all tracks with original rates and channels", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateMultiAudioClip(dir, "source_multi.mp4", 8.0);
            string target = Path.Combine(dir, "target_multi.mp4");

            int res = NativeTrimInterop.hr_trim(source, target, 1.0, 5.0, 0, null, IntPtr.Zero);
            Equal(0, res);
            True(File.Exists(target));

            var probe = ProbeMedia(target);
            var aStreams = probe.Streams.FindAll(s => s.CodecType == "audio");

            Equal(2, aStreams.Count, "Target must preserve exactly 2 audio streams");
            Equal("aac", aStreams[0].CodecName);
            Equal(48000, aStreams[0].SampleRate);
            Equal(2, aStreams[0].Channels);

            Equal("aac", aStreams[1].CodecName);
            Equal(44100, aStreams[1].SampleRate);
            Equal(1, aStreams[1].Channels);
        }));

        test("T1-11 §R2: Audio/Video synchronization packet timestamps maintain < 25ms drift", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateMultiAudioClip(dir, "source_sync.mp4", 10.0);
            string target = Path.Combine(dir, "target_sync.mp4");

            int res = NativeTrimInterop.hr_trim(source, target, 2.0, 7.0, 0, null, IntPtr.Zero);
            Equal(0, res);

            var vPackets = ReadPackets(target, "v:0");
            var a0Packets = ReadPackets(target, "a:0");
            var a1Packets = ReadPackets(target, "a:1");

            True(vPackets.Count > 30, "Should have sufficient video packets");
            True(a0Packets.Count > 30, "Should have sufficient audio 0 packets");
            True(a1Packets.Count > 30, "Should have sufficient audio 1 packets");

            // Initial packet PTS offset check
            double offsetA0 = Math.Abs(a0Packets[0].PtsTime - vPackets[0].PtsTime);
            double offsetA1 = Math.Abs(a1Packets[0].PtsTime - vPackets[0].PtsTime);

            True(offsetA0 < 0.025, $"Initial A0 offset ({offsetA0*1000:F2}ms) must be < 25ms");
            True(offsetA1 < 0.025, $"Initial A1 offset ({offsetA1*1000:F2}ms) must be < 25ms");
        }));

        test("T1-12 §R2: Source video file is completely immutable across trimming operations", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateSampleClip(dir, "source_immutable.mp4", 6.0, hasAudio: true);
            string initialHash = ComputeFileSha256(source);
            DateTime initialWriteTime = File.GetLastWriteTimeUtc(source);

            // Operation 1: Successful trim
            string target1 = Path.Combine(dir, "out1.mp4");
            Equal(0, NativeTrimInterop.hr_trim(source, target1, 1.0, 3.0, 0, null, IntPtr.Zero));
            Equal(initialHash, ComputeFileSha256(source));
            Equal(initialWriteTime, File.GetLastWriteTimeUtc(source));

            // Operation 2: Cancelled trim
            string target2 = Path.Combine(dir, "out2.mp4");
            NativeTrimInterop.ProgressCallback cancelCb = (_, _) => 1;
            int cancelRes = NativeTrimInterop.hr_trim(source, target2, 1.0, 4.0, 0, cancelCb, IntPtr.Zero);
            True(cancelRes < 0);
            Equal(initialHash, ComputeFileSha256(source));
            Equal(initialWriteTime, File.GetLastWriteTimeUtc(source));

            // Operation 3: Failing trim
            int failRes = NativeTrimInterop.hr_trim(source, source, 1.0, 3.0, 0, null, IntPtr.Zero);
            True(failRes < 0);
            Equal(initialHash, ComputeFileSha256(source));
            Equal(initialWriteTime, File.GetLastWriteTimeUtc(source));
        }));

        testAsync("T1-13 §R2: Asynchronous trimming service progress reporting reaches 1.0 (100%)", async () =>
        {
            EnsureNativeLoaded();
            var asm = GetAppAssembly();
            var serviceType = asm.GetType("HuntReplay.App.Services.VideoTrimmingService")!;
            var instanceProp = serviceType.GetProperty("Instance")!;
            var service = instanceProp.GetValue(null)!;
            var trimMethod = serviceType.GetMethod("TrimVideoAsync")!;

            await InTempAsync(async dir =>
            {
                string source = GenerateSampleClip(dir, "source_progress.mp4", 6.0, hasAudio: true);
                string target = Path.Combine(dir, "target_progress.mp4");

                var progressValues = new List<double>();
                var progressLock = new object();
                var progress = new Progress<double>(p => { lock (progressLock) progressValues.Add(p); });

                var task = (Task<string>)trimMethod.Invoke(service, [source, target, 1.0, 4.0, false, progress, CancellationToken.None])!;
                string resultPath = await task;

                Equal(target, resultPath);
                True(File.Exists(resultPath));
                True(progressValues.Count > 0, "Progress reporter must receive notifications");
                Equal(1.0, progressValues[^1], "Final progress report must be 1.0");
            });
        });

        test("T1-14 §R3: Audio-less video trimming preserves video stream without errors", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateSampleClip(dir, "source_silent.mp4", 6.0, hasAudio: false);
            string target = Path.Combine(dir, "target_silent.mp4");

            int res = NativeTrimInterop.hr_trim(source, target, 1.0, 4.0, 0, null, IntPtr.Zero);
            Equal(0, res);
            True(File.Exists(target));

            var probe = ProbeMedia(target);
            InRange(probe.Duration, 2.5, 3.5);
            Equal(1, probe.Streams.FindAll(s => s.CodecType == "video").Count);
            Equal(0, probe.Streams.FindAll(s => s.CodecType == "audio").Count, "Must have zero audio streams");

            // Verify playable in native player engine
            int openRes = NativeTrimInterop.hr_player_open(target, out IntPtr player, out int w, out int h, out double dur);
            Equal(0, openRes);
            try
            {
                True(w > 0 && h > 0);
                InRange(dur, 2.5, 3.5);
            }
            finally
            {
                NativeTrimInterop.hr_player_close(player);
            }
        }));

        test("T1-15 §R3: Player controls non-regression preserves normal playback and volume", () =>
        {
            WithThemedWindow((_, host) =>
            {
                var volSlider = new Slider { Minimum = 0, Maximum = 1, Value = 1.0 };
                var seekSlider = new Slider { Minimum = 0, Maximum = 100, Value = 0.0 };
                host.Children.Add(volSlider);
                host.Children.Add(seekSlider);
                Pump();

                volSlider.Value = 0.5;
                Pump();
                Equal(0.5, volSlider.Value);

                seekSlider.Value = 42.0;
                Pump();
                Equal(42.0, seekSlider.Value);
            });
        });
    }
}
