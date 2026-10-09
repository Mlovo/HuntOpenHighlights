using System.Text.Json;
using Google.Protobuf;
using HuntReplay.Core.Configuration;
using HuntReplay.Core.Replay;
using HuntReplay.Protocol;
using HuntReplay.Protocol.Generated;

namespace HuntReplay.Tests;

// BusMessage.uniqueid is an opaque, bounded correlation token. These regressions cover the
// field report "Invalid IPC identifier: BusMessage.uniqueid (SDK/CreateSDKInstanceRequest)
// contains control characters (length 37)", which disconnected a healthy game client after JOIN.
// The fixture python-synthetic-uniqueid-wire.json is SYNTHETIC: generated from the repository's
// original Python proto modules, not a captured game trace.
public static partial class Program
{
    private const string SyntheticToken = "3f2b8c1e-5d4a-4e6f-9a7b-1c2d3e4f5a6b";
    private const string SyntheticFixture = "python-synthetic-uniqueid-wire.json";

    private static void IpcCorrelationTests()
    {
        Test("NUL-suffixed 37-unit correlation ID is accepted and echoed exactly", () =>
        {
            var s = Session(); s.Process(Join());
            var id = SyntheticToken + "\0";
            Equal(37, id.Length);
            var replies = s.Process(Request(id, new() { CreateSDKInstanceRequest = new() { Client = "Hunt", ClientPID = 42 } }));
            Equal(0, Response(replies).Retval);
            Equal(id, replies[0].ReferenceUniqueid);
            Equal(37, replies[0].ReferenceUniqueid.Length);
        });
        Test("every control-character class is opaque in JOIN and SDK correlation", () =>
        {
            var s = Session();
            var joinId = SyntheticToken + "\u0010"; // distinct from every SDK token below (reply cache is per uniqueid)
            var joinReplies = s.Process(new BusMessage { Uniqueid = joinId, SourceSystem = "Hunt1896", SourceModule = "GfeSDK", Joined = true });
            Equal(joinId, joinReplies[0].ReferenceUniqueid);
            string[] suffixes = ["\u0000", "\u0001", "\t", "\n", "\r\n", "\u001F", "\u007F", "\u0085"];
            foreach (var suffix in suffixes)
            {
                var id = SyntheticToken + suffix;
                var replies = s.Process(Request(id, new() { IsClientAliveRequest = new() }));
                True(Response(replies).IsClientAliveResponse.Alive);
                Equal(id, replies[0].ReferenceUniqueid);
            }
            var leading = "\u0001" + SyntheticToken;
            Equal(leading, s.Process(Request(leading, new() { IsClientAliveRequest = new() }))[0].ReferenceUniqueid);
        });
        Test("retransmitted control-character request reports one highlight", () =>
        {
            var events = new List<HighlightEvent>();
            var s = Armed(events, out var group);
            var kill = Request(SyntheticToken + "\u0085", new() { SetVideoHighlightRequest = new()
                { GroupId = group, HighlightId = "HUNTER_KILLED", StartDelta = -20000, EndDelta = 5000 } });
            var first = s.Process(kill);
            var second = s.Process(kill);
            Equal(1, events.Count);
            Equal(SyntheticToken + "\u0085", first[0].ReferenceUniqueid);
            Equal(first[0].ReferenceUniqueid, second[0].ReferenceUniqueid);
            Equal(0, Response(second).Retval);
        });
        Test("correlation IDs differing only by control characters are distinct requests", () =>
        {
            var events = new List<HighlightEvent>();
            var s = Armed(events, out var group);
            var shortId = SyntheticToken + "\u0004";
            var longId = SyntheticToken + "\u0004\0";
            s.Process(Request(shortId, new() { SetVideoHighlightRequest = new()
                { GroupId = group, HighlightId = "HUNTER_KILLED", StartDelta = -20000, EndDelta = 5000 } }));
            s.Process(Request(longId, new() { SetVideoHighlightRequest = new()
                { GroupId = group, HighlightId = "HUNTER_KILLED", StartDelta = -20000, EndDelta = 5000 } }));
            Equal(2, events.Count);
            False(events[0].RequestId == events[1].RequestId);
            Equal(shortId, events[0].RequestId);
            Equal(longId, events[1].RequestId);
            Equal(2, Response(s.Process(Request(SyntheticToken + "\u0005", new()
                { GetNumberOfHighlightsInGroupRequest = new() { GroupId = group } }))).GetNumberOfHighlightsInGroupResponse.NumberOfHighlights);
        });
        Test("malformed UTF-16 correlation ID is rejected with code point and index only", () =>
        {
            var s = Session(); s.Process(Join());
            var bad = SyntheticToken + "\uD800";
            try { s.Process(Request(bad, new() { IsClientAliveRequest = new() })); }
            catch (InvalidDataException ex)
            {
                True(ex.Message.Contains("BusMessage.uniqueid")); True(ex.Message.Contains("IsClientAliveRequest"));
                True(ex.Message.Contains("well-formed")); True(ex.Message.Contains("U+D800@36"));
                False(ex.Message.Contains('\uD800')); False(ex.Message.Contains(SyntheticToken));
                return;
            }
            throw new Exception("Expected malformed UTF-16 identifier to be rejected.");
        });
        Test("correlation ID length bound is 128 UTF-16 units, unchanged", () =>
        {
            var s = Session(); s.Process(Join());
            var limit = new string('a', 128);
            True(Response(s.Process(Request(limit, new() { IsClientAliveRequest = new() }))).IsClientAliveResponse.Alive);
            InvalidIdentifier(() => s.Process(Request(new string('a', 129), new() { IsClientAliveRequest = new() })),
                "BusMessage.uniqueid", "IsClientAliveRequest", "length 129 exceeds 128");
        });
        Test("control characters in group IDs stay rejected with code point and index", () =>
        {
            var s = Authorized();
            var reply = Response(s.Process(Request("open-bad", new() { OpenGroupRequest = new() { GroupId = "match\u0007" } })));
            Equal(-1, reply.Retval);
            True(reply.Retstatus.Contains("openGroupRequest.groupId")); True(reply.Retstatus.Contains("control characters"));
            True(reply.Retstatus.Contains("U+0007@5")); False(reply.Retstatus.Contains('\u0007'));
        });
        Test("synthetic fixture is labelled synthetic and carries named frames", () =>
        {
            var json = JsonDocument.Parse(File.ReadAllText(FixturePath(SyntheticFixture))).RootElement;
            True(json.GetProperty("synthetic").GetBoolean());
            Equal(10, json.GetProperty("frames").GetArrayLength());
        });
        Test("synthetic protobuf sequence: exact correlation, one highlight per kill, planned tail", () =>
        {
            var frames = NamedFixtures(SyntheticFixture);
            var logs = new List<string>();
            var clock = new Queue<long>(new long[] { 100_000_000, 101_000_000 });
            var events = new List<HighlightEvent>();
            var s = Logged(logs, e => { events.Add(e); return true; }, () => clock.Dequeue());
            var replies = new Dictionary<string, IReadOnlyList<BusMessage>>(StringComparer.Ordinal);
            foreach (var (name, message) in frames)
            {
                var reply = s.Process(message);
                True(reply.Count > 0);
                Equal(message.Uniqueid, reply[0].ReferenceUniqueid);
                if (message.Joined) { replies[name] = reply; continue; }
                Equal(0, Response(reply).Retval);
                replies[name] = reply;
            }
            Equal(2, events.Count);
            Equal("PLAYER_DOWNED", events[0].HighlightId); Equal("HUNTER_KILLED", events[1].HighlightId);
            Equal(frames[4].Message.Uniqueid, events[0].RequestId);
            Equal(frames[5].Message.Uniqueid, events[1].RequestId);
            Equal(2, Response(replies["count"]).GetNumberOfHighlightsInGroupResponse.NumberOfHighlights);
            var config = Response(replies["config"]).GetUserConfigResponse;
            Equal(2, config.HighlightTable.Count); True(config.HighlightTable.All(h => h.UserEnabled));

            var planner = new HighlightPlanner(new ReplaySettings());
            foreach (var evt in events) planner.Register(evt);
            Equal(1, planner.PendingCount);
            var plan = planner.TakeReady(long.MaxValue, force: true).Single();
            Equal("HUNTER_KILLED_PLAYER_DOWNED", plan.EventTag);
            Equal(1, plan.KillCount);
            Equal(80_000_000L, plan.StartUs);       // 20 s pre-roll before the first event (SmartTrim)
            Equal(108_000_000L, plan.EndUs);        // last event + 7 s post-roll tail
            Equal(116_000_000L, plan.ReadyAtUs);    // last event + 15 s coalesce horizon
        });
        Test("synthetic retransmit reuses the cached reply without a second highlight", () =>
        {
            var frames = NamedFixtures(SyntheticFixture);
            var events = new List<HighlightEvent>();
            var s = Logged(new List<string>(), e => { events.Add(e); return true; });
            foreach (var (_, message) in frames.Take(6)) s.Process(message);
            Equal(2, events.Count);
            var replay = s.Process(frames[6].Message);
            Equal("hunter-killed-retransmit", frames[6].Name);
            Equal(2, events.Count);
            Equal(frames[5].Message.Uniqueid, replay[0].ReferenceUniqueid);
        });
        Test("synthetic PID, JOIN and bounds checks keep the pipe state machine intact", () =>
        {
            var frames = NamedFixtures(SyntheticFixture);
            Throws<InvalidDataException>(() => Session().Process(frames[1].Message));
            var s = Logged(new List<string>());
            s.Process(frames[0].Message);
            var spoofed = frames[1].Message.Clone();
            var request = SDKIPCMessage.Parser.ParseFrom(spoofed.Generic.Data);
            request.Request.CreateSDKInstanceRequest.ClientPID = 99;
            spoofed.Generic.Data = request.ToByteString();
            Equal("CLIENT_PID_MISMATCH", Response(s.Process(spoofed)).Retstatus);
            Equal("SDK_INSTANCE_REQUIRED", Response(s.Process(frames[2].Message)).Retstatus);
            var oversized = frames[0].Message.Clone();
            oversized.Uniqueid = new string('j', 129);
            InvalidIdentifier(() => Session().Process(oversized), "BusMessage.uniqueid", "JOIN", "length 129 exceeds 128");
        });
        Test("user-disabled highlight is a traceable no-op with its request ordinal", () =>
        {
            var logs = new List<string>(); var events = new List<HighlightEvent>();
            var s = new HighlightsSession(42, () => 100_000_000, id => id != "HUNTER_KILLED", e => { events.Add(e); return true; }, logs.Add);
            s.Process(new BusMessage { Uniqueid = SyntheticToken, SourceSystem = "Hunt1896", SourceModule = "GfeSDK", Joined = true });
            s.Process(Request(SyntheticToken + "\u0001", new() { CreateSDKInstanceRequest = new() { Client = "Hunt", ClientPID = 42 } }));
            var configure = new SDKIPCRequest { ConfigureHighlightsRequest = new() };
            configure.ConfigureHighlightsRequest.HighlightTable.Add(new ConfigureHighlightsRequest.Types.HighlightDefinition { HighlightId = "HUNTER_KILLED" });
            s.Process(Request(SyntheticToken + "\u0002", configure));
            s.Process(Request(SyntheticToken + "\u0003", new() { OpenGroupRequest = new() { GroupId = "match" } }));
            Equal(0, Response(s.Process(Request(SyntheticToken + "\u0004", new() { SetVideoHighlightRequest = new()
                { GroupId = "match", HighlightId = "HUNTER_KILLED", StartDelta = -20000, EndDelta = 5000 } }))).Retval);
            Equal(0, events.Count);
            True(logs.Any(line => line.Contains("#4") && line.Contains("HUNTER_KILLED") && line.Contains("отключено")));
        });
        Test("synthetic logs carry code points and indexes, never the raw correlation token", () =>
        {
            var frames = NamedFixtures(SyntheticFixture);
            var logs = new List<string>();
            var clock = new Queue<long>(new long[] { 100_000_000, 101_000_000 });
            var s = Logged(logs, _ => true, () => clock.Dequeue());
            foreach (var (_, message) in frames) s.Process(message);
            True(logs.Count > 0);
            True(logs.Any(line => line.Contains("U+0000@36")));
            foreach (var line in logs)
            {
                False(line.Contains(SyntheticToken));
                False(line.Any(char.IsControl));
            }
        });
    }

