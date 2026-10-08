#include "common.h"

namespace hr {
D3D::D3D(HMONITOR monitor) {
    ComPtr<IDXGIFactory1> factory;
    check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
    ComPtr<IDXGIAdapter1> fallback;
    for (UINT i = 0; ; ++i) {
        ComPtr<IDXGIAdapter1> candidate;
        HRESULT enumerated = factory->EnumAdapters1(i, &candidate);
        if (enumerated == DXGI_ERROR_NOT_FOUND) break;
        check(enumerated);
        DXGI_ADAPTER_DESC1 desc{};
        check(candidate->GetDesc1(&desc));
        if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
        if (!fallback) fallback = candidate;
        for (UINT j = 0; ; ++j) {
            ComPtr<IDXGIOutput> output;
            HRESULT enumerated_output = candidate->EnumOutputs(j, &output);
            if (enumerated_output == DXGI_ERROR_NOT_FOUND) break;
            check(enumerated_output);
            DXGI_OUTPUT_DESC info{}; check(output->GetDesc(&info));
            if (info.Monitor == monitor) adapter = candidate;
        }
    }
    if (!adapter) adapter = fallback;
    if (!adapter) throw std::runtime_error("No hardware DXGI adapter available (RDP/headless is not supported).");
    DXGI_ADAPTER_DESC1 desc{}; check(adapter->GetDesc1(&desc));
    vendor = desc.VendorId; name = desc.Description;
    D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    check(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
        levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &device, nullptr, &context));
    ComPtr<ID3D10Multithread> multithread;
    check(context.As(&multithread)); multithread->SetMultithreadProtected(TRUE);
    check(device.As(&video_device)); check(context.As(&video_context));
}

VideoEncoder::VideoEncoder(D3D& device, hr_config config, Emit callback) : d3d(device), emit(std::move(callback)) {
    encoder = config.encoder;
    if (encoder == 0) {
        switch (d3d.vendor) {
            case 0x1002: encoder = 1; break;
            case 0x10de: encoder = 2; break;
            case 0x8086: encoder = 3; break;
            default: throw std::runtime_error("Unknown GPU. Select an encoder explicitly; there is no silent CPU fallback.");
        }
    }
    const char* names[5][3] = {
        {nullptr, nullptr, nullptr}, {"h264_amf", "hevc_amf", "av1_amf"},
        {"h264_nvenc", "hevc_nvenc", "av1_nvenc"}, {"h264_qsv", "hevc_qsv", "av1_qsv"},
        {"libx264", nullptr, nullptr}
    };
    const char* name = names[encoder][config.codec];
    const AVCodec* implementation = name ? avcodec_find_encoder_by_name(name) : nullptr;
    if (!implementation) throw std::runtime_error("Selected encoder/codec is not included in the native runtime.");

    d3d_device.reset(av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA));
    if (!d3d_device) throw std::bad_alloc();
    auto* hw = reinterpret_cast<AVD3D11VADeviceContext*>(reinterpret_cast<AVHWDeviceContext*>(d3d_device->data)->hwctx);
    hw->device = d3d.device.Get(); hw->device->AddRef();
    avcheck(av_hwdevice_ctx_init(d3d_device.get()), "D3D11 FFmpeg device");
    frames.reset(av_hwframe_ctx_alloc(d3d_device.get()));
    if (!frames) throw std::bad_alloc();
    auto* fc = reinterpret_cast<AVHWFramesContext*>(frames->data);
    fc->format = AV_PIX_FMT_D3D11; fc->sw_format = AV_PIX_FMT_NV12;
    fc->width = encoder == 3 ? (config.width + 31) & ~31 : config.width;
    fc->height = encoder == 3 ? (config.height + 31) & ~31 : config.height;
    fc->initial_pool_size = 12;
    auto* dxframes = reinterpret_cast<AVD3D11VAFramesContext*>(fc->hwctx);
    dxframes->BindFlags = D3D11_BIND_RENDER_TARGET;
    avcheck(av_hwframe_ctx_init(frames.get()), "GPU NV12 frame pool");
    codec.reset(avcodec_alloc_context3(implementation));
    if (!codec) throw std::bad_alloc();
    codec->width = config.width; codec->height = config.height;
    codec->time_base = AVRational{1, config.fps}; codec->framerate = AVRational{config.fps, 1};
    codec->bit_rate = config.bitrate; codec->rc_max_rate = config.bitrate;
    codec->rc_buffer_size = config.bitrate * 2;
    codec->gop_size = config.fps * config.gop_seconds;
    codec->max_b_frames = 0; codec->flags |= AV_CODEC_FLAG_GLOBAL_HEADER | AV_CODEC_FLAG_CLOSED_GOP;
    codec->color_range = AVCOL_RANGE_MPEG; codec->colorspace = AVCOL_SPC_BT709;
    codec->color_primaries = AVCOL_PRI_BT709; codec->color_trc = AVCOL_TRC_BT709;
    codec->sample_aspect_ratio = AVRational{1, 1};
    if (encoder == 3) {
        AVBufferRef* qdev = nullptr;
        avcheck(av_hwdevice_ctx_create_derived(&qdev, AV_HWDEVICE_TYPE_QSV, d3d_device.get(), 0), "QSV device");
        qsv_device.reset(qdev);
        AVBufferRef* qframes = nullptr;
        avcheck(av_hwframe_ctx_create_derived(&qframes, AV_PIX_FMT_QSV, qsv_device.get(), frames.get(),
            AV_HWFRAME_MAP_READ), "QSV shared GPU frames");
        qsv_frames.reset(qframes);
        codec->pix_fmt = AV_PIX_FMT_QSV;
        codec->hw_frames_ctx = av_buffer_ref(qsv_frames.get());
    } else if (encoder == 4) {
        codec->pix_fmt = AV_PIX_FMT_NV12; // Explicit CPU x264 fallback with GPU readback.
    } else {
        codec->pix_fmt = AV_PIX_FMT_D3D11;
        codec->hw_frames_ctx = av_buffer_ref(frames.get());
    }
    AVDictionary* options = nullptr;
    if (encoder == 1) {
        av_dict_set(&options, "usage", "lowlatency", 0);
        av_dict_set(&options, "quality", "speed", 0);
        av_dict_set(&options, "rc", "cbr", 0);
    } else if (encoder == 2) {
        av_dict_set(&options, "preset", "p4", 0);
        av_dict_set(&options, "tune", "ull", 0);
        av_dict_set(&options, "rc", "cbr", 0);
        av_dict_set(&options, "forced-idr", "1", 0);
    } else if (encoder == 3) {
        av_dict_set(&options, "preset", "veryfast", 0);
        av_dict_set(&options, "async_depth", "4", 0);
    } else {
        av_dict_set(&options, "preset", "veryfast", 0);
        av_dict_set(&options, "tune", "zerolatency", 0);
    }
    int result = avcodec_open2(codec.get(), implementation, &options);
    av_dict_free(&options);
    avcheck(result, "Open hardware encoder (check GPU codec support and installed driver)");
    description = d3d.name + L" / " + wide(name) + (encoder == 4 ? L" / CPU" : L" / GPU");
}

