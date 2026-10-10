#include "common.h"
#include <ksmedia.h>

namespace hr {

struct AudioPlayback {
    ComPtr<IAudioClient> client;
    ComPtr<IAudioRenderClient> render;
    WAVEFORMATEX* mix_format = nullptr;
    SwrContext* resampler = nullptr;
    UINT32 buffer_frames = 0;
    bool ready = false;

    explicit AudioPlayback(AVCodecContext* codec) {
        if (!codec) return;
        try {
            ComPtr<IMMDeviceEnumerator> enumerator;
            if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)))) return;
            ComPtr<IMMDevice> device;
            if (FAILED(enumerator->GetDefaultAudioEndpoint(eRender, eConsole, &device))) return;
            if (FAILED(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(client.GetAddressOf())))) return;
            if (FAILED(client->GetMixFormat(&mix_format)) || !mix_format) return;
            if (FAILED(client->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, 5'000'000, 0, mix_format, nullptr))) return;
            if (FAILED(client->GetBufferSize(&buffer_frames))) return;
            if (FAILED(client->GetService(IID_PPV_ARGS(&render)))) return;

            AVChannelLayout out_layout{};
            if (mix_format->wFormatTag == WAVE_FORMAT_EXTENSIBLE) {
                auto* ext = reinterpret_cast<WAVEFORMATEXTENSIBLE*>(mix_format);
                if (ext->dwChannelMask) av_channel_layout_from_mask(&out_layout, ext->dwChannelMask);
                else av_channel_layout_default(&out_layout, mix_format->nChannels);
            } else {
                av_channel_layout_default(&out_layout, mix_format->nChannels);
            }
            AVSampleFormat out_fmt = (mix_format->wBitsPerSample == 32) ? AV_SAMPLE_FMT_FLT : AV_SAMPLE_FMT_S16;
            int res = swr_alloc_set_opts2(&resampler, &out_layout, out_fmt, mix_format->nSamplesPerSec,
                                          &codec->ch_layout, codec->sample_fmt, codec->sample_rate, 0, nullptr);
            av_channel_layout_uninit(&out_layout);
            if (res < 0 || !resampler) return;
            if (swr_init(resampler) < 0) return;
            ready = true;
        } catch (...) {
            ready = false;
        }
    }

    ~AudioPlayback() {
        if (client) client->Stop();
        if (resampler) swr_free(&resampler);
        if (mix_format) CoTaskMemFree(mix_format);
    }

    std::atomic<float> volume{1.0f};

    void set_volume(float v) {
        volume.store(std::clamp(v, 0.0f, 1.0f));
    }

    void write(AVFrame* frame) {
        if (!ready || !render || !resampler) return;
        int out_samples = static_cast<int>(av_rescale_rnd(swr_get_delay(resampler, frame->sample_rate) + frame->nb_samples,
                                                          mix_format->nSamplesPerSec, frame->sample_rate, AV_ROUND_UP));
        if (out_samples <= 0) return;
        UINT32 padding = 0;
        if (FAILED(client->GetCurrentPadding(&padding))) return;
        if (padding >= buffer_frames) return;
        UINT32 available = buffer_frames - padding;
        UINT32 to_write = std::min(static_cast<UINT32>(out_samples), available);
        if (to_write == 0) return;

        BYTE* dest = nullptr;
        if (FAILED(render->GetBuffer(to_write, &dest))) return;
        uint8_t* out_ptrs[1]{dest};
        const uint8_t** in_ptrs = const_cast<const uint8_t**>(frame->data);
        int converted = swr_convert(resampler, out_ptrs, to_write, in_ptrs, frame->nb_samples);
        if (converted > 0) {
            float vol = volume.load();
            if (vol <= 0.0001f) {
                memset(dest, 0, static_cast<size_t>(converted) * mix_format->nBlockAlign);
            } else if (vol < 0.999f || vol > 1.001f) {
                if (mix_format->wBitsPerSample == 32) {
                    float* s = reinterpret_cast<float*>(dest);
                    int total = converted * mix_format->nChannels;
                    for (int i = 0; i < total; ++i) {
                        s[i] *= vol;
                    }
                } else if (mix_format->wBitsPerSample == 16) {
                    int16_t* s = reinterpret_cast<int16_t*>(dest);
                    int total = converted * mix_format->nChannels;
                    for (int i = 0; i < total; ++i) {
                        s[i] = static_cast<int16_t>(std::clamp<int>(static_cast<int>(s[i] * vol), -32768, 32767));
                    }
                }
            }
            render->ReleaseBuffer(converted, 0);
        } else {
            render->ReleaseBuffer(0, 0);
        }
    }
};

