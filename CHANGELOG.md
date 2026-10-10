# Changelog

All notable changes to **HuntReplay Studio** are documented in this file.

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
