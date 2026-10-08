using Google.Protobuf;
using HuntReplay.Core.Replay;
using HuntReplay.Protocol.Generated;

namespace HuntReplay.Protocol;

/// <summary>One protocol state machine per pipe connection; no shared game/group state.</summary>
public sealed class HighlightsSession
{
    private readonly Func<long> nowUs;
    private readonly Func<string, bool> enabled;
    private readonly Func<HighlightEvent, bool> onHighlight;
    private readonly Action<string> log;
    private readonly uint clientPid;
    private readonly Dictionary<string, BusMessage[]> responses = new(StringComparer.Ordinal);
    private readonly Queue<string> responseOrder = new();
    private readonly HashSet<string> definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> groupEpochs = new(StringComparer.Ordinal);
    private readonly HashSet<string> closedGroups = new(StringComparer.Ordinal);
    private bool joined;
    private bool authorized;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string? GameName { get; private set; }
    public event Action<string?>? GameChanged;

    public HighlightsSession(uint clientPid, Func<long> nowUs, Func<string, bool> enabled,
        Func<HighlightEvent, bool> onHighlight, Action<string> log)
    {
        this.clientPid = clientPid;
        this.nowUs = nowUs;
        this.enabled = enabled;
        this.onHighlight = onHighlight;
        this.log = log;
    }

    public IReadOnlyList<BusMessage> Process(BusMessage bus)
    {
        CheckText(bus.Uniqueid, 128);
        CheckText(bus.SourceSystem, 256);
        CheckText(bus.SourceModule, 256);
        if (responses.TryGetValue(bus.Uniqueid, out var cached)) return cached;
        var replies = new List<BusMessage>();
        if (bus.Joined)
        {
            joined = true;
            var reply = ReplyEnvelope(bus, "MessageBusBroadcast", "Broadcaster");
            reply.BusPeer.Add(new BusMessage.Types.Peer { System = "NvNode", Module = "SDKAPI" });
            replies.Add(reply);
            replies.Add(new BusMessage
            {
                Uniqueid = Guid.NewGuid().ToString(), SourceSystem = "NvNode", SourceModule = "SDKAPI", Joined = true
            });
            log($"IPC JOIN · {Clean(bus.SourceSystem)}:{Clean(bus.SourceModule)} · PID {clientPid}");
        }
        else if (bus.Generic is { Data.Length: > 0 } generic)
        {
            if (!joined) throw new InvalidDataException("SDK request before bus JOIN.");
            var message = SDKIPCMessage.Parser.ParseFrom(generic.Data);
            if (message.MessageCase != SDKIPCMessage.MessageOneofCase.Request) return [];
            var request = message.Request;
            currentRequestId = bus.Uniqueid;
            var response = Handle(request);
            var envelope = ReplyEnvelope(bus, "NvNode", "SDKAPI");
            envelope.Generic = new BusMessage.Types.Generic
            {
                Type = 2, Data = new SDKIPCMessage { Response = response }.ToByteString()
            };
            replies.Add(envelope);
            if (request.RequestCase == SDKIPCRequest.RequestOneofCase.RequestPermissionRequest && response.Retval == 0)
            {
                var notification = new PermissionsChangedNotification();
                foreach (var scope in request.RequestPermissionRequest.Scopes)
                    notification.ScopePermission.Add(new ScopePermission { Scope = scope, Permission = scope is >= 1 and <= 7 ? 1u : 0u });
                var notice = ReplyEnvelope(bus, "NvNode", "SDKAPI");
                notice.Generic = new BusMessage.Types.Generic
                {
                    Type = 3, Data = new SDKIPCMessage
                    {
                        Notification = new SDKIPCNotification
                        {
                            Component = request.Component, PermissionsChangedNotification = notification
                        }
                    }.ToByteString()
                };
                replies.Add(notice);
            }
        }
        else if (bus.EncryptedBusMessage is not null || bus.Encryptionsetup is not null)
            throw new InvalidDataException("Encrypted NVIDIA IPC is not supported by this video protocol implementation.");
        var result = replies.ToArray();
        responses[bus.Uniqueid] = result;
        responseOrder.Enqueue(bus.Uniqueid);
        // Also bounds response cache memory: incoming configuration art is never echoed back.
        while (responseOrder.Count > 128) responses.Remove(responseOrder.Dequeue());
        return result;
    }

