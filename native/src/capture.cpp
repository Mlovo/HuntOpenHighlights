#include "common.h"
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Security.Authorization.AppCapabilityAccess.h>

namespace hr {
void validate(const hr_config& c) {
    if (c.size != sizeof(hr_config)) throw std::runtime_error("Native ABI/config version mismatch.");
    if (c.encoder < 0 || c.encoder > 4 || c.codec < 0 || c.codec > 2 ||
        (c.fps != 30 && c.fps != 60 && c.fps != 120) || c.bitrate < 10'000'000 || c.bitrate > 80'000'000 ||
        c.width < 0 || c.height < 0 || c.width > 8192 || c.height > 8192 ||
        (c.gop_seconds != 1 && c.gop_seconds != 2) ||
        !std::isfinite(c.microphone_gain) || c.microphone_gain < 0 || c.microphone_gain > 1)
        throw std::runtime_error("Invalid capture configuration.");
    if (c.encoder == 4 && c.codec != 0) throw std::runtime_error("CPU x264 supports H.264 only.");
}
// A height without a width keeps the source's aspect ratio and never scales the source up; no height records the
// source as it is. An explicit width and height are used unchanged. HuntReplay.Core VideoResolutions.OutputSize mirrors this.
void fit_output(hr_config& c, int source_width, int source_height) {
    if (c.width > 0 && c.height > 0) return;
    if (c.height <= 0 || c.height >= source_height) {
        c.width = (source_width / 2) * 2; c.height = (source_height / 2) * 2;
        return;
    }
    const long width = std::lround(static_cast<double>(source_width) * c.height / source_height);
    c.width = std::max(2, static_cast<int>(width / 2) * 2); c.height = (c.height / 2) * 2;
}
Session::Session(const hr_config& source, hr_packet_callback packet_cb, hr_log_callback log_cb, void* context)
    : config(source), packets(packet_cb), logs(log_cb), user(context) {
    validate(source);
    output_id = source.output_device ? source.output_device : L"";
    microphone_id = source.microphone_device ? source.microphone_device : L"";
    gpu_id = source.gpu_device ? source.gpu_device : L"";
    config.output_device = output_id.c_str(); config.microphone_device = microphone_id.c_str();
    config.gpu_device = gpu_id.c_str();
}
Session::~Session() { stop(); }
void Session::log(int level, const std::string& message) const { if (logs) logs(user, level, message.c_str()); }
void Session::emit(int stream, const AVPacket* packet, AVRational base) {
    if (packet->pts == AV_NOPTS_VALUE || packet->dts == AV_NOPTS_VALUE)
        throw std::runtime_error("Encoder returned a packet without timestamps.");
    if (stream == 0 && !video_header_frozen) {
        // Some hardware backends publish global/sequence headers with their FIRST packet,
        // not at avcodec_open2. Freeze a deep copy before that packet reaches the RAM ring.
        std::lock_guard lock(headers_mutex);
        auto* raw = avcodec_parameters_alloc(); if (!raw) throw std::bad_alloc();
        std::unique_ptr<AVCodecParameters, ParametersDelete> header(raw);
        avcheck(avcodec_parameters_from_context(raw, video->codec.get()), "Freeze video codec parameters");
        size_t extra_size = 0;
        const uint8_t* extra = av_packet_get_side_data(packet, AV_PKT_DATA_NEW_EXTRADATA, &extra_size);
        auto extracted = make_packet();
        if (raw->extradata_size == 0 && !extra) {
            struct Filter { AVBSFContext* value = nullptr; ~Filter() { av_bsf_free(&value); } } filter;
            const AVBitStreamFilter* implementation = av_bsf_get_by_name("extract_extradata");
            if (!implementation) throw std::runtime_error("The runtime lacks extract_extradata.");
            avcheck(av_bsf_alloc(implementation, &filter.value), "Allocate sequence header parser");
            avcheck(avcodec_parameters_copy(filter.value->par_in, raw), "Configure sequence header parser");
            filter.value->time_base_in = base;
            avcheck(av_bsf_init(filter.value), "Initialize sequence header parser");
            auto copy = make_packet(); avcheck(av_packet_ref(copy.get(), packet), "Reference header packet");
            avcheck(av_bsf_send_packet(filter.value, copy.get()), "Parse sequence header");
            avcheck(av_bsf_receive_packet(filter.value, extracted.get()), "Read sequence header");
            extra = av_packet_get_side_data(extracted.get(), AV_PKT_DATA_NEW_EXTRADATA, &extra_size);
        }
        if (extra && extra_size > 0) {
            if (extra_size > 1024 * 1024) throw std::runtime_error("Invalid encoder sequence header size.");
            av_freep(&raw->extradata);
            raw->extradata = static_cast<uint8_t*>(av_mallocz(extra_size + AV_INPUT_BUFFER_PADDING_SIZE));
            if (!raw->extradata) throw std::bad_alloc();
            memcpy(raw->extradata, extra, extra_size); raw->extradata_size = static_cast<int>(extra_size);
        }
        if (raw->extradata_size == 0) throw std::runtime_error("Encoder supplied no decodable global/sequence header. Update the driver or choose another codec.");
        headers[0] = std::move(header);
        video_header_frozen = true;
    }
    hr_packet data{};
    data.stream = stream; data.keyframe = (packet->flags & AV_PKT_FLAG_KEY) != 0;
    data.pts_us = av_rescale_q(packet->pts, base, microseconds);
    data.dts_us = av_rescale_q(packet->dts, base, microseconds);
    data.duration_us = av_rescale_q(packet->duration, base, microseconds);
    data.data = packet->data; data.size = packet->size;
    bytes.fetch_add(packet->size, std::memory_order_relaxed);
    if (packets) packets(user, &data);
}
void Session::start() {
    std::promise<void> ready;
    auto result = ready.get_future();
    worker = std::jthread([this, promise = std::move(ready)](std::stop_token token) mutable { capture(token, std::move(promise)); });
    result.get();
}
void Session::stop() {
    if (worker.joinable()) { worker.request_stop(); worker.join(); }
    running = false;
}
namespace {
// The capture thread never waits longer than this for Windows to answer the consent request.
constexpr auto border_consent_timeout = std::chrono::seconds(3);

const char* access_name(winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus status) {
    using Status = winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus;
    switch (status) {
        case Status::Allowed: return "Allowed";
        case Status::DeniedByUser: return "DeniedByUser";
        case Status::DeniedBySystem: return "DeniedBySystem";
        case Status::NotDeclaredByApp: return "NotDeclaredByApp";
        case Status::UserPromptRequired: return "UserPromptRequired";
        default: return "unknown status";
    }
}

// The official, consent-based request for borderless capture. It sets IsBorderRequired(false) only when Windows
// answers Allowed. Every other outcome keeps the OS indicator. The function never throws and never bypasses the answer.
std::string request_borderless(winrt::Windows::Graphics::Capture::GraphicsCaptureSession session) {
    using Status = winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus;
    const std::string retained = "OS capture indicator is retained: ";
    try {
        if (!winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
                L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsBorderRequired"))
            return retained + "this Windows build has no GraphicsCaptureSession.IsBorderRequired.";
        auto answer = std::make_shared<std::promise<Status>>();
        auto pending = answer->get_future();
        std::thread([answer] {
            try {
                Apartment apartment;
                answer->set_value(winrt::Windows::Graphics::Capture::GraphicsCaptureAccess::RequestAccessAsync(
                    winrt::Windows::Graphics::Capture::GraphicsCaptureAccessKind::Borderless).get());
            } catch (...) {
                answer->set_exception(std::current_exception());
            }
        }).detach();
        if (pending.wait_for(border_consent_timeout) != std::future_status::ready)
            return retained + "Windows did not answer the consent request within 3 seconds.";
        const Status status = pending.get();
        if (status != Status::Allowed)
            return retained + "Windows answered " + access_name(status) + " to the consent request.";
        session.IsBorderRequired(false);
        return "";
    } catch (const winrt::hresult_error& error) {
        return retained + "consent request failed: " + winrt::to_string(error.message()) + ".";
    } catch (const std::exception& error) {
        return retained + "consent request failed: " + error.what() + ".";
    }
}
} // namespace

void Session::capture(std::stop_token stop, std::promise<void> ready) {
    bool signaled = false;
    namespace Capture = winrt::Windows::Graphics::Capture;
    namespace GraphicsDirectX = winrt::Windows::Graphics::DirectX;
    Capture::Direct3D11CaptureFramePool pool{nullptr};
    Capture::GraphicsCaptureSession capture_session{nullptr};
    std::unique_ptr<Apartment> apartment;
    try {
        apartment = std::make_unique<Apartment>();
        if (!Capture::GraphicsCaptureSession::IsSupported()) throw std::runtime_error("Windows Graphics Capture is not supported on this session.");
        HWND window = reinterpret_cast<HWND>(static_cast<uintptr_t>(config.window));
        HMONITOR monitor = window ? MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST)
            : reinterpret_cast<HMONITOR>(static_cast<uintptr_t>(config.monitor));
        if (!monitor) monitor = MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY);
        if (window && !IsWindow(window)) throw std::runtime_error("Game window is no longer available.");
        d3d = std::make_unique<D3D>(monitor, config.gpu_device);
        auto interop = winrt::get_activation_factory<Capture::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        Capture::GraphicsCaptureItem item{nullptr};
        if (window) check(interop->CreateForWindow(window, winrt::guid_of<Capture::GraphicsCaptureItem>(), winrt::put_abi(item)));
        else check(interop->CreateForMonitor(monitor, winrt::guid_of<Capture::GraphicsCaptureItem>(), winrt::put_abi(item)));
        auto size = item.Size();
        if (size.Width < 2 || size.Height < 2) throw std::runtime_error("Capture target has no visible content.");
        fit_output(config, size.Width, size.Height);
        validate(config);
        if (d3d->requested_fallback)
            log(1, "Selected GPU \"" + utf8(gpu_id) + "\" is unavailable; using " + utf8(d3d->name) + ".");
        auto callback = [this](int stream, const AVPacket* packet, AVRational base) { emit(stream, packet, base); };
        log(0, "Initializing GPU capture: " + utf8(d3d->name) + " / " + std::to_string(config.width)
            + "x" + std::to_string(config.height) + " / " + std::to_string(config.fps) + " FPS.");
        video = std::make_unique<VideoEncoder>(*d3d, config, callback);
        description = video->description;
        ComPtr<IDXGIDevice> dxgi; check(d3d->device.As(&dxgi));
        winrt::com_ptr<IInspectable> inspectable;
        check(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(), inspectable.put()));
        auto device = inspectable.as<GraphicsDirectX::Direct3D11::IDirect3DDevice>();
        pool = Capture::Direct3D11CaptureFramePool::CreateFreeThreaded(device,
            GraphicsDirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, size);
        capture_session = pool.CreateCaptureSession(item);
        capture_session.IsCursorCaptureEnabled(false);
        const std::string border = request_borderless(capture_session);
        auto closed = std::make_shared<std::atomic<bool>>(false);
        auto closed_handler = item.Closed(winrt::auto_revoke, [closed](auto&&, auto&&) { *closed = true; });
        epoch_100ns = qpc_100ns();
        audio = std::make_unique<AudioCapture>(config, epoch_100ns, callback,
            [this](std::string message) { log(2, message); failed = true; },
            [this](std::string message) { log(1, message); });
        // Audio parameters are final now; video parameters are frozen on the first packet.
        for (AVCodecContext* codec : {video->codec.get()}) {
            auto* p = avcodec_parameters_alloc(); if (!p) throw std::bad_alloc();
            headers.emplace_back(p); avcheck(avcodec_parameters_from_context(p, codec), "Copy video codec headers");
        }
        for (const auto& codec : audio->codecs) {
            auto* p = avcodec_parameters_alloc(); if (!p) throw std::bad_alloc();
            headers.emplace_back(p); avcheck(avcodec_parameters_from_context(p, codec.get()), "Copy AAC headers");
        }
        audio->start();
        capture_session.StartCapture();
        running = true; ready.set_value(); signaled = true;
        log(0, "WGC + D3D11 NV12 + " + utf8(description) + " + WASAPI started. SDR capture." + (border.empty() ? "" : " " + border));
        ComPtr<ID3D11Texture2D> last_texture;
        int input_width = 0, input_height = 0;
        int64_t last_pts = -1;
        int64_t last_arrival = epoch_100ns;
        PreciseTimer pacer;
        auto next = Clock::now();
        const auto interval = std::chrono::nanoseconds(1'000'000'000 / config.fps);
        while (!stop.stop_requested() && !failed && !closed->load()) {
            next += interval;
            for (int attempts = 0; attempts < 3; ++attempts) {
                auto frame = pool.TryGetNextFrame();
                if (!frame) break;
                auto actual = frame.ContentSize();
                if (actual.Width < 2 || actual.Height < 2) { frame.Close(); continue; }
                auto surface = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
                ComPtr<ID3D11Texture2D> texture;
                check(surface->GetInterface(IID_PPV_ARGS(&texture)));
                D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
                if (actual.Width <= static_cast<int>(desc.Width) && actual.Height <= static_cast<int>(desc.Height)) {
                    if (input_width != actual.Width || input_height != actual.Height) {
                        D3D11_TEXTURE2D_DESC owned{};
                        owned.Width = actual.Width; owned.Height = actual.Height;
                        owned.MipLevels = 1; owned.ArraySize = 1; owned.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                        owned.SampleDesc.Count = 1; owned.BindFlags = D3D11_BIND_RENDER_TARGET;
                        last_texture.Reset(); check(d3d->device->CreateTexture2D(&owned, nullptr, &last_texture));
                        input_width = actual.Width; input_height = actual.Height;
                    }
                    D3D11_BOX box{0, 0, 0, static_cast<UINT>(input_width), static_cast<UINT>(input_height), 1};
                    d3d->context->CopySubresourceRegion(last_texture.Get(), 0, 0, 0, 0, texture.Get(), 0, &box);
                    last_arrival = qpc_100ns();
                }
                frame.Close();
                if (actual.Width != size.Width || actual.Height != size.Height) {
                    size = actual; pool.Recreate(device, GraphicsDirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 3, size);
                    break;
                }
            }
            auto now = qpc_100ns();
            stale = now - last_arrival > 20'000'000;
            if (last_texture) {
                int64_t pts = av_rescale(now - epoch_100ns, config.fps, 10'000'000);
                if (pts > last_pts) {
                    if (last_pts >= 0) dropped += std::max<int64_t>(0, pts - last_pts - 1);
                    video->encode(last_texture.Get(), input_width, input_height, pts);
                    last_pts = pts; ++frames_count;
                }
            } else if (now - epoch_100ns > 50'000'000) {
                throw std::runtime_error("WGC returned no frames for 5 seconds. Restore the window / disable protected or exclusive presentation.");
            }
            if (next < Clock::now() - interval) next = Clock::now();
            pacer.sleep_until(next);
        }
        if (closed->load()) log(1, "Capture target closed.");
        capture_session.Close(); capture_session = nullptr;
        pool.Close(); pool = nullptr;
        audio->stop(); video->drain(true);
    } catch (const winrt::hresult_error& e) {
        failed = true;
        if (!signaled) ready.set_exception(std::current_exception());
        else log(2, "Capture: " + winrt::to_string(e.message()));
    } catch (const std::exception& e) {
        failed = true;
        if (!signaled) ready.set_exception(std::current_exception()); else log(2, e.what());
    } catch (...) {
        failed = true;
        if (!signaled) ready.set_exception(std::current_exception()); else log(2, "Unknown capture error");
    }
    // Never let an exception escape a worker or a C ABI callback.
    if (audio) audio->stop();
    try { if (capture_session) capture_session.Close(); if (pool) pool.Close(); } catch (...) { }
    running = false;
}
}
