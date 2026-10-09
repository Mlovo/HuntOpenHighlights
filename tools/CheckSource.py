"""Dependency-free static sanity check; not a substitute for a Windows compile or GPU test."""
from pathlib import Path
import hashlib
import json
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
errors = []

def require(condition, message):
    if not condition:
        errors.append(message)

for pattern in ("*.xaml", "*.csproj", "*.props", "*.manifest"):
    for path in ROOT.rglob(pattern):
        if any(part in {".cache", "obj", "vendor", "artifacts", ".git"} for part in path.parts):
            continue
        try:
            ET.parse(path)
        except ET.ParseError as exc:
            errors.append(f"{path.relative_to(ROOT)}: {exc}")

require(hashlib.sha256((ROOT / "bin/MessageBus.dll").read_bytes()).hexdigest() ==
        "64e300997b5796da22ad8baa49e4eb919cdc170157cbe6770279f94508a84650", "NVIDIA payload hash changed")
lock = json.loads((ROOT / "tools/ffmpeg.lock.json").read_text())
require(len(lock["sha256"]) == 64 and re.fullmatch("[0-9a-f]+", lock["sha256"]), "Unpinned FFmpeg archive")
require("latest" not in lock["url"], "Floating FFmpeg release URL is not allowed")

production = list((ROOT / "src").rglob("*.cs")) + list((ROOT / "native/src").glob("*.cpp")) + list((ROOT / "native/src").glob("*.h"))
for path in production:
    text = path.read_text(encoding="utf-8")
    require(not re.search(r"\b(?:SendInput|keybd_event|SendKeys|WriteProcessMemory|CreateRemoteThread)\s*\(", text),
            f"Forbidden injection/input API: {path.relative_to(ROOT)}")
    require(not re.search(r"\b(?:NotImplementedException|TODO|FIXME)\b", text), f"Unimplemented production placeholder: {path.relative_to(ROOT)}")
    require(not re.search(r"(?:obsws|obs-websocket|streamlabs)", text, flags=re.I), f"External recorder dependency: {path.relative_to(ROOT)}")
    require(not re.search(r"Process\.Start\s*\(\s*\"(?:ffmpeg|ffprobe|obs|python)", text, flags=re.I), f"External recording process: {path.relative_to(ROOT)}")

# Every native ABI export must have a corresponding managed import (hr_free included).
header = (ROOT / "native/include/huntreplay_media.h").read_text(encoding="utf-8")
imports = (ROOT / "src/HuntReplay.App/Interop/NativeMethods.cs").read_text(encoding="utf-8")
exports = set(re.findall(r"HR_CALL\s+(hr_\w+)\s*\(", header))
managed = set(re.findall(r"\b(hr_\w+)\s*\(", imports))
require(exports <= managed, "Missing managed ABI imports: " + str(exports - managed))

# Python-generated golden wire messages remain deterministic and present.
fixtures = json.loads((ROOT / "tests/HuntReplay.Tests/Fixtures/python-wire.json").read_text(encoding="utf-8"))
require([f["name"] for f in fixtures] == ["join", "create", "configure", "open", "kill", "config", "count", "close"], "Unexpected protocol fixtures")
controls = json.loads((ROOT / "tests/HuntReplay.Tests/Fixtures/python-control-wire.json").read_text(encoding="utf-8"))
require([f["name"] for f in controls] == ["join", "empty-ack", "echo-join-ack", "create-no-client-label", "sdk-response", "keepalive"], "Unexpected control-frame fixtures")
# Opaque BusMessage.uniqueid regression input: synthetic (generated from the Python proto modules), not a game trace.
synthetic = json.loads((ROOT / "tests/HuntReplay.Tests/Fixtures/python-synthetic-uniqueid-wire.json").read_text(encoding="utf-8"))
require(synthetic.get("synthetic") is True, "IPC uniqueid fixture must be labelled synthetic")
require([f["name"] for f in synthetic["frames"]] == ["join", "create", "configure", "open", "player-downed", "hunter-killed",
        "hunter-killed-retransmit", "config", "count", "close"], "Unexpected synthetic uniqueid fixtures")
# Pin the allocation policy which is incompatible with NV12 render-target arrays in FFmpeg 8.1.
video = (ROOT / "native/src/video.cpp").read_text(encoding="utf-8")
require(re.search(r"fc->initial_pool_size\s*=\s*0\s*;", video), "NV12 capture must use standalone dynamic-pool GPU textures")
# The application version must match wherever a user can see it. The native ABI (hr_abi_version) and the
# CMake project version are deliberately not bumped by app releases unless the native contract changes.
def version_in(path, pattern):
    match = re.search(pattern, (ROOT / path).read_text(encoding="utf-8"))
    return match.group(1) if match else None
versions = {
    "HuntReplay.App.csproj": version_in("src/HuntReplay.App/HuntReplay.App.csproj", r"<Version>([0-9.]+)</Version>"),
    "MainWindow footer": version_in("src/HuntReplay.App/MainWindow.xaml", r"STUDIO\s+/\s+([0-9.]+)"),
}
require(all(versions.values()) and len(set(versions.values())) == 1, f"Version mismatch or missing: {versions}")
if errors:
    raise SystemExit("\n".join(errors))
print(f"PASS: XML, pinned payloads, {len(production)} production source files, {len(exports)} ABI exports, golden fixtures.")
