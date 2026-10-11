# HuntReplayStudio Interactive Video Trimming — E2E Test Infrastructure (Tiers 1-4)

## 1. Overview & Architecture
This document defines the comprehensive opaque-box, requirement-driven End-to-End (E2E) testing infrastructure for the **HuntReplayStudio Interactive Video Trimming** system.

The E2E test suite validates features specified in `ORIGINAL_REQUEST.md` across both managed WPF/MVVM layers (`GalleryView`, `RangeSlider`, `VideoTrimmingService`) and the native multimedia engine (`HuntReplay.Media.dll` wrapping FFmpeg 8.1.3).

### Key Architectural Principles
- **Opaque-Box & Requirement-Driven**: Tests interact exclusively through public APIs, user interface state contracts, and exported C ABI functions, verifying external observable behaviors without relying on white-box internals.
- **Deterministic Synthetic Fixtures**: Sample video and multi-track audio clips are synthesized dynamically using bundled FFmpeg (`testsrc`, `sine`) with fixed GOP sizes and known audio frequencies. Tests do not depend on external downloads or game installations.
- **Strict Non-Destructive Invariants**: Verifies before and after every operation that source video files are untouched (via SHA-256 and modification timestamps).
- **Packet-Level Ground Truth**: Validates stream integrity, multi-track audio preservation, DTS monotonicity, and Audio/Video synchronization drift (< 25ms) via bundled `ffprobe`.

---

## 2. Test Suite Structure & Tier Breakdown

The test suite is structured across 4 distinct verification tiers totaling 39 automated end-to-end tests:

```
tests/HuntReplay.E2ETests/
├── HuntReplay.E2ETests.csproj       # .NET 8 Windows WPF test harness project
├── E2EInfrastructure.cs             # Shared test harness, FFmpeg/ffprobe wrappers, assertions, native loader
├── Tier1_FeatureCoverageTests.cs    # Tier 1: Isolated requirement happy paths (15 tests)
├── Tier2_BoundaryTests.cs           # Tier 2: Boundary conditions & corner cases (13 tests)
├── Tier3_CrossFeatureTests.cs       # Tier 3: Cross-feature combinations & pairwise workflows (6 tests)
├── Tier4_RealWorldScenariosTests.cs # Tier 4: Real-world gameplay highlight scenarios (5 tests)
└── Program.cs                       # STA CLI runner, test coordinator & exit code generator
```

### Tier 1: Feature Coverage (Isolated Happy Path) — 15 Tests
Verifies happy-path execution of each isolated requirement from `ORIGINAL_REQUEST.md`:
- **T1-01 (§R1)**: "Trim Video" button presence and smooth inline mode entry in `GalleryView` (Seek slider swapped for `RangeSlider` and `TrimToolbar`, player paused).
- **T1-02 (§R1)**: Dual-handle timeline initialization to clip duration, visual distinction (accent selected range, dimmed unselected regions).
- **T1-03 (§R1)**: Real-time timestamp formatting in MM:SS (sub-hour) and HH:MM:SS (hour-plus) updating dynamically.
- **T1-04 (§R1)**: Fine-tuning stepping buttons (-0.1s / +0.1s) for start and end boundaries with precise clamping.
- **T1-05 (§R1)**: Mark In `[I]` and Mark Out `[O]` setting boundaries to current player PTS.
- **T1-06 (§R1)**: Selection preview playback starting at `SelectionStart` and automatically halting at `SelectionEnd`.
- **T1-07 (§R1)**: "Cancel" ("Отмена") action cleanly restoring normal player state with zero side effects or file alterations.
- **T1-08 (§R2)**: Native fast stream copying (`accurate=0`) remuxing packets without re-encoding, preserving H.264 video & AAC audio.
- **T1-09 (§R2)**: Native frame-accurate trimming mode flag (`accurate=1`) executing successfully.
- **T1-10 (§R2)**: Multi-track audio preservation retaining all audio tracks with original sample rates and channel layouts.
- **T1-11 (§R2)**: Audio/Video synchronization packet timestamps maintaining strict drift < 25ms.
- **T1-12 (§R2)**: Source video file immutability across multiple trimming operations.
- **T1-13 (§R2)**: Asynchronous trimming service progress reporting reaching 1.0 (100%).
- **T1-14 (§R3)**: Audio-less video trimming preserving video stream cleanly without missing audio crashes.
- **T1-15 (§R3)**: Standard player non-regression outside trim mode (normal playback, volume slider, mute toggle).