struct PlayerImpl {
    AVFormatContext* format = nullptr;
    int video_stream_idx = -1;
    int audio_stream_idx = -1;
    Codec video_codec;
    Codec audio_codec;
    SwsContext* sws = nullptr;

    int out_width = 0;
    int out_height = 0;
    int out_stride = 0;
    double duration_sec = 0.0;
    double fps = 60.0;

    std::unique_ptr<AudioPlayback> audio_playback;
    std::atomic<float> volume{1.0f};

    std::atomic<bool> playing{false};
    std::atomic<bool> ended{false};
    std::atomic<bool> stop_worker{false};
    std::atomic<bool> seek_requested{false};
    std::atomic<double> seek_target_sec{0.0};
    std::atomic<double> current_pts_sec{0.0};

    std::mutex frame_mutex;
    std::vector<uint8_t> frame_bgra;
    double frame_pts = 0.0;
    bool has_new_frame = false;

    std::thread worker;
    std::condition_variable cv;
    std::mutex cv_mutex;

    std::mutex seek_mutex;
    std::condition_variable seek_done_cv;
    std::atomic<bool> seek_done{false};

    std::mutex preview_mutex;
    AVFormatContext* preview_format = nullptr;
    Codec preview_codec;

    int64_t clock_start_qpc = 0;
    double clock_start_pts = 0.0;

    ~PlayerImpl() {
        stop_worker = true;
        playing = false;
        seek_done = true;
        cv.notify_all();
        seek_done_cv.notify_all();
        if (worker.joinable()) worker.join();

        {
            std::lock_guard<std::mutex> lock(preview_mutex);
            if (preview_format) avformat_close_input(&preview_format);
            preview_codec.reset();
        }

        if (sws) sws_freeContext(sws);
        if (format) avformat_close_input(&format);
    }

    void start_worker() {
        worker = std::thread([this] { worker_loop(); });
    }

    void perform_seek(double target) {
        if (target < 0.0) target = 0.0;
        if (duration_sec > 0.0 && target > duration_sec) target = duration_sec;

        AVRational tb = format->streams[video_stream_idx]->time_base;
        int64_t ts = static_cast<int64_t>(target / av_q2d(tb));
        int seek_flags = (target <= 0.05) ? (AVSEEK_FLAG_BACKWARD | AVSEEK_FLAG_ANY) : AVSEEK_FLAG_BACKWARD;
        av_seek_frame(format, video_stream_idx, (target <= 0.05 ? 0 : ts), seek_flags);

        avcodec_flush_buffers(video_codec.get());
        if (audio_codec) {
            avcodec_flush_buffers(audio_codec.get());
            if (audio_playback && audio_playback->client) audio_playback->client->Reset();
        }

        auto packet = make_packet();
        auto frame = make_frame();
        for (int attempts = 0; attempts < 300 && av_read_frame(format, packet.get()) >= 0; ++attempts) {
            if (packet->stream_index == video_stream_idx) {
                if (avcodec_send_packet(video_codec.get(), packet.get()) >= 0) {
                    if (avcodec_receive_frame(video_codec.get(), frame.get()) >= 0) {
                        double pts = target;
                        if (frame->best_effort_timestamp != AV_NOPTS_VALUE) {
                            pts = frame->best_effort_timestamp * av_q2d(tb);
                        } else if (frame->pts != AV_NOPTS_VALUE) {
                            pts = frame->pts * av_q2d(tb);
                        }
                        {
                            std::lock_guard<std::mutex> lock(frame_mutex);
                            uint8_t* dst_planes[1]{frame_bgra.data()};
                            int dst_strides[1]{out_stride};
                            sws_scale(sws, frame->data, frame->linesize, 0, frame->height, dst_planes, dst_strides);
                            frame_pts = pts;
                            has_new_frame = true;
                        }
                        current_pts_sec = pts;
                        if (target <= 0.05 || pts >= target - 0.05) {
                            av_packet_unref(packet.get());
                            break;
                        }
                    }
                }
            }
            av_packet_unref(packet.get());
        }

        clock_start_qpc = qpc_100ns();
        clock_start_pts = current_pts_sec.load();
        ended = false;
    }

