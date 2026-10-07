"""
HuntOpenHighlights - NVIDIA Highlights Emulator for AMD & non-NVIDIA GPUs.
Listens on the native NVIDIA MessageBus pipe and captures game highlight events.
"""
import os
import sys
import time
import uuid
import struct
import threading
import win32pipe
import win32file
import pywintypes

# Ensure proto directory is in path
CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
PROTO_DIR = os.path.join(CURRENT_DIR, "proto")
if PROTO_DIR not in sys.path:
    sys.path.insert(0, PROTO_DIR)

import BusMessage_pb2
import sdk_pb2
from recorder import HighlightRecorder

PIPE_NAME = r"\\.\pipe\NvMessageBusBroadcast"

def log(msg):
    ts = time.strftime("%H:%M:%S")
    print(f"[{ts}] {msg}", flush=True)

# Initialize capture recorder manager (ReLive / OBS)
recorder = HighlightRecorder(log_callback=log)

def send_bus_message(pipe, msg):
    payload = msg.SerializeToString()
    header = struct.pack(">I", len(payload))
    win32file.WriteFile(pipe, header)
    win32file.WriteFile(pipe, payload)

def handle_client(pipe, client_id):
    log(f"Game process #{client_id} CONNECTED to NVIDIA MessageBus pipe!")
    try:
        while True:
            # Read 4-byte big-endian message length header
            hr, header = win32file.ReadFile(pipe, 4)
            if not header or len(header) < 4:
                log(f"Client #{client_id} disconnected (EOF on header).")
                break
            length = struct.unpack(">I", header)[0]

            # Read exact message payload
            chunks = []
            bytes_left = length
            while bytes_left > 0:
                hr, chunk = win32file.ReadFile(pipe, min(bytes_left, 65536))
                if not chunk:
                    break
                chunks.append(chunk)
                bytes_left -= len(chunk)

            payload = b"".join(chunks)
            if len(payload) < length:
                log(f"Client #{client_id} sent incomplete payload: {len(payload)}/{length}")
                break

            bus_msg = BusMessage_pb2.BusMessage()
            bus_msg.ParseFromString(payload)

            # Handle bus JOIN announcement
            if bus_msg.joined:
                log(f"RECV JOIN from: {bus_msg.source_system}:{bus_msg.source_module} (uid: {bus_msg.uniqueid})")

                # 1. Broadcaster replies to sender with reference to their uniqueid
                join_reply = BusMessage_pb2.BusMessage()
                join_reply.uniqueid = str(uuid.uuid4())
                join_reply.reference_uniqueid = bus_msg.uniqueid
                join_reply.source_system = "MessageBusBroadcast"
                join_reply.source_module = "Broadcaster"
                join_reply.target_system = bus_msg.source_system
                join_reply.target_module = bus_msg.source_module
                join_reply.status.code = BusMessage_pb2.BusMessage.Status.Completed

                # Announce NvNode:SDKAPI peer
                p = join_reply.bus_peer.add()
                p.system = "NvNode"
                p.module = "SDKAPI"
                send_bus_message(pipe, join_reply)
                log("  -> Sent JOIN response with NvNode:SDKAPI peer list.")

                # 2. Presence announcement from NvNode:SDKAPI
                nv_join = BusMessage_pb2.BusMessage()
                nv_join.uniqueid = str(uuid.uuid4())
                nv_join.source_system = "NvNode"
                nv_join.source_module = "SDKAPI"
                nv_join.joined = True
                send_bus_message(pipe, nv_join)
                log("  -> Sent NvNode:SDKAPI online announcement.")

            elif bus_msg.HasField("generic") and bus_msg.generic.data:
                try:
                    sdk_msg = sdk_pb2.SDKIPCMessage()
                    sdk_msg.ParseFromString(bus_msg.generic.data)
                    which = sdk_msg.WhichOneof("message")

                    if which == "request":
                        req = sdk_msg.request
                        req_type = req.WhichOneof("request")
                        log(f"RECV SDK Request: {req_type}")

                        # Standard SUCCESS response
                        resp_sdk = sdk_pb2.SDKIPCMessage()
                        resp_sdk.response.retval = 0  # NVGSDK_SUCCESS
                        resp_sdk.response.retstatus = "SUCCESS"

                        if req_type == "createSDKInstanceRequest":
                            cr = req.createSDKInstanceRequest
                            log(f"  [CreateSDKInstanceRequest] game: '{cr.gameName}', client: '{cr.client}', pid: {cr.clientPID}")
                            inst_resp = resp_sdk.response.createSDKInstanceResponse
                            inst_resp.installedGfeVersion = "3.28.0.412"
                            inst_resp.installedGfeSDKIPCVersionMajor = 1
                            inst_resp.installedGfeSDKIPCVersionMinor = 0

                            # Grant all requested permission scopes
                            for sc in (cr.scopes if cr.scopes else [1, 2, 3, 4, 5, 6, 7]):
                                sp = inst_resp.scopePermission.add()
                                sp.scope = sc
                                sp.permission = 1  # Granted

                        elif req_type == "configureHighlightsRequest":
                            ch = req.configureHighlightsRequest
                            log(f"  [ConfigureHighlightsRequest] Registered {len(ch.highlightTable)} highlight types:")
                            for h in ch.highlightTable:
                                log(f"    - {h.highlightId}")

                        elif req_type == "openGroupRequest":
                            og = req.openGroupRequest
                            log(f"  [OpenGroupRequest] GroupId: '{og.groupId}' (Match Started)")

                        elif req_type == "closeGroupRequest":
                            cg = req.closeGroupRequest
                            log(f"  [CloseGroupRequest] GroupId: '{cg.groupId}' (Match Ended)")

                        elif req_type == "setVideoHighlightRequest":
                            vh = req.setVideoHighlightRequest
                            recorder.trigger_highlight("VIDEO", vh.groupId, vh.highlightId, vh.startDelta, vh.endDelta)

                        elif req_type == "setScreenshotHighlightRequest":
                            sh = req.setScreenshotHighlightRequest
                            recorder.trigger_highlight("SCREENSHOT", sh.groupId, sh.highlightId, 0, 0)

                        elif req_type == "getUserConfigRequest":
                            uc = resp_sdk.response.getUserConfigResponse
                            uc.gameEnabled = True

                        elif req_type == "getUILanguageRequest":
                            resp_sdk.response.getUILanguageResponse.localeName = "en-US"

                        elif req_type == "isClientAliveRequest":
                            resp_sdk.response.isClientAliveResponse.alive = True

                        elif req_type == "openGroupSummaryRequest":
                            log("  [OpenGroupSummaryRequest] Game opened Highlights Gallery Summary in UI.")

                        # Send back wrapped response
                        reply_bus = BusMessage_pb2.BusMessage()
                        reply_bus.uniqueid = str(uuid.uuid4())
                        reply_bus.reference_uniqueid = bus_msg.uniqueid
                        reply_bus.source_system = "NvNode"
                        reply_bus.source_module = "SDKAPI"
                        reply_bus.target_system = bus_msg.source_system
                        reply_bus.target_module = bus_msg.source_module
                        reply_bus.status.code = BusMessage_pb2.BusMessage.Status.Completed

                        reply_bus.generic.type = 2  # Response
                        reply_bus.generic.data = resp_sdk.SerializeToString()

                        send_bus_message(pipe, reply_bus)
                        log(f"  -> Replied with SUCCESS to {req_type}.")

                except Exception as e:
                    log(f"Error processing SDK message: {e}")
            else:
                log(f"RECV generic BusMessage (uid: {bus_msg.uniqueid})")

    except pywintypes.error as e:
        log(f"Client #{client_id} pipe error: {e}")
    except Exception as e:
        log(f"Client #{client_id} exception: {e}")
    finally:
        win32file.CloseHandle(pipe)
        log(f"Client #{client_id} connection closed.")

