#include "common.h"

struct hr_mux {
    AVFormatContext* format = nullptr;
    std::vector<int64_t> last_dts;
    ~hr_mux() {
        if (format) {
            if (format->pb) avio_closep(&format->pb);
            avformat_free_context(format);
        }
    }
};
// All exported operations are exception-wrapped in api.cpp.
namespace hr {
hr_mux* mux_open(Session* session, const wchar_t* path) {
    if (!session || !path) throw std::runtime_error("Missing MP4 session/path.");
    auto mux = std::make_unique<hr_mux>();
    std::string filename = utf8(path);
    avcheck(avformat_alloc_output_context2(&mux->format, nullptr, "mp4", filename.c_str()), "Allocate MP4 muxer");
    if (!mux->format) throw std::bad_alloc();
    {
    std::lock_guard lock(session->headers_mutex);
    if (session->headers.empty() || !session->video_header_frozen)
        throw std::runtime_error("Capture has not supplied its first video sequence header yet.");
    for (size_t i = 0; i < session->headers.size(); ++i) {
        auto* stream = avformat_new_stream(mux->format, nullptr);
        if (!stream) throw std::bad_alloc();
        avcheck(avcodec_parameters_copy(stream->codecpar, session->headers[i].get()), "Copy MP4 stream configuration");
        stream->codecpar->codec_tag = 0;
        if (stream->codecpar->codec_id == AV_CODEC_ID_HEVC) stream->codecpar->codec_tag = MKTAG('h','v','c','1');
        stream->time_base = microseconds;
        if (i == 0) stream->avg_frame_rate = AVRational{session->config.fps, 1};
        else {
            av_dict_set(&stream->metadata, "title", i == 1 ? "Game audio" : "Microphone", 0);
            stream->disposition = i == 1 ? AV_DISPOSITION_DEFAULT : 0;
        }
        mux->last_dts.push_back(AV_NOPTS_VALUE);
    }
    }
    avcheck(avio_open(&mux->format->pb, filename.c_str(), AVIO_FLAG_WRITE), "Open clip output");
    AVDictionary* options = nullptr;
    av_dict_set(&options, "movflags", "+faststart", 0);
    int result = avformat_write_header(mux->format, &options); av_dict_free(&options);
    avcheck(result, "Write MP4 header");
    return mux.release();
}
void mux_write(hr_mux* mux, const hr_packet* source, int64_t origin) {
    if (!mux || !source || source->stream < 0 || source->stream >= static_cast<int>(mux->format->nb_streams) ||
        !source->data || source->size <= 0 || source->dts_us < origin)
        throw std::runtime_error("Invalid MP4 packet/timestamp.");
    auto packet = make_packet(); avcheck(av_new_packet(packet.get(), source->size), "Allocate mux packet");
    memcpy(packet->data, source->data, source->size);
    packet->stream_index = source->stream;
    packet->pts = source->pts_us - origin; packet->dts = source->dts_us - origin;
    packet->duration = source->duration_us; packet->pos = -1;
    packet->flags = source->keyframe ? AV_PKT_FLAG_KEY : 0;
    av_packet_rescale_ts(packet.get(), microseconds, mux->format->streams[source->stream]->time_base);
    if (mux->last_dts[source->stream] != AV_NOPTS_VALUE && packet->dts <= mux->last_dts[source->stream])
        throw std::runtime_error("Non-monotonic MP4 DTS; export aborted, no corrupt clip published.");
    mux->last_dts[source->stream] = packet->dts;
    avcheck(av_interleaved_write_frame(mux->format, packet.get()), "Stream-copy MP4 packet");
}
void mux_close(hr_mux* pointer) {
    std::unique_ptr<hr_mux> mux(pointer);
    if (!mux) throw std::runtime_error("Null MP4 muxer.");
    avcheck(av_write_trailer(mux->format), "Finalize MP4 (disk may be full)");
    avcheck(avio_closep(&mux->format->pb), "Flush clip output");
}
void mux_abort(hr_mux* mux) { delete mux; }

void thumbnail(const wchar_t* path, int max_width, uint8_t** pixels, int* width, int* height, int* stride) {
    if (!path || !pixels || !width || !height || !stride || max_width < 64 || max_width > 1920)
        throw std::runtime_error("Invalid thumbnail arguments.");
    *pixels = nullptr;
    struct Input { AVFormatContext* value = nullptr; ~Input() { avformat_close_input(&value); } } input;
    auto filename = utf8(path);
    avcheck(avformat_open_input(&input.value, filename.c_str(), nullptr, nullptr), "Open thumbnail source");
    avcheck(avformat_find_stream_info(input.value, nullptr), "Read thumbnail streams");
    const AVCodec* decoder = nullptr;
    int video = av_find_best_stream(input.value, AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);
    avcheck(video, "Find thumbnail video");
    Codec codec(avcodec_alloc_context3(decoder)); if (!codec) throw std::bad_alloc();
    avcheck(avcodec_parameters_to_context(codec.get(), input.value->streams[video]->codecpar), "Configure thumbnail decoder");
    codec->thread_count = 2;
    avcheck(avcodec_open2(codec.get(), decoder, nullptr), "Open thumbnail decoder");
    auto packet = make_packet(); auto frame = make_frame();
    bool decoded = false;
    for (int attempts = 0; attempts < 500 && av_read_frame(input.value, packet.get()) >= 0; ++attempts) {
        if (packet->stream_index == video) {
            avcheck(avcodec_send_packet(codec.get(), packet.get()), "Decode thumbnail packet");
            int result = avcodec_receive_frame(codec.get(), frame.get());
            if (result >= 0) { decoded = true; break; }
            if (result != AVERROR(EAGAIN)) avcheck(result, "Receive thumbnail frame");
        }
        av_packet_unref(packet.get());
    }
    if (!decoded || frame->width < 1 || frame->height < 1) throw std::runtime_error("No decodable thumbnail frame.");
    *width = std::min(max_width, frame->width);
    *height = std::max(1, static_cast<int>(av_rescale(frame->height, *width, frame->width)));
    *stride = *width * 4;
    if (*height > 8192) throw std::runtime_error("Invalid thumbnail dimensions.");
    SwsContext* scaler = sws_getContext(frame->width, frame->height, static_cast<AVPixelFormat>(frame->format),
        *width, *height, AV_PIX_FMT_BGRA, SWS_BILINEAR, nullptr, nullptr, nullptr);
    if (!scaler) throw std::runtime_error("Thumbnail scaler unavailable.");
    auto* output = static_cast<uint8_t*>(av_malloc(static_cast<size_t>(*stride) * *height));
    if (!output) { sws_freeContext(scaler); throw std::bad_alloc(); }
    uint8_t* planes[]{output, nullptr, nullptr, nullptr};
    int lines[]{*stride, 0, 0, 0};
    int result = sws_scale(scaler, frame->data, frame->linesize, 0, frame->height, planes, lines);
    sws_freeContext(scaler);
    if (result <= 0) { av_free(output); throw std::runtime_error("Thumbnail conversion failed."); }
    *pixels = output;
}
}