### Tier 2: Boundary & Corner Cases — 13 Tests
Stress-tests domain boundaries, invalid parameters, and extreme conditions:
- **T2-01 (§R1, §R2)**: Sub-0.5s minimum duration constraint enforced by both UI and service layers.
- **T2-02 (§R1)**: Crossing boundaries prevented (Start pushed past End clamps to `End - 0.5s`; End pushed before Start clamps to `Start + 0.5s`).
- **T2-03 (§R2)**: Trimming from origin `t = 0.0s` produces valid stream starting at PTS 0.000s.
- **T2-04 (§R2)**: Trimming up to exact clip duration handles EOF cleanly without overrun.
- **T2-05 (§R2)**: Narrow slice near end of long recording [58.0s, 59.5s] remuxes accurately.
- **T2-06 (§R1, §R2)**: Out-of-bounds, negative, NaN, and Infinity timestamps rejected safely.
- **T2-07 (§R2)**: Identical source and target paths rejected (including case-insensitive Windows match) to prevent overwriting source.
- **T2-08 (§R2)**: Missing, empty, or whitespace source paths fail validation with clear exceptions.
- **T2-09 (§R2)**: Cancellation mid-flight aborts promptly (< 250ms) and cleans up partial target files across 0%, 50%, and 95% progress points.
- **T2-10 (§R2)**: 3-track multi-audio preservation (48kHz stereo, 44.1kHz mono, 32kHz stereo) retains all distinct attributes.
- **T2-11 (§R2)**: Long recording 60s trimming maintains strictly monotonic packet DTS.
- **T2-12 (§R1)**: Translation dragging of range bar preserves duration window invariant.
- **T2-13 (§R1)**: Reducing Maximum below current RangeEnd safely coerces bounds without violating `RangeStart <= RangeEnd <= Maximum`.

### Tier 3: Cross-Feature Combinations & Pairwise Interactions — 6 Tests
Verifies interactions between disparate features:
- **T3-01 (§R1)**: Interactive workflow: fine-tune stepping -> jump to start -> preview playback -> automatic pause at boundary.
- **T3-02 (§R1, §R2)**: Mark In `[I]` & Mark Out `[O]` followed by direct export and boundary duration verification.
- **T3-03 (§R1, §R2)**: Export cancellation cleans up resources and allows clean trim mode exit restoring normal player.
- **T3-04 (§R2)**: Consecutive trimming passes on the same source video remain independent and preserve source integrity.
- **T3-05 (§R2)**: Concurrent multi-threaded trimming across 8 threads isolates thread-local state and error reporting (`hr_last_error`).
- **T3-06 (§R1)**: LiveSeek scrubbing event updates playhead and player frame smoothly.

### Tier 4: Real-World Application Scenarios — 5 Tests
Validates complete user workflows matching real game replay usage:
- **T4-01 (§R1, §R2)**: Realistic game replay 30s recording trimmed to 7s highlight and playable in native player engine with thumbnail extraction.
- **T4-02 (§R2)**: Gameplay clip with multi-track commentary (game sound + mic) preserves A/V sync (< 25ms drift).
- **T4-03 (§R2, §R3)**: Silent gameplay clip highlight extraction and thumbnail verification.
- **T4-04 (§R1, §R2)**: Multi-highlight batch extraction from match recording registered in `ClipLibrary` with metadata sidecars.
- **T4-05 (§R2, §R3)**: Long gameplay recording (60s) final clutch play extraction near EOF with clean native playback.

---

## 3. Test Runner & Execution Methods

### Method 1: .NET CLI Direct Execution
```powershell
dotnet run --project tests/HuntReplay.E2ETests
```
Expected output: 39 passed, 0 failed, exit code 0.

### Method 2: Python Runner
```powershell
python tools/run_e2e_tests.py
```
Expected output: 39 passed, 0 failed, exit code 0.

### Method 3: Full Solution Build & Verification
```powershell
dotnet build HuntReplayStudio.sln
dotnet run --project tests/HuntReplay.Tests
dotnet run --project tests/HuntReplay.E2ETests
```

---

## 4. Test Verification Invariants & Tolerances
| Invariant | Authoritative Requirement | Tolerance / Acceptance Criteria |
|---|---|---|
| Non-destructive source | §R2 Never overwrite source file | Byte-for-byte SHA256 identical, LastWriteTime untouched |
| Audio/Video sync drift | §R2 Maintain A/V synchronization | Offset difference between target & source < 25.0 ms |
| Minimum clip duration | §R1 Start strictly precedes end | Duration >= 0.5 s; sub-0.5s clamped or rejected |
| Multi-track audio | §R2 Accurately preserve audio tracks | All audio tracks preserved with original sample rate & channels |
| DTS monotonicity | §R2 Packet remuxing | Target packet DTS strictly monotonically increasing |
| Prompt cancellation | §R2 Support user cancellation | Abort in < 250 ms, target file deleted from disk |
| Native error reporting | §R2 Robust error handling | Negative return code and non-empty `hr_last_error()` |
