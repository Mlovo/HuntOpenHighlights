#include "common.h"
#include <ksmedia.h>
#include <avrt.h>

namespace hr {
namespace {
constexpr int rate = 48000;
constexpr int timeline_size = rate * 4;

// Sparse timestamped PCM ring. Silence is synthesized on the same QPC timeline when WASAPI
// emits no packets (e.g. a silent desktop); this prevents AAC from drifting behind video.
struct Timeline {
    std::vector<float> samples = std::vector<float>(timeline_size * 2);
    std::vector<int64_t> stamps = std::vector<int64_t>(timeline_size, INT64_MIN);
    void write(int64_t at, const float* data, int count) {
        for (int i = 0; i < count; ++i) {
            int64_t tick = at + i;
            if (tick < 0) continue;
            auto index = static_cast<size_t>(tick % timeline_size);
            samples[index * 2] = data[i * 2]; samples[index * 2 + 1] = data[i * 2 + 1]; stamps[index] = tick;
        }
    }
    float get(int64_t at, int channel) const {
        if (at < 0) return 0;
        auto index = static_cast<size_t>(at % timeline_size);
        return stamps[index] == at ? samples[index * 2 + channel] : 0;
    }
};
struct WaveDelete { void operator()(WAVEFORMATEX* p) const { CoTaskMemFree(p); } };
struct SwrDelete { void operator()(SwrContext* p) const { swr_free(&p); } };
struct Endpoint {
    ComPtr<IAudioClient> client;
    ComPtr<IAudioCaptureClient> capture;
    std::unique_ptr<WAVEFORMATEX, WaveDelete> format;
    std::unique_ptr<SwrContext, SwrDelete> resampler;
    Handle event{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
    Timeline timeline;
    bool packed24 = false;
    int64_t next_sample = -1;
    int64_t epoch;
    uint64_t expected_position = 0;
    std::wstring device_id;
    // Reused by read() on every WASAPI packet (~10 ms) to keep the audio thread allocation-free in steady state.
    std::vector<uint8_t> silence;
    std::vector<float> unpacked, converted;

    Endpoint(IMMDeviceEnumerator* enumerator, const wchar_t* id, bool loopback, int64_t epoch_100ns) : epoch(epoch_100ns) {
        ComPtr<IMMDevice> device;
        if (id && *id) check(enumerator->GetDevice(id, &device));
        else check(enumerator->GetDefaultAudioEndpoint(loopback ? eRender : eCapture, eConsole, &device));
        LPWSTR endpoint_id = nullptr; check(device->GetId(&endpoint_id));
        device_id = endpoint_id; CoTaskMemFree(endpoint_id);
        check(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(client.GetAddressOf())));
        WAVEFORMATEX* raw = nullptr; check(client->GetMixFormat(&raw)); format.reset(raw);
        if (raw->nChannels < 1 || raw->nChannels > 32 || raw->nSamplesPerSec < 8000 ||
            raw->nSamplesPerSec > 384000 || raw->nBlockAlign == 0)
            throw std::runtime_error("Invalid WASAPI endpoint format.");
        AVSampleFormat sample_format = AV_SAMPLE_FMT_NONE;
        GUID subtype{};
        uint64_t mask = 0;
        if (raw->wFormatTag == WAVE_FORMAT_EXTENSIBLE) {
            auto* ext = reinterpret_cast<WAVEFORMATEXTENSIBLE*>(raw);
            subtype = ext->SubFormat; mask = ext->dwChannelMask;
        } else if (raw->wFormatTag == WAVE_FORMAT_IEEE_FLOAT) subtype = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        else if (raw->wFormatTag == WAVE_FORMAT_PCM) subtype = KSDATAFORMAT_SUBTYPE_PCM;
        if (subtype == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT && raw->wBitsPerSample == 32) sample_format = AV_SAMPLE_FMT_FLT;
        else if (subtype == KSDATAFORMAT_SUBTYPE_PCM) {
            switch (raw->wBitsPerSample) {
                case 8: sample_format = AV_SAMPLE_FMT_U8; break;
                case 16: sample_format = AV_SAMPLE_FMT_S16; break;
                case 24: packed24 = true; sample_format = AV_SAMPLE_FMT_FLT; break;
                case 32: sample_format = AV_SAMPLE_FMT_S32; break;
            }
        }
        if (sample_format == AV_SAMPLE_FMT_NONE) throw std::runtime_error("Unsupported WASAPI endpoint mix format.");
        AVChannelLayout input{}, output = AV_CHANNEL_LAYOUT_STEREO;
        if (mask) avcheck(av_channel_layout_from_mask(&input, mask), "Audio channel mask");
        else av_channel_layout_default(&input, raw->nChannels);
        SwrContext* swr = nullptr;
        int result = swr_alloc_set_opts2(&swr, &output, AV_SAMPLE_FMT_FLT, rate,
            &input, sample_format, raw->nSamplesPerSec, 0, nullptr);
        av_channel_layout_uninit(&input);
        resampler.reset(swr); avcheck(result, "Create audio resampler");
        avcheck(swr_init(resampler.get()), "Initialize audio resampler");
        DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | (loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
        check(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 1'000'000, 0, raw, nullptr));
        if (!event.value) check(HRESULT_FROM_WIN32(GetLastError()));
        check(client->SetEventHandle(event.value));
        check(client->GetService(IID_PPV_ARGS(&capture)));
    }
    void read() {
        UINT32 available = 0; check(capture->GetNextPacketSize(&available));
        while (available) {
            BYTE* data = nullptr; UINT32 frames = 0; DWORD flags = 0;
            UINT64 position = 0, timestamp = 0;
            HRESULT status = capture->GetBuffer(&data, &frames, &flags, &position, &timestamp);
            if (status == AUDCLNT_S_BUFFER_EMPTY) break;
            check(status);
            try {
                int64_t at = (flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR)
                    ? av_rescale(qpc_100ns() - epoch, rate, 10'000'000) - frames * rate / format->nSamplesPerSec
                    : av_rescale(static_cast<int64_t>(timestamp) - epoch, rate, 10'000'000);
                if (next_sample < 0 || std::abs(at - next_sample) > rate / 10 ||
                    ((flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) && position != expected_position)) {
                    swr_close(resampler.get()); avcheck(swr_init(resampler.get()), "Reset discontinuous audio");
                    next_sample = at;
                } else if (std::abs(at - next_sample) > rate / 1000) {
                    // Smooth independent device-clock drift rather than accumulating A/V error.
                    avcheck(swr_set_compensation(resampler.get(), static_cast<int>(at - next_sample), rate), "Audio clock compensation");
                }
                expected_position = position + frames;
                if (flags & AUDCLNT_BUFFERFLAGS_SILENT) {
                    silence.assign(static_cast<size_t>(frames) * format->nBlockAlign,
                        format->wBitsPerSample == 8 ? 128 : 0);
                    data = silence.data();
                }
                if (packed24) {
                    unpacked.resize(static_cast<size_t>(frames) * format->nChannels);
                    for (size_t i = 0; i < unpacked.size(); ++i) {
                        const uint8_t* p = data + i * 3;
                        int32_t value = (p[0] | (p[1] << 8) | (p[2] << 16));
                        if (value & 0x800000) value |= static_cast<int32_t>(0xff000000u);
                        unpacked[i] = value / 8388608.f;
                    }
                    data = reinterpret_cast<BYTE*>(unpacked.data());
                }
                int capacity = static_cast<int>(av_rescale_rnd(swr_get_delay(resampler.get(), format->nSamplesPerSec) + frames,
                    rate, format->nSamplesPerSec, AV_ROUND_UP)) + 256;
                converted.resize(static_cast<size_t>(capacity) * 2);
                uint8_t* target = reinterpret_cast<uint8_t*>(converted.data());
                const uint8_t* source = data;
                int count = swr_convert(resampler.get(), &target, capacity, &source, frames);
                avcheck(count, "Resample audio");
                timeline.write(next_sample, converted.data(), count);
                next_sample += count;
            } catch (...) { capture->ReleaseBuffer(frames); throw; }
            check(capture->ReleaseBuffer(frames));
            check(capture->GetNextPacketSize(&available));
        }
    }
};

Codec audio_encoder() {
    auto* implementation = avcodec_find_encoder(AV_CODEC_ID_AAC);
    if (!implementation) throw std::runtime_error("AAC encoder missing from native runtime.");
    Codec codec(avcodec_alloc_context3(implementation));
    if (!codec) throw std::bad_alloc();
    codec->sample_rate = rate; codec->sample_fmt = AV_SAMPLE_FMT_FLTP;
    codec->bit_rate = 192000; codec->time_base = AVRational{1, rate};
    av_channel_layout_default(&codec->ch_layout, 2);
    codec->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    avcheck(avcodec_open2(codec.get(), implementation, nullptr), "Open AAC encoder");
    return codec;
}
}

struct AudioCapture::Impl {
    AudioCapture& owner;
    hr_config config;
    std::wstring output_id, microphone_id;
    int64_t epoch;
    Emit emit;
    std::function<void(std::string)> fail;
    std::jthread thread;
    std::promise<void> ready;
    explicit Impl(AudioCapture& parent, const hr_config& source, int64_t epoch_100ns, Emit emit, std::function<void(std::string)> fail)
        : owner(parent), config(source), epoch(epoch_100ns), emit(std::move(emit)), fail(std::move(fail)) {
        output_id = source.output_device ? source.output_device : L"";
        microphone_id = source.microphone_device ? source.microphone_device : L"";
    }
    void drain(int index, bool flush = false) {
        auto* codec = owner.codecs[index].get();
        if (flush) avcheck(avcodec_send_frame(codec, nullptr), "Flush audio encoder");
        auto packet = make_packet();
        for (;;) {
            int result = avcodec_receive_packet(codec, packet.get());
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) break;
            avcheck(result, "Receive AAC packet");
            emit(index + 1, packet.get(), codec->time_base); av_packet_unref(packet.get());
        }
    }
    void run(std::stop_token stop) {
        bool signaled = false;
        try {
            Apartment apartment;
            DWORD task_index = 0;
            HANDLE priority = AvSetMmThreadCharacteristicsW(L"Audio", &task_index);
            struct PriorityGuard { HANDLE h; ~PriorityGuard() { if (h) AvRevertMmThreadCharacteristics(h); } } priority_guard{priority};
            ComPtr<IMMDeviceEnumerator> enumerator;
            check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)));
            Endpoint game(enumerator.Get(), output_id.c_str(), true, epoch);
            std::unique_ptr<Endpoint> mic;
            if (config.microphone_enabled) mic = std::make_unique<Endpoint>(enumerator.Get(), microphone_id.c_str(), false, epoch);
            check(game.client->Start());
            if (mic) check(mic->client->Start());
            ready.set_value(); signaled = true;
            int frame_samples = owner.codecs[0]->frame_size;
            int64_t cursor = 0;
            HANDLE events[]{game.event.value, mic ? mic->event.value : game.event.value};
            auto check_default = [&](EDataFlow flow, const Endpoint& endpoint) {
                ComPtr<IMMDevice> current;
                check(enumerator->GetDefaultAudioEndpoint(flow, eConsole, &current));
                LPWSTR id = nullptr; check(current->GetId(&id));
                bool changed = endpoint.device_id != id; CoTaskMemFree(id);
                if (changed) throw std::runtime_error("Default audio endpoint changed; restart buffer to use the new device.");
            };
            auto next_device_check = Clock::now() + std::chrono::seconds(1);
            auto encode_until = [&](int64_t limit) {
                while (cursor + frame_samples <= limit) {
                    for (int i = 0; i < static_cast<int>(owner.codecs.size()); ++i) {
                        auto frame = make_frame(); auto* codec = owner.codecs[i].get();
                        frame->nb_samples = frame_samples; frame->sample_rate = rate; frame->format = codec->sample_fmt;
                        avcheck(av_channel_layout_copy(&frame->ch_layout, &codec->ch_layout), "Copy AAC channel layout");
                        frame->pts = cursor; avcheck(av_frame_get_buffer(frame.get(), 0), "Allocate AAC samples");
                        for (int s = 0; s < frame_samples; ++s) for (int channel = 0; channel < 2; ++channel) {
                            float g = game.timeline.get(cursor + s, channel);
                            float m = mic ? mic->timeline.get(cursor + s, channel) * config.microphone_gain : 0;
                            float value = config.separate_microphone ? (i == 0 ? g : m) : g + m;
                            reinterpret_cast<float*>(frame->data[channel])[s] = std::clamp(value, -1.f, 1.f);
                        }
                        int result = avcodec_send_frame(codec, frame.get());
                        if (result == AVERROR(EAGAIN)) { drain(i); result = avcodec_send_frame(codec, frame.get()); }
                        avcheck(result, "Encode AAC"); drain(i);
                    }
                    cursor += frame_samples;
                }
            };
            while (!stop.stop_requested()) {
                DWORD wait = WaitForMultipleObjects(mic ? 2 : 1, events, FALSE, 10);
                if (wait == WAIT_FAILED) check(HRESULT_FROM_WIN32(GetLastError()));
                game.read(); if (mic) mic->read();
                if (Clock::now() >= next_device_check) {
                    if (output_id.empty()) check_default(eRender, game);
                    if (mic && microphone_id.empty()) check_default(eCapture, *mic);
                    next_device_check = Clock::now() + std::chrono::seconds(1);
                }
                // A bounded 120ms jitter buffer on the shared video/WASAPI QPC timeline.
                encode_until(av_rescale(qpc_100ns() - epoch, rate, 10'000'000) - rate * 120 / 1000);
            }
            game.read(); if (mic) mic->read();
            encode_until(av_rescale(qpc_100ns() - epoch, rate, 10'000'000));
            game.client->Stop(); if (mic) mic->client->Stop();
            for (int i = 0; i < static_cast<int>(owner.codecs.size()); ++i) drain(i, true);
        } catch (const winrt::hresult_error& e) {
            if (!signaled) ready.set_exception(std::current_exception());
            else fail("WASAPI: " + winrt::to_string(e.message()) + " (endpoint changed or disconnected; restart buffer)");
        } catch (const std::exception& e) {
            if (!signaled) ready.set_exception(std::current_exception()); else fail(std::string("Audio: ") + e.what());
        } catch (...) {
            if (!signaled) ready.set_exception(std::current_exception()); else fail("Unknown audio error");
        }
    }
};
AudioCapture::AudioCapture(const hr_config& config, int64_t epoch, Emit emit, std::function<void(std::string)> fail)
    : impl(std::make_unique<Impl>(*this, config, epoch, std::move(emit), std::move(fail))) {
    codecs.push_back(audio_encoder());
    if (config.microphone_enabled && config.separate_microphone) codecs.push_back(audio_encoder());
}
AudioCapture::~AudioCapture() { stop(); }
void AudioCapture::start() {
    auto ready = impl->ready.get_future();
    impl->thread = std::jthread([this](std::stop_token stop) { impl->run(stop); });
    ready.get();
}
void AudioCapture::stop() {
    if (impl->thread.joinable()) { impl->thread.request_stop(); impl->thread.join(); }
}
}
