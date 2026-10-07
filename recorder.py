"""
HuntOpenHighlights - Recording & Capture Manager
Handles automated capture for AMD ReLive and OBS Studio Replay Buffer
with intelligent multi-kill coalescing (debounce).
"""
import os
import sys
import json
import time
import threading
import winreg
import win32api
import win32con

VK_MAP = {
    # Modifiers
    "ctrl": win32con.VK_CONTROL,
    "control": win32con.VK_CONTROL,
    "shift": win32con.VK_SHIFT,
    "alt": win32con.VK_MENU,
    # Isolated / special keys
    "scroll_lock": win32con.VK_SCROLL,
    "scrolllock": win32con.VK_SCROLL,
    "scroll": win32con.VK_SCROLL,
    "pause": win32con.VK_PAUSE,
    "break": win32con.VK_PAUSE,
    "insert": win32con.VK_INSERT,
    "delete": win32con.VK_DELETE,
    "home": win32con.VK_HOME,
    "end": win32con.VK_END,
    "pageup": win32con.VK_PRIOR,
    "pagedown": win32con.VK_NEXT,
    "printscreen": win32con.VK_SNAPSHOT,
    "prntscrn": win32con.VK_SNAPSHOT,
    # Function keys (F1 - F24)
    "f1": win32con.VK_F1,
    "f2": win32con.VK_F2,
    "f3": win32con.VK_F3,
    "f4": win32con.VK_F4,
    "f5": win32con.VK_F5,
    "f6": win32con.VK_F6,
    "f7": win32con.VK_F7,
    "f8": win32con.VK_F8,
    "f9": win32con.VK_F9,
    "f10": win32con.VK_F10,
    "f11": win32con.VK_F11,
    "f12": win32con.VK_F12,
    "f13": 0x7C,
    "f14": 0x7D,
    "f15": 0x7E,
    "f16": 0x7F,
    "f17": 0x80,
    "f18": 0x81,
    "f19": 0x82,
    "f20": 0x83,
    "f21": 0x84,
    "f22": 0x85,
    "f23": 0x86,
    "f24": 0x87,
    # Numpad
    "numpad0": win32con.VK_NUMPAD0,
    "numpad1": win32con.VK_NUMPAD1,
    "numpad2": win32con.VK_NUMPAD2,
    "numpad3": win32con.VK_NUMPAD3,
    "numpad4": win32con.VK_NUMPAD4,
    "numpad5": win32con.VK_NUMPAD5,
    "numpad6": win32con.VK_NUMPAD6,
    "numpad7": win32con.VK_NUMPAD7,
    "numpad8": win32con.VK_NUMPAD8,
    "numpad9": win32con.VK_NUMPAD9,
}

DEFAULT_CONFIG = {
    "mode": "relive",
    "relive": {
        "hotkey": "auto",
        "post_event_delay_sec": 7.0,
        "max_coalesce_sec": 60.0
    },
    "obs": {
        "host": "localhost",
        "port": 4455,
        "password": "",
        "post_event_delay_sec": 7.0,
        "max_coalesce_sec": 60.0,
        "auto_start_replay_buffer": True
    },
    "enabled_events": [
        "HUNTER_KILLED",
        "PLAYER_DOWNED"
    ],
    "log_to_desktop": False
}

def get_amd_registered_hotkey():
    """Reads the current Instant Replay hotkey configured in AMD Adrenalin from Windows Registry."""
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\AMD\DVR") as key:
            val, _ = winreg.QueryValueEx(key, "SaveInstantReplayHotkey")
            if val:
                parts = []
                if "," in val:
                    mods, k = val.split(",", 1)
                    for m in mods.split("+"):
                        m_str = m.strip().lower()
                        if m_str and m_str != "none":
                            parts.append(m_str)
                    if k.strip():
                        parts.append(k.strip().lower())
                else:
                    parts.append(val.strip().lower())
                return parts
    except Exception:
        pass
    return None

def resolve_key(key_str):
    k = str(key_str).lower().strip().replace(" ", "_")
    if k in VK_MAP:
        return VK_MAP[k]
    if len(k) == 1:
        return ord(k.upper())
    raise ValueError(f"Unknown key in hotkey config: '{key_str}'")