def pipe_server():
    client_id = 0
    while True:
        try:
            pipe = win32pipe.CreateNamedPipe(
                PIPE_NAME,
                win32pipe.PIPE_ACCESS_DUPLEX,
                win32pipe.PIPE_TYPE_BYTE | win32pipe.PIPE_READMODE_BYTE | win32pipe.PIPE_WAIT,
                win32pipe.PIPE_UNLIMITED_INSTANCES,
                65536,
                65536,
                0,
                None
            )
            log(f"Pipe server listening on {PIPE_NAME}...")
            win32pipe.ConnectNamedPipe(pipe, None)
            client_id += 1
            t = threading.Thread(target=handle_client, args=(pipe, client_id), daemon=True)
            t.start()
        except Exception as e:
            log(f"Server loop error: {e}")
            time.sleep(1)

def main():
    print("=" * 64)
    print("  HuntOpenHighlights - NVIDIA Highlights Emulator for AMD GPUs")
    print("=" * 64)
    print(f"[*] Pipe: {PIPE_NAME}")
    print(f"[*] Capture Mode: {recorder.config.get('mode', 'relive').upper()}")
    if recorder.config.get('log_to_desktop', False):
        print(f"[*] Desktop Log: {recorder.desktop_log_path}")
    print("[*] Ready. Launch Hunt: Showdown 1896 to begin.\n")

    t = threading.Thread(target=pipe_server, daemon=True)
    t.start()

    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        print("\n[*] Shutting down HuntOpenHighlights server.")

if __name__ == "__main__":
    main()
