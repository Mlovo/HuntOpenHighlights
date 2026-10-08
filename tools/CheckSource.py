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
    text = path.read_text()
    require(not re.search(r"\b(?:SendInput|keybd_event|SendKeys|WriteProcessMemory|CreateRemoteThread)\s*\(", text),
            f"Forbidden injection/input API: {path.relative_to(ROOT)}")
    require(not re.search(r"\b(?:NotImplementedException|TODO|FIXME)\b", text), f"Unimplemented production placeholder: {path.relative_to(ROOT)}")
    require(not re.search(r"(?:obsws|obs-websocket|streamlabs)", text, flags=re.I), f"External recorder dependency: {path.relative_to(ROOT)}")
    require(not re.search(r"Process\.Start\s*\(\s*\"(?:ffmpeg|ffprobe|obs|python)", text, flags=re.I), f"External recording process: {path.relative_to(ROOT)}")

# Every native ABI export must have a corresponding managed import (hr_free included).
header = (ROOT / "native/include/huntreplay_media.h").read_text()
imports = (ROOT / "src/HuntReplay.App/Interop/NativeMethods.cs").read_text()
exports = set(re.findall(r"HR_CALL\s+(hr_\w+)\s*\(", header))
managed = set(re.findall(r"\b(hr_\w+)\s*\(", imports))
require(exports <= managed, "Missing managed ABI imports: " + str(exports - managed))

# Python-generated golden wire messages remain deterministic and present.
fixtures = json.loads((ROOT / "tests/HuntReplay.Tests/Fixtures/python-wire.json").read_text())
require([f["name"] for f in fixtures] == ["join", "create", "configure", "open", "kill", "config", "count", "close"], "Unexpected protocol fixtures")
if errors:
    raise SystemExit("\n".join(errors))
print(f"PASS: XML, pinned payloads, {len(production)} production source files, {len(exports)} ABI exports, golden fixtures.")