    void worker_loop() {
        Apartment apartment;
        while (!stop_worker) {
            if (!playing && !seek_requested) {
                std::unique_lock<std::mutex> lock(cv_mutex);
                cv.wait(lock, [&] { return playing.load() || stop_worker.load() || seek_requested.load(); });
            }
            if (stop_worker) break;

            if (seek_requested) {
                double target = seek_target_sec.load();
                seek_requested = false;
                perform_seek(target);
                seek_done = true;
                seek_done_cv.notify_all();
                continue;
            }

            auto packet = make_packet();
            int ret = av_read_frame(format, packet.get());
            if (ret < 0) {
                if (ret == AVERROR_EOF) {
                    playing = false;
                    ended = true;
                    if (audio_playback && audio_playback->client) audio_playback->client->Stop();
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
                continue;
            }

            if (packet->stream_index == video_stream_idx) {
                if (avcodec_send_packet(video_codec.get(), packet.get()) >= 0) {
                    auto frame = make_frame();
                    while (avcodec_receive_frame(video_codec.get(), frame.get()) >= 0) {
                        AVRational tb = format->streams[video_stream_idx]->time_base;
                        double pts = 0.0;
                        if (frame->best_effort_timestamp != AV_NOPTS_VALUE) {
                            pts = frame->best_effort_timestamp * av_q2d(tb);
                        } else if (frame->pts != AV_NOPTS_VALUE) {
                            pts = frame->pts * av_q2d(tb);
                        }

                        if (playing) {
                            double master_time = clock_start_pts + (qpc_100ns() - clock_start_qpc) / 10'000'000.0;
                            double diff = pts - master_time;
                            if (diff > 0.002 && diff < 0.5) {
                                std::this_thread::sleep_for(std::chrono::duration<double>(diff));
                            }
                        }

                        {
                            std::lock_guard<std::mutex> lock(frame_mutex);
                            uint8_t* dst_planes[1]{frame_bgra.data()};
                            int dst_strides[1]{out_stride};
                            sws_scale(sws, frame->data, frame->linesize, 0, frame->height, dst_planes, dst_strides);
                            frame_pts = pts;
                            has_new_frame = true;
                        }
                        current_pts_sec = pts;

                        if (seek_requested || !playing || stop_worker) break;
                    }
                }
            } else if (packet->stream_index == audio_stream_idx && audio_codec && audio_playback && audio_playback->ready) {
                if (avcodec_send_packet(audio_codec.get(), packet.get()) >= 0) {
                    auto frame = make_frame();
                    while (avcodec_receive_frame(audio_codec.get(), frame.get()) >= 0) {
                        if (playing) {
                            audio_playback->write(frame.get());
                        }
                    }
                }
            }
            av_packet_unref(packet.get());
        }
    }
};

hr_player* player_open(const wchar_t* path, int32_t* width, int32_t* height, double* duration) {
    if (!path || !width || !height || !duration) throw std::runtime_error("Invalid player arguments.");
    auto player = std::make_unique<PlayerImpl>();
    std::string filename = utf8(path);

    avcheck(avformat_open_input(&player->format, filename.c_str(), nullptr, nullptr), "Open player media");
    avcheck(avformat_find_stream_info(player->format, nullptr), "Read stream info");

    const AVCodec* v_decoder = nullptr;
    player->video_stream_idx = av_find_best_stream(player->format, AVMEDIA_TYPE_VIDEO, -1, -1, &v_decoder, 0);
    avcheck(player->video_stream_idx, "Find video stream");

    player->video_codec = Codec(avcodec_alloc_context3(v_decoder));
    if (!player->video_codec) throw std::bad_alloc();
    avcheck(avcodec_parameters_to_context(player->video_codec.get(), player->format->streams[player->video_stream_idx]->codecpar), "Configure video decoder");
    player->video_codec->thread_count = 4;
    avcheck(avcodec_open2(player->video_codec.get(), v_decoder, nullptr), "Open video decoder");

    if (avformat_open_input(&player->preview_format, filename.c_str(), nullptr, nullptr) >= 0) {
        if (avformat_find_stream_info(player->preview_format, nullptr) >= 0) {
            player->preview_codec = Codec(avcodec_alloc_context3(v_decoder));
            if (player->preview_codec) {
                avcodec_parameters_to_context(player->preview_codec.get(), player->preview_format->streams[player->video_stream_idx]->codecpar);
                player->preview_codec->thread_count = 2;
                avcodec_open2(player->preview_codec.get(), v_decoder, nullptr);
            }
        }
    }

    const AVCodec* a_decoder = nullptr;
    player->audio_stream_idx = av_find_best_stream(player->format, AVMEDIA_TYPE_AUDIO, -1, -1, &a_decoder, 0);
    if (player->audio_stream_idx >= 0 && a_decoder) {
        player->audio_codec = Codec(avcodec_alloc_context3(a_decoder));
        if (player->audio_codec) {
            if (avcodec_parameters_to_context(player->audio_codec.get(), player->format->streams[player->audio_stream_idx]->codecpar) >= 0) {
                if (avcodec_open2(player->audio_codec.get(), a_decoder, nullptr) >= 0) {
                    player->audio_playback = std::make_unique<AudioPlayback>(player->audio_codec.get());
                    if (player->audio_playback) player->audio_playback->set_volume(player->volume.load());
                }
            }
        }
    }

    int raw_w = player->video_codec->width;
    int raw_h = player->video_codec->height;
    if (raw_w < 2 || raw_h < 2) throw std::runtime_error("Invalid video dimensions.");

    player->out_width = raw_w;
    player->out_height = raw_h;
    if (player->out_width > 1920) {
        player->out_height = std::max(2, static_cast<int>(av_rescale(player->out_height, 1920, player->out_width)));
        player->out_width = 1920;
        player->out_height = (player->out_height / 2) * 2;
    }
    player->out_stride = player->out_width * 4;
    player->frame_bgra.resize(static_cast<size_t>(player->out_stride) * player->out_height, 0);

    player->sws = sws_getContext(raw_w, raw_h, player->video_codec->pix_fmt,
                                 player->out_width, player->out_height, AV_PIX_FMT_BGRA,
                                 SWS_BILINEAR, nullptr, nullptr, nullptr);
    if (!player->sws) throw std::runtime_error("Failed to allocate scaler.");

    if (player->format->duration > 0) {
        player->duration_sec = static_cast<double>(player->format->duration) / AV_TIME_BASE;
    } else {
        auto* stream = player->format->streams[player->video_stream_idx];
        if (stream->duration > 0) player->duration_sec = stream->duration * av_q2d(stream->time_base);
    }

    AVRational r_fps = player->format->streams[player->video_stream_idx]->r_frame_rate;
    player->fps = (r_fps.den > 0 && r_fps.num > 0) ? av_q2d(r_fps) : 60.0;

    *width = player->out_width;
    *height = player->out_height;
    *duration = player->duration_sec;

    // Decode initial frame
    player->perform_seek(0.0);
    player->start_worker();

    return reinterpret_cast<hr_player*>(player.release());
}

void player_close(hr_player* ptr) {
    delete reinterpret_cast<PlayerImpl*>(ptr);
}

void player_play(hr_player* ptr) {
    if (!ptr) return;
    auto* player = reinterpret_cast<PlayerImpl*>(ptr);
    if (player->ended) {
        player->perform_seek(0.0);
        player->ended = false;
    }
    player->clock_start_qpc = qpc_100ns();
    player->clock_start_pts = player->current_pts_sec.load();
    player->playing = true;
    if (player->audio_playback && player->audio_playback->client) player->audio_playback->client->Start();
    player->cv.notify_one();
}

void player_pause(hr_player* ptr) {
    if (!ptr) return;
    auto* player = reinterpret_cast<PlayerImpl*>(ptr);
    player->playing = false;
    if (player->audio_playback && player->audio_playback->client) player->audio_playback->client->Stop();
}

void player_seek(hr_player* ptr, double seconds) {
    if (!ptr) return;
    auto* player = reinterpret_cast<PlayerImpl*>(ptr);
    std::unique_lock<std::mutex> lock(player->seek_mutex);
    player->seek_done = false;
    player->seek_target_sec = seconds;
    player->seek_requested = true;
    player->cv.notify_one();
    player->seek_done_cv.wait_for(lock, std::chrono::milliseconds(300), [&] { return player->seek_done.load(); });
}

void player_get_state(hr_player* ptr, int32_t* is_playing, int32_t* is_ended, double* current_pts) {
    if (!ptr) return;
    auto* player = reinterpret_cast<PlayerImpl*>(ptr);
    if (is_playing) *is_playing = player->playing.load() ? 1 : 0;
    if (is_ended) *is_ended = player->ended.load() ? 1 : 0;
    if (current_pts) *current_pts = player->current_pts_sec.load();
}

void player_read_frame(hr_player* ptr, uint8_t* buffer, int32_t stride, int32_t width, int32_t height, int32_t* updated, double* pts) {
    if (!ptr || !buffer || !updated || !pts) return;
    auto* player = reinterpret_cast<PlayerImpl*>(ptr);
    *updated = 0;
    std::lock_guard<std::mutex> lock(player->frame_mutex);
    *pts = player->frame_pts;
    if (player->has_new_frame && width == player->out_width && height == player->out_height && stride == player->out_stride) {
        memcpy(buffer, player->frame_bgra.data(), player->frame_bgra.size());
        *updated = 1;
        player->has_new_frame = false;
    }
}

void player_preview(hr_player* ptr, double seconds, int32_t max_width,
                    uint8_t** pixels, int32_t* width, int32_t* height, int32_t* stride) {
    if (!ptr || !pixels || !width || !height || !stride || max_width < 32 || max_width > 1920)
        throw std::runtime_error("Invalid preview arguments.");
    *pixels = nullptr;
    auto* player = reinterpret_cast<PlayerImpl*>(ptr);
    if (!player) throw std::runtime_error("Null player.");
    if (player->stop_worker.load()) throw std::runtime_error("Player closing.");

    std::lock_guard<std::mutex> lock(player->preview_mutex);
    if (player->stop_worker.load() || !player->preview_format || !player->preview_codec)
        throw std::runtime_error("Preview decoder not available.");

    AVRational tb = player->preview_format->streams[player->video_stream_idx]->time_base;
    int64_t ts = static_cast<int64_t>(seconds / av_q2d(tb));
    int seek_flags = (seconds <= 0.05) ? (AVSEEK_FLAG_BACKWARD | AVSEEK_FLAG_ANY) : AVSEEK_FLAG_BACKWARD;
    av_seek_frame(player->preview_format, player->video_stream_idx, (seconds <= 0.05 ? 0 : ts), seek_flags);
    avcodec_flush_buffers(player->preview_codec.get());

    auto packet = make_packet();
    auto frame = make_frame();
    auto best_frame = make_frame();
    double best_diff = 1e9;
    bool decoded = false;

    for (int attempts = 0; attempts < 250 && !player->stop_worker.load() && av_read_frame(player->preview_format, packet.get()) >= 0; ++attempts) {
        if (packet->stream_index == player->video_stream_idx) {
            if (avcodec_send_packet(player->preview_codec.get(), packet.get()) >= 0) {
                while (avcodec_receive_frame(player->preview_codec.get(), frame.get()) >= 0) {
                    double pts = frame->best_effort_timestamp != AV_NOPTS_VALUE
                        ? frame->best_effort_timestamp * av_q2d(tb)
                        : (frame->pts != AV_NOPTS_VALUE ? frame->pts * av_q2d(tb) : seconds);

                    double diff = std::abs(pts - seconds);
                    if (diff < best_diff) {
                        best_diff = diff;
                        av_frame_unref(best_frame.get());
                        av_frame_ref(best_frame.get(), frame.get());
                        decoded = true;
                    }

                    if (diff < 0.08 || pts > seconds + 0.1) {
                        break;
                    }
                }
                if (decoded && (best_diff < 0.08 || attempts > 80)) {
                    av_packet_unref(packet.get());
                    break;
                }
            }
        }
        av_packet_unref(packet.get());
    }

    if (!decoded || best_frame->width < 1 || best_frame->height < 1) throw std::runtime_error("No decodable preview frame.");

    *width = std::min(max_width, best_frame->width);
    *height = std::max(1, static_cast<int>(av_rescale(best_frame->height, *width, best_frame->width)));
    *stride = *width * 4;

    SwsContext* scaler = sws_getContext(best_frame->width, best_frame->height, static_cast<AVPixelFormat>(best_frame->format),
                                        *width, *height, AV_PIX_FMT_BGRA, SWS_FAST_BILINEAR, nullptr, nullptr, nullptr);
    if (!scaler) throw std::runtime_error("Preview scaler unavailable.");

    auto* output = static_cast<uint8_t*>(av_malloc(static_cast<size_t>(*stride) * *height));
    if (!output) { sws_freeContext(scaler); throw std::bad_alloc(); }
    uint8_t* planes[]{output, nullptr, nullptr, nullptr};
    int lines[]{*stride, 0, 0, 0};
    int res = sws_scale(scaler, best_frame->data, best_frame->linesize, 0, best_frame->height, planes, lines);
    sws_freeContext(scaler);
    if (res <= 0) { av_free(output); throw std::runtime_error("Preview conversion failed."); }
    *pixels = output;
}

void player_set_volume(hr_player* ptr, float volume) {
    if (!ptr) return;
    auto* player = reinterpret_cast<PlayerImpl*>(ptr);
    float clamped = std::clamp(volume, 0.0f, 1.0f);
    player->volume.store(clamped);
    if (player->audio_playback) {
        player->audio_playback->set_volume(clamped);
    }
}

} // namespace hr
