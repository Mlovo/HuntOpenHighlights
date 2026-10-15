#pragma once
#include <stdint.h>
#ifdef _WIN32
#define HR_API extern "C" __declspec(dllexport)
#define HR_CALL __cdecl
#else
#error HuntReplay.Media requires Windows 10 2004 or newer, x64.
#endif

// Versioned C ABI. No C++ objects, ownership or exceptions cross this boundary.
struct hr_config {
    uint32_t size;
    int32_t encoder; // 0 auto, 1 AMF, 2 NVENC, 3 QSV, 4 x264 (explicit only)
    int32_t codec;   // 0 H264, 1 HEVC, 2 AV1
    int32_t fps, width, height, bitrate, gop_seconds;
    uint64_t window, monitor;
    const wchar_t* output_device;
    const wchar_t* microphone_device;
    int32_t microphone_enabled;
    float microphone_gain;
    int32_t separate_microphone;
    const wchar_t* gpu_device;
};
struct hr_packet {
    int32_t stream, keyframe;
    int64_t pts_us, dts_us, duration_us;
    const uint8_t* data;
    int32_t size;
};
struct hr_stats {
    int64_t frames, dropped_frames, encoded_bytes, elapsed_us;
    int32_t width, height, encoder, codec, running, source_stale;
};
struct hr_device {
    int32_t kind, is_default, vendor; // kind: 0 monitor, 1 render endpoint, 2 microphone, 3 GPU
    uint64_t handle;
    const wchar_t* id;
    const wchar_t* name;
};
struct hr_session;
struct hr_mux;
struct hr_player;
typedef void (HR_CALL *hr_packet_callback)(void* user, const hr_packet* packet);
typedef void (HR_CALL *hr_log_callback)(void* user, int32_t level, const char* utf8_message);
typedef void (HR_CALL *hr_device_callback)(void* user, const hr_device* device);
typedef int (HR_CALL *hr_trim_progress_callback)(void* user, double progress);

HR_API int HR_CALL hr_abi_version();
HR_API const wchar_t* HR_CALL hr_last_error(); // Thread-local, valid until next failing API call.
HR_API int HR_CALL hr_devices(hr_device_callback callback, void* user);
HR_API int HR_CALL hr_probe(const hr_config* config); // Synthetic GPU frames + 200ms WASAPI/AAC probe; no file is written.
HR_API int HR_CALL hr_start(const hr_config* config, hr_packet_callback packets, hr_log_callback logs,
                           void* user, hr_session** result);
HR_API void HR_CALL hr_stop(hr_session* session); // Joins workers; preserves codec headers for export.
HR_API void HR_CALL hr_destroy(hr_session* session);
HR_API int HR_CALL hr_get_stats(hr_session* session, hr_stats* result);
HR_API const wchar_t* HR_CALL hr_description(hr_session* session);
HR_API int HR_CALL hr_mux_open(hr_session* session, const wchar_t* path, hr_mux** result);
HR_API int HR_CALL hr_mux_write(hr_mux* mux, const hr_packet* packet, int64_t origin_us);
HR_API int HR_CALL hr_mux_close(hr_mux* mux); // Always consumes mux, even on failure.
HR_API void HR_CALL hr_mux_abort(hr_mux* mux);
HR_API int HR_CALL hr_thumbnail(const wchar_t* path, int32_t max_width,
                               uint8_t** pixels, int32_t* width, int32_t* height, int32_t* stride);
HR_API void HR_CALL hr_free(void* memory);
HR_API int HR_CALL hr_player_open(const wchar_t* path, hr_player** result,
                                 int32_t* width, int32_t* height, double* duration);
HR_API void HR_CALL hr_player_close(hr_player* player);
HR_API int HR_CALL hr_player_play(hr_player* player);
HR_API int HR_CALL hr_player_pause(hr_player* player);
HR_API int HR_CALL hr_player_seek(hr_player* player, double seconds);
HR_API int HR_CALL hr_player_get_state(hr_player* player, int32_t* is_playing, int32_t* is_ended, double* current_pts);
HR_API int HR_CALL hr_player_read_frame(hr_player* player, uint8_t* buffer, int32_t stride,
                                       int32_t width, int32_t height, int32_t* updated, double* pts);
HR_API int HR_CALL hr_player_preview(hr_player* player, double seconds, int32_t max_width,
                                    uint8_t** pixels, int32_t* width, int32_t* height, int32_t* stride);
HR_API int HR_CALL hr_player_set_volume(hr_player* player, float volume);
HR_API int HR_CALL hr_trim(const wchar_t* source_path,
                           const wchar_t* target_path,
                           double start_seconds,
                           double end_seconds,
                           int32_t accurate,
                           hr_trim_progress_callback progress_cb,
                           void* user);
