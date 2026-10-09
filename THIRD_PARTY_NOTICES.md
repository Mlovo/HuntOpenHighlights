# Third-party components / redistribution checklist

## HuntOpenHighlights source

The protocol and highlight scheduling logic originate from the HuntOpenHighlights baseline.
The application is a complete native/WPF rewrite and does not use Python or OBS.
This repository's own source is offered under MIT; see `LICENSE`.

## NVIDIA MessageBus.dll — proprietary, NOT MIT / NOT GPL

- Supplied by the original repository at `bin/MessageBus.dll`; not modified.
- SHA-256: `64e300997b5796da22ad8baa49e4eb919cdc170157cbe6770279f94508a84650`.
- The installer verifies that exact hash, `WinVerifyTrust` and the NVIDIA
  Corporation signer on the target Windows system. No counterfeit signature,
  replacement GfeSDK DLL, or game-file patch is created.
- A valid Authenticode signature proves origin/integrity, **not permission to
  redistribute, SDK authorization, NVIDIA endorsement or anti-cheat approval**.
- This repository does not grant NVIDIA redistribution rights. Confirm the
  applicable NVIDIA license before publishing an installer that embeds this file.
  If those rights cannot be established, do not publish that binary distribution.

## FFmpeg and bundled codec implementations

The default build uses the **GPL shared** BtbN FFmpeg SDK because the requested
explicit CPU fallback is `libx264`. AMF, NVENC and QSV remain driver-dependent.

Exact binary URL, SHA-256, upstream revision and build-recipe revision are in
`tools/ffmpeg.lock.json`. There is no `latest` resolution or download at app runtime.
The package's DLLs are embedded; **ffmpeg.exe / ffprobe.exe are not used or bundled
as the recording backend**.

Relevant source locations:

- https://github.com/FFmpeg/FFmpeg/tree/330caae0c1acccd2222edc52a05940c574561ce5
- https://github.com/BtbN/FFmpeg-Builds/tree/9acad4a9ef1583096af7836cc1e9c8cbcb4d3950

`licenses/FFmpeg-GPLv3.txt` contains upstream's GPLv3 text. Review the actual
archive's configuration and third-party notices: optional codec libraries have
their own licenses. The default combined, linked application distribution must
comply with GPLv3-compatible obligations; the MIT grant for our source does not
remove those obligations. Distributors must supply complete corresponding source
(including enabled dependency sources, modifications and build instructions) by
a GPL-compliant method. Merely including these two links or an unfulfilled source
offer is **not** a substitute. Preserve the exact source bundle/build manifest
alongside any public release. Hosted CI artifacts are unsigned QA builds, not a
statement that distribution rights or license compliance have been cleared.

An LGPL-only build can be engineered separately, but it will not contain x264;
remove/disable that setting rather than claiming the encoder exists. Do not swap
arbitrary DLL versions into the pinned runtime without rebuilding and retesting.

## .NET / WPF / Windows SDK

.NET 8 and WPF are Microsoft/.NET Foundation projects, primarily MIT. The
self-contained runtime is supplied by the .NET SDK during publishing. Preserve
its third-party license notices when redistributing:

- https://github.com/dotnet/runtime
- https://github.com/dotnet/wpf

Windows OS APIs and installed GPU drivers are system prerequisites, not supplied
by this project. NVIDIA, AMD, Intel, Hunt: Showdown and Windows are trademarks of
their respective owners. No endorsement or certification is claimed.

## Managed dependencies

- Google.Protobuf 3.36.2 — BSD-3-Clause; `licenses/Google.Protobuf.txt`.
- Grpc.Tools 2.84.0 — Apache-2.0 build tools, not an end-user dependency.
- Microsoft.Toolkit.Uwp.Notifications 7.1.3 — MIT, used for native Windows toasts.

These are fixed package versions in the project files. Preserve upstream notices
for all resolved/transitive dependencies in a shipped source/binary bundle.

## Signing releases

MessageBus.dll's signature must remain unchanged. This repository contains no
code-signing private key and does not manufacture a publisher identity. Sign your
own final `.exe` with a legitimate Authenticode code-signing certificate, verify
it after signing, and regenerate release checksums. If signing the native engine
as well, do so **before** creating the embedded runtime hash manifest. The
unsigned development build may trigger SmartScreen / show an unknown publisher.
