using System.IO.Pipes;
using Google.Protobuf;
using HuntReplay.Protocol;
using HuntReplay.Protocol.Generated;

namespace HuntReplay.Tests;

/// <summary>An explicit developer tool, never included in the recorder. No game injection or keyboard input.</summary>
internal static class LivePipeSmoke
{
    public static async Task<int> RunAsync(bool multi)
    {
        Console.WriteLine("This sends real test highlight events to a running Studio. Start its buffer first.");
        Console.WriteLine("Use an explicitly selected monitor or a visible game window. No recording source is changed by this tool.");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var pipe = new NamedPipeClientStream(".", "NvMessageBusBroadcast", PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(5000, stop.Token);
            var join = Envelope(); join.Joined = true;
            await BusFraming.WriteAsync(pipe, join, stop.Token);
            var ack = await BusFraming.ReadAsync(pipe, stop.Token);
            var presence = await BusFraming.ReadAsync(pipe, stop.Token);
            if (ack?.ReferenceUniqueid != join.Uniqueid || presence?.Joined != true) throw new IOException("JOIN handshake failed.");
            await BusFraming.WriteAsync(pipe, new BusMessage
            {
                Uniqueid = "", SourceSystem = "", SourceModule = "", ReferenceUniqueid = ack.Uniqueid,
                Status = new() { Code = BusMessage.Types.Status.Types.Code.Completed }
            }, stop.Token); // No reply is expected for a service ACK.
            await Send(new() { CreateSDKInstanceRequest = new() { Client = "HuntReplay smoke test", ClientPID = (ulong)Environment.ProcessId, GameName = "HuntReplay diagnostic client" } });
            var config = new ConfigureHighlightsRequest();
            config.HighlightTable.Add(new ConfigureHighlightsRequest.Types.HighlightDefinition { HighlightId = "HUNTER_KILLED" });
            await Send(new() { ConfigureHighlightsRequest = config });
            const string group = "studio-smoke-test";
            await Send(new() { OpenGroupRequest = new() { GroupId = group } });
            await Send(new() { SetVideoHighlightRequest = new() { GroupId = group, HighlightId = "HUNTER_KILLED", StartDelta = -5000, EndDelta = 5000 } });
            if (multi)
            {
                Console.WriteLine("First kill accepted. Waiting 12 seconds (longer than post-roll, within coalescing gap)...");
                await Task.Delay(TimeSpan.FromSeconds(12), stop.Token);
                await Send(new() { SetVideoHighlightRequest = new() { GroupId = group, HighlightId = "HUNTER_KILLED", StartDelta = -5000, EndDelta = 5000 } });
            }
            await Send(new() { CloseGroupRequest = new() { GroupId = group } });
            Console.WriteLine("Events accepted. Wait 16 seconds, then verify one playable MP4 with audio in Studio's gallery.");
            await Task.Delay(TimeSpan.FromSeconds(16), stop.Token);
            return 0;

            async Task Send(SDKIPCRequest request)
            {
                request.VersionMajor = 1; request.VersionMinor = 0; request.Component = (SDKComponent)1;
                var bus = Envelope();
                bus.Generic = new BusMessage.Types.Generic { Type = 1, Data = new SDKIPCMessage { Request = request }.ToByteString() };
                await BusFraming.WriteAsync(pipe, bus, stop.Token);
                var reply = await BusFraming.ReadAsync(pipe, stop.Token) ?? throw new EndOfStreamException();
                if (reply.ReferenceUniqueid != bus.Uniqueid) throw new IOException("Response correlation mismatch.");
                var response = SDKIPCMessage.Parser.ParseFrom(reply.Generic.Data).Response;
                if (response.Retval != 0) throw new IOException(response.Retstatus);
                Console.WriteLine("OK " + request.RequestCase);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine("Live smoke test failed: " + ex.Message); return 1; }
    }
    private static BusMessage Envelope() => new()
    {
        Uniqueid = Guid.NewGuid().ToString(), SourceSystem = "HuntReplayDiagnostic",
        SourceModule = "GfeSDK", SourcePid = (uint)Environment.ProcessId
    };
}