    private SDKIPCResponse Handle(SDKIPCRequest request)
    {
        var response = new SDKIPCResponse { Retval = 0, Retstatus = "SUCCESS" };
        if (request.VersionMajor != 1) return Failure("UNSUPPORTED_IPC_VERSION");
        if (!authorized && request.RequestCase is not (SDKIPCRequest.RequestOneofCase.CreateSDKInstanceRequest or
                SDKIPCRequest.RequestOneofCase.GetUILanguageRequest or SDKIPCRequest.RequestOneofCase.IsClientAliveRequest))
            return Failure("SDK_INSTANCE_REQUIRED");
        switch (request.RequestCase)
        {
            case SDKIPCRequest.RequestOneofCase.CreateSDKInstanceRequest:
                var create = request.CreateSDKInstanceRequest;
                if (create.ClientPID != clientPid) return Failure("CLIENT_PID_MISMATCH");
                CheckText(create.Client, 256);
                var instance = new CreateSDKInstanceResponse
                {
                    InstalledGfeVersion = "3.28.0.412", InstalledGfeSDKIPCVersionMajor = 1, InstalledGfeSDKIPCVersionMinor = 0
                };
                var scopes = create.Scopes.Count > 0 ? create.Scopes.ToArray() : [1u, 2u, 3u, 4u, 5u, 6u, 7u];
                if (scopes.Length > 32) return Failure("TOO_MANY_SCOPES");
                foreach (var scope in scopes)
                    instance.ScopePermission.Add(new ScopePermission { Scope = scope, Permission = scope is >= 1 and <= 7 ? 1u : 0u });
                response.CreateSDKInstanceResponse = instance;
                authorized = true;
                GameName = Clean(string.IsNullOrWhiteSpace(create.GameName) ? create.ClientProcessName : create.GameName);
                GameChanged?.Invoke(GameName);
                log($"SDK авторизован · {GameName} · scopes 1–7");
                break;
            case SDKIPCRequest.RequestOneofCase.ConfigureHighlightsRequest:
                var highlights = request.ConfigureHighlightsRequest.HighlightTable;
                if (highlights.Count > 256) return Failure("TOO_MANY_DEFINITIONS");
                definitions.Clear();
                foreach (var item in highlights) { CheckText(item.HighlightId, 128); definitions.Add(item.HighlightId); }
                log($"Зарегистрированы события · {string.Join(", ", definitions.Select(Clean))}");
                break;
            case SDKIPCRequest.RequestOneofCase.OpenGroupRequest:
                var group = request.OpenGroupRequest.GroupId;
                CheckText(group, 256);
                if (groups.Count >= 128 && !groups.ContainsKey(group)) return Failure("TOO_MANY_GROUPS");
                if (!groups.ContainsKey(group) || closedGroups.Remove(group))
                {
                    groups[group] = 0;
                    groupEpochs[group] = Guid.NewGuid().ToString("N");
                }
                log($"Матч начат · {Clean(group)}");
                break;
            case SDKIPCRequest.RequestOneofCase.CloseGroupRequest:
                // Do not cancel post-roll and do not delete real files on destroyHighlights.
                // The SDK owns virtual groups; the user's library owns saved clips.
                var close = request.CloseGroupRequest;
                closedGroups.Add(close.GroupId);
                if (close.DestroyHighlights)
                {
                    groups.Remove(close.GroupId); groupEpochs.Remove(close.GroupId); closedGroups.Remove(close.GroupId);
                }
                log($"Матч завершён · {Clean(close.GroupId)} · постзапись сохранена");
                break;
            case SDKIPCRequest.RequestOneofCase.SetVideoHighlightRequest:
                var video = request.SetVideoHighlightRequest;
                CheckText(video.HighlightId, 128);
                CheckText(video.GroupId, 256);
                if (!groups.ContainsKey(video.GroupId)) return Failure("GROUP_NOT_FOUND");
                if (!definitions.Contains(video.HighlightId)) return Failure("HIGHLIGHT_NOT_CONFIGURED");
                if (!enabled(video.HighlightId)) break; // A user-disabled event is a valid no-op.
                // baseTimeStamp has an SDK-specific clock domain; never compare it to Unix time/QPC.
                var evt = new HighlightEvent(SessionId + ":" + groupEpochs[video.GroupId], currentRequestId, video.GroupId, video.HighlightId,
                    nowUs(), video.StartDelta, video.EndDelta);
                if (!onHighlight(evt)) return Failure("CAPTURE_NOT_READY_OR_QUEUE_FULL");
                groups[video.GroupId]++;
                break;
            case SDKIPCRequest.RequestOneofCase.GetUserConfigRequest:
                var config = new GetUserConfigResponse { GameEnabled = true };
                foreach (var id in definitions)
                    config.HighlightTable.Add(new GetUserConfigResponse.Types.HighlightConfig { HighlightId = id, UserEnabled = enabled(id) });
                response.GetUserConfigResponse = config;
                break;
            case SDKIPCRequest.RequestOneofCase.GetUILanguageRequest:
                response.GetUILanguageResponse = new GetUILanguageResponse { LocaleName = "en-US" };
                break;
            case SDKIPCRequest.RequestOneofCase.IsClientAliveRequest:
                response.IsClientAliveResponse = new IsClientAliveResponse { Alive = true };
                break;
            case SDKIPCRequest.RequestOneofCase.GetNumberOfHighlightsInGroupRequest:
                response.GetNumberOfHighlightsInGroupResponse = new GetNumberOfHighlightsInGroupResponse
                {
                    NumberOfHighlights = groups.GetValueOrDefault(request.GetNumberOfHighlightsInGroupRequest.GroupId)
                };
                break;
            case SDKIPCRequest.RequestOneofCase.DestroySDKInstanceRequest:
                authorized = false;
                GameName = null;
                GameChanged?.Invoke(null);
                definitions.Clear();
                groups.Clear(); groupEpochs.Clear(); closedGroups.Clear();
                break;
            case SDKIPCRequest.RequestOneofCase.RequestPermissionRequest:
                if (request.RequestPermissionRequest.Scopes.Count > 32) return Failure("TOO_MANY_SCOPES");
                break;
            case SDKIPCRequest.RequestOneofCase.OpenGroupSummaryRequest:
                log("Игра запросила сводку; клипы доступны в галерее HuntReplay Studio.");
                break;
            case SDKIPCRequest.RequestOneofCase.SetScreenshotHighlightRequest:
            case SDKIPCRequest.RequestOneofCase.ToggleOverlayRequest:
            case SDKIPCRequest.RequestOneofCase.OpsOptimizeRequest:
            default:
                // Do not pretend to provide an NVIDIA overlay, driver optimizer or screenshot API.
                return Failure("NOT_IMPLEMENTED_BY_STANDALONE_VIDEO_RECORDER");
        }
        return response;
    }

    private string currentRequestId = "";
    private BusMessage ReplyEnvelope(BusMessage source, string system, string module)
    {
        return new BusMessage
        {
            Uniqueid = Guid.NewGuid().ToString(), ReferenceUniqueid = source.Uniqueid,
            SourceSystem = system, SourceModule = module,
            TargetSystem = source.SourceSystem, TargetModule = source.SourceModule,
            Status = new BusMessage.Types.Status { Code = BusMessage.Types.Status.Types.Code.Completed }
        };
    }

    private static SDKIPCResponse Failure(string status) => new() { Retval = -1, Retstatus = status };
    private static void CheckText(string value, int limit)
    {
        if (string.IsNullOrEmpty(value) || value.Length > limit || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid IPC identifier.");
    }
    private static string Clean(string value) => new(value.Where(c => !char.IsControl(c)).Take(256).ToArray());
}
