using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HuntReplay.Core.Storage;
using static HuntReplay.E2ETests.E2EInfrastructure;

namespace HuntReplay.E2ETests;

public static class Tier4_RealWorldScenariosTests
{
    public static void RunAll(Action<string, Action> test, Action<string, Func<Task>> testAsync)
    {
        Console.WriteLine("\n=======================================================");
        Console.WriteLine("  TIER 4: REAL-WORLD APPLICATION SCENARIOS");
        Console.WriteLine("=======================================================");

        testAsync("T4-01 §R1, §R2: Realistic game replay 30s recording trimmed to 7s highlight and playable in engine", async () =>
        {
            EnsureNativeLoaded();
            var asm = GetAppAssembly();
            var serviceType = asm.GetType("HuntReplay.App.Services.VideoTrimmingService")!;
            var instance = serviceType.GetProperty("Instance")!.GetValue(null)!;
            var trimMethod = serviceType.GetMethod("TrimVideoAsync")!;
            var namingMethod = serviceType.GetMethod("GenerateDefaultTargetPath")!;

            await InTempAsync(async dir =>
            {
                // 1. Generate realistic 30s match recording
                string source = GenerateSampleClip(dir, "Hunt_Match_2026-10-11.mp4", 30.0, hasAudio: true);
                string preHash = ComputeFileSha256(source);

                // 2. Compute non-colliding default target name
                string target = (string)namingMethod.Invoke(null, [source])!;
                True(target.Contains("_trimmed"), "Default name should contain '_trimmed'");

                // 3. User trims [15.0s, 22.0s] (7.0s kill highlight)
                var task = (Task<string>)trimMethod.Invoke(instance, [source, target, 15.0, 22.0, false, null, CancellationToken.None])!;
                string result = await task;

                Equal(target, result);
                True(File.Exists(result));

                // 4. Verify media probe
                var probe = ProbeMedia(result);
                InRange(probe.Duration, 6.5, 7.5, "Highlight duration should be ~7.0s");

                // 5. Verify playable in native player engine
                int openRes = NativeTrimInterop.hr_player_open(result, out IntPtr player, out int w, out int h, out double dur);
                Equal(0, openRes);
                try
                {
                    True(w > 0 && h > 0);
                    InRange(dur, 6.5, 7.5);

                    // Seek test
                    Equal(0, NativeTrimInterop.hr_player_seek(player, 2.0));

                    // Thumbnail generation
                    int thumbRes = NativeTrimInterop.hr_thumbnail(result, 320, out IntPtr pixels, out int tw, out int th, out int stride);
                    Equal(0, thumbRes);
                    try
                    {
                        True(pixels != IntPtr.Zero && tw > 0 && th > 0 && stride > 0);
                    }
                    finally
                    {
                        NativeTrimInterop.hr_free(pixels);
                    }
                }
                finally
                {
                    NativeTrimInterop.hr_player_close(player);
                }

                // 6. Source video file remains untouched
                Equal(preHash, ComputeFileSha256(source));
            });
        });

        test("T4-02 §R2: Gameplay clip with multi-track commentary (game sound + mic) preserves A/V sync", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            // Source clip with game audio (48kHz) and mic audio (44.1kHz)
            string source = GenerateMultiAudioClip(dir, "Hunt_Commentary.mp4", 15.0);
            string target = Path.Combine(dir, "Hunt_Commentary_highlight.mp4");

            // Extract 5-second highlight [4.0s, 9.0s]
            int res = NativeTrimInterop.hr_trim(source, target, 4.0, 9.0, 0, null, IntPtr.Zero);
            Equal(0, res);

            var probe = ProbeMedia(target);
            var aStreams = probe.Streams.FindAll(s => s.CodecType == "audio");

            Equal(2, aStreams.Count, "Must preserve both game audio and mic tracks");
            Equal(48000, aStreams[0].SampleRate);
            Equal(44100, aStreams[1].SampleRate);

            // Check A/V drift across packets
            var vPackets = ReadPackets(target, "v:0");
            var a0Packets = ReadPackets(target, "a:0");
            var a1Packets = ReadPackets(target, "a:1");

            double offset0 = Math.Abs(a0Packets[0].PtsTime - vPackets[0].PtsTime);
            double offset1 = Math.Abs(a1Packets[0].PtsTime - vPackets[0].PtsTime);

            True(offset0 < 0.025, $"Game audio offset ({offset0*1000:F2}ms) must be < 25ms");
            True(offset1 < 0.025, $"Mic audio offset ({offset1*1000:F2}ms) must be < 25ms");
        }));

        test("T4-03 §R2, §R3: Silent gameplay clip highlight extraction and thumbnail verification", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateSampleClip(dir, "Hunt_SilentReplay.mp4", 10.0, hasAudio: false);
            string target = Path.Combine(dir, "Hunt_Silent_highlight.mp4");

            int res = NativeTrimInterop.hr_trim(source, target, 2.0, 6.0, 0, null, IntPtr.Zero);
            Equal(0, res);

            var probe = ProbeMedia(target);
            InRange(probe.Duration, 3.5, 4.5);
            Equal(0, probe.Streams.FindAll(s => s.CodecType == "audio").Count);

            int thumbRes = NativeTrimInterop.hr_thumbnail(target, 240, out IntPtr pixels, out int tw, out int th, out int stride);
            Equal(0, thumbRes);
            try
            {
                True(pixels != IntPtr.Zero && tw > 0 && th > 0);
            }
            finally
            {
                NativeTrimInterop.hr_free(pixels);
            }
        }));

        testAsync("T4-04 §R1, §R2: Multi-highlight batch extraction from match recording registered in ClipLibrary", async () =>
        {
            EnsureNativeLoaded();
            var asm = GetAppAssembly();
            var serviceType = asm.GetType("HuntReplay.App.Services.VideoTrimmingService")!;
            var instance = serviceType.GetProperty("Instance")!.GetValue(null)!;
            var trimMethod = serviceType.GetMethod("TrimVideoAsync")!;
            var namingMethod = serviceType.GetMethod("GenerateDefaultTargetPath")!;

            await InTempAsync(async dir =>
            {
                string storeDir = Path.Combine(dir, "store");
                Directory.CreateDirectory(storeDir);
                var library = new ClipLibrary(dir, storeDir);

                string source = GenerateSampleClip(dir, "Match_Full_Session.mp4", 25.0, hasAudio: true);
                string preHash = ComputeFileSha256(source);

                // Highlight 1: First Kill [2.0s, 6.0s]
                string target1 = (string)namingMethod.Invoke(null, [source])!;
                await (Task<string>)trimMethod.Invoke(instance, [source, target1, 2.0, 6.0, false, null, CancellationToken.None])!;
                var clip1 = library.Register(target1, new ClipMetadata
                {
                    FileName = Path.GetFileName(target1),
                    EventTag = "FIRST_KILL",
                    DurationSeconds = 4.0,
                    CreatedAt = DateTimeOffset.Now
                });

                // Highlight 2: Double Kill [10.0s, 15.0s]
                string target2 = (string)namingMethod.Invoke(null, [source])!;
                await (Task<string>)trimMethod.Invoke(instance, [source, target2, 10.0, 15.0, false, null, CancellationToken.None])!;
                var clip2 = library.Register(target2, new ClipMetadata
                {
                    FileName = Path.GetFileName(target2),
                    EventTag = "MULTI_KILL",
                    DurationSeconds = 5.0,
                    CreatedAt = DateTimeOffset.Now
                });

                // Highlight 3: Extraction [18.0s, 23.0s]
                string target3 = (string)namingMethod.Invoke(null, [source])!;
                await (Task<string>)trimMethod.Invoke(instance, [source, target3, 18.0, 23.0, false, null, CancellationToken.None])!;
                var clip3 = library.Register(target3, new ClipMetadata
                {
                    FileName = Path.GetFileName(target3),
                    EventTag = "EXTRACTION",
                    DurationSeconds = 5.0,
                    CreatedAt = DateTimeOffset.Now
                });

                // Verify library inventory
                var clips = library.List();
                Equal(3, clips.Count, "Library should contain exactly 3 trimmed highlight clips");
                True(clips.Any(c => c.Metadata.EventTag == "FIRST_KILL"));
                True(clips.Any(c => c.Metadata.EventTag == "MULTI_KILL"));
                True(clips.Any(c => c.Metadata.EventTag == "EXTRACTION"));

                // Verify source file untouched
                Equal(preHash, ComputeFileSha256(source));
            });
        });

        test("T4-05 §R2, §R3: Long gameplay recording (60s) final clutch play extraction near EOF", () => InTemp(dir =>
        {
            EnsureNativeLoaded();
            string source = GenerateLongClip(dir, "Long_Recording_60s.mp4", 60.0);
            string target = Path.Combine(dir, "Clutch_Play_56_59.mp4");

            // Extract last 3 seconds [56.0s, 59.0s]
            int res = NativeTrimInterop.hr_trim(source, target, 56.0, 59.0, 0, null, IntPtr.Zero);
            Equal(0, res);
            True(File.Exists(target));

            var probe = ProbeMedia(target);
            InRange(probe.Duration, 2.5, 3.5, "Clutch highlight duration should be ~3.0s");

            // Verify clean native playback
            int openRes = NativeTrimInterop.hr_player_open(target, out IntPtr player, out int w, out int h, out double dur);
            Equal(0, openRes);
            try
            {
                True(w > 0 && h > 0);
                InRange(dur, 2.5, 3.5);
                Equal(0, NativeTrimInterop.hr_player_seek(player, 1.0));
            }
            finally
            {
                NativeTrimInterop.hr_player_close(player);
            }
        }));
    }
}
