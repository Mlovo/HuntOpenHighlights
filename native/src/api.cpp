#include "common.h"

namespace hr {
hr_mux* mux_open(Session*, const wchar_t*);
void mux_write(hr_mux*, const hr_packet*, int64_t);
void mux_close(hr_mux*);
void mux_abort(hr_mux*);
void thumbnail(const wchar_t*, int, uint8_t**, int*, int*, int*);
thread_local std::wstring last_error;
template<typename F> int guard(F operation) noexcept {
    try { operation(); last_error.clear(); return 0; }
    catch (const winrt::hresult_error& e) { last_error = std::wstring(e.message()); }
    catch (const std::exception& e) { last_error = wide(e.what()); }
    catch (...) { last_error = L"Unknown native multimedia error"; }
    return -1;
}
}
int HR_CALL hr_abi_version() {
    static std::once_flag initialized;
    std::call_once(initialized, [] {
        av_log_set_callback([](void*, int level, const char* format, va_list arguments) {
            if (level > AV_LOG_WARNING) return;
            try {
                char buffer[2048]{};
                vsnprintf(buffer, sizeof(buffer), format, arguments);
                hr::ffmpeg_detail = buffer;
            } catch (...) { }
        });
    });
    return 1;
}
const wchar_t* HR_CALL hr_last_error() { return hr::last_error.c_str(); }
int HR_CALL hr_devices(hr_device_callback callback, void* user) {
    return hr::guard([&] { if (!callback) throw std::runtime_error("Missing device callback."); hr::enumerate(callback, user); });
}
int HR_CALL hr_probe(const hr_config* source) {
    return hr::guard([&] {
        if (!source) throw std::runtime_error("Missing probe config.");
        auto config = *source; hr::validate(config);
        hr::Apartment apartment;
        auto window = reinterpret_cast<HWND>(static_cast<uintptr_t>(config.window));
        auto monitor = window ? MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST)
            : reinterpret_cast<HMONITOR>(static_cast<uintptr_t>(config.monitor));
        if (!monitor) monitor = MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY);
        hr::D3D d3d(monitor);
        if (!config.width || !config.height) {
            RECT area{};
            if (window) hr::check(GetClientRect(window, &area) ? S_OK : HRESULT_FROM_WIN32(GetLastError()));
            else {
                MONITORINFO info{}; info.cbSize = sizeof(info);
                hr::check(GetMonitorInfoW(monitor, &info) ? S_OK : HRESULT_FROM_WIN32(GetLastError()));
                area = info.rcMonitor;
            }
            config.width = ((area.right - area.left) / 2) * 2;
            config.height = ((area.bottom - area.top) / 2) * 2;
            if (config.width < 2 || config.height < 2) throw std::runtime_error("Probe target has no usable dimensions.");
            hr::validate(config);
        }
        int encoded = 0;
        hr::VideoEncoder encoder(d3d, config, [&](int, const AVPacket*, AVRational) { ++encoded; });
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = config.width; desc.Height = config.height; desc.MipLevels = 1; desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        hr::ComPtr<ID3D11Texture2D> black; hr::check(d3d.device->CreateTexture2D(&desc, nullptr, &black));
        hr::ComPtr<ID3D11RenderTargetView> view; hr::check(d3d.device->CreateRenderTargetView(black.Get(), nullptr, &view));
        const float color[]{0, 0, 0, 1}; d3d.context->ClearRenderTargetView(view.Get(), color);
        for (int i = 0; i < 8; ++i) encoder.encode(black.Get(), config.width, config.height, i);
        encoder.drain(true);
        if (encoded == 0) throw std::runtime_error("Encoder accepted input but produced no packets.");
        int audio_packets = 0;
        std::string audio_failure;
        hr::AudioCapture audio(config, hr::qpc_100ns(),
            [&](int, const AVPacket*, AVRational) { ++audio_packets; },
            [&](std::string message) { audio_failure = std::move(message); });
        audio.start();
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        audio.stop(); // Join before reading the callback-owned probe results.
        if (!audio_failure.empty()) throw std::runtime_error(audio_failure);
        if (audio_packets == 0) throw std::runtime_error("WASAPI/AAC probe produced no packets.");
    });
}
int HR_CALL hr_start(const hr_config* config, hr_packet_callback packets, hr_log_callback logs, void* user, hr_session** result) {
    if (result) *result = nullptr;
    return hr::guard([&] {
        if (!result || !config || !packets) throw std::runtime_error("Missing capture arguments.");
        auto session = std::make_unique<hr_session>(*config, packets, logs, user);
        session->start(); *result = session.release();
    });
}
void HR_CALL hr_stop(hr_session* session) { if (session) session->stop(); }
void HR_CALL hr_destroy(hr_session* session) { delete session; }
int HR_CALL hr_get_stats(hr_session* session, hr_stats* result) {
    return hr::guard([&] {
        if (!session || !result) throw std::runtime_error("Missing stats arguments.");
        *result = {session->frames_count.load(), session->dropped.load(), session->bytes.load(),
            std::max<int64_t>(0, (hr::qpc_100ns() - session->epoch_100ns) / 10), session->config.width, session->config.height,
            session->video ? session->video->encoder : 0, session->config.codec, session->running, session->stale};
    });
}
const wchar_t* HR_CALL hr_description(hr_session* session) { return session ? session->description.c_str() : L"Not running"; }
int HR_CALL hr_mux_open(hr_session* session, const wchar_t* path, hr_mux** result) {
    if (result) *result = nullptr;
    return hr::guard([&] { if (!result) throw std::runtime_error("Missing mux result."); *result = hr::mux_open(session, path); });
}
int HR_CALL hr_mux_write(hr_mux* mux, const hr_packet* packet, int64_t origin_us) {
    return hr::guard([&] { hr::mux_write(mux, packet, origin_us); });
}
int HR_CALL hr_mux_close(hr_mux* mux) { return hr::guard([&] { hr::mux_close(mux); }); }
void HR_CALL hr_mux_abort(hr_mux* mux) { hr::mux_abort(mux); }
int HR_CALL hr_thumbnail(const wchar_t* path, int32_t max_width, uint8_t** pixels, int32_t* width, int32_t* height, int32_t* stride) {
    return hr::guard([&] { hr::thumbnail(path, max_width, pixels, width, height, stride); });
}
void HR_CALL hr_free(void* memory) { av_free(memory); }
