using System.Buffers.Binary;
using System.Text.Json;
using Google.Protobuf;
using HuntReplay.Core.Configuration;
using HuntReplay.Core.Media;
using HuntReplay.Core.Replay;
using HuntReplay.Core.Storage;
using HuntReplay.Protocol;
using HuntReplay.Protocol.Generated;

using HuntReplay.TestShared;

namespace HuntReplay.Tests;

/// <summary>Every registered core test as its own MSTest case.</summary>
[TestClass]
public sealed class CoreSuite
{
    public static IEnumerable<object[]> Cases => TestRegistry.Rows(Program.RegisterAll);
    public static string CaseName(System.Reflection.MethodInfo method, object[] row) => (string)row[0];

    [TestMethod]
    [DynamicData(nameof(Cases), DynamicDataDisplayName = nameof(CaseName))]
    public Task Run(string name) => TestRegistry.Run(name);
}

public static partial class Program
{
    /// <summary>Records every core test with <see cref="TestRegistry"/>; <see cref="CoreSuite"/> runs them under MSTest.</summary>
    internal static void RegisterAll()
    {
        Test("defaults satisfy the settings contract", () => Settings().Validate());
        Test("unknown enum rejected", () => { var s = Settings(); s.Video.Codec = (VideoCodec)99; Throws<InvalidDataException>(s.Validate); });
        Test("NaN post-roll rejected", () => { var s = Settings(); s.Replay.PostEventSeconds = double.NaN; Throws<InvalidDataException>(s.Validate); });
        Test("unsupported FPS rejected", () => { var s = Settings(); s.Video.Fps = 59; Throws<InvalidDataException>(s.Validate); });
        Test("older resolution names still load", () => Equal(VideoResolution.Qhd, JsonSerializer.Deserialize<StudioSettings>("""{"Video":{"Resolution":"Qhd"}}""", StudioSettings.JsonOptions)!.Video.Resolution));
        Test("resolution presets cover every enum value except Native", () => Equal(Enum.GetValues<VideoResolution>().Length - 1, VideoResolutions.Presets.Distinct().Count()));
        Test("resolution presets ascend by height", () => True(VideoResolutions.Presets.Zip(VideoResolutions.Presets.Skip(1)).All(p => p.First.Height() < p.Second.Height())));
        Test("resolution preset keeps the source aspect ratio", () =>
        {
            Equal((1920, 1080), VideoResolution.FullHd.OutputSize(2560, 1440));
            Equal((2580, 1080), VideoResolution.FullHd.OutputSize(3440, 1440));
            Equal((1728, 1080), VideoResolution.FullHd.OutputSize(1920, 1200));
            Equal((1280, 720), VideoResolution.P720.OutputSize(1366, 768)); // 1280.6 rounds to 1281, then down to even.
        });
        Test("resolution preset never scales the source up", () =>
        {
            Equal((1920, 1080), VideoResolution.Uhd.OutputSize(1920, 1080));
            Equal((1920, 1080), VideoResolution.FullHd.OutputSize(1920, 1080));
            Equal((1366, 768), VideoResolution.Native.OutputSize(1367, 769));
        });
        Test("overlay is off until enabled", () => False(Settings().System.OverlayEnabled));
        Test("unsupported overlay duration rejected", () => { var s = Settings(); s.System.OverlaySeconds = 4; Throws<InvalidDataException>(s.Validate); });
        Test("unknown overlay corner rejected", () => { var s = Settings(); s.System.OverlayCorner = (OverlayCorner)9; Throws<InvalidDataException>(s.Validate); });
        Test("x264 is explicit and H264-only", () => { var s = Settings(); s.Video.Encoder = VideoEncoder.X264; s.Video.Codec = VideoCodec.Hevc; Throws<InvalidDataException>(s.Validate); });
        Test("empty event selection disables both events", () => { var r = new ReplaySettings { HunterKilled = false, PlayerDowned = false }; False(r.IsEnabled("HUNTER_KILLED")); False(r.IsEnabled("PLAYER_DOWNED")); });
        Test("safe template and multi-kill name", () => Equal("Hunt_MULTI_KILL_x2_2026-10-08_12-13-14-000.mp4", ClipFileName.Create("Hunt_{event}_{date}_{time}.mp4", "MULTI_KILL_x2", new DateTimeOffset(2026,10,8,12,13,14,TimeSpan.Zero))));
        Test("template traversal rejected", () => Throws<InvalidDataException>(() => ClipFileName.ValidateTemplate("../{event}.mp4")));
        Test("unknown filename tokens rejected", () => Throws<InvalidDataException>(() => ClipFileName.ValidateTemplate("Hunt_{pid}.mp4")));
        Test("Windows reserved file names rejected on every OS", () => { False(ClipFileName.IsSafeName("CON.mp4")); False(ClipFileName.IsSafeName("LPT1.mp4")); False(ClipFileName.IsSafeName("x.mp4 ")); });
        Test("untrusted event names cannot become paths", () => False(ClipFileName.Create("{event}.mp4", "../../escape:C", DateTimeOffset.Now).Contains('/')));
        Test("atomic settings save and backup", () => InTemp(path => { var store = new SettingsStore(Path.Combine(path,"config.json")); var s = Settings(); store.Save(s); s.Video.BitrateMbps = 40; store.Save(s); Equal(40,store.Load().Video.BitrateMbps); True(File.Exists(store.FilePath+".bak")); Equal(0, Directory.GetFiles(path,"*.tmp").Length); }));
        Test("corrupt config is not overwritten on load", () => InTemp(path => { var file = Path.Combine(path,"config.json"); File.WriteAllText(file,"{bad"); var store = new SettingsStore(file); _ = store.Load(); True(store.LoadWarning is not null); Equal("{bad",File.ReadAllText(file)); }));
        Test("future config schema is not silently accepted", () => InTemp(path => { var file = Path.Combine(path,"config.json"); var s = Settings(); s.SchemaVersion=42; File.WriteAllText(file,JsonSerializer.Serialize(s)); var store = new SettingsStore(file); _ = store.Load(); True(store.LoadWarning is not null); }));

        Test("empty buffer has no snapshot", () => True(new PacketRingBuffer(60,1000).Snapshot(0,1_000_000) is null));
        Test("P frames before first IDR are ignored", () => { var ring = new PacketRingBuffer(60,1000); ring.Add(Video(1,false)); ring.Add(Audio(1)); Equal(0L,ring.Status.Bytes); });
        Test("smart trim starts on the previous keyframe", () => { var ring = Filled(); var snap = ring.Snapshot(5_500_000,9_000_000)!; Equal(4_000_000L,snap.StartUs); True(snap.Packets.First(p=>p.IsVideo).IsKeyFrame); False(snap.TruncatedStart); });
        Test("export excludes future video and aligns audio", () => { var snap=Filled().Snapshot(5_500_000,9_000_000)!; True(snap.Packets.All(p=>p.DtsUs>=snap.StartUs && p.PtsUs<9_000_000)); Equal(9_000_000L,snap.EndUs); });
        Test("per-stream ordering accepts interleaved callbacks", () => { var r=new PacketRingBuffer(60,10000); r.Add(Video(0,true)); r.Add(Video(1,false)); r.Add(Audio(0)); r.Add(Audio(1)); Equal(4,r.Snapshot(0,2_000_000)!.Packets.Count); });
        Test("nonmonotonic encoder DTS rejected", () => { var r=Filled(); Throws<InvalidDataException>(()=>r.Add(Video(1,false))); });
        Test("duplicate encoder DTS rejected rather than eviction loop", () => { var r=new PacketRingBuffer(60,1000); r.Add(Video(0,true)); Throws<InvalidDataException>(()=>r.Add(Video(0,true))); });
        Test("time eviction preserves a complete leading GOP", () => { var r=new PacketRingBuffer(5,10000); for(int i=0;i<20;i++)r.Add(Video(i,i%2==0)); Equal(14_000_000L,r.Status.FirstUs); True(r.Snapshot(0,20_000_000)!.TruncatedStart); });
        Test("pending sequence lease survives normal eviction", () => { var r=new PacketRingBuffer(5,10000); for(int i=0;i<6;i++)r.Add(Video(i,i%2==0)); var pin=r.Pin(2_000_000); for(int i=6;i<20;i++)r.Add(Video(i,i%2==0)); Equal(2_000_000L,r.Status.FirstUs); r.Unpin(pin); Equal(14_000_000L,r.Status.FirstUs); });
        Test("hard RAM limit overrides leases at GOP boundaries", () => { var r=new PacketRingBuffer(300,24); var pin=r.Pin(0); for(int i=0;i<20;i++)r.Add(Video(i,i%2==0)); True(r.Status.Bytes<=24); True(r.Status.EvictedGops>0); True(r.Snapshot(0,20_000_000)!.Packets.First().IsKeyFrame); r.Unpin(pin); });
        Test("oversized GOP is dropped until the next IDR", () => { var r=new PacketRingBuffer(60,2); r.Add(Video(0,true)); Equal(0L,r.Status.Bytes); r.Add(Video(1,false)); Equal(0L,r.Status.Bytes); });
        Test("snapshots retain immutable payloads after eviction", () => { var r=new PacketRingBuffer(5,10000); r.Add(Video(0,true)); var s=r.Snapshot(0,1_000_000)!; for(int i=1;i<50;i++)r.Add(Video(i,i%2==0)); Equal(0L,s.StartUs); Equal(4,s.Packets[0].Payload.Length); });
        Test("early game exit flags truncated post-roll", () => True(Filled().Snapshot(0,50_000_000)!.TruncatedEnd));
        Test("concurrent capture/snapshots are safe", () => { var r=new PacketRingBuffer(15,100000); Parallel.Invoke(()=>{for(int i=0;i<2000;i++) r.Add(Video(i,i%2==0));},()=>{for(int i=0;i<1000;i++){var s=r.Status;_ = r.Snapshot(0,s.LatestUs);}}); True(r.Status.Bytes<=100000); });

        Test("post-roll and coalesce horizon are separate", () => { var p=new HighlightPlanner(new()); var plan=p.Register(Event("one",100))!; Equal(107_000_000L,plan.EndUs); Equal(115_000_000L,plan.ReadyAtUs); Equal(0,p.TakeReady(107_000_000).Count); Equal(1,p.TakeReady(115_000_000).Count); });
        Test("kills 12 seconds apart form one clip", () => { var p=new HighlightPlanner(new()); var a=p.Register(Event("a",100))!; var b=p.Register(Event("b",112))!; Equal(a.Id,b.Id); Equal("MULTI_KILL_x2",b.EventTag); Equal(80_000_000L,b.StartUs); Equal(119_000_000L,b.EndUs); Equal(127_000_000L,b.ReadyAtUs); });
        Test("exact 15-second boundary merges", () => { var p=new HighlightPlanner(new()); var a=p.Register(Event("a",100))!; Equal(a.Id,p.Register(Event("b",115))!.Id); });
        Test("outside coalesce gap becomes a new clip", () => { var p=new HighlightPlanner(new()); var a=p.Register(Event("a",100))!; False(a.Id==p.Register(Event("b",116))!.Id); });
        Test("different matches never coalesce", () => { var p=new HighlightPlanner(new()); var a=p.Register(Event("a",100))!; False(a.Id==p.Register(Event("b",101) with{GroupId="other"})!.Id); });
        Test("different game clients never coalesce", () => { var p=new HighlightPlanner(new()); var a=p.Register(Event("a",100))!; False(a.Id==p.Register(Event("b",101) with{SessionId="other"})!.Id); });
        Test("retransmitted request never doubles the kill count", () => { var p=new HighlightPlanner(new()); var e=Event("same",100); var a=p.Register(e)!; True(p.Register(e) is null); Equal(1,a.KillCount); });
        Test("disabled coalescing saves at post-roll", () => { var p=new HighlightPlanner(new(){Coalesce=false}); var a=p.Register(Event("a",100))!; Equal(107_000_000L,a.ReadyAtUs); False(a.Id==p.Register(Event("b",101))!.Id); });
        Test("continuous kill chain has a maximum sequence length", () => { var p=new HighlightPlanner(new()); var a=p.Register(Event("0",100))!; for(int i=1;i<=6;i++)Equal(a.Id,p.Register(Event(i.ToString(),100+i*10))!.Id); False(a.Id==p.Register(Event("7",170))!.Id); });
        Test("signed int32 startDelta cannot overflow", () => { var p=new HighlightPlanner(new()); Equal(40_000_000L,p.Register(Event("a",100) with{StartDeltaMs=int.MinValue})!.StartUs); });
        Test("untrusted endDelta is capped at 30 seconds", () => { var p=new HighlightPlanner(new()); Equal(130_000_000L,p.Register(Event("a",100) with{EndDeltaMs=int.MaxValue})!.EndUs); });
        Test("bounded pending plans reject event floods", () => { var p=new HighlightPlanner(new(){Coalesce=false}); for(int i=0;i<8;i++)p.Register(Event(i.ToString(),100+i)); Throws<InvalidOperationException>(()=>p.Register(Event("overflow",109))); });
        Test("export backpressure retains unconsumed pending plans", () =>
        {
            var p = new HighlightPlanner(new() { Coalesce = false });
            for (int i=0; i<8; i++) p.Register(Event(i.ToString(),100+i));
            Equal(2, p.TakeReady(999_000_000, maximum: 2).Count); Equal(6, p.PendingCount);
            Equal(6, p.TakeReady(999_000_000, force: true).Count); Equal(0, p.PendingCount);
        });
        Test("force flush returns each pending plan only once", () => { var p=new HighlightPlanner(new()); p.Register(Event("a",100)); Equal(1,p.TakeReady(101_000_000,true).Count); Equal(0,p.TakeReady(999_000_000,true).Count); });

        Test("library rename moves MP4 and keeps the same record Id", () => InLibrary((path, store) => { var l=new ClipLibrary(path+Path.DirectorySeparatorChar, store); var c=CreateClip(l,"a.mp4",10); var r=l.Rename(c,"b.mp4"); True(File.Exists(r.FullPath)); False(File.Exists(c.FullPath)); Equal(c.Metadata.Id,l.List().Single().Metadata.Id); }));
        Test("library traversal and reserved names rejected", () => InLibrary((path, store) =>{var l=new ClipLibrary(path, store);var c=CreateClip(l,"a.mp4",10);Throws<InvalidDataException>(()=>l.Rename(c,"../out.mp4"));Throws<InvalidDataException>(()=>l.Rename(c,"AUX.mp4"));}));
        Test("unmanaged files are never deleted", () => InLibrary((path, store) =>{var l=new ClipLibrary(path, store);var c=CreateClip(l,"a.mp4",10);File.Delete(c.RecordPath);Throws<IOException>(()=>l.Delete(c));True(File.Exists(c.FullPath));}));
        Test("bulk delete skips a failing clip and deletes the rest", () => InLibrary((path, store) =>{var l=new ClipLibrary(path, store);var a=CreateClip(l,"a.mp4",10);var b=CreateClip(l,"b.mp4",10);var c=CreateClip(l,"c.mp4",10);File.Delete(b.RecordPath);var failed=l.DeleteEach([a,b,c]);Equal(b.FullPath,failed.Single().Clip.FullPath);True(failed.Single().Error is IOException);False(File.Exists(a.FullPath));True(File.Exists(b.FullPath));False(File.Exists(c.FullPath));}));
        Test("retention protects current clip and unrelated files",() => InLibrary((path, store) =>{var l=new ClipLibrary(path, store);var oldest=CreateClip(l,"old.mp4",8,DateTimeOffset.Now.AddDays(-2));var newest=CreateClip(l,"new.mp4",8);File.WriteAllText(Path.Combine(path,"unrelated.mp4"),"not-owned");Equal(1,l.EnforceByteLimit(15,new HashSet<Guid>{newest.Metadata.Id}));False(File.Exists(oldest.FullPath));True(File.Exists(newest.FullPath));True(File.Exists(Path.Combine(path,"unrelated.mp4")));}));
        Test("unlimited retention deletes nothing", () => InLibrary((path, store) =>{var l=new ClipLibrary(path, store);CreateClip(l,"a.mp4",10);Equal(0,l.EnforceLimit(new(){Directory=path,AutoCleanup=true,LimitGb=0}));}));
        Test("bad sidecar cannot hide healthy clips", () => InLibrary((path, store) => { var l = new ClipLibrary(path, store); CreateClip(l, "a.mp4", 10); File.WriteAllText(Path.Combine(path, "bad.mp4.hunt.json"), "garbage"); Equal(1, l.List().Count); l.Maintain(); Equal(1, l.List().Count); True(File.Exists(Path.Combine(path, "bad.mp4.hunt.json"))); }));
        Test("clips folder holds only MP4 files through register, rename and delete", () => InLibrary((path, store) =>
        {
            var l = new ClipLibrary(path, store); var c = CreateClip(l, "a.mp4", 10);
            File.WriteAllBytes(c.ThumbnailPath, [1, 2, 3]);
            Equal("a.mp4", Names(path));
            False(c.RecordPath.StartsWith(path, StringComparison.OrdinalIgnoreCase));
            False(c.ThumbnailPath.StartsWith(path, StringComparison.OrdinalIgnoreCase));
            var r = l.Rename(c, "b.mp4");
            Equal("b.mp4", Names(path)); Equal(c.ThumbnailPath, r.ThumbnailPath); True(File.Exists(r.ThumbnailPath));
            l.Delete(r);
            Equal("", Names(path)); False(File.Exists(r.RecordPath)); False(File.Exists(r.ThumbnailPath));
            Equal(0, Directory.GetFiles(store, "*", SearchOption.AllDirectories).Length);
        }));
        Test("0.1.2 sidecars are adopted into the store and leave the clips folder", () => InLibrary((path, store) =>
        {
            var legacy = WriteLegacyClip(path, "old.mp4");
            var l = new ClipLibrary(path, store);
            Equal(1, l.List().Count);
            var report = l.Maintain();
            Equal(1, report.Adopted); Equal("old.mp4", Names(path));
            var clip = l.List().Single();
            Equal(legacy.Id, clip.Metadata.Id); Equal(3L, new FileInfo(clip.ThumbnailPath).Length); True(File.Exists(clip.RecordPath));
            Equal(0, l.Maintain().Adopted);
        }));
        Test("0.1.2 clip stays visible and deletable before maintenance", () => InLibrary((path, store) =>
        {
            WriteLegacyClip(path, "old.mp4");
            var l = new ClipLibrary(path, store);
            l.Delete(l.List().Single());
            Equal("", Names(path)); Equal(0, l.List().Count);
        }));
        Test("copied clip with a duplicate Id receives a fresh record Id", () => InLibrary((path, store) =>
        {
            var first = Path.Combine(path, "First"); var second = Path.Combine(path, "Second");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            var original = CreateClip(new ClipLibrary(first, store), "x.mp4", 10);
            var copy = WriteLegacyClip(second, "x.mp4") with { Id = original.Metadata.Id };
            File.WriteAllText(Path.Combine(second, "x.mp4") + ".hunt.json", JsonSerializer.Serialize(copy, StudioSettings.JsonOptions));
            var l = new ClipLibrary(second, store);
            Equal(1, l.Maintain().Adopted);
            False(l.List().Single().Metadata.Id == original.Metadata.Id);
            Equal(original.Metadata.Id, new ClipLibrary(first, store).List().Single().Metadata.Id);
        }));
        Test("records follow a renamed clips folder after maintenance", () => InLibrary((path, store) =>
        {
            var before = Path.Combine(path, "Clips"); Directory.CreateDirectory(before);
            var clip = CreateClip(new ClipLibrary(before, store), "a.mp4", 10);
            var after = Path.Combine(path, "Moved"); Directory.Move(before, after);
            var l = new ClipLibrary(after, store);
            Equal(1, l.Maintain().Relocated);
            Equal(clip.Metadata.Id, l.List().Single().Metadata.Id);
        }));
        Test("maintenance prunes records of deleted MP4 files but keeps other clips folders", () => InLibrary((path, store) =>
        {
            var first = Path.Combine(path, "First"); var second = Path.Combine(path, "Second");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            var lf = new ClipLibrary(first, store); var ls = new ClipLibrary(second, store);
            var gone = CreateClip(lf, "gone.mp4", 10); File.WriteAllBytes(gone.ThumbnailPath, [1]);
            var kept = CreateClip(ls, "kept.mp4", 10);
            File.Delete(gone.FullPath);
            var report = lf.Maintain();
            Equal(1, report.Pruned); Equal(0, report.Relocated);
            False(File.Exists(gone.RecordPath)); False(File.Exists(gone.ThumbnailPath));
            True(File.Exists(kept.RecordPath)); Equal(1, ls.List().Count);
        }));
        Test("thumbnails without a record are removed", () => InLibrary((path, store) =>
        {
            var l = new ClipLibrary(path, store); l.Maintain();
            var orphan = Path.Combine(store, "thumbs", Guid.NewGuid().ToString("D") + ".png");
            File.WriteAllBytes(orphan, [1]);
            Equal(1, l.Maintain().Pruned); False(File.Exists(orphan));
        }));
        Test("maintenance removes 0.1.2 leftovers of deleted MP4 files and keeps foreign or broken files", () => InLibrary((path, store) =>
        {
            WriteLegacyClip(path, "ghost.mp4"); File.Delete(Path.Combine(path, "ghost.mp4"));
            File.WriteAllText(Path.Combine(path, "bad.mp4.hunt.json"), "garbage");
            File.WriteAllText(Path.Combine(path, "notes.txt"), "mine");
            File.WriteAllBytes(Path.Combine(path, "unrelated.mp4"), [1, 2]);
            var l = new ClipLibrary(path, store);
            Equal(1, l.Maintain().Pruned);
            Equal("bad.mp4.hunt.json,notes.txt,unrelated.mp4", Names(path)); Equal(0, l.List().Count);
        }));
        Test("stale temporary files are removed and fresh ones are kept", () => InLibrary((path, store) =>
        {
            var l = new ClipLibrary(path, store); l.Maintain();
            var stale = Path.Combine(store, "records", "old.json.1.tmp"); File.WriteAllText(stale, "x"); File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
            var fresh = Path.Combine(store, "records", "new.json.2.tmp"); File.WriteAllText(fresh, "x");
            l.Maintain();
            False(File.Exists(stale)); True(File.Exists(fresh));
        }));
        Test("interrupted exports are removed from the clips folder once stale", () => InLibrary((path, store) =>
        {
            var l = new ClipLibrary(path, store);
            var stale = Path.Combine(path, "Hunt_A.mp4.partial.mp4"); File.WriteAllText(stale, "x"); File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
            var trim = Path.Combine(path, "Hunt_B_trimmed.mp4.1a2b3c4d.partial.mp4"); File.WriteAllText(trim, "x"); File.SetLastWriteTimeUtc(trim, DateTime.UtcNow.AddHours(-2));
            var running = Path.Combine(path, "Hunt_C.mp4.partial.mp4"); File.WriteAllText(running, "x");
            var clip = Path.Combine(path, "Hunt_D.mp4"); File.WriteAllText(clip, "x"); File.SetLastWriteTimeUtc(clip, DateTime.UtcNow.AddHours(-2));
            l.Maintain();
            False(File.Exists(stale)); False(File.Exists(trim)); True(File.Exists(running)); True(File.Exists(clip));
        }));
        Test("0.1.2 copy with a duplicate Id cannot delete the original's record or thumbnail", () => InLibrary((path, store) =>
        {
            var first = Path.Combine(path, "First"); var second = Path.Combine(path, "Second");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            var lf = new ClipLibrary(first, store);
            var original = CreateClip(lf, "x.mp4", 10); File.WriteAllBytes(original.ThumbnailPath, [4, 5]);
            var copy = WriteLegacyClip(second, "x.mp4") with { Id = original.Metadata.Id };
            File.WriteAllText(Path.Combine(second, "x.mp4") + ".hunt.json", JsonSerializer.Serialize(copy, StudioSettings.JsonOptions));
            var ls = new ClipLibrary(second, store);
            ls.Delete(ls.List().Single());             // worst case: no maintenance before the delete
            True(File.Exists(original.RecordPath)); True(File.Exists(original.ThumbnailPath));
            Equal(original.Metadata.Id, lf.List().Single().Metadata.Id); Equal("", Names(second));
        }));
        Test("renaming a 0.1.2 copy with a duplicate Id leaves the original record alone", () => InLibrary((path, store) =>
        {
            var first = Path.Combine(path, "First"); var second = Path.Combine(path, "Second");
            Directory.CreateDirectory(first); Directory.CreateDirectory(second);
            var lf = new ClipLibrary(first, store);
            var original = CreateClip(lf, "x.mp4", 10);
            var copy = WriteLegacyClip(second, "x.mp4") with { Id = original.Metadata.Id };
            File.WriteAllText(Path.Combine(second, "x.mp4") + ".hunt.json", JsonSerializer.Serialize(copy, StudioSettings.JsonOptions));
            var ls = new ClipLibrary(second, store);
            var renamed = ls.Rename(ls.List().Single(), "y.mp4");
            False(renamed.Metadata.Id == original.Metadata.Id); True(File.Exists(renamed.RecordPath));
            Equal(original.Metadata.Id, lf.List().Single().Metadata.Id); True(File.Exists(original.RecordPath));
            Equal("y.mp4", Names(second));
        }));
        Test("rename refuses a name still used by a 0.1.2 sidecar", () => InLibrary((path, store) =>
        {
            var l = new ClipLibrary(path, store); var c = CreateClip(l, "a.mp4", 10);
            File.WriteAllText(Path.Combine(path, "b.mp4.hunt.json"), "{}");
            Throws<IOException>(() => l.Rename(c, "b.mp4"));
            True(File.Exists(c.FullPath)); Equal("a.mp4,b.mp4.hunt.json", Names(path));
        }));

        TestAsync("framing supports short reads and big endian length", async()=> { var bus=Join(); using var output=new MemoryStream(); await BusFraming.WriteAsync(output,bus,default); var bytes=output.ToArray(); Equal(bus.CalculateSize(),(int)BinaryPrimitives.ReadUInt32BigEndian(bytes)); using var fragmented=new ShortReadStream(bytes); Equal("join",(await BusFraming.ReadAsync(fragmented,default))!.Uniqueid); });
        TestAsync("clean EOF is not malformed input",async()=>{using var stream=new MemoryStream();True(await BusFraming.ReadAsync(stream,default) is null);});
        TestAsync("incomplete header rejected",async()=>{using var stream=new MemoryStream(new byte[]{0,0});await ThrowsAsync<EndOfStreamException>(async()=>{await BusFraming.ReadAsync(stream,default);});});
        TestAsync("oversized frame rejected before allocation",async()=>{var header=new byte[4];BinaryPrimitives.WriteUInt32BigEndian(header,BusFraming.MaximumMessageBytes+1u);using var stream=new MemoryStream(header);await ThrowsAsync<InvalidDataException>(async()=>{await BusFraming.ReadAsync(stream,default);});});
        TestAsync("zero length frame rejected",async()=>{using var stream=new MemoryStream(new byte[4]);await ThrowsAsync<InvalidDataException>(async()=>{await BusFraming.ReadAsync(stream,default);});});
        TestAsync("truncated payload rejected",async()=>{using var stream=new MemoryStream(new byte[]{0,0,0,5,1});await ThrowsAsync<EndOfStreamException>(async()=>{await BusFraming.ReadAsync(stream,default);});});
        Test("Python protobuf golden sequence matches C# SDK", ProtocolGolden);
        Test("bus join confirms NvNode SDKAPI peer and correlation",()=> {var s=Session();var replies=s.Process(Join());Equal(2,replies.Count);Equal("join",replies[0].ReferenceUniqueid);Equal("NvNode",replies[0].BusPeer[0].System);Equal("SDKAPI",replies[0].BusPeer[0].Module);True(replies[1].Joined);});
        Test("SDK requests require a JOIN",()=>Throws<InvalidDataException>(()=>Session().Process(Request("create",new(){CreateSDKInstanceRequest=new(){Client="Hunt",ClientPID=42}}))));
        Test("SDK spoofed PID is not authorized",()=>{var s=Session();s.Process(Join());Equal(-1,Response(s.Process(Request("create",new(){CreateSDKInstanceRequest=new(){Client="Hunt",ClientPID=99}}))).Retval);});
        Test("unknown IPC versions fail rather than fake success",()=>{var s=Session();s.Process(Join());var r=Request("create",new(){CreateSDKInstanceRequest=new(){Client="Hunt",ClientPID=42}});var m=SDKIPCMessage.Parser.ParseFrom(r.Generic.Data);m.Request.VersionMajor=99;r.Generic.Data=m.ToByteString();Equal(-1,Response(s.Process(r)).Retval);});
        Test("unimplemented screenshot API returns explicit failure",()=>{var s=Authorized();Equal(-1,Response(s.Process(Request("screen",new(){SetScreenshotHighlightRequest=new(){GroupId="g",HighlightId="id"}}))).Retval);});
        Test("IPC retransmits cached reply without re-triggering",()=>{int called=0;var s=Session(_=>{called++;return true;});foreach(var row in Fixtures().Take(5))s.Process(row);var evt=Fixtures()[4];s.Process(evt);Equal(1,called);});
        Test("separate SDK sessions do not share group counts",()=>{var first=Session();foreach(var row in Fixtures())first.Process(row);var second=Authorized();Equal(0,Response(second.Process(Request("c",new(){GetNumberOfHighlightsInGroupRequest=new(){GroupId="match-1896"}}))).GetNumberOfHighlightsInGroupResponse.NumberOfHighlights);});
        Test("reused SDK groupId starts a new match epoch", () =>
        {
            var events = new List<HighlightEvent>(); var s = Session(e => { events.Add(e); return true; });
            foreach (var row in Fixtures()) s.Process(row);
            s.Process(Request("new-open", new() { OpenGroupRequest = new() { GroupId = "match-1896" } }));
            s.Process(Request("new-kill", new() { SetVideoHighlightRequest = new() { GroupId = "match-1896", HighlightId = "HUNTER_KILLED" } }));
            Equal(2, events.Count); False(events[0].SessionId == events[1].SessionId);
            Equal(1, Response(s.Process(Request("new-count", new() { GetNumberOfHighlightsInGroupRequest = new() { GroupId = "match-1896" } }))).GetNumberOfHighlightsInGroupResponse.NumberOfHighlights);
        });
        Test("one pending series cannot grow without bounds", () =>
        {
            var p = new HighlightPlanner(new());
            for (int i=0; i<256; i++) p.Register(Event(i.ToString(),100));
            Throws<InvalidOperationException>(() => p.Register(Event("too-many",100)));
        });
        Test("library accepts uppercase MP4 extension", () => InLibrary((path, store) => { var l=new ClipLibrary(path,store); CreateClip(l,"clip.MP4",8); Equal(1,l.List().Count); }));
        Test("invalid metadata duration is excluded safely", () => InLibrary((path, store) =>
        {
            var l = new ClipLibrary(path, store); var c = CreateClip(l,"a.mp4",8);
            File.WriteAllText(c.RecordPath, JsonSerializer.Serialize(new { Folder = l.Root, Clip = c.Metadata with { DurationSeconds = -1 } }, StudioSettings.JsonOptions));
            Equal(0,l.List().Count); True(File.Exists(c.FullPath));
        }));
        RuntimeRegressionTests();
        IpcCorrelationTests();
        DeviceSelectionTests();
        PlaybackDiagnosticsTests();
        LocalizationTests();
        UpdateTests();
        PayloadArenaTests();
        VideoTrimmingTestsAsync().GetAwaiter().GetResult(); // Registers only; trim bodies run later as MSTest cases.
    }

