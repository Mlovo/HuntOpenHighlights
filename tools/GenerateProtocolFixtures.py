"""Regenerate golden wire inputs using the repository's ORIGINAL Python protobuf modules.
Build-time validation only. The Windows application never executes Python.
Usage: python -m pip install protobuf==7.35.1; python tools/GenerateProtocolFixtures.py
"""
from pathlib import Path
import base64
import json
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "proto"))
import BusMessage_pb2 as bus
import sdk_pb2 as sdk

fixtures = []
def envelope(name, request=None, joined=False):
    message = bus.BusMessage(uniqueid=name, source_system="Hunt1896", source_module="GfeSDK", source_pid=42)
    if joined:
        message.joined = True
    if request is not None:
        request.version_major = 1
        request.version_minor = 0
        request.component = sdk.SDKComponent_SHARE
        message.generic.type = 1
        message.generic.data = sdk.SDKIPCMessage(request=request).SerializeToString()
    fixtures.append({"name": name, "base64": base64.b64encode(message.SerializeToString()).decode("ascii")})

envelope("join", joined=True)
r=sdk.SDKIPCRequest()
r.createSDKInstanceRequest.client="Hunt"
r.createSDKInstanceRequest.clientPID=42
r.createSDKInstanceRequest.gameName="Hunt: Showdown 1896"
envelope("create", r)
r=sdk.SDKIPCRequest()
for id in ("HUNTER_KILLED", "PLAYER_DOWNED"):
    r.configureHighlightsRequest.highlightTable.add(highlightId=id)
envelope("configure", r)
r=sdk.SDKIPCRequest(); r.openGroupRequest.groupId="match-1896"; envelope("open",r)
r=sdk.SDKIPCRequest()
r.setVideoHighlightRequest.groupId="match-1896"
r.setVideoHighlightRequest.highlightId="HUNTER_KILLED"
r.setVideoHighlightRequest.startDelta=-20000
r.setVideoHighlightRequest.endDelta=5000
r.setVideoHighlightRequest.baseTimeStamp=12345678
envelope("kill",r)
r=sdk.SDKIPCRequest(); r.getUserConfigRequest.SetInParent(); envelope("config",r)
r=sdk.SDKIPCRequest(); r.getNumberOfHighlightsInGroupRequest.groupId="match-1896"; envelope("count",r)
r=sdk.SDKIPCRequest(); r.closeGroupRequest.groupId="match-1896"; envelope("close",r)
path=ROOT / "tests/HuntReplay.Tests/Fixtures/python-wire.json"
path.write_text(json.dumps(fixtures, indent=2)+"\n", encoding="utf-8")
print(path.relative_to(ROOT))

# Synthetic compatibility/regression inputs, not a captured game trace. Use the
# original proto2 serializer to verify that required string fields may be empty.
controls = []
def control(name, message):
    controls.append({"name": name, "base64": base64.b64encode(message.SerializeToString()).decode("ascii")})

control("join", bus.BusMessage(uniqueid="join", source_system="Hunt1896", source_module="GfeSDK", joined=True))
ack = bus.BusMessage(uniqueid="", source_system="", source_module="", reference_uniqueid="join")
ack.status.code = bus.BusMessage.Status.Completed
control("empty-ack", ack)
ack.uniqueid = "join"
control("echo-join-ack", ack)
r = sdk.SDKIPCRequest(version_major=1, version_minor=0, component=sdk.SDKComponent_SHARE)
r.createSDKInstanceRequest.client = ""
r.createSDKInstanceRequest.clientPID = 42
r.createSDKInstanceRequest.gameName = "Hunt: Showdown 1896"
m = bus.BusMessage(uniqueid="create", source_system="", source_module="")
m.generic.type = 1
m.generic.data = sdk.SDKIPCMessage(request=r).SerializeToString()
control("create-no-client-label", m)
m = bus.BusMessage(uniqueid="create", source_system="", source_module="")
m.generic.type = 2
m.generic.data = sdk.SDKIPCMessage(response=sdk.SDKIPCResponse(retval=0, retstatus="SUCCESS")).SerializeToString()
control("sdk-response", m)
r = sdk.SDKIPCRequest(version_major=1, version_minor=0, component=sdk.SDKComponent_SHARE)
r.isClientAliveRequest.SetInParent()
m = bus.BusMessage(uniqueid="alive", source_system="", source_module="")
m.generic.type = 1
m.generic.data = sdk.SDKIPCMessage(request=r).SerializeToString()
control("keepalive", m)
path = ROOT / "tests/HuntReplay.Tests/Fixtures/python-control-wire.json"
path.write_text(json.dumps(controls, indent=2)+"\n", encoding="utf-8")
print(path.relative_to(ROOT))
