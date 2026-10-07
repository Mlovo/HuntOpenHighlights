"""
HuntOpenHighlights - Recording & Capture Manager
Dedicated to OBS Studio Replay Buffer via WebSocket Protocol v5.
Zero keystroke emulation, lossless dynamic trimming, and smart multi-kill coalescing.
"""
import os
import sys
import json
import time
import threading
import subprocess
import re
from collections import Counter

DEFAULT_CONFIG = {
    "obs": {
        "host": "localhost",
        "port": 4455,
        "password": "",
        "post_event_delay_sec": 7.0,
        "max_coalesce_sec": 60.0,
        "auto_start_replay_buffer": True,
        "auto_rename_clips": True,
        "smart_trim": True,
        "min_clip_sec": 20.0
    },
    "enabled_events": [
        "HUNTER_KILLED",
        "PLAYER_DOWNED"
    ],
    "log_to_desktop": False
}

def trim_clip_lossless(src_path, target_duration, log_func=print):
    """
    Losslessly trims the beginning of a video clip to keep only the target combat duration.
    Uses -c copy (zero re-encoding, lossless stream copy, ~0.15s execution).
    """
    try:
        import imageio_ffmpeg
        ffmpeg_exe = imageio_ffmpeg.get_ffmpeg_exe()
    except Exception:
        ffmpeg_exe = "ffmpeg"

    # Probe duration
    try:
        probe = subprocess.run([ffmpeg_exe, "-i", src_path], capture_output=True, text=True, errors="ignore")
        m = re.search(r"Duration:\s*(\d+):(\d+):(\d+\.\d+)", probe.stderr)
        if not m:
            return False
        h, mins, s = map(float, m.groups())
        total_duration = h * 3600 + mins * 60 + s
    except Exception as e:
        log_func(f"[Recorder] Probe error: {e}")
        return False

    # Only trim if video is longer than target_duration + 3.0s buffer
    if total_duration <= target_duration + 3.0:
        return False

    cut_offset = max(0.0, total_duration - target_duration)
    tmp_path = src_path + ".tmp.mp4"

    cmd = [
        ffmpeg_exe,
        "-y",
        "-ss", str(round(cut_offset, 2)),
        "-i", src_path,
        "-c", "copy",
        tmp_path
    ]

    try:
        t0 = time.time()
        res = subprocess.run(cmd, capture_output=True, text=True, errors="ignore")
        if res.returncode == 0 and os.path.exists(tmp_path) and os.path.getsize(tmp_path) > 1000:
            os.replace(tmp_path, src_path)
            dt = time.time() - t0
            log_func(f"[Recorder] Lossless Smart Trim: reduced clip from {total_duration:.1f}s to {target_duration:.1f}s in {dt:.2f}s!")
            return True
        else:
            if os.path.exists(tmp_path):
                os.remove(tmp_path)
    except Exception as e:
        log_func(f"[Recorder] Trim error: {e}")
        if os.path.exists(tmp_path):
            try:
                os.remove(tmp_path)
            except Exception:
                pass
    return False

