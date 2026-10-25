# Changelog

All notable changes to **HuntReplay Studio** are documented in this file.

## [0.2.1] - 2026-10-25

### Added
- **Select several clips**: tick the circle on a clip, or Ctrl- and Shift-click. Shift fills everything between the selected clips and the one you click; Ctrl+A selects all. Delete them with one confirmation: a clip that is open in another program is skipped and reported, the rest are deleted.
- **Selection mode**: with two or more clips selected, clicking a clip plays it without losing the selection. A bar next to the search box shows how many clips are selected, with Delete selected and Clear selection (or Esc).
- **Clip menu**: the ... button on a clip, a right-click or Shift+F10 opens Trim, Rename (F2), Show in Explorer, Open with system player and Delete (Del).

### Changed
- Redesigned clip cards: the preview fills the card with the event and length on it, and the title is the date and time, or your own name for a renamed clip. The play button, selection circle and menu appear when you point at a card.
- Keyboard in the gallery: arrows move between clips, Enter plays or pauses, Delete and F2 work on the selection.
- The buttons under the player act on the clip in the player, which its card marks "In player".
- Dashboard: the sidebar tagline is gone, recent highlights keep three columns, and the session log can be collapsed.
- Lower memory use when the library refreshes during a match.

### Fixed
- Saving a trimmed fragment over an existing clip and then cancelling, or hitting an error, deleted that clip. The file is now replaced only once the fragment is finished.
- If the Highlights connection was busy at startup or dropped later, Studio stopped receiving game events until restarted. It now reconnects.
- Cancelling a fragment export could break a newer one, which then could no longer be cancelled.
- Interrupted exports left hidden .partial.mp4 files in the clips folder; they are now cleaned up.
- Error messages could end with an old, unrelated FFmpeg warning.

---

## [0.2.0] - 2026-10-18

### Added
- **In-place updates**: the header update button downloads the new HuntReplayStudio.exe from the latest GitHub release, verifies its SHA-256 digest, replaces the running executable and restarts. The download can be cancelled; any failure is explained with options to retry or open the releases page.
- **Support card** in the sidebar with a GitHub link and wallet addresses to copy.

### Changed
- Release notes in the update confirmation are shown as plain text, without raw links or Markdown.
- Gallery hides clip details until a clip is selected and trims long labels; ComboBox text trims instead of overflowing.

### Fixed
- Recordings could show teal or green bars around the picture: captured frames now fill the output instead of being letterboxed.
- A saved GPU is resolved by ID, name or legacy index, so Settings and the capture engine agree after upgrades or adapter reordering.
- Mouse wheel over the gallery clip list did not scroll the page.

---

## [0.1.9] - 2026-10-16

### Added
- **Notifications over the game** (Settings → System, off by default): a small click-through notice when a clip is saved, the replay buffer starts or stops, or recording fails. It never takes focus from the game and is never recorded into clips. Choice of corner and duration, with a preview button. Not shown while a game runs in exclusive fullscreen, where a window on top can freeze it; borderless and windowed modes show it.
- **More output resolutions**: 720p, 900p, 1080p, 1200p, 1440p, 1600p and 2160p. Each keeps the monitor's aspect ratio (1080p on a 3440 × 1440 monitor records 2580 × 1080 instead of letterboxing), and resolutions above the monitor's own are hidden. Existing 1080p / 1440p / 2160p settings carry over.
- **Language switch in the first-run setup wizard**.

### Changed
- A new install starts in the Windows display language (Russian or English).
- Default bitrate raised from 25 to 40 Mbit/s: Hunt is often very dark and low rates smear shadows. Existing settings keep their bitrate.
- Encoders use recording-oriented presets; frame pacing uses a high-resolution timer.
- Audio follows a change of the default Windows output device during recording.
- Trimming re-encodes on the GPU encoder when one is available; the built-in player decodes on the GPU and refreshes in step with the display.
- Lower memory churn while buffering; many internal performance and readability improvements.
- The NVIDIA container is paused only when it actually holds the Highlights pipe.

### Fixed
- The chosen language was lost after a restart when settings were saved later.
- Unsaved-settings question needed a second click before it switched pages.
- During a trimmed-fragment preview, the playback position could start slightly before the fragment.
- Player restart after the end of a clip could crash; portable mode registered trimmed clips in the wrong library; clip folders on network shares failed exports; QSV keyframes; several smaller audit fixes.

---

## [0.1.6] - 2026-10-11

### Added
- **Interactive Video Trimming in Gallery**:
  - Two-handled interactive range slider to trim start and end points directly in the built-in video player.
  - Keyframe-accurate lossless stream-copy trimming via native FFmpeg backend without re-encoding.
  - Dedicated "Trim" button with progressive timecode readout (`00:00 / 00:00`), live slider feedback, auto-pausing playback on thumb drag to eliminate jitter, and automatic thumbnail update after trimming.
