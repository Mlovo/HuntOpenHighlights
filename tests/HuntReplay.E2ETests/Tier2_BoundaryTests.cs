using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using static HuntReplay.E2ETests.E2EInfrastructure;

namespace HuntReplay.E2ETests;

public static class Tier2_BoundaryTests
{
    public static void RunAll(Action<string, Action> test, Action<string, Func<Task>> testAsync)
    {
        Console.WriteLine("\n=======================================================");
        Console.WriteLine("  TIER 2: BOUNDARY & CORNER CASES");
        Console.WriteLine("=======================================================");

        testAsync("T2-01 §R1, §R2: Sub-0.5s minimum duration constraint is strictly enforced", async () =>
        {
            var asm = GetAppAssembly();
            var serviceType = asm.GetType("HuntReplay.App.Services.VideoTrimmingService")!;
            var instance = serviceType.GetProperty("Instance")!.GetValue(null)!;
            var trimMethod = serviceType.GetMethod("TrimVideoAsync")!;

            await InTempAsync(async dir =>
            {
                string source = GenerateSampleClip(dir, "src_min.mp4", 5.0, hasAudio: true);
                string target = Path.Combine(dir, "out_min.mp4");

                // Start = 2.0, End = 2.3 -> delta 0.3s < 0.5s -> Must throw InvalidOperationException
                var task = (Task<string>)trimMethod.Invoke(instance, [source, target, 2.0, 2.3, false, null, CancellationToken.None])!;
                await ThrowsAsync<InvalidOperationException>(async () => await task);
            });
        });

        test("T2-02 §R1: Crossing boundaries prevents Start > End and clamps with 0.5s separation", () =>
        {
            var asm = GetAppAssembly();
            var sliderType = asm.GetType("HuntReplay.App.Controls.RangeSlider")!;

            WithThemedWindow((_, host) =>
            {
                var slider = (FrameworkElement)Activator.CreateInstance(sliderType)!;
                slider.Width = 400;
                host.Children.Add(slider);
                Pump();

                var propMin = sliderType.GetProperty("Minimum")!;
                var propMax = sliderType.GetProperty("Maximum")!;
                var propStart = sliderType.GetProperty("RangeStart")!;
                var propEnd = sliderType.GetProperty("RangeEnd")!;
                var propMinRange = sliderType.GetProperty("MinRange")!;

                propMin.SetValue(slider, 0.0);
                propMax.SetValue(slider, 60.0);
                propMinRange.SetValue(slider, 0.5);
                propStart.SetValue(slider, 10.0);
                propEnd.SetValue(slider, 30.0);
                Pump();

                // 1. Direct attempt to set Start past End (Start = 50.0 while End = 30.0)
                propStart.SetValue(slider, 50.0);
                Pump();
                Equal(29.5, (double)propStart.GetValue(slider)!, "Start must be clamped to End - 0.5");

                // 2. Direct attempt to set End before Start (End = 5.0 while Start = 29.5)
                propEnd.SetValue(slider, 5.0);
                Pump();
                Equal(30.0, (double)propEnd.GetValue(slider)!, "End must be clamped to Start + 0.5");

                // 3. Negative delta drag on End Thumb
                var methodEndDrag = sliderType.GetMethod("OnEndThumbDragDelta", BindingFlags.NonPublic | BindingFlags.Instance);
                if (methodEndDrag != null)
                {
                    methodEndDrag.Invoke(slider, [slider, new DragDeltaEventArgs(-600.0, 0.0)]);
                    Pump();
                    double curStart = (double)propStart.GetValue(slider)!;
                    double curEnd = (double)propEnd.GetValue(slider)!;
                    True(curEnd >= curStart + 0.5, "End thumb must not cross below Start + 0.5");
                }
            });
        });

        test("T2-03 §R2: Trimming from origin t=0.0s produces valid stream starting at PTS 0.000s", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateSampleClip(dir, "src_origin.mp4", 6.0, hasAudio: true);
            string target = Path.Combine(dir, "out_origin.mp4");

            int res = NativeTrimInterop.hr_trim(source, target, 0.0, 3.5, 0, null, IntPtr.Zero);
            Equal(0, res);
            True(File.Exists(target));

            var probe = ProbeMedia(target);
            InRange(probe.Duration, 3.0, 4.0);

            var vPackets = ReadPackets(target, "v:0");
            True(vPackets.Count > 0);
            True(Math.Abs(vPackets[0].PtsTime) < 0.05, $"First video packet PTS ({vPackets[0].PtsTime:F4}s) must be ~0.000s");
        }));

        test("T2-04 §R2: Trimming up to exact clip duration handles EOF cleanly without buffer overrun", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateSampleClip(dir, "src_eof.mp4", 6.0, hasAudio: true);
            string target = Path.Combine(dir, "out_eof.mp4");

            int res = NativeTrimInterop.hr_trim(source, target, 2.0, 6.0, 0, null, IntPtr.Zero);
            Equal(0, res);
            True(File.Exists(target));

            var probe = ProbeMedia(target);
            InRange(probe.Duration, 3.5, 4.5);
        }));

        test("T2-05 §R2: Narrow slice near end of long recording [58.0s, 59.5s] remuxes accurately", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateLongClip(dir, "src_long_near_end.mp4", 60.0);
            string target = Path.Combine(dir, "out_near_end.mp4");

            int res = NativeTrimInterop.hr_trim(source, target, 58.0, 59.5, 0, null, IntPtr.Zero);
            Equal(0, res);
            True(File.Exists(target));

            var probe = ProbeMedia(target);
            InRange(probe.Duration, 1.2, 1.8, "Narrow slice duration should be ~1.5s");
        }));

        testAsync("T2-06 §R1, §R2: Out-of-bounds, negative, NaN and Infinity timestamps rejected safely", async () =>
        {
            var asm = GetAppAssembly();
            var serviceType = asm.GetType("HuntReplay.App.Services.VideoTrimmingService")!;
            var instance = serviceType.GetProperty("Instance")!.GetValue(null)!;
            var trimMethod = serviceType.GetMethod("TrimVideoAsync")!;

            await InTempAsync(async dir =>
            {
                string source = GenerateSampleClip(dir, "src_invalid_ts.mp4", 5.0, hasAudio: true);
                string target = Path.Combine(dir, "out_ts.mp4");

                // Negative start
                var t1 = (Task<string>)trimMethod.Invoke(instance, [source, target, -1.0, 3.0, false, null, CancellationToken.None])!;
                await ThrowsAsync<ArgumentOutOfRangeException>(async () => await t1);

                // NaN start
                var t2 = (Task<string>)trimMethod.Invoke(instance, [source, target, double.NaN, 3.0, false, null, CancellationToken.None])!;
                await ThrowsAsync<ArgumentException>(async () => await t2);

                // Infinity end
                var t3 = (Task<string>)trimMethod.Invoke(instance, [source, target, 1.0, double.PositiveInfinity, false, null, CancellationToken.None])!;
                await ThrowsAsync<ArgumentException>(async () => await t3);

                // End <= Start
                var t4 = (Task<string>)trimMethod.Invoke(instance, [source, target, 3.0, 2.0, false, null, CancellationToken.None])!;
                await ThrowsAsync<ArgumentException>(async () => await t4);
            });
        });

        testAsync("T2-07 §R2: Identical source and target paths rejected to prevent overwriting source", async () =>
        {
            var asm = GetAppAssembly();
            var serviceType = asm.GetType("HuntReplay.App.Services.VideoTrimmingService")!;
            var instance = serviceType.GetProperty("Instance")!.GetValue(null)!;
            var trimMethod = serviceType.GetMethod("TrimVideoAsync")!;

            await InTempAsync(async dir =>
            {
                string source = GenerateSampleClip(dir, "src_same.mp4", 5.0, hasAudio: true);
                string initialHash = ComputeFileSha256(source);

                // Identical path
                var t1 = (Task<string>)trimMethod.Invoke(instance, [source, source, 1.0, 3.0, false, null, CancellationToken.None])!;
                await ThrowsAsync<InvalidOperationException>(async () => await t1);

                // Case-insensitive match on Windows
                string sameUpper = Path.Combine(dir, "SRC_SAME.MP4");
                var t2 = (Task<string>)trimMethod.Invoke(instance, [source, sameUpper, 1.0, 3.0, false, null, CancellationToken.None])!;
                await ThrowsAsync<InvalidOperationException>(async () => await t2);

                // Source must remain completely intact
                Equal(initialHash, ComputeFileSha256(source));
            });
        });

        testAsync("T2-08 §R2: Missing and empty source paths fail validation gracefully", async () =>
        {
            var asm = GetAppAssembly();
            var serviceType = asm.GetType("HuntReplay.App.Services.VideoTrimmingService")!;
            var instance = serviceType.GetProperty("Instance")!.GetValue(null)!;
            var trimMethod = serviceType.GetMethod("TrimVideoAsync")!;

            await InTempAsync(async dir =>
            {
                string target = Path.Combine(dir, "out_missing.mp4");

                // Empty source
                var t1 = (Task<string>)trimMethod.Invoke(instance, ["", target, 0.0, 3.0, false, null, CancellationToken.None])!;
                await ThrowsAsync<ArgumentException>(async () => await t1);

                // Missing source file
                var t2 = (Task<string>)trimMethod.Invoke(instance, [Path.Combine(dir, "nonexistent.mp4"), target, 0.0, 3.0, false, null, CancellationToken.None])!;
                await ThrowsAsync<FileNotFoundException>(async () => await t2);
            });
        });

        test("T2-09 §R2: Cancellation mid-flight aborts promptly and deletes partial files", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateSampleClip(dir, "src_cancel_stages.mp4", 12.0, hasAudio: true);
            string initialHash = ComputeFileSha256(source);

            // Stage 1: Immediate cancellation at 0%
            string target0 = Path.Combine(dir, "out_cancel_0.mp4");
            NativeTrimInterop.ProgressCallback cancel0 = (_, _) => 1;
            int res0 = NativeTrimInterop.hr_trim(source, target0, 1.0, 10.0, 0, cancel0, IntPtr.Zero);
            True(res0 < 0, "Early cancellation must return negative code");
            False(File.Exists(target0), "Partial file must be deleted");

            // Stage 2: Halfway cancellation at 50%
            string target50 = Path.Combine(dir, "out_cancel_50.mp4");
            var sw = Stopwatch.StartNew();
            NativeTrimInterop.ProgressCallback cancel50 = (_, prog) => prog >= 0.50 ? 1 : 0;
            int res50 = NativeTrimInterop.hr_trim(source, target50, 1.0, 10.0, 0, cancel50, IntPtr.Zero);
            sw.Stop();
            True(res50 < 0, "Mid-flight cancellation must return negative code");
            True(sw.ElapsedMilliseconds < 300, $"Cancellation abort took {sw.ElapsedMilliseconds}ms, must be < 300ms");
            False(File.Exists(target50), "Partial file must be deleted");

            // Stage 3: Late cancellation at 95%
            string target95 = Path.Combine(dir, "out_cancel_95.mp4");
            NativeTrimInterop.ProgressCallback cancel95 = (_, prog) => prog >= 0.95 ? 1 : 0;
            int res95 = NativeTrimInterop.hr_trim(source, target95, 1.0, 10.0, 0, cancel95, IntPtr.Zero);
            True(res95 < 0, "Late cancellation must return negative code");
            False(File.Exists(target95), "Partial file must be deleted");

            // Source file must remain completely unmodified
            Equal(initialHash, ComputeFileSha256(source));
        }));

        test("T2-10 §R2: Multi-audio 3-track preservation retains all distinct rates and channels", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateThreeAudioClip(dir, "src_3track.mp4", 8.0);
            string target = Path.Combine(dir, "out_3track.mp4");

            int res = NativeTrimInterop.hr_trim(source, target, 1.0, 6.0, 0, null, IntPtr.Zero);
            Equal(0, res);
            True(File.Exists(target));

            var probe = ProbeMedia(target);
            var aStreams = probe.Streams.FindAll(s => s.CodecType == "audio");

            Equal(3, aStreams.Count, "Target must preserve exactly 3 audio streams");
            Equal(48000, aStreams[0].SampleRate);
            Equal(2, aStreams[0].Channels);

            Equal(44100, aStreams[1].SampleRate);
            Equal(1, aStreams[1].Channels);

            Equal(32000, aStreams[2].SampleRate);
            Equal(2, aStreams[2].Channels);
        }));

        test("T2-11 §R2: Long recording 60s trimming maintains strictly monotonic packet DTS", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateLongClip(dir, "src_long_dts.mp4", 60.0);
            string target = Path.Combine(dir, "out_long_dts.mp4");

            int res = NativeTrimInterop.hr_trim(source, target, 10.0, 40.0, 0, null, IntPtr.Zero);
            Equal(0, res);

            var vPackets = ReadPackets(target, "v:0");
            var aPackets = ReadPackets(target, "a:0");

            True(vPackets.Count > 100);
            True(aPackets.Count > 100);

            // Verify video DTS monotonicity
            for (int i = 1; i < vPackets.Count; i++)
            {
                True(vPackets[i].DtsTime >= vPackets[i - 1].DtsTime,
                    $"Video packet {i} DTS non-monotonic: {vPackets[i].DtsTime} < {vPackets[i-1].DtsTime}");
            }

            // Verify audio DTS monotonicity
            for (int i = 1; i < aPackets.Count; i++)
            {
                True(aPackets[i].DtsTime >= aPackets[i - 1].DtsTime,
                    $"Audio packet {i} DTS non-monotonic: {aPackets[i].DtsTime} < {aPackets[i-1].DtsTime}");
            }
        }));

        test("T2-12 §R1: Translation dragging of range bar preserves duration window invariant", () =>
        {
            var asm = GetAppAssembly();
            var sliderType = asm.GetType("HuntReplay.App.Controls.RangeSlider")!;

            WithThemedWindow((_, host) =>
            {
                var slider = (FrameworkElement)Activator.CreateInstance(sliderType)!;
                slider.Width = 400;
                host.Children.Add(slider);
                Pump();

                var propMin = sliderType.GetProperty("Minimum")!;
                var propMax = sliderType.GetProperty("Maximum")!;
                var propStart = sliderType.GetProperty("RangeStart")!;
                var propEnd = sliderType.GetProperty("RangeEnd")!;
                var propMinRange = sliderType.GetProperty("MinRange")!;

                propMin.SetValue(slider, 0.0);
                propMax.SetValue(slider, 100.0);
                propMinRange.SetValue(slider, 0.5);
                propEnd.SetValue(slider, 20.0);
                propStart.SetValue(slider, 10.0); // Duration = 10.0s
                Pump();

                double initialDur = (double)propEnd.GetValue(slider)! - (double)propStart.GetValue(slider)!;
                Equal(10.0, initialDur);

                // Drag right by +15s
                var setRangeMethod = sliderType.GetMethod("SetRange");
                if (setRangeMethod != null)
                {
                    setRangeMethod.Invoke(slider, [25.0, 35.0]);
                }
                else
                {
                    propEnd.SetValue(slider, 35.0);
                    propStart.SetValue(slider, 25.0);
                }
                Pump();

                double endAfter = (double)propEnd.GetValue(slider)!;
                double startAfter = (double)propStart.GetValue(slider)!;
                Equal(25.0, startAfter);
                Equal(35.0, endAfter);
                Equal(initialDur, endAfter - startAfter, "Duration window must remain exactly 10.0s");
            });
        });

        test("T2-13 §R1: Reducing Maximum below current RangeEnd safely coerces bounds", () =>
        {
            var asm = GetAppAssembly();
            var sliderType = asm.GetType("HuntReplay.App.Controls.RangeSlider")!;

            WithThemedWindow((_, host) =>
            {
                var slider = (FrameworkElement)Activator.CreateInstance(sliderType)!;
                host.Children.Add(slider);
                Pump();

                var propMin = sliderType.GetProperty("Minimum")!;
                var propMax = sliderType.GetProperty("Maximum")!;
                var propStart = sliderType.GetProperty("RangeStart")!;
                var propEnd = sliderType.GetProperty("RangeEnd")!;

                propMin.SetValue(slider, 0.0);
                propMax.SetValue(slider, 100.0);
                propEnd.SetValue(slider, 80.0);
                propStart.SetValue(slider, 50.0);
                Pump();

                // Reduce Maximum to 30.0 (less than both RangeStart and RangeEnd)
                propMax.SetValue(slider, 30.0);
                Pump();

                double curStart = (double)propStart.GetValue(slider)!;
                double curEnd = (double)propEnd.GetValue(slider)!;
                double curMax = (double)propMax.GetValue(slider)!;

                True(curStart <= curEnd, "RangeStart must not exceed RangeEnd");
                True(curStart <= curMax, "RangeStart must not exceed Maximum");
                True(curEnd <= curMax, "RangeEnd must not exceed Maximum");
            });
        });
    }
}
