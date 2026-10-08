# Выполненная проверка / ограничения среды

Дата: **2026-10-09**. Рабочая среда: Linux x86-64, без Windows, Visual Studio,
Windows SDK, интерактивного Windows рабочего стола или физического GPU для WGC.
Готового `.exe` и успешного Windows build result эта запись не утверждает.

## Выполнено

1. Изучены исходные `server.py`, `recorder.py`, `install.bat`, обе proto2-схемы;
   оригинальная NVIDIA DLL сохранена без изменений, SHA-256 совпадает с pin.
2. Реальные C# исходники **Core и Protocol** скомпилированы Roslyn в language mode
   C# 12. Для исполнения использован доступный .NET **10.0.11** runtime из
   `@dprint/roslyn-linux-x64-glibc` 0.22.1; это **не .NET 8 target-pack build**.
   В sandbox не было .NET SDK, а Microsoft/NuGet download hosts недоступны.
3. Google.Protobuf собран из upstream **v36.2**; `.proto` для локальной проверки
   сгенерированы `protoc 29.3`. Обычная сборка проекта использует pinned
   Google.Protobuf **3.36.2** и **Grpc.Tools 2.84.0** через NuGet и проверяется CI.
4. Дополнительно semantic-скомпилированы C# Win32 interop declarations,
   RuntimeAssets, GameWindow, Authenticode, SelfInstaller и StudioLog против
   доступных runtime assemblies. Дополнительно скомпилированы RecorderService,
   pipe listener и diagnostics с NativeCaptureEngine, где только WPF PNG-метод
   был исключён в scratch-копии для Linux semantic check. Это не подменяет
   Windows-сборку; production-исходник PNG-метода сохранён. Windows API не вызывались.
5. **83 regression tests passed, 0 failed** — исполнялся настоящий
   `tests/HuntReplay.Tests`, не переписанная Python-модель алгоритма:
   - defaults/config bounds/NaN/unknown schema/backup/corruption, восстановление
     legacy JSON-null device IDs без сброса остальных настроек;
   - filename traversal/Windows reserved names;
   - keyframe-aligned snapshots, per-stream ordering, duplicate/nonmonotonic DTS,
     byte/time limits, pin retention, oversize GOP, immutable snapshots, concurrency;
   - separate finalize/video-end clocks, kills 12/15/16 seconds apart, session/group
     boundaries, duplicate events, maximum sequence/queue/event count, bounded deltas;
   - library rename/retention/protected/unmanaged files/bad sidecars/uppercase extension;
   - short reads, big-endian framing, EOF/truncation/size caps;
   - JOIN, SDK PID authorization, unsupported versions/requests, retransmission cache,
     group isolation and reused groupId epochs; ACK до/после JOIN, echo-ID ACK,
     пустой descriptive client, routing fallback, точные field/type ошибки,
     отклонение неверного PID, транзакционная смена highlight definitions;
   - wire compatibility с fixture-последовательностью, созданной **исходными
     Python `_pb2.py` модулями репозитория** (Python protobuf **7.35.1**).
6. `python3 tools/CheckSource.py`: XML syntax XAML/projects/manifest, pins,
   managed imports всех 15 C ABI exports, наличие fixtures; static scan production
   sources на запрещённые input/injection/recorder APIs и placeholders.
7. Все C# source files прошли Roslyn syntax parsing. Это не WPF markup compile.

## Windows CI и регрессии 0.1.1

- Исходный commit `c1287c3`: .NET 8 Core/Protocol job прошёл в GitHub Actions
  ([run 37851254775](https://github.com/Mlovo/HuntOpenHighlights/actions/runs/37851254775)).
  Windows job тогда упал на неоднозначном `DirectX` в C++/WinRT; namespace исправлен.
- В `tests/HuntReplay.WindowsTests` добавлены 18 регрессий на настоящих WPF
  `ComboBox`, TwoWay bindings и Dispatcher: Default / explicit selection,
  refresh, unplug/replug, transient null и замена settings. Этот проект запускается
  `Build.ps1` на Windows; локальная Linux-проверка не означает его прохождение.
- NV12 policy проверена по **точному pinned FFmpeg commit**, не по старой ветке:
  динамический D3D11 pool поддерживает отдельные textures и QSV derivation.
  Успешное кодирование на физическом GPU здесь не утверждается.
- Новые Python control-frame fixtures синтетические, из исходных proto2-схем,
  **не перехваченный трафик игры**. Какое именно поле вызвало прежнюю общую
  `Invalid IPC identifier`, из пользовательского лога определить нельзя;
  теперь ошибка указывает поле и вид запроса без вывода raw payload.

## НЕ проверено в этой среде

- `dotnet build/publish` полного решения именно .NET 8/Windows target packs;
- MSVC/C++/WinRT compilation, import libs, loading pinned FFmpeg DLLs on Windows;
- WPF XAML compilation и реальный GUI (DPI/bindings/tray/toast/player);
- `WinVerifyTrust`, UAC и реальный rollback/ACL/registry на Windows;
- WGC frame capture, GPU surface interop и аппаратный output AMF/NVENC/QSV;
- WASAPI packets, устройство/clock drift, AAC/audio sync и mux decode integrity;
- live `NvMessageBusBroadcast` с настоящей игрой;
- производительность CPU/GPU/RAM/disk и работа с Easy Anti-Cheat;

## Как воспроизвести стандартным способом

```powershell
# На машине с .NET 8 SDK; не нужен Python, OBS или графический адаптер для этих тестов:
dotnet run --project tests/HuntReplay.Tests -c Release

# Только Windows / .NET 8: реальные WPF binding regressions, без GPU или приложения:
dotnet run --project tests/HuntReplay.WindowsTests -c Release

# Dependency-free static checks:
python tools/CheckSource.py

# Windows native/WPF build:
.\tools\Build.ps1
```

Scratch runtime/compiler downloads использовались только для проверки в `.cache/`;
в приложение и Git они не включаются. Полная инструкция целевой проверки —
`WINDOWS_VALIDATION.md`. Любые найденные там ошибки необходимо устранить **до**
маркировки сборки как готового бинарного релиза.