- **Unsaved Settings Confirmation Dialog**:
  - Built-in detection of pending configuration changes (`HasUnsavedSettings`).
  - Safe confirmation prompt (*"You have unsaved changes in settings. Would you like to save them before leaving?"* / *"У вас есть несохранённые изменения в настройках. Сохранить их перед переходом?"*) with `[Yes]` / `[No]` / `[Cancel]` actions when switching tabs within Settings, navigating away to other pages (Dashboard, Gallery, Diagnostics), or closing/minimizing the window.
  - Selecting "Yes" validates and applies settings cleanly; "No" discards modifications; "Cancel" aborts navigation and remains on the current view.
- **Light & Dark Theme Switcher & Full Bilingual Localization**:
  - Instant one-click theme switcher (`🌙 DARK` / `☀️ LIGHT`) with live palette swap across all UI controls.
  - Complete Russian and English language support across all dialogs, controls, navigation, and system notifications.

### Fixed
- **Monitor Capture Alt-Tab & Minimize Robustness**:
  - Fixed an issue where minimizing the game window (Alt-Tab) interrupted the recording buffer when "Selected Monitor" capture was active. Minimized window states (`IsIconic`) and active game process lifetimes (`HasGameProcess`) are now accurately tracked.
- **Settings Switch Buffer Continuation**:
  - Resolved buffer restart race condition when switching capture source from Game Window to Monitor during an active match.
- **Gallery Clip Reload on Navigation**:
  - Fixed clip viewer state retention when switching between views during video playback.

---

## [0.1.5] - 2026-10-10

### Added
- **Full Bilingual Localization (Russian & English)**:
  - Instant one-click language toggle button (`RU / EN` / `EN / RU`) in the header bar.
  - Complete translations across all views: Dashboard, Clip Gallery, Settings, Diagnostics, Setup Wizard, and System Tray menu.
  - Persistent language preference saved in `config.json`.
- **Light & Dark Theme Switcher**:
  - One-click theme toggle button (`🌙 DARK` / `☀️ LIGHT`) with live theme switching without app restart.
  - Full modern Light palette matching studio aesthetic (`#F2F5F3` background, `#FFFFFF` cards, `#3C662E` accents).
  - Clean video screen background in empty player state with localized hint icons.
  - Persistent theme preference saved in `config.json`.
- **Automatic GitHub Update Checker**:
  - Built-in GitHub Releases update check with header badge notifications (`UPDATE AVAILABLE ↗`).
  - Manual "Check for updates" button in Settings with localized status feedback.
- **Adaptive Gallery Grid**:
  - Replaced fixed-width wrap panel with an adaptive `UniformGrid`.
  - Automatically calculates column count and expands tile widths to symmetrically fill available window width across any screen resolution and window size.

### Fixed
- **Settings ComboBox Localization Bug**: Fixed an issue where changing languages would clear combobox selections (Monitor, Audio Output, Microphone, Resolution, Encoder, Codec). Selections now smoothly rebind and preserve saved IDs across language switches.
- **Dynamic Theme Resourcing**: Converted all static resource bindings across views to dynamic resources, ensuring complete, artifact-free theme swaps in both directions (Dark ➔ Light and Light ➔ Dark).
- **Audio Slider Mouse Wheel Sensitivity**: Volume slider scrolling now uses smooth 5% increments with localized tooltip indicators.

---

## [0.1.4] - 2026-10-10

### Added
- **In-process Native Video Player**:
  - Embedded FFmpeg video playback engine with smooth seeking, Play/Pause toggle, and direct frame rendering via `WriteableBitmap`.
- **Hover Thumbnail Scrubbing**:
  - Floating preview thumbnail card tracking seek bar mouse hover.
- **Volume & Mute Controls**:
  - Volume slider and one-click mute/unmute with mouse wheel support.
- **Clip Management**:
  - Rename clip with safe title sanitization, delete clip with confirmation, and open in external system player.
- **NVIDIA Container Auto-Mitigation**:
  - Automated detection and graceful pausing/restoring of `NvContainerLocalSystem` service during runtime to prevent IPC conflicts.

---

## [0.1.3] - 2026-10-10

### Initial Release
- Standalone portable native recorder for Hunt: Showdown 1896.
- Pure Windows Graphics Capture (WGC) + DirectX 11 capture pipeline.
- Circular RAM ring buffer with keyframe-accurate lossless MP4 export.
- Hardware encoding via NVIDIA NVENC, AMD AMF, Intel QSV, and CPU x264.
- Official signed NVIDIA MessageBus IPC integration safe for Easy Anti-Cheat.