    private static StudioSettings Settings() { var s=new StudioSettings();s.Storage.Directory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"HuntReplay-tests"));return s; }
    private static EncodedPacket Video(int second,bool key)=>new(0,new byte[]{1,2,3,4},second*1_000_000L,second*1_000_000L,1_000_000,key);
    private static EncodedPacket Audio(int second)=>new(1,new byte[]{5,6},second*1_000_000L,second*1_000_000L,1_000_000,false);
    private static PacketRingBuffer Filled() {var ring=new PacketRingBuffer(60,10000);for(int i=0;i<20;i++){ring.Add(Video(i,i%2==0));ring.Add(Audio(i));}return ring;}
    private static HighlightEvent Event(string id,int second)=>new("s",id,"g","HUNTER_KILLED",second*1_000_000L,-20000,5000);
    private static void InTemp(Action<string> test) {var path=Path.Combine(Path.GetTempPath(),"HuntReplay-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);try{test(path);}finally{Directory.Delete(path,true);}}
    private static void InLibrary(Action<string, string> test)
    {
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), "HuntReplay-test-" + id);
        var store = Path.Combine(Path.GetTempPath(), "HuntReplay-test-store-" + id);
        Directory.CreateDirectory(path);
        try { test(path, store); }
        finally { Directory.Delete(path, true); if (Directory.Exists(store)) Directory.Delete(store, true); }
    }
    private static string Names(string directory) => string.Join(",", Directory.GetFiles(directory).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal));
    private static ClipMetadata WriteLegacyClip(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        using (var file = File.Create(path)) file.SetLength(12);
        var metadata = new ClipMetadata { FileName = name, EventTag = "HUNTER_KILLED", DurationSeconds = 9, CreatedAt = DateTimeOffset.Now.AddHours(-1) };
        File.WriteAllText(path + ".hunt.json", JsonSerializer.Serialize(metadata, StudioSettings.JsonOptions));
        File.WriteAllBytes(path + ".thumb.png", [9, 8, 7]);
        return metadata;
    }
    private static LibraryClip CreateClip(ClipLibrary library,string name,long size,DateTimeOffset? when=null)
    {var path=Path.Combine(library.Root,name);using(var file=File.Create(path))file.SetLength(size);return library.Register(path,new(){FileName=name,EventTag="TEST",CreatedAt=when??DateTimeOffset.Now,DurationSeconds=1});}
    private static BusMessage Join()=>new(){Uniqueid="join",SourceSystem="Hunt1896",SourceModule="GfeSDK",Joined=true};
    private static BusMessage Request(string id,SDKIPCRequest request)
    {request.VersionMajor=1;request.VersionMinor=0;request.Component=(SDKComponent)1;return new(){Uniqueid=id,SourceSystem="Hunt1896",SourceModule="GfeSDK",Generic=new(){Type=1,Data=new SDKIPCMessage{Request=request}.ToByteString()}};}
    private static HighlightsSession Session(Func<HighlightEvent,bool>? sink=null)=>new(42,()=>100_000_000,_=>true,sink??(_=>true),_=>{});
    private static HighlightsSession Authorized() {var s=Session();s.Process(Join());s.Process(Request("create",new(){CreateSDKInstanceRequest=new(){Client="Hunt",ClientPID=42}}));return s;}
    private static SDKIPCResponse Response(IReadOnlyList<BusMessage> replies)=>SDKIPCMessage.Parser.ParseFrom(replies[0].Generic.Data).Response;
    private static BusMessage[] Fixtures(string fileName = "python-wire.json")
    {
        var path=Path.Combine(AppContext.BaseDirectory,"Fixtures",fileName);
        if(!File.Exists(path))path=Path.Combine(Directory.GetCurrentDirectory(),"tests","HuntReplay.Tests","Fixtures",fileName);
        using var json=JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.EnumerateArray().Select(e=>BusMessage.Parser.ParseFrom(Convert.FromBase64String(e.GetProperty("base64").GetString()!))).ToArray();
    }
    private static void ProtocolGolden()
    {
        HighlightEvent? received=null;
        var s=new HighlightsSession(42,()=>100_000_000,id=>id=="HUNTER_KILLED",e=>{received=e;return true;},_=>{});
        var rows=Fixtures();s.Process(rows[0]);
        var create=Response(s.Process(rows[1])).CreateSDKInstanceResponse;Equal(7,create.ScopePermission.Count);True(create.ScopePermission.All(p=>p.Permission==1));
        s.Process(rows[2]);s.Process(rows[3]);Equal(0,Response(s.Process(rows[4])).Retval);
        Equal(-20000,received!.StartDeltaMs);Equal(5000,received.EndDeltaMs);Equal(100_000_000L,received.AtUs);
        var config=Response(s.Process(rows[5])).GetUserConfigResponse;Equal(2,config.HighlightTable.Count);False(config.HighlightTable.Single(h=>h.HighlightId=="PLAYER_DOWNED").UserEnabled);
        Equal(1,Response(s.Process(rows[6])).GetNumberOfHighlightsInGroupResponse.NumberOfHighlights);
        Equal(0,Response(s.Process(rows[7])).Retval);
    }
    private static void Test(string name, Action action) => TestRegistry.Add(name, action);
    private static Task TestAsync(string name, Func<Task> action) { TestRegistry.Add(name, action); return Task.CompletedTask; }
    private static void True(bool value) {if(!value)throw new Exception("Expected true");}
    private static void False(bool value)=>True(!value);
    private static void Equal<T>(T expected,T actual) {if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"Expected {expected}, got {actual}");}
    private static void Throws<T>(Action action) where T:Exception {try{action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
    private static async Task ThrowsAsync<T>(Func<Task> action) where T:Exception {try{await action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
    private sealed class ShortReadStream(byte[] bytes):MemoryStream(bytes)
    {public override ValueTask<int> ReadAsync(Memory<byte> target,CancellationToken token=default)=>base.ReadAsync(target[..Math.Min(1,target.Length)],token);}
}
