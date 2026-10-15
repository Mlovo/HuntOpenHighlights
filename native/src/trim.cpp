#include "common.h"

namespace hr {

struct SwsDelete {
    void operator()(SwsContext* p) const {
        if (p) sws_freeContext(p);
    }
};
using Sws = std::unique_ptr<SwsContext, SwsDelete>;

// Keeps DTS strictly increasing per output stream and PTS >= DTS, then interleaves the packet.
static void write_monotonic(AVFormatContext* output, AVPacket* packet, std::vector<int64_t>& last_dts, const char* operation) {
    auto& last = last_dts[packet->stream_index];
    if (last != AV_NOPTS_VALUE && packet->dts <= last) packet->dts = last + 1;
    if (packet->pts < packet->dts) packet->pts = packet->dts;
    last = packet->dts;
    avcheck(av_interleaved_write_frame(output, packet), operation);
}

struct TrimOutputGuard {
    AVFormatContext* format = nullptr;
    std::wstring path;
    bool committed = false;

    explicit TrimOutputGuard(std::wstring p) : path(std::move(p)) {}
    ~TrimOutputGuard() {
        if (format) {
            if (format->pb) avio_closep(&format->pb);
            avformat_free_context(format);
            format = nullptr;
        }
        if (!committed && !path.empty()) {
            _wremove(path.c_str());
        }
    }
};

void trim(const wchar_t* source_path, const wchar_t* target_path,
          double start_seconds, double end_seconds, bool accurate,
          hr_trim_progress_callback progress_cb, void* user) {
    if (!source_path || !target_path || source_path[0] == L'\0' || target_path[0] == L'\0') {
        throw std::runtime_error("Missing source or target path.");
    }
    if (_wcsicmp(source_path, target_path) == 0) {
        throw std::runtime_error("Destination path cannot be identical to source path.");
    }
    if (!std::isfinite(start_seconds) || !std::isfinite(end_seconds)) {
        throw std::runtime_error("Trim boundaries must be finite.");
    }
    if (start_seconds < 0.0) start_seconds = 0.0;
    if (end_seconds <= start_seconds) {
        throw std::runtime_error("End time must strictly exceed start time.");
    }
    if (end_seconds - start_seconds < 0.1) {
        throw std::runtime_error("Clip duration is too short (minimum 0.1s).");
    }

    struct InputContext {
        AVFormatContext* format = nullptr;
        ~InputContext() {
            if (format) avformat_close_input(&format);
        }
    } input;

    std::string in_file = utf8(source_path);
    avcheck(avformat_open_input(&input.format, in_file.c_str(), nullptr, nullptr), "Open source media for trim");
    avcheck(avformat_find_stream_info(input.format, nullptr), "Read stream info for trim");

    int video_idx = av_find_best_stream(input.format, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
    if (video_idx < 0) {
        throw std::runtime_error("No video stream found in source media.");
    }

    AVStream* in_vst = input.format->streams[video_idx];
    int64_t start_us = static_cast<int64_t>(start_seconds * 1'000'000.0);
    int64_t end_us = static_cast<int64_t>(end_seconds * 1'000'000.0);
    int64_t target_pts = static_cast<int64_t>(start_seconds / av_q2d(in_vst->time_base));

    // Estimate single frame duration in microseconds
    int64_t frame_dur_us = 33333; // Fallback ~30fps
    if (in_vst->avg_frame_rate.num > 0 && in_vst->avg_frame_rate.den > 0) {
        frame_dur_us = av_rescale_q(1, av_inv_q(in_vst->avg_frame_rate), microseconds);
    } else if (in_vst->r_frame_rate.num > 0 && in_vst->r_frame_rate.den > 0) {
        frame_dur_us = av_rescale_q(1, av_inv_q(in_vst->r_frame_rate), microseconds);
    }
    if (frame_dur_us <= 0) frame_dur_us = 33333;

    // Determine whether frame-accurate re-encoding is required.
    // When accurate == true or cutting at a non-keyframe, frame-accurate re-encoding is performed.
    bool need_reencode = accurate;
    if (!need_reencode) {
        if (start_seconds <= 0.05) {
            need_reencode = false; // Starts at origin 0.0s
        } else {
            bool is_keyframe_aligned = false;
            int entries_count = avformat_index_get_entries_count(in_vst);
            if (entries_count > 0) {
                const AVIndexEntry* entry_bw = avformat_index_get_entry_from_timestamp(in_vst, target_pts, AVSEEK_FLAG_BACKWARD);
                if (entry_bw && (entry_bw->flags & AVINDEX_KEYFRAME)) {
                    int64_t entry_pts_us = av_rescale_q(entry_bw->timestamp, in_vst->time_base, microseconds);
                    if (std::abs(entry_pts_us - start_us) <= frame_dur_us / 2) {
                        is_keyframe_aligned = true;
                    }
                }
                if (!is_keyframe_aligned) {
                    const AVIndexEntry* entry_fw = avformat_index_get_entry_from_timestamp(in_vst, target_pts, 0);
                    if (entry_fw && (entry_fw->flags & AVINDEX_KEYFRAME)) {
                        int64_t entry_pts_us = av_rescale_q(entry_fw->timestamp, in_vst->time_base, microseconds);
                        if (std::abs(entry_pts_us - start_us) <= frame_dur_us / 2) {
                            is_keyframe_aligned = true;
                        }
                    }
                }
            } else {
                // Probe nearest preceding keyframe via demuxer seek
                if (av_seek_frame(input.format, video_idx, target_pts, AVSEEK_FLAG_BACKWARD) >= 0) {
                    auto probe_pkt = make_packet();
                    while (av_read_frame(input.format, probe_pkt.get()) >= 0) {
                        if (probe_pkt->stream_index == video_idx && (probe_pkt->flags & AV_PKT_FLAG_KEY)) {
                            int64_t pts = probe_pkt->pts != AV_NOPTS_VALUE ? probe_pkt->pts : probe_pkt->dts;
                            int64_t pkt_pts_us = av_rescale_q(pts, in_vst->time_base, microseconds);
                            if (std::abs(pkt_pts_us - start_us) <= frame_dur_us / 2) {
                                is_keyframe_aligned = true;
                            }
                            break;
                        }
                        av_packet_unref(probe_pkt.get());
                    }
                }
            }
            if (!is_keyframe_aligned) {
                need_reencode = true;
            }
        }
    }

    Codec dec_ctx;
    Codec enc_ctx;
    if (need_reencode) {
        const AVCodec* decoder = avcodec_find_decoder(in_vst->codecpar->codec_id);
        if (!decoder) {
            throw std::runtime_error("Unsupported video codec for decode in trim.");
        }
        dec_ctx = Codec(avcodec_alloc_context3(decoder));
        if (!dec_ctx) throw std::bad_alloc();
        avcheck(avcodec_parameters_to_context(dec_ctx.get(), in_vst->codecpar), "Configure video decoder parameters");
        dec_ctx->thread_count = 4;
        avcheck(avcodec_open2(dec_ctx.get(), decoder, nullptr), "Open video decoder for trim");

        const AVCodec* encoder = avcodec_find_encoder_by_name("libx264");
        if (!encoder) {
            encoder = avcodec_find_encoder(AV_CODEC_ID_H264);
        }
        if (!encoder) {
            throw std::runtime_error("H.264 encoder (libx264) not found for trim re-encoding.");
        }
        enc_ctx = Codec(avcodec_alloc_context3(encoder));
        if (!enc_ctx) throw std::bad_alloc();

        enc_ctx->width = dec_ctx->width;
        enc_ctx->height = dec_ctx->height;
        enc_ctx->pix_fmt = AV_PIX_FMT_YUV420P;
        AVRational fps = in_vst->avg_frame_rate.num > 0 ? in_vst->avg_frame_rate : in_vst->r_frame_rate;
        if (fps.num <= 0 || fps.den <= 0) fps = AVRational{30, 1};
        enc_ctx->framerate = fps;
        // Source time base keeps variable-frame-rate capture timing intact.
        enc_ctx->time_base = in_vst->time_base;
        enc_ctx->max_b_frames = 0;
        enc_ctx->gop_size = (fps.num / fps.den > 0) ? (fps.num / fps.den) : 30;
        enc_ctx->flags |= AV_CODEC_FLAG_GLOBAL_HEADER | AV_CODEC_FLAG_CLOSED_GOP;

        if (in_vst->codecpar->bit_rate > 0) {
            enc_ctx->bit_rate = in_vst->codecpar->bit_rate;
        } else if (input.format->bit_rate > 0) {
            enc_ctx->bit_rate = input.format->bit_rate;
        } else {
            enc_ctx->bit_rate = 6'000'000;
        }

        enc_ctx->color_range = dec_ctx->color_range;
        enc_ctx->colorspace = dec_ctx->colorspace;
        enc_ctx->color_primaries = dec_ctx->color_primaries;
        enc_ctx->color_trc = dec_ctx->color_trc;
        enc_ctx->sample_aspect_ratio = dec_ctx->sample_aspect_ratio;

        AVDictionary* enc_opts = nullptr;
        av_dict_set(&enc_opts, "preset", "veryfast", 0);
        av_dict_set(&enc_opts, "tune", "zerolatency", 0);
        av_dict_set(&enc_opts, "crf", "20", 0);
        int enc_res = avcodec_open2(enc_ctx.get(), encoder, &enc_opts);
        av_dict_free(&enc_opts);
        avcheck(enc_res, "Open libx264 video encoder for trim");
    }

    TrimOutputGuard output(target_path);
    std::string out_file = utf8(target_path);
    avcheck(avformat_alloc_output_context2(&output.format, nullptr, "mp4", out_file.c_str()), "Allocate trim output context");
    if (!output.format) throw std::bad_alloc();

    std::vector<int> stream_map(input.format->nb_streams, -1);
    int active_audio_streams = 0;

    for (unsigned int i = 0; i < input.format->nb_streams; ++i) {
        AVStream* in_st = input.format->streams[i];
        if (in_st->codecpar->codec_type != AVMEDIA_TYPE_VIDEO &&
            in_st->codecpar->codec_type != AVMEDIA_TYPE_AUDIO) {
            continue;
        }

        AVStream* out_st = avformat_new_stream(output.format, nullptr);
        if (!out_st) throw std::bad_alloc();

        if (i == static_cast<unsigned int>(video_idx) && need_reencode) {
            avcheck(avcodec_parameters_from_context(out_st->codecpar, enc_ctx.get()), "Copy trim encoder parameters to output stream");
            out_st->codecpar->codec_tag = 0;
            out_st->time_base = enc_ctx->time_base;
            out_st->avg_frame_rate = enc_ctx->framerate;
            out_st->r_frame_rate = enc_ctx->framerate;
        } else {
            avcheck(avcodec_parameters_copy(out_st->codecpar, in_st->codecpar), "Copy stream codec parameters");
            out_st->codecpar->codec_tag = 0;
            if (out_st->codecpar->codec_id == AV_CODEC_ID_HEVC) {
                out_st->codecpar->codec_tag = MKTAG('h', 'v', 'c', '1');
            }
            out_st->time_base = in_st->time_base;
            out_st->avg_frame_rate = in_st->avg_frame_rate;
            out_st->r_frame_rate = in_st->r_frame_rate;
        }

        out_st->disposition = in_st->disposition;
        av_dict_copy(&out_st->metadata, in_st->metadata, 0);

        if (in_st->codecpar->codec_type == AVMEDIA_TYPE_AUDIO) {
            active_audio_streams++;
        }

        stream_map[i] = out_st->index;
    }

    avcheck(avio_open(&output.format->pb, out_file.c_str(), AVIO_FLAG_WRITE), "Open trim output file");
    AVDictionary* options = nullptr;
    av_dict_set(&options, "movflags", "+faststart", 0);
    int res = avformat_write_header(output.format, &options);
    av_dict_free(&options);
    avcheck(res, "Write trim MP4 header");

    // Seek backward to preceding keyframe
    if (start_seconds > 0.05) {
        int seek_res = av_seek_frame(input.format, video_idx, target_pts, AVSEEK_FLAG_BACKWARD);
        if (seek_res < 0) {
            av_seek_frame(input.format, video_idx, 0, AVSEEK_FLAG_BACKWARD);
        }
    } else {
        av_seek_frame(input.format, video_idx, 0, AVSEEK_FLAG_BACKWARD);
    }

    int64_t origin_us = AV_NOPTS_VALUE;
    std::vector<int64_t> last_dts(output.format->nb_streams, AV_NOPTS_VALUE);
    std::vector<bool> audio_done(input.format->nb_streams, false);
    int completed_audio_streams = 0;
    bool video_started = false;
    bool video_done = false;
    bool video_flushed = false;
    int64_t last_encoded_pts = AV_NOPTS_VALUE;

    struct QueuedAudioPacket {
        Packet pkt;
        int in_idx;
        int64_t pts_us;
        int64_t dts_us;
    };
    std::deque<QueuedAudioPacket> audio_queue;
    Sws sws_ctx;
    Frame converted_frame; // Pixel-format conversion target, allocated once per output size.

    auto packet = make_packet();
    auto dec_frame = make_frame();
    auto enc_pkt = make_packet();

    double trim_duration = end_seconds - start_seconds;
    if (trim_duration < 0.1) trim_duration = 0.1;
    int packet_counter = 0;

    auto write_audio_packet = [&](AVPacket* pkt, int in_stream_idx, int64_t pts_us, int64_t dts_us) {
        if (pts_us < origin_us) {
            return; // Discard audio packet preceding origin_us (privacy leak prevention)
        }
        if (pts_us > end_us) {
            if (!audio_done[in_stream_idx]) {
                audio_done[in_stream_idx] = true;
                completed_audio_streams++;
            }
            return;
        }

        int out_stream_idx = stream_map[in_stream_idx];
        AVStream* in_stream = input.format->streams[in_stream_idx];
        AVStream* out_stream = output.format->streams[out_stream_idx];

        int64_t rel_pts_us = pts_us - origin_us;
        int64_t rel_dts_us = dts_us >= origin_us ? (dts_us - origin_us) : rel_pts_us;

        pkt->pts = av_rescale_q(rel_pts_us, microseconds, out_stream->time_base);
        pkt->dts = av_rescale_q(rel_dts_us, microseconds, out_stream->time_base);
        if (pkt->duration > 0) {
            pkt->duration = av_rescale_q(pkt->duration, in_stream->time_base, out_stream->time_base);
        }
        pkt->pos = -1;
        pkt->stream_index = out_stream_idx;
        write_monotonic(output.format, pkt, last_dts, "Write remuxed audio packet");
    };

    auto report_progress = [&](int64_t rel_pts_us) {
        if (!progress_cb || ++packet_counter % 10 != 0) return;
        double prog = std::clamp(static_cast<double>(rel_pts_us) / 1'000'000.0 / trim_duration, 0.0, 0.99);
        if (progress_cb(user, prog) != 0) throw std::runtime_error("Операция обрезки отменена пользователем.");
    };

    // Moves every packet the encoder has ready into the output. av_packet_rescale_ts converts duration too.
    auto drain_encoder = [&](const char* operation, int64_t rel_pts_us) {
        int out_v_idx = stream_map[video_idx];
        AVStream* out_v_st = output.format->streams[out_v_idx];
        while (avcodec_receive_packet(enc_ctx.get(), enc_pkt.get()) == 0) {
            enc_pkt->stream_index = out_v_idx;
            av_packet_rescale_ts(enc_pkt.get(), enc_ctx->time_base, out_v_st->time_base);
            enc_pkt->pos = -1;
            write_monotonic(output.format, enc_pkt.get(), last_dts, operation);
            av_packet_unref(enc_pkt.get());
            if (rel_pts_us >= 0) report_progress(rel_pts_us);
        }
    };

    auto flush_audio_queue = [&]() {
        while (!audio_queue.empty()) {
            auto& q = audio_queue.front();
            write_audio_packet(q.pkt.get(), q.in_idx, q.pts_us, q.dts_us);
            audio_queue.pop_front();
        }
    };

    auto flush_encoder = [&]() {
        if (!video_flushed && need_reencode && enc_ctx) {
            video_flushed = true;
            avcodec_send_frame(enc_ctx.get(), nullptr);
            drain_encoder("Write flushed trim video packet", -1);
        }
    };

    auto encode_video_frame = [&](AVFrame* frame) {
        int64_t frame_pts = frame->best_effort_timestamp != AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
        if (frame_pts == AV_NOPTS_VALUE) return;
        int64_t frame_pts_us = av_rescale_q(frame_pts, in_vst->time_base, microseconds);

        if (!video_started) {
            if (frame_pts_us < start_us) {
                return; // Discard pre-roll video frame
            }
            video_started = true;
            origin_us = frame_pts_us;
            flush_audio_queue();
        }

        if (frame_pts_us > end_us) {
            video_done = true;
            return;
        }

        AVFrame* frame_to_send = frame;
        if (frame->format != enc_ctx->pix_fmt ||
            frame->width != enc_ctx->width ||
            frame->height != enc_ctx->height) {
            sws_ctx.reset(sws_getCachedContext(
                sws_ctx.release(),
                frame->width, frame->height, static_cast<AVPixelFormat>(frame->format),
                enc_ctx->width, enc_ctx->height, enc_ctx->pix_fmt,
                SWS_BILINEAR, nullptr, nullptr, nullptr));
            if (!sws_ctx) throw std::bad_alloc();

            if (!converted_frame) {
                converted_frame = make_frame();
                converted_frame->format = enc_ctx->pix_fmt;
                converted_frame->width = enc_ctx->width;
                converted_frame->height = enc_ctx->height;
                avcheck(av_frame_get_buffer(converted_frame.get(), 32), "Allocate converted frame buffer");
            } else {
                // The encoder may still reference the previous picture; this copies only in that case.
                avcheck(av_frame_make_writable(converted_frame.get()), "Reuse converted frame buffer");
            }
            sws_scale(sws_ctx.get(), frame->data, frame->linesize, 0, frame->height,
                      converted_frame->data, converted_frame->linesize);
            frame_to_send = converted_frame.get();
        }

        int64_t rel_pts_us = frame_pts_us - origin_us;
        int64_t enc_pts = av_rescale_q(rel_pts_us, microseconds, enc_ctx->time_base);
        if (last_encoded_pts != AV_NOPTS_VALUE && enc_pts <= last_encoded_pts) {
            enc_pts = last_encoded_pts + 1;
        }
        last_encoded_pts = enc_pts;
        frame_to_send->pts = enc_pts;

        if (enc_pts == 0) {
            frame_to_send->pict_type = AV_PICTURE_TYPE_I;
            frame_to_send->flags |= AV_FRAME_FLAG_KEY;
        } else {
            frame_to_send->pict_type = AV_PICTURE_TYPE_NONE;
            frame_to_send->flags &= ~AV_FRAME_FLAG_KEY;
        }

        avcheck(avcodec_send_frame(enc_ctx.get(), frame_to_send), "Send frame to trim video encoder");
        drain_encoder("Write encoded trim video packet", rel_pts_us);
    };

    while (av_read_frame(input.format, packet.get()) >= 0) {
        int in_idx = packet->stream_index;
        if (in_idx < 0 || in_idx >= static_cast<int>(stream_map.size()) || stream_map[in_idx] < 0) {
            av_packet_unref(packet.get());
            continue;
        }

        AVStream* in_st = input.format->streams[in_idx];
        int out_idx = stream_map[in_idx];
        AVStream* out_st = output.format->streams[out_idx];

        int64_t pkt_pts = packet->pts != AV_NOPTS_VALUE ? packet->pts : packet->dts;
        int64_t pkt_dts = packet->dts != AV_NOPTS_VALUE ? packet->dts : packet->pts;
        if (pkt_pts == AV_NOPTS_VALUE && pkt_dts == AV_NOPTS_VALUE) {
            av_packet_unref(packet.get());
            continue;
        }

        int64_t pkt_pts_us = av_rescale_q(pkt_pts, in_st->time_base, microseconds);
        int64_t pkt_dts_us = av_rescale_q(pkt_dts, in_st->time_base, microseconds);

        // -------------------------------------------------------------
        // Audio stream handling
        // -------------------------------------------------------------
        if (in_st->codecpar->codec_type == AVMEDIA_TYPE_AUDIO) {
            if (audio_done[in_idx]) {
                av_packet_unref(packet.get());
                continue;
            }

            if (pkt_pts_us > end_us) {
                if (!audio_done[in_idx]) {
                    audio_done[in_idx] = true;
                    completed_audio_streams++;
                }
                av_packet_unref(packet.get());
                if (video_done && completed_audio_streams >= active_audio_streams) {
                    break;
                }
                continue;
            }

            // Privacy: discard audio prior to requested start
            if (pkt_pts_us < start_us || pkt_dts_us < start_us) {
                av_packet_unref(packet.get());
                continue;
            }

            if (!video_started) {
                QueuedAudioPacket q;
                q.pkt = make_packet();
                av_packet_move_ref(q.pkt.get(), packet.get());
                q.in_idx = in_idx;
                q.pts_us = pkt_pts_us;
                q.dts_us = pkt_dts_us;
                audio_queue.push_back(std::move(q));
                continue;
            }

            write_audio_packet(packet.get(), in_idx, pkt_pts_us, pkt_dts_us);
            av_packet_unref(packet.get());

            if (video_done && completed_audio_streams >= active_audio_streams) {
                break;
            }
            continue;
        }

        // -------------------------------------------------------------
        // Video stream handling
        // -------------------------------------------------------------
        if (in_idx == video_idx) {
            if (need_reencode) {
                if (video_done) {
                    av_packet_unref(packet.get());
                    if (pkt_pts_us > end_us + 1'500'000) {
                        break;
                    }
                    continue;
                }

                int ret = avcodec_send_packet(dec_ctx.get(), packet.get());
                av_packet_unref(packet.get());
                if (ret < 0) continue;

                while (avcodec_receive_frame(dec_ctx.get(), dec_frame.get()) == 0) {
                    encode_video_frame(dec_frame.get());
                    av_frame_unref(dec_frame.get());
                    if (video_done) {
                        flush_encoder();
                        break;
                    }
                }

                if (video_done && completed_audio_streams >= active_audio_streams) {
                    break;
                }
                continue;
            } else {
                // Fast Stream Copy Path
                if (video_done) {
                    av_packet_unref(packet.get());
                    if (pkt_pts_us > end_us + 1'500'000) {
                        break;
                    }
                    continue;
                }

                if (!video_started) {
                    if ((packet->flags & AV_PKT_FLAG_KEY) &&
                        (start_seconds <= 0.05 || pkt_pts_us >= start_us - frame_dur_us / 2)) {
                        video_started = true;
                        origin_us = std::min(pkt_pts_us, pkt_dts_us);
                        flush_audio_queue();
                    } else {
                        av_packet_unref(packet.get());
                        continue;
                    }
                }

                if (pkt_dts_us < origin_us || pkt_pts_us < origin_us) {
                    av_packet_unref(packet.get());
                    continue;
                }

                if (pkt_pts_us > end_us) {
                    video_done = true;
                    av_packet_unref(packet.get());
                    if (completed_audio_streams >= active_audio_streams) {
                        break;
                    }
                    continue;
                }

                int64_t rel_pts_us = pkt_pts_us - origin_us;
                int64_t rel_dts_us = pkt_dts_us - origin_us;

                packet->pts = av_rescale_q(rel_pts_us, microseconds, out_st->time_base);
                packet->dts = av_rescale_q(rel_dts_us, microseconds, out_st->time_base);
                if (packet->duration > 0) {
                    packet->duration = av_rescale_q(packet->duration, in_st->time_base, out_st->time_base);
                }
                packet->pos = -1;
                packet->stream_index = out_idx;
                write_monotonic(output.format, packet.get(), last_dts, "Write remuxed trim packet");
                av_packet_unref(packet.get());
                report_progress(rel_pts_us);
                continue;
            }
        }

        av_packet_unref(packet.get());
    }

    // Flush decoder and encoder if EOF was reached
    if (need_reencode && dec_ctx && !video_done) {
        avcodec_send_packet(dec_ctx.get(), nullptr);
        while (avcodec_receive_frame(dec_ctx.get(), dec_frame.get()) == 0) {
            encode_video_frame(dec_frame.get());
            av_frame_unref(dec_frame.get());
            if (video_done) break;
        }
    }
    flush_encoder();

    if (!video_started) {
        throw std::runtime_error("Failed to find video frame in specified range.");
    }

    avcheck(av_write_trailer(output.format), "Finalize trim MP4 trailer");
    avcheck(avio_closep(&output.format->pb), "Flush trim output file");
    output.committed = true;

    if (progress_cb) progress_cb(user, 1.0);
}

} // namespace hr
