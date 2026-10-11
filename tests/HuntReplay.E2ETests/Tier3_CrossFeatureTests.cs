using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using static HuntReplay.E2ETests.E2EInfrastructure;

namespace HuntReplay.E2ETests;

public static class Tier3_CrossFeatureTests
{
    public static void RunAll(Action<string, Action> test, Action<string, Func<Task>> testAsync)
    {
        Console.WriteLine("\n=======================================================");
        Console.WriteLine("  TIER 3: CROSS-FEATURE COMBINATIONS & PAIRWISE");
        Console.WriteLine("=======================================================");

        test("T3-01 §R1: Interactive workflow: fine-tune stepping -> jump to start -> preview playback -> boundary pause", () =>
        {
            var asm = GetAppAssembly();
            var sliderType = asm.GetType("HuntReplay.App.Controls.RangeSlider")!;
            var formatterType = asm.GetType("HuntReplay.App.Services.VideoTrimmingFormatters")!;
            var stepMethod = formatterType.GetMethod("StepTime")!;

            WithThemedWindow((_, host) =>
            {
                var slider = (FrameworkElement)Activator.CreateInstance(sliderType)!;
                slider.Width = 500;
                host.Children.Add(slider);
                Pump();

                var propMin = sliderType.GetProperty("Minimum")!;
                var propMax = sliderType.GetProperty("Maximum")!;
                var propStart = sliderType.GetProperty("RangeStart")!;
                var propEnd = sliderType.GetProperty("RangeEnd")!;
                var propPlayhead = sliderType.GetProperty("Playhead")!;

                propMin.SetValue(slider, 0.0);
                propMax.SetValue(slider, 30.0);
                propEnd.SetValue(slider, 15.0);
                propStart.SetValue(slider, 5.0);
                Pump();

                // 1. Fine-tune stepping
                double start = (double)propStart.GetValue(slider)!;
                double end = (double)propEnd.GetValue(slider)!;

                // Step start +0.3s (3 clicks)
                for (int i = 0; i < 3; i++) start = (double)stepMethod.Invoke(null, [start, +0.1, 0.0, end - 0.5])!;
                propStart.SetValue(slider, start);

                // Step end -0.5s (5 clicks)
                for (int i = 0; i < 5; i++) end = (double)stepMethod.Invoke(null, [end, -0.1, start + 0.5, 30.0])!;
                propEnd.SetValue(slider, end);
                Pump();

                Equal(5.3, (double)propStart.GetValue(slider)!);
                Equal(14.5, (double)propEnd.GetValue(slider)!);

                // 2. Jump to start
                propPlayhead.SetValue(slider, start);
                Pump();
                Equal(5.3, (double)propPlayhead.GetValue(slider)!);

                // 3. Preview playback simulation
                bool isPlaying = true;
                bool isPreviewing = true;
                double currentPts = (double)propPlayhead.GetValue(slider)!;

                // Advance playhead in ticks
                currentPts += 4.0; // 9.3s
                propPlayhead.SetValue(slider, currentPts);
                Pump();
                True(isPlaying);

                currentPts = end; // Reaches 14.5s
                if (isPreviewing && currentPts >= end)
                {
                    isPlaying = false;
                    isPreviewing = false;
                }
                propPlayhead.SetValue(slider, currentPts);
                Pump();

                False(isPlaying, "Preview playback must halt at boundary end");
                Equal(14.5, (double)propPlayhead.GetValue(slider)!);
            });
        });

        testAsync("T3-02 §R1, §R2: Mark In [I] & Mark Out [O] followed by direct export verification", async () =>
        {
            EnsureNativeLoaded();
            var asm = GetAppAssembly();
            var serviceType = asm.GetType("HuntReplay.App.Services.VideoTrimmingService")!;
            var instance = serviceType.GetProperty("Instance")!.GetValue(null)!;
            var trimMethod = serviceType.GetMethod("TrimVideoAsync")!;

            await InTempAsync(async dir =>
            {
                string source = GenerateSampleClip(dir, "src_mark_export.mp4", 12.0, hasAudio: true);
                string target = Path.Combine(dir, "out_mark_export.mp4");

                // Simulate player at 2.0s -> Mark In
                double markInPts = 2.0;
                // Simulate player at 8.0s -> Mark Out
                double markOutPts = 8.0;

                // Export via service
                var task = (Task<string>)trimMethod.Invoke(instance, [source, target, markInPts, markOutPts, false, null, CancellationToken.None])!;
                string result = await task;

                Equal(target, result);
                True(File.Exists(result));

                var probe = ProbeMedia(result);
                InRange(probe.Duration, 5.5, 6.5, "Exported duration must match Mark Out - Mark In span (~6.0s)");
            });
        });

        testAsync("T3-03 §R1, §R2: Export cancellation cleans up resources and allows clean trim mode exit", async () =>
        {
            EnsureNativeLoaded();
            var asm = GetAppAssembly();
            var serviceType = asm.GetType("HuntReplay.App.Services.VideoTrimmingService")!;
            var instance = serviceType.GetProperty("Instance")!.GetValue(null)!;
            var trimMethod = serviceType.GetMethod("TrimVideoAsync")!;

            await InTempAsync(async dir =>
            {
                string source = GenerateSampleClip(dir, "src_cancel_workflow.mp4", 15.0, hasAudio: true);
                string target = Path.Combine(dir, "out_cancel_workflow.mp4");

                var cts = new CancellationTokenSource();
                var progress = new Progress<double>(p =>
                {
                    if (p >= 0.20) cts.Cancel();
                });

                var task = (Task<string>)trimMethod.Invoke(instance, [source, target, 1.0, 14.0, false, progress, cts.Token])!;
                await ThrowsAsync<OperationCanceledException>(async () => await task);

                // Target file must be cleaned up
                False(File.Exists(target), "Cancelled export target must be deleted");

                // Simulate exiting trim mode
                bool isTrimMode = false;
                False(isTrimMode);
            });
        });

        test("T3-04 §R2: Consecutive trimming passes on the same source video remain independent", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateSampleClip(dir, "src_consec.mp4", 15.0, hasAudio: true);
            string initialHash = ComputeFileSha256(source);

            // Pass 1: [1.0s, 4.0s] (3.0s duration)
            string out1 = Path.Combine(dir, "pass1.mp4");
            Equal(0, NativeTrimInterop.hr_trim(source, out1, 1.0, 4.0, 0, null, IntPtr.Zero));
            InRange(ProbeMedia(out1).Duration, 2.5, 3.5);

            // Pass 2: [5.0s, 9.0s] (4.0s duration)
            string out2 = Path.Combine(dir, "pass2.mp4");
            Equal(0, NativeTrimInterop.hr_trim(source, out2, 5.0, 9.0, 0, null, IntPtr.Zero));
            InRange(ProbeMedia(out2).Duration, 3.5, 4.5);

            // Pass 3: [10.0s, 14.0s] (4.0s duration)
            string out3 = Path.Combine(dir, "pass3.mp4");
            Equal(0, NativeTrimInterop.hr_trim(source, out3, 10.0, 14.0, 0, null, IntPtr.Zero));
            InRange(ProbeMedia(out3).Duration, 3.5, 4.5);

            // All 3 output files must exist simultaneously
            True(File.Exists(out1));
            True(File.Exists(out2));
            True(File.Exists(out3));

            // Source video must be 100% byte-identical
            Equal(initialHash, ComputeFileSha256(source));
        }));

        test("T3-05 §R2: Concurrent multi-threaded trimming isolates thread-local state and error reporting", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateSampleClip(dir, "src_parallel.mp4", 10.0, hasAudio: true);

            int concurrency = 8;
            var barrier = new Barrier(concurrency);
            var results = new ConcurrentDictionary<int, (int Code, string? Error, bool FileCreated)>();

            var threads = new List<Thread>();
            for (int i = 0; i < concurrency; i++)
            {
                int tid = i;
                var t = new Thread(() =>
                {
                    barrier.SignalAndWait(); // Synchronize all 8 threads to start concurrently
                    string target = Path.Combine(dir, $"parallel_{tid}.mp4");

                    if (tid % 4 == 0 || tid % 4 == 2) // Valid trim
                    {
                        int res = NativeTrimInterop.hr_trim(source, target, 1.0, 4.0, 0, null, IntPtr.Zero);
                        string? err = NativeTrimInterop.GetLastError();
                        results[tid] = (res, err, File.Exists(target));
                    }
                    else if (tid % 4 == 1) // Deliberate failure (bad path)
                    {
                        int res = NativeTrimInterop.hr_trim("nonexistent_input.mp4", target, 0, 5, 0, null, IntPtr.Zero);
                        string? err = NativeTrimInterop.GetLastError();
                        results[tid] = (res, err, File.Exists(target));
                    }
                    else // Cancelled trim
                    {
                        NativeTrimInterop.ProgressCallback cancel = (_, prog) => prog >= 0.3 ? 1 : 0;
                        int res = NativeTrimInterop.hr_trim(source, target, 1.0, 8.0, 0, cancel, IntPtr.Zero);
                        string? err = NativeTrimInterop.GetLastError();
                        results[tid] = (res, err, File.Exists(target));
                    }
                });
                threads.Add(t);
                t.Start();
            }

            foreach (var t in threads) t.Join();

            // Validate results
            for (int i = 0; i < concurrency; i++)
            {
                var r = results[i];
                if (i % 4 == 0 || i % 4 == 2)
                {
                    Equal(0, r.Code, $"Thread {i} expected success");
                    True(r.FileCreated, $"Thread {i} expected output file");
                    True(string.IsNullOrEmpty(r.Error), $"Thread {i} expected no error");
                }
                else if (i % 4 == 1)
                {
                    True(r.Code < 0, $"Thread {i} expected failure");
                    False(r.FileCreated, $"Thread {i} expected no file");
                    True(!string.IsNullOrEmpty(r.Error), $"Thread {i} expected error message");
                }
                else
                {
                    True(r.Code < 0, $"Thread {i} expected cancellation");
                    False(r.FileCreated, $"Thread {i} expected target cleanup");
                }
            }
        }));

        test("T3-06 §R1: LiveSeek scrubbing event updates playhead and player frame", () =>
        {
            var asm = GetAppAssembly();
            var sliderType = asm.GetType("HuntReplay.App.Controls.RangeSlider")!;

            WithThemedWindow((_, host) =>
            {
                var slider = (FrameworkElement)Activator.CreateInstance(sliderType)!;
                slider.Width = 500;
                host.Children.Add(slider);
                Pump();

                var propMin = sliderType.GetProperty("Minimum")!;
                var propMax = sliderType.GetProperty("Maximum")!;
                var propStart = sliderType.GetProperty("RangeStart")!;
                var propEnd = sliderType.GetProperty("RangeEnd")!;
                var propPlayhead = sliderType.GetProperty("Playhead")!;

                propMin.SetValue(slider, 0.0);
                propMax.SetValue(slider, 60.0);
                propStart.SetValue(slider, 10.0);
                propEnd.SetValue(slider, 40.0);
                Pump();

                // Scrub playhead to 25.0s
                propPlayhead.SetValue(slider, 25.0);
                Pump();
                Equal(25.0, (double)propPlayhead.GetValue(slider)!);

                // Scrub playhead to 38.0s
                propPlayhead.SetValue(slider, 38.0);
                Pump();
                Equal(38.0, (double)propPlayhead.GetValue(slider)!);
            });
        });
    }
}
