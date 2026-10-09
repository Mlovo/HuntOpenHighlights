#pragma once
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <roapi.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi1_2.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <wrl/client.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavcodec/bsf.h>
#include <libavformat/avformat.h>
#include <libavutil/channel_layout.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libavutil/imgutils.h>
#include <libavutil/opt.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>
}
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <future>
#include <functional>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
#include "huntreplay_media.h"

namespace hr {
using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;
inline thread_local std::string ffmpeg_detail;
constexpr AVRational microseconds{1, 1'000'000};
inline void check(HRESULT code) { winrt::check_hresult(code); }
inline void avcheck(int code, const char* operation) {
    if (code >= 0) return;
    char message[AV_ERROR_MAX_STRING_SIZE]{};
    av_strerror(code, message, sizeof(message));
    throw std::runtime_error(std::string(operation) + ": " + message + (ffmpeg_detail.empty() ? "" : " / " + ffmpeg_detail));
}
inline int64_t qpc_100ns() {
    LARGE_INTEGER value, frequency;
    QueryPerformanceCounter(&value); QueryPerformanceFrequency(&frequency);
    return av_rescale(value.QuadPart, 10'000'000, frequency.QuadPart);
}
inline std::string utf8(const std::wstring& value) { return winrt::to_string(value); }
inline std::wstring wide(const char* value) { return std::wstring(winrt::to_hstring(value)); }
struct FrameDelete { void operator()(AVFrame* p) const { av_frame_free(&p); } };
struct PacketDelete { void operator()(AVPacket* p) const { av_packet_free(&p); } };
struct CodecDelete { void operator()(AVCodecContext* p) const { avcodec_free_context(&p); } };
struct ParametersDelete { void operator()(AVCodecParameters* p) const { avcodec_parameters_free(&p); } };
struct BufferDelete { void operator()(AVBufferRef* p) const { av_buffer_unref(&p); } };
using Frame = std::unique_ptr<AVFrame, FrameDelete>;
using Packet = std::unique_ptr<AVPacket, PacketDelete>;
using Codec = std::unique_ptr<AVCodecContext, CodecDelete>;
using Buffer = std::unique_ptr<AVBufferRef, BufferDelete>;
inline Frame make_frame() { auto* p = av_frame_alloc(); if (!p) throw std::bad_alloc(); return Frame(p); }
inline Packet make_packet() { auto* p = av_packet_alloc(); if (!p) throw std::bad_alloc(); return Packet(p); }
struct Handle {
    HANDLE value = nullptr;
    explicit Handle(HANDLE value = nullptr) : value(value) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};
struct Apartment {
    HRESULT result = RoInitialize(RO_INIT_MULTITHREADED);
    Apartment() { if (FAILED(result) && result != RPC_E_CHANGED_MODE) check(result); }
    ~Apartment() { if (SUCCEEDED(result)) RoUninitialize(); }
};
using Emit = std::function<void(int, const AVPacket*, AVRational)>;

struct D3D {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11VideoDevice> video_device;
    ComPtr<ID3D11VideoContext> video_context;
    ComPtr<IDXGIAdapter1> adapter;
    uint32_t vendor = 0;
    std::wstring name;
    explicit D3D(HMONITOR monitor);
};

class VideoEncoder {
    D3D& d3d;
    Buffer d3d_device;
    Buffer frames;
    Buffer qsv_device;
    Buffer qsv_frames;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator;
    ComPtr<ID3D11VideoProcessor> processor;
    int input_width = 0, input_height = 0;
    int64_t last_forced_gop = -1;
    Emit emit;
    void rebuild_processor(int width, int height);
public:
    Codec codec;
    int encoder = 0;
    std::wstring description;
    VideoEncoder(D3D& device, hr_config config, Emit callback);
    void encode(ID3D11Texture2D* bgra, int width, int height, int64_t pts);
    void drain(bool flush = false);
};

class AudioCapture {
    struct Impl;
    std::unique_ptr<Impl> impl;
public:
    std::vector<Codec> codecs;
    AudioCapture(const hr_config& config, int64_t epoch_100ns, Emit emit,
                 std::function<void(std::string)> fail);
    ~AudioCapture();
    void start();
    void stop();
};

struct Session {
    hr_config config;
    std::wstring output_id, microphone_id, description;
    hr_packet_callback packets;
    hr_log_callback logs;
    void* user;
    std::jthread worker;
    std::atomic<bool> running{false};
    std::atomic<bool> failed{false};
    std::atomic<bool> stale{false};
    std::atomic<int64_t> frames_count{0}, dropped{0}, bytes{0};
    int64_t epoch_100ns = 0;
    std::unique_ptr<D3D> d3d;
    std::unique_ptr<VideoEncoder> video;
    std::unique_ptr<AudioCapture> audio;
    std::mutex headers_mutex;
    bool video_header_frozen = false;
    std::vector<std::unique_ptr<AVCodecParameters, ParametersDelete>> headers;
    Session(const hr_config& source, hr_packet_callback packet_cb, hr_log_callback log_cb, void* context);
    ~Session();
    void start();
    void stop();
    void capture(std::stop_token stop, std::promise<void> ready);
    void emit(int stream, const AVPacket* packet, AVRational time_base);
    void log(int level, const std::string& message) const;
};
void enumerate(hr_device_callback callback, void* user);
void validate(const hr_config& config);
}

struct hr_session : hr::Session { using Session::Session; };