    private static string FixturePath(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.Exists(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), "tests", "HuntReplay.Tests", "Fixtures", fileName);
    }

    private static (string Name, BusMessage Message)[] NamedFixtures(string fileName)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(FixturePath(fileName)));
        var root = json.RootElement;
        var frames = root.ValueKind == JsonValueKind.Object ? root.GetProperty("frames") : root;
        return frames.EnumerateArray().Select(e => (e.GetProperty("name").GetString()!,
            BusMessage.Parser.ParseFrom(Convert.FromBase64String(e.GetProperty("base64").GetString()!)))).ToArray();
    }

    private static HighlightsSession Logged(List<string> logs, Func<HighlightEvent, bool>? sink = null, Func<long>? clock = null) =>
        new(42, clock ?? (() => 100_000_000), _ => true, sink ?? (_ => true), logs.Add);

    // JOIN, CreateSDKInstance, ConfigureHighlights (both events) and OpenGroup, all on control-character tokens.
    private static HighlightsSession Armed(List<HighlightEvent> events, out string group)
    {
        group = "match-1896";
        var s = Logged(new List<string>(), e => { events.Add(e); return true; });
        // Every request carries its own token: the reply cache is keyed by BusMessage.uniqueid.
        s.Process(new BusMessage { Uniqueid = SyntheticToken, SourceSystem = "Hunt1896", SourceModule = "GfeSDK", Joined = true });
        Equal(0, Response(s.Process(Request(SyntheticToken + "\0", new() { CreateSDKInstanceRequest = new() { Client = "Hunt", ClientPID = 42 } }))).Retval);
        var configure = new SDKIPCRequest { ConfigureHighlightsRequest = new() };
        configure.ConfigureHighlightsRequest.HighlightTable.Add(new ConfigureHighlightsRequest.Types.HighlightDefinition { HighlightId = "HUNTER_KILLED" });
        configure.ConfigureHighlightsRequest.HighlightTable.Add(new ConfigureHighlightsRequest.Types.HighlightDefinition { HighlightId = "PLAYER_DOWNED" });
        Equal(0, Response(s.Process(Request(SyntheticToken + "\u0001", configure))).Retval);
        Equal(0, Response(s.Process(Request("\u001f" + SyntheticToken, new() { OpenGroupRequest = new() { GroupId = group } }))).Retval);
        return s;
    }
}