void VideoEncoder::rebuild_processor(int width, int height) {
    processor.Reset(); enumerator.Reset();
    D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{};
    desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
    desc.InputWidth = width; desc.InputHeight = height;
    desc.OutputWidth = codec->width; desc.OutputHeight = codec->height;
    desc.InputFrameRate = {static_cast<UINT>(codec->framerate.num), 1}; desc.OutputFrameRate = desc.InputFrameRate;
    desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
    check(d3d.video_device->CreateVideoProcessorEnumerator(&desc, &enumerator));
    UINT support = 0; check(enumerator->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &support));
    if (!(support & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT))
        throw std::runtime_error("The selected GPU does not support NV12 video processor output.");
    check(d3d.video_device->CreateVideoProcessor(enumerator.Get(), 0, &processor));
    d3d.video_context->VideoProcessorSetStreamFrameFormat(processor.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
    d3d.video_context->VideoProcessorSetStreamAutoProcessingMode(processor.Get(), 0, FALSE);
    D3D11_VIDEO_PROCESSOR_COLOR_SPACE rgb{}; rgb.Usage = 0; rgb.RGB_Range = 0;
    D3D11_VIDEO_PROCESSOR_COLOR_SPACE yuv{}; yuv.YCbCr_Matrix = 1; yuv.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
    d3d.video_context->VideoProcessorSetStreamColorSpace(processor.Get(), 0, &rgb);
    d3d.video_context->VideoProcessorSetOutputColorSpace(processor.Get(), &yuv);
    D3D11_VIDEO_COLOR black{}; black.YCbCr.Y = 16.f / 255.f; black.YCbCr.Cb = .5f; black.YCbCr.Cr = .5f; black.YCbCr.A = 1.f;
    d3d.video_context->VideoProcessorSetOutputBackgroundColor(processor.Get(), TRUE, &black);
    RECT source{0, 0, width, height};
    double scale = std::min(double(codec->width) / width, double(codec->height) / height);
    int outw = (int(width * scale) / 2) * 2, outh = (int(height * scale) / 2) * 2;
    int x = ((codec->width - outw) / 4) * 2, y = ((codec->height - outh) / 4) * 2;
    RECT target{x, y, x + outw, y + outh};
    RECT output{0, 0, codec->width, codec->height};
    d3d.video_context->VideoProcessorSetStreamSourceRect(processor.Get(), 0, TRUE, &source);
    d3d.video_context->VideoProcessorSetStreamDestRect(processor.Get(), 0, TRUE, &target);
    d3d.video_context->VideoProcessorSetOutputTargetRect(processor.Get(), TRUE, &output);
    input_width = width; input_height = height;
}

