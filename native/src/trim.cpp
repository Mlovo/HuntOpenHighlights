#include "common.h"

namespace hr {

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
          double start_seconds, double end_seconds, bool /*accurate*/,
          hr_trim_progress_callback progress_cb, void* user) {
    if (!source_path || !target_path || source_path[0] == L'\0' || target_path[0] == L'\0') {
        throw std::runtime_error("Missing source or target path.");
    }
    if (_wcsicmp(source_path, target_path) == 0) {
        throw std::runtime_error("Destination path cannot be identical to source path.");
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

    TrimOutputGuard output(target_path);
    std::string out_file = utf8(target_path);
    avcheck(avformat_alloc_output_context2(&output.format, nullptr, "mp4", out_file.c_str()), "Allocate trim output context");
    if (!output.format) throw std::bad_alloc();

    std::vector<int> stream_map(input.format->nb_streams, -1);
    for (unsigned int i = 0; i < input.format->nb_streams; ++i) {
        AVStream* in_st = input.format->streams[i];
        if (in_st->codecpar->codec_type != AVMEDIA_TYPE_VIDEO &&
            in_st->codecpar->codec_type != AVMEDIA_TYPE_AUDIO) {
            continue;
        }

        AVStream* out_st = avformat_new_stream(output.format, nullptr);
        if (!out_st) throw std::bad_alloc();

        avcheck(avcodec_parameters_copy(out_st->codecpar, in_st->codecpar), "Copy stream codec parameters");
        out_st->codecpar->codec_tag = 0;
        if (out_st->codecpar->codec_id == AV_CODEC_ID_HEVC) {
            out_st->codecpar->codec_tag = MKTAG('h', 'v', 'c', '1');
        }
        out_st->time_base = in_st->time_base;
        out_st->avg_frame_rate = in_st->avg_frame_rate;
        out_st->r_frame_rate = in_st->r_frame_rate;
        out_st->disposition = in_st->disposition;
        av_dict_copy(&out_st->metadata, in_st->metadata, 0);

        stream_map[i] = out_st->index;
    }

    avcheck(avio_open(&output.format->pb, out_file.c_str(), AVIO_FLAG_WRITE), "Open trim output file");
    AVDictionary* options = nullptr;
    av_dict_set(&options, "movflags", "+faststart", 0);
    int res = avformat_write_header(output.format, &options);
    av_dict_free(&options);
    avcheck(res, "Write trim MP4 header");

    AVStream* in_vst = input.format->streams[video_idx];
    int64_t target_pts = static_cast<int64_t>(start_seconds / av_q2d(in_vst->time_base));
    if (start_seconds > 0.05) {
        int seek_res = av_seek_frame(input.format, video_idx, target_pts, AVSEEK_FLAG_BACKWARD);
        if (seek_res < 0) {
            av_seek_frame(input.format, video_idx, 0, AVSEEK_FLAG_BACKWARD);
        }
    } else {
        av_seek_frame(input.format, video_idx, 0, AVSEEK_FLAG_BACKWARD);
    }

    int64_t end_us = static_cast<int64_t>(end_seconds * 1'000'000.0);
    int64_t origin_us = AV_NOPTS_VALUE;
    std::vector<int64_t> last_dts(output.format->nb_streams, AV_NOPTS_VALUE);

    auto packet = make_packet();
    bool video_started = false;
    double trim_duration = end_seconds - start_seconds;
    if (trim_duration < 0.1) trim_duration = 0.1;
    int packet_counter = 0;

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

        if (!video_started) {
            if (in_idx == video_idx && (packet->flags & AV_PKT_FLAG_KEY)) {
                video_started = true;
                origin_us = std::min(pkt_pts_us, pkt_dts_us);
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
            if (in_idx == video_idx) {
                av_packet_unref(packet.get());
                break;
            }
            av_packet_unref(packet.get());
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

        if (last_dts[out_idx] != AV_NOPTS_VALUE && packet->dts <= last_dts[out_idx]) {
            packet->dts = last_dts[out_idx] + 1;
        }
        if (packet->pts < packet->dts) {
            packet->pts = packet->dts;
        }
        last_dts[out_idx] = packet->dts;

        avcheck(av_interleaved_write_frame(output.format, packet.get()), "Write remuxed trim packet");
        av_packet_unref(packet.get());

        if (progress_cb && (++packet_counter % 10 == 0)) {
            double current_sec = static_cast<double>(rel_pts_us) / 1'000'000.0;
            double prog = std::clamp(current_sec / trim_duration, 0.0, 0.99);
            if (progress_cb(user, prog) != 0) {
                throw std::runtime_error("Операция обрезки отменена пользователем.");
            }
        }
    }

    if (!video_started) {
        throw std::runtime_error("Failed to find video keyframe in specified range.");
    }

    avcheck(av_write_trailer(output.format), "Finalize trim MP4 trailer");
    avcheck(avio_closep(&output.format->pb), "Flush trim output file");
    output.committed = true;

    if (progress_cb) progress_cb(user, 1.0);
}

} // namespace hr