def press_hotkey(keys):
    vk_codes = [resolve_key(k) for k in keys]
    # Key Down in order
    for vk in vk_codes:
        scan = win32api.MapVirtualKey(vk, 0)
        win32api.keybd_event(vk, scan, 0, 0)
    time.sleep(0.06)
    # Key Up in reverse order
    for vk in reversed(vk_codes):
        scan = win32api.MapVirtualKey(vk, 0)
        win32api.keybd_event(vk, scan, win32con.KEYEVENTF_KEYUP, 0)

class HighlightRecorder:
    def __init__(self, config_path="config.json", log_callback=None):
        self.config_path = os.path.abspath(config_path)
        self.log_callback = log_callback or self._default_log
        self.config = self._load_config()
        self._lock = threading.Lock()
        self._timer = None
        self._pending_events = []
        self._batch_start_time = None
        self.relive_hotkey = ["ctrl", "shift", "s"]

        # Optional desktop log path
        user_profile = os.environ.get("USERPROFILE", "C:")
        self.desktop_log_path = os.path.join(user_profile, "Desktop", "HuntHighlights.log")

        mode = self.config.get("mode", "relive").lower()
        self.log_callback(f"[Recorder] Mode initialized: '{mode.upper()}'")
        if mode == "relive":
            configured_hotkey = self.config.get("relive", {}).get("hotkey", "auto")
            if configured_hotkey == "auto" or not configured_hotkey:
                detected = get_amd_registered_hotkey()
                if detected:
                    self.relive_hotkey = detected
                    self.log_callback(f"[Recorder] Auto-detected AMD ReLive hotkey from Windows Registry: {'+'.join(detected).upper()}")
                else:
                    self.relive_hotkey = ["ctrl", "shift", "s"]
            elif isinstance(configured_hotkey, list):
                self.relive_hotkey = configured_hotkey
            else:
                self.relive_hotkey = [str(configured_hotkey)]

            hotkey_str = "+".join(self.relive_hotkey).upper()
            self.log_callback(f"[Recorder] Active AMD ReLive hotkey: {hotkey_str}")
            self.log_callback(f"[Recorder] Coalescing delay: {self.config['relive']['post_event_delay_sec']}s (max {self.config['relive']['max_coalesce_sec']}s)")
        elif mode == "obs":
            self.log_callback(f"[Recorder] OBS WebSocket target: {self.config['obs']['host']}:{self.config['obs']['port']}")
            self.log_callback(f"[Recorder] Coalescing delay: {self.config['obs']['post_event_delay_sec']}s")

    def _default_log(self, msg):
        ts = time.strftime("%H:%M:%S")
        print(f"[{ts}] {msg}", flush=True)

    def _load_config(self):
        if not os.path.exists(self.config_path):
            try:
                with open(self.config_path, "w", encoding="utf-8") as f:
                    json.dump(DEFAULT_CONFIG, f, indent=4, ensure_ascii=False)
                return DEFAULT_CONFIG.copy()
            except Exception as e:
                self.log_callback(f"[Recorder] Failed to create default config: {e}")
                return DEFAULT_CONFIG.copy()
        try:
            with open(self.config_path, "r", encoding="utf-8") as f:
                data = json.load(f)
            config = DEFAULT_CONFIG.copy()
            config.update(data)
            return config
        except Exception as e:
            self.log_callback(f"[Recorder] Error reading {self.config_path}: {e}. Using defaults.")
            return DEFAULT_CONFIG.copy()

    def trigger_highlight(self, event_type, group_id, highlight_id, start_delta_ms, end_delta_ms):
        ts = time.strftime("%Y-%m-%d %H:%M:%S")
        self.log_callback("=" * 64)
        self.log_callback(f"*** HIGHLIGHT EVENT DETECTED: {highlight_id} (Window: {start_delta_ms}ms .. +{end_delta_ms}ms) ***")
        self.log_callback("=" * 64)

        # Optional desktop file logging
        if self.config.get("log_to_desktop", False):
            try:
                line = f"[{ts}] EVENT: {event_type} | GROUP: {group_id} | ID: {highlight_id} | START: {start_delta_ms}ms | END: {end_delta_ms}ms\n"
                with open(self.desktop_log_path, "a", encoding="utf-8") as f:
                    f.write(line)
                self.log_callback(f"[Recorder] Logged event to: {self.desktop_log_path}")
            except Exception as e:
                self.log_callback(f"[Recorder] Error writing desktop log: {e}")

        # Check if recording is enabled for this event
        enabled_events = self.config.get("enabled_events", [])
        if enabled_events and highlight_id not in enabled_events:
            self.log_callback(f"[Recorder] Event '{highlight_id}' is not in enabled_events list. Skipping capture.")
            return

        mode = self.config.get("mode", "relive").lower()
        if mode == "disabled":
            self.log_callback("[Recorder] Recording mode is set to 'disabled'.")
            return

        # Coalescing calculation
        cfg_section = self.config.get(mode, {})
        base_delay = cfg_section.get("post_event_delay_sec", 7.0)
        max_coalesce = cfg_section.get("max_coalesce_sec", 60.0)

        # Calculate time to wait: game's endDelta + buffer
        game_delay = (end_delta_ms / 1000.0) + 1.0 if end_delta_ms > 0 else 0
        wait_time = max(base_delay, game_delay)

        with self._lock:
            now = time.time()
            self._pending_events.append(highlight_id)

            if self._batch_start_time is None:
                self._batch_start_time = now

            batch_duration = now - self._batch_start_time

            # Check if we should extend or finalize
            if self._timer and self._timer.is_alive():
                if batch_duration + wait_time <= max_coalesce:
                    self._timer.cancel()
                    self.log_callback(
                        f"[Recorder] Multi-kill detected! Batch so far: {self._pending_events}. "
                        f"Extending replay timer by {wait_time:.1f}s (Batch duration: {batch_duration:.1f}s)..."
                    )
                    self._timer = threading.Timer(wait_time, self._execute_save)
                    self._timer.daemon = True
                    self._timer.start()
                else:
                    self.log_callback(
                        f"[Recorder] Max coalesce window ({max_coalesce}s) reached. Letting current timer finish."
                    )
            else:
                self.log_callback(
                    f"[Recorder] Queueing replay save in {wait_time:.1f}s for: {highlight_id}..."
                )
                self._timer = threading.Timer(wait_time, self._execute_save)
                self._timer.daemon = True
                self._timer.start()

    def _execute_save(self):
        with self._lock:
            events = list(self._pending_events)
            self._pending_events.clear()
            self._batch_start_time = None
            self._timer = None

        if not events:
            return

        mode = self.config.get("mode", "relive").lower()
        events_summary = ", ".join(events)
        self.log_callback(f"[Recorder] >>> Triggering replay capture for: [{events_summary}] <<<")

        if mode == "relive":
            self._save_relive(events)
        elif mode == "obs":
            self._save_obs(events)
        else:
            self.log_callback(f"[Recorder] Unknown recording mode: '{mode}'")

    def _save_relive(self, events):
        hotkey_str = "+".join(self.relive_hotkey).upper()
        try:
            press_hotkey(self.relive_hotkey)
            self.log_callback(f"[Recorder] SUCCESS: Sent AMD ReLive shortcut ({hotkey_str})! Replay saved.")
        except Exception as e:
            self.log_callback(f"[Recorder] ERROR sending ReLive hotkey: {e}")

    def _save_obs(self, events):
        try:
            import obsws_python as obs
        except ImportError:
            self.log_callback("[Recorder] ERROR: 'obsws-python' package is required for OBS mode. Run: pip install obsws-python")
            return

        obs_cfg = self.config.get("obs", {})
        host = obs_cfg.get("host", "localhost")
        port = obs_cfg.get("port", 4455)
        password = obs_cfg.get("password", "")

        try:
            cl = obs.ReqClient(host=host, port=port, password=password, timeout=3)
            # Check buffer status
            status = cl.get_replay_buffer_status()
            if not status.output_active:
                if obs_cfg.get("auto_start_replay_buffer", True):
                    self.log_callback("[Recorder] OBS Replay Buffer was inactive. Starting it now...")
                    cl.start_replay_buffer()
                    time.sleep(1.0)
                else:
                    self.log_callback("[Recorder] WARNING: OBS Replay Buffer is not running!")
                    return

            cl.save_replay_buffer()
            self.log_callback("[Recorder] SUCCESS: OBS Replay Buffer saved via WebSocket!")
        except Exception as e:
            self.log_callback(f"[Recorder] ERROR communicating with OBS Studio: {e}")
