using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using HuntReplay.Core.Configuration;
using HuntReplay.Protocol.Generated;

namespace HuntReplay.Tests;

public static partial class Program
{
    private static void RuntimeRegressionTests()
    {
        if (OperatingSystem.IsWindows()) TestAsync("Highlights pipe server restarts after its listen loop failed", async () =>
        {
            if (AppAssembly() is not { } app) return;
            var root = Path.Combine(Path.GetTempPath(), "HuntReplay-pipe-test-" + Guid.NewGuid().ToString("N"));
            var name = "HuntReplayTest." + Guid.NewGuid().ToString("N");
            var log = (IAsyncDisposable)Activator.CreateInstance(app.GetType("HuntReplay.App.Services.StudioLog")!, root)!;
            var recorder = (IAsyncDisposable)Activator.CreateInstance(app.GetType("HuntReplay.App.Services.RecorderService")!,
                new StudioSettings(), log, Path.Combine(root, "library"))!;
            var serverType = app.GetType("HuntReplay.App.Services.HighlightsPipeServer")!;
            var server = (IAsyncDisposable)Activator.CreateInstance(serverType, recorder, log, name)!;
            bool TryStart() => (bool)serverType.GetMethod("TryStart")!.Invoke(server, [false])!;
            bool Listening() => (bool)serverType.GetProperty("IsListening")!.GetValue(server)!;
            var blockers = new List<System.IO.Pipes.NamedPipeServerStream>();
            try
            {
                True(TryStart());
                // Take the other 15 of the 16 instances: after the next connection the server cannot create
                // a listening instance, and its listen loop ends with an IOException.
                for (int i = 0; i < 15; i++)
                    blockers.Add(new(name, System.IO.Pipes.PipeDirection.InOut, 16, System.IO.Pipes.PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly));
                using (var first = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut))
                {
                    await first.ConnectAsync(5000);
                    for (int wait = 0; wait < 100 && Listening(); wait++) await Task.Delay(50);
                    False(Listening());
                }
                foreach (var blocker in blockers) blocker.Dispose();
                blockers.Clear();
                // The watcher's next retry must start a fresh loop, not report the dead one as running.
                True(TryStart());
                True(Listening());
                using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut);
                await client.ConnectAsync(5000);
                True(client.IsConnected);
            }
            finally
            {
                foreach (var blocker in blockers) blocker.Dispose();
                await server.DisposeAsync();
                await recorder.DisposeAsync();
                await log.DisposeAsync();
                Directory.Delete(root, true);
            }
        });

        Test("legacy null device IDs recover without resetting other settings", () => InTemp(path =>
        {
            var json = JsonSerializer.SerializeToNode(Settings(), StudioSettings.JsonOptions)!;
            json["Video"]!["MonitorDevice"] = null;
            json["Video"]!["GpuDevice"] = null;
            json["Video"]!["Fps"] = 120;
            json["Audio"]!["OutputDeviceId"] = null;
            json["Audio"]!["MicrophoneDeviceId"] = null;
            json["Audio"]!["MicrophoneEnabled"] = true;
            var store = new SettingsStore(Path.Combine(path, "config.json"));
            File.WriteAllText(store.FilePath, json.ToJsonString());
            var settings = store.Load();
            True(store.LoadWarning is null);
            settings.Validate();
            Equal("", settings.Video.MonitorDevice);
            Equal("", settings.Video.GpuDevice);
            Equal("", settings.Audio.OutputDeviceId);
            Equal("", settings.Audio.MicrophoneDeviceId);
            Equal(120, settings.Video.Fps);
            True(settings.Audio.MicrophoneEnabled);
            store.Save(settings);
            var saved = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
            Equal("", saved["Video"]!["MonitorDevice"]!.GetValue<string>());
            Equal("", saved["Video"]!["GpuDevice"]!.GetValue<string>());
            Equal("", saved["Audio"]!["OutputDeviceId"]!.GetValue<string>());
            Equal("", saved["Audio"]!["MicrophoneDeviceId"]!.GetValue<string>());
        }));
        Test("disconnected explicit device IDs survive validation and cloning", () =>
        {
            var s = Settings();
            s.Video.MonitorDevice = @"\\.\DISPLAY9";
            s.Video.GpuDevice = "1";
            s.Audio.OutputDeviceId = "unplugged-output-endpoint";
            s.Audio.MicrophoneDeviceId = "unplugged-microphone-endpoint";
            s.Validate(); var copy = s.Clone();
            Equal(s.Video.MonitorDevice, copy.Video.MonitorDevice);
            Equal(s.Video.GpuDevice, copy.Video.GpuDevice);
            Equal(s.Audio.OutputDeviceId, copy.Audio.OutputDeviceId);
            Equal(s.Audio.MicrophoneDeviceId, copy.Audio.MicrophoneDeviceId);
        });
        Test("missing device settings remain Windows defaults", () =>
        {
            var video = JsonSerializer.Deserialize<VideoSettings>("{}")!;
            var audio = JsonSerializer.Deserialize<AudioSettings>("{}")!;
            Equal("", video.MonitorDevice); Equal("", video.GpuDevice); Equal("", audio.OutputDeviceId); Equal("", audio.MicrophoneDeviceId);
        });
        Test("direct null device assignments normalize only the affected field", () =>
        {
            var s = Settings(); s.Audio.OutputDeviceId = "keep-output";
            s.Video.MonitorDevice = null; s.Video.GpuDevice = null; s.Audio.MicrophoneDeviceId = null;
            Equal("", s.Video.MonitorDevice); Equal("", s.Video.GpuDevice); Equal("", s.Audio.MicrophoneDeviceId);
            Equal("keep-output", s.Audio.OutputDeviceId); s.Validate();
        });

        Test("Python control-frame fixture allows JOIN ACK then empty-client create", () =>
        {
            var s = Session(); var rows = Fixtures("python-control-wire.json");
            Equal(2, s.Process(rows[0]).Count);
            Equal(0, s.Process(rows[1]).Count); // ACK with empty required strings
            Equal(0, s.Process(rows[2]).Count); // ACK reusing the JOIN request ID
            var create = s.Process(rows[3]);
            Equal(0, Response(create).Retval);
            Equal("Hunt1896", create[0].TargetSystem); Equal("GfeSDK", create[0].TargetModule);
            Equal(0, s.Process(rows[4]).Count); // SDK response is not a request
            True(Response(s.Process(rows[5])).IsClientAliveResponse.Alive);
        });
        Test("control frames before JOIN do not poison the request cache", () =>
        {
            var s = Session(); var ack = new BusMessage { Uniqueid = "join", SourceSystem = "", SourceModule = "" };
            Equal(0, s.Process(ack).Count); Equal(2, s.Process(Join()).Count);
        });
        Test("ACK echoing an SDK ID is never answered with the cached response", () =>
        {
            var s = Authorized();
            Equal(0, s.Process(new BusMessage
            {
                Uniqueid = "create", SourceSystem = "", SourceModule = "",
                Status = new() { Code = BusMessage.Types.Status.Types.Code.Completed }
            }).Count);
            True(Response(s.Process(Request("alive", new() { IsClientAliveRequest = new() }))).IsClientAliveResponse.Alive);
        });
        Test("empty generic and SDK notifications are not request envelopes", () =>
        {
            var s = Authorized();
            var bus = new BusMessage { Uniqueid = "", SourceSystem = "", SourceModule = "", Generic = new() };
            Equal(0, s.Process(bus).Count);
            bus.Generic.Data = new SDKIPCMessage
            {
                Notification = new() { Component = (SDKComponent)1, PermissionsChangedNotification = new() }
            }.ToByteString();
            Equal(0, s.Process(bus).Count);
        });
        Test("empty client label never bypasses OS pipe-client PID check", () =>
        {
            var s = Session(); s.Process(Join());
            Equal("CLIENT_PID_MISMATCH", Response(s.Process(Request("create", new()
                { CreateSDKInstanceRequest = new() { Client = "", ClientPID = 99 } }))).Retstatus);
            Equal("SDK_INSTANCE_REQUIRED", Response(s.Process(Request("open", new()
                { OpenGroupRequest = new() { GroupId = "match" } }))).Retstatus);
        });
        Test("omitted routing fields use the joined identity for SDK responses", () =>
        {
            var s = Authorized();
            var request = Request("alive", new() { IsClientAliveRequest = new() });
            request.SourceSystem = ""; request.SourceModule = "";
            var replies = s.Process(request);
            True(Response(replies).IsClientAliveResponse.Alive);
            Equal("alive", replies[0].ReferenceUniqueid);
            Equal("Hunt1896", replies[0].TargetSystem); Equal("GfeSDK", replies[0].TargetModule);
        });
        Test("empty SDK correlation ID still fails with field and request name", () =>
        {
            var request = Request("", new() { IsClientAliveRequest = new() });
            InvalidIdentifier(() => Authorized().Process(request), "BusMessage.uniqueid", "IsClientAliveRequest", "empty");
        });
        Test("JOIN still requires a bounded nonempty routing identity", () =>
        {
            var join = Join(); join.SourceModule = "";
            InvalidIdentifier(() => Session().Process(join), "BusMessage.source_module", "JOIN", "empty");
            join.SourceModule = new string('x', 257);
            InvalidIdentifier(() => Session().Process(join), "BusMessage.source_module", "JOIN", "length 257 exceeds 256");
        });
        Test("control characters in SDK routing remain rejected without echoing input", () =>
        {
            var request = Request("alive", new() { IsClientAliveRequest = new() });
            request.SourceSystem = "do-not-echo\ninjected-log";
            InvalidIdentifier(() => Authorized().Process(request), "BusMessage.source_system", "IsClientAliveRequest", "control characters");
        });
        Test("bad SDK label returns a correlatable error and permits retry on same pipe", () =>
        {
            var s = Session(); s.Process(Join());
            var error = Response(s.Process(Request("bad", new()
                { CreateSDKInstanceRequest = new() { Client = "bad\nlabel", ClientPID = 42 } })));
            Equal(-1, error.Retval); True(error.Retstatus.Contains("createSDKInstanceRequest.client"));
            False(error.Retstatus.Contains('\n'));
            Equal(0, Response(s.Process(Request("good", new()
                { CreateSDKInstanceRequest = new() { Client = "", ClientPID = 42 } }))).Retval);
        });
        Test("invalid reconfiguration cannot erase existing highlight definitions", () =>
        {
            var s = Authorized(); s.Process(Fixtures()[2]);
            var request = new SDKIPCRequest { ConfigureHighlightsRequest = new() };
            request.ConfigureHighlightsRequest.HighlightTable.Add(new ConfigureHighlightsRequest.Types.HighlightDefinition { HighlightId = "replacement" });
            request.ConfigureHighlightsRequest.HighlightTable.Add(new ConfigureHighlightsRequest.Types.HighlightDefinition { HighlightId = "" });
            var error = Response(s.Process(Request("invalid-config", request)));
            Equal(-1, error.Retval); True(error.Retstatus.Contains("configureHighlightsRequest.highlightId"));
            var config = Response(s.Process(Request("inspect", new() { GetUserConfigRequest = new() }))).GetUserConfigResponse;
            Equal(2, config.HighlightTable.Count);
            True(config.HighlightTable.Any(h => h.HighlightId == "HUNTER_KILLED"));
            False(config.HighlightTable.Any(h => h.HighlightId == "replacement"));
        });
        Test("unknown close groups cannot grow retained session state", () =>
        {
            var s = Authorized();
            for (int i = 0; i < 140; i++)
                Equal("GROUP_NOT_FOUND", Response(s.Process(Request("close" + i, new()
                    { CloseGroupRequest = new() { GroupId = "unknown" + i } }))).Retstatus);
            Equal(0, Response(s.Process(Request("open", new() { OpenGroupRequest = new() { GroupId = "real" } }))).Retval);
        });
        Test("ACK classification does not permit encrypted envelopes", () =>
        {
            var bus = new BusMessage { Uniqueid = "", SourceSystem = "", SourceModule = "", Encryptionsetup = new() };
            Throws<InvalidDataException>(() => Session().Process(bus));
        });
    }

    private static void InvalidIdentifier(Action action, string field, string kind, string reason)
    {
        try { action(); }
        catch (InvalidDataException ex)
        {
            True(ex.Message.Contains(field)); True(ex.Message.Contains(kind)); True(ex.Message.Contains(reason));
            False(ex.Message.Contains("do-not-echo")); return;
        }
        throw new Exception("Expected a precise InvalidDataException for " + field);
    }
}