void VideoEncoder::encode(ID3D11Texture2D* bgra, int width, int height, int64_t pts) {
    if (!processor || width != input_width || height != input_height) rebuild_processor(width, height);
    auto hardware = make_frame();
    avcheck(av_hwframe_get_buffer(frames.get(), hardware.get(), 0), "Acquire NV12 texture");
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC iv{};
    iv.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
    ComPtr<ID3D11VideoProcessorInputView> input;
    check(d3d.video_device->CreateVideoProcessorInputView(bgra, enumerator.Get(), &iv, &input));
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ov{};
    auto* nv12 = reinterpret_cast<ID3D11Texture2D*>(hardware->data[0]);
    D3D11_TEXTURE2D_DESC target_desc{}; nv12->GetDesc(&target_desc);
    if (target_desc.ArraySize == 1) {
        ov.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        ov.Texture2D.MipSlice = 0;
    } else {
        ov.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2DARRAY;
        ov.Texture2DArray.MipSlice = 0;
        ov.Texture2DArray.FirstArraySlice = static_cast<UINT>(reinterpret_cast<uintptr_t>(hardware->data[1]));
        ov.Texture2DArray.ArraySize = 1;
    }
    ComPtr<ID3D11VideoProcessorOutputView> output;
    check(d3d.video_device->CreateVideoProcessorOutputView(
        reinterpret_cast<ID3D11Texture2D*>(hardware->data[0]), enumerator.Get(), &ov, &output));
    D3D11_VIDEO_PROCESSOR_STREAM stream{}; stream.Enable = TRUE; stream.pInputSurface = input.Get();
    check(d3d.video_context->VideoProcessorBlt(processor.Get(), output.Get(), 0, 1, &stream));
    d3d.context->Flush();
    Frame mapped;
    AVFrame* frame = hardware.get();
    if (encoder == 3) {
        mapped = make_frame();
        mapped->format = AV_PIX_FMT_QSV; mapped->hw_frames_ctx = av_buffer_ref(qsv_frames.get());
        avcheck(av_hwframe_map(mapped.get(), hardware.get(), AV_HWFRAME_MAP_READ), "Map D3D11 to QSV");
        frame = mapped.get();
    } else if (encoder == 4) {
        mapped = make_frame();
        avcheck(av_hwframe_transfer_data(mapped.get(), hardware.get(), 0), "Explicit x264 GPU readback");
        frame = mapped.get();
    }
    frame->width = codec->width; frame->height = codec->height;
    frame->pts = pts;
    frame->color_range = codec->color_range; frame->colorspace = codec->colorspace;
    frame->color_primaries = codec->color_primaries; frame->color_trc = codec->color_trc;
    // Force closed IDRs on the configured wall-clock GOP grid, even after frame drops.
    if (pts / codec->gop_size > last_forced_gop) {
        frame->pict_type = AV_PICTURE_TYPE_I;
        last_forced_gop = pts / codec->gop_size;
    }
    int result = avcodec_send_frame(codec.get(), frame);
    if (result == AVERROR(EAGAIN)) { drain(); result = avcodec_send_frame(codec.get(), frame); }
    avcheck(result, "Encode video frame"); drain();
}

void VideoEncoder::drain(bool flush) {
    if (flush) avcheck(avcodec_send_frame(codec.get(), nullptr), "Flush video encoder");
    auto packet = make_packet();
    for (;;) {
        int result = avcodec_receive_packet(codec.get(), packet.get());
        if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) break;
        avcheck(result, "Receive encoded video");
        if (packet->duration <= 0) packet->duration = 1;
        emit(0, packet.get(), codec->time_base);
        av_packet_unref(packet.get());
    }
}
}