class HighlightRecorder:
    def __init__(self, config_path="config.json", log_callback=None):
        self.config_path = os.path.abspath(config_path)
        self.log_callback = log_callback or self._default_log
        self.config = self._load_config()
        self._lock = threading.Lock()
        self._timer = None
        self._pending_events = []
        self._batch_start_time = None
        self._obs_event_client = None
        self._pending_rename_events = None
        self._combat_start_time = None

        # Optional desktop log path
        user_profile = os.environ.get("USERPROFILE", "C:")
        self.desktop_log_path = os.path.join(user_profile, "Desktop", "HuntHighlights.log")

        obs_cfg = self.config.get("obs", {})
        self.log_callback(f"[Recorder] OBS WebSocket target: {obs_cfg.get('host', 'localhost')}:{obs_cfg.get('port', 4455)}")
        self.log_callback(f"[Recorder] Coalescing delay: {obs_cfg.get('post_event_delay_sec', 7.0)}s (max {obs_cfg.get('max_coalesce_sec', 60.0)}s)")
        if obs_cfg.get("smart_trim", True):
            self.log_callback("[Recorder] Lossless Smart Trim: ENABLED (dynamic clip duration)")

        # Connect and auto-start replay buffer in background thread
        threading.Thread(target=self._try_init_obs, daemon=True).start()

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

    def _try_init_obs(self):
        try:
            import obsws_python as obs
            obs_cfg = self.config.get("obs", {})
            host = obs_cfg.get("host", "localhost")
            port = obs_cfg.get("port", 4455)
            password = obs_cfg.get("password", "")

            # 1. Connect ReqClient to manage replay buffer
            cl = obs.ReqClient(host=host, port=port, password=password, timeout=2)
            try:
                status = cl.get_replay_buffer_status()
                if not status.output_active and obs_cfg.get("auto_start_replay_buffer", True):
                    cl.start_replay_buffer()
                    self.log_callback("[Recorder] Connected to OBS Studio: Replay Buffer started automatically!")
                else:
                    self.log_callback("[Recorder] Connected to OBS Studio: Replay Buffer is active.")
            except Exception as e:
                self.log_callback(f"[Recorder] OBS connected, but Replay Buffer not active in settings ({e}).")

            # 2. Connect EventClient for auto-renaming and smart trimming
            if self._obs_event_client is None and obs_cfg.get("auto_rename_clips", True):
                try:
                    self._obs_event_client = obs.EventClient(host=host, port=port, password=password, timeout=2)
                    self._obs_event_client.callback.register(self.on_replay_buffer_saved)
                    self.log_callback("[Recorder] OBS auto-renaming listener active.")
                except Exception as e:
                    self.log_callback(f"[Recorder] Notice: OBS EventClient listener not started: {e}")
        except Exception:
            # OBS not open yet; will connect on demand when events arrive
            pass

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

        obs_cfg = self.config.get("obs", {})
        base_delay = obs_cfg.get("post_event_delay_sec", 7.0)
        max_coalesce = obs_cfg.get("max_coalesce_sec", 60.0)

        # Calculate time to wait: game's endDelta + buffer
        game_delay = (end_delta_ms / 1000.0) + 1.0 if end_delta_ms > 0 else 0
        wait_time = max(base_delay, game_delay)

        with self._lock:
            now = time.time()
            self._pending_events.append(highlight_id)

            # Track start of combat sequence based on game startDelta
            event_start_delta = (start_delta_ms / 1000.0) if start_delta_ms else -20.0
            if self._combat_start_time is None:
                self._combat_start_time = now + event_start_delta

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
            combat_start = self._combat_start_time
            self._combat_start_time = None

        if not events:
            return

        now = time.time()
        min_sec = self.config.get("obs", {}).get("min_clip_sec", 20.0)
        if combat_start:
            target_duration = max(min_sec, (now - combat_start) + 1.0)
        else:
            target_duration = 25.0

        events_summary = ", ".join(events)
        self.log_callback(f"[Recorder] >>> Triggering replay capture for: [{events_summary}] (dynamic duration: {target_duration:.1f}s) <<<")
        self._save_obs(events, target_duration)

    def on_replay_buffer_saved(self, data):
        orig_path = getattr(data, "saved_replay_path", None)
        if not orig_path:
            return

        with self._lock:
            payload = self._pending_rename_events
            self._pending_rename_events = None

        if isinstance(payload, tuple) and len(payload) == 2:
            events, target_duration = payload
        elif payload:
            events = list(payload)
            target_duration = 25.0
        else:
            events = ["HIGHLIGHT"]
            target_duration = 25.0

        if not self.config.get("obs", {}).get("auto_rename_clips", True):
            self.log_callback(f"[Recorder] OBS Replay saved: {os.path.basename(orig_path)}")
            final_path = orig_path
        else:
            counts = Counter(events)
            tag_parts = []
            for ev, cnt in counts.items():
                if cnt > 1:
                    tag_parts.append(f"{ev}_x{cnt}")
                else:
                    tag_parts.append(ev)
            tag = "_".join(tag_parts)

            dir_name = os.path.dirname(orig_path)
            base_name = os.path.basename(orig_path)

            if not base_name.startswith("Hunt_"):
                new_name = f"Hunt_{tag}_{base_name}"
                new_path = os.path.join(dir_name, new_name)
                
                # Retry loop in case OBS has the file handle temporarily open
                renamed = False
                for _ in range(15):
                    time.sleep(0.2)
                    try:
                        os.rename(orig_path, new_path)
                        renamed = True
                        break
                    except PermissionError:
                        continue
                    except Exception as e:
                        self.log_callback(f"[Recorder] Rename error: {e}")
                        break

                if renamed:
                    self.log_callback(f"[Recorder] SUCCESS: Replay saved & renamed to -> {new_name}")
                    final_path = new_path
                else:
                    self.log_callback(f"[Recorder] Replay saved as {base_name} (file busy)")
                    final_path = orig_path
            else:
                self.log_callback(f"[Recorder] SUCCESS: Replay saved: {base_name}")
                final_path = orig_path

        # If smart_trim is enabled, dynamically trim unneeded dead-air before the fight
        if self.config.get("obs", {}).get("smart_trim", True) and target_duration and os.path.exists(final_path):
            trim_clip_lossless(final_path, target_duration, log_func=self.log_callback)

    def _save_obs(self, events, target_duration=25.0):
        try:
            import obsws_python as obs
        except ImportError:
            self.log_callback("[Recorder] ERROR: 'obsws-python' package is required for OBS mode. Run: pip install obsws-python")
            return

        obs_cfg = self.config.get("obs", {})
        host = obs_cfg.get("host", "localhost")
        port = obs_cfg.get("port", 4455)
        password = obs_cfg.get("password", "")

        with self._lock:
            self._pending_rename_events = (list(events), target_duration)

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
            self.log_callback("[Recorder] Command sent: OBS Replay Buffer saving to disk...")
        except Exception as e:
            self.log_callback(f"[Recorder] ERROR communicating with OBS Studio: {e}")
